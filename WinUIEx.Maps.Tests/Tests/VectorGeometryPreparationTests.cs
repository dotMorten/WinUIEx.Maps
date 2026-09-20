using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

[TestClass]
[DoNotParallelize]
public sealed class VectorGeometryPreparationTests
{
    [TestMethod]
    public void SupersededPreparationReleasesBeforeLatestInputIsCaptured()
    {
        TileId id = new(14, 4823, 6160);
        byte[] bytes = new MapboxVectorTileBuilder()
            .AddLine("road", [new(0, 2048), new(4096, 2048)])
            .Build();
        TestVectorTileSource source = TestVectorTileSource.Create(id, bytes,
            """
            {"version":8,"layers":[{"type":"line","source-layer":"road",
              "paint":{"line-color":"#000000","line-width":4}}]}
            """, "{}", [0, 0, 0, 0], 1, 1);
        VectorTileFeatureCollection features = VectorTileDecoder.Decode(bytes);
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(256, 256);
        var center = source.TileCenter;
        renderer.SetCameraTargetImmediately(center.Longitude, center.Latitude, id.Zoom, 256, 256);
        renderer.SetLayerRenderPlan(
        [
            new LayerRenderSnapshot(LayerRenderKind.VectorPoints, 0, 1, true, 1,
                TimeSpan.Zero, 0, 24, 0, 256, Style: (int)MapStyle.Road),
        ]);
        renderer.ActivateRasterTileSet(1, 1, 1,
            MapCamera.CreateScene(center.Longitude, center.Latitude, id.Zoom, id.Zoom, 256, 256),
            tile => tile == id, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        renderer.AddVectorTileForBenchmark(new(1, id), features, source.StyleAssets);
        renderer.RenderOffscreenFrameForBenchmark();

        using ManualResetEventSlim started = new();
        using ManualResetEventSlim release = new();
        int starts = 0;
        renderer.VectorGeometryPreparationStartingForTest = () =>
        {
            Interlocked.Increment(ref starts);
            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The preparation test did not release its gate.");
        };
        try
        {
            renderer.ActivateRasterTileSet(1, 1, 2,
                MapCamera.CreateScene(center.Longitude, center.Latitude, id.Zoom, id.Zoom, 256, 256),
                tile => tile == id, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
            renderer.RenderOffscreenFrameForBenchmark();
            Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
            for (int index = 0; index < 12; index++)
            {
                renderer.ActivateRasterTileSet(1, 1, index + 3,
                    MapCamera.CreateScene(center.Longitude, center.Latitude, id.Zoom, id.Zoom, 256, 256),
                    tile => tile == id, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
                renderer.RenderOffscreenFrameForBenchmark();
                Assert.AreEqual(1, renderer.ActiveVectorGeometryPreparations);
            }
            Assert.AreEqual(1, Volatile.Read(ref starts));
        }
        finally
        {
            release.Set();
            Assert.IsTrue(SpinWait.SpinUntil(
                () => renderer.ActiveVectorGeometryPreparations == 0, TimeSpan.FromSeconds(5)));
        }
        renderer.VectorGeometryPreparationStartingForTest = null;
        renderer.RenderOffscreenFrameForBenchmark();
        Assert.IsTrue(SpinWait.SpinUntil(
            () => renderer.ActiveVectorGeometryPreparations == 0, TimeSpan.FromSeconds(5)));
        renderer.RenderOffscreenFrameForBenchmark();
    }
}
