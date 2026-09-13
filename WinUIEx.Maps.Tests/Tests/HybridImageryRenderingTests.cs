using Microsoft.Extensions.Configuration;
using System.Reflection;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

[TestClass]
[DoNotParallelize]
public sealed class HybridImageryRenderingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(19)]
    [DataRow(22)]
    public async Task HybridBackgroundDoesNotCoverImageryAtNativeOrOverzoom(int displayZoom)
    {
        TileId id = new(19, 83976, 183082);
        byte[] bytes = new MapboxVectorTileBuilder()
            .AddLine("road", [new(0, 2048), new(4096, 2048)]).Build();
        VectorStyleAssets assets = CreateStyle(MapStyle.SatelliteWithRoads);
        byte[] pixels = new byte[256 * 256 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 211;
            pixels[i + 1] = 37;
            pixels[i + 2] = 19;
            pixels[i + 3] = 255;
        }
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await VerifyCompositionAsync(new(id, VectorTileDecoder.Decode(bytes), assets, [],
            new(id, pixels, 256, 256, 0, 0), 0, 0), timeout.Token, displayZoom, true);
    }

    private static VectorStyleAssets CreateStyle(MapStyle style) =>
        VectorStyleAssets.CreateForTest(style,
            """
            {"version":8,"layers":[
              {"type":"background","paint":{"background-color":"#eeeeee"}},
              {"type":"line","source-layer":"road","paint":{"line-color":"#00ff00","line-width":8}}
            ]}
            """u8.ToArray(), "{}"u8.ToArray(), [0, 0, 0, 0], 1, 1);

    [TestMethod]
    public async Task AzureHybridImagerySurvivesVectorComposition()
    {
        string? token = new ConfigurationBuilder()
            .AddUserSecrets<HybridImageryRenderingTests>(optional: true).Build()
            ["AzureMaps:MapServiceToken"];
        if (string.IsNullOrWhiteSpace(token))
            Assert.Inconclusive("Azure Maps test user secret is required.");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        AzureTileAcquisitionSession source = new(MapStyle.SatelliteWithRoads, token!, "en-US");
        Assert.AreEqual(LayerRenderKind.HybridTiles, source.RenderKind);
        Assert.AreEqual(RasterSourceKind.Azure, source.SourceKind);
        Assert.AreEqual(19, source.MaxSourceZoom);
        TileId id = new(18, 41988, 91541);
        DecodedVectorTile decoded;
        try
        {
            decoded = await source.GetVectorTileAsync(id, timeout.Token);
        }
        catch (Exception exception)
        {
            Assert.Fail($"Hybrid acquisition failed: {exception.GetType().Name} (0x{exception.HResult:X8}).");
            return;
        }
        Assert.IsNotNull(decoded.Background);
        await VerifyCompositionAsync(decoded, timeout.Token);
    }

    private async Task VerifyCompositionAsync(DecodedVectorTile decoded, CancellationToken cancellationToken,
        int? displayZoom = null, bool isSynthetic = false)
    {
        using RenderingEventListener listener = new(
            "TileUploadSummary", "TileUploadCommitSummary", "VectorTileCommitSummary",
            "RasterCoverageMilestone", "VectorPolygonRenderBatch", "VectorLineRenderBatch",
            "TileUploadFailed", "RendererFailure");
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(256, 256);
        TileId id = decoded.Id;
        int zoom = displayZoom ?? id.Zoom;
        double longitude = MapCamera.WorldXToLongitude((id.X + 0.5) / Math.Pow(2, id.Zoom));
        double latitude = MapCamera.WorldYToLatitude((id.Y + 0.5) / Math.Pow(2, id.Zoom));
        LayerRenderSnapshot hybrid = new(LayerRenderKind.HybridTiles, 0, 1, true, 1,
            TimeSpan.Zero, 0, 24, 0, 256, Style: (int)MapStyle.SatelliteWithRoads);
        renderer.SetLayerRenderPlan([hybrid]);
        renderer.SetCameraTargetImmediately(longitude, latitude, zoom, 256, 256);
        renderer.ActivateRasterTileSet(1, 1, 1,
            MapCamera.CreateScene(longitude, latitude, zoom, id.Zoom, 256, 256, 0, 0),
            tile => tile == id, RasterSourceKind.Azure, LayerRenderKind.HybridTiles, false);
        DecodedRasterTile raster = decoded.Background!.Value;
        VectorTileData tileData = new(
            new(1, id), decoded.Features, decoded.StyleAssets, decoded.SpriteTextures,
            new(new(1, id), raster.Pixels, raster.Width, raster.Height, 1, RasterSourceKind.Azure),
            1, (int)MapStyle.SatelliteWithRoads);
        Assert.IsTrue(await renderer.QueueHybridTileAsync(tileData, cancellationToken));
        // Offscreen initialization does not start the presentation/upload threads.
        typeof(MapRenderer).GetMethod("ProcessRasterPixelUploads",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(renderer, null);
        MapRenderFrame frame = renderer.CaptureOffscreenFrameForBenchmark();
        Assert.AreEqual(1, Convert.ToInt32(listener.Events("TileUploadCommitSummary").Single().Payload[0]));
        Assert.AreEqual(1, Convert.ToInt32(listener.Events("VectorTileCommitSummary").Single().Payload[1]));
        Assert.IsTrue(listener.Events("RasterCoverageMilestone")
            .Any(e => Equals(e.Payload[3], "OpaqueCoverage")));
        renderer.SetLayerRenderPlan([hybrid with { Kind = LayerRenderKind.RasterTiles }]);
        MapRenderFrame imagery = renderer.CaptureOffscreenFrameForBenchmark();
        if (isSynthetic)
        {
            Assert.AreEqual((byte)211, imagery.Pixels.Span[0], "Raster-only control must contain the uploaded imagery.");
            int road = (128 * 256 + 128) * 4;
            Assert.AreEqual((byte)255, frame.Pixels.Span[road + 1], "Road must render above imagery.");
        }
        int matching = 0;
        int changed = 0;
        for (int i = 0; i < frame.Pixels.Length; i += 4)
        {
            bool same = true;
            for (int channel = 0; channel < 3; channel++)
                same &= Math.Abs(frame.Pixels.Span[i + channel] - imagery.Pixels.Span[i + channel]) <= 2;
            if (same) matching++; else changed++;
        }
        foreach (string name in new[] { "TileUploadCommitSummary", "VectorTileCommitSummary",
            "RasterCoverageMilestone", "VectorPolygonRenderBatch", "VectorLineRenderBatch" })
            foreach (var captured in listener.Events(name))
                TestContext.WriteLine($"{name}: {string.Join(", ", captured.Payload)}");
        TestContext.WriteLine($"Imagery matching pixels: {matching}; overlay changed pixels: {changed}");
        Assert.IsEmpty(listener.Events("TileUploadFailed"));
        Assert.IsEmpty(listener.Events("RendererFailure"));
        Assert.IsGreaterThan(1000, matching, "Hybrid must preserve actual satellite pixels beneath vector overlays.");
        Assert.IsGreaterThan(0, changed, "Vector overlays must contribute pixels above imagery.");
        if (isSynthetic)
        {
            Assert.AreEqual(256 * 248, matching, "Only the eight-pixel road should replace imagery.");
            // Prepared geometry uses the same style resolution, not a separate
            // background policy. Packing returns line vertices high / polygon low.
            long prepared = renderer.PrepareAndUploadVectorTileForBenchmark(
                decoded.Features, decoded.StyleAssets, id, zoom, 256, 256);
            Assert.AreEqual(0u, (uint)prepared, "Prepared hybrid geometry must not contain a canvas background.");
            Assert.IsGreaterThan(0, (int)(prepared >> 32));

            MapScene scene = MapCamera.CreateScene(longitude, latitude, zoom, id.Zoom, 256, 256, 0, 0);
            renderer.SetLayerRenderPlan([hybrid with { Kind = LayerRenderKind.VectorPoints, Style = (int)MapStyle.Road }]);
            renderer.ActivateRasterTileSet(1, 2, 2, scene, tile => tile == id,
                RasterSourceKind.Azure, LayerRenderKind.VectorPoints, true);
            Assert.IsTrue(await renderer.QueueVectorTileAsync(tileData with
            {
                Generation = 2, Background = null, Style = (int)MapStyle.Road,
                StyleAssets = CreateStyle(MapStyle.Road),
            }, cancellationToken));
            MapRenderFrame roadFrame = renderer.CaptureOffscreenFrameForBenchmark();
            Assert.AreEqual((byte)238, roadFrame.Pixels.Span[0], "Road must retain its vector canvas background.");

            renderer.SetLayerRenderPlan([hybrid]);
            renderer.ActivateRasterTileSet(1, 3, 3, scene, tile => tile == id,
                RasterSourceKind.Azure, LayerRenderKind.HybridTiles, true);
            Assert.IsTrue(await renderer.QueueHybridTileAsync(tileData with
            {
                Generation = 3,
                Background = tileData.Background!.Value with { Generation = 3 },
            }, cancellationToken));
            typeof(MapRenderer).GetMethod("ProcessRasterPixelUploads",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(renderer, null);
            for (int index = 0; index < 3; index++)
            {
                MapRenderFrame switched = renderer.CaptureOffscreenFrameForBenchmark();
                Assert.IsTrue(frame.Pixels.Span.SequenceEqual(switched.Pixels.Span),
                    "Returning from Road must restore imagery and roads, including cached frames.");
            }
        }
        Assert.IsTrue(listener.Events("TileUploadCommitSummary").All(e =>
            Convert.ToInt32(e.Payload[1]) == 0 && Convert.ToInt32(e.Payload[2]) == 0),
            "No hybrid upload should be discarded as stale or duplicate.");
        Assert.IsEmpty(listener.Events("TileUploadFailed"));
        Assert.IsEmpty(listener.Events("RendererFailure"));
    }
}
