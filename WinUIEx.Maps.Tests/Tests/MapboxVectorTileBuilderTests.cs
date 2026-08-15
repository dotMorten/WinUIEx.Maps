using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class MapboxVectorTileBuilderTests
{
    [TestMethod]
    public void BuilderCreatesPointLinePolygonAndTypedProperties()
    {
        byte[] tile = new MapboxVectorTileBuilder()
            .AddPoint(
                "points",
                2048,
                1024,
                new Dictionary<string, object>
                {
                    ["name"] = "marker",
                    ["rank"] = 7,
                    ["visible"] = true,
                })
            .AddLine(
                "lines",
                [new(100, 200), new(300, 400)])
            .AddPolygon(
                "polygons",
                [[new(10, 10), new(200, 10), new(10, 200)]])
            .Build();

        VectorTileFeatureCollection features = VectorTileDecoder.Decode(tile);
        VectorTileFeature point = Assert.ContainsSingle(
            features.GetSourceLayer("points"));
        VectorTileFeature line = Assert.ContainsSingle(
            features.GetSourceLayer("lines"));
        VectorTileFeature polygon = Assert.ContainsSingle(
            features.GetSourceLayer("polygons"));

        Assert.AreEqual(VectorTileGeometryType.Point, point.GeometryType);
        Assert.AreEqual(0.5, point.Points[0].X, 0.000001);
        Assert.AreEqual(0.25, point.Points[0].Y, 0.000001);
        Assert.IsTrue(point.TryGetProperty("name", out VectorTileValue name));
        Assert.AreEqual("marker", name.StringValue);
        Assert.IsTrue(point.TryGetProperty("rank", out VectorTileValue rank));
        Assert.IsTrue(rank.TryGetNumber(out double numericRank));
        Assert.AreEqual(7, numericRank);
        Assert.IsTrue(point.TryGetProperty(
            "visible",
            out VectorTileValue visible));
        Assert.IsTrue(visible.BoolValue);
        Assert.ContainsSingle(line.Lines);
        Assert.ContainsSingle(polygon.Polygons);
    }
}
