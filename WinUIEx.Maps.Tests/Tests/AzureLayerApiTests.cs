using System.Reflection;
using Microsoft.UI.Xaml;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class AzureLayerApiTests
{
    [TestMethod]
    public void AzureHierarchyDoesNotChangeCustomTileInheritance()
    {
        Assert.AreEqual(typeof(MapLayer), typeof(TileLayer).BaseType);
        Assert.AreEqual(typeof(MapLayer), typeof(AzureTileLayer).BaseType);
        Assert.IsTrue(typeof(AzureTileLayer).IsPublic);
        Assert.IsTrue(typeof(AzureTileLayer).IsAbstract);
        Assert.AreEqual(0, typeof(AzureTileLayer).GetConstructors().Length);
        Assert.IsTrue(typeof(AzureTileLayer).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .All(c => c.IsAssembly));
        foreach (Type type in new[] { typeof(AzureTrafficLayer), typeof(AzureWeatherLayer), typeof(AzureBaseTileLayer) })
        {
            Assert.AreEqual(typeof(AzureTileLayer), type.BaseType);
            Assert.IsTrue(type.IsSealed);
            Assert.IsNull(type.GetProperty(nameof(TileLayer.TileUrl)));
            Assert.IsNull(type.GetProperty(nameof(TileLayer.StyleUrl)));
        }
        Assert.IsFalse(typeof(AzureBaseTileLayer).IsPublic);
        Assert.IsTrue(typeof(AzureTrafficLayer).IsPublic);
        Assert.IsTrue(typeof(AzureWeatherLayer).IsPublic);
        Assert.IsNull(typeof(MapLayer).Assembly.GetType("WinUIEx.Maps.TrafficLayer"));
        Assert.IsNull(typeof(MapLayer).Assembly.GetType("WinUIEx.Maps.WeatherLayer"));
        Assert.IsNull(typeof(AzureTrafficLayer).GetField("RoadLineOpacity"));
        Assert.AreEqual(0.5, typeof(AzureTrafficLayer)
            .GetField("RoadLineOpacity", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue());
        Assert.AreEqual(typeof(double), typeof(AzureTrafficLayer).GetProperty(nameof(AzureTrafficLayer.MinIncidentZoom))!.PropertyType);
        foreach (var (type, name) in new[]
        {
            (typeof(AzureTrafficLayer), nameof(AzureTrafficLayer.FlowStyle)),
            (typeof(AzureTrafficLayer), nameof(AzureTrafficLayer.ShowIncidents)),
            (typeof(AzureTrafficLayer), nameof(AzureTrafficLayer.MinIncidentZoom)),
            (typeof(AzureWeatherLayer), nameof(AzureWeatherLayer.Kind)),
            (typeof(AzureWeatherLayer), nameof(AzureWeatherLayer.Timestamp)),
        })
        {
            Assert.IsNotNull(type.GetProperty(name));
            Assert.AreEqual(typeof(DependencyProperty), type.GetField(name + "Property")!.FieldType);
        }
    }

    [TestMethod]
    [DataRow(TrafficFlowStyle.Absolute, "microsoft.traffic.absolute")]
    [DataRow(TrafficFlowStyle.Relative, "microsoft.traffic.relative")]
    [DataRow(TrafficFlowStyle.RelativeDark, "microsoft.traffic.relative")]
    [DataRow(TrafficFlowStyle.Delay, "microsoft.traffic.delay")]
    [DataRow(TrafficFlowStyle.Reduced, "microsoft.traffic.relative")]
    public void TrafficStylesSelectSupportedVectorTilesets(TrafficFlowStyle style, string expected) =>
        Assert.AreEqual(expected, AzureTrafficLayer.GetTileset(style));

    [TestMethod]
    public void IncidentDetailsAreReadOnlyAndMapOptionalTileFields()
    {
        foreach (string name in new[] { "Position", "Description", "Delay", "Title", "IncidentType", "StartTime", "EndTime" })
            Assert.IsFalse(typeof(AzureTrafficIncidentEventArgs).GetProperty(name)!.CanWrite);
        var location = new Windows.Devices.Geolocation.Geopoint(new() { Latitude = 47, Longitude = -122 });
        VectorTileFeature feature = new("Traffic incident POI", VectorTileGeometryType.Point, [], [
            new("id", VectorTileValue.FromString("fixture")),
            new("icon_category_0", VectorTileValue.FromInt(9)),
            new("description_0", VectorTileValue.FromString("Lane closed")),
            new("description_1", VectorTileValue.FromString("Road works")),
            new("description_2", VectorTileValue.FromString("Road works")),
            new("magnitude", VectorTileValue.FromInt(2)),
            new("delay", VectorTileValue.FromInt(120)),
            new("title", VectorTileValue.FromString("Test road")),
            new("incidentType", VectorTileValue.FromString("Construction")),
            new("startTime", VectorTileValue.FromString("2026-09-24T14:54:00-07:00")),
            new("endTime", VectorTileValue.FromString("2026-09-25T02:34:00.0000000Z")),
        ], [], []);
        var args = MapControl.CreateIncidentEventArgs(feature, location, new(100, 200));
        Assert.AreEqual("fixture", args.Id);
        Assert.AreEqual(9, args.Category);
        Assert.AreEqual(2, args.Magnitude);
        Assert.AreEqual("Lane closed; Road works", args.Description);
        Assert.AreEqual(TimeSpan.FromMinutes(2), args.Delay);
        Assert.AreEqual("Test road", args.Title);
        Assert.AreEqual("Construction", args.IncidentType);
        Assert.AreEqual(new DateTimeOffset(2026, 9, 24, 21, 54, 0, TimeSpan.Zero), args.StartTime);
        Assert.AreEqual(new DateTimeOffset(2026, 9, 25, 2, 34, 0, TimeSpan.Zero), args.EndTime);
        Assert.AreEqual(new Windows.Foundation.Point(100, 200), args.Position);
        Assert.AreSame(location, args.Location);
    }

    [TestMethod]
    [DataRow(-1d)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.MaxValue)]
    public void IncidentDetailsDoNotInventMissingOrInvalidValues(double delay)
    {
        VectorTileFeature feature = new("incident", VectorTileGeometryType.Point, [], [
            new("delay", VectorTileValue.FromDouble(delay)),
            new("icon_category", VectorTileValue.FromDouble(1.5)),
            new("description", VectorTileValue.FromInt(1)),
        ], [], []);
        var args = MapControl.CreateIncidentEventArgs(feature,
            new Windows.Devices.Geolocation.Geopoint(new()));
        Assert.IsNull(args.Delay);
        Assert.IsNull(args.Category);
        Assert.IsNull(args.Description);
        Assert.IsNull(args.Magnitude);
        Assert.IsNull(args.Id);
        Assert.IsNull(args.Title);
        Assert.IsNull(args.IncidentType);
        Assert.IsNull(args.StartTime);
        Assert.IsNull(args.EndTime);
    }

    [TestMethod]
    [DataRow("2026-09-24", false)]
    [DataRow("2026-09-24T14:54:00", false)]
    [DataRow("invalid", false)]
    [DataRow("1790286840000", false)]
    [DataRow("2026-09-24T14:54:00Z", true)]
    [DataRow("2026-09-24T14:54:00.123+02:00", true)]
    public void IncidentTimesRequireExplicitIsoTimeZones(string value, bool valid)
    {
        VectorTileFeature feature = new("incident", VectorTileGeometryType.Point, [], [
            new("start_time", VectorTileValue.FromString(value)),
            new("end_time", VectorTileValue.FromString(value)),
        ], [], []);
        var args = MapControl.CreateIncidentEventArgs(feature, new Windows.Devices.Geolocation.Geopoint(new()));
        Assert.AreEqual(valid, args.StartTime.HasValue);
        Assert.AreEqual(valid, args.EndTime.HasValue);
        if (valid) Assert.AreEqual(TimeSpan.Zero, args.StartTime!.Value.Offset);
    }

    [TestMethod]
    public void IncidentIndexNarrowPhasePreservesPointsAndRoadSegments()
    {
        VectorTileFeature point = new("poi", VectorTileGeometryType.Point, [new(0.2, 0.3)], [], [], []);
        VectorTileFeature line = new("road", VectorTileGeometryType.LineString, [], [],
            [new([new(0.6, 0.1), new(0.8, 0.9)])], []);
        IncidentSpatialIndex index = new(new([point, line]));
        Assert.AreSequenceEqual([point], index.Query(0.19, 0.29, 0.21, 0.31).ToArray());
        Assert.AreSequenceEqual([line], index.Query(0.65, 0.4, 0.75, 0.5).ToArray());
        Assert.AreEqual(0, index.Query(0.9, 0.9, 1, 1).Count());
        Assert.AreEqual(4d, MapRenderer.IncidentSegmentDistanceSquared(5, 2, new(0, 0), new(10, 0)));
        Assert.AreEqual(25d, MapRenderer.IncidentSegmentDistanceSquared(3, 4, new(0, 0), new(0, 0)));
    }
}
