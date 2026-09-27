using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

[TestClass]
[DoNotParallelize]
public sealed class VectorLineJoinRenderingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(false, 15d, 0d)]
    [DataRow(false, 45d, 0d)]
    [DataRow(false, 90d, 0d)]
    [DataRow(false, -45d, 60d)]
    [DataRow(true, 45d, 0d)]
    [DataRow(true, -45d, 60d)]
    public void ThickMapElementRoundJoinsCoverTheirInterior(
        bool polygon, double angle, double pitch)
    {
        const double radius = 32;
        const double heading = 37;
        double radians = angle * Math.PI / 180;
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(512, 512);
        renderer.SetCameraTargetImmediately(0, 0, 6, 512, 512, heading, pitch);
        renderer.SetLayerRenderPlan([new(LayerRenderKind.MapElements, 0, 1, true, 1,
            TimeSpan.Zero, 0, 24, 0, 256)]);
        MapScreenPoint[] offsets =
        [
            new(-150, 0), new(0, 0),
            new(150 * Math.Cos(radians), 150 * Math.Sin(radians)),
        ];
        if (polygon)
            offsets = [offsets[1], offsets[2], offsets[0]];
        var path = new Windows.Devices.Geolocation.Geopath(offsets.Select(point =>
        {
            MapCenter location = MapCamera.LocationAtOffset(
                0, 0, 6, point.X, point.Y, heading, pitch, 512);
            return new Windows.Devices.Geolocation.BasicGeoposition
            {
                Longitude = location.Longitude,
                Latitude = location.Latitude,
            };
        }).ToList());
        MapGeometryData geometry = polygon
            ? new MapPolygon { Path = path }.GetState().Geometry
            : new MapPolyline { Path = path }.GetState().Geometry;
        renderer.SetMapElements([], [new(
            polygon ? MapGeometryKind.Polygon : MapGeometryKind.Polyline,
            geometry, default, new MapColorSnapshot(255, 255, 0, 0), false,
            radius * 2, 0, 0, 0)]);
        MapRenderFrame frame = renderer.CaptureOffscreenFrameForBenchmark();
        int incorrectPixels = 0;
        double side = Math.Sign(angle);
        for (int y = 220; y < 292; y++)
        {
            for (int x = 256; x < 292; x++)
            {
                double dx = x + 0.5 - 256;
                double dy = y + 0.5 - 256;
                double sweep = Math.Atan2(dx, -side * dy);
                if (sweep <= 0 || sweep >= Math.Abs(radians) ||
                    dx * dx + dy * dy >= (radius - 1) * (radius - 1))
                    continue;
                int offset = (y * frame.Width + x) * 4;
                if (frame.Pixels.Span[offset + 2] < 250 ||
                    frame.Pixels.Span[offset + 1] > 5 ||
                    frame.Pixels.Span[offset] > 5)
                    incorrectPixels++;
            }
        }
        Assert.AreEqual(0, incorrectPixels,
            "Pixels at least one pixel inside the round join must not expose the background.");
    }

    [TestMethod]
    [DataRow(1, 1, 16, false)]
    [DataRow(1, -1, 16, false)]
    [DataRow(1, 1, 64, false)]
    [DataRow(1, -1, 64, false)]
    [DataRow(4, 1, 64, false)]
    [DataRow(4, -1, 64, false)]
    [DataRow(1, 1, 64, true)]
    [DataRow(1, -1, 64, true)]
    [DataRow(4, 1, 64, true)]
    [DataRow(4, -1, 64, true)]
    public void VectorJoinsHaveNoBackgroundHolesInStreamedOrCachedFrames(
        int samples, int direction, int width, bool round)
    {
        using RenderingEventListener events = new(
            "VectorLineRenderBatch", "VectorGeometryFrameCacheSummary", "RendererFailure");
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(256, 256, samples);
        if (renderer.RenderSampleCount != samples)
            Assert.Inconclusive("The requested multisample target is not supported.");

        TileId tile = new(4, 8, 8);
        byte[] bytes = new MapboxVectorTileBuilder()
            .AddLine("road", [new(1024, 2048), new(2048, 2048),
                new(2048, 2048 + direction * 1024)]).Build();
        string join = round ? "round" : "miter";
        TestVectorTileSource source = TestVectorTileSource.Create(tile, bytes,
            $$$"""
            {"version":8,"layers":[
              {"type":"background","paint":{"background-color":"#ffffff"}},
              {"type":"line","source-layer":"road","layout":{"line-join":"{{{join}}}"},
               "paint":{"line-color":"#0000ff","line-width":{{{width + 4}}} }},
              {"type":"line","source-layer":"road","layout":{"line-join":"{{{join}}}"},
               "paint":{"line-color":"#ff0000","line-width":{{{width}}} }}
            ]}
            """, "{}", [0, 0, 0, 0], 1, 1);
        renderer.SetLayerRenderPlan([new(LayerRenderKind.VectorPoints, 0, 1, true, 1,
            TimeSpan.Zero, 0, 24, 0, 256)]);
        renderer.SetCameraTargetImmediately(
            source.TileCenter.Longitude, source.TileCenter.Latitude, 4, 256, 256);
        renderer.ActivateRasterTileSet(1, 1, 1,
            MapCamera.CreateScene(source.TileCenter.Longitude, source.TileCenter.Latitude,
                4, 4, 256, 256, 0, 0),
            id => id == tile, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        renderer.AddVectorTileForBenchmark(new(1, tile),
            VectorTileDecoder.Decode(bytes), source.StyleAssets);

        for (int frameIndex = 0; frameIndex < 2; frameIndex++)
        {
            MapRenderFrame frame = renderer.CaptureOffscreenFrameForBenchmark();
            var batch = events.Events("VectorLineRenderBatch").Last();
            TestContext.WriteLine($"Frame {frameIndex}, VectorLineRenderBatch: {string.Join(", ", batch.Payload)}");
            Assert.AreEqual(2, Convert.ToInt32(batch.Payload[2]));
            Assert.AreEqual(0, Convert.ToInt32(batch.Payload[4]));
            Assert.IsEmpty(events.Events("RendererFailure"));
            var cache = events.Events("VectorGeometryFrameCacheSummary")
                .Last(e => Convert.ToInt32(e.Payload[1]) == 1);
            Assert.AreEqual(frameIndex, Convert.ToInt32(cache.Payload[2]));

            int incorrectPixels = 0;
            for (int dx = 2; dx < width / 2 - 2; dx++)
            {
                for (int dy = 2; dy < width / 2 - 2; dy++)
                {
                    if (round && Math.Pow(dx + 0.5, 2) + Math.Pow(dy + 0.5, 2) >=
                        Math.Pow(width / 2d - 1, 2))
                        continue;
                    int x = 128 + dx;
                    int y = direction > 0 ? 127 - dy : 128 + dy;
                    int offset = (y * frame.Width + x) * 4;
                    if (frame.Pixels.Span[offset + 2] < 250 ||
                        frame.Pixels.Span[offset + 1] > 5 ||
                        frame.Pixels.Span[offset] > 5)
                        incorrectPixels++;
                }
            }
            Assert.AreEqual(0, incorrectPixels,
                $"Frame {frameIndex}: every pixel inside the outer {join} join must be solid red, not background or casing.");
        }
    }
}
