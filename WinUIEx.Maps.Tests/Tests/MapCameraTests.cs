using Microsoft.VisualStudio.TestTools.UnitTesting;

using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class MapCameraTests
{
    [TestMethod]
    [DataRow(2, 1)]
    [DataRow(0.5, -1)]
    [DataRow(1, 0)]
    public void PinchScaleConvertsToAdditiveZoomDelta(double scale, double expectedZoomDelta)
    {
        Assert.IsTrue(MapCamera.TryGetZoomDeltaFromScale(scale, out double zoomDelta));

        Assert.AreEqual(Math.Round((double)(expectedZoomDelta), 10), Math.Round((double)(zoomDelta), 10));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public void InvalidPinchScaleIsRejected(double scale)
    {
        Assert.IsFalse(MapCamera.TryGetZoomDeltaFromScale(scale, out double zoomDelta));

        Assert.AreEqual(0, zoomDelta);
    }

    [TestMethod]
    public void ZoomOneWorldUsesFourTiles()
    {
        MapScene scene = MapCamera.CreateScene(0, 0, 1, 512, 512);

        Assert.AreEqual(1, scene.TileZoom);
        Assert.AreSequenceEqual(
            [
                new TileId(1, 0, 0),
                new TileId(1, 1, 0),
                new TileId(1, 0, 1),
                new TileId(1, 1, 1),
            ],
            scene.RequiredTiles);
    }

    [TestMethod]
    public void FractionalZoomUsesBestLowerIntegerTileLevel()
    {
        MapScene scene = MapCamera.CreateScene(0, 0, 5.75, 800, 600);

        Assert.AreEqual(5, scene.TileZoom);
        foreach (var tile in scene.RequiredTiles)
        {
            Assert.AreEqual(5, tile.Zoom);
        }
        foreach (var tile in scene.VisibleTiles)
        {
            Assert.AreEqual(
                Math.Round(256 * Math.Pow(2, 0.75), 6),
                Math.Round(tile.Size, 6));
        }
    }

    [TestMethod]
    public void LowerSourceLevelCoversDisplayWithLargerTiles()
    {
        MapScene scene = MapCamera.CreateScene(12.5683, 55.6761, 22, 19, 1200, 800);

        Assert.AreEqual(19, scene.TileZoom);
        foreach (var tile in scene.RequiredTiles)
        {
            Assert.AreEqual(19, tile.Zoom);
        }
        foreach (var tile in scene.VisibleTiles)
        {
            Assert.AreEqual(2048, tile.Size);
        }
        Assert.IsTrue(scene.RequiredTiles.Count <= 4);
    }

    [TestMethod]
    public void VisibleTilesCoverViewportAtUsableZoom()
    {
        const double width = 1000;
        const double height = 600;

        MapScene scene = MapCamera.CreateScene(-122.33, 47.61, 3.25, width, height);

        Assert.IsTrue(scene.VisibleTiles.Min(tile => tile.Left) <= 0);
        Assert.IsTrue(scene.VisibleTiles.Max(tile => tile.Left + tile.Size) >= width);
        Assert.IsTrue(scene.VisibleTiles.Min(tile => tile.Top) <= 0);
        Assert.IsTrue(scene.VisibleTiles.Max(tile => tile.Top + tile.Size) >= height);
    }

    [TestMethod]
    public void RequiredTilesArePrioritizedFromViewportCenterOutward()
    {
        MapScene scene = MapCamera.CreateScene(-122.33, 47.61, 5.25, 1200, 800);

        double centerX = scene.ViewportWidth / 2;
        double centerY = scene.ViewportHeight / 2;
        double[] distances = scene.RequiredTiles
            .Select(id => scene.VisibleTiles
                .Where(tile => tile.Id == id)
                .Min(tile =>
                    Math.Pow((tile.Left + (tile.Size / 2)) - centerX, 2) +
                    Math.Pow((tile.Top + (tile.Size / 2)) - centerY, 2)))
            .ToArray();

        Assert.AreSequenceEqual(distances.Order(), distances);
        VisibleTile first = scene.VisibleTiles
            .Where(tile => tile.Id == scene.RequiredTiles[0])
            .MinBy(tile =>
                Math.Pow((tile.Left + (tile.Size / 2)) - centerX, 2) +
                Math.Pow((tile.Top + (tile.Size / 2)) - centerY, 2));
        Assert.IsInRange(first.Left, first.Left + first.Size, centerX);
        Assert.IsInRange(first.Top, first.Top + first.Size, centerY);
    }

    [TestMethod]
    public void WrappedTilePriorityUsesClosestVisibleInstance()
    {
        MapScene scene = MapCamera.CreateScene(179, 0, 1, 1600, 512);

        Assert.AreEqual(scene.RequiredTiles.Count, scene.RequiredTiles.Distinct().Count());
        Assert.AreEqual(
            scene.VisibleTiles
                .OrderBy(tile =>
                    Math.Pow((tile.Left + (tile.Size / 2)) - (scene.ViewportWidth / 2), 2) +
                    Math.Pow((tile.Top + (tile.Size / 2)) - (scene.ViewportHeight / 2), 2))
                .First()
                .Id,
            scene.RequiredTiles[0]);
    }

    [TestMethod]
    public void DatelineViewportWrapsCanonicalTileCoordinates()
    {
        MapScene scene = MapCamera.CreateScene(179, 0, 1, 1024, 256);

        foreach (var tile in scene.RequiredTiles)
        {
            Assert.IsInRange(0, 1, tile.X);
        }
        Assert.Contains(tile => tile.WorldX >= 2, scene.VisibleTiles);
        Assert.Contains(tile => tile.WorldX < 0, scene.VisibleTiles);
    }

    [TestMethod]
    [DataRow(90)]
    [DataRow(-90)]
    public void LatitudeIsClampedToWebMercator(double latitude)
    {
        MapScene scene = MapCamera.CreateScene(0, latitude, 4, 512, 512);

        Assert.IsInRange(-MapCamera.MaximumLatitude, MapCamera.MaximumLatitude, scene.Latitude);
        Assert.IsNotEmpty(scene.VisibleTiles);
        foreach (var tile in scene.RequiredTiles)
        {
            Assert.IsInRange(0, (1 << tile.Zoom) - 1, tile.Y);
        }
    }

    [TestMethod]
    public void NonFiniteCameraValuesUseSafeDefaults()
    {
        MapScene scene = MapCamera.CreateScene(double.NaN, double.NaN, double.PositiveInfinity, 512, 512);

        Assert.AreEqual(0, scene.Longitude);
        Assert.AreEqual(0, scene.Latitude);
        Assert.AreEqual(0, scene.Zoom);
        Assert.IsNotEmpty(scene.VisibleTiles);
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(360, 0)]
    [DataRow(450, 90)]
    [DataRow(-90, 270)]
    [DataRow(double.NaN, 0)]
    public void HeadingIsNormalized(double heading, double expected)
    {
        Assert.AreEqual(expected, MapCamera.NormalizeHeading(heading));
    }

    [TestMethod]
    [DataRow(-1, 0)]
    [DataRow(0, 0)]
    [DataRow(30, 30)]
    [DataRow(60, 60)]
    [DataRow(61, 60)]
    [DataRow(double.NaN, 0)]
    public void PitchIsNormalized(double pitch, double expected)
    {
        Assert.AreEqual(expected, MapCamera.NormalizePitch(pitch));
    }

    [TestMethod]
    public void HeadingRotatesEastTowardTopOfViewport()
    {
        Assert.IsTrue(MapCamera.TryProjectLocation(
            1,
            0,
            0,
            0,
            4,
            1000,
            600,
            90,
            out MapViewportPoint point));

        Assert.AreEqual(500, point.X, 0.000000001);
        Assert.IsLessThan(300, point.Y);
    }

    [TestMethod]
    public void RotatedProjectionAndLocationConversionRoundTrip()
    {
        Assert.IsTrue(MapCamera.TryProjectLocation(
            -121.9,
            47.8,
            -122.33,
            47.61,
            8,
            1000,
            600,
            37,
            out MapViewportPoint point));
        Assert.IsTrue(MapCamera.TryGetLocationFromOffset(
            -122.33,
            47.61,
            8,
            1000,
            600,
            point.X,
            point.Y,
            37,
            out MapCenter location));

        Assert.AreEqual(-121.9, location.Longitude, 0.000000001);
        Assert.AreEqual(47.8, location.Latitude, 0.000000001);
    }

    [TestMethod]
    public void PitchedProjectionAndLocationConversionRoundTrip()
    {
        Assert.IsTrue(MapCamera.TryProjectLocation(
            -121.9,
            47.8,
            -122.33,
            47.61,
            8,
            1000,
            600,
            37,
            45,
            out MapViewportPoint point));
        Assert.IsTrue(MapCamera.TryGetLocationFromOffset(
            -122.33,
            47.61,
            8,
            1000,
            600,
            point.X,
            point.Y,
            37,
            45,
            out MapCenter location));

        Assert.AreEqual(-121.9, location.Longitude, 0.000000001);
        Assert.AreEqual(47.8, location.Latitude, 0.000000001);
    }

    [TestMethod]
    public void PitchCreatesPerspectiveAroundViewportCenter()
    {
        MapCamera.TransformViewportOffset(
            100,
            -200,
            heading: 0,
            pitch: 60,
            viewportHeight: 600,
            out double upperX,
            out double upperY);
        MapCamera.TransformViewportOffset(
            100,
            200,
            heading: 0,
            pitch: 60,
            viewportHeight: 600,
            out double lowerX,
            out double lowerY);

        Assert.IsLessThan(100, upperX);
        Assert.IsGreaterThan(100, lowerX);
        Assert.IsLessThan(0, upperY);
        Assert.IsGreaterThan(0, lowerY);
        Assert.IsTrue(Math.Abs(upperY) < Math.Abs(lowerY));
    }

    [TestMethod]
    public void RotationKeepsGeographicAnchorUnderGestureCenter()
    {
        const double horizontalOffset = 180;
        const double verticalOffset = -90;
        MapCenter anchor = MapCamera.LocationAtOffset(
            -122.33,
            47.61,
            8,
            horizontalOffset,
            verticalOffset,
            15);
        MapCenter rotatedCenter = MapCamera.CenterForLocationAtOffset(
            anchor,
            8,
            horizontalOffset,
            verticalOffset,
            70);
        MapCenter locationAfterRotation = MapCamera.LocationAtOffset(
            rotatedCenter.Longitude,
            rotatedCenter.Latitude,
            8,
            horizontalOffset,
            verticalOffset,
            70);

        Assert.AreEqual(
            anchor.Longitude,
            locationAfterRotation.Longitude,
            0.000000001);
        Assert.AreEqual(
            anchor.Latitude,
            locationAfterRotation.Latitude,
            0.000000001);
    }

    [TestMethod]
    public void RotatedSceneCoversUnrotatedViewportBounds()
    {
        MapScene scene = MapCamera.CreateScene(
            0,
            0,
            4,
            1000,
            600,
            heading: 45);
        MapCamera.GetUnrotatedViewportSize(
            scene.ViewportWidth,
            scene.ViewportHeight,
            scene.Heading,
            out double coverageWidth,
            out double coverageHeight);
        double coverageLeft = (scene.ViewportWidth - coverageWidth) / 2;
        double coverageTop = (scene.ViewportHeight - coverageHeight) / 2;

        Assert.IsTrue(
            scene.VisibleTiles.Min(tile => tile.Left) <= coverageLeft);
        Assert.IsTrue(
            scene.VisibleTiles.Max(tile => tile.Left + tile.Size) >=
                coverageLeft + coverageWidth);
        Assert.IsTrue(
            scene.VisibleTiles.Min(tile => tile.Top) <= coverageTop);
        Assert.IsTrue(
            scene.VisibleTiles.Max(tile => tile.Top + tile.Size) >=
                coverageTop + coverageHeight);
    }

    [TestMethod]
    public void PitchedSceneCoversTheInverseProjectedViewport()
    {
        MapScene scene = MapCamera.CreateScene(
            0,
            0,
            4,
            1000,
            600,
            heading: 25,
            pitch: 60);
        MapCamera.GetMapPlaneViewportBounds(
            scene.ViewportWidth,
            scene.ViewportHeight,
            scene.Heading,
            scene.Pitch,
            out double minimumX,
            out double minimumY,
            out double maximumX,
            out double maximumY);

        Assert.AreEqual(60, scene.Pitch);
        Assert.IsTrue(
            scene.VisibleTiles.Min(tile => tile.Left) <=
                (scene.ViewportWidth / 2) + minimumX);
        Assert.IsTrue(
            scene.VisibleTiles.Max(tile => tile.Left + tile.Size) >=
                (scene.ViewportWidth / 2) + maximumX);
        Assert.IsTrue(
            scene.VisibleTiles.Min(tile => tile.Top) <=
                (scene.ViewportHeight / 2) + minimumY);
        Assert.IsTrue(
            scene.VisibleTiles.Max(tile => tile.Top + tile.Size) >=
                (scene.ViewportHeight / 2) + maximumY);
    }

    [TestMethod]
    public void TouchRotationWaitsForActivationThreshold()
    {
        TouchRotationState state = new();

        Assert.AreEqual(0, state.GetRotationDelta(4.9));
        Assert.IsFalse(state.IsActive);
        Assert.AreEqual(1, state.GetRotationDelta(6));
        Assert.IsTrue(state.IsActive);
        Assert.AreEqual(2, state.GetRotationDelta(8));
    }

    [TestMethod]
    public void ZoomCenterKeepsLocationAtCursorFixed()
    {
        MapCenter anchor = MapCamera.LocationAtOffset(
            -98,
            39,
            3,
            350,
            -180);
        MapCenter targetCenter = MapCamera.CenterForLocationAtOffset(
            anchor,
            7,
            350,
            -180);

        MapCenter locationAfterZoom = MapCamera.LocationAtOffset(
            targetCenter.Longitude,
            targetCenter.Latitude,
            7,
            350,
            -180);

        Assert.AreEqual(Math.Round((double)(anchor.Longitude), 10), Math.Round((double)(locationAfterZoom.Longitude), 10));
        Assert.AreEqual(Math.Round((double)(anchor.Latitude), 10), Math.Round((double)(locationAfterZoom.Latitude), 10));
    }

    [TestMethod]
    public void CursorAnchoredZoomWrapsAcrossDateline()
    {
        MapCenter anchor = MapCamera.LocationAtOffset(179, 0, 2, 300, 0);
        MapCenter targetCenter = MapCamera.CenterForLocationAtOffset(anchor, 5, 300, 0);
        MapCenter locationAfterZoom = MapCamera.LocationAtOffset(
            targetCenter.Longitude,
            targetCenter.Latitude,
            5,
            300,
            0);

        Assert.AreEqual(Math.Round((double)(anchor.Longitude), 10), Math.Round((double)(locationAfterZoom.Longitude), 10));
    }

    [TestMethod]
    public void LocationFromOffsetConvertsViewportPoint()
    {
        bool converted = MapCamera.TryGetLocationFromOffset(
            -122.33,
            47.61,
            6,
            1000,
            600,
            750,
            150,
            out MapCenter location);
        MapCenter expected = MapCamera.LocationAtOffset(-122.33, 47.61, 6, 250, -150);

        Assert.IsTrue(converted);
        Assert.AreEqual(Math.Round((double)(expected.Longitude), 10), Math.Round((double)(location.Longitude), 10));
        Assert.AreEqual(Math.Round((double)(expected.Latitude), 10), Math.Round((double)(location.Latitude), 10));
    }

    [TestMethod]
    [DataRow(-1, 300)]
    [DataRow(1001, 300)]
    [DataRow(500, -1)]
    [DataRow(500, 601)]
    [DataRow(double.NaN, 300)]
    public void LocationFromOffsetRejectsPointsOutsideViewport(double x, double y)
    {
        Assert.IsFalse(MapCamera.TryGetLocationFromOffset(
            0,
            0,
            4,
            1000,
            600,
            x,
            y,
            out _));
    }

    [TestMethod]
    public void ProjectedCameraCenterIsViewportCenter()
    {
        Assert.IsTrue(MapCamera.TryProjectLocation(
            -122.33, 47.61,
            -122.33, 47.61,
            8, 1000, 600,
            out MapViewportPoint point));

        Assert.AreEqual(Math.Round((double)(500), 10), Math.Round((double)(point.X), 10));
        Assert.AreEqual(Math.Round((double)(300), 10), Math.Round((double)(point.Y), 10));
    }

    [TestMethod]
    public void ProjectionUsesShortestWrapAcrossDateline()
    {
        Assert.IsTrue(MapCamera.TryProjectLocation(
            -179.9, 0,
            179.9, 0,
            3, 1000, 600,
            out MapViewportPoint point));

        Assert.IsInRange(500, 502, point.X);
        Assert.AreEqual(Math.Round((double)(300), 10), Math.Round((double)(point.Y), 10));
    }

    [TestMethod]
    public void ShortWorldKeepsRequestedCameraLatitude()
    {
        Assert.IsTrue(MapCamera.TryProjectLocation(
            0, 80,
            0, 80,
            1, 1000, 600,
            out MapViewportPoint point));

        Assert.AreEqual(Math.Round((double)(300), 10), Math.Round((double)(point.Y), 10));
    }

    [TestMethod]
    [DataRow(MapCamera.MaximumLatitude, 300)]
    [DataRow(-MapCamera.MaximumLatitude, 44)]
    public void VerticalWorldEdgeMayReachViewportCenter(
        double latitude,
        double expectedTileTop)
    {
        MapScene scene = MapCamera.CreateScene(0, latitude, 1, 1000, 600);
        VisibleTile tile = latitude > 0
            ? scene.VisibleTiles.Single(tile => tile.Id.Y == 0 && tile.WorldX == 0)
            : scene.VisibleTiles.Single(tile => tile.Id.Y == 1 && tile.WorldX == 0);

        Assert.AreEqual(expectedTileTop, tile.Top, 0.000000001);
    }

    [TestMethod]
    public void LowZoomAnchorRemainsAtPointerWithinRelaxedVerticalClamp()
    {
        const double initialZoom = 0;
        const double targetZoom = 1;
        const double viewportWidth = 640;
        const double viewportHeight = 480;
        const double horizontalOffset = 0;
        const double verticalOffset = -61;
        MapCenter anchor = MapCamera.LocationAtOffset(
            0,
            0,
            initialZoom,
            horizontalOffset,
            verticalOffset);
        MapCenter targetCenter = MapCamera.CenterForLocationAtOffset(
            anchor,
            targetZoom,
            horizontalOffset,
            verticalOffset);

        Assert.IsTrue(MapCamera.TryProjectLocation(
            anchor.Longitude,
            anchor.Latitude,
            targetCenter.Longitude,
            targetCenter.Latitude,
            targetZoom,
            viewportWidth,
            viewportHeight,
            out MapViewportPoint projected));

        Assert.AreEqual(viewportWidth / 2, projected.X, 0.000000001);
        Assert.AreEqual((viewportHeight / 2) + verticalOffset, projected.Y, 0.000000001);
    }

    [TestMethod]
    [DataRow(80)]
    [DataRow(-80)]
    public void ProjectionPreservesMercatorNorthSouthDirection(double locationLatitude)
    {
        Assert.IsTrue(MapCamera.TryProjectLocation(
            0, locationLatitude,
            0, 0,
            1, 1000, 600,
            out MapViewportPoint point));

        Assert.IsTrue(locationLatitude > 0 ? point.Y < 300 : point.Y > 300);
    }

    [TestMethod]
    [DataRow(double.NaN, 0)]
    [DataRow(0, double.PositiveInfinity)]
    public void ProjectionRejectsNonFiniteLocation(double longitude, double latitude)
    {
        Assert.IsFalse(MapCamera.TryProjectLocation(
            longitude, latitude,
            0, 0,
            4, 1000, 600,
            out _));
    }

    [TestMethod]
    [DataRow(-16, -31, 32, 32, true)]
    [DataRow(999, 599, 32, 32, true)]
    [DataRow(1000, 100, 32, 32, false)]
    [DataRow(-32, 100, 32, 32, false)]
    [DataRow(100, 600, 32, 32, false)]
    [DataRow(100, -32, 32, 32, false)]
    public void RectangleCullingIncludesOnlyViewportIntersections(
        double left,
        double top,
        double width,
        double height,
        bool expected)
    {
        Assert.AreEqual(
            expected,
            MapCamera.IsRectangleVisible(left, top, width, height, 1000, 600));
    }
}
