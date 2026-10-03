using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Devices.Geolocation;
using WinUIEx.Maps;

namespace MapSample.Samples.Maps;

public sealed partial class OpenStreetMapVectorTilesPage : Page
{
    public OpenStreetMapVectorTilesPage()
    {
        InitializeComponent();
        Map.Center = new Geopoint(new BasicGeoposition
        {
            Longitude = -122.33,
            Latitude = 47.61,
        });
        Map.ZoomLevel = 16;
        Map.Pitch = 55;
        Map.Heading = 20;
        Map.Layers.Add(new TileLayer(
            new TileLayerOptions
            {
                TileUrl = "https://tiles.openfreemap.org/planet/20260913_164504_pt/{z}/{x}/{y}.pbf",
                StyleUrl = "https://tiles.openfreemap.org/styles/liberty",
                TileSize = 512,
                MaxSourceZoom = 14,
            },
            "openstreetmap-vector-sample")
        {
            Attribution = "© OpenFreeMap · © OpenMapTiles · © OpenStreetMap contributors",
            AttributionLink = new Uri("https://openfreemap.org/"),
        });
    }
}
