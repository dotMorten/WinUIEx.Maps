using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using Windows.Devices.Geolocation;
using WinUIEx.Maps;

namespace MapSample.Samples.Interaction;

public sealed partial class TrySetViewPage : Page
{
    private long _viewRequestVersion;

    public TrySetViewPage()
    {
        InitializeComponent();
        PresetPicker.ItemsSource = new List<ViewPreset>
        {
            new("Seattle, United States", -122.3321, 47.6062, 12),
            new("New York City, United States", -74.0060, 40.7128, 12),
            new("Rio de Janeiro, Brazil", -43.1729, -22.9068, 11),
            new("London, United Kingdom", -0.1276, 51.5072, 12),
            new("Paris, France", 2.3522, 48.8566, 12),
            new("Cairo, Egypt", 31.2357, 30.0444, 11),
            new("Cape Town, South Africa", 18.4241, -33.9249, 11),
            new("Tokyo, Japan", 139.6917, 35.6895, 12),
            new("Sydney, Australia", 151.2093, -33.8688, 12),
        };
        AnimationPicker.ItemsSource = new List<MapAnimationKind>
        {
            MapAnimationKind.Default,
            MapAnimationKind.None,
            MapAnimationKind.Linear,
            MapAnimationKind.Bow,
        };
        PresetPicker.SelectedIndex = 0;
        AnimationPicker.SelectedItem = MapAnimationKind.Default;
        Map.Layers.Add(new TileLayer(
            new TileLayerOptions
            {
                TileUrl = "https://tile.openstreetmap.org/[level]/[column]/[row].png",
                TileSize = 256,
                MaxSourceZoom = 19,
            },
            "openstreetmap-view-sample")
        {
            Attribution = "© OpenStreetMap contributors",
            AttributionLink = new Uri("https://www.openstreetmap.org/copyright"),
        });
    }

    private async void TrySetView_Click(object sender, RoutedEventArgs e)
    {
        if (PresetPicker.SelectedItem is not ViewPreset preset ||
            AnimationPicker.SelectedItem is not MapAnimationKind animation)
        {
            return;
        }

        long requestVersion = ++_viewRequestVersion;
        ResultText.Text = "animating";
        bool displayed = await Map.TrySetViewAsync(
            preset.Center,
            preset.ZoomLevel,
            HeadingInput.Value,
            PitchInput.Value,
            animation);

        if (requestVersion == _viewRequestVersion)
        {
            ResultText.Text = displayed ? "true" : "false";
        }
    }
}

public sealed class ViewPreset
{
    public ViewPreset(string name, double longitude, double latitude, double zoomLevel)
    {
        Name = name;
        Center = new Geopoint(new BasicGeoposition
        {
            Longitude = longitude,
            Latitude = latitude,
        });
        ZoomLevel = zoomLevel;
    }

    public string Name { get; }

    public Geopoint Center { get; }

    public double ZoomLevel { get; }
}
