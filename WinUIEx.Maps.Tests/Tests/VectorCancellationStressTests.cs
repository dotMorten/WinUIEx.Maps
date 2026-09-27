using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

[TestClass]
[DoNotParallelize]
public sealed class VectorCancellationStressTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void HeldPreparationSurvivesRepeatedCrossTierSupersessionWithoutCapturingMoreInputs()
    {
        var (features, oldStyle, latestStyle) = CreateData();
        using MapRenderer renderer = CreateRenderer();
        using MapRenderer reference = CreateRenderer();
        using RenderingEventListener events = new("VectorRetainedMemory", "VectorCacheOwnership", "RendererFailure");
        long version = 0;
        long? settledBytes = null;
        double[] zooms = [4.25, 17.75, 3, 19, 8.5, 14.125];
        for (int cycle = 0; cycle < 6; cycle++)
        {
            MapScene scene = ResetScene(renderer, 6, features, oldStyle, ref version);
            renderer.RenderOffscreenFrameForBenchmark();
            using ManualResetEventSlim started = new();
            using ManualResetEventSlim release = new();
            int starts = 0;
            renderer.VectorGeometryPreparationStartingForTest = () =>
            {
                Interlocked.Increment(ref starts);
                started.Set();
                if (!release.Wait(TimeSpan.FromSeconds(20)))
                    throw new TimeoutException("Cross-tier test did not release preparation.");
            };
            try
            {
                InvalidateScene(renderer, scene, ++version);
                renderer.RenderOffscreenFrameForBenchmark();
                Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
                foreach (double zoom in zooms)
                {
                    scene = ResetScene(renderer, zoom, features, latestStyle, ref version);
                    renderer.RenderOffscreenFrameForBenchmark();
                    InvalidateScene(renderer, scene, ++version);
                    renderer.RenderOffscreenFrameForBenchmark();
                    Assert.AreEqual(1, renderer.ActiveVectorGeometryPreparations);
                    Assert.AreEqual(0, renderer.CompletedVectorGeometryPreparations);
                    Assert.AreEqual(1, Volatile.Read(ref starts));
                }
            }
            finally
            {
                release.Set();
                Assert.IsTrue(SpinWait.SpinUntil(() => renderer.ActiveVectorGeometryPreparations == 0,
                    TimeSpan.FromSeconds(5)));
                renderer.VectorGeometryPreparationStartingForTest = null;
            }
            Assert.AreEqual(0, renderer.CompletedVectorGeometryPreparations,
                "Cancelled work must release rather than publish its obsolete result.");
            renderer.RenderOffscreenFrameForBenchmark();
            Assert.IsTrue(SpinWait.SpinUntil(() => renderer.ActiveVectorGeometryPreparations == 0,
                TimeSpan.FromSeconds(5)));
            var actual = renderer.CaptureOffscreenFrameForBenchmark();
            var ownership = events.Events("VectorCacheOwnership")[^1].Payload;
            ResetScene(reference, zooms[^1], features, latestStyle, ref version);
            AssertPixelsEqual(reference.CaptureOffscreenFrameForBenchmark(), actual);
            Assert.AreEqual(0, renderer.ActiveVectorGeometryPreparations);
            Assert.AreEqual(0, renderer.CompletedVectorGeometryPreparations);
            long bytes = Convert.ToInt64(ownership[2]) + Convert.ToInt64(ownership[3]);
            settledBytes ??= bytes;
            Assert.AreEqual(settledBytes.Value, bytes, "Revisiting the same final scene must not accumulate retained data.");
            Assert.AreEqual(0L, Convert.ToInt64(ownership[4]));
            Assert.AreEqual(MapRenderer.NativeGeometryBuffer.AllocatedBytes, MapRenderer.NativeGeometryBuffer.ReleasedBytes);
        }
        Assert.IsEmpty(events.Events("RendererFailure"));
        Assert.IsTrue(events.Events("VectorRetainedMemory").All(e =>
            Convert.ToInt32(e.Payload[6]) <= 1 && Convert.ToInt32(e.Payload[7]) <= 1));
        TestContext.WriteLine("6 held owners, 36 cross-tier reversals/arrivals (zoom 3–19), 6 latest-scene pixel comparisons; running <=1, completed <=1, settled bytes constant, scratch zero.");
    }

    [TestMethod]
    public void CompletedButUncommittedGeometryIsReleasedBeforeCrossTierReplacement()
    {
        var (features, oldStyle, latestStyle) = CreateData();
        using MapRenderer renderer = CreateRenderer();
        using MapRenderer reference = CreateRenderer();
        long version = 0;
        for (int cycle = 0; cycle < 8; cycle++)
        {
            MapScene oldScene = ResetScene(renderer, 14, features, oldStyle, ref version);
            renderer.RenderOffscreenFrameForBenchmark();
            InvalidateScene(renderer, oldScene, ++version);
            renderer.RenderOffscreenFrameForBenchmark();
            Assert.IsTrue(SpinWait.SpinUntil(() => renderer.ActiveVectorGeometryPreparations == 0 &&
                renderer.CompletedVectorGeometryPreparations == 1, TimeSpan.FromSeconds(5)));
            double zoom = cycle % 2 == 0 ? 5.5 : 18.25;
            ResetScene(renderer, zoom, features, latestStyle, ref version);
            Assert.AreEqual(0, renderer.CompletedVectorGeometryPreparations);
            ResetScene(reference, zoom, features, latestStyle, ref version);
            AssertPixelsEqual(reference.CaptureOffscreenFrameForBenchmark(), renderer.CaptureOffscreenFrameForBenchmark());
            Assert.AreEqual(MapRenderer.NativeGeometryBuffer.AllocatedBytes, MapRenderer.NativeGeometryBuffer.ReleasedBytes);
        }
        renderer.RemoveRasterTileSource(1);
        Assert.AreEqual(0, renderer.CompletedVectorGeometryPreparations);
        Assert.AreEqual(0, renderer.ActiveVectorGeometryPreparations);
        TestContext.WriteLine("8 completed-before-commit replacements across zooms 5.5/14/18.25; obsolete queue drained, reference pixels match, no retained scratch.");
    }

    private static MapRenderer CreateRenderer()
    {
        MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(256, 256);
        renderer.SetLayerRenderPlan([new(LayerRenderKind.VectorPoints, 0, 1, true, 1,
            TimeSpan.Zero, 0, 24, 0, 256, Style: (int)MapStyle.Road)]);
        return renderer;
    }

    private static MapScene ResetScene(MapRenderer renderer, double zoom, VectorTileFeatureCollection features,
        VectorStyleAssets style, ref long version)
    {
        double longitude = zoom < 10 ? 30 : -122.33;
        double latitude = zoom < 10 ? -20 : 47.6;
        double pitch = zoom > 10 ? 45 : 0;
        renderer.SetCameraTargetImmediately(longitude, latitude, zoom, 256, 256, 25, pitch);
        MapScene scene = MapCamera.CreateScene(longitude, latitude, zoom, (int)Math.Floor(zoom), 256, 256, 25, pitch);
        renderer.ActivateRasterTileSet(1, 1, ++version, scene, _ => true,
            RasterSourceKind.Custom, LayerRenderKind.VectorPoints, true);
        foreach (TileId id in scene.RequiredTiles)
            renderer.AddVectorTileForBenchmark(new(1, id), features, style);
        return scene;
    }

    private static void InvalidateScene(MapRenderer renderer, MapScene scene, long version) =>
        renderer.ActivateRasterTileSet(1, 1, version, scene, _ => true,
            RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);

    private static (VectorTileFeatureCollection, VectorStyleAssets, VectorStyleAssets) CreateData()
    {
        byte[] bytes = new MapboxVectorTileBuilder()
            .AddPolygon("area", [[new(0, 0), new(4096, 0), new(4096, 4096), new(0, 4096)]])
            .AddLine("road", [new(0, 0), new(4096, 4096)]).Build();
        VectorStyleAssets Style(string color) => TestVectorTileSource.Create(new(14, 4823, 6160), bytes,
            $$$"""
            {"version":8,"layers":[
              {"type":"background","paint":{"background-color":"{{{color}}}"}},
              {"type":"fill","source-layer":"area","paint":{"fill-color":"{{{color}}}"}},
              {"type":"line","source-layer":"road","paint":{"line-color":"#000000","line-width":4}}]}
            """, "{}", [0, 0, 0, 0], 1, 1).StyleAssets;
        return (VectorTileDecoder.Decode(bytes), Style("#ee3020"), Style("#2080ee"));
    }

    private static void AssertPixelsEqual(MapRenderFrame expected, MapRenderFrame actual)
    {
        Assert.AreEqual(expected.Pixels.Length, actual.Pixels.Length);
        int differences = 0;
        for (int i = 0; i < expected.Pixels.Length; i++)
            if (Math.Abs(expected.Pixels.Span[i] - actual.Pixels.Span[i]) > 2)
                differences++;
        Assert.AreEqual(0, differences, "No obsolete prepared color/geometry may be committed after the final scene.");
    }
}
