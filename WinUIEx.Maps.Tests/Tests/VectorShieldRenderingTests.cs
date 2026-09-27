using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

[TestClass]
[DoNotParallelize]
public sealed class VectorShieldRenderingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(false, 18, 0.8)]
    [DataRow(true, 18, 0.8)]
    [DataRow(false, 22, 3d)]
    [DataRow(true, 22, 3d)]
    public async Task RouteNumbersStayInsideSpriteContentWhenOverzoomed(
        bool line, int zoom, double shieldScale)
    {
        using RenderingEventListener events = new(
            "VectorSymbolRenderBatch", "VectorLabelRenderBatch", "RendererFailure");
        TileId tileId = new(18, 131072, 131072);
        Dictionary<string, object> properties = new() { ["shield-scale"] = shieldScale };
        MapboxVectorTileBuilder builder = new();
        int halfLength = zoom == 18 ? 1024 : 64;
        byte[] tile = (line
            ? builder.AddLine("road",
                [new(2048 - halfLength, 2048), new(2048 + halfLength, 2048)], properties)
            : builder.AddPoint("road", 2048, 2048, properties)).Build();
        byte[] pixels = new byte[51 * 51 * 4];
        for (int y = 0; y < 51; y++)
        for (int x = 0; x < 51; x++)
        {
            int offset = (y * 51 + x) * 4;
            bool content = x >= 13 && x < 38 && y >= 16 && y < 35;
            pixels[offset] = content ? (byte)255 : (byte)0;
            pixels[offset + 2] = content ? (byte)0 : (byte)255;
            pixels[offset + 3] = 255;
        }
        TestVectorTileSource source = TestVectorTileSource.Create(tileId, tile,
            $$$"""
            {"version":8,"layers":[
              {"type":"background","paint":{"background-color":"#000000"}},
              {"type":"symbol","source-layer":"road","layout":{
                "symbol-placement":"{{{(line ? "line" : "point")}}}",
                "symbol-spacing":1000,"icon-image":"shield","icon-text-fit":"both",
                "icon-rotation-alignment":"viewport","text-rotation-alignment":"viewport",
                "text-field":"405","text-font":["TestFont"],
                "text-size":["*",10,["number",["get","shield-scale"],0.8]],
                "text-letter-spacing":0.05
              },"paint":{"text-color":"#ffffff"}}
            ]}
            """,
            """
            {"shield":{"x":0,"y":0,"width":51,"height":51,"pixelRatio":1.8,
              "content":[13,16,38,35],
              "textFitWidth":"stretchOnly","textFitHeight":"proportional"}}
            """, pixels, 51, 51);
        source.AddGlyphs("TestFont", TestGlyph.RectangleSdf('4'),
            TestGlyph.RectangleSdf('0'), TestGlyph.RectangleSdf('5'));
        var features = VectorTileDecoder.Decode(tile);
        var textures = await source.StyleAssets.PrepareTexturesAsync(
            features, zoom, CancellationToken.None);
        VectorTileSymbol icon = Assert.ContainsSingle(
            source.StyleAssets.ResolveSymbols(features, zoom).Symbols.Where(
                symbol => symbol.Kind == VectorSymbolKind.Icon));

        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(256, 256);
        var center = source.TileCenter;
        renderer.SetCameraTargetImmediately(center.Longitude, center.Latitude, zoom, 256, 256);
        renderer.SetLayerRenderPlan([new(LayerRenderKind.VectorPoints, 0, 1, true, 1,
            TimeSpan.Zero, 0, 24, 0, 256)]);
        renderer.ActivateRasterTileSet(1, 1, 1,
            MapCamera.CreateScene(center.Longitude, center.Latitude, zoom, 18, 256, 256, 0, 0),
            id => id == tileId, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        renderer.AddVectorTexturesForBenchmark(textures);
        renderer.AddVectorTileForBenchmark(new(1, tileId), features, source.StyleAssets);
        double left = 128 + icon.OffsetX - icon.Width / 2;
        double top = 128 + icon.OffsetY - icon.Height / 2;
        double contentLeft = left + icon.Width * 13 / 51;
        double contentRight = left + icon.Width * 38 / 51;
        double contentTop = top + icon.Height * 16 / 51;
        double contentBottom = top + icon.Height * 35 / 51;
        for (int frameIndex = 0; frameIndex < 2; frameIndex++)
        {
            MapRenderFrame frame = renderer.CaptureOffscreenFrameForBenchmark();
            Assert.IsEmpty(events.Events("RendererFailure"));
            Assert.AreEqual(1, Convert.ToInt32(
                events.Events("VectorSymbolRenderBatch").Last().Payload[2]));
            Assert.AreEqual(3, Convert.ToInt32(
                events.Events("VectorLabelRenderBatch").Last().Payload[2]));
            Assert.AreEqual(0, Convert.ToInt32(
                events.Events("VectorSymbolRenderBatch").Last().Payload[3]));
            Assert.AreEqual(0, Convert.ToInt32(
                events.Events("VectorLabelRenderBatch").Last().Payload[3]));
            TestContext.WriteLine("Symbols: " + string.Join(", ",
                events.Events("VectorSymbolRenderBatch").Last().Payload));
            TestContext.WriteLine("Labels: " + string.Join(", ",
                events.Events("VectorLabelRenderBatch").Last().Payload));
            int inside = 0;
            int outside = 0;
            for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
            {
                int offset = (y * frame.Width + x) * 4;
                if (frame.Pixels.Span[offset] < 240 ||
                    frame.Pixels.Span[offset + 1] < 240 ||
                    frame.Pixels.Span[offset + 2] < 240)
                    continue;
                if (x + 0.5 >= contentLeft && x + 0.5 <= contentRight &&
                    y + 0.5 >= contentTop && y + 0.5 <= contentBottom)
                    inside++;
                else
                    outside++;
            }
            Assert.IsGreaterThan(5, inside, "The route number must actually render.");
            Assert.AreEqual(0, outside,
                "Route text must fit inside the sprite's content rectangle, not its outer border.");
        }
    }
}
