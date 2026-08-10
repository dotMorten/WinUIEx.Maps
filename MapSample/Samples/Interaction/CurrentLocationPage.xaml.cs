using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;
using WinUIEx.Maps;
using MapElementsLayer = WinUIEx.Maps.MapElementsLayer;
using MapIcon = WinUIEx.Maps.MapIcon;

namespace MapSample.Samples.Interaction;

public sealed partial class CurrentLocationPage : Page
{
    private readonly MapElementsLayer _locationLayer = new();
    private readonly FontIcon _locationIcon = new()
    {
        FontSize = 32,
        Foreground = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
        Glyph = "\uE81D",
    };
    private Geolocator? _geolocator;
    private MapIcon? _currentLocation;
    private bool _started;
    private bool _isActive = true;

    public CurrentLocationPage()
    {
        InitializeComponent();
        Map.ZoomLevel = 15;
        Map.Layers.Add(new TileLayer(
            new TileLayerOptions
            {
                TileUrl = "https://tile.openstreetmap.org/[level]/[column]/[row].png",
                TileSize = 256,
                MaxSourceZoom = 19,
            },
            "openstreetmap-location-sample")
        {
            Attribution = "© OpenStreetMap contributors",
            AttributionLink = new Uri("https://www.openstreetmap.org/copyright"),
        });
        Map.Layers.Add(_locationLayer);
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started)
        {
            return;
        }
        _started = true;

        GeolocationAccessStatus access;
        try
        {
            access = await Geolocator.RequestAccessAsync();
        }
        catch (UnauthorizedAccessException)
        {
            if (_isActive)
            {
                ShowLocationError(
                    "Location access is disabled",
                    "Allow this app to use location in Windows Settings, then reopen the sample.");
            }
            return;
        }
        if (!_isActive)
        {
            return;
        }
        if (access != GeolocationAccessStatus.Allowed)
        {
            ShowLocationError(
                "Location access is disabled",
                "Allow this app to use location in Windows Settings, then reopen the sample.");
            return;
        }

        _geolocator = new Geolocator
        {
            DesiredAccuracy = PositionAccuracy.High,
            MovementThreshold = 5,
            ReportInterval = 1000,
        };
        _geolocator.PositionChanged += Geolocator_PositionChanged;
        _geolocator.StatusChanged += Geolocator_StatusChanged;
        LocationStatus.Title = "Finding your location";
        LocationStatus.Message = "Waiting for the first location update.";

        try
        {
            Geoposition position = await _geolocator.GetGeopositionAsync(
                maximumAge: TimeSpan.FromSeconds(5),
                timeout: TimeSpan.FromSeconds(15));
            UpdatePosition(position);
        }
        catch (UnauthorizedAccessException)
        {
            if (_isActive)
            {
                ShowLocationError(
                    "Location access is disabled",
                    "Allow this app to use location in Windows Settings, then reopen the sample.");
            }
        }
        catch (TaskCanceledException)
        {
            if (_isActive)
            {
                ShowLocationError(
                    "Location is unavailable",
                    "Windows did not provide a location before the request timed out.");
            }
        }
    }

    private void Geolocator_PositionChanged(
        Geolocator sender,
        PositionChangedEventArgs args)
    {
        if (!_isActive)
        {
            return;
        }
        DispatcherQueue.TryEnqueue(() => UpdatePosition(args.Position));
    }

    private void Geolocator_StatusChanged(
        Geolocator sender,
        StatusChangedEventArgs args)
    {
        if (!_isActive)
        {
            return;
        }
        DispatcherQueue.TryEnqueue(() => UpdateStatus(args.Status));
    }

    private void UpdatePosition(Geoposition position)
    {
        if (!_isActive)
        {
            return;
        }

        Geopoint location = position.Coordinate.Point;
        if (_currentLocation is null)
        {
            _currentLocation = new MapIcon(_locationIcon, location);
            _locationLayer.MapElements.Add(_currentLocation);
            Map.Center = location;
        }
        else
        {
            _currentLocation.Location = location;
        }

        LocationStatus.Severity = InfoBarSeverity.Success;
        LocationStatus.Title = "Location active";
        LocationStatus.Message =
            $"Accuracy: {position.Coordinate.Accuracy:F0} m. " +
            $"Updated: {position.Coordinate.Timestamp.ToLocalTime():T}.";
    }

    private void UpdateStatus(PositionStatus status)
    {
        switch (status)
        {
            case PositionStatus.Disabled:
                ShowLocationError(
                    "Location access is disabled",
                    "Allow this app to use location in Windows Settings, then reopen the sample.");
                break;
            case PositionStatus.NoData:
            case PositionStatus.NotAvailable:
                ShowLocationError(
                    "Location is unavailable",
                    "Windows cannot currently determine this device's location.");
                break;
            case PositionStatus.Initializing:
                LocationStatus.Severity = InfoBarSeverity.Informational;
                LocationStatus.Title = "Finding your location";
                LocationStatus.Message = "Waiting for a location update.";
                break;
        }
    }

    private void ShowLocationError(string title, string message)
    {
        LocationStatus.Severity = InfoBarSeverity.Error;
        LocationStatus.Title = title;
        LocationStatus.Message = message;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _isActive = false;
        if (_geolocator is not null)
        {
            _geolocator.PositionChanged -= Geolocator_PositionChanged;
            _geolocator.StatusChanged -= Geolocator_StatusChanged;
            _geolocator = null;
        }
        base.OnNavigatedFrom(e);
    }
}
