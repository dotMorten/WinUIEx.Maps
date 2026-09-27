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
