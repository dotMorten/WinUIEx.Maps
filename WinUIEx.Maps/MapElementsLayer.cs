using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace WinUIEx.Maps;

/// <summary>
/// Represents a map layer containing lightweight <see cref="MapElement"/> objects.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MapElements"/> may be replaced, or its current collection may be mutated. Once
/// attached to a <see cref="MapControl"/>, both operations must occur on that control's UI
/// thread; replacement transfers observation from the old collection to the new one. The
/// layer's position in <see cref="MapControl.Layers"/> determines draw order: layers are
/// rendered first to last, with later layers above earlier layers.
/// </para>
/// <para>
/// Elements are lightweight objects rather than dependency objects and are suitable for
/// large sets. Prefer <see cref="MapElementCollection.AddRange"/> and
/// <see cref="MapElementCollection.RemoveRange"/> for bulk changes. Reuse one unparented
/// <see cref="Microsoft.UI.Xaml.Controls.IconElement"/> among multiple
/// <see cref="MapIcon"/> instances to share its raster and GPU texture. The built-in
/// element types are <see cref="MapIcon"/>, <see cref="MapPolygon"/>, and
/// <see cref="MapPolyline"/>.
/// </para>
/// <para>
/// Built-in element properties, including polygon path-list mutations, publish immutable
/// snapshots and may be updated from a worker thread; layer and element collection changes
/// and XAML icon creation or visual mutation remain UI-thread-only.
/// <see cref="MapLayer.IsVisible"/> suppresses the entire layer, and
/// <see cref="MapLayer.Opacity"/> multiplies the opacity of every rendered element.
/// <see cref="MapElement.IsVisible"/> controls individual rendering,
/// <see cref="MapElement.IsEnabled"/> controls individual input, and
/// <see cref="MapElement.ZIndex"/> changes ordering only within this layer.
/// </para>
/// <para>
/// Pointer and tap events report only the topmost visible element under the input position.
/// Hit testing is disabled when no layer attached to the map has a relevant event subscriber.
/// Map icons are narrowed through the renderer's spatial index before exact viewport bounds
/// are tested; polygons test fill and stroke, and polylines test stroke.
/// </para>
/// </remarks>
public sealed class MapElementsLayer : MapLayer
{
    private EventHandler<MapElementPointerEventArgs>? _pointerEntered;
    private EventHandler<MapElementPointerEventArgs>? _pointerExited;
    private EventHandler<MapElementPointerEventArgs>? _pointerMoved;
    private EventHandler<MapElementPointerEventArgs>? _pointerPressed;
    private EventHandler<MapElementPointerEventArgs>? _pointerReleased;
    private EventHandler<MapElementTappedEventArgs>? _tapped;
    private EventHandler<MapElementRightTappedEventArgs>? _rightTapped;
    private MapElementCollection _mapElements = null!;
    private bool _isMapElementsChangePrevalidated;
    private bool _isRestoringMapElements;

    /// <summary>
    /// Initializes an empty map-elements layer.
    /// </summary>
    public MapElementsLayer()
    {
        SetValue(MapElementsProperty, new MapElementCollection());
    }

    /// <summary>
    /// Gets or sets the observable collection of elements in this layer.
    /// </summary>
    /// <remarks>
    /// The collection cannot be <see langword="null"/>. Replacing it transfers collection
    /// observation from the old collection to the new collection. Replace or mutate an
    /// attached collection only on the owning map control's UI thread.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// The assigned collection is <see langword="null"/>.
    /// </exception>
    public MapElementCollection MapElements
    {
        get => (MapElementCollection)GetValue(MapElementsProperty);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_mapElements, value))
            {
                return;
            }

            MapElementsChanging?.Invoke(this, EventArgs.Empty);
            _isMapElementsChangePrevalidated = true;
            try
            {
                SetValue(MapElementsProperty, value);
            }
            finally
            {
                _isMapElementsChangePrevalidated = false;
            }
        }
    }

    /// <summary>
    /// Identifies the <see cref="MapElements"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty MapElementsProperty =
        DependencyProperty.Register(
            nameof(MapElements),
            typeof(MapElementCollection),
            typeof(MapElementsLayer),
            new PropertyMetadata(null, OnMapElementsPropertyChanged));

    /// <summary>Occurs when a pointer enters the topmost map element in this layer.</summary>
    public event EventHandler<MapElementPointerEventArgs> PointerEntered
    {
        add => AddHandler(ref _pointerEntered, value);
        remove => RemoveHandler(ref _pointerEntered, value);
    }

    /// <summary>Occurs when a pointer exits the topmost map element in this layer.</summary>
    public event EventHandler<MapElementPointerEventArgs> PointerExited
    {
        add => AddHandler(ref _pointerExited, value);
        remove => RemoveHandler(ref _pointerExited, value);
    }

    /// <summary>Occurs when a pointer moves over the topmost map element in this layer.</summary>
    public event EventHandler<MapElementPointerEventArgs> PointerMoved
    {
        add => AddHandler(ref _pointerMoved, value);
        remove => RemoveHandler(ref _pointerMoved, value);
    }

    /// <summary>Occurs when a pointer is pressed over the topmost map element in this layer.</summary>
    public event EventHandler<MapElementPointerEventArgs> PointerPressed
    {
        add => AddHandler(ref _pointerPressed, value);
        remove => RemoveHandler(ref _pointerPressed, value);
    }

    /// <summary>Occurs when a pointer is released over the topmost map element in this layer.</summary>
    public event EventHandler<MapElementPointerEventArgs> PointerReleased
    {
        add => AddHandler(ref _pointerReleased, value);
        remove => RemoveHandler(ref _pointerReleased, value);
    }

    /// <summary>Occurs when the topmost map element in this layer is tapped.</summary>
    public event EventHandler<MapElementTappedEventArgs> Tapped
    {
        add => AddHandler(ref _tapped, value);
        remove => RemoveHandler(ref _tapped, value);
    }

    /// <summary>Occurs when the topmost map element in this layer is right-tapped.</summary>
    public event EventHandler<MapElementRightTappedEventArgs> RightTapped
    {
        add => AddHandler(ref _rightTapped, value);
        remove => RemoveHandler(ref _rightTapped, value);
    }

    internal event EventHandler? MapElementsChanging;
    internal event EventHandler<MapElementsCollectionChangedEventArgs>? MapElementsChanged;
    internal event EventHandler? InputHandlersChanged;

    internal MapElementInputEventKind InputHandlers
    {
        get
        {
            MapElementInputEventKind handlers = MapElementInputEventKind.None;
            handlers |= _pointerEntered is null ? 0 : MapElementInputEventKind.PointerEntered;
            handlers |= _pointerExited is null ? 0 : MapElementInputEventKind.PointerExited;
            handlers |= _pointerMoved is null ? 0 : MapElementInputEventKind.PointerMoved;
            handlers |= _pointerPressed is null ? 0 : MapElementInputEventKind.PointerPressed;
            handlers |= _pointerReleased is null ? 0 : MapElementInputEventKind.PointerReleased;
            handlers |= _tapped is null ? 0 : MapElementInputEventKind.Tapped;
            handlers |= _rightTapped is null ? 0 : MapElementInputEventKind.RightTapped;
            return handlers;
        }
    }

    internal void RaisePointerEntered(MapElement element, PointerRoutedEventArgs args) =>
        _pointerEntered?.Invoke(this, new MapElementPointerEventArgs(element, args));

    internal void RaisePointerExited(MapElement element, PointerRoutedEventArgs args) =>
        _pointerExited?.Invoke(this, new MapElementPointerEventArgs(element, args));

    internal void RaisePointerMoved(MapElement element, PointerRoutedEventArgs args) =>
        _pointerMoved?.Invoke(this, new MapElementPointerEventArgs(element, args));

    internal void RaisePointerPressed(MapElement element, PointerRoutedEventArgs args) =>
        _pointerPressed?.Invoke(this, new MapElementPointerEventArgs(element, args));

    internal void RaisePointerReleased(MapElement element, PointerRoutedEventArgs args) =>
        _pointerReleased?.Invoke(this, new MapElementPointerEventArgs(element, args));

    internal void RaiseTapped(
        MapElement element,
        Windows.Devices.Geolocation.Geopoint location,
        TappedRoutedEventArgs args) =>
        _tapped?.Invoke(this, new MapElementTappedEventArgs(element, location, args));

    internal void RaiseRightTapped(
        MapElement element,
        Windows.Devices.Geolocation.Geopoint location,
        RightTappedRoutedEventArgs args) =>
        _rightTapped?.Invoke(
            this,
            new MapElementRightTappedEventArgs(element, location, args));

    private void AddHandler<T>(ref EventHandler<T>? handlers, EventHandler<T>? value)
        where T : EventArgs
    {
        if (value is null)
        {
            return;
        }

        MapElementInputEventKind previous = InputHandlers;
        handlers += value;
        if (previous != InputHandlers)
        {
            InputHandlersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RemoveHandler<T>(ref EventHandler<T>? handlers, EventHandler<T>? value)
        where T : EventArgs
    {
        if (value is null)
        {
            return;
        }

        MapElementInputEventKind previous = InputHandlers;
        handlers -= value;
        if (previous != InputHandlers)
        {
            InputHandlersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static void OnMapElementsPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        MapElementsLayer layer = (MapElementsLayer)dependencyObject;
        if (layer._isRestoringMapElements)
        {
            return;
        }

        if (args.NewValue is not MapElementCollection newCollection)
        {
            layer.RestoreMapElementsProperty();
            return;
        }
        if (ReferenceEquals(layer._mapElements, newCollection))
        {
            return;
        }

        MapElementCollection oldCollection = layer._mapElements;
        if (!layer._isMapElementsChangePrevalidated)
        {
            try
            {
                layer.MapElementsChanging?.Invoke(layer, EventArgs.Empty);
            }
            catch
            {
                layer.RestoreMapElementsProperty();
                throw;
            }
        }

        layer._mapElements = newCollection;
        layer.MapElementsChanged?.Invoke(
            layer,
            new MapElementsCollectionChangedEventArgs(oldCollection, newCollection));
    }

    private void RestoreMapElementsProperty()
    {
        MapElementCollection validElements = _mapElements ?? new MapElementCollection();
        _isRestoringMapElements = true;
        try
        {
            SetValue(MapElementsProperty, validElements);
        }
        finally
        {
            _isRestoringMapElements = false;
        }
    }
}

[Flags]
internal enum MapElementInputEventKind
{
    None = 0,
    PointerEntered = 1 << 0,
    PointerExited = 1 << 1,
    PointerMoved = 1 << 2,
    PointerPressed = 1 << 3,
    PointerReleased = 1 << 4,
    Tapped = 1 << 5,
    RightTapped = 1 << 6,
    PointerHover = PointerEntered | PointerExited | PointerMoved,
}

internal sealed class MapElementsCollectionChangedEventArgs(
    MapElementCollection oldCollection,
    MapElementCollection newCollection) : EventArgs
{
    public MapElementCollection OldCollection { get; } = oldCollection;

    public MapElementCollection NewCollection { get; } = newCollection;
}
