using MapSample.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Devices.Geolocation;
using WinUIEx.Maps;
using MapControl = WinUIEx.Maps.MapControl;
using MapElementsLayer = WinUIEx.Maps.MapElementsLayer;
using MapIcon = WinUIEx.Maps.MapIcon;

namespace MapSample.Samples.Performance;

internal static class PerformanceMapFactory
{
    internal static MapControl Create()
    {
        MapControl map = new()
        {
            Center = new Geopoint(new BasicGeoposition
            {
                Longitude = -122.33,
                Latitude = 47.61,
            }),
            MapServiceToken = MapServiceTokenStore.Current,
            MapStyle = MapStyle.Road,
            ZoomLevel = 10,
        };

        FontIcon icon = new()
        {
            FontSize = 28,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            Glyph = "\uE707",
        };
        MapElementsLayer elements = new();
        elements.MapElements.Add(new MapIcon(icon, map.Center));
        map.Layers.Add(elements);
        return map;
    }
}
