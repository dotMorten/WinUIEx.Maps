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
        const double progress = 0.25;

        Assert.AreEqual(
            progress,
            Rendering.CameraAnimation.Ease(progress, MapAnimationKind.Linear));
        Assert.AreEqual(
            0.578125,
            Rendering.CameraAnimation.Ease(progress, MapAnimationKind.Bow));
        Assert.AreEqual(
            0.578125,
            Rendering.CameraAnimation.Ease(progress, MapAnimationKind.Default));
    }

    [TestMethod]
    public void ProgrammaticCameraFollowsOneLinearCenterAndHeightPath()
    {
        CameraAnimation animation = new();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        animation.Reset(0, 0, 5, 350, 10);

        animation.SetTarget(
            0, 0, 5, 350, 10,
            20, 10, 9, 10, 30,
            800, 600,
            start,
            MapAnimationKind.Linear,
            durationMilliseconds: 1000);
        animation.GetCamera(
            start + (System.Diagnostics.Stopwatch.Frequency / 4),
            out MapCenter quarterCenter,
            out double quarterZoom,
            out double quarterHeading,
            out double quarterPitch);

        Assert.AreEqual(5, quarterCenter.Longitude, 0.000001);
        Assert.AreEqual(
            MapCamera.LatitudeToWorldY(0) +
                ((MapCamera.LatitudeToWorldY(10) - MapCamera.LatitudeToWorldY(0)) / 4),
            MapCamera.LatitudeToWorldY(quarterCenter.Latitude),
            0.000001);
        Assert.AreEqual(
            -Math.Log2(((1d / 32) * 0.75) + ((1d / 512) * 0.25)),
            quarterZoom,
            0.000001);
        Assert.AreEqual(355, quarterHeading, 0.000001);
        Assert.AreEqual(15, quarterPitch, 0.000001);

        animation.GetCamera(
            start + System.Diagnostics.Stopwatch.Frequency,
            out MapCenter completedCenter,
            out double completedZoom,
            out double completedHeading,
            out double completedPitch);
        Assert.AreEqual(20, completedCenter.Longitude, 0.000001);
        Assert.AreEqual(10, completedCenter.Latitude, 0.000001);
        Assert.AreEqual(9, completedZoom, 0.000001);
        Assert.AreEqual(10, completedHeading, 0.000001);
        Assert.AreEqual(30, completedPitch, 0.000001);
        Assert.IsFalse(animation.IsActive);
    }

    [TestMethod]
    public void BowDoesNotCreateAnOutwardApexForAnInViewTarget()
    {
        CameraAnimation animation = new();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        animation.Reset(0, 0, 12, 0, 0);
        animation.SetTarget(
            0, 0, 12, 0, 0,
            0.01, 0, 12, 0, 0,
            800, 600,
            start,
            MapAnimationKind.Bow,
            durationMilliseconds: 1000);
        animation.GetCamera(
            start + (System.Diagnostics.Stopwatch.Frequency / 2),
            out _,
            out double midpointZoom,
            out _,
            out _);

        Assert.AreEqual(12, midpointZoom, 0.000001);
        Assert.AreEqual(
            0,
            CameraAnimation.GetBowZoomOutLevels(
                0,
                0,
                0.01,
                0,
                12,
                800,
                600));
    }

    [TestMethod]
    public void BowUsesTheStraightHeightPathWhenNoOutwardArcIsNeeded()
    {
        CameraAnimation animation = new();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        animation.Reset(0, 0, 12, 0, 0);
        animation.SetTarget(
            0, 0, 12, 0, 0,
            0, 0, 10, 0, 0,
            800, 600,
            start,
            MapAnimationKind.Bow,
            durationMilliseconds: 1000);

        double midpointProgress = 1 - Math.Cbrt(0.5);
        animation.GetCamera(
            start + (long)(System.Diagnostics.Stopwatch.Frequency * midpointProgress),
            out _,
            out double midpointZoom,
            out _,
            out _);

        Assert.AreEqual(
            -Math.Log2(((1d / 4096) + (1d / 1024)) / 2),
            midpointZoom,
            0.00001);
    }

    [TestMethod]
    public void BowApexScalesFromCloseOutsideToFarTargets()
    {
        double closeApex = CameraAnimation.GetBowZoomOutLevels(
            0, 0, 0.2, 0, 12, 800, 600);
        double farApex = CameraAnimation.GetBowZoomOutLevels(
            0, 0, 10, 0, 12, 800, 600);

        Assert.IsGreaterThan(0, closeApex);
        Assert.IsGreaterThan(closeApex, farApex);
        Assert.IsLessThanOrEqualTo(5, farApex);
    }

    [TestMethod]
    public void BowLiftAmountDependsOnViewportDistance()
    {
        double longitudeAtZoomFive = 360 * (800d / (256 * Math.Pow(2, 5)));
        double longitudeAtZoomFifteen = 360 * (800d / (256 * Math.Pow(2, 15)));
        double fromZoomFive = CameraAnimation.GetBowZoomOutLevels(
            0, 0, longitudeAtZoomFive, 0, 5, 800, 600);
        double fromZoomFifteen = CameraAnimation.GetBowZoomOutLevels(
            0, 0, longitudeAtZoomFifteen, 0, 15, 800, 600);

        Assert.IsGreaterThan(0, fromZoomFive);
        Assert.AreEqual(fromZoomFive, fromZoomFifteen, 0.000001);
    }

    [TestMethod]
    public void BowBezierPathReachesItsHeightSummitAtTheGeographicMidpoint()
    {
        CameraAnimation animation = new();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        animation.Reset(0, 0, 12, 0, 0);
        animation.SetTarget(
            0, 0, 12, 0, 0,
            10, 0, 12, 0, 0,
            800, 600,
            start,
            MapAnimationKind.Bow,
            durationMilliseconds: 1000);

        double peakProgress = 1 - Math.Cbrt(0.5);
        animation.GetCamera(
            start + (long)(System.Diagnostics.Stopwatch.Frequency *
                (1 - Math.Cbrt(0.51))),
            out _,
            out double beforePeak,
            out _,
            out _);
        animation.GetCamera(
            start + (long)(System.Diagnostics.Stopwatch.Frequency * peakProgress),
            out MapCenter peakCenter,
            out double peak,
            out _,
            out _);
        animation.GetCamera(
            start + (long)(System.Diagnostics.Stopwatch.Frequency *
                (1 - Math.Cbrt(0.49))),
            out _,
            out double afterPeak,
            out _,
            out _);

        Assert.AreEqual(beforePeak, afterPeak, 0.000001);
        Assert.IsGreaterThan(peak, beforePeak);
        Assert.AreEqual(5, peakCenter.Longitude, 0.00001);
    }

    [TestMethod]
    public void BowZoomArcDeceleratesAtArrival()
    {
        CameraAnimation animation = new();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        animation.Reset(0, 0, 12, 0, 0);
        animation.SetTarget(
            0, 0, 12, 0, 0,
            10, 0, 12, 0, 0,
            800, 600,
            start,
            MapAnimationKind.Bow,
            durationMilliseconds: 1000);

        animation.GetCamera(
            start + (long)(System.Diagnostics.Stopwatch.Frequency * 0.9),
            out _,
            out double earlyArrivalZoom,
            out _,
            out _);
        animation.GetCamera(
            start + (long)(System.Diagnostics.Stopwatch.Frequency * 0.99),
            out _,
            out double nearArrivalZoom,
            out _,
            out _);
        animation.GetCamera(
            start + System.Diagnostics.Stopwatch.Frequency,
            out _,
            out double completedZoom,
            out _,
            out _);

        Assert.IsGreaterThan(
            completedZoom - nearArrivalZoom,
            nearArrivalZoom - earlyArrivalZoom);
        Assert.IsLessThan(0.1, completedZoom - nearArrivalZoom);
    }

    [TestMethod]
    public void ProgrammaticCameraUsesShortestAntimeridianRoute()
    {
        CameraAnimation animation = new();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        animation.Reset(179, 0, 10, 0, 0);
        animation.SetTarget(
            179, 0, 10, 0, 0,
            -179, 0, 10, 0, 0,
            800, 600,
            start,
            MapAnimationKind.Linear,
            durationMilliseconds: 1000);
        animation.GetCamera(
            start + (System.Diagnostics.Stopwatch.Frequency / 2),
            out MapCenter midpoint,
            out _,
            out _,
            out _);

        Assert.IsTrue(Math.Abs(midpoint.Longitude) > 179);

        animation.GetCamera(
            start + System.Diagnostics.Stopwatch.Frequency,
            out MapCenter completedCenter,
            out double completedZoom,
            out double completedHeading,
            out double completedPitch);
        Assert.AreEqual(-179, completedCenter.Longitude, 0.000001);
        Assert.AreEqual(0, completedCenter.Latitude, 0.000001);
        Assert.AreEqual(10, completedZoom, 0.000001);
        Assert.AreEqual(0, completedHeading, 0.000001);
        Assert.AreEqual(0, completedPitch, 0.000001);
        Assert.IsFalse(animation.IsActive);
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
