using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Maps;
using Microsoft.Maui.Maps.Handlers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using HoldingState = Microsoft.UI.Input.HoldingState;
using MauiLocation = Microsoft.Maui.Devices.Sensors.Location;

namespace WinUIEx.Maps.Maui;

/// <summary>Adapts the MAUI map contract to a native Direct3D-backed Windows map.</summary>
public partial class WinUIExMapHandler : ViewHandler<IMap, MapControl>, IMapHandler
{
    public static readonly IPropertyMapper<IMap, WinUIExMapHandler> Mapper =
        new PropertyMapper<IMap, WinUIExMapHandler>(ViewMapper)
        {
            [nameof(IMap.MapType)] = (h, m) => h.UpdateMapType(),
            [nameof(IMap.IsScrollEnabled)] = (h, m) => h.PlatformView.IsPanEnabled = m.IsScrollEnabled,
            [nameof(IMap.IsZoomEnabled)] = (h, m) => h.PlatformView.IsZoomEnabled = m.IsZoomEnabled,
            [nameof(IMap.IsTrafficEnabled)] = (h, m) => h._traffic.IsVisible = m.IsTrafficEnabled,
            [nameof(IMap.IsShowingUser)] = (h, m) => h.UpdateUserLocation(),
            [nameof(IMap.IsClusteringEnabled)] = (h, m) => h.UpdatePins(),
            ["MapStyle"] = (h, m) => h.UpdateMapStyle(),
            [nameof(IMap.Pins)] = (h, m) => h.UpdatePins(),
            [nameof(IMap.Elements)] = (h, m) => h.UpdateElements(),
        };

    public static readonly CommandMapper<IMap, WinUIExMapHandler> Commands =
        new(ViewCommandMapper)
        {
            [nameof(IMap.MoveToRegion)] = (h, m, arg) => h.MoveToRegion(arg),
            [nameof(IMap.ShowInfoWindow)] = (h, m, arg) =>
            {
                if (arg is IMapPin pin) h.ShowInfoWindow(pin);
            },
            [nameof(IMap.HideInfoWindow)] = (h, m, arg) =>
            {
                if (arg is IMapPin pin && ReferenceEquals(pin, h._infoPin)) h.HideInfoWindow();
            },
            [nameof(IMapHandler.UpdateMapElement)] = (h, m, arg) => h.UpdateElements(),
        };

    private readonly AzureTrafficLayer _traffic = new() { IsVisible = false };
    private readonly MapElementsLayer _shapes = new();
    private readonly MapElementsLayer _pins = new();
    private readonly MapElementsLayer _user = new();
    private bool _connected;
    private long _centerCallback;
    private long _zoomCallback;
    private long _headingCallback;
    private long _pitchCallback;
    private MoveToRegionRequest? _pendingRegion;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _cameraTimer;

    public WinUIExMapHandler() : base(Mapper, Commands) { }

    IMap IMapHandler.VirtualView => VirtualView;
    FrameworkElement IMapHandler.PlatformView => PlatformView;

    protected override MapControl CreatePlatformView() => new()
    {
        MapServiceToken = MauiContext!.Services
            .GetRequiredService<MauiAppBuilderExtensions.MapConfiguration>().Token,
    };

    protected override void ConnectHandler(MapControl platformView)
    {
        base.ConnectHandler(platformView);
        _connected = true;
        platformView.Layers.Add(_traffic);
        platformView.Layers.Add(_shapes);
        platformView.Layers.Add(_pins);
        platformView.Layers.Add(_user);
        _pins.Tapped += OnPinTapped;
        _shapes.Tapped += OnShapeTapped;
        platformView.Tapped += OnMapTapped;
        platformView.Holding += OnMapHolding;
        platformView.RightTapped += OnMapRightTapped;
        platformView.Loaded += OnLoaded;
        platformView.Unloaded += OnUnloaded;
        platformView.SizeChanged += OnSizeChanged;
        platformView.ViewChanged += OnDisplayedViewChanged;
        _centerCallback = platformView.RegisterPropertyChangedCallback(MapControl.CenterProperty, OnCameraChanged);
        _zoomCallback = platformView.RegisterPropertyChangedCallback(MapControl.ZoomLevelProperty, OnCameraChanged);
        _headingCallback = platformView.RegisterPropertyChangedCallback(MapControl.HeadingProperty, OnCameraChanged);
        _pitchCallback = platformView.RegisterPropertyChangedCallback(MapControl.PitchProperty, OnCameraChanged);
        _cameraTimer = platformView.DispatcherQueue.CreateTimer();
        _cameraTimer.Interval = TimeSpan.FromMilliseconds(100);
        _cameraTimer.IsRepeating = false;
        _cameraTimer.Tick += OnCameraSettled;
    }

    protected override void DisconnectHandler(MapControl platformView)
    {
        _connected = false;
        StopLocation();
        HideInfoWindow();
        ClearPins();
        ClearElements();
        if (_cameraTimer is not null)
        {
            _cameraTimer.Stop();
            _cameraTimer.Tick -= OnCameraSettled;
            _cameraTimer = null;
        }
        platformView.UnregisterPropertyChangedCallback(MapControl.CenterProperty, _centerCallback);
        platformView.UnregisterPropertyChangedCallback(MapControl.ZoomLevelProperty, _zoomCallback);
        platformView.UnregisterPropertyChangedCallback(MapControl.HeadingProperty, _headingCallback);
        platformView.UnregisterPropertyChangedCallback(MapControl.PitchProperty, _pitchCallback);
        platformView.Tapped -= OnMapTapped;
        platformView.Holding -= OnMapHolding;
        platformView.RightTapped -= OnMapRightTapped;
        platformView.Loaded -= OnLoaded;
        platformView.Unloaded -= OnUnloaded;
        platformView.SizeChanged -= OnSizeChanged;
        platformView.ViewChanged -= OnDisplayedViewChanged;
        _pins.Tapped -= OnPinTapped;
        _shapes.Tapped -= OnShapeTapped;
        platformView.Layers.Clear();
        _user.MapElements.Clear();
        _pendingRegion = null;
        base.DisconnectHandler(platformView);
    }

    private void UpdateMapType() => PlatformView.MapStyle = VirtualView.MapType switch
    {
        MapType.Satellite => MapStyle.Satellite,
        MapType.Hybrid => MapStyle.SatelliteWithRoads,
        _ => MapStyle.Road,
    };

    private void UpdateMapStyle()
    {
        // MAUI documents Google Maps JSON styling as Android-only.
        if (VirtualView is Microsoft.Maui.Controls.BindableObject map &&
            map.GetValue(Microsoft.Maui.Controls.Maps.Map.MapStyleProperty) is not null)
            throw new NotSupportedException("Google Maps JSON styles are Android-only. Use MapType on Windows.");
    }

    public void UpdateMapElement(IMapElement element) => UpdateElements();

    private async void MoveToRegion(object? arg)
    {
        var request = arg switch
        {
            MoveToRegionRequest value => value,
            MapSpan span => new MoveToRegionRequest(span, true),
            _ => null,
        };
        if (request?.Region is not { } region) return;
        _pendingRegion = request;
        if (PlatformView.ActualWidth <= 0 || PlatformView.ActualHeight <= 0) return;
        _pendingRegion = null;
        double north = Math.Min(85.05112878, region.Center.Latitude + region.LatitudeDegrees / 2);
        double south = Math.Max(-85.05112878, region.Center.Latitude - region.LatitudeDegrees / 2);
        double latitude = Math.Clamp(region.Center.Latitude, -85.05112878, 85.05112878);
        double ySpan = Math.Abs(MapMath.WorldY(north) - MapMath.WorldY(south));
        double zoom = Math.Min(
            Math.Log2(PlatformView.ActualWidth / (256 * Math.Max(region.LongitudeDegrees / 360, 1e-10))),
            Math.Log2(PlatformView.ActualHeight / (256 * Math.Max(ySpan, 1e-10))));
        await PlatformView.TrySetViewAsync(
            ToGeopoint(new MauiLocation(latitude, region.Center.Longitude)), zoom, 0, 0,
            request.Animated ? MapAnimationKind.Default : MapAnimationKind.None);
        if (_connected) UpdateVisibleRegion();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_pendingRegion is not null) MoveToRegion(_pendingRegion);
        UpdateVisibleRegion();
        UpdateUserLocation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        StopLocation();
        HideInfoWindow();
        _cameraTimer?.Stop();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (_pendingRegion is not null) MoveToRegion(_pendingRegion);
        QueueCameraUpdate();
    }

    private void OnCameraChanged(DependencyObject sender, DependencyProperty property) => QueueCameraUpdate();

    private void OnDisplayedViewChanged(object? sender, EventArgs args) => UpdateVisibleRegion();

    private void QueueCameraUpdate()
    {
        HideInfoWindow();
        _cameraTimer?.Start();
    }

    private void OnCameraSettled(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        UpdateVisibleRegion();
        if (VirtualView.IsClusteringEnabled && _clusterZoom != PlatformView.ZoomLevel) UpdatePins();
    }

    private void UpdateVisibleRegion()
    {
        if (!_connected || !PlatformView.TryGetLocationFromOffset(
                new Point(PlatformView.ActualWidth / 2, PlatformView.ActualHeight / 2), out var center))
            return;
        var corners = new[]
        {
            new Point(0, 0), new Point(PlatformView.ActualWidth, 0),
            new Point(0, PlatformView.ActualHeight), new Point(PlatformView.ActualWidth, PlatformView.ActualHeight),
        };
        var locations = corners
            .Select(p => PlatformView.TryGetLocationFromOffset(p, out var location) ? location : null)
            .Where(p => p is not null).Select(p => p!.Position).ToList();
        if (locations.Count == 0) return;
        double latitudeSpan = 2 * locations.Max(p => Math.Abs(p.Latitude - center.Position.Latitude));
        double longitudeSpan = 2 * locations.Max(p => Math.Abs(MapMath.WrapLongitude(p.Longitude - center.Position.Longitude)));
        VirtualView.VisibleRegion = new MapSpan(ToLocation(center.Position), latitudeSpan, longitudeSpan);
    }

    private void OnMapTapped(object sender, TappedRoutedEventArgs args)
    {
        if (!args.Handled && PlatformView.TryGetLocationFromOffset(args.GetPosition(PlatformView), out var point))
        {
            HideInfoWindow();
            VirtualView.Clicked(ToLocation(point.Position));
        }
    }

    private void OnMapHolding(object sender, HoldingRoutedEventArgs args)
    {
        if (args.HoldingState == HoldingState.Started &&
            PlatformView.TryGetLocationFromOffset(args.GetPosition(PlatformView), out var point))
        {
            VirtualView.LongClicked(ToLocation(point.Position));
            args.Handled = true;
        }
    }

    private void OnMapRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        // Touch holds already report LongClicked through Holding.
        if (args.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse &&
            PlatformView.TryGetLocationFromOffset(args.GetPosition(PlatformView), out var point))
        {
            VirtualView.LongClicked(ToLocation(point.Position));
            args.Handled = true;
        }
    }

    private static Geopoint ToGeopoint(MauiLocation location) => new(new BasicGeoposition
    {
        Latitude = location.Latitude, Longitude = location.Longitude, Altitude = location.Altitude ?? 0,
    });

    private static MauiLocation ToLocation(BasicGeoposition point) => new(point.Latitude, point.Longitude, point.Altitude);
}
