using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class OffscreenAntialiasingTests
{
    [TestMethod]
    public async Task FourSamplesResolveDiagonalCoverageWithoutChangingOpaqueInterior()
    {
        int[] edgeCounts = new int[2];
        for (int index = 0; index < 2; index++)
        {
            int samples = index == 0 ? 1 : 4;
            using RenderingEventListener events = new("RenderSurfaceChanged");
            using MapRenderer renderer = new();
            renderer.InitializeOffscreenForBenchmark(256, 256, samples);
            if (renderer.RenderSampleCount != samples)
            {
                Assert.Inconclusive("This device does not support the four-sample benchmark target.");
            }
            CapturedRenderingEvent surface = events.Events("RenderSurfaceChanged").Last();
            Assert.AreEqual(samples, Convert.ToInt32(surface.Payload[8]));
            Assert.AreEqual(256L * 256 * 4 * (samples == 4 ? 5 : 1),
                Convert.ToInt64(surface.Payload[7]));
            TileId id = new(4, 8, 8);
            byte[] tile = new MapboxVectorTileBuilder()
                .AddLine("roads", [new(256, 512), new(2048, 3072), new(3840, 1024)])
                .Build();
            TestVectorTileSource source = TestVectorTileSource.Create(
                id, tile,
                """
                {"version":8,"layers":[{"type":"line","source-layer":"roads",
                  "layout":{"line-cap":"round","line-join":"round"},
                  "paint":{"line-color":"#000000","line-width":3}}]}
                """, "{}", [0, 0, 0, 0], 1, 1);
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
            renderer.AddVectorTileForBenchmark(new RasterTileKey(1, id),
                VectorTileDecoder.Decode(tile), source.StyleAssets);
            renderer.RenderOffscreenFrameForBenchmark();
            MapRenderFrame frame = renderer.CaptureOffscreenFrameForBenchmark();
            byte[] pixels = frame.Pixels.ToArray();
            int opaqueBlack = 0;
            for (int pixel = 0; pixel < pixels.Length; pixel += 4)
            {
                if (pixels[pixel] == 0) opaqueBlack++;
                if (pixels[pixel] is > 8 and < 230) edgeCounts[index]++;
                Assert.AreEqual((byte)255, pixels[pixel + 3]);
            }
            Assert.IsGreaterThan(300, opaqueBlack);
            await frame.SavePngAsync(Path.Combine(AppContext.BaseDirectory,
                "TestResults", $"diagonal-samples-{samples}.png"));
        }
        Assert.AreEqual(0, edgeCounts[0]);
        Assert.IsGreaterThan(100, edgeCounts[1]);
    }
}
