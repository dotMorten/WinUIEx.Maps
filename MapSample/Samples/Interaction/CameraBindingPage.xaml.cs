using MapSample.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Devices.Geolocation;
using WinUIEx.Maps;

namespace MapSample.Samples.Interaction;

public sealed partial class CameraBindingPage : Page
{
    public CameraViewModel ViewModel { get; } = new();

    public CameraBindingPage()
    {
        InitializeComponent();
        string token = MapServiceTokenStore.Current;
        Map.MapServiceToken = token;
        Map.MapStyle = string.IsNullOrWhiteSpace(token) ? MapStyle.Blank : MapStyle.Road;
        TokenRequired.IsOpen = string.IsNullOrWhiteSpace(token);
    }
}

public sealed class CameraViewModel : ObservableObject
{
    private double _longitude = -122.33;
    private double _latitude = 47.61;
    private double _zoomLevel = 2;
    private double _heading;
    private double _pitch;
    private Geopoint _center;

    public CameraViewModel()
    {
        _center = CreateCenter();
    }

    public double Longitude
    {
        get => _longitude;
        set
        {
            double normalized = Math.Clamp(value, -180, 180);
            if (!SetProperty(ref _longitude, normalized))
            {
                return;
            }
            OnPropertyChanged(nameof(LongitudeText));
            UpdateCenter();
        }
    }

    public double Latitude
    {
        get => _latitude;
        set
        {
            double normalized = Math.Clamp(value, -85, 85);
            if (!SetProperty(ref _latitude, normalized))
            {
                return;
            }
            OnPropertyChanged(nameof(LatitudeText));
            UpdateCenter();
        }
    }

    public double ZoomLevel
    {
        get => _zoomLevel;
        set
        {
            double normalized = Math.Clamp(value, 0, 22);
            if (!SetProperty(ref _zoomLevel, normalized))
            {
                return;
            }
            OnPropertyChanged(nameof(ZoomText));
        }
    }

    public Geopoint Center
    {
        get => _center;
        set
        {
            BasicGeoposition position = value.Position;
            if (!SetProperty(ref _center, value))
            {
                return;
            }
            _longitude = position.Longitude;
            _latitude = position.Latitude;
            OnPropertyChanged(nameof(Longitude));
            OnPropertyChanged(nameof(Latitude));
            OnPropertyChanged(nameof(LongitudeText));
            OnPropertyChanged(nameof(LatitudeText));
        }
    }

    public double Heading
    {
        get => _heading;
        set
        {
            double normalized = ((value % 360) + 360) % 360;
            if (!SetProperty(ref _heading, normalized))
            {
                return;
            }
            OnPropertyChanged(nameof(HeadingText));
        }
    }

    public double Pitch
    {
        get => _pitch;
        set
        {
            double normalized = Math.Clamp(value, 0, 60);
            if (!SetProperty(ref _pitch, normalized))
            {
                return;
            }
            OnPropertyChanged(nameof(PitchText));
        }
    }

    public string LongitudeText => Longitude.ToString("F2");

    public string LatitudeText => Latitude.ToString("F2");

    public string ZoomText => ZoomLevel.ToString("F2");

    public string HeadingText => $"{Heading:F0}°";

    public string PitchText => $"{Pitch:F0}°";

    private void UpdateCenter()
    {
        _center = CreateCenter();
        OnPropertyChanged(nameof(Center));
    }

    private Geopoint CreateCenter() =>
        new(new BasicGeoposition
        {
            Longitude = _longitude,
            Latitude = _latitude,
        });

}
