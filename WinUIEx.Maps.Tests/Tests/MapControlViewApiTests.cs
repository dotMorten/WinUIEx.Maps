using Microsoft.UI.Xaml;
using Windows.Devices.Geolocation;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests.Tests;

[TestClass]
public sealed class MapControlViewApiTests
{
    [TestMethod]
    public void TrySetViewAsyncExposesAllUwpOverloads()
    {
        Type type = typeof(MapControl);
        Type nullableDouble = typeof(double?);

        Assert.IsNotNull(type.GetMethod(
            nameof(MapControl.TrySetViewAsync),
            [typeof(Geopoint)]));
        Assert.IsNotNull(type.GetMethod(
            nameof(MapControl.TrySetViewAsync),
            [typeof(Geopoint), nullableDouble]));
        Assert.IsNotNull(type.GetMethod(
            nameof(MapControl.TrySetViewAsync),
            [
                typeof(Geopoint),
                nullableDouble,
                nullableDouble,
                nullableDouble,
            ]));
        Assert.IsNotNull(type.GetMethod(
            nameof(MapControl.TrySetViewAsync),
            [
                typeof(Geopoint),
                nullableDouble,
                nullableDouble,
                nullableDouble,
                typeof(MapAnimationKind),
            ]));
        Assert.IsTrue(type
            .GetMethods()
            .Where(method => method.Name == nameof(MapControl.TrySetViewAsync))
            .All(method => method.ReturnType == typeof(Task<bool>)));
    }

    [TestMethod]
    public void TrySetViewBoundsAsyncExposesUwpSignature()
    {
        Type type = typeof(MapControl);

        Assert.IsNotNull(type.GetMethod(
            nameof(MapControl.TrySetViewBoundsAsync),
            [
                typeof(GeoboundingBox),
                typeof(Thickness?),
                typeof(MapAnimationKind),
            ]));
        Assert.AreEqual(
            typeof(Task<bool>),
            type.GetMethod(
                nameof(MapControl.TrySetViewBoundsAsync),
                [
                    typeof(GeoboundingBox),
                    typeof(Thickness?),
                    typeof(MapAnimationKind),
                ])!.ReturnType);
    }

    [TestMethod]
    public void MapAnimationKindMatchesUwpValues()
    {
        CollectionAssert.AreEqual(
            new[] { 0, 1, 2, 3 },
            Enum.GetValues<MapAnimationKind>()
                .Select(value => (int)value)
                .ToArray());
    }

    [TestMethod]
    public void CameraAnimationKindsUseDistinctProgressCurves()
    {
        const double progress = 0.5;

        Assert.AreEqual(
            progress,
            Rendering.CameraAnimation.Ease(progress, MapAnimationKind.Linear));
        Assert.AreEqual(
            0.75,
            Rendering.CameraAnimation.Ease(progress, MapAnimationKind.Bow));
        Assert.AreEqual(
            0.875,
            Rendering.CameraAnimation.Ease(progress, MapAnimationKind.Default));
    }

    [TestMethod]
    public void BoundsViewFitsEveryCornerInsideMargins()
    {
        GeoboundingBox bounds = CreateBounds(48, -123, 47, -121);
        var margin = new Thickness(40, 20, 60, 30);

        Assert.IsTrue(MapControl.TryCalculateBoundsView(
            bounds,
            margin,
            800,
            500,
            25,
            20,
            out BasicGeoposition center,
            out double zoom));

        AssertCornersAreInside(
            bounds,
            margin,
            center,
            zoom,
            800,
            500,
            25,
            20);
    }

    [TestMethod]
    public void BoundsViewHandlesAntimeridianAndDegenerateBounds()
    {
        GeoboundingBox antimeridian = CreateBounds(10, 170, -10, -170);

        Assert.IsTrue(MapControl.TryCalculateBoundsView(
            antimeridian,
            new Thickness(),
            600,
            400,
            0,
            0,
            out BasicGeoposition antimeridianCenter,
            out double antimeridianZoom));
        Assert.IsTrue(Math.Abs(antimeridianCenter.Longitude) > 179);
        AssertCornersAreInside(
            antimeridian,
            new Thickness(),
            antimeridianCenter,
            antimeridianZoom,
            600,
            400,
            0,
            0);

        GeoboundingBox point = CreateBounds(47.61, -122.33, 47.61, -122.33);
        Assert.IsTrue(MapControl.TryCalculateBoundsView(
            point,
            new Thickness(10),
            600,
            400,
            0,
            0,
            out BasicGeoposition pointCenter,
            out double pointZoom));
        Assert.AreEqual(47.61, pointCenter.Latitude, 0.000000001);
        Assert.AreEqual(-122.33, pointCenter.Longitude, 0.000000001);
        Assert.AreEqual(MapCamera.MaximumTileZoom, pointZoom, 0.000001);
    }

    [TestMethod]
    public void BoundsViewRejectsInvalidOrOversizedMargins()
    {
        GeoboundingBox bounds = CreateBounds(48, -123, 47, -121);

        Assert.IsFalse(MapControl.TryCalculateBoundsView(
            bounds,
            new Thickness(-1),
            800,
            500,
            0,
            0,
            out _,
            out _));
        Assert.IsFalse(MapControl.TryCalculateBoundsView(
            bounds,
            new Thickness(400, 0, 400, 0),
            800,
            500,
            0,
            0,
            out _,
            out _));
    }

    private static GeoboundingBox CreateBounds(
        double north,
        double west,
        double south,
        double east) =>
        new(
            new BasicGeoposition
            {
                Latitude = north,
                Longitude = west,
            },
            new BasicGeoposition
            {
                Latitude = south,
                Longitude = east,
            });

    private static void AssertCornersAreInside(
        GeoboundingBox bounds,
        Thickness margin,
        BasicGeoposition center,
        double zoom,
        double viewportWidth,
        double viewportHeight,
        double heading,
        double pitch)
    {
        BasicGeoposition northwest = bounds.NorthwestCorner;
        BasicGeoposition southeast = bounds.SoutheastCorner;
        BasicGeoposition[] corners =
        [
            northwest,
            new()
            {
                Latitude = northwest.Latitude,
                Longitude = southeast.Longitude,
            },
            southeast,
            new()
            {
                Latitude = southeast.Latitude,
                Longitude = northwest.Longitude,
            },
        ];

        foreach (BasicGeoposition corner in corners)
        {
            Assert.IsTrue(MapCamera.TryProjectLocation(
                corner.Longitude,
                corner.Latitude,
                center.Longitude,
                center.Latitude,
                zoom,
                viewportWidth,
                viewportHeight,
                heading,
                pitch,
                out MapViewportPoint point));
            Assert.IsGreaterThanOrEqualTo(margin.Left - 0.5, point.X);
            Assert.IsLessThanOrEqualTo(
                viewportWidth - margin.Right + 0.5,
                point.X);
            Assert.IsGreaterThanOrEqualTo(margin.Top - 0.5, point.Y);
            Assert.IsLessThanOrEqualTo(
                viewportHeight - margin.Bottom + 0.5,
                point.Y);
        }
    }
}
