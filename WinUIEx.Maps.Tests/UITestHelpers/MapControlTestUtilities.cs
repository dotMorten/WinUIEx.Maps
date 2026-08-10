using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Devices.Geolocation;
using Windows.Foundation;

namespace WinUIEx.Maps.Tests.UITestHelpers;

internal static class MapControlTestUtilities
{
    internal const double CoordinateTolerance = 0.000000001;
    internal const double ZoomTolerance = 0.001;
    internal const double TileSize = 256;
    private static readonly TimeSpan InputTimeout = TimeSpan.FromSeconds(5);

    internal static async Task SetupMapAsync(MapControl map)
    {
        BasicGeoposition initialCenter = new()
        {
            Latitude = 10,
            Longitude = 20,
        };
        map.MapStyle = WinUIEx.Maps.MapStyle.Blank;
        map.ZoomLevel = 5;
        map.Center = new Geopoint(initialCenter);
        var pin = new FontIcon
        {
            Glyph = "\uE81D",
            FontSize = 28,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red),
        };
        var layer = new MapElementsLayer();
        layer.MapElements.Add(new MapIcon(pin, new Geopoint(initialCenter)));
        map.Layers.Add(layer);
        await WaitForDisplayedCameraAsync(map, initialCenter, map.ZoomLevel);
    }

    internal static BasicGeoposition PanByPixels(
        BasicGeoposition center,
        double zoom,
        double horizontalDelta,
        double verticalDelta)
    {
        double worldSize = TileSize * Math.Pow(2, zoom);
        double worldX = ((center.Longitude + 180) / 360) - (horizontalDelta / worldSize);
        double worldY = LatitudeToWorldY(center.Latitude) - (verticalDelta / worldSize);
        return new BasicGeoposition
        {
            Longitude = ((worldX - Math.Floor(worldX)) * 360) - 180,
            Latitude = WorldYToLatitude(worldY),
        };
    }

    internal static BasicGeoposition LocationAtOffset(
        BasicGeoposition center,
        double zoom,
        double viewportWidth,
        double viewportHeight,
        Point offset)
    {
        double worldSize = TileSize * Math.Pow(2, zoom);
        double worldX = ((center.Longitude + 180) / 360) +
            ((offset.X - (viewportWidth / 2)) / worldSize);
        double cameraY = LatitudeToWorldY(center.Latitude) * worldSize;
        double effectiveCameraY = Math.Clamp(cameraY, 0, worldSize);
        double worldY =
            (effectiveCameraY + offset.Y - (viewportHeight / 2)) / worldSize;
        return new BasicGeoposition
        {
            Longitude = ((worldX - Math.Floor(worldX)) * 360) - 180,
            Latitude = WorldYToLatitude(worldY),
        };
    }

    internal static Task WaitForDisplayedCameraAsync(
        MapControl map,
        BasicGeoposition expectedCenter,
        double expectedZoom) =>
        WaitForAsync(() =>
            TryGetDisplayedCamera(map, out BasicGeoposition center, out double zoom) &&
            Math.Abs(center.Longitude - expectedCenter.Longitude) <= CoordinateTolerance &&
            Math.Abs(center.Latitude - expectedCenter.Latitude) <= CoordinateTolerance &&
            Math.Abs(zoom - expectedZoom) <= ZoomTolerance);

    internal static Task WaitForDisplayedZoomAsync(
        MapControl map,
        double expectedZoom) =>
        WaitForAsync(() =>
            TryGetDisplayedCamera(map, out _, out double zoom) &&
            Math.Abs(zoom - expectedZoom) <= ZoomTolerance);

    internal static BasicGeoposition GetDisplayedLocation(
        MapControl map,
        Point offset)
    {
        if (!map.TryGetLocationFromOffset(offset, out Geopoint location))
        {
            throw new InvalidOperationException(
                $"The renderer could not resolve viewport point ({offset.X}, {offset.Y}).");
        }

        return location.Position;
    }

    internal static void AssertCoordinatesEqual(
        BasicGeoposition expected,
        BasicGeoposition actual,
        double zoom)
    {
        double worldSize = TileSize * Math.Pow(2, zoom);
        double longitudePixels =
            Math.Abs(expected.Longitude - actual.Longitude) * worldSize / 360;
        double latitudePixels =
            Math.Abs(LatitudeToWorldY(expected.Latitude) - LatitudeToWorldY(actual.Latitude)) *
            worldSize;
        const double pixelTolerance = 1;
        Assert.IsLessThanOrEqualTo(
            pixelTolerance,
            longitudePixels,
            $"Longitude anchor moved by {longitudePixels} pixels.");
        Assert.IsLessThanOrEqualTo(
            pixelTolerance,
            latitudePixels,
            $"Latitude anchor moved by {latitudePixels} pixels.");
    }

    internal static async Task WaitForAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + InputTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("The injected input did not update the map before the timeout.");
    }

    internal static bool TryGetDisplayedCamera(
        MapControl map,
        out BasicGeoposition center,
        out double zoom)
    {
        return map.TryGetDisplayedCamera(
            out center,
            out zoom,
            out _,
            out _);
    }

    private static double LatitudeToWorldY(double latitude)
    {
        double radians = latitude * Math.PI / 180;
        return (1 - (Math.Log(Math.Tan(radians) + (1 / Math.Cos(radians))) / Math.PI)) / 2;
    }

    private static double WorldYToLatitude(double worldY)
    {
        double mercator = Math.PI - (2 * Math.PI * worldY);
        return Math.Atan(Math.Sinh(mercator)) * 180 / Math.PI;
    }
}
