using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Rendering.Diagnostics;

namespace WinUIEx.Maps;

/// <summary>
/// Owns the UI-thread portion of map-icon tracking, XAML observation, rasterization, and
/// immutable snapshot updates for one <see cref="MapControl"/>.
/// </summary>
/// <remarks>
/// XAML objects remain confined to the control's dispatcher. Only immutable snapshots and
/// versioned pixel buffers are passed to <see cref="MapRenderer"/>, which continues to own
/// GPU textures, upload work, spatial indexing, and draw batching.
/// </remarks>
internal sealed class MapIconService
{
    private readonly MapRenderer _renderer;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly MapIconTextureReferences _references = new();
    private readonly Queue<IconRasterWork> _rasterQueue = new();
    private readonly Dictionary<IconElement, int> _elementReferenceCounts =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IconElement, IconElementChangeSubscription>
        _elementChangeSubscriptions = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MapIcon, TrackedIcon> _trackedIcons =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MapIcon, List<PublishedIcon>> _publishedByIcon =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IconElement, List<PublishedIcon>> _publishedByElement =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<PublishedIcon> _pendingPublishedIcons = [];
    private ContentControl? _rasterizationHost;
    private IconElement? _elementBeingRasterized;
    private double _rasterizationScale = 1;
    private bool _isLoaded;
    private bool _runtimeResourcesAvailable;
    private bool _isProcessingRasterQueue;

    internal MapIconService(
        MapRenderer renderer,
        DispatcherQueue dispatcherQueue)
    {
        _renderer = renderer;
        _dispatcherQueue = dispatcherQueue;
    }

    internal int TrackedTextureCount => _references.Entries.Count;

    internal int GetTextureReferenceCount(IconElement iconElement) =>
        _references.TryGet(iconElement, out MapIconTextureReferences.Entry? entry)
            ? entry!.ReferenceCount
            : 0;

    internal long GetTextureVersion(IconElement iconElement) =>
        _references.TryGet(iconElement, out MapIconTextureReferences.Entry? entry)
            ? entry!.Version
            : 0;

    internal void SetRasterizationHost(ContentControl? host)
    {
        EnsureUiThread();
        _rasterizationHost = host;
    }

    internal void SetLoaded(bool isLoaded)
    {
        EnsureUiThread();
        _isLoaded = isLoaded;
        if (!isLoaded)
        {
            return;
        }

        QueueAllRasterizations(force: false);
    }

    internal void SetRuntimeResourcesAvailable(bool available)
    {
        EnsureUiThread();
        _runtimeResourcesAvailable = available;
    }

    internal void UpdateRasterizationScale(double scale)
    {
        EnsureUiThread();
        if (!double.IsFinite(scale) || scale <= 0)
        {
            scale = 1;
        }
        if (Math.Abs(scale - _rasterizationScale) < 0.001)
        {
            return;
        }

        _rasterizationScale = scale;
        QueueAllRasterizations(force: true);
    }

    internal void ReleaseDormantResources()
    {
        EnsureUiThread();
        _rasterQueue.Clear();
        _pendingPublishedIcons.Clear();
        _publishedByIcon.Clear();
        _publishedByElement.Clear();
    }

    internal void InvalidateTheme() => QueueAllRasterizations(force: true);

    internal void QueueAllRasterizations(bool force)
    {
        EnsureUiThread();
        foreach (IconElement iconElement in _elementReferenceCounts.Keys)
        {
            if (_references.TryGet(
                    iconElement,
                    out MapIconTextureReferences.Entry? entry) &&
                entry is not null)
            {
                if (force)
                {
                    entry.Version++;
                }
                QueueRasterization(iconElement, entry);
            }
        }
    }

    internal void AttachIcon(MapIcon icon)
    {
        EnsureUiThread();
        if (_trackedIcons.TryGetValue(icon, out TrackedIcon? tracked))
        {
            tracked.ReferenceCount++;
            AddElementReference(tracked.IconElement);
            return;
        }

        IconElement iconElement = icon.GetState().IconElement;
        _trackedIcons.Add(icon, new TrackedIcon(iconElement, icon.GetBaseState()));
        AddElementReference(iconElement);
    }

    internal void DetachIcon(MapIcon icon)
    {
        EnsureUiThread();
        if (!_trackedIcons.TryGetValue(icon, out TrackedIcon? tracked))
        {
            return;
        }

        RemoveElementReference(tracked.IconElement);
        if (--tracked.ReferenceCount == 0)
        {
            _trackedIcons.Remove(icon);
        }
    }

    internal void DetachAllReferences()
    {
        EnsureUiThread();
        foreach ((IconElement iconElement, int count) in _elementReferenceCounts)
        {
            _elementChangeSubscriptions.Remove(
                iconElement,
                out IconElementChangeSubscription? subscription);
            subscription?.Detach();
            for (int index = 0; index < count; index++)
            {
                MapIconTextureReferences.Entry? released =
                    _references.Remove(iconElement);
                if (released is not null)
                {
                    _renderer.RemoveMapIconTexture(released.TextureId);
                }
            }
        }

        _rasterQueue.Clear();
        _trackedIcons.Clear();
        _elementReferenceCounts.Clear();
    }

    internal bool ProcessChangedIcons(IReadOnlyList<MapIcon> icons)
    {
        EnsureUiThread();
        bool allTracked = true;
        int changedElementCount = 0;
        List<MapIconSnapshotUpdate> updates = [];
        foreach (MapIcon icon in icons)
        {
            if (!_trackedIcons.TryGetValue(icon, out TrackedIcon? tracked))
            {
                allTracked = false;
                continue;
            }

            MapIconState state = icon.GetState();
            if (!ReferenceEquals(tracked.IconElement, state.IconElement))
            {
                IconElement previousElement = tracked.IconElement;
                for (int index = 0; index < tracked.ReferenceCount; index++)
                {
                    RemoveElementReference(previousElement);
                    AddElementReference(state.IconElement);
                }
                tracked.IconElement = state.IconElement;
                MovePublishedIconReferences(icon, previousElement, state.IconElement);
            }

            MapElementState elementState = icon.GetBaseState();
            if (tracked.ElementState != elementState)
            {
                tracked.ElementState = elementState;
                allTracked = false;
                continue;
            }

            int previousUpdateCount = updates.Count;
            AppendPublishedIconUpdates(icon, state, updates);
            if (updates.Count != previousUpdateCount)
            {
                changedElementCount++;
            }
        }

        PublishUpdates(updates, changedElementCount);
        return allTracked;
    }

    internal void BeginSnapshotRebuild()
    {
        EnsureUiThread();
        _pendingPublishedIcons.Clear();
    }

    internal bool TryCreateSnapshot(
        MapIcon icon,
        int snapshotIndex,
        int layerIndex,
        int elementIndex,
        int orderIndex,
        MapElementState elementState,
        out MapIconSnapshot snapshot)
    {
        EnsureUiThread();
        snapshot = default;
        if (!_trackedIcons.TryGetValue(icon, out TrackedIcon? tracked) ||
            !_references.TryGet(
                tracked.IconElement,
                out MapIconTextureReferences.Entry? entry) ||
            entry is null)
        {
            return false;
        }

        MapIconState state = icon.GetState();
        tracked.ElementState = elementState;
        snapshot = CreateSnapshot(
            state,
            entry,
            layerIndex,
            elementIndex,
            orderIndex,
            elementState.IsEnabled);
        _pendingPublishedIcons.Add(new PublishedIcon(
            icon,
            tracked.IconElement,
            snapshotIndex,
            layerIndex,
            elementIndex,
            orderIndex,
            elementState.IsEnabled));
        return true;
    }

    internal void CommitSnapshotRebuild(IReadOnlyList<MapIconSnapshot> snapshots)
    {
        EnsureUiThread();
        _publishedByIcon.Clear();
        _publishedByElement.Clear();
        foreach (PublishedIcon published in _pendingPublishedIcons)
        {
            GetOrCreate(_publishedByIcon, published.Icon).Add(published);
            GetOrCreate(_publishedByElement, published.IconElement).Add(published);
        }
        _pendingPublishedIcons.Clear();

        if (MapControlEventSource.Log.IsEnabled(
            System.Diagnostics.Tracing.EventLevel.Informational,
            MapControlEventSource.Keywords.Icons))
        {
            MapControlEventSource.Log.IconSnapshotPublished(
                snapshots.Count,
                snapshots.Select(snapshot => snapshot.TextureId).Distinct().Count());
        }
    }

    private void AddElementReference(IconElement iconElement)
    {
        _elementReferenceCounts.TryGetValue(iconElement, out int count);
        _elementReferenceCounts[iconElement] = count + 1;
        if (count == 0)
        {
            _elementChangeSubscriptions.Add(
                iconElement,
                new IconElementChangeSubscription(iconElement, this));
        }

        MapIconTextureReferences.Entry entry = _references.Add(iconElement);
        QueueRasterization(iconElement, entry);
    }

    private void RemoveElementReference(IconElement iconElement)
    {
        if (!_elementReferenceCounts.TryGetValue(iconElement, out int count))
        {
            return;
        }

        if (count == 1)
        {
            _elementReferenceCounts.Remove(iconElement);
            _elementChangeSubscriptions.Remove(
                iconElement,
                out IconElementChangeSubscription? subscription);
            subscription?.Detach();
        }
        else
        {
            _elementReferenceCounts[iconElement] = count - 1;
        }

        MapIconTextureReferences.Entry? released = _references.Remove(iconElement);
        if (released is not null)
        {
            _renderer.RemoveMapIconTexture(released.TextureId);
        }
    }

    private void OnIconElementPropertyChanged(
        DependencyObject sender,
        DependencyProperty property)
    {
        if (ReferenceEquals(sender, _elementBeingRasterized) ||
            sender is not IconElement iconElement ||
            !_references.TryGet(
                iconElement,
                out MapIconTextureReferences.Entry? entry) ||
            entry is null)
        {
            return;
        }

        entry.Version++;
        QueueRasterization(iconElement, entry);
    }

    private void QueueRasterization(
        IconElement iconElement,
        MapIconTextureReferences.Entry entry)
    {
        if (!_isLoaded ||
            _rasterizationHost is null ||
            entry.QueuedVersion >= entry.Version)
        {
            return;
        }

        entry.QueuedVersion = entry.Version;
        _rasterQueue.Enqueue(new IconRasterWork(iconElement, entry, entry.Version));
        if (!_isProcessingRasterQueue)
        {
            ProcessRasterQueueAsync();
        }
    }

    private async void ProcessRasterQueueAsync()
    {
        _isProcessingRasterQueue = true;
        try
        {
            while (_rasterQueue.TryDequeue(out IconRasterWork work))
            {
                if (!_references.TryGet(
                        work.IconElement,
                        out MapIconTextureReferences.Entry? current) ||
                    !ReferenceEquals(current, work.Entry) ||
                    current.Version != work.Version)
                {
                    continue;
                }

                try
                {
                    await RasterizeIconAsync(work);
                }
                catch (Exception exception)
                {
                    MapControlEventSource.Log.IconRasterizationFailed(
                        work.Entry.TextureId,
                        exception.GetType().FullName ?? exception.GetType().Name,
                        exception.HResult);
                }
            }
        }
        finally
        {
            _isProcessingRasterQueue = false;
        }
    }

    private async Task RasterizeIconAsync(IconRasterWork work)
    {
        ContentControl host = _rasterizationHost ??
            throw new InvalidOperationException(
                "The MapControl template has no icon rasterization host.");
        if (work.IconElement.Parent is not null)
        {
            throw new InvalidOperationException(
                "MapIcon.IconElement must not already have a XAML visual parent.");
        }

        const double defaultSize = 32;
        _elementBeingRasterized = work.IconElement;
        Border? rasterRoot = null;
        try
        {
            work.IconElement.Measure(
                new Size(double.PositiveInfinity, double.PositiveInfinity));
            double width = GetIconDimension(
                work.IconElement.Width,
                work.IconElement.DesiredSize.Width);
            double height = GetIconDimension(
                work.IconElement.Height,
                work.IconElement.DesiredSize.Height);
            rasterRoot = new Border
            {
                Width = Math.Max(defaultSize, width),
                Height = Math.Max(defaultSize, height),
                Child = work.IconElement,
            };
            host.Width = rasterRoot.Width;
            host.Height = rasterRoot.Height;
            host.Content = rasterRoot;
            host.UpdateLayout();
            _elementBeingRasterized = null;

            RenderTargetBitmap bitmap = new();
            await bitmap.RenderAsync(rasterRoot);
            MapIconRasterDimensions dimensions = MapIconRasterDimensions.Create(
                rasterRoot.Width,
                rasterRoot.Height,
                bitmap.PixelWidth,
                bitmap.PixelHeight);
            if (dimensions.PixelWidth > 4096 || dimensions.PixelHeight > 4096)
            {
                throw new InvalidOperationException(
                    $"MapIcon raster dimensions {dimensions.PixelWidth}x" +
                    $"{dimensions.PixelHeight} exceed the 4096 pixel limit.");
            }

            byte[] pixels = (await bitmap.GetPixelsAsync()).ToArray();
            if (!MapRenderer.IsValidPixelBuffer(
                pixels,
                dimensions.PixelWidth,
                dimensions.PixelHeight))
            {
                throw new InvalidOperationException(
                    $"RenderTargetBitmap returned {pixels.Length} BGRA bytes for " +
                    $"{dimensions.PixelWidth}x{dimensions.PixelHeight}.");
            }

            if (!_references.TryGet(
                    work.IconElement,
                    out MapIconTextureReferences.Entry? current) ||
                !ReferenceEquals(current, work.Entry) ||
                current.Version != work.Version)
            {
                return;
            }

            current.Width = dimensions.LogicalWidth;
            current.Height = dimensions.LogicalHeight;
            if (_runtimeResourcesAvailable)
            {
                _renderer.QueueMapIconTexture(new MapIconPixelData(
                    current.TextureId,
                    current.Version,
                    pixels,
                    dimensions.PixelWidth,
                    dimensions.PixelHeight));
            }
            UpdatePublishedIcons(work.IconElement);
        }
        finally
        {
            _elementBeingRasterized = work.IconElement;
            if (rasterRoot is not null)
            {
                rasterRoot.Child = null;
            }
            host.Content = null;
            host.Width = defaultSize;
            host.Height = defaultSize;
            _elementBeingRasterized = null;
        }
    }

    private void AppendPublishedIconUpdates(
        MapIcon icon,
        MapIconState state,
        List<MapIconSnapshotUpdate> updates)
    {
        if (!_runtimeResourcesAvailable ||
            !_publishedByIcon.TryGetValue(icon, out List<PublishedIcon>? published))
        {
            return;
        }

        foreach (PublishedIcon item in published)
        {
            if (ReferenceEquals(item.IconElement, state.IconElement) &&
                TryCreateSnapshot(item, state, out MapIconSnapshot snapshot))
            {
                updates.Add(new MapIconSnapshotUpdate(item.SnapshotIndex, snapshot));
            }
        }
    }

    private void UpdatePublishedIcons(IconElement iconElement)
    {
        if (!_runtimeResourcesAvailable ||
            !_publishedByElement.TryGetValue(
                iconElement,
                out List<PublishedIcon>? published))
        {
            return;
        }

        List<MapIconSnapshotUpdate> updates = new(published.Count);
        HashSet<MapIcon> changedIcons =
            new(ReferenceEqualityComparer.Instance);
        foreach (PublishedIcon item in published)
        {
            MapIconState state = item.Icon.GetState();
            if (ReferenceEquals(item.IconElement, state.IconElement) &&
                TryCreateSnapshot(item, state, out MapIconSnapshot snapshot))
            {
                updates.Add(new MapIconSnapshotUpdate(item.SnapshotIndex, snapshot));
                changedIcons.Add(item.Icon);
            }
        }
        PublishUpdates(updates, changedIcons.Count);
    }

    private void PublishUpdates(
        List<MapIconSnapshotUpdate> updates,
        int changedElementCount)
    {
        if (updates.Count == 0)
        {
            return;
        }

        _renderer.UpdateMapIcons(updates);
        if (MapControlEventSource.Log.IsEnabled(
            System.Diagnostics.Tracing.EventLevel.Informational,
            MapControlEventSource.Keywords.Icons))
        {
            MapControlEventSource.Log.IconUpdatesPublished(
                changedElementCount,
                updates.Count);
        }
    }

    private bool TryCreateSnapshot(
        PublishedIcon published,
        MapIconState state,
        out MapIconSnapshot snapshot)
    {
        snapshot = default;
        if (!_references.TryGet(
                published.IconElement,
                out MapIconTextureReferences.Entry? entry) ||
            entry is null)
        {
            return false;
        }

        snapshot = CreateSnapshot(
            state,
            entry,
            published.LayerIndex,
            published.ElementIndex,
            published.OrderIndex,
            published.IsEnabled);
        return true;
    }

    private static MapIconSnapshot CreateSnapshot(
        MapIconState state,
        MapIconTextureReferences.Entry entry,
        int layerIndex,
        int elementIndex,
        int orderIndex,
        bool isEnabled) =>
        new(
            entry.TextureId,
            state.Longitude,
            state.Latitude,
            entry.Width,
            entry.Height,
            layerIndex,
            state.NormalizedAnchorPoint.X,
            state.NormalizedAnchorPoint.Y,
            elementIndex,
            orderIndex,
            isEnabled);

    private void MovePublishedIconReferences(
        MapIcon icon,
        IconElement previousElement,
        IconElement currentElement)
    {
        if (!_publishedByIcon.TryGetValue(icon, out List<PublishedIcon>? published))
        {
            return;
        }

        foreach (PublishedIcon item in published)
        {
            RemovePublishedElement(previousElement, item);
            item.IconElement = currentElement;
            GetOrCreate(_publishedByElement, currentElement).Add(item);
        }
    }

    private void RemovePublishedElement(
        IconElement iconElement,
        PublishedIcon published)
    {
        if (!_publishedByElement.TryGetValue(
                iconElement,
                out List<PublishedIcon>? references))
        {
            return;
        }

        references.Remove(published);
        if (references.Count == 0)
        {
            _publishedByElement.Remove(iconElement);
        }
    }

    private static List<PublishedIcon> GetOrCreate<TKey>(
        Dictionary<TKey, List<PublishedIcon>> dictionary,
        TKey key)
        where TKey : notnull
    {
        if (!dictionary.TryGetValue(key, out List<PublishedIcon>? values))
        {
            values = [];
            dictionary.Add(key, values);
        }
        return values;
    }

    private void EnsureUiThread()
    {
        if (!_dispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException(
                "Map icon XAML tracking and rasterization must run on the MapControl UI thread.");
        }
    }

    private static double GetIconDimension(double explicitValue, double desiredValue)
    {
        double value = double.IsFinite(explicitValue) && explicitValue > 0
            ? explicitValue
            : desiredValue;
        return double.IsFinite(value) && value > 0 ? value : 32;
    }

    private readonly record struct IconRasterWork(
        IconElement IconElement,
        MapIconTextureReferences.Entry Entry,
        long Version);

    private sealed class TrackedIcon(
        IconElement iconElement,
        MapElementState elementState)
    {
        internal IconElement IconElement { get; set; } = iconElement;
        internal MapElementState ElementState { get; set; } = elementState;
        internal int ReferenceCount { get; set; } = 1;
    }

    private sealed class PublishedIcon(
        MapIcon icon,
        IconElement iconElement,
        int snapshotIndex,
        int layerIndex,
        int elementIndex,
        int orderIndex,
        bool isEnabled)
    {
        internal MapIcon Icon { get; } = icon;
        internal IconElement IconElement { get; set; } = iconElement;
        internal int SnapshotIndex { get; } = snapshotIndex;
        internal int LayerIndex { get; } = layerIndex;
        internal int ElementIndex { get; } = elementIndex;
        internal int OrderIndex { get; } = orderIndex;
        internal bool IsEnabled { get; } = isEnabled;
    }

    private sealed class IconElementChangeSubscription
    {
        private static readonly DependencyProperty[] BaseObservedProperties =
            CreateObservedProperties<IconElement>();
        private static readonly DependencyProperty[] AnimatedIconObservedProperties =
            CreateObservedProperties<AnimatedIcon>();
        private static readonly DependencyProperty[] BitmapIconObservedProperties =
            CreateObservedProperties<BitmapIcon>();
        private static readonly DependencyProperty[] FontIconObservedProperties =
            CreateObservedProperties<FontIcon>();
        private static readonly DependencyProperty[] ImageIconObservedProperties =
            CreateObservedProperties<ImageIcon>();
        private static readonly DependencyProperty[] IconSourceElementObservedProperties =
            CreateObservedProperties<IconSourceElement>();
        private static readonly DependencyProperty[] PathIconObservedProperties =
            CreateObservedProperties<PathIcon>();
        private static readonly DependencyProperty[] SymbolIconObservedProperties =
            CreateObservedProperties<SymbolIcon>();
        private readonly IconElement _iconElement;
        private readonly WeakReference<MapIconService> _owner;
        private readonly List<(DependencyProperty Property, long Token)> _callbacks = [];

        internal IconElementChangeSubscription(
            IconElement iconElement,
            MapIconService owner)
        {
            _iconElement = iconElement;
            _owner = new(owner);
            foreach (DependencyProperty property in GetObservedProperties(iconElement))
            {
                long token = iconElement.RegisterPropertyChangedCallback(
                    property,
                    OnIconElementPropertyChanged);
                _callbacks.Add((property, token));
            }
        }

        internal void Detach()
        {
            foreach ((DependencyProperty property, long token) in _callbacks)
            {
                _iconElement.UnregisterPropertyChangedCallback(property, token);
            }
            _callbacks.Clear();
        }

        private static DependencyProperty[] GetObservedProperties(IconElement iconElement) =>
            iconElement switch
            {
                AnimatedIcon => AnimatedIconObservedProperties,
                BitmapIcon => BitmapIconObservedProperties,
                FontIcon => FontIconObservedProperties,
                ImageIcon => ImageIconObservedProperties,
                IconSourceElement => IconSourceElementObservedProperties,
                PathIcon => PathIconObservedProperties,
                SymbolIcon => SymbolIconObservedProperties,
                _ => BaseObservedProperties,
            };

        private static DependencyProperty[] CreateObservedProperties<
            [DynamicallyAccessedMembers(
                DynamicallyAccessedMemberTypes.PublicFields |
                DynamicallyAccessedMemberTypes.PublicProperties)]
            TIcon>()
            where TIcon : IconElement
        {
            HashSet<DependencyProperty> properties =
                new(ReferenceEqualityComparer.Instance);
            AddDeclaredProperties<UIElement>(properties);
            AddDeclaredProperties<FrameworkElement>(properties);
            AddDeclaredProperties<IconElement>(properties);
            AddDeclaredProperties<TIcon>(properties);
            return [.. properties];
        }

        private static void AddDeclaredProperties<
            [DynamicallyAccessedMembers(
                DynamicallyAccessedMemberTypes.PublicFields |
                DynamicallyAccessedMemberTypes.PublicProperties)]
            TElement>(HashSet<DependencyProperty> properties)
            where TElement : DependencyObject
        {
            foreach (FieldInfo field in typeof(TElement).GetFields(
                BindingFlags.Public |
                BindingFlags.Static |
                BindingFlags.DeclaredOnly))
            {
                if (field.FieldType == typeof(DependencyProperty) &&
                    field.GetValue(null) is DependencyProperty property)
                {
                    properties.Add(property);
                }
            }
            foreach (PropertyInfo propertyInfo in typeof(TElement).GetProperties(
                BindingFlags.Public |
                BindingFlags.Static |
                BindingFlags.DeclaredOnly))
            {
                if (propertyInfo.PropertyType == typeof(DependencyProperty) &&
                    propertyInfo.GetValue(null) is DependencyProperty property)
                {
                    properties.Add(property);
                }
            }
        }

        private void OnIconElementPropertyChanged(
            DependencyObject sender,
            DependencyProperty property)
        {
            if (_owner.TryGetTarget(out MapIconService? owner))
            {
                owner.OnIconElementPropertyChanged(sender, property);
            }
        }
    }

}
