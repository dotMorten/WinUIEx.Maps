using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Rendering.Diagnostics;
using WinUIEx.Maps.Automation.Peers;
using WinUIEx.Maps.Localization;
using System.Collections.Specialized;
using Windows.Devices.Geolocation;
using Windows.Foundation;

namespace WinUIEx.Maps;

/// <summary>
/// Displays an interactive WinUI map composed of an optional Azure base map, custom raster
/// tile layers, and lightweight geographic elements.
/// </summary>
/// <remarks>
/// <para>
/// The control creates its rendering and tile-acquisition resources when its template is
/// applied. Loading resumes acquisition and rendering; unloading suspends network work while
/// retaining the control for reuse. The Azure base map is an implementation-owned layer
/// rendered behind the public <see cref="Layers"/> collection. Public layers render from
/// first to last, so later entries appear above earlier entries.
/// </para>
/// <para>
/// A non-<see cref="MapStyle.Blank"/> style requires a valid <see cref="MapServiceToken"/>
/// to acquire Azure base-map tiles. The current implementation uses an Azure Maps
/// subscription key: create or select an Azure Maps account in the Azure portal, open its
/// <c>Authentication</c> page, copy the Primary Key or Secondary Key, and assign it to
/// <see cref="MapServiceToken"/>. For key-management and authentication guidance, see the
/// <see href="https://learn.microsoft.com/azure/azure-maps/how-to-manage-authentication">
/// Azure Maps authentication documentation</see>. Keep subscription keys outside source
/// control and never commit them.
/// </para>
/// <para>
/// Azure raster and vector tile requests use the effective
/// <see cref="FrameworkElement.Language"/> value as their IETF language tag when that
/// property is explicitly set on the map. If it is not set, the language parameter is
/// omitted and Azure selects its default. Changing the language replaces the hidden Azure
/// acquisition session so cached tiles from different languages are not mixed.
/// </para>
/// <para>
/// Select <see cref="MapStyle.Blank"/> for a token-free surface containing only public
/// layers. A custom <see cref="TileLayer"/> also requires no Azure Maps token, although its
/// provider may impose separate credentials, terms, attribution, and usage requirements that
/// the application must satisfy.
/// </para>
/// <para>
/// The control, its dependency properties, all <see cref="MapLayer"/> objects, and attached
/// layer and element collections belong to the control's UI thread. Create, assign, read, and
/// mutate them there. Built-in <see cref="MapElement"/> properties and
/// <see cref="MapPolygon.Paths"/> publish immutable snapshots and may be changed by worker
/// threads, while creating or changing a referenced XAML <see cref="IconElement"/> remains
/// UI-thread-only.
/// </para>
/// <para>
/// At a high level, the UI thread publishes immutable camera and layer state, acquisition
/// workers perform bounded and cancellable network and image-decoding work, the control-owned
/// icon service observes and rasterizes XAML icons on the UI thread, a dedicated upload
/// worker creates graphics resources, and the render thread draws the newest accepted state.
/// Applications do not need to coordinate these workers, but should avoid unnecessary
/// dependency-property changes because each change may republish state or invalidate work.
/// </para>
/// <para>
/// <see cref="MapIcon"/>, <see cref="MapPolygon"/>, and <see cref="MapPolyline"/> are
/// lightweight built-in elements intended for large sets. Reuse the same unparented
/// <see cref="IconElement"/> among icons to reuse its raster and GPU texture, prefer
/// collection range operations for bulk changes, and avoid unnecessary property churn.
/// Changes to dependency properties on a referenced icon element automatically regenerate
/// its shared raster.
/// </para>
/// <para>
/// Unloading pauses rendering, uploads, and raster acquisition while preserving scene,
/// layer, icon, and worker state for immediate restoration. Reconstructable state is
/// released only after the control is unloaded and detached from its <see cref="XamlRoot"/>.
/// </para>
/// </remarks>
/// <example>
/// Read the subscription key from configuration or an environment variable rather than
/// embedding it in source code:
/// <code>
/// using System;
/// using WinUIEx.Maps;
///
/// string subscriptionKey =
///     Environment.GetEnvironmentVariable("AZURE_MAPS_SUBSCRIPTION_KEY")
///     ?? throw new InvalidOperationException(
///         "Configure the Azure Maps subscription key before creating the map.");
///
/// var map = new MapControl
/// {
///     MapStyle = MapStyle.Road,
///     MapServiceToken = subscriptionKey,
/// };
/// </code>
/// Do not commit the key or configuration files containing it.
/// </example>
public sealed partial class MapControl : Control
{
    private MapLayerCollection _layers = null!;
    private bool _isRestoringLayers;
    private readonly HashSet<MapElementsLayer> _trackedLayers =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<MapLayer> _trackedMapLayers =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MapElementCollection, int> _trackedElementCollections =
        new(ReferenceEqualityComparer.Instance);
    private Dictionary<MapElement, int> _elementCounts =
        new(ReferenceEqualityComparer.Instance);
    private readonly MapRenderer _renderer;
    private readonly MapIconService _iconService;
    private readonly RasterTileManager _rasterTileManager;
    private AzureTileLayer? _azureTileLayer;
    private long _attributionGeneration;
    private SwapChainPanel? _panel;
    private Border? _attributionContainer;
    private TextBlock? _attributionText;
    private string _attributionAutomationName = string.Empty;
    private TextBlock? _mapStateText;
    private MapAccessibilitySnapshot? _accessibilitySnapshot;
    private string _accessibilityDescription = string.Empty;
    private bool _isAccessibilityDescriptionDetailed;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer?
        _accessibilityAnnouncementTimer;
    private InfoBar? _azureAuthenticationInfoBar;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer?
        _missingAzureTokenTimer;
    private XamlRoot? _iconXamlRoot;
    private readonly WeakXamlRootChangedSubscription
        _xamlRootChangedSubscription;
    private bool _suppressCameraUpdate;
    private bool _isNormalizingHeading;
    private bool _isNormalizingPitch;
    private bool _hasPublishedCameraTarget;
    private bool _runtimeResourcesReleased = true;
    private bool _lifecycleSubscriptionsAttached;
    private bool _unloadReconciliationQueued;
    private bool _isMapElementSnapshotQueued;
    private readonly object _pendingElementChangesSync = new();
    private readonly HashSet<MapElement> _pendingElementChanges =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<MapElement> _publishedMapElements = [];
    private readonly List<MapElementsLayer> _publishedElementLayers = [];
    private MapElementInputEventKind _elementInputHandlers;
    private bool _areElementChangesQueued;
    private MapControlAutomationPeer? _automationPeer;
    private int _automationCameraUpdateQueued;

    internal int TrackedElementReferenceCount => _elementCounts.Values.Sum();

    internal int TrackedIconTextureCount => _iconService.TrackedTextureCount;

    internal AzureTileLayer? AzureBaseLayer => _azureTileLayer;

    internal MapElementInputEventKind ElementInputHandlers => _elementInputHandlers;

    internal bool RuntimeResourcesReleased => _runtimeResourcesReleased;

    internal bool RendererHasDeviceResources => _renderer.HasDeviceResources;

    /// <summary>
    /// Waits for the current tile wave and renderer uploads, then reads back only the map
    /// surface from the completed D3D frame.
    /// </summary>
    internal async Task<MapRenderFrame> CaptureRenderedFrameAsync(
        CancellationToken cancellationToken = default)
    {
        await _rasterTileManager
            .WaitForIdleAsync(cancellationToken)
            .ConfigureAwait(false);
        return await _renderer
            .CaptureFrameAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal int ActiveRasterWorkerCount => _rasterTileManager.ActiveWorkerCount;

    internal bool RasterManagerHasScene => _rasterTileManager.HasScene;

    internal int GetIconTextureReferenceCount(IconElement iconElement) =>
        _iconService.GetTextureReferenceCount(iconElement);

    internal long GetIconTextureVersion(IconElement iconElement) =>
        _iconService.GetTextureVersion(iconElement);

    internal bool TryGetDisplayedCamera(
        out BasicGeoposition center,
        out double zoom,
        out double heading,
        out double pitch)
    {
        bool hasCamera = _renderer.TryGetDisplayedCamera(
            out MapCenter displayedCenter,
            out zoom,
            out heading,
            out pitch);
        center = new BasicGeoposition
        {
            Longitude = displayedCenter.Longitude,
            Latitude = displayedCenter.Latitude,
        };
        return hasCamera;
    }

    internal bool TryGetDisplayedHeading(out double heading)
    {
        bool hasCamera = TryGetDisplayedCamera(
            out _,
            out _,
            out heading,
            out _);
        return hasCamera;
    }

    internal bool TryGetDisplayedPitch(out double pitch)
    {
        bool hasCamera = TryGetDisplayedCamera(
            out _,
            out _,
            out _,
            out pitch);
        return hasCamera;
    }

    internal bool TryHitTestMapElement(Point offset, out MapElement? element)
    {
        element = null;
        if (_panel is null)
        {
            return false;
        }

        Point panelOffset = TransformToVisual(_panel).TransformPoint(offset);
        if (!TryHitTestMapElement(panelOffset, out MapElementHitTarget hit))
        {
            return false;
        }

        element = hit.Element;
        return true;
    }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() =>
        _automationPeer ??= new MapControlAutomationPeer(this);

    /// <summary>
    /// Initializes a map control with an empty public layer collection, the road map style,
    /// and interactive pan and zoom input enabled.
    /// </summary>
    /// <remarks>
    /// Construct and use the control on a WinUI UI thread. Rendering resources are deferred
    /// until the control template is applied.
    /// </remarks>
    public MapControl()
    {
        _xamlRootChangedSubscription =
            new WeakXamlRootChangedSubscription(this);
        _renderer = new MapRenderer();
        _renderer.SceneChanged += OnRendererSceneChanged;
        _renderer.DisplayedCameraChanged += OnRendererDisplayedCameraChanged;
        _renderer.AccessibilitySnapshotChanged +=
            OnRendererAccessibilitySnapshotChanged;
        _iconService = new MapIconService(_renderer, DispatcherQueue);
        _rasterTileManager = new RasterTileManager(_renderer);
        _rasterTileManager.AttributionChanged += OnAttributionChanged;
        _rasterTileManager.AzureAuthenticationFailed +=
            OnAzureAuthenticationFailed;
        SetValue(LayersProperty, new MapLayerCollection());
        InitializeTextScaling();
        InitializeLocalization();
        DefaultStyleKey = typeof(MapControl);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
        AddHandler(
            PointerEnteredEvent,
            new PointerEventHandler(OnMapElementPointerEntered),
            handledEventsToo: true);
        AddHandler(
            PointerExitedEvent,
            new PointerEventHandler(OnMapElementPointerExited),
            handledEventsToo: true);
        AddHandler(
            PointerMovedEvent,
            new PointerEventHandler(OnMapElementPointerMoved),
            handledEventsToo: true);
        AddHandler(
            PointerPressedEvent,
            new PointerEventHandler(OnMapElementPointerPressed),
            handledEventsToo: true);
        AddHandler(
            PointerReleasedEvent,
            new PointerEventHandler(OnMapElementPointerReleased),
            handledEventsToo: true);
        AddHandler(
            TappedEvent,
            new TappedEventHandler(OnMapElementTapped),
            handledEventsToo: true);
        AddHandler(
            RightTappedEvent,
            new RightTappedEventHandler(OnMapElementRightTapped),
            handledEventsToo: true);
        MapControlEventSource.Log.ControlCreated();
    }

    /// <summary>
    /// Converts a point on the map to a geographic location.
    /// </summary>
    /// <param name="offset">A point on the map to convert to a geographic location.</param>
    /// <param name="location">
    /// When this method returns, contains the corresponding geographic location.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the point was converted to a valid geographic location;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// The offset is relative to the upper-left corner of the control. Conversion uses the
    /// currently displayed map camera, including any in-progress pan or zoom animation.
    /// See
    /// <see href="https://learn.microsoft.com/en-us/uwp/api/windows.ui.xaml.controls.maps.mapcontrol.trygetlocationfromoffset">
    /// MapControl.TryGetLocationFromOffset</see>.
    /// </remarks>
    public bool TryGetLocationFromOffset(Point offset, out Geopoint location)
    {
        location = Center ?? new Geopoint(new BasicGeoposition());
        if (_runtimeResourcesReleased || _panel?.XamlRoot is null)
        {
            return false;
        }

        Point panelOffset = TransformToVisual(_panel).TransformPoint(offset);
        if (!_renderer.TryGetLocationFromOffset(panelOffset.X, panelOffset.Y, out MapCenter center))
        {
            return false;
        }

        location = new Geopoint(new BasicGeoposition
        {
            Longitude = center.Longitude,
            Latitude = center.Latitude,
        });
        return true;
    }

    /// <summary>
    /// Builds or refreshes the control's visual tree and connects it to the renderer.
    /// </summary>
    /// <remarks>
    /// This lifecycle method is called by WinUI on the UI thread. It resolves the required
    /// template parts and initializes or reconnects the tile manager, renderer, camera, layer
    /// snapshots, and control-owned icon service.
    /// </remarks>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _attributionContainer =
            GetTemplateChild("PART_AttributionContainer") as Border;
        _attributionText = GetTemplateChild("PART_Attribution") as TextBlock;
        _mapStateText = GetTemplateChild("PART_MapState") as TextBlock;
        if (_mapStateText is not null)
        {
            _mapStateText.Text = _accessibilityDescription;
        }
        _azureAuthenticationInfoBar =
            GetTemplateChild("PART_AzureAuthenticationInfoBar") as InfoBar;
        _iconService.SetRasterizationHost(
            GetTemplateChild("PART_IconHost") as ContentControl);

        if (GetTemplateChild("PART_SwapChain") is not SwapChainPanel panel)
        {
            MapControlEventSource.Log.ControlFailure(
                "ApplyTemplate.MissingSwapChainPanel",
                nameof(InvalidOperationException),
                unchecked((int)0x80004005));
            return;
        }

        if (_panel is not null)
        {
            _panel.SizeChanged -= OnPanelSizeChanged;
        }
        _panel = panel;
        _panel.SizeChanged += OnPanelSizeChanged;

        UpdateCameraTarget(forceImmediate: true);
        _renderer.Attach(panel);
        AttachIconXamlRoot();
        _renderer.SetMaximumTileZoom(MapCamera.MaximumTileZoom);
        ReplaceAzureTileLayer();
        if (IsLoaded)
        {
            ResumeRuntimeResources();
        }
        UpdateAttribution();
        UpdateAzureAuthenticationInfoBar();
        PublishLayerSnapshots();
        PublishMapElementSnapshot();
        _iconService.QueueAllRasterizations(force: true);
    }

    private void RestoreLayersProperty()
    {
        MapLayerCollection validLayers = _layers ?? new MapLayerCollection();
        _isRestoringLayers = true;
        try
        {
            SetValue(LayersProperty, validLayers);
        }
        finally
        {
            _isRestoringLayers = false;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MapControlEventSource.Log.ControlLoaded();
        AttachSystemAccessibilitySettings();
        AttachLifecycleSubscriptions();
        _iconService.SetLoaded(true);
        AttachIconXamlRoot();
        bool runtimeRecreated = false;
        if (_panel is not null)
        {
            _panel.SizeChanged -= OnPanelSizeChanged;
            _panel.SizeChanged += OnPanelSizeChanged;
            UpdateCameraTarget(forceImmediate: true);
            _renderer.Attach(_panel);
            _renderer.SetMaximumTileZoom(MapCamera.MaximumTileZoom);
            runtimeRecreated = ResumeRuntimeResources();
        }
        if (string.IsNullOrWhiteSpace(MapServiceToken) && MapStyle != MapStyle.Blank)
        {
            MapControlEventSource.Log.ControlFailure(
                "MapServiceToken.Missing",
                nameof(InvalidOperationException),
                0);
        }
        UpdateAzureAuthenticationInfoBar();
        UpdateCameraTarget(forceImmediate: true);
        _iconService.QueueAllRasterizations(force: runtimeRecreated);
        if (_accessibilitySnapshot is not null)
        {
            Microsoft.UI.Dispatching.DispatcherQueueTimer timer =
                GetAccessibilityAnnouncementTimer();
            timer.Stop();
            timer.Start();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        MapControlEventSource.Log.ControlUnloaded();
        _iconService.SetLoaded(false);
        StopAccessibilityAnnouncementTimer();
        StopMissingAzureTokenTimer();
        if (_azureAuthenticationInfoBar is not null)
        {
            _azureAuthenticationInfoBar.IsOpen = false;
        }
        QueueUnloadReconciliation();
    }

    private void QueueUnloadReconciliation()
    {
        if (_unloadReconciliationQueued)
        {
            return;
        }

        // WinUI can raise Loaded and Unloaded out of order during rapid reparenting and
        // IsLoaded can disagree with the actual tree state. Reconcile after the events
        // settle and inspect visual ancestry instead: https://github.com/microsoft/microsoft-ui-xaml/issues/1900
        _unloadReconciliationQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _unloadReconciliationQueued = false;
            if (IsAttachedToXamlRootVisualTree())
            {
                _iconService.SetLoaded(true);
                _renderer.Resume();
                _rasterTileManager.Resume();
                return;
            }

            _iconService.SetLoaded(false);
            _rasterTileManager.Suspend();
            _renderer.SuspendBackgroundWork();
            _renderer.Suspend();
            DetachSystemAccessibilitySettings();
            CancelPendingViewChange();
        });
    }

    private bool IsAttachedToXamlRootVisualTree()
    {
        if (_panel?.XamlRoot?.Content is not DependencyObject rootContent)
        {
            return false;
        }

        DependencyObject? current = _panel;
        while (current is not null)
        {
            if (ReferenceEquals(current, rootContent))
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void AttachIconXamlRoot()
    {
        if (!IsLoaded)
        {
            DetachIconXamlRoot();
            return;
        }

        XamlRoot? xamlRoot = XamlRoot;
        if (!ReferenceEquals(_iconXamlRoot, xamlRoot))
        {
            DetachIconXamlRoot();
            _iconXamlRoot = xamlRoot;
            if (_iconXamlRoot is not null)
            {
                _xamlRootChangedSubscription.Attach(_iconXamlRoot);
            }
        }
        UpdateIconRasterizationScale();
    }

    private void DetachIconXamlRoot()
    {
        if (_iconXamlRoot is null)
        {
            return;
        }

        _xamlRootChangedSubscription.Detach();
        _iconXamlRoot = null;
    }

    private void AttachLifecycleSubscriptions()
    {
        if (_lifecycleSubscriptionsAttached)
        {
            return;
        }

        _lifecycleSubscriptionsAttached = true;
        _layers.Changing += OnCollectionChanging;
        _layers.CollectionChanged += OnLayersChanged;
        AttachAllLayers();
        MarkMapElementSnapshotDirty();
    }

    private void DetachLifecycleSubscriptions()
    {
        if (!_lifecycleSubscriptionsAttached)
        {
            return;
        }

        _lifecycleSubscriptionsAttached = false;
        _layers.Changing -= OnCollectionChanging;
        _layers.CollectionChanged -= OnLayersChanged;
        DetachAllLayers();
        lock (_pendingElementChangesSync)
        {
            _pendingElementChanges.Clear();
            _areElementChangesQueued = false;
        }
        MarkMapElementSnapshotDirty();
    }

    private void OnIconXamlRootChanged(
        XamlRoot sender,
        XamlRootChangedEventArgs args)
    {
        if (IsLoaded)
        {
            UpdateIconRasterizationScale();
        }
    }

    private void OnIconXamlRootContentUnloaded()
    {
        if (!IsLoaded)
        {
            ReleaseDetachedResources();
        }
    }

    private void UpdateIconRasterizationScale() =>
        _iconService.UpdateRasterizationScale(
            _iconXamlRoot?.RasterizationScale ?? 1);

    private bool ResumeRuntimeResources()
    {
        if (!_runtimeResourcesReleased)
        {
            _renderer.Resume();
            _rasterTileManager.Resume();
            return false;
        }

        _runtimeResourcesReleased = false;
        _iconService.SetRuntimeResourcesAvailable(true);
        _renderer.Resume();
        PublishLayerSnapshots();
        PublishMapElementSnapshot();
        _rasterTileManager.Resume();
        return true;
    }

    private void ReleaseDetachedResources()
    {
        if (_runtimeResourcesReleased)
        {
            return;
        }

        _runtimeResourcesReleased = true;
        _iconService.SetRuntimeResourcesAvailable(false);
        DetachIconXamlRoot();
        _iconService.ReleaseDormantResources();
        DetachLifecycleSubscriptions();
        _rasterTileManager.ReleaseWorkers("XamlRootDetached");
        _renderer.ReleaseDormantResources();
        if (_panel is not null)
        {
            _panel.SizeChanged -= OnPanelSizeChanged;
        }
    }

    private void OnLayersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        EnsureUiThread();
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                AttachLayers(e.NewItems);
                break;
            case NotifyCollectionChangedAction.Remove:
                DetachLayers(e.OldItems);
                break;
            case NotifyCollectionChangedAction.Replace:
                DetachLayers(e.OldItems);
                AttachLayers(e.NewItems);
                break;
            case NotifyCollectionChangedAction.Reset:
                DetachAllLayers();
                AttachAllLayers();
                break;
        }
        MarkMapElementSnapshotDirty();
        PublishLayerSnapshots();
        UpdateAttribution();
        TraceLayersChanged($"Layers{e.Action}");
    }

    private void OnLayerMapElementsChanged(
        object? sender,
        MapElementsCollectionChangedEventArgs e)
    {
        EnsureUiThread();
        if (sender is not MapElementsLayer layer || !_trackedLayers.Contains(layer))
        {
            return;
        }

        DetachElementCollection(e.OldCollection);
        RemoveMapElements(e.OldCollection);
        AttachElementCollection(e.NewCollection);
        AddMapElements(e.NewCollection);
        MarkMapElementSnapshotDirty();
        TraceLayersChanged("MapElementsReplaced");
    }

    private void OnMapElementsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        EnsureUiThread();
        if (sender is not MapElementCollection collection ||
            !_trackedElementCollections.TryGetValue(collection, out int layerCount))
        {
            return;
        }

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                Repeat(layerCount, () => AddMapElements(e.NewItems));
                break;
            case NotifyCollectionChangedAction.Remove:
                Repeat(layerCount, () => RemoveMapElements(e.OldItems));
                break;
            case NotifyCollectionChangedAction.Replace:
                Repeat(layerCount, () =>
                {
                    RemoveMapElements(e.OldItems);
                    AddMapElements(e.NewItems);
                });
                break;
            case NotifyCollectionChangedAction.Reset:
                RebuildMapElements();
                break;
        }
        MarkMapElementSnapshotDirty();
    }

    private void OnMapElementChanged(object? sender, EventArgs e)
    {
        if (sender is not MapElement element)
        {
            return;
        }
        if (!DispatcherQueue.HasThreadAccess)
        {
            QueueMapElementChange(element);
            return;
        }

        QueueMapElementChange(element);
    }

    private void QueueMapElementChange(MapElement element)
    {
        lock (_pendingElementChangesSync)
        {
            _pendingElementChanges.Add(element);
            if (_areElementChangesQueued)
            {
                return;
            }
            _areElementChangesQueued = true;
        }

        if (!DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            ProcessPendingMapElementChanges))
        {
            lock (_pendingElementChangesSync)
            {
                _areElementChangesQueued = false;
                _pendingElementChanges.Clear();
            }
        }
    }

    private void ProcessPendingMapElementChanges()
    {
        MapElement[] changes;
        lock (_pendingElementChangesSync)
        {
            changes = [.. _pendingElementChanges];
            _pendingElementChanges.Clear();
            _areElementChangesQueued = false;
        }
        bool rebuildSnapshots = false;
        List<MapIcon> changedIcons = [];
        foreach (MapElement element in changes)
        {
            if (element is MapIcon icon)
            {
                changedIcons.Add(icon);
            }
            else
            {
                rebuildSnapshots = true;
            }
        }
        rebuildSnapshots |= !_iconService.ProcessChangedIcons(changedIcons);
        if (rebuildSnapshots)
        {
            QueueMapElementSnapshot();
        }
    }

    private void AttachAllLayers() => AttachLayers(_layers);

    private void AttachLayers(System.Collections.IList? items)
    {
        if (items is null)
        {
            return;
        }

        foreach (object? item in items)
        {
            MapLayer layer = item as MapLayer ??
                throw new ArgumentException("Layers accepts only non-null MapLayer instances.");
            if (_trackedMapLayers.Add(layer))
            {
                layer.Changed += OnLayerChanged;
            }
            if (layer is not MapElementsLayer elementsLayer || !_trackedLayers.Add(elementsLayer))
            {
                continue;
            }

            elementsLayer.MapElementsChanging += OnCollectionChanging;
            elementsLayer.MapElementsChanged += OnLayerMapElementsChanged;
            elementsLayer.InputHandlersChanged += OnLayerInputHandlersChanged;
            AttachElementCollection(elementsLayer.MapElements);
            AddMapElements(elementsLayer.MapElements);
        }
        RefreshElementInputHandlers();
    }

    private void DetachLayers(System.Collections.IList? items)
    {
        if (items is null)
        {
            return;
        }

        foreach (object? item in items)
        {
            if (item is not MapLayer layer)
            {
                continue;
            }
            if (_trackedMapLayers.Remove(layer))
            {
                layer.Changed -= OnLayerChanged;
            }
            if (layer is not MapElementsLayer elementsLayer ||
                !_trackedLayers.Remove(elementsLayer))
            {
                continue;
            }

            elementsLayer.MapElementsChanging -= OnCollectionChanging;
            elementsLayer.MapElementsChanged -= OnLayerMapElementsChanged;
            elementsLayer.InputHandlersChanged -= OnLayerInputHandlersChanged;
            DetachElementCollection(elementsLayer.MapElements);
            RemoveMapElements(elementsLayer.MapElements);
        }
        RefreshElementInputHandlers();
    }

    private void DetachAllLayers()
    {
        foreach (MapLayer layer in _trackedMapLayers.ToArray())
        {
            DetachLayers(new[] { layer });
        }
    }

    private void OnLayerChanged(object? sender, MapLayerChangedEventArgs e)
    {
        EnsureUiThread();
        if (sender is not MapLayer layer || !_trackedMapLayers.Contains(layer))
        {
            return;
        }
        if (e.Property == MapLayer.AttributionProperty ||
            e.Property == MapLayer.AttributionLinkProperty)
        {
            UpdateAttribution();
            return;
        }
        PublishLayerSnapshots();
        UpdateAttribution();
        if (layer is MapElementsLayer)
        {
            MarkMapElementSnapshotDirty();
        }
    }

    private void OnLayerInputHandlersChanged(object? sender, EventArgs e) =>
        RefreshElementInputHandlers();

    private void RefreshElementInputHandlers()
    {
        MapElementInputEventKind handlers = MapElementInputEventKind.None;
        foreach (MapElementsLayer layer in _trackedLayers)
        {
            handlers |= layer.InputHandlers;
        }
        _elementInputHandlers = handlers;
        if ((handlers & MapElementInputEventKind.PointerHover) == 0)
        {
            _hoveredMapElements.Clear();
        }
    }

    private void PublishLayerSnapshots()
    {
        LayerSnapshotPublication publication = CreateLayerSnapshotPublication(
            _azureTileLayer,
            _layers,
            _animationsEnabled);
        _renderer.SetLayerRenderPlan(publication.RenderPlan);
        if (!_runtimeResourcesReleased)
        {
            _rasterTileManager.SetLayers(publication.RasterLayers);
        }
    }

    /// <summary>
    /// Captures the hidden Azure layer and public layers into one immutable ordered plan.
    /// </summary>
    /// <remarks>
    /// UI-thread-only: this method reads dependency-object properties. The returned records
    /// are immutable and are the only raster state passed to render/scheduler workers.
    /// </remarks>
    internal static LayerSnapshotPublication CreateLayerSnapshotPublication(
        AzureTileLayer? azureLayer,
        IReadOnlyList<MapLayer> layers,
        bool animationsEnabled = true)
    {
        LayerRenderPlanBuilder renderPlan = new();
        List<TileLayerSnapshot> tileLayers = [];
        for (int index = 0; index < layers.Count; index++)
        {
            MapLayer layer = layers[index];
            if (layer is TileLayer tileLayer)
            {
                AddTileLayerSnapshot(
                    tileLayer,
                    index,
                    animationsEnabled,
                    renderPlan,
                    tileLayers);
            }
            else if (layer is MapElementsLayer)
            {
                renderPlan.Add(new LayerRenderSnapshot(
                    LayerRenderKind.MapElements,
                    index,
                    0,
                    layer.IsVisible,
                    layer.Opacity,
                    TimeSpan.Zero,
                    0,
                    24,
                    0,
                    256,
                    -1));
            }
        }
        TileLayerSnapshot? azureSnapshot = azureLayer?.CreateSnapshot();
        if (!animationsEnabled && azureSnapshot is not null)
        {
            azureSnapshot = azureSnapshot with { FadeDuration = TimeSpan.Zero };
        }

        return LayerSnapshotPublication.PrependHiddenAzure(
            azureSnapshot,
            renderPlan.Build(),
            tileLayers.ToArray());
    }

    /// <summary>
    /// Captures a layer on the UI thread before publishing it to raster workers.
    /// </summary>
    private static void AddTileLayerSnapshot(
        TileLayer tileLayer,
        int layerIndex,
        bool animationsEnabled,
        LayerRenderPlanBuilder renderPlan,
        List<TileLayerSnapshot> tileLayers)
    {
        TileLayerSnapshot snapshot = tileLayer.CreateSnapshot();
        if (!animationsEnabled)
        {
            snapshot = snapshot with { FadeDuration = TimeSpan.Zero };
        }

        tileLayers.Add(snapshot);
        renderPlan.Add(new LayerRenderSnapshot(
            snapshot.Acquisition.RenderKind,
            layerIndex,
            snapshot.RuntimeId,
            snapshot.IsVisible,
            snapshot.Opacity,
            snapshot.FadeDuration,
            snapshot.MinZoom,
            snapshot.MaxZoom,
            snapshot.MinSourceZoom,
            snapshot.TileSize,
            -1));
    }

    /// <summary>
    /// Replaces the hidden Azure layer from UI-thread dependency-property state.
    /// </summary>
    private void ReplaceAzureTileLayer()
    {
        string? language = GetAzureRequestLanguage();
        AzureTileLayer? replacement = MapStyle == MapStyle.Blank
            ? null
            : _azureTileLayer is not null &&
                _azureTileLayer.Matches(
                    MapStyle,
                    MapServiceToken,
                    language)
                ? _azureTileLayer
                : CreateAzureBaseLayer(
                    MapStyle,
                    MapServiceToken,
                    language);
        if (ReferenceEquals(_azureTileLayer, replacement))
        {
            return;
        }
        _azureTileLayer = replacement;
        _attributionGeneration = 0;
        UpdateAttribution();
    }

    internal string? GetAzureRequestLanguage() =>
        ReferenceEquals(
                ReadLocalValue(LanguageProperty),
                DependencyProperty.UnsetValue)
            ? null
            : AzureTileAcquisitionSession.GetRequestLanguage(Language);

    internal static AzureTileLayer? CreateAzureBaseLayer(MapStyle style, string? token) =>
        CreateAzureBaseLayer(style, token, null);

    internal static AzureTileLayer? CreateAzureBaseLayer(
        MapStyle style,
        string? token,
        string? language) =>
        HasAzureBaseLayer(style)
            ? new AzureTileLayer(style, token, language)
            : null;

    internal static bool HasAzureBaseLayer(MapStyle style) => style != MapStyle.Blank;

    private void AttachElementCollection(MapElementCollection collection)
    {
        _trackedElementCollections.TryGetValue(collection, out int count);
        _trackedElementCollections[collection] = count + 1;
        if (count == 0)
        {
            collection.Changing += OnCollectionChanging;
            collection.CollectionChanged += OnMapElementsChanged;
        }
    }

    private void DetachElementCollection(MapElementCollection collection)
    {
        if (!_trackedElementCollections.TryGetValue(collection, out int count))
        {
            return;
        }
        if (count == 1)
        {
            collection.Changing -= OnCollectionChanging;
            collection.CollectionChanged -= OnMapElementsChanged;
            _trackedElementCollections.Remove(collection);
        }
        else
        {
            _trackedElementCollections[collection] = count - 1;
        }
    }

    private static void Repeat(int count, Action action)
    {
        for (int index = 0; index < count; index++)
        {
            action();
        }
    }

    private void OnCollectionChanging(object? sender, EventArgs e) => EnsureUiThread();

    private void AddMapElements(System.Collections.IList? items)
    {
        if (items is null)
        {
            return;
        }

        foreach (object? item in items)
        {
            MapElement element = item as MapElement ??
                throw new ArgumentException("MapElements accepts only non-null MapElement instances.");
            _elementCounts.TryGetValue(element, out int count);
            _elementCounts[element] = count + 1;
            if (count == 0)
            {
                element.Changed += OnMapElementChanged;
            }
            if (element is MapIcon icon)
            {
                _iconService.AttachIcon(icon);
            }
        }
    }

    private void RemoveMapElements(System.Collections.IList? items)
    {
        if (items is null)
        {
            return;
        }

        foreach (object? item in items)
        {
            if (item is not MapElement element ||
                !_elementCounts.TryGetValue(element, out int count))
            {
                continue;
            }
            if (element is MapIcon icon)
            {
                _iconService.DetachIcon(icon);
            }
            if (count == 1)
            {
                element.Changed -= OnMapElementChanged;
                _elementCounts.Remove(element);
            }
            else
            {
                _elementCounts[element] = count - 1;
            }
        }
    }

    private void RebuildMapElements()
    {
        foreach (MapElement element in _elementCounts.Keys)
        {
            element.Changed -= OnMapElementChanged;
        }
        _iconService.DetachAllReferences();
        _elementCounts.Clear();
        foreach (MapElementsLayer layer in _layers.OfType<MapElementsLayer>())
        {
            AddMapElements(layer.MapElements);
        }
    }

    private void MarkMapElementSnapshotDirty()
    {
        _hoveredMapElements.Clear();
        QueueMapElementSnapshot();
    }

    private void TraceLayersChanged(string operation)
    {
        if (!MapControlEventSource.Log.IsEnabled(
            System.Diagnostics.Tracing.EventLevel.Informational,
            MapControlEventSource.Keywords.Icons))
        {
            return;
        }

        MapControlEventSource.Log.LayersChanged(
            operation,
            _layers.Count,
            _layers.OfType<MapElementsLayer>().Count(),
            _layers.OfType<MapElementsLayer>().Sum(layer => layer.MapElements.Count));
    }

    private void QueueMapElementSnapshot()
    {
        if (_isMapElementSnapshotQueued)
        {
            return;
        }
        _isMapElementSnapshotQueued = true;
        if (!DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                _isMapElementSnapshotQueued = false;
                PublishMapElementSnapshot();
            }))
        {
            _isMapElementSnapshotQueued = false;
            PublishMapElementSnapshot();
        }
    }

    private void PublishMapElementSnapshot()
    {
        if (_runtimeResourcesReleased)
        {
            return;
        }

        int elementCapacity = _layers.OfType<MapElementsLayer>()
            .Sum(layer => layer.MapElements.Count);
        List<MapIconSnapshot> iconSnapshots = new(elementCapacity);
        List<MapGeometrySnapshot> geometrySnapshots = new(elementCapacity);
        _publishedMapElements.Clear();
        _publishedElementLayers.Clear();
        _iconService.BeginSnapshotRebuild();
        int orderIndex = 0;
        for (int layerIndex = 0; layerIndex < _layers.Count; layerIndex++)
        {
            if (_layers[layerIndex] is not MapElementsLayer elementsLayer)
            {
                continue;
            }

            List<(MapElement Element, int CollectionIndex, MapElementState State)>
                orderedElements = new(elementsLayer.MapElements.Count);
            for (int collectionIndex = 0;
                collectionIndex < elementsLayer.MapElements.Count;
                collectionIndex++)
            {
                MapElement element = elementsLayer.MapElements[collectionIndex];
                orderedElements.Add((
                    element,
                    collectionIndex,
                    element.GetBaseState()));
            }
            orderedElements.Sort(static (left, right) =>
            {
                int comparison = left.State.ZIndex.CompareTo(right.State.ZIndex);
                return comparison != 0
                    ? comparison
                    : left.CollectionIndex.CompareTo(right.CollectionIndex);
            });

            foreach ((MapElement element, _, MapElementState elementState) in
                orderedElements)
            {
                if (!elementState.IsVisible)
                {
                    continue;
                }

                int elementIndex = _publishedMapElements.Count;
                if (element is MapIcon icon)
                {
                    if (!_iconService.TryCreateSnapshot(
                        icon,
                        iconSnapshots.Count,
                        layerIndex,
                        elementIndex,
                        orderIndex,
                        elementState,
                        out MapIconSnapshot snapshot))
                    {
                        continue;
                    }

                    iconSnapshots.Add(snapshot);
                }
                else if (element is MapPolygon polygon)
                {
                    MapPolygonState state = polygon.GetState();
                    geometrySnapshots.Add(new MapGeometrySnapshot(
                        MapGeometryKind.Polygon,
                        state.Geometry,
                        MapColorSnapshot.FromColor(state.FillColor),
                        MapColorSnapshot.FromColor(state.StrokeColor),
                        state.StrokeDashed,
                        state.StrokeThickness,
                        layerIndex,
                        elementIndex,
                        orderIndex,
                        elementState.IsEnabled));
                }
                else if (element is MapPolyline polyline)
                {
                    MapPolylineState state = polyline.GetState();
                    geometrySnapshots.Add(new MapGeometrySnapshot(
                        MapGeometryKind.Polyline,
                        state.Geometry,
                        default,
                        MapColorSnapshot.FromColor(state.StrokeColor),
                        state.StrokeDashed,
                        state.StrokeThickness,
                        layerIndex,
                        elementIndex,
                        orderIndex,
                        elementState.IsEnabled));
                }
                else
                {
                    continue;
                }

                _publishedMapElements.Add(element);
                _publishedElementLayers.Add(elementsLayer);
                orderIndex++;
            }
        }
        MapIconSnapshot[] publishedIcons = iconSnapshots.ToArray();
        _renderer.SetMapElements(publishedIcons, geometrySnapshots.ToArray());
        _iconService.CommitSnapshotRebuild(publishedIcons);
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        _iconService.InvalidateTheme();
    }

    private void EnsureUiThread()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException(
                "Layers and layer MapElements must be assigned or mutated on the MapControl UI thread.");
        }
    }

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCameraTarget(preservePendingViewChange: true);
    }

    private void UpdateCameraTarget(
        bool forceImmediate = false,
        MapAnimationKind animation = MapAnimationKind.Default,
        bool preservePendingViewChange = false)
    {
        if (!preservePendingViewChange)
        {
            CancelPendingViewChange();
        }
        if (_panel is null ||
            _panel.ActualWidth <= 0 ||
            _panel.ActualHeight <= 0)
        {
            return;
        }

        BasicGeoposition position = Center?.Position ?? new BasicGeoposition();
        bool applyImmediately =
            forceImmediate ||
            !_animationsEnabled ||
            !IsLoaded ||
            !_hasPublishedCameraTarget;
        if (applyImmediately)
        {
            _renderer.SetCameraTargetImmediately(
                position.Longitude,
                position.Latitude,
                ZoomLevel,
                _panel.ActualWidth,
                _panel.ActualHeight,
                Heading,
                Pitch);
        }
        else
        {
            _renderer.SetCameraTarget(
                position.Longitude,
                position.Latitude,
                ZoomLevel,
                _panel.ActualWidth,
                _panel.ActualHeight,
                Heading,
                Pitch,
                animation);
        }
        _hasPublishedCameraTarget = true;
    }

    private void OnRendererSceneChanged(MapScene scene)
    {
        _rasterTileManager.UpdateScene(scene);
    }

    private void OnRendererAccessibilitySnapshotChanged(
        MapAccessibilitySnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _accessibilitySnapshot = snapshot;
            if (!IsLoaded)
            {
                return;
            }
            Microsoft.UI.Dispatching.DispatcherQueueTimer timer =
                GetAccessibilityAnnouncementTimer();
            timer.Stop();
            timer.Start();
        });
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer
        GetAccessibilityAnnouncementTimer()
    {
        if (_accessibilityAnnouncementTimer is not null)
        {
            return _accessibilityAnnouncementTimer;
        }

        Microsoft.UI.Dispatching.DispatcherQueueTimer timer =
            DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(500);
        timer.IsRepeating = false;
        timer.Tick += OnAccessibilityAnnouncementTimerTick;
        _accessibilityAnnouncementTimer = timer;
        return timer;
    }

    private void StopAccessibilityAnnouncementTimer()
    {
        if (_accessibilityAnnouncementTimer is not
            Microsoft.UI.Dispatching.DispatcherQueueTimer timer)
        {
            return;
        }

        timer.Stop();
        timer.Tick -= OnAccessibilityAnnouncementTimerTick;
        _accessibilityAnnouncementTimer = null;
    }

    private void OnAccessibilityAnnouncementTimerTick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        MapAccessibilitySnapshot? snapshot = _accessibilitySnapshot;
        string description = snapshot is null
            ? string.Empty
            : CreateAccessibilityDescription(
                snapshot,
                _isAccessibilityDescriptionDetailed);
        UpdateAccessibilityDescription(
            description,
            snapshot?.Features.Length ?? 0,
            1);
    }

    private void ToggleAccessibilityDescriptionDetail()
    {
        _isAccessibilityDescriptionDetailed =
            !_isAccessibilityDescriptionDetailed;
        MapAccessibilitySnapshot? snapshot = _accessibilitySnapshot;
        if (snapshot is null)
        {
            return;
        }

        _accessibilityAnnouncementTimer?.Stop();
        UpdateAccessibilityDescription(
            CreateAccessibilityDescription(
                snapshot,
                _isAccessibilityDescriptionDetailed),
            snapshot.Features.Length,
            3);
    }

    private void UpdateAccessibilityDescription(
        string description,
        int featureCount,
        int changeReason)
    {
        bool changed = !string.Equals(
            description,
            _accessibilityDescription,
            StringComparison.Ordinal);
        if (changed)
        {
            string previousDescription = _accessibilityDescription;
            _accessibilityDescription = description;
            _automationPeer?.NotifyAccessibilityDescriptionChanged(
                previousDescription,
                description);
            if (_mapStateText is not null)
            {
                _mapStateText.Text = description;
            }
        }
        TextBlock? mapStateText = _mapStateText;
        bool raised = false;
        if (changed &&
            description.Length != 0 &&
            mapStateText is not null)
        {
            AutomationPeer? peer =
                FrameworkElementAutomationPeer.FromElement(mapStateText) ??
                FrameworkElementAutomationPeer.CreatePeerForElement(mapStateText);
            if (peer is not null)
            {
                peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                raised = true;
            }
        }
        MapControlEventSource.Log.AccessibilityAnnouncementDecision(
            featureCount,
            description.Length == 0 ? 0 : changed ? changeReason : 2,
            raised);
    }

    internal string GetAccessibilityDescription() => _accessibilityDescription;

    internal static string CreateAccessibilityDescription(
        IReadOnlyList<MapAccessibilityFeature> features)
    {
        string[] names = features
            .Select(feature => feature.Name.Trim())
            .Where(name => name.Length != 0)
            .Take(5)
            .ToArray();
        return names.Length switch
        {
            0 => string.Empty,
            1 => MapControlResources.Format(
                "MapDescriptionSingleFormat",
                names[0]),
            2 => MapControlResources.Format(
                "MapDescriptionPairFormat",
                names[0],
                names[1]),
            _ => MapControlResources.Format(
                "MapDescriptionManyFormat",
                string.Join(
                    MapControlResources.GetString(
                        "MapDescriptionListSeparator"),
                    names[..^1]),
                names[^1]),
        };
    }

    internal static string CreateAccessibilityDescription(
        MapAccessibilitySnapshot snapshot,
        bool detailed)
    {
        string simplified = CreateAccessibilityDescription(snapshot.Features);
        if (!detailed)
        {
            return simplified;
        }

        string mapDescription = simplified.Length == 0
            ? MapControlResources.GetString("MapDescriptionFallback")
            : simplified;
        string latitudeDirection = MapControlResources.GetString(
            snapshot.Latitude < 0 ? "DirectionSouth" : "DirectionNorth");
        string longitudeDirection = MapControlResources.GetString(
            snapshot.Longitude < 0 ? "DirectionWest" : "DirectionEast");
        return MapControlResources.Format(
            "MapDescriptionDetailedFormat",
            mapDescription,
            snapshot.Zoom,
            Math.Abs(snapshot.Latitude),
            latitudeDirection,
            Math.Abs(snapshot.Longitude),
            longitudeDirection);
    }

    private void OnAttributionChanged(
        object? sender,
        RasterAttributionUpdate update)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_attributionText is null)
            {
                return;
            }
            if (update.SourceId == 0)
            {
                if (_azureTileLayer is null)
                {
                    _attributionGeneration = 0;
                    UpdateAttribution();
                }
                return;
            }
            if (_azureTileLayer?.RuntimeId != update.SourceId ||
                update.Generation < _attributionGeneration)
            {
                return;
            }
            _attributionGeneration = update.Generation;
            _azureTileLayer.Attribution = update.Text;
            UpdateAttribution();
        });
    }

    private void UpdateAttribution()
    {
        if (_attributionContainer is null || _attributionText is null)
        {
            return;
        }

        _attributionText.Inlines.Clear();
        bool hasAttribution = false;
        List<string> attributionNames = [];
        AppendLayerAttribution(
            _azureTileLayer,
            attributionNames,
            ref hasAttribution);
        foreach (MapLayer layer in _layers)
        {
            AppendLayerAttribution(
                layer,
                attributionNames,
                ref hasAttribution);
        }
        _attributionContainer.Visibility =
            hasAttribution ? Visibility.Visible : Visibility.Collapsed;

        string automationName = hasAttribution
            ? MapControlResources.Format(
                "MapAttributionFormat",
                string.Join(
                    MapControlResources.GetString(
                        "MapAttributionListSeparator"),
                    attributionNames))
            : string.Empty;
        AutomationProperties.SetName(_attributionText, automationName);
        if (hasAttribution &&
            !string.Equals(
                automationName,
                _attributionAutomationName,
                StringComparison.Ordinal))
        {
            FrameworkElementAutomationPeer
                .FromElement(_attributionText)?
                .RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        _attributionAutomationName = automationName;
    }

    private void AppendLayerAttribution(
        MapLayer? layer,
        List<string> attributionNames,
        ref bool hasAttribution)
    {
        if (layer is null ||
            !layer.IsVisible ||
            layer.Opacity <= 0 ||
            string.IsNullOrWhiteSpace(layer.Attribution))
        {
            return;
        }

        if (hasAttribution)
        {
            _attributionText!.Inlines.Add(new Run
            {
                Text = MapControlResources.GetString(
                    "MapAttributionVisualSeparator"),
            });
        }

        string text = layer.Attribution.Trim();
        if (layer.AttributionLink is Uri link)
        {
            Hyperlink hyperlink = new() { NavigateUri = link };
            AutomationProperties.SetName(hyperlink, text);
            hyperlink.Inlines.Add(new Run { Text = text });
            _attributionText!.Inlines.Add(hyperlink);
        }
        else
        {
            _attributionText!.Inlines.Add(new Run { Text = text });
        }
        attributionNames.Add(text);
        hasAttribution = true;
    }

    private void OnAzureAuthenticationFailed(
        object? sender,
        RasterAuthenticationFailure failure)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_azureAuthenticationInfoBar is null ||
                _azureTileLayer?.RuntimeId != failure.RuntimeId)
            {
                return;
            }

            ShowAzureAuthenticationInfoBar(
                InfoBarSeverity.Error,
                MapControlResources.GetString(
                    "AzureAuthenticationFailedTitle"),
                MapControlResources.GetString(
                    "AzureAuthenticationFailedMessage"));
        });
    }

    private void UpdateAzureAuthenticationInfoBar()
    {
        if (_azureAuthenticationInfoBar is null)
        {
            return;
        }

        bool tokenMissing =
            MapStyle != MapStyle.Blank &&
            string.IsNullOrWhiteSpace(MapServiceToken);
        StopMissingAzureTokenTimer();
        _azureAuthenticationInfoBar.IsOpen = false;
        if (!tokenMissing || !IsLoaded)
        {
            return;
        }

        SetAzureAuthenticationInfoBarContent(
            InfoBarSeverity.Warning,
            MapControlResources.GetString("AzureTokenRequiredTitle"),
            MapControlResources.GetString("AzureTokenRequiredMessage"));
        _missingAzureTokenTimer ??= CreateMissingAzureTokenTimer();
        _missingAzureTokenTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer
        CreateMissingAzureTokenTimer()
    {
        Microsoft.UI.Dispatching.DispatcherQueueTimer timer =
            DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(500);
        timer.IsRepeating = false;
        timer.Tick += OnMissingAzureTokenTimerTick;
        return timer;
    }

    private void OnMissingAzureTokenTimerTick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        if (_azureAuthenticationInfoBar is not null &&
            IsLoaded &&
            MapStyle != MapStyle.Blank &&
            string.IsNullOrWhiteSpace(MapServiceToken))
        {
            ShowAzureAuthenticationInfoBar(
                InfoBarSeverity.Warning,
                MapControlResources.GetString("AzureTokenRequiredTitle"),
                MapControlResources.GetString("AzureTokenRequiredMessage"));
        }
    }

    private void ShowAzureAuthenticationInfoBar(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        SetAzureAuthenticationInfoBarContent(severity, title, message);
        _azureAuthenticationInfoBar!.IsOpen = true;
    }

    private void SetAzureAuthenticationInfoBarContent(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        _azureAuthenticationInfoBar!.Severity = severity;
        _azureAuthenticationInfoBar.Title = title;
        _azureAuthenticationInfoBar.Message = message;
        AutomationProperties.SetName(
            _azureAuthenticationInfoBar,
            $"{title}. {message}");
    }

    private void StopMissingAzureTokenTimer()
    {
        if (_missingAzureTokenTimer is not
            Microsoft.UI.Dispatching.DispatcherQueueTimer timer)
        {
            return;
        }

        timer.Stop();
        timer.Tick -= OnMissingAzureTokenTimerTick;
        _missingAzureTokenTimer = null;
    }

    private sealed class WeakXamlRootChangedSubscription(MapControl owner)
    {
        private readonly WeakReference<MapControl> _owner = new(owner);
        private WeakReference<XamlRoot>? _root;
        private WeakReference<FrameworkElement>? _content;

        internal void Attach(XamlRoot root)
        {
            Detach();
            _root = new WeakReference<XamlRoot>(root);
            root.Changed += OnChanged;
            AttachContent(root.Content as FrameworkElement);
        }

        internal void Detach()
        {
            if (_root?.TryGetTarget(out XamlRoot? root) == true)
            {
                root.Changed -= OnChanged;
            }
            if (_content?.TryGetTarget(out FrameworkElement? content) == true)
            {
                content.Unloaded -= OnContentUnloaded;
            }
            _root = null;
            _content = null;
        }

        private void OnChanged(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            if (_owner.TryGetTarget(out MapControl? control))
            {
                AttachContent(sender.Content as FrameworkElement);
                control.OnIconXamlRootChanged(sender, args);
            }
            else
            {
                Detach();
            }
        }

        private void AttachContent(FrameworkElement? content)
        {
            if (_content?.TryGetTarget(out FrameworkElement? current) == true)
            {
                if (ReferenceEquals(current, content))
                {
                    return;
                }
                current.Unloaded -= OnContentUnloaded;
            }
            _content = null;
            if (content is not null)
            {
                _content = new WeakReference<FrameworkElement>(content);
                content.Unloaded += OnContentUnloaded;
            }
        }

        private void OnContentUnloaded(object sender, RoutedEventArgs args)
        {
            if (_owner.TryGetTarget(out MapControl? control))
            {
                control.OnIconXamlRootContentUnloaded();
            }
            else
            {
                Detach();
            }
        }
    }
}
