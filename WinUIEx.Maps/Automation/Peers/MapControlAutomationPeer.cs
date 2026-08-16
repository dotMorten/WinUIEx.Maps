using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using WinUIEx.Maps.Rendering;
using Windows.Devices.Geolocation;

namespace WinUIEx.Maps.Automation.Peers;

/// <summary>
/// Exposes <see cref="MapControl"/> to Microsoft UI Automation clients.
/// </summary>
/// <remarks>
/// This peer implements the same Scroll, Transform, and Transform2 provider
/// patterns as the UWP <c>MapControlAutomationPeer</c>. Provider operations
/// update the map immediately without camera animation.
/// </remarks>
public sealed class MapControlAutomationPeer :
    FrameworkElementAutomationPeer,
    IScrollProvider,
    ITransformProvider,
    ITransformProvider2
{
    private const double SmallScrollFraction = 0.1;
    private const double LargeScrollFraction = 1;
    private const double SmallZoomIncrement = 1;
    private const double LargeZoomIncrement = 5;
    private AutomationState _lastAutomationState;

    /// <summary>
    /// Initializes a new instance of the <see cref="MapControlAutomationPeer"/> class.
    /// </summary>
    /// <param name="owner">The map control exposed by this peer.</param>
    public MapControlAutomationPeer(MapControl owner)
        : base(owner)
    {
        _lastAutomationState = GetAutomationState();
    }

    private MapControl MapOwner => (MapControl)Owner;

    /// <summary>
    /// Gets a value indicating whether the associated map can scroll horizontally.
    /// </summary>
    public bool HorizontallyScrollable =>
        GetAutomationState().HorizontallyScrollable;

    /// <summary>
    /// Gets the horizontal scroll position of the associated map as a percentage.
    /// </summary>
    public double HorizontalScrollPercent =>
        GetAutomationState().HorizontalScrollPercent;

    /// <summary>
    /// Gets the horizontal size of the associated map viewport as a percentage.
    /// </summary>
    public double HorizontalViewSize =>
        GetAutomationState().HorizontalViewSize;

    /// <summary>
    /// Gets a value indicating whether the associated map can scroll vertically.
    /// </summary>
    public bool VerticallyScrollable =>
        GetAutomationState().VerticallyScrollable;

    /// <summary>
    /// Gets the vertical scroll position of the associated map as a percentage.
    /// </summary>
    public double VerticalScrollPercent =>
        GetAutomationState().VerticalScrollPercent;

    /// <summary>
    /// Gets the vertical size of the associated map viewport as a percentage.
    /// </summary>
    public double VerticalViewSize =>
        GetAutomationState().VerticalViewSize;

    /// <summary>
    /// Gets a value indicating whether the associated map can be repositioned.
    /// </summary>
    public bool CanMove => true;

    /// <summary>
    /// Gets a value indicating whether the associated map can be resized.
    /// </summary>
    public bool CanResize => true;

    /// <summary>
    /// Gets a value indicating whether the associated map can be rotated.
    /// </summary>
    public bool CanRotate => true;

    /// <summary>
    /// Gets a value indicating whether the associated map can be zoomed.
    /// </summary>
    public bool CanZoom => true;

    /// <summary>
    /// Gets the maximum zoom level supported by the associated map.
    /// </summary>
    public double MaxZoom => MapCamera.MaximumTileZoom;

    /// <summary>
    /// Gets the minimum zoom level supported by the associated map.
    /// </summary>
    public double MinZoom => 0;

    /// <summary>
    /// Gets the current displayed zoom level of the associated map.
    /// </summary>
    public double ZoomLevel => GetAutomationState().ZoomLevel;

    /// <summary>
    /// Scrolls the associated map by the specified horizontal and vertical amounts.
    /// </summary>
    /// <param name="horizontalAmount">The amount to scroll horizontally.</param>
    /// <param name="verticalAmount">The amount to scroll vertically.</param>
    public void Scroll(
        ScrollAmount horizontalAmount,
        ScrollAmount verticalAmount)
    {
        EnsureEnabled();
        double width = GetViewportWidth();
        double height = GetViewportHeight();
        double horizontalPixels =
            GetScrollFraction(horizontalAmount, nameof(horizontalAmount)) * width;
        double verticalPixels =
            GetScrollFraction(verticalAmount, nameof(verticalAmount)) * height;
        MapOwner.MoveAutomationView(-horizontalPixels, -verticalPixels);
    }

    /// <summary>
    /// Sets the percentage that the associated map is scrolled horizontally and
    /// vertically.
    /// </summary>
    /// <param name="horizontalPercent">The horizontal scroll percentage.</param>
    /// <param name="verticalPercent">The vertical scroll percentage.</param>
    public void SetScrollPercent(
        double horizontalPercent,
        double verticalPercent)
    {
        EnsureEnabled();
        ValidateScrollPercent(horizontalPercent, nameof(horizontalPercent));
        ValidateScrollPercent(verticalPercent, nameof(verticalPercent));

        AutomationState state = GetTargetAutomationState();
        double longitude = horizontalPercent == ScrollPatternIdentifiers.NoScroll
            ? state.Longitude
            : MapCamera.WorldXToLongitude(horizontalPercent / 100);
        double latitude = verticalPercent == ScrollPatternIdentifiers.NoScroll
            ? state.Latitude
            : MapCamera.WorldYToLatitude(verticalPercent / 100);
        MapOwner.SetAutomationView(
            new BasicGeoposition
            {
                Longitude = longitude,
                Latitude = latitude,
                Altitude = state.Altitude,
            },
            state.ZoomLevel,
            state.Heading);
    }

    /// <summary>
    /// Moves the associated map by the specified horizontal and vertical pixel amounts.
    /// </summary>
    /// <param name="x">The amount to move the map horizontally.</param>
    /// <param name="y">The amount to move the map vertically.</param>
    public void Move(double x, double y)
    {
        EnsureEnabled();
        ValidateFinite(x, nameof(x));
        ValidateFinite(y, nameof(y));
        MapOwner.MoveAutomationView(x, y);
    }

    /// <summary>
    /// Resizes the associated map to the specified width and height.
    /// </summary>
    /// <param name="width">The new width of the map.</param>
    /// <param name="height">The new height of the map.</param>
    public void Resize(double width, double height)
    {
        EnsureEnabled();
        ValidatePositive(width, nameof(width));
        ValidatePositive(height, nameof(height));
        MapOwner.Width = width;
        MapOwner.Height = height;
    }

    /// <summary>
    /// Rotates the associated map clockwise from its current camera position.
    /// </summary>
    /// <param name="degrees">The number of degrees to rotate.</param>
    public void Rotate(double degrees)
    {
        EnsureEnabled();
        ValidateFinite(degrees, nameof(degrees));
        AutomationState state = GetTargetAutomationState();
        MapOwner.SetAutomationView(
            new BasicGeoposition
            {
                Longitude = state.Longitude,
                Latitude = state.Latitude,
                Altitude = state.Altitude,
            },
            state.ZoomLevel,
            state.Heading + degrees);
    }

    /// <summary>
    /// Zooms the associated map to the specified zoom level.
    /// </summary>
    /// <param name="zoom">The zoom level to set.</param>
    public void Zoom(double zoom)
    {
        EnsureEnabled();
        ValidateFinite(zoom, nameof(zoom));
        AutomationState state = GetTargetAutomationState();
        MapOwner.SetAutomationView(
            new BasicGeoposition
            {
                Longitude = state.Longitude,
                Latitude = state.Latitude,
                Altitude = state.Altitude,
            },
            Math.Clamp(zoom, MinZoom, MaxZoom),
            state.Heading);
    }

    /// <summary>
    /// Zooms the associated map by the specified logical unit.
    /// </summary>
    /// <param name="zoomUnit">The logical unit by which to change the zoom.</param>
    public void ZoomByUnit(ZoomUnit zoomUnit)
    {
        double delta = zoomUnit switch
        {
            ZoomUnit.NoAmount => 0,
            ZoomUnit.SmallDecrement => -SmallZoomIncrement,
            ZoomUnit.LargeDecrement => -LargeZoomIncrement,
            ZoomUnit.SmallIncrement => SmallZoomIncrement,
            ZoomUnit.LargeIncrement => LargeZoomIncrement,
            _ => throw new ArgumentOutOfRangeException(nameof(zoomUnit)),
        };
        Zoom(GetTargetAutomationState().ZoomLevel + delta);
    }

    /// <inheritdoc />
    protected override object? GetPatternCore(PatternInterface patternInterface)
    {
        return patternInterface is PatternInterface.Scroll or
            PatternInterface.Transform or
            PatternInterface.Transform2
                ? this
                : base.GetPatternCore(patternInterface);
    }

    /// <inheritdoc />
    protected override string GetClassNameCore() => nameof(MapControl);

    /// <inheritdoc />
    protected override string GetNameCore()
    {
        string name = base.GetNameCore();
        return string.IsNullOrWhiteSpace(name) ? "Map" : name;
    }

    /// <inheritdoc />
    protected override string GetHelpTextCore()
    {
        string helpText = base.GetHelpTextCore();
        return string.IsNullOrWhiteSpace(helpText)
            ? "Use arrow keys to pan and plus and minus to zoom."
            : helpText;
    }

    /// <inheritdoc />
    protected override string GetFullDescriptionCore()
    {
        string description = base.GetFullDescriptionCore();
        if (!string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        description = MapOwner.GetAccessibilityDescription();
        return string.IsNullOrWhiteSpace(description)
            ? "Interactive map."
            : description;
    }

    internal void NotifyDisplayedCameraChanged()
    {
        AutomationState current = GetAutomationState();
        AutomationState previous = _lastAutomationState;
        _lastAutomationState = current;
        if (!ListenerExists(AutomationEvents.PropertyChanged))
        {
            return;
        }

        RaiseIfChanged(
            ScrollPatternIdentifiers.HorizontallyScrollableProperty,
            previous.HorizontallyScrollable,
            current.HorizontallyScrollable);
        RaiseIfChanged(
            ScrollPatternIdentifiers.HorizontalScrollPercentProperty,
            previous.HorizontalScrollPercent,
            current.HorizontalScrollPercent);
        RaiseIfChanged(
            ScrollPatternIdentifiers.HorizontalViewSizeProperty,
            previous.HorizontalViewSize,
            current.HorizontalViewSize);
        RaiseIfChanged(
            ScrollPatternIdentifiers.VerticallyScrollableProperty,
            previous.VerticallyScrollable,
            current.VerticallyScrollable);
        RaiseIfChanged(
            ScrollPatternIdentifiers.VerticalScrollPercentProperty,
            previous.VerticalScrollPercent,
            current.VerticalScrollPercent);
        RaiseIfChanged(
            ScrollPatternIdentifiers.VerticalViewSizeProperty,
            previous.VerticalViewSize,
            current.VerticalViewSize);
        RaiseIfChanged(
            TransformPattern2Identifiers.ZoomLevelProperty,
            previous.ZoomLevel,
            current.ZoomLevel);
    }

    internal void NotifyAccessibilityDescriptionChanged(
        string previousDescription,
        string currentDescription)
    {
        if (!string.IsNullOrWhiteSpace(base.GetFullDescriptionCore()) ||
            !ListenerExists(AutomationEvents.PropertyChanged) &&
            !ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            return;
        }

        string previousValue = string.IsNullOrWhiteSpace(previousDescription)
            ? "Interactive map."
            : previousDescription;
        string currentValue = string.IsNullOrWhiteSpace(currentDescription)
            ? "Interactive map."
            : currentDescription;
        if (!string.Equals(
                previousValue,
                currentValue,
                StringComparison.Ordinal))
        {
            RaisePropertyChangedEvent(
                AutomationElementIdentifiers.FullDescriptionProperty,
                previousValue,
                currentValue);
            RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private AutomationState GetAutomationState()
    {
        AutomationState target = GetTargetAutomationState();
        double longitude = target.Longitude;
        double latitude = target.Latitude;
        double zoom = target.ZoomLevel;
        double heading = target.Heading;
        double pitch = MapOwner.Pitch;
        if (MapOwner.TryGetDisplayedCamera(
            out BasicGeoposition displayedCenter,
            out double displayedZoom,
            out double displayedHeading,
            out double displayedPitch))
        {
            longitude = displayedCenter.Longitude;
            latitude = displayedCenter.Latitude;
            zoom = displayedZoom;
            heading = displayedHeading;
            pitch = displayedPitch;
        }

        double width = GetViewportWidth();
        double height = GetViewportHeight();
        GetViewSizePercentages(
            zoom,
            width,
            height,
            heading,
            pitch,
            out double horizontalViewSize,
            out double verticalViewSize);
        bool horizontallyScrollable = horizontalViewSize < 100;
        bool verticallyScrollable = verticalViewSize < 100;
        return new AutomationState(
            longitude,
            latitude,
            target.Altitude,
            zoom,
            heading,
            horizontallyScrollable,
            horizontallyScrollable
                ? MapCamera.LongitudeToWorldX(longitude) * 100
                : ScrollPatternIdentifiers.NoScroll,
            horizontalViewSize,
            verticallyScrollable,
            verticallyScrollable
                ? MapCamera.LatitudeToWorldY(latitude) * 100
                : ScrollPatternIdentifiers.NoScroll,
            verticalViewSize);
    }

    private AutomationState GetTargetAutomationState()
    {
        BasicGeoposition center =
            MapOwner.Center?.Position ?? new BasicGeoposition();
        return new AutomationState(
            center.Longitude,
            center.Latitude,
            center.Altitude,
            MapOwner.ZoomLevel,
            MapOwner.Heading,
            false,
            0,
            0,
            false,
            0,
            0);
    }

    internal static void GetViewSizePercentages(
        double zoom,
        double width,
        double height,
        double heading,
        double pitch,
        out double horizontal,
        out double vertical)
    {
        if (!double.IsFinite(width) ||
            !double.IsFinite(height) ||
            width <= 0 ||
            height <= 0)
        {
            horizontal = 100;
            vertical = 100;
            return;
        }

        MapCamera.GetMapPlaneViewportBounds(
            width,
            height,
            heading,
            pitch,
            out double minimumX,
            out double minimumY,
            out double maximumX,
            out double maximumY);
        double worldSize = MapCamera.TileSize *
            Math.Pow(2, Math.Clamp(zoom, 0, MapCamera.MaximumTileZoom));
        horizontal = Math.Clamp((maximumX - minimumX) / worldSize * 100, 0, 100);
        vertical = Math.Clamp((maximumY - minimumY) / worldSize * 100, 0, 100);
    }

    private double GetViewportWidth() =>
        Math.Max(1, MapOwner.ActualWidth);

    private double GetViewportHeight() =>
        Math.Max(1, MapOwner.ActualHeight);

    private void EnsureEnabled()
    {
        if (!MapOwner.IsEnabled)
        {
            throw new InvalidOperationException(
                "The associated map control is disabled.");
        }
    }

    private static double GetScrollFraction(
        ScrollAmount amount,
        string parameterName) =>
        amount switch
        {
            ScrollAmount.NoAmount => 0,
            ScrollAmount.SmallDecrement => -SmallScrollFraction,
            ScrollAmount.LargeDecrement => -LargeScrollFraction,
            ScrollAmount.SmallIncrement => SmallScrollFraction,
            ScrollAmount.LargeIncrement => LargeScrollFraction,
            _ => throw new ArgumentOutOfRangeException(parameterName),
        };

    private static void ValidateScrollPercent(double value, string parameterName)
    {
        if (value != ScrollPatternIdentifiers.NoScroll &&
            (!double.IsFinite(value) || value < 0 || value > 100))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidatePositive(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private void RaiseIfChanged(
        AutomationProperty property,
        bool oldValue,
        bool newValue)
    {
        if (oldValue != newValue)
        {
            RaisePropertyChangedEvent(property, oldValue, newValue);
        }
    }

    private void RaiseIfChanged(
        AutomationProperty property,
        double oldValue,
        double newValue)
    {
        if (oldValue != newValue)
        {
            RaisePropertyChangedEvent(property, oldValue, newValue);
        }
    }

    private readonly record struct AutomationState(
        double Longitude,
        double Latitude,
        double Altitude,
        double ZoomLevel,
        double Heading,
        bool HorizontallyScrollable,
        double HorizontalScrollPercent,
        double HorizontalViewSize,
        bool VerticallyScrollable,
        double VerticalScrollPercent,
        double VerticalViewSize);
}
