using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class VectorMemoryOwnershipTests
{
    [TestMethod]
    public void DecoderOwnershipFastPathMatchesReferenceDeduplication()
    {
        MapboxVectorTileBuilder builder = new();
        for (int i = 0; i < 100; i++)
        {
            builder.AddLine("road", [new(0, i * 32), new(4096, i * 32)],
                new Dictionary<string, object> { ["name"] = "shared name" });
            builder.AddPolygon("area", [[new(0, 0), new(4096, 0), new(4096, 4096), new(0, 4096)]]);
        }
        var decoded = VectorTileDecoder.Decode(builder.Build());
        var deduplicated = new VectorTileFeatureCollection(decoded.Features);
        Assert.AreEqual(deduplicated.RetainedByteSize, decoded.RetainedByteSize);
    }

    [TestMethod]
    public void AccurateChargingEvictsHistoryButPreservesCoverageAboveSoftCap()
    {
        VectorTileFeature[] retained = new VectorTileFeature[2300];
        for (int i = 0; i < retained.Length; i++)
            retained[i] = new("road", VectorTileGeometryType.Point, [new(0.5, 0.5)],
                [new("name", VectorTileValue.FromString(new string('x', 8192) + i))], [], []);
        VectorTileFeatureCollection heavy = new(retained);
        Assert.IsGreaterThan(32L * 1024 * 1024, heavy.RetainedByteSize);
        Assert.IsLessThan(1024L * 1024, heavy.ByteSize);
        var style = VectorStyleAssets.CreateForTest(MapStyle.Road,
            """{"version":8,"layers":[]}"""u8.ToArray(), "{}"u8.ToArray(), [0, 0, 0, 0], 1, 1);
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(512, 256);
        renderer.SetCameraTargetImmediately(0, 0, 14, 512, 256);
        MapScene scene = MapCamera.CreateScene(0, 0, 14, 14, 512, 256);
        renderer.SetLayerRenderPlan([new(LayerRenderKind.VectorPoints, 0, 1, true, 1,
            TimeSpan.Zero, 0, 24, 0, 256)]);
        renderer.ActivateRasterTileSet(1, 1, 1, scene, _ => true,
            RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        RasterTileKey far = new(1, new(14, 100, 100));
        renderer.AddVectorTileForBenchmark(far, heavy, style);
        renderer.AddVectorTileForBenchmark(new(1, scene.RequiredTiles[0]), new([]), style);
        using RenderingEventListener events = new("VectorCacheOwnership");
        renderer.RenderOffscreenFrameForBenchmark();
        Assert.AreEqual(1, Convert.ToInt32(events.Events("VectorCacheOwnership")[^1].Payload[5]));
        // Re-adding proves eviction rather than merely omitting the tile from diagnostics.
        renderer.AddVectorTileForBenchmark(far, heavy, style);
        renderer.AddVectorTileForBenchmark(new(1, scene.RequiredTiles[1]), heavy, style);
        renderer.RenderOffscreenFrameForBenchmark();
        var last = events.Events("VectorCacheOwnership")[^1];
        Assert.AreEqual(2, Convert.ToInt32(last.Payload[5]));
        Assert.IsGreaterThan(32L * 1024 * 1024, Convert.ToInt64(last.Payload[2]) + Convert.ToInt64(last.Payload[3]));
        renderer.AddVectorTileForBenchmark(far, heavy, style);
    }

    [TestMethod]
    public void SharedDecodedGeometryIsChargedOnceAtItsOwner()
    {
        VectorTilePoint[] points = [new(0, 0), new(1, 1)];
        VectorTileLine line = new(points);
        VectorTileFeature feature = new("road", VectorTileGeometryType.LineString, [], [], [line], []);
        VectorTileFeatureCollection single = new([feature]);
        VectorTileFeatureCollection shared = new([feature, feature]);
        // Only the feature array and the source-layer index grow, not the feature/points.
        Assert.AreEqual(2L * IntPtr.Size, shared.RetainedByteSize - single.RetainedByteSize);
        Assert.IsGreaterThan(single.ByteSize, single.RetainedByteSize);
    }

    [TestMethod]
    public void DerivedResolutionAndProjectionAreChargedWithoutGeometryDuplication()
    {
        byte[] bytes = new MapboxVectorTileBuilder()
            .AddLine("road", [new(0, 2048), new(4096, 2048)])
            .Build();
        var source = TestVectorTileSource.Create(new(14, 4823, 6160), bytes,
            """
            {"version":8,"layers":[{"type":"line","source-layer":"road",
              "paint":{"line-width":["interpolate",["linear"],["zoom"],14,2,15,6]}}]}
            """, "{}", [0, 0, 0, 0], 1, 1);
        var features = VectorTileDecoder.Decode(bytes);
        var cache = new MapRenderer.VectorTileCacheEntry(features, source.StyleAssets, 0);
        long decoded = cache.DecodedByteSize;
        long initial = cache.ByteSize;
        var first = cache.GetLines(14.25);
        long resolved = cache.ByteSize;
        Assert.IsGreaterThan(initial, resolved);
        Assert.IsLessThan(1024L, resolved - initial);
        Assert.AreSame(features.Features[0].Lines[0].Points, first.Lines[0].Points);
        double width = first.Lines[0].Style.Width;
        var next = cache.GetLines(14.5);
        Assert.AreNotSame(first.Lines, next.Lines);
        Assert.AreEqual(width, first.Lines[0].Style.Width);
        Assert.AreEqual(resolved, cache.ByteSize, "Replacement must subtract obsolete resolution storage.");
        Assert.AreEqual(decoded, cache.DecodedByteSize, "Event 83's decoded estimate is unchanged.");

        VectorTileSymbol[] symbols =
        [
            new(0, 0, 0, 0, 1, 1, 0, 0, LinePoints: features.Features[0].Lines[0].Points),
            new(0, 0, 0, 0, 1, 1, 0, 0, LinePoints: features.Features[0].Lines[0].Points),
        ];
        var projection = cache.GetSymbolProjection(symbols);
        Assert.AreEqual(2L * sizeof(int), projection.IndexBytes);
        Assert.AreEqual(resolved + projection.RetainedByteSize, cache.ByteSize);
        Assert.AreSame(projection, cache.GetSymbolProjection(symbols));
    }

    [TestMethod]
    public void MembershipPreservesFailuresAndFractionalFilterVisibility()
    {
        byte[] bytes = new MapboxVectorTileBuilder()
            .AddLine("road", [new(0, 2048), new(4096, 2048)]).Build();
        var source = TestVectorTileSource.Create(new(14, 4823, 6160), bytes,
            """
            {"version":8,"layers":[
              {"type":"line","source-layer":"road","filter":["==",["get","missing"],true]},
              {"type":"line","source-layer":"road","filter":["step",["zoom"],true,14.5,false],
                "paint":{"line-width":["+",["zoom"],1]}},
              {"type":"line","source-layer":"road","minzoom":14.25,"maxzoom":14.75,
                "paint":{"line-width":["+",["zoom"],1]}}]}
            """, "{}", [0, 0, 0, 0], 1, 1);
        var features = VectorTileDecoder.Decode(bytes);
        VectorGeometryMembership membership = new();
        foreach (double zoom in new[] { 14, 14.25, 14.49, 14.5, 14.75, 14.3 })
        {
            var expected = source.StyleAssets.ResolveLines(features, zoom);
            var actual = source.StyleAssets.ResolveLines(features, zoom, membership);
            Assert.AreEqual(expected.EvaluationFailureCount, actual.EvaluationFailureCount);
            Assert.AreSequenceEqual(expected.Lines, actual.Lines);
        }
        Assert.IsGreaterThan(0L, membership.ByteSize);
    }
}
