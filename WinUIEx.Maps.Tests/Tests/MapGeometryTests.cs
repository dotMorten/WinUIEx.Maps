using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Devices.Geolocation;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class MapGeometryTests
{
    [TestMethod]
    public void VectorDefaultsAndChangesArePublished()
    {
        MapPolygon polygon = new();
        MapPolyline polyline = new();
        int polygonChanges = 0;
        int polylineChanges = 0;
        polygon.Changed += (_, _) => polygonChanges++;
        polyline.Changed += (_, _) => polylineChanges++;

        Assert.IsNull(polygon.Path);
        Assert.IsEmpty(polygon.Paths);
        Assert.AreEqual(Microsoft.UI.Colors.Transparent, polygon.FillColor);
        Assert.AreEqual(Microsoft.UI.Colors.Black, polygon.StrokeColor);
        Assert.IsFalse(polygon.StrokeDashed);
        Assert.AreEqual(1d, polygon.StrokeThickness);
        Assert.IsNull(polyline.Path);
        Assert.AreEqual(Microsoft.UI.Colors.Black, polyline.StrokeColor);
        Assert.IsFalse(polyline.StrokeDashed);
        Assert.AreEqual(1d, polyline.StrokeThickness);

        polygon.FillColor = Microsoft.UI.Colors.Red;
        polygon.StrokeColor = Microsoft.UI.Colors.Blue;
        polygon.StrokeDashed = true;
        polygon.StrokeThickness = 3;
        polyline.StrokeColor = Microsoft.UI.Colors.Green;
        polyline.StrokeDashed = true;
        polyline.StrokeThickness = 4;
        polyline.Path = CreatePath((0, 0), (1, 1));

        Assert.AreEqual(4, polygonChanges);
        Assert.AreEqual(4, polylineChanges);
        Assert.Throws<ArgumentOutOfRangeException>(() => polygon.StrokeThickness = double.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => polyline.StrokeThickness = -1);
    }

    [TestMethod]
    public void PathAndPathsAreMutuallyExclusive()
    {
        Geopath single = CreatePath((-1, -1), (1, -1), (1, 1), (-1, 1));
        Geopath outer = CreatePath((-2, -2), (2, -2), (2, 2), (-2, 2));
        Geopath hole = CreatePath((-.5, -.5), (.5, -.5), (.5, .5), (-.5, .5));
        MapPolygon polygon = new() { Path = single };
        int changes = 0;
        polygon.Changed += (_, _) => changes++;

        polygon.Paths.Add(outer);
        polygon.Paths.Add(hole);

        Assert.IsNull(polygon.Path);
        Assert.AreSequenceEqual([outer, hole], polygon.Paths);
        Assert.AreEqual(2, changes);
        MapGeometryData pathsGeometry = polygon.GetState().Geometry;

        polygon.Path = single;

        Assert.AreSame(single, polygon.Path);
        Assert.IsEmpty(polygon.Paths);
        Assert.AreNotSame(pathsGeometry, polygon.GetState().Geometry);
        Assert.AreEqual(3, changes);

        Assert.IsFalse(polygon.Paths.Remove(outer));
        Assert.AreSame(single, polygon.Path);
        Assert.AreEqual(3, changes);
    }

    [TestMethod]
    public void PolygonEvenOddTessellationSupportsHoles()
    {
        MapPolygon polygon = new()
        {
            FillColor = Microsoft.UI.Colors.Red,
            StrokeThickness = 0,
        };
        polygon.Paths.Add(CreatePath((-2, -2), (2, -2), (2, 2), (-2, 2)));
        polygon.Paths.Add(CreatePath((-.5, -.5), (.5, -.5), (.5, .5), (-.5, .5)));
        MapPolygonState state = polygon.GetState();
        MapGeometrySnapshot snapshot = CreateSnapshot(polygon, orderIndex: 0);
        MapGeometryCamera camera = new(0, 0, 6, 0, 512, 512);

        Assert.IsGreaterThan(0, state.Geometry.FillVertices.Length);
        Assert.IsGreaterThan(0, state.Geometry.FillIndices.Length);
        Assert.IsFalse(MapGeometryOperations.Contains(snapshot, camera, 256, 256));
        Assert.IsTrue(MapCamera.TryProjectLocation(
            1,
            0,
            0,
            0,
            6,
            512,
            512,
            out MapViewportPoint filledPoint));
        Assert.IsTrue(MapGeometryOperations.Contains(
            snapshot,
            camera,
            filledPoint.X,
            filledPoint.Y));
        Assert.IsGreaterThan(
            0,
            MapGeometryOperations.BuildFillTriangles(state.Geometry, camera).Length);
    }

    [TestMethod]
    public void DatelinePolylineUsesShortestWrappedPath()
    {
        MapPolyline polyline = new()
        {
            Path = CreatePath((179, 0), (-179, 0)),
            StrokeThickness = 2,
        };
        MapGeometryData geometry = polyline.GetState().Geometry;
        MapWorldPoint[] points = Assert.ContainsSingle(geometry.Contours).Points;
        MapGeometryCamera camera = new(180, 0, 5, 0, 512, 512);

        Assert.IsLessThan(.01, Math.Abs(points[1].X - points[0].X));
        MapScreenSegment segment = Assert.ContainsSingle(
            MapGeometryOperations.BuildStrokeSegments(
                geometry,
                closed: false,
                thickness: 2,
                dashed: false,
                camera));
        Assert.IsLessThan(60, Math.Abs(segment.End.X - segment.Start.X));
        Assert.IsGreaterThan(200, segment.Start.X);
        Assert.IsLessThan(312, segment.End.X);

        MapScreenSegment rotated = Assert.ContainsSingle(
            MapGeometryOperations.BuildStrokeSegments(
                geometry,
                closed: false,
                thickness: 2,
                dashed: false,
                camera with { Heading = 90 }));
        Assert.IsLessThan(1, Math.Abs(rotated.End.X - rotated.Start.X));
        Assert.IsGreaterThan(20, Math.Abs(rotated.End.Y - rotated.Start.Y));
    }

    [TestMethod]
    public void DashedStrokeGenerationIsDeterministicAndScreenSpace()
    {
        MapPolyline polyline = new()
        {
            Path = CreatePath((-2, 0), (2, 0)),
            StrokeThickness = 2,
            StrokeDashed = true,
        };
        MapGeometryCamera camera = new(0, 0, 5, 0, 512, 512);

        MapScreenSegment[] first = MapGeometryOperations.BuildStrokeSegments(
            polyline.GetState().Geometry,
            closed: false,
            polyline.StrokeThickness,
            polyline.StrokeDashed,
            camera);
        MapScreenSegment[] second = MapGeometryOperations.BuildStrokeSegments(
            polyline.GetState().Geometry,
            closed: false,
            polyline.StrokeThickness,
            polyline.StrokeDashed,
            camera);

        Assert.AreSequenceEqual(first, second);
        Assert.IsGreaterThan(1, first.Length);
        foreach (MapScreenSegment segment in first)
        {
            Assert.IsLessThanOrEqualTo(
                6.001,
                Math.Abs(segment.End.X - segment.Start.X));
        }
        MapScreenPoint[] triangles =
            MapGeometryOperations.ExpandStrokeTriangles(first, 2);
        Assert.AreEqual(first.Length * 6, triangles.Length);
        Assert.AreEqual(
            2,
            Math.Abs(triangles[0].Y - triangles[5].Y),
            .001);
    }

    [TestMethod]
    public void AutomaticRoundJoinsFillCornersAndPolygonSeams()
    {
        MapScreenSegment[] open =
        [
            new(new MapScreenPoint(0, 10), new MapScreenPoint(10, 10)),
            new(new MapScreenPoint(10, 10), new MapScreenPoint(10, 20)),
        ];

        MapScreenPoint[] openTriangles =
            MapGeometryOperations.ExpandStrokeTriangles(
                open,
                thickness: 10,
                closed: false,
                MapGeometryOperations.AutomaticStrokeJoinPolicy);

        Assert.AreEqual(18, openTriangles.Length);
        Assert.IsTrue(TrianglesContain(openTriangles, new MapScreenPoint(13, 7)));

        MapScreenSegment[] closed =
        [
            new(new MapScreenPoint(10, 10), new MapScreenPoint(20, 10)),
            new(new MapScreenPoint(20, 10), new MapScreenPoint(20, 20)),
            new(new MapScreenPoint(20, 20), new MapScreenPoint(10, 20)),
            new(new MapScreenPoint(10, 20), new MapScreenPoint(10, 10)),
        ];
        MapScreenPoint[] closedTriangles =
            MapGeometryOperations.ExpandStrokeTriangles(
                closed,
                thickness: 10,
                closed: true,
                MapGeometryOperations.AutomaticStrokeJoinPolicy);

        Assert.AreEqual(48, closedTriangles.Length);
        Assert.IsTrue(TrianglesContain(closedTriangles, new MapScreenPoint(7, 7)));
    }

    [TestMethod]
    public void AutomaticRoundJoinsRespectPathsDashesAndSharpAngleBounds()
    {
        MapScreenSegment[] disconnected =
        [
            new(
                new MapScreenPoint(0, 10),
                new MapScreenPoint(10, 10),
                PathIndex: 0),
            new(
                new MapScreenPoint(10, 10),
                new MapScreenPoint(10, 20),
                PathIndex: 1),
        ];
        MapScreenPoint[] disconnectedTriangles =
            MapGeometryOperations.ExpandStrokeTriangles(
                disconnected,
                thickness: 10,
                closed: false,
                MapGeometryOperations.AutomaticStrokeJoinPolicy);
        Assert.AreEqual(12, disconnectedTriangles.Length);

        MapScreenSegment[] sharp =
        [
            new(new MapScreenPoint(0, 0), new MapScreenPoint(10, 0)),
            new(new MapScreenPoint(10, 0), new MapScreenPoint(1, 1)),
        ];
        MapScreenPoint[] sharpTriangles =
            MapGeometryOperations.ExpandStrokeTriangles(
                sharp,
                thickness: 10,
                closed: false,
                MapGeometryOperations.AutomaticStrokeJoinPolicy);

        Assert.IsLessThanOrEqualTo(24, sharpTriangles.Length);
        Assert.IsTrue(sharpTriangles.All(point =>
            Math.Abs(point.X - 10) <= 15 &&
            Math.Abs(point.Y) <= 10));
    }

    [TestMethod]
    public void GeometryHitTestingHonorsFillStrokeDashesAndOrder()
    {
        MapPolygon polygon = new()
        {
            FillColor = Microsoft.UI.Colors.Red,
            StrokeThickness = 0,
        };
        polygon.Path = CreatePath((-1, -1), (1, -1), (1, 1), (-1, 1));
        MapPolyline polyline = new()
        {
            Path = CreatePath((-2, 0), (2, 0)),
            StrokeColor = Microsoft.UI.Colors.Blue,
            StrokeThickness = 4,
        };
        MapGeometryCamera camera = new(0, 0, 6, 0, 512, 512);
        MapGeometrySnapshot bottom = CreateSnapshot(polygon, orderIndex: 0) with
        {
            ElementIndex = 10,
        };
        MapGeometrySnapshot top = CreateSnapshot(polyline, orderIndex: 2) with
        {
            ElementIndex = 20,
        };

        Assert.IsTrue(MapGeometryOperations.Contains(top, camera, 256, 256));
        Assert.IsFalse(MapGeometryOperations.Contains(top, camera, 256, 270));
        Assert.IsTrue(MapGeometryOperations.TryHitTestAbove(
            [bottom, top],
            [true],
            camera,
            256,
            256,
            minimumOrder: 1,
            out int elementIndex,
            out int orderIndex));
        Assert.AreEqual(20, elementIndex);
        Assert.AreEqual(2, orderIndex);
        Assert.IsFalse(MapGeometryOperations.TryHitTestAbove(
            [bottom],
            [true],
            camera,
            256,
            256,
            minimumOrder: 1,
            out _,
            out _));

        Assert.IsTrue(MapGeometryOperations.TryHitTestAbove(
            [bottom, top with { IsEnabled = false }],
            [true],
            camera,
            256,
            256,
            minimumOrder: -1,
            out elementIndex,
            out orderIndex));
        Assert.AreEqual(10, elementIndex);
        Assert.AreEqual(0, orderIndex);

        polyline.StrokeDashed = true;
        MapGeometrySnapshot dashed = CreateSnapshot(polyline, orderIndex: 2);
        MapScreenSegment[] dashes = MapGeometryOperations.BuildStrokeSegments(
            dashed.Geometry,
            closed: false,
            dashed.StrokeThickness,
            dashed.StrokeDashed,
            camera);
        MapScreenPoint gap = new(
            (dashes[0].End.X + dashes[1].Start.X) / 2,
            (dashes[0].End.Y + dashes[1].Start.Y) / 2);
        Assert.IsFalse(MapGeometryOperations.Contains(dashed, camera, gap.X, gap.Y));

        MapPolygon strokeOnly = new()
        {
            Path = CreatePath((-1, -1), (1, -1), (1, 1), (-1, 1)),
            FillColor = Microsoft.UI.Colors.Transparent,
            StrokeColor = Microsoft.UI.Colors.Red,
            StrokeThickness = 4,
        };
        MapGeometrySnapshot strokeSnapshot = CreateSnapshot(strokeOnly, orderIndex: 3);
        Assert.IsTrue(MapCamera.TryProjectLocation(
            1,
            0,
            0,
            0,
            6,
            512,
            512,
            out MapViewportPoint edge));
        Assert.IsTrue(MapGeometryOperations.Contains(
            strokeSnapshot,
            camera,
            edge.X,
            edge.Y));
        Assert.IsFalse(MapGeometryOperations.Contains(
            strokeSnapshot,
            camera,
            256,
            256));
    }

    [TestMethod]
    public void StrokeHitTestingUsesRenderedButtCaps()
    {
        MapPolyline polyline = new()
        {
            Path = CreatePath((-1, 0), (1, 0)),
            StrokeThickness = 10,
        };
        MapGeometryCamera camera = new(0, 0, 6, 0, 512, 512);
        MapGeometrySnapshot snapshot = CreateSnapshot(polyline, orderIndex: 0);
        MapScreenSegment segment = Assert.ContainsSingle(
            MapGeometryOperations.BuildStrokeSegments(
                snapshot.Geometry,
                closed: false,
                snapshot.StrokeThickness,
                snapshot.StrokeDashed,
                camera));

        Assert.IsTrue(MapGeometryOperations.Contains(
            snapshot,
            camera,
            segment.Start.X,
            segment.Start.Y + 4));
        Assert.IsFalse(MapGeometryOperations.Contains(
            snapshot,
            camera,
            segment.Start.X - 1,
            segment.Start.Y));
    }

    [TestMethod]
    public void StrokeHitTestingIncludesAutomaticRoundJoin()
    {
        MapPolyline polyline = new()
        {
            Path = CreatePath((-1, 0), (0, 0), (0, -1)),
            StrokeThickness = 10,
        };
        MapGeometryCamera camera = new(0, 0, 6, 0, 512, 512);
        MapGeometrySnapshot snapshot = CreateSnapshot(polyline, orderIndex: 0);
        MapScreenSegment[] segments = MapGeometryOperations.BuildStrokeSegments(
            snapshot.Geometry,
            closed: false,
            snapshot.StrokeThickness,
            snapshot.StrokeDashed,
            camera);
        Assert.HasCount(2, segments);
        MapScreenPoint joinOnlyPoint = new(
            segments[0].End.X + 3,
            segments[0].End.Y - 3);

        Assert.IsTrue(MapGeometryOperations.Contains(
            snapshot,
            camera,
            joinOnlyPoint.X,
            joinOnlyPoint.Y));
    }

    [TestMethod]
    public void GeometryColorPreservesRgbAndMultipliesAlphaByLayerOpacity()
    {
        MapColorSnapshot color = MapColorSnapshot.FromColor(
            Windows.UI.Color.FromArgb(128, 64, 32, 16));

        System.Numerics.Vector4 vector = color.ToVector(.5);

        Assert.AreEqual(64 / 255f, vector.X, .0001);
        Assert.AreEqual(32 / 255f, vector.Y, .0001);
        Assert.AreEqual(16 / 255f, vector.Z, .0001);
        Assert.AreEqual((128 / 255f) * .5f, vector.W, .0001);
    }

    [TestMethod]
    public void IconSpatialIndexUsesLayerAndElementOrder()
    {
        MapIconSpatialIndex index = new();
        MapIconSnapshot later = new(
            1, 0, 0, 32, 32, LayerIndex: 0, OrderIndex: 3);
        MapIconSnapshot earlier = new(
            2, 0, 0, 32, 32, LayerIndex: 0, OrderIndex: 1);
        MapIconSnapshot topLayer = new(
            3, 0, 0, 32, 32, LayerIndex: 1, OrderIndex: 4);
        index.Rebuild([later, topLayer, earlier]);

        Assert.AreSequenceEqual(
            [earlier, later, topLayer],
            index.GetVisible(0, 0, 10, 512, 512));
        Assert.IsTrue(index.TryHitTest(
            0,
            0,
            10,
            512,
            512,
            256,
            256,
            [true, true],
            heading: 0,
            out int elementIndex,
            out int orderIndex));
        Assert.AreEqual(1, elementIndex);
        Assert.AreEqual(4, orderIndex);
    }

    [TestMethod]
    public void NonFiniteCoordinatesAndExcessiveGeometryAreRejected()
    {
        MapPolyline polyline = new();
        Assert.Throws<ArgumentException>(() =>
            polyline.Path = CreatePath((double.NaN, 0), (0, 0)));

        BasicGeoposition[] positions = new BasicGeoposition[
            MapGeometryData.MaximumPointCount + 1];
        Assert.Throws<ArgumentException>(() =>
            polyline.Path = new Geopath(positions));
    }

    private static MapGeometrySnapshot CreateSnapshot(
        MapPolygon polygon,
        int orderIndex)
    {
        MapPolygonState state = polygon.GetState();
        return new MapGeometrySnapshot(
            MapGeometryKind.Polygon,
            state.Geometry,
            MapColorSnapshot.FromColor(state.FillColor),
            MapColorSnapshot.FromColor(state.StrokeColor),
            state.StrokeDashed,
            state.StrokeThickness,
            0,
            0,
            orderIndex);
    }

    private static MapGeometrySnapshot CreateSnapshot(
        MapPolyline polyline,
        int orderIndex)
    {
        MapPolylineState state = polyline.GetState();
        return new MapGeometrySnapshot(
            MapGeometryKind.Polyline,
            state.Geometry,
            default,
            MapColorSnapshot.FromColor(state.StrokeColor),
            state.StrokeDashed,
            state.StrokeThickness,
            0,
            0,
            orderIndex);
    }

    private static Geopath CreatePath(params (double Longitude, double Latitude)[] points) =>
        new(points.Select(point => new BasicGeoposition
        {
            Longitude = point.Longitude,
            Latitude = point.Latitude,
        }));

    private static bool TrianglesContain(
        IReadOnlyList<MapScreenPoint> triangles,
        MapScreenPoint point)
    {
        for (int index = 0; index + 2 < triangles.Count; index += 3)
        {
            MapScreenPoint first = triangles[index];
            MapScreenPoint second = triangles[index + 1];
            MapScreenPoint third = triangles[index + 2];
            double firstCross = Cross(first, second, point);
            double secondCross = Cross(second, third, point);
            double thirdCross = Cross(third, first, point);
            if ((firstCross >= -1e-7 &&
                    secondCross >= -1e-7 &&
                    thirdCross >= -1e-7) ||
                (firstCross <= 1e-7 &&
                    secondCross <= 1e-7 &&
                    thirdCross <= 1e-7))
            {
                return true;
            }
        }
        return false;
    }

    private static double Cross(
        MapScreenPoint start,
        MapScreenPoint end,
        MapScreenPoint point) =>
        ((end.X - start.X) * (point.Y - start.Y)) -
        ((end.Y - start.Y) * (point.X - start.X));
}
