using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Devices.Geolocation;
using WinUIEx.Maps;

namespace MapSample.Samples.Maps;

public sealed partial class OpenStreetMapPage : Page
{
    public OpenStreetMapPage()
    {
        InitializeComponent();
        Map.Center = new Geopoint(new BasicGeoposition
        {
            Longitude = -122.33,
            Latitude = 47.61,
        });
        Map.ZoomLevel = 10;
        Map.Layers.Add(new TileLayer(
            new TileLayerOptions
            {
                TileUrl = "https://tile.openstreetmap.org/[level]/[column]/[row].png",
                TileSize = 256,
                MaxSourceZoom = 19,
            },
            "openstreetmap-sample")
        {
            Attribution = "© OpenStreetMap contributors",
            AttributionLink = new Uri("https://www.openstreetmap.org/copyright"),
        });
    }
}
