using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BuildingFootprintRenderingTests
{
    [TestMethod]
    [DataRow(1d)]
    [DataRow(0.5)]
    public void AzureHslaFootprintsRenderPolygonsWithHolesOutlinesAndRoadsAbove(double opacity)
    {
        using RenderingEventListener listener = new("VectorPolygonRenderBatch");
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(512, 512);
        TileId tile = new(18, 41988, 91541);
        byte[] bytes = new MapboxVectorTileBuilder()
            .AddPolygon("land", [[new(0, 0), new(4096, 0), new(4096, 4096), new(0, 4096)]])
            .AddPolygon("footprint", [
                [new(512, 512), new(3584, 512), new(3584, 3584), new(512, 3584)],
                [new(1536, 1536), new(1536, 2560), new(2560, 2560), new(2560, 1536)]])
            .AddLine("road", [new(0, 1024), new(4096, 1024)]).Build();
        TestVectorTileSource source = TestVectorTileSource.Create(tile, bytes,
            """
            {"version":8,"layers":[
              {"type":"fill","source-layer":"land","paint":{"fill-color":"#0000ff"}},
              {"type":"fill","source-layer":"footprint","minzoom":15,"paint":{
                "fill-color":["interpolate",["linear"],["zoom"],
                  16,"hsla(60, 9%, 86%, 0.82)",22,"hsla(60, 5%, 71%, 0.82)"],
                "fill-outline-color":"hsla(0, 0%, 70%, 0.9)"}},
              {"type":"line","source-layer":"road","paint":{"line-color":"#00ff00","line-width":8}}
            ]}
            """, "{}", [0, 0, 0, 0], 1, 1);
        renderer.SetLayerRenderPlan([new(LayerRenderKind.VectorPoints, 0, 1, true, opacity,
            TimeSpan.Zero, 0, 24, 0, 256)]);
        renderer.SetCameraTargetImmediately(source.TileCenter.Longitude, source.TileCenter.Latitude, 18, 512, 512);
        renderer.ActivateRasterTileSet(1, 1, 1,
            MapCamera.CreateScene(source.TileCenter.Longitude, source.TileCenter.Latitude,
                18, 18, 512, 512, 0, 0),
            id => id == tile, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        renderer.AddVectorTileForBenchmark(new(1, tile), VectorTileDecoder.Decode(bytes), source.StyleAssets);
        MapRenderFrame frame = renderer.CaptureOffscreenFrameForBenchmark();
        int hole = (256 * 512 + 256) * 4;
        int footprint = (290 * 512 + 185) * 4;
        int road = (192 * 512 + 256) * 4;
        ReadOnlySpan<byte> pixels = frame.Pixels.Span;
        Assert.IsGreaterThan(30, pixels[footprint + 2] - pixels[hole + 2],
            "The interpolated HSL footprint must actually contribute pixels.");
        double alpha = 0.82 * opacity;
        Assert.AreEqual(0.6750513333 * opacity * 255 + pixels[hole + 2] * (1 - alpha),
            pixels[footprint + 2], 3);
        Assert.AreEqual(0.6533486667 * opacity * 255 + pixels[hole] * (1 - alpha),
            pixels[footprint], 3);
        Assert.IsGreaterThan(pixels[hole + 2], pixels[hole],
            "The courtyard must reveal blue ground.");
        Assert.IsGreaterThan(pixels[road], pixels[road + 1],
            "Road geometry must stay above the footprint.");
        var batch = listener.Events("VectorPolygonRenderBatch").Single();
        Assert.AreEqual(0, Convert.ToInt32(batch.Payload[4]));
        Assert.IsGreaterThan(0, Convert.ToInt32(batch.Payload[3]));
    }

    [TestMethod]
    [DataRow(15)]
    [DataRow(16)]
    public async Task FractionalThresholdCrossingsDoNotResurrectIneligibleFallbackBuildings(int sourceZoom)
    {
        using RenderingEventListener listener = new(
            "VectorPolygonRenderBatch", "VectorGeometryFrameCacheSummary",
            "VectorGeometryPreparationSummary", "VectorGeometryFallbackOpacitySummary");
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(256, 256);
        TileId detail = new(sourceZoom, 1 << (sourceZoom - 1), 1 << (sourceZoom - 1));
        TileId coarse = new(sourceZoom - 1, detail.X / 2, detail.Y / 2);
        byte[] bytes = new MapboxVectorTileBuilder().AddPolygon("footprint",
            [[new(1024, 1024), new(3072, 1024), new(3072, 3072), new(1024, 3072)]])
            .Build();
        TestVectorTileSource source = TestVectorTileSource.Create(detail, bytes,
            """
            {"version":8,"layers":[{"type":"fill","source-layer":"footprint","minzoom":15,
              "paint":{"fill-color":"hsla(0, 100%, 50%, 1)"}}]}
            """, "{}", [0, 0, 0, 0], 1, 1);
        renderer.SetLayerRenderPlan([new(LayerRenderKind.VectorPoints, 0, 1, true, 1,
            TimeSpan.Zero, 0, 24, 0, 256)]);
        void Activate(double zoom, TileId tile, long version)
        {
            renderer.SetCameraTargetImmediately(source.TileCenter.Longitude, source.TileCenter.Latitude,
                zoom, 256, 256);
            renderer.ActivateRasterTileSet(1, 1, version,
                MapCamera.CreateScene(source.TileCenter.Longitude, source.TileCenter.Latitude,
                    zoom, tile.Zoom, 256, 256, 0, 0),
                id => id == tile, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        }
        Activate(sourceZoom + 0.05, detail, 1);
        renderer.AddVectorTileForBenchmark(new(1, detail), VectorTileDecoder.Decode(bytes), source.StyleAssets);
        Assert.IsGreaterThan(0, RedPixels(renderer.CaptureOffscreenFrameForBenchmark()));
        List<string> frames = [];
        int unexpected = 0;
        for (int frame = 0; frame < 80; frame++)
        {
            double zoomOffset = (frame % 8) switch
            {
                0 or 1 => -0.01, 2 or 3 => -0.03, 4 or 5 => 0.01, _ => 0.03,
            };
            double zoom = sourceZoom + zoomOffset;
            Activate(zoom, zoom < sourceZoom ? coarse : detail, frame + 2);
            if (frame == 40)
                renderer.AddVectorTileForBenchmark(new(1, coarse), new([]), source.StyleAssets);
            if (frame == 69)
                renderer.AddVectorTileForBenchmark(new(1, new TileId(sourceZoom, detail.X + 1, detail.Y)),
                    new([]), source.StyleAssets);
            // Capture itself renders; no extra Render call may hide a one-frame error.
            int red = RedPixels(renderer.CaptureOffscreenFrameForBenchmark());
            bool shouldDraw = zoom >= sourceZoom || (sourceZoom == 16 && frame < 40);
            if ((red > 0) != shouldDraw)
                unexpected++;
            if (shouldDraw && Math.Abs(red / (16384 * Math.Pow(2, 2 * (zoom - sourceZoom))) - 1) > 0.08)
                unexpected++;
            frames.Add(FormattableString.Invariant($"{frame},{zoom:F2},{red},{shouldDraw}"));
            if (frame > 40)
                await Task.Delay(3);
        }
        for (int frame = 80; frame < 86; frame++)
        {
            await Task.Delay(10);
            if (frame == 81)
                renderer.AddVectorTileForBenchmark(new(1, new TileId(sourceZoom, detail.X + 2, detail.Y)),
                    new([]), source.StyleAssets);
            int red = RedPixels(renderer.CaptureOffscreenFrameForBenchmark());
            if (Math.Abs(red / (16384 * Math.Pow(2, 0.06)) - 1) > 0.08)
                unexpected++;
            frames.Add(FormattableString.Invariant($"{frame},{sourceZoom + 0.03:F2},{red},True"));
        }
        string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "artifacts", "azure-buildings"));
        Directory.CreateDirectory(directory);
        string suffix = sourceZoom == 15 ? "current" : "16-current";
        await File.WriteAllLinesAsync(Path.Combine(directory, $"threshold-{suffix}.csv"), frames);
        await File.WriteAllLinesAsync(Path.Combine(directory, $"threshold-events-{suffix}.txt"),
            new[] { "VectorPolygonRenderBatch", "VectorGeometryFrameCacheSummary",
                    "VectorGeometryPreparationSummary", "VectorGeometryFallbackOpacitySummary" }
                .SelectMany(name => listener.Events(name).Select(e =>
                    name + ": " + string.Join(", ", e.Payload))));
        Assert.AreEqual(0, unexpected, "Every captured frame must respect the building minzoom, including retained fallback.");
        Assert.HasCount(87, listener.Events("VectorPolygonRenderBatch"));
        Assert.IsTrue(listener.Events("VectorGeometryPreparationSummary")
            .Any(e => Convert.ToInt32(e.Payload[1]) == 0));
        Assert.IsTrue(listener.Events("VectorGeometryPreparationSummary")
            .Any(e => Convert.ToInt32(e.Payload[1]) == 1));
    }

    private static int RedPixels(MapRenderFrame frame)
    {
        int count = 0;
        ReadOnlySpan<byte> pixels = frame.Pixels.Span;
        for (int offset = 0; offset < pixels.Length; offset += 4)
            if (pixels[offset + 2] > 200 && pixels[offset] < 80 && pixels[offset + 1] < 80)
                count++;
        return count;
    }
}
