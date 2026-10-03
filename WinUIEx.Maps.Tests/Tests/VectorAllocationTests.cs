using System.Diagnostics;
using System.Numerics;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

[TestClass]
[DoNotParallelize]
public sealed class VectorAllocationTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string Style = """
        {"version":8,"layers":[
          {"type":"fill","source-layer":"area","paint":{"fill-color":"#80a060"}},
          {"type":"line","source-layer":"road","paint":{"line-color":"#604080",
          "line-width":["interpolate",["linear"],["zoom"],10,2,18,8]}}]}
        """;

    [TestMethod]
    public void ZoomingReusesUnchangedTextLayersWithoutChangingSymbols()
    {
        var (assets, features) = CreateMixedTextLayers();
        VectorStyleAssets.TextResolutionCache cache = new();
        foreach (double zoom in new[] { 14d, 14.1, 14.2, 15.1, 14.2, 13d, 14.2 })
        {
            var expected = assets.ResolveSymbols(features, zoom);
            var actual = assets.ResolveSymbols(features, zoom, textCache: cache);
            AssertSameSymbols(expected.Symbols, actual.Symbols);
            Assert.AreEqual(expected.ResolvedGlyphCount, actual.ResolvedGlyphCount);
            Assert.AreEqual(expected.EvaluationFailureCount, actual.EvaluationFailureCount);
        }
        Assert.IsLessThan(14, cache.BuildCount, "The static layer must not be rebuilt at every fractional zoom.");
        Assert.IsGreaterThan(0L, cache.ByteSize);
        var scaled = assets.ResolveSymbols(features, 14.2, 1.5, cache);
        AssertSameSymbols(assets.ResolveSymbols(features, 14.2, 1.5).Symbols, scaled.Symbols);
        assets.ResolveSymbols(features, 12, textCache: cache);
        Assert.AreEqual(0L, cache.ByteSize, "Hidden text layers must release retained symbols.");
    }

    private static void AssertSameSymbols(VectorTileSymbol[] expected, VectorTileSymbol[] actual)
    {
        Assert.HasCount(expected.Length, actual);
        for (int i = 0; i < expected.Length; i++)
        {
            var e = expected[i];
            var a = actual[i];
            Assert.AreEqual(e.Width, a.Width, 1e-10);
            Assert.AreEqual(e.Height, a.Height, 1e-10);
            Assert.AreEqual(e.OffsetX, a.OffsetX, 1e-10);
            Assert.AreEqual(e.OffsetY, a.OffsetY, 1e-10);
            Assert.AreEqual(e.Paint.HaloOffset, a.Paint.HaloOffset, 1e-10);
            Assert.AreEqual(e.Paint.HaloBlur, a.Paint.HaloBlur, 1e-10);
            Assert.AreEqual(e with { Width = a.Width, Height = a.Height, OffsetX = a.OffsetX,
                OffsetY = a.OffsetY, Paint = e.Paint with { HaloOffset = a.Paint.HaloOffset, HaloBlur = a.Paint.HaloBlur } }, a);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TextResizePreservesWrappingAnchorsHalosAndPlacement(bool line)
    {
        byte[] bytes = line
            ? new MapboxVectorTileBuilder().AddLine("poi", [new(0, 1000), new(4096, 2000)]).Build()
            : new MapboxVectorTileBuilder().AddPoint("poi", 2048, 2048).Build();
        var source = TestVectorTileSource.Create(new(14, 4823, 6160), bytes, $$$"""
            {"version":8,"layers":[{"type":"symbol","source-layer":"poi",
             "layout":{"symbol-placement":"{{{(line ? "line" : "point")}}}","text-field":"AAAA AAA\nAA",
                "text-font":["Test"],"text-size":["interpolate",["linear"],["zoom"],10,4,18,40],
                "text-max-width":3,"text-anchor":"bottom-right","text-offset":[1,2],
                "text-radial-offset":0.5,"text-letter-spacing":0.1,"text-line-height":1.4,
                "symbol-spacing":70,"text-padding":7},
             "paint":{"text-color":"#123456","text-halo-color":"#fff","text-halo-width":10,"text-halo-blur":4}}]}
            """, "{}", [0, 0, 0, 0], 1, 1);
        source.AddGlyphs("Test", TestGlyph.RectangleSdf('A'), TestGlyph.RectangleSdf(' '));
        var features = VectorTileDecoder.Decode(bytes);
        VectorStyleAssets.TextResolutionCache cache = new();
        foreach (var zoom in new[] { 10d, 14.25, 17.9, 10.01 })
            AssertSameSymbols(source.StyleAssets.ResolveSymbols(features, zoom).Symbols,
                source.StyleAssets.ResolveSymbols(features, zoom, textCache: cache).Symbols);
        Assert.AreEqual(1, cache.BuildCount, "Uniform size changes should only transform the original glyph layout.");
    }

    [TestMethod]
    public void TextCacheRetriesMissingGlyphs()
    {
        byte[] bytes = new MapboxVectorTileBuilder().AddPoint("poi", 2048, 2048).Build();
        var source = TestVectorTileSource.Create(new(14, 4823, 6160), bytes,
            """{"version":8,"layers":[{"type":"symbol","source-layer":"poi","layout":{"text-field":"A","text-font":["Test"]}}]}""",
            "{}", [0, 0, 0, 0], 1, 1);
        var features = VectorTileDecoder.Decode(bytes);
        VectorStyleAssets.TextResolutionCache cache = new();
        Assert.AreEqual(1, source.StyleAssets.ResolveSymbols(features, 14, textCache: cache).UnavailableGlyphCount);
        Assert.AreEqual(1, source.StyleAssets.ResolveSymbols(features, 14.1, textCache: cache).UnavailableGlyphCount);
        Assert.AreEqual(1, cache.BuildCount, "A missing glyph should not rebuild a layer before its atlas changes.");
        source.AddGlyphs("Test", TestGlyph.RectangleSdf('A'));
        var result = source.StyleAssets.ResolveSymbols(features, 14.1, textCache: cache);
        Assert.AreEqual(0, result.UnavailableGlyphCount);
        Assert.AreEqual(1, result.ResolvedGlyphCount);
        Assert.AreEqual(2, cache.BuildCount);
    }

    [TestMethod]
    public void TextWrappingIsSizeIndependentAtExactEmBoundary()
    {
        byte[] bytes = new MapboxVectorTileBuilder().AddPoint("poi", 2048, 2048).Build();
        var source = TestVectorTileSource.Create(new(14, 4823, 6160), bytes, """
            {"version":8,"layers":[{"type":"symbol","source-layer":"poi",
              "layout":{"text-field":"A A","text-font":["Test"],"text-max-width":1.25,
                "text-size":["interpolate",["linear"],["zoom"],10,4,18,40]}}]}
            """, "{}", [0, 0, 0, 0], 1, 1);
        source.AddGlyphs("Test", TestGlyph.RectangleSdf('A'), TestGlyph.RectangleSdf(' '));
        var features = VectorTileDecoder.Decode(bytes);
        VectorStyleAssets.TextResolutionCache cache = new();
        for (int i = 0; i < 50; i++)
        {
            double zoom = 10 + i / 7d;
            var expected = source.StyleAssets.ResolveSymbols(features, zoom);
            Assert.HasCount(3, expected.Symbols);
            Assert.AreEqual(expected.Symbols[0].OffsetY, expected.Symbols[2].OffsetY, 1e-10,
                "An exact em-width fit must stay on one line at every fractional zoom.");
            AssertSameSymbols(expected.Symbols,
                source.StyleAssets.ResolveSymbols(features, zoom, textCache: cache).Symbols);
        }
        Assert.AreEqual(1, cache.BuildCount);
    }

    [TestMethod]
    [DataRow(0d)]
    [DataRow(55d)]
    public async Task ResizedCachedTextMatchesFreshFramePixels(double pitch)
    {
        TileId id = new(14, 4823, 6160);
        byte[] bytes = new MapboxVectorTileBuilder().AddPoint("poi", 2048, 2048).Build();
        var source = TestVectorTileSource.Create(id, bytes, """
            {"version":8,"layers":[{"type":"symbol","source-layer":"poi",
              "layout":{"text-field":"AAAA AA","text-font":["Test"],"text-max-width":2,
                "text-size":["interpolate",["linear"],["zoom"],10,12,18,40]},
              "paint":{"text-color":"#000","text-halo-color":"#fff","text-halo-width":2}}]}
            """, "{}", [0, 0, 0, 0], 1, 1);
        source.AddGlyphs("Test", TestGlyph.RectangleSdf('A'), TestGlyph.RectangleSdf(' '));
        var features = VectorTileDecoder.Decode(bytes);
        var textures = await source.StyleAssets.PrepareTexturesAsync(features, 14, CancellationToken.None);
        var center = source.TileCenter;
        byte[] Capture(bool warm)
        {
            using MapRenderer renderer = new();
            renderer.InitializeOffscreenForBenchmark(256, 256);
            renderer.SetLayerRenderPlan([new(LayerRenderKind.VectorPoints, 0, 1, true, 1,
                TimeSpan.Zero, 0, 24, 0, 256, Style: (int)MapStyle.Road)]);
            renderer.ActivateRasterTileSet(1, 1, 1,
                MapCamera.CreateScene(center.Longitude, center.Latitude, 14, 14, 256, 256),
                _ => true, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
            renderer.AddVectorTexturesForBenchmark(textures);
            renderer.AddVectorTileForBenchmark(new(1, id), features, source.StyleAssets);
            if (warm)
            {
                renderer.SetCameraTargetImmediately(center.Longitude, center.Latitude, 14, 256, 256, 20, pitch);
                renderer.RenderOffscreenFrameForBenchmark();
            }
            renderer.SetTextScaleFactor(1.25);
            renderer.SetCameraTargetImmediately(center.Longitude, center.Latitude, 14.35, 256, 256, 20, pitch);
            renderer.RenderOffscreenFrameForBenchmark();
            return renderer.CaptureOffscreenFrameForBenchmark().Pixels.ToArray();
        }
        byte[] expected = Capture(false), actual = Capture(true);
        Assert.IsGreaterThan(100, Enumerable.Range(0, expected.Length / 4).Count(i => expected[i * 4] < 8),
            "The comparison must contain visible glyphs, not just the background.");
        CollectionAssert.AreEqual(expected, actual);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TextResizePreservesFailuresAndReevaluatesFeatureDependentSize(bool featureDependent)
    {
        byte[] bytes = new MapboxVectorTileBuilder()
            .AddPoint("poi", 2048, 2048, new Dictionary<string, object> { ["name"] = "A", ["size"] = 4d })
            .AddPoint("poi", 1024, 2048).Build();
        string size = featureDependent
            ? """["+",["get","size"],["zoom"]]"""
            : """["interpolate",["linear"],["zoom"],10,4,18,40]""";
        var source = TestVectorTileSource.Create(new(14, 4823, 6160), bytes, $$$"""
            {"version":8,"layers":[{"type":"symbol","source-layer":"poi",
              "layout":{"text-field":["get","name"],"text-font":["Test"],"text-size":{{{size}}}
              }}]}
            """, "{}", [0, 0, 0, 0], 1, 1);
        source.AddGlyphs("Test", TestGlyph.RectangleSdf('A'));
        var features = VectorTileDecoder.Decode(bytes);
        VectorStyleAssets.TextResolutionCache cache = new();
        foreach (double zoom in new[] { 14d, 14.5 })
        {
            var expected = source.StyleAssets.ResolveSymbols(features, zoom);
            var actual = source.StyleAssets.ResolveSymbols(features, zoom, textCache: cache);
            AssertSameSymbols(expected.Symbols, actual.Symbols);
            Assert.AreEqual(1, actual.ResolvedGlyphCount);
            Assert.AreEqual(1, actual.EvaluationFailureCount);
        }
        Assert.AreEqual(featureDependent ? 2 : 1, cache.BuildCount);
    }

    [TestMethod]
    public void MixedTextZoomCacheReducesAllocations()
    {
        var (assets, features) = CreateMixedTextLayers();
        VectorStyleAssets.TextResolutionCache cache = new();
        assets.ResolveSymbols(features, 14, textCache: cache);
        long Measure(bool cached)
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20; i++)
                assets.ResolveSymbols(features, 14.1 + i / 100d, textCache: cached ? cache : null);
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
        long uncached = Measure(false), cached = Measure(true);
        TestContext.WriteLine($"Mixed text zoom: uncached={uncached}; cached={cached}");
        Assert.IsLessThan(uncached * 0.75, cached,
            "Changing one text layer must not reshape all unchanged labels.");
    }

    private static (VectorStyleAssets Assets, VectorTileFeatureCollection Features) CreateMixedTextLayers()
    {
        const string json = """
            {"version":8,"layers":[
            {"type":"symbol","source-layer":"poi","minzoom":13,"maxzoom":15,
             "filter":["==",["get","rank"],0],
             "layout":{"text-field":"AAAA","text-font":["Test"],"text-size":["interpolate",["linear"],["zoom"],13,12,16,24]}},
            {"type":"symbol","source-layer":"poi","minzoom":13,
             "layout":{"text-field":"AAAA","text-font":["Test"],"text-size":16}}]}
            """;
        MapboxVectorTileBuilder builder = new();
        for (int i = 0; i < 128; i++)
            builder.AddPoint("poi", i * 20, 2048, new Dictionary<string, object> { ["rank"] = i });
        byte[] bytes = builder.Build();
        var source = TestVectorTileSource.Create(new(14, 4823, 6160), bytes, json, "{}", [0, 0, 0, 0], 1, 1);
        source.AddGlyphs("Test", TestGlyph.RectangleSdf('A'));
        return (source.StyleAssets, VectorTileDecoder.Decode(bytes));
    }

    [TestMethod]
    public void PolygonDecodeAllocationWorkload()
    {
        MapboxVectorTileBuilder builder = new();
        for (int i = 0; i < 256; i++)
        {
            int x = i % 16 * 256;
            int y = i / 16 * 256;
            builder.AddPolygon("area",
                [[new(x, y), new(x + 200, y), new(x + 200, y + 200), new(x, y + 200)]]);
        }
        byte[] bytes = builder.Build();
        VectorTileDecoder.Decode(bytes);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 8; i++)
        {
            VectorTileFeatureCollection result = VectorTileDecoder.Decode(bytes);
            Assert.AreEqual(256, result.PolygonCount);
            Assert.AreEqual(512, result.PolygonTriangleCount);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        TestContext.WriteLine($"PolygonDecode8: bytes={allocated}");
        Assert.IsLessThan(3_700_000L, allocated,
            "The per-polygon tessellator baseline allocated 15,397,920 bytes.");
    }

    [TestMethod]
    [DataRow(0.5)]
    [DataRow(2d)]
    public void UnpatternedLineCoverageDoesNotAllocate(double width)
    {
        VectorLineStyle style = new(Vector4.One, width, VectorLineCap.Butt, VectorLineJoin.Miter);
        for (int i = 0; i < 100; i++)
            MapRenderer.PrepareVectorLineForRasterization(style);
        long before = GC.GetAllocatedBytesForCurrentThread();
        VectorLineStyle result = default;
        for (int i = 0; i < 10_000; i++)
            result = MapRenderer.PrepareVectorLineForRasterization(style);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        TestContext.WriteLine($"LineCoverage10000: bytes={allocated}");
        Assert.AreEqual(Math.Max(1, width), result.Width);
        Assert.AreEqual(Vector4.One * (float)Math.Min(1, width), result.Color);
        Assert.AreEqual(0L, allocated);
    }

    [TestMethod]
    public void ZoomDependentStyleAllocationWorkload()
    {
        MapboxVectorTileBuilder builder = new();
        for (int i = 0; i < 512; i++)
            builder.AddLine("road", [new(0, i * 8), new(4096, i * 8)]);
        byte[] bytes = builder.Build();
        TestVectorTileSource source = TestVectorTileSource.Create(
            new(14, 4823, 6160), bytes, Style, "{}", [0, 0, 0, 0], 1, 1);
        VectorTileFeatureCollection features = VectorTileDecoder.Decode(bytes);
        for (int i = 0; i < 10; i++)
            source.StyleAssets.ResolveLines(features, 14 + i / 100d);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch timer = Stopwatch.StartNew();
        for (int i = 0; i < 40; i++)
        {
            VectorLineResolution result = source.StyleAssets.ResolveLines(features, 14 + i / 100d);
            Assert.HasCount(512, result.Lines);
            Assert.AreEqual(0, result.EvaluationFailureCount);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        TestContext.WriteLine($"Style: bytes={allocated}; ms={timer.Elapsed.TotalMilliseconds:F3}");
        Assert.IsLessThan(2_200_000L, allocated,
            "The compact-record baseline with a gradient closure allocated 2,462,520 bytes.");
    }

    [TestMethod]
    [DataRow(0d)]
    [DataRow(45d)]
    public void RepeatedPendingFramesWorkload(double pitch)
    {
        TileId id = new(14, 4823, 6160);
        MapboxVectorTileBuilder builder = new();
        for (int i = 0; i < 64; i++)
            builder.AddLine("road", [new(0, i * 64), new(4096, i * 64)]);
        byte[] bytes = builder.Build();
        TestVectorTileSource source = TestVectorTileSource.Create(id, bytes, Style, "{}", [0, 0, 0, 0], 1, 1);
        VectorTileFeatureCollection features = VectorTileDecoder.Decode(bytes);
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(512, 256);
        var center = source.TileCenter;
        renderer.SetCameraTargetImmediately(center.Longitude, center.Latitude, id.Zoom, 512, 256, 0, pitch);
        renderer.SetLayerRenderPlan([new(LayerRenderKind.VectorPoints, 0, 1, true, 1,
            TimeSpan.Zero, 0, 24, 0, 256, Style: (int)MapStyle.Road)]);
        renderer.ActivateRasterTileSet(1, 1, 1,
            MapCamera.CreateScene(center.Longitude, center.Latitude, id.Zoom, id.Zoom, 512, 256),
            _ => true, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        renderer.AddVectorTileForBenchmark(new(1, id), features, source.StyleAssets);
        renderer.RenderOffscreenFrameForBenchmark();
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim release = new();
        renderer.VectorGeometryPreparationStartingForTest = () =>
        {
            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException();
        };
        try
        {
            renderer.AddVectorTileForBenchmark(new(1, new(id.Zoom, id.X + 1, id.Y)), features, source.StyleAssets);
            renderer.RenderOffscreenFrameForBenchmark();
            Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
            using RenderingEventListener events = new("GeometryStreamUploadTiming", "GeometryScratchMemory");
            long before = GC.GetAllocatedBytesForCurrentThread();
            long nativeBefore = MapRenderer.NativeGeometryBuffer.AllocatedBytes;
            Stopwatch timer = Stopwatch.StartNew();
            for (int i = 0; i < 12; i++)
                renderer.RenderOffscreenFrameForBenchmark();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            long native = MapRenderer.NativeGeometryBuffer.AllocatedBytes - nativeBefore;
            TestContext.WriteLine($"Pending12: managed={allocated}; native={native}; ms={timer.Elapsed.TotalMilliseconds:F3}");
            Assert.AreEqual(0L, native, "Eligible pending tiles must not be tessellated repeatedly.");
            var uploads = events.Events("GeometryStreamUploadTiming");
            Assert.HasCount(12, uploads);
            Assert.IsTrue(uploads.All(e => Convert.ToInt32(e.Payload[2]) == 0));
        }
        finally
        {
            release.Set();
            Assert.IsTrue(SpinWait.SpinUntil(() => renderer.ActiveVectorGeometryPreparations == 0, TimeSpan.FromSeconds(5)));
        }
    }
}
