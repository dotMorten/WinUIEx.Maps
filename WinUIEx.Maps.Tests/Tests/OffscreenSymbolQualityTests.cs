using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class OffscreenSymbolQualityTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnalyticCoverageAddsCoverageWithoutChangingOpaqueCore(bool polygon)
    {
        int[] partial = new int[2];
        for (int mode = 0; mode < 2; mode++)
        {
            using MapRenderer renderer = new();
            renderer.InitializeCoveragePrototypeForBenchmark(256, 256, mode == 1, polygon);
            MapRenderFrame frame = await RenderGlyphAsync(renderer, 96,
                new TestGlyph('A', 18, 18, 0, 18, 18, new byte[24 * 24]), !polygon);
            byte[] pixels = frame.Pixels.ToArray();
            int black = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i] is > 8 and < 230) partial[mode]++;
                if (pixels[i] < 8) black++;
                Assert.AreEqual((byte)255, pixels[i + 3]);
            }
            Assert.IsGreaterThan(100, black);
            await frame.SavePngAsync(Path.Combine(AppContext.BaseDirectory,
                "TestResults", $"analytic-{(polygon ? "polygon" : "capsule")}-{mode}.png"));
        }
        Assert.AreEqual(0, partial[0]);
        Assert.IsGreaterThan(60, partial[1]);
    }

    [TestMethod]
    [DataRow(12)]
    [DataRow(15)]
    [DataRow(18)]
    [DataRow(21)]
    [DataRow(24)]
    [DataRow(30)]
    [DataRow(36)]
    [DataRow(42)]
    [DataRow(48)]
    public async Task AxisAlignedSdfEdgeHasOnePhysicalPixelTransition(int size)
    {
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(256, 256);
        MapRenderFrame frame = await RenderGlyphAsync(renderer, size, TestGlyph.RectangleSdf('A'));
        byte[] pixels = frame.Pixels.ToArray();
        int bestRow = 0;
        int mostBlack = 0;
        for (int y = 0; y < 256; y++)
        {
            int black = 0;
            for (int x = 0; x < 256; x++)
                if (pixels[(y * 256 + x) * 4] < 8) black++;
            if (black > mostBlack)
            {
                mostBlack = black;
                bestRow = y;
            }
        }
        Assert.IsGreaterThan(2, mostBlack, "The complete glyph must be rendered.");
        int transitions = 0;
        for (int x = 0; x < 256; x++)
            if (pixels[(bestRow * 256 + x) * 4] is > 8 and < 230) transitions++;
        Assert.IsLessThanOrEqualTo(2, transitions,
            "An axis-aligned signed-distance edge should not blur over two physical pixels per side.");
    }

    private static async Task<MapRenderFrame> RenderGlyphAsync(
        MapRenderer renderer, int size, TestGlyph glyph, bool writeBrowserReference = false)
    {
        TileId id = new(4, 8, 8);
        byte[] tile = new MapboxVectorTileBuilder().AddPoint("labels", 2048, 2048).Build();
        TestVectorTileSource source = TestVectorTileSource.Create(id, tile,
            $$$"""
            {"version":8,"layers":[{"type":"symbol","source-layer":"labels",
              "layout":{"text-field":"A","text-font":["TestFont"],"text-size":{{{size}}},
                "text-allow-overlap":true},
              "paint":{"text-color":"#000000"}}]}
            """, "{}", [0, 0, 0, 0], 1, 1);
        source.AddGlyphs("TestFont", glyph);
        var features = VectorTileDecoder.Decode(tile);
        var textures = await source.StyleAssets.PrepareTexturesAsync(features, 4, CancellationToken.None);
        var center = source.TileCenter;
        MapScene scene = MapCamera.CreateScene(center.Longitude, center.Latitude, 4, 4, 256, 256, 0, 0);
        renderer.SetCameraTargetImmediately(center.Longitude, center.Latitude, 4, 256, 256);
        renderer.ActivateRasterTileSet(1, 1, 1, scene, static _ => true,
            RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        renderer.SetLayerRenderPlan(
        [
            new LayerRenderSnapshot(LayerRenderKind.VectorPoints, 0, 1, true, 1,
                TimeSpan.Zero, 0, 24, 0, 256, Style: (int)MapStyle.Road),
        ]);
        renderer.AddVectorTexturesForBenchmark(textures);
        renderer.AddVectorTileForBenchmark(new RasterTileKey(1, id), features, source.StyleAssets);
        renderer.RenderOffscreenFrameForBenchmark();
        MapRenderFrame frame = renderer.CaptureOffscreenFrameForBenchmark();
        if (writeBrowserReference)
        {
            var symbol = Assert.ContainsSingle(source.StyleAssets.ResolveSymbols(features, 4).Symbols);
            double left = 128 + symbol.OffsetX - symbol.Width / 2;
            double top = 128 + symbol.OffsetY - symbol.Height / 2;
            byte background = frame.Pixels.Span[0];
            string html = FormattableString.Invariant($$"""
                <!doctype html><html><meta charset="utf-8">
                <style>html,body{margin:0;width:256px;height:256px;overflow:hidden}</style>
                <canvas id="c" width="256" height="256"></canvas><script>
                const c=document.getElementById('c'),g=c.getContext('2d');
                g.fillStyle='rgb({{background}},{{background}},{{background}})';g.fillRect(0,0,256,256);
                g.lineCap='round';g.strokeStyle='black';g.lineWidth={{0.05 * symbol.Width}};
                g.beginPath();g.moveTo({{left + .2 * symbol.Width}},{{top + .25 * symbol.Height}});
                g.lineTo({{left + .8 * symbol.Width}},{{top + .75 * symbol.Height}});g.stroke();
                document.title='Local capsule fixture DPR '+devicePixelRatio;
                </script></html>
                """);
            string results = Path.Combine(AppContext.BaseDirectory, "TestResults");
            Directory.CreateDirectory(results);
            await File.WriteAllTextAsync(Path.Combine(results, "analytic-capsule.html"), html);
            await File.WriteAllBytesAsync(Path.Combine(results, "analytic-capsule-native.bgra"), frame.Pixels.ToArray());
        }
        return frame;
    }
}
