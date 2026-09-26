using System.Reflection;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class TrafficLineCompositeTests
{
    [TestMethod]
    [DataRow(1, 1d)]
    [DataRow(4, 1d)]
    [DataRow(1, 0.5)]
    [DataRow(4, 0.5)]
    public async Task CompositeSharesOpacityAcrossSourcesButNotIcons(int samples, double opacity)
    {
        using RenderingEventListener events = new("VectorLineComposite", "VectorGeometryPreparationSummary");
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(256, 256, samples);
        if (renderer.RenderSampleCount != samples)
            Assert.Inconclusive("The requested multisample target is not supported.");
        TileId id = new(12, 2048, 2048);
        double longitude = MapCamera.WorldXToLongitude(2048.5 / 4096);
        double latitude = MapCamera.WorldYToLatitude(2048.5 / 4096);
        MapScene scene = MapCamera.CreateScene(longitude, latitude, 12, 12, 256, 256, 0, 0);
        renderer.SetCameraTargetImmediately(longitude, latitude, 12, 256, 256);
        byte[] flow = new MapboxVectorTileBuilder()
            .AddLine("flow", [new(512, 2048), new(3584, 2048)],
                new Dictionary<string, object> { ["traffic_level"] = 0.7 }).Build();
        byte[] incidents = new MapboxVectorTileBuilder()
            .AddLine("incident", [new(1792, 2048), new(2816, 2048)])
            .AddPoint("incident", 2048, 2048,
                new Dictionary<string, object> { ["icon_category"] = 9, ["magnitude"] = 2 }).Build();
        await AddSource(1, flow, TrafficFlowStyle.Relative);
        await AddSource(2, incidents, null);
        LayerRenderSnapshot[] plan =
        [
            new(LayerRenderKind.VectorPoints, 0, 1, true, opacity, TimeSpan.Zero, 0, 24, 0, 256,
                LineCompositeOpacity: AzureTrafficLayer.RoadLineOpacity),
            new(LayerRenderKind.VectorPoints, 0, 2, true, opacity, TimeSpan.Zero, 0, 24, 0, 256,
                LineCompositeOpacity: AzureTrafficLayer.RoadLineOpacity),
        ];
        renderer.SetLayerRenderPlan(plan);
        renderer.RenderOffscreenFrameForBenchmark();
        MapRenderFrame frame = renderer.CaptureOffscreenFrameForBenchmark();
        double background = frame.Pixels.Span[0];
        Assert.AreEqual(background * (1 - 0.5 * opacity) + 34 * 0.5 * opacity,
            Blue(frame, 64, 128), 2, "Flow opacity must be applied once.");
        Assert.AreEqual(background * (1 - 0.5 * opacity),
            Blue(frame, 160, 128), 2, "Incident roads must replace flow within the shared composite.");
        Assert.IsNotEmpty(ConnectedComponentAnalyzer.Find(frame,
            ConnectedComponentAnalyzer.Near(
                (byte)(255 * opacity + background * (1 - opacity)),
                (byte)(213 * opacity + background * (1 - opacity)),
                (byte)(79 * opacity + background * (1 - opacity)), tolerance: 3),
            minimumPixelCount: 15), "The incident sign must use layer opacity, not road opacity.");
        CapturedRenderingEvent composite = events.Events("VectorLineComposite").Last();
        Assert.AreEqual(2, composite.Payload[0]);
        Assert.AreEqual(opacity * 0.5, composite.Payload[4]);
        long bytes = 256L * 256 * 4 * (samples == 4 ? 5 : 1);
        Assert.AreEqual(bytes, composite.Payload[5]);
        IntPtr target = Field<IntPtr>("_lineCompositeTarget");
        Assert.AreNotEqual(IntPtr.Zero, target);
        renderer.CaptureOffscreenFrameForBenchmark();
        Assert.AreEqual(target, Field<IntPtr>("_lineCompositeTarget"), "Steady frames reuse the target.");

        // Separate public layers must not merge their opacity or retain another target.
        renderer.SetLayerRenderPlan([plan[0], plan[1] with { LayerIndex = 1 }]);
        frame = renderer.CaptureOffscreenFrameForBenchmark();
        double flowBlue = background * (1 - 0.5 * opacity) + 34 * 0.5 * opacity;
        Assert.AreEqual(flowBlue * (1 - 0.5 * opacity), Blue(frame, 160, 128), 2);
        Assert.AreEqual(target, Field<IntPtr>("_lineCompositeTarget"));
        Assert.AreEqual(bytes, Field<long>("_lineCompositeBytes"));

        renderer.SetLayerRenderPlan([plan[0]]);
        renderer.RenderOffscreenFrameForBenchmark();
        renderer.ActivateRasterTileSet(1, 1, 2, scene, static _ => true,
            RasterSourceKind.Azure, LayerRenderKind.VectorPoints, false);
        using (CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10)))
        {
            do
            {
                frame = renderer.CaptureOffscreenFrameForBenchmark();
                if (events.Events("VectorGeometryPreparationSummary")
                    .Any(e => Convert.ToInt32(e.Payload[1]) == 1))
                    break;
                await Task.Delay(10, timeout.Token);
            } while (true);
        }
        Assert.AreEqual(background * (1 - 0.5 * opacity) + 34 * 0.5 * opacity,
            Blue(frame, 64, 128), 2, "Prepared geometry must not bake in road or public-layer opacity twice.");

        renderer.SetLayerRenderPlan([plan[0] with { MinZoom = 13 }, plan[1] with { MinZoom = 13 }]);
        renderer.CaptureOffscreenFrameForBenchmark();
        Assert.AreEqual(0L, Field<long>("_lineCompositeBytes"), "Ineligible layers must release the target.");
        renderer.SetLayerRenderPlan(plan);
        renderer.CaptureOffscreenFrameForBenchmark();
        Assert.AreEqual(bytes, Field<long>("_lineCompositeBytes"));
        renderer.SetLayerRenderPlan([plan[0] with { LineCompositeOpacity = 1 }]);
        renderer.CaptureOffscreenFrameForBenchmark();
        Assert.AreEqual(0L, Field<long>("_lineCompositeBytes"), "Ordinary vector layers need no composite memory.");
        renderer.SetLayerRenderPlan(plan);
        renderer.CaptureOffscreenFrameForBenchmark();
        renderer.SetLayerRenderPlan([]);
        renderer.CaptureOffscreenFrameForBenchmark();
        Assert.AreEqual(IntPtr.Zero, Field<IntPtr>("_lineCompositeTarget"));
        Assert.AreEqual(0L, Field<long>("_lineCompositeBytes"), "Removing traffic must release the target.");
        renderer.SetLayerRenderPlan(plan);
        renderer.CaptureOffscreenFrameForBenchmark();
        renderer.Dispose();
        Assert.AreEqual(0L, Field<long>("_lineCompositeBytes"), "Device teardown must release the target.");

        T Field<T>(string name) => (T)typeof(MapRenderer)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;

        async Task AddSource(long sourceId, byte[] encoded, TrafficFlowStyle? style)
        {
            var tile = await AzureOverlayAcquisitionSession.DecodeTrafficTileAsync(id, encoded, style);
            renderer.ActivateRasterTileSet(sourceId, 1, 1, scene, static _ => true,
                RasterSourceKind.Azure, LayerRenderKind.VectorPoints, false);
            renderer.AddVectorTexturesForBenchmark(tile.SpriteTextures);
            renderer.AddVectorTileForBenchmark(new(sourceId, id), tile.Features, tile.StyleAssets);
        }
    }

    private static byte Blue(MapRenderFrame frame, int x, int y) =>
        frame.Pixels.Span[(y * frame.Width + x) * 4];
}
