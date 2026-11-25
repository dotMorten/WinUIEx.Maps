using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using MauiLocation = Microsoft.Maui.Devices.Sensors.Location;

namespace WinUIEx.Maps.Maui;

public partial class WinUIExMapHandler
{
    private Geolocator? _locator;
    private volatile Microsoft.UI.Dispatching.DispatcherQueue? _locationDispatcher;
    private int _locationVersion;

    private void StopLocation()
    {
        _locationVersion++;
        _locationDispatcher = null;
        if (_locator is not null)
        {
            _locator.PositionChanged -= OnPositionChanged;
            _locator.StatusChanged -= OnLocationStatusChanged;
            _locator = null;
        }
        _user.MapElements.Clear();
    }

    private async void UpdateUserLocation()
    {
        StopLocation();
        if (!_connected || !VirtualView.IsShowingUser || !PlatformView.IsLoaded) return;
        int version = _locationVersion;
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (version != _locationVersion || !_connected) return;
            if (access != GeolocationAccessStatus.Allowed)
            {
                ReportFailure("Location.AccessDenied", null);
                return;
            }
            _locationDispatcher = PlatformView.DispatcherQueue;
            _locator = new Geolocator { DesiredAccuracy = PositionAccuracy.High, MovementThreshold = 5 };
            _locator.PositionChanged += OnPositionChanged;
            _locator.StatusChanged += OnLocationStatusChanged;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (version == _locationVersion) StopLocation();
            ReportFailure("Location.Start", exception);
        }
    }

    private void OnPositionChanged(Geolocator sender, PositionChangedEventArgs args)
    {
        var coordinate = args.Position.Coordinate;
        _locationDispatcher?.TryEnqueue(() =>
        {
            if (!_connected || !ReferenceEquals(sender, _locator)) return;
            var position = coordinate.Point.Position;
            var location = new MauiLocation(position.Latitude, position.Longitude)
            {
                Accuracy = coordinate.Accuracy,
                Altitude = position.Altitude,
                Timestamp = coordinate.Timestamp,
                Speed = coordinate.Speed,
                Course = coordinate.Heading,
            };
            if (_user.MapElements.Count == 0)
            {
                _user.MapElements.Add(new MapIcon(new PathIcon
                {
                    Data = new EllipseGeometry { Center = new Point(8, 8), RadiusX = 8, RadiusY = 8 },
                    Width = 16, Height = 16,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
                }, coordinate.Point) { NormalizedAnchorPoint = new Point(0.5, 0.5) });
            }
            else
            {
                ((MapIcon)_user.MapElements[0]).Location = coordinate.Point;
            }
            VirtualView.UserLocationUpdated(location);
        });
    }

    private void OnLocationStatusChanged(Geolocator sender, StatusChangedEventArgs args)
    {
        if (args.Status is PositionStatus.Disabled or PositionStatus.NotAvailable or PositionStatus.NoData)
            _locationDispatcher?.TryEnqueue(() =>
            {
                if (!ReferenceEquals(sender, _locator)) return;
                _user.MapElements.Clear();
                ReportFailure("Location.Unavailable", null);
            });
    }
}
