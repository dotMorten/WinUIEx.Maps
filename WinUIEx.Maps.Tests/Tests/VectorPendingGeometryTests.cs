using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

[TestClass]
[DoNotParallelize]
public sealed class VectorPendingGeometryTests
{
    [TestMethod]
    [DataRow(0d, true)]
    [DataRow(45d, true)]
    [DataRow(0d, false)]
    public void RasterEvictionPreservesVectorFallbackUntilReplacementArrives(
        double pitch, bool azureRaster)
    {
        TileId fine = new(14, 4823, 6160);
        TileId coarse = new(fine.Zoom - 1, fine.X >> 1, fine.Y >> 1);
        byte[] bytes = CreateTile();
        var source = TestVectorTileSource.Create(fine, bytes, Style, "{}", [0, 0, 0, 0], 1, 1);
        var features = VectorTileDecoder.Decode(bytes);
        using MapRenderer renderer = CreateRenderer(source, fine, 512, pitch);
        var center = source.TileCenter;
        renderer.SetLayerRenderPlan([
            new(LayerRenderKind.VectorPoints, 0, 1, true, 0.7, TimeSpan.Zero, 0, 24, 0, 256,
                Style: (int)MapStyle.Road),
            new(LayerRenderKind.RasterTiles, 1, 2, true, 1, TimeSpan.Zero, 0, 24, 0, 256),
        ]);
        renderer.ActivateRasterTileSet(1, 1, 2,
            MapCamera.CreateScene(center.Longitude, center.Latitude, fine.Zoom, coarse.Zoom, 512, 256),
            _ => true, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        renderer.AddVectorTileForBenchmark(new(1, coarse), features, source.StyleAssets);
        renderer.RenderOffscreenFrameForBenchmark();
        MapScene scene = MapCamera.CreateScene(
            center.Longitude, center.Latitude, fine.Zoom, fine.Zoom, 512, 256);
        renderer.ActivateRasterTileSet(1, 2, 3, scene,
            _ => true, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        renderer.ActivateRasterTileSet(2, 1, 1, scene,
            _ => true, azureRaster ? RasterSourceKind.Azure : RasterSourceKind.Custom,
            LayerRenderKind.RasterTiles, false);
        MapRenderFrame expected = renderer.CaptureOffscreenFrameForBenchmark();

        // Offscreen raster history exceeds the unchanged 32 MiB minimum cache budget.
        byte[] pixels = new byte[1024 * 1024 * 4];
        for (int i = 0; i < 9; i++)
            renderer.AddRasterTileForBenchmark(new(2, new(fine.Zoom, i, 0)), pixels, 1024, 1024);
        using RenderingEventListener events = new(
            "TileCacheEvicted", "VectorPolygonRenderBatch", "VectorLineRenderBatch");
        AssertPixelsEqual(expected, renderer.CaptureOffscreenFrameForBenchmark());
        Assert.IsNotEmpty(events.Events("TileCacheEvicted"), "The reproduction must actually evict raster tiles.");
        for (int i = 0; i < 3; i++)
            AssertPixelsEqual(expected, renderer.CaptureOffscreenFrameForBenchmark());
        Assert.IsTrue(events.Events("VectorPolygonRenderBatch").All(e => Convert.ToInt32(e.Payload[2]) > 0));
        Assert.IsTrue(events.Events("VectorLineRenderBatch").All(e => Convert.ToInt32(e.Payload[2]) > 0));

        renderer.AddVectorTileForBenchmark(new(1, fine), features, source.StyleAssets);
        using MapRenderer reference = CreateRenderer(source, fine, 512, pitch);
        reference.AddVectorTileForBenchmark(new(1, coarse), features, source.StyleAssets);
        reference.AddVectorTileForBenchmark(new(1, fine), features, source.StyleAssets);
        reference.ActivateRasterTileSet(1, 2, 3, scene,
            _ => true, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        AssertPixelsEqual(reference.CaptureOffscreenFrameForBenchmark(), renderer.CaptureOffscreenFrameForBenchmark());
    }

    [TestMethod]
    [DataRow(0d)]
    [DataRow(45d)]
    public void PolygonBoundaryDoesNotStrokeBufferedTileClosure(double pitch)
    {
        TileId tile = new(14, 4823, 6160);
        const string style = """
            {"version":8,"layers":[{"type":"line","source-layer":"area",
              "paint":{"line-color":"#246824","line-width":4}}]}
            """;
        byte[] polygon = new MapboxVectorTileBuilder().AddPolygon("area",
            [[new(-600,-600), new(4696,-600), new(4696,3072), new(-600,3072)]]).Build();
        byte[] line = new MapboxVectorTileBuilder().AddLine("area",
            [new(4096,3072), new(0,3072)]).Build();
        var source = TestVectorTileSource.Create(tile, polygon, style, "{}", [0, 0, 0, 0], 1, 1);
        using MapRenderer actual = CreateRenderer(source, tile, 512, pitch);
        using MapRenderer reference = CreateRenderer(source, tile, 512, pitch);
        actual.AddVectorTileForBenchmark(new(1, tile), VectorTileDecoder.Decode(polygon), source.StyleAssets);
        reference.AddVectorTileForBenchmark(new(1, tile), VectorTileDecoder.Decode(line), source.StyleAssets);
        AssertPixelsEqual(reference.CaptureOffscreenFrameForBenchmark(), actual.CaptureOffscreenFrameForBenchmark());
    }

    [TestMethod]
    [DataRow(0d)]
    [DataRow(45d)]
    public void PolygonBoundaryPixelsMatchAnExplicitClosedLine(double pitch)
    {
        TileId tile = new(14, 4823, 6160);
        const string style = """
            {"version":8,"layers":[{"type":"line","source-layer":"area",
              "layout":{"line-join":"miter","line-cap":"round"},
              "paint":{"line-color":"#246824","line-width":4}}]}
            """;
        byte[] polygon = new MapboxVectorTileBuilder().AddPolygon("area",
            [[new(512,512), new(3584,512), new(3584,3584), new(512,3584)]]).Build();
        byte[] line = new MapboxVectorTileBuilder().AddLine("area",
            [new(512,512), new(3584,512), new(3584,3584), new(512,3584), new(512,512)]).Build();
        var source = TestVectorTileSource.Create(tile, polygon, style, "{}", [0, 0, 0, 0], 1, 1);
        using MapRenderer actual = CreateRenderer(source, tile, 512, pitch);
        using MapRenderer reference = CreateRenderer(source, tile, 512, pitch);
        actual.AddVectorTileForBenchmark(new(1, tile), VectorTileDecoder.Decode(polygon), source.StyleAssets);
        reference.AddVectorTileForBenchmark(new(1, tile), VectorTileDecoder.Decode(line), source.StyleAssets);
        using RenderingEventListener events = new("VectorLineRenderBatch");
        AssertPixelsEqual(reference.CaptureOffscreenFrameForBenchmark(), actual.CaptureOffscreenFrameForBenchmark());
        Assert.IsTrue(events.Events("VectorLineRenderBatch").All(e => Convert.ToInt32(e.Payload[2]) == 1));
    }

    private const string Style = """
        {"version":8,"layers":[
          {"type":"fill","source-layer":"area","paint":{"fill-color":"#408080","fill-outline-color":"#202020"}},
          {"type":"line","source-layer":"road","paint":{"line-color":"#c04080","line-width":4,"line-gap-width":2}},
          {"type":"line","source-layer":"road","paint":{"line-color":"#8040c0","line-width":2}}]}
        """;

    [TestMethod]
    public void PendingBudgetOverflowStreamsWithoutDroppingContent()
    {
        TileId first = new(14, 4823, 6160);
        TileId second = new(first.Zoom, first.X + 1, first.Y);
        byte[] bytes = CreateTile();
        var source = TestVectorTileSource.Create(first, bytes, Style, "{}", [0, 0, 0, 0], 1, 1);
        var firstFeatures = VectorTileDecoder.Decode(bytes);
        MapboxVectorTileBuilder dense = new();
        for (int i = 0; i < 64; i++)
            dense.AddLine("road", [new(0, i * 64), new(4096, i * 64)]);
        var secondFeatures = VectorTileDecoder.Decode(dense.Build());
        using MapRenderer actual = CreateRenderer(source, first, 512, 0);
        using MapRenderer reference = CreateRenderer(source, first, 512, 0);
        foreach (var renderer in new[] { actual, reference })
            renderer.AddVectorTileForBenchmark(new(1, first), firstFeatures, source.StyleAssets);
        actual.RenderOffscreenFrameForBenchmark();
        reference.AddVectorTileForBenchmark(new(1, second), secondFeatures, source.StyleAssets);
        var expected = reference.CaptureOffscreenFrameForBenchmark();
        using ManualResetEventSlim release = new();
        actual.VectorGeometryPreparationStartingForTest = () =>
        {
            if (!release.Wait(TimeSpan.FromSeconds(20)))
                throw new TimeoutException();
        };
        try
        {
            actual.AddVectorTileForBenchmark(new(1, second), secondFeatures, source.StyleAssets);
            using RenderingEventListener events = new("VectorPendingGeometry", "GeometryStreamUploadTiming");
            AssertPixelsEqual(expected, actual.CaptureOffscreenFrameForBenchmark());
            var pending = events.Events("VectorPendingGeometry");
            Assert.IsNotEmpty(pending);
            Assert.IsTrue(pending.All(e => Convert.ToInt32(e.Payload[2]) == 0));
            Assert.IsTrue(events.Events("GeometryStreamUploadTiming").Any(e => Convert.ToInt32(e.Payload[2]) > 0));
        }
        finally
        {
            release.Set();
            Assert.IsTrue(SpinWait.SpinUntil(() => actual.ActiveVectorGeometryPreparations == 0, TimeSpan.FromSeconds(5)));
        }
    }

    [TestMethod]
    public async Task NewlyArrivedPatternStaysOnCompletePolygonPath()
    {
        TileId first = new(14, 4823, 6160);
        TileId second = new(first.Zoom, first.X + 1, first.Y);
        byte[] solid = CreateTile();
        byte[] patterned = new MapboxVectorTileBuilder()
            .AddPolygon("pattern", [[new(0, 0), new(4096, 0), new(4096, 4096), new(0, 4096)]]).Build();
        var source = TestVectorTileSource.Create(first, solid,
            """
            {"version":8,"layers":[
              {"type":"fill","source-layer":"area","paint":{"fill-color":"#408080"}},
              {"type":"fill","source-layer":"pattern","paint":{"fill-pattern":"checker"}}]}
            """,
            """{"checker":{"x":0,"y":0,"width":1,"height":1,"pixelRatio":1}}""",
            [20, 80, 200, 255], 1, 1);
        var firstFeatures = VectorTileDecoder.Decode(solid);
        var secondFeatures = VectorTileDecoder.Decode(patterned);
        var textures = await source.StyleAssets.PrepareTexturesAsync(secondFeatures, first.Zoom, CancellationToken.None);
        using MapRenderer actual = CreateRenderer(source, first, 512, 0);
        using MapRenderer reference = CreateRenderer(source, first, 512, 0);
        foreach (var renderer in new[] { actual, reference })
        {
            renderer.AddVectorTexturesForBenchmark(textures);
            renderer.AddVectorTileForBenchmark(new(1, first), firstFeatures, source.StyleAssets);
        }
        actual.RenderOffscreenFrameForBenchmark();
        actual.AddVectorTileForBenchmark(new(1, second), secondFeatures, source.StyleAssets);
        reference.AddVectorTileForBenchmark(new(1, second), secondFeatures, source.StyleAssets);
        using RenderingEventListener events = new("VectorPolygonDecorationSummary");
        AssertPixelsEqual(reference.CaptureOffscreenFrameForBenchmark(), actual.CaptureOffscreenFrameForBenchmark());
        Assert.IsTrue(events.Events("VectorPolygonDecorationSummary").All(e => Convert.ToInt32(e.Payload[1]) > 0));
    }

    [TestMethod]
    [DataRow(0d, false)]
    [DataRow(45d, false)]
    [DataRow(0d, true)]
    public void PendingContentMatchesCompleteSceneAndReusesBuffers(double pitch, bool wrapped)
    {
        TileId first = wrapped ? new(1, 0, 0) : new(14, 4823, 6160);
        TileId second = new(first.Zoom, first.X + 1, first.Y);
        int width = wrapped ? 1024 : 512;
        byte[] bytes = CreateTile();
        var source = TestVectorTileSource.Create(first, bytes, Style, "{}", [0, 0, 0, 0], 1, 1);
        var features = VectorTileDecoder.Decode(bytes);
        using MapRenderer actual = CreateRenderer(source, first, width, pitch);
        using MapRenderer reference = CreateRenderer(source, first, width, pitch);
        foreach (var renderer in new[] { actual, reference })
            renderer.AddVectorTileForBenchmark(new(1, first), features, source.StyleAssets);
        actual.RenderOffscreenFrameForBenchmark();
        reference.AddVectorTileForBenchmark(new(1, second), features, source.StyleAssets);
        MapRenderFrame expected = reference.CaptureOffscreenFrameForBenchmark();
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim release = new();
        actual.VectorGeometryPreparationStartingForTest = () =>
        {
            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20)))
                throw new TimeoutException();
        };
        try
        {
            actual.AddVectorTileForBenchmark(new(1, second), features, source.StyleAssets);
            MapRenderFrame firstPending = actual.CaptureOffscreenFrameForBenchmark();
            Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
            AssertPixelsEqual(expected, firstPending);
            using RenderingEventListener events = new("VectorPendingGeometry", "GeometryStreamUploadTiming");
            long before = MapRenderer.NativeGeometryBuffer.AllocatedBytes;
            MapRenderFrame repeated = actual.CaptureOffscreenFrameForBenchmark();
            AssertPixelsEqual(firstPending, repeated);
            Assert.AreEqual(before, MapRenderer.NativeGeometryBuffer.AllocatedBytes);
            var reuse = events.Events("VectorPendingGeometry");
            Assert.IsTrue(reuse.Any(e => Convert.ToInt32(e.Payload[0]) == 1 && Convert.ToInt32(e.Payload[1]) == 1));
            Assert.IsTrue(reuse.Any(e => Convert.ToInt32(e.Payload[0]) == 2 && Convert.ToInt32(e.Payload[1]) == 1));

            // An unrelated source arrival must not invalidate already retained pending content.
            actual.ActivateRasterTileSet(2, 1, 1,
                MapCamera.CreateScene(source.TileCenter.Longitude, source.TileCenter.Latitude,
                    first.Zoom, first.Zoom, width, 256),
                _ => true, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
            actual.AddVectorTileForBenchmark(new(2, first), features, source.StyleAssets);
            before = MapRenderer.NativeGeometryBuffer.AllocatedBytes;
            AssertPixelsEqual(firstPending, actual.CaptureOffscreenFrameForBenchmark());
            Assert.AreEqual(before, MapRenderer.NativeGeometryBuffer.AllocatedBytes);
            actual.RemoveRasterTileSource(2);
            AssertPixelsEqual(firstPending, actual.CaptureOffscreenFrameForBenchmark());
            Assert.AreEqual(before, MapRenderer.NativeGeometryBuffer.AllocatedBytes);
            var moved = MapCamera.LocationAtOffset(source.TileCenter.Longitude, source.TileCenter.Latitude,
                first.Zoom, 5, 7, 0, pitch, 256);
            actual.SetCameraTargetImmediately(moved.Longitude, moved.Latitude, first.Zoom, width, 256, 0, pitch);
            reference.SetCameraTargetImmediately(moved.Longitude, moved.Latitude, first.Zoom, width, 256, 0, pitch);
            AssertPixelsEqual(reference.CaptureOffscreenFrameForBenchmark(), actual.CaptureOffscreenFrameForBenchmark());
            Assert.AreEqual(before, MapRenderer.NativeGeometryBuffer.AllocatedBytes);
        }
        finally
        {
            release.Set();
            Assert.IsTrue(SpinWait.SpinUntil(() => actual.ActiveVectorGeometryPreparations == 0, TimeSpan.FromSeconds(5)));
        }
    }

    [TestMethod]
    public void PendingFadeProgressesWithoutRebuildingGeometry()
    {
        TileId first = new(14, 4823, 6160);
        TileId second = new(first.Zoom, first.X + 1, first.Y);
        byte[] bytes = CreateTile();
        var source = TestVectorTileSource.Create(first, bytes, Style, "{}", [0, 0, 0, 0], 1, 1);
        var features = VectorTileDecoder.Decode(bytes);
        using MapRenderer renderer = CreateRenderer(source, first, 512, 0, TimeSpan.FromSeconds(10));
        renderer.AddVectorTileForBenchmark(new(1, first), features, source.StyleAssets);
        renderer.SetVectorTileAgeForBenchmark(new(1, first), TimeSpan.FromMinutes(1));
        renderer.RenderOffscreenFrameForBenchmark();
        renderer.AddVectorTileForBenchmark(new(1, second), features, source.StyleAssets);
        renderer.SetVectorTileAgeForBenchmark(new(1, second), TimeSpan.FromSeconds(2));
        var early = renderer.CaptureOffscreenFrameForBenchmark();
        long before = MapRenderer.NativeGeometryBuffer.AllocatedBytes;
        renderer.SetVectorTileAgeForBenchmark(new(1, second), TimeSpan.FromSeconds(7));
        using RenderingEventListener events = new("VectorPendingGeometry", "GeometryStreamUploadTiming",
            "VectorCacheOwnership", "VectorRetainedMemory");
        var later = renderer.CaptureOffscreenFrameForBenchmark();
        Assert.AreEqual(before, MapRenderer.NativeGeometryBuffer.AllocatedBytes);
        Assert.IsFalse(early.Pixels.Span.SequenceEqual(later.Pixels.Span), "Fade must not be baked into retained geometry.");
        Assert.AreEqual(0, renderer.ActiveVectorGeometryPreparations, "Fading coverage must not be captured by a worker.");
        Assert.IsTrue(events.Events("GeometryStreamUploadTiming").All(e => Convert.ToInt32(e.Payload[2]) == 0));
        long pendingBytes = Convert.ToInt64(events.Events("VectorCacheOwnership")[^1].Payload[4]);
        long mainBytes = Convert.ToInt64(events.Events("VectorRetainedMemory")[^1].Payload[5]);
        Assert.IsGreaterThan(0L, pendingBytes);
        Assert.IsTrue(pendingBytes <= mainBytes, "Pending retention must not exceed its owner capacity.");
        renderer.SetLayerRenderPlan([]);
        renderer.RenderOffscreenFrameForBenchmark();
        Assert.AreEqual(0L, Convert.ToInt64(events.Events("VectorCacheOwnership")[^1].Payload[4]));
    }

    private static byte[] CreateTile() => new MapboxVectorTileBuilder()
        .AddPolygon("area", [[new(0, 0), new(4096, 0), new(4096, 4096), new(0, 4096)]])
        .AddLine("road", [new(0, 800), new(2048, 2048), new(4096, 3200)])
        .Build();

    private static MapRenderer CreateRenderer(TestVectorTileSource source, TileId first,
        int width, double pitch, TimeSpan fade = default)
    {
        MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(width, 256);
        var center = source.TileCenter;
        renderer.SetCameraTargetImmediately(center.Longitude, center.Latitude, first.Zoom, width, 256, 0, pitch);
        renderer.SetLayerRenderPlan([new(LayerRenderKind.VectorPoints, 0, 1, true, 0.7,
            fade, 0, 24, 0, 256, Style: (int)MapStyle.Road)]);
        renderer.ActivateRasterTileSet(1, 1, 1,
            MapCamera.CreateScene(center.Longitude, center.Latitude, first.Zoom, first.Zoom, width, 256),
            _ => true, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        return renderer;
    }

    private static void AssertPixelsEqual(MapRenderFrame expected, MapRenderFrame actual)
    {
        Assert.AreEqual(expected.Width, actual.Width);
        Assert.AreEqual(expected.Height, actual.Height);
        int different = 0;
        for (int i = 0; i < expected.Pixels.Length; i++)
            if (Math.Abs(expected.Pixels.Span[i] - actual.Pixels.Span[i]) > 2)
                different++;
        Assert.AreEqual(0, different, "Pending and complete scenes must agree within rounding tolerance.");
    }
}
