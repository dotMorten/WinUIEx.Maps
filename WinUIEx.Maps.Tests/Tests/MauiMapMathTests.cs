using WinUIEx.Maps.Maui;

namespace WinUIEx.Maps.Tests.Tests;

[TestClass]
public sealed class MauiMapMathTests
{
    [TestMethod]
    public void ReconciliationRetainsUnchangedMarkersAcrossInsertionRemovalAndReordering()
    {
        object first = new(), second = new(), removed = new(), retained = new(), added = new();
        IReadOnlyList<object>[] previous = [[first, second], [removed], [retained]];
        object[] markers = [new(), new(), new()];
        IReadOnlyList<object>[] next = [[retained], [added], [first, second]];
        int created = 0;
        var result = MapMath.ReconcileClusters<object, object>(
            previous.Select((pins, index) => (pins, markers[index])), next, _ =>
            {
                created++;
                return new object();
            });
        Assert.AreEqual(1, created);
        Assert.AreSame(markers[2], result[0]);
        Assert.AreSame(markers[0], result[2]);
        Assert.IsFalse(result.Contains(markers[1]));
    }

    [TestMethod]
    public void ClusterSplitsAndMergesRetainUnaffectedMarkers()
    {
        object first = new(), second = new(), independent = new();
        IReadOnlyList<object>[] merged = [[first, second], [independent]];
        object[] original = [new(), new()];
        IReadOnlyList<object>[] split = [[first], [second], [independent]];
        var splitMarkers = MapMath.ReconcileClusters<object, object>(
            merged.Select((pins, index) => (pins, original[index])), split, _ => new object());
        Assert.AreNotSame(original[0], splitMarkers[0]);
        Assert.AreNotSame(original[0], splitMarkers[1]);
        Assert.AreSame(original[1], splitMarkers[2]);
        var mergedMarkers = MapMath.ReconcileClusters<object, object>(
            split.Select((pins, index) => (pins, splitMarkers[index])), merged, _ => new object());
        Assert.AreNotSame(splitMarkers[0], mergedMarkers[0]);
        Assert.AreNotSame(splitMarkers[1], mergedMarkers[0]);
        Assert.AreSame(original[1], mergedMarkers[1]);
    }

    [TestMethod]
    public void EqualButDistinctPinsDoNotReuseOldMarkers()
    {
        var first = new EqualPin("Pin");
        var replacement = new EqualPin("Pin");
        var marker = new object();
        var result = MapMath.ReconcileClusters<EqualPin, object>(
            [((IReadOnlyList<EqualPin>)[first], marker)], [[replacement]], _ => new object());
        Assert.AreNotSame(marker, result[0]);
    }

    private sealed record EqualPin(string Label);

    [TestMethod]
    public void ClustersRespectIdentifiersAndEmptyIdentifiersOptOut()
    {
        var points = new[]
        {
            (Latitude: 10d, Longitude: 20d, Key: "a"),
            (Latitude: 10d, Longitude: 20d, Key: "a"),
            (Latitude: 10d, Longitude: 20d, Key: "b"),
            (Latitude: 10d, Longitude: 20d, Key: ""),
            (Latitude: 10d, Longitude: 20d, Key: ""),
        };
        var groups = MapMath.Cluster(points, p => p, 5);
        CollectionAssert.AreEquivalent(new[] { 2, 1, 1, 1 }, groups.Select(g => g.Count).ToArray());
    }

    [TestMethod]
    public void ClustersSplitAsZoomIncreases()
    {
        var points = new[]
        {
            (Latitude: 0d, Longitude: 0d, Key: "a"),
            (Latitude: 0d, Longitude: 0.001d, Key: "a"),
        };
        Assert.HasCount(1, MapMath.Cluster(points, p => p, 5));
        Assert.HasCount(2, MapMath.Cluster(points, p => p, 20));
    }

    [TestMethod]
    public void DateLineNeighborsClusterAndCentroidRemainsAtDateLine()
    {
        var points = new[]
        {
            (Latitude: 0d, Longitude: 179.999d, Key: "a"),
            (Latitude: 0d, Longitude: -179.999d, Key: "a"),
        };
        Assert.HasCount(1, MapMath.Cluster(points, p => p, 5));
        var center = MapMath.Centroid(points.Select(p => (p.Latitude, p.Longitude)));
        Assert.AreEqual(180, Math.Abs(center.Longitude), 0.000001);
        Assert.AreEqual(0, center.Latitude);
    }

    [TestMethod]
    public void CircleUsesGeodesicRadiusAndWrapsDateLine()
    {
        const double radius = 1000;
        var north = MapMath.CirclePoint(0, 179.999, radius, 0);
        var east = MapMath.CirclePoint(0, 179.999, radius, 90);
        Assert.AreEqual(radius / 6371008.8 * 180 / Math.PI, north.Latitude, 1e-10);
        Assert.AreEqual(179.999, north.Longitude, 1e-10);
        Assert.AreEqual(0, east.Latitude, 1e-10);
        Assert.IsLessThan(-179.9, east.Longitude);
        Assert.IsTrue(double.IsFinite(MapMath.WorldY(90)));
        Assert.IsTrue(double.IsFinite(MapMath.WorldY(-90)));
    }
}
