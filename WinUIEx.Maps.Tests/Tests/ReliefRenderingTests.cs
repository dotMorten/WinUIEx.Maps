using Microsoft.Extensions.Configuration;
using System.Reflection;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ReliefRenderingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TerrainReplacementBlendsOnceOnEveryZoomFrame(bool multipleTiers)
    {
        using RenderingEventListener listener = new("TileUploadCommitSummary", "VectorTileCommitSummary",
            "RasterCoverageMilestone", "VectorPolygonRenderBatch", "VectorGeometryFallbackOpacitySummary",
            "VectorGeometryFrameCacheSummary", "VectorGeometryPreparationSummary",
            "VectorGeometryDeferredRebuildSummary", "MapFrameStageTiming");
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(256, 256);
        VectorStyleAssets assets = VectorStyleAssets.CreateForTest(MapStyle.RoadShadedRelief,
            """
            {"version":8,"sources":{"base":{"type":"vector","url":"tilesetId=microsoft.base"},
            "terrain":{"type":"raster","url":"microsoft.terra.main"}},"layers":[
            {"type":"background","paint":{"background-color":"#ff0000"}},
            {"type":"raster","source":"terrain","maxzoom":15,"paint":{"raster-fade-duration":0}}]}
            """u8.ToArray(), "{}"u8.ToArray(), [0, 0, 0, 0], 1, 1);
        var features = VectorTileDecoder.Decode(new MapboxVectorTileBuilder().Build());
        LayerRenderSnapshot layer = new(LayerRenderKind.HybridTiles, 0, 1, true, 1,
            TimeSpan.Zero, 0, 24, 0, 256, Style: 13);
        renderer.SetLayerRenderPlan([layer]);
        double lon = MapCamera.WorldXToLongitude(10.5 / 64);
        double lat = MapCamera.WorldYToLatitude(23.5 / 64);
        int version = 0;
        void Activate(double zoom)
        {
            renderer.SetCameraTargetImmediately(lon, lat, zoom, 256, 256);
            renderer.ActivateRasterTileSet(1, 1, ++version,
                MapCamera.CreateScene(lon, lat, zoom, (int)zoom, 256, 256, 0, 0),
                _ => true, RasterSourceKind.Azure, LayerRenderKind.HybridTiles, false);
        }
        async Task AddAsync(TileId id)
        {
            byte[] pixels = new byte[16 * 16 * 4];
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 128;
            Assert.IsTrue(await renderer.QueueHybridTileAsync(new(new(1, id), features, assets, [],
                new(new(1, id), pixels, 16, 16, 1, RasterSourceKind.Azure), 1, 13),
                TestContext.CancellationToken));
            DrainUploads(renderer);
        }
        Activate(6);
        await AddAsync(new(6, 10, 23));
        List<string> failures = [];
        int minimum = 255, maximum = 0, frames = 0;
        int frameCount = multipleTiers ? 120 : 80;
        for (int frame = 0; frame < frameCount; frame++)
        {
            double zoom = 6 + (frame < frameCount / 2 ? frame : frameCount - 1 - frame) * .045;
            Activate(zoom);
            // Partial arrival, then all four children; parent and children remain cached
            // through repeated scene publication in both zoom directions.
            if (frame is 24 or 28 or 32 or 36)
            {
                int child = (frame - 24) / 4;
                await AddAsync(new(7, 20 + child % 2, 46 + child / 2));
            }
            if (multipleTiers && frame is 48 or 50 or 52 or 54)
            {
                int child = (frame - 48) / 2;
                await AddAsync(new(8, 41 + child % 2, 93 + child / 2));
            }
            MapRenderFrame capture = renderer.CaptureOffscreenFrameForBenchmark();
            int low = 255, high = 0;
            for (int y = 2; y < 254; y++)
                for (int x = 2; x < 254; x++)
                {
                    int red = capture.Pixels.Span[(y * 256 + x) * 4 + 2];
                    low = Math.Min(low, red);
                    high = Math.Max(high, red);
                }
            minimum = Math.Min(minimum, low);
            maximum = Math.Max(maximum, high);
            if (low < 126 || high > 128) failures.Add($"{frame}:{low}..{high}");
            frames++;
        }
        TestContext.WriteLine($"Temporal frames={frames}; red range={minimum}..{maximum}; bad frames={failures.Count}; " +
            $"raster commits={listener.Events("TileUploadCommitSummary").Count()}; " +
            $"vector commits={listener.Events("VectorTileCommitSummary").Count()}; " +
            $"coverage milestones={listener.Events("RasterCoverageMilestone").Count()}; " +
            $"polygon batches={listener.Events("VectorPolygonRenderBatch").Count()}; " +
            $"frame stages={listener.Events("MapFrameStageTiming").Count()}");
        Assert.IsEmpty(failures, string.Join("; ", failures.Take(16)));
        Assert.HasCount(frameCount, listener.Events("MapFrameStageTiming"));
        Assert.HasCount(multipleTiers ? 9 : 5, listener.Events("TileUploadCommitSummary"));
        Assert.HasCount(multipleTiers ? 9 : 5, listener.Events("VectorTileCommitSummary"));
        // Hold the final zoom, then admit a same-level neighbor while panning.
        // This exercises the real deferred merge and background-prepared cache,
        // not the benchmark hook (which builds and disposes a separate frame).
        for (int frame = 0; frame < 16; frame++)
        {
            if (frame == 2) await AddAsync(new(6, 11, 23));
            renderer.SetCameraTargetImmediately(lon + .001 * frame, lat, 6, 256, 256);
            MapRenderFrame capture = renderer.CaptureOffscreenFrameForBenchmark();
            for (int y = 2; y < 254; y++)
                for (int x = 2; x < 254; x++)
                    Assert.AreEqual(127d, capture.Pixels.Span[(y * 256 + x) * 4 + 2], 1,
                        $"Deferred/prepared frame={frame}, pixel={x},{y}");
            await Task.Delay(10, TestContext.CancellationToken);
        }
        Assert.IsNotEmpty(listener.Events("VectorGeometryDeferredRebuildSummary"));
        Assert.IsTrue(listener.Events("VectorGeometryPreparationSummary")
            .Any(e => Convert.ToInt32(e.Payload[1]) == 1), "A real prepared frame must be accepted.");
        TestContext.WriteLine($"Deferred/prepared frames=16; deferred events={listener.Events("VectorGeometryDeferredRebuildSummary").Length}; " +
            $"accepted preparation events={listener.Events("VectorGeometryPreparationSummary").Count(e => Convert.ToInt32(e.Payload[1]) == 1)}");
    }

    [TestMethod]
    [DataRow(8, false)]
    [DataRow(10, false)]
    [DataRow(14, false)]
    [DataRow(10, true)]
    public async Task TerrainCropsMatchAncestorSamplingDuringPartialReplacement(int tileZoom, bool liveBaker)
    {
        using RenderingEventListener listener = new("TileUploadCommitSummary", "VectorTileCommitSummary",
            "RasterCoverageMilestone", "VectorPolygonRenderBatch", "MapFrameStageTiming");
        using MapRenderer reference = new();
        using MapRenderer renderer = new();
        VectorStyleAssets assets = VectorStyleAssets.CreateForTest(MapStyle.RoadShadedRelief,
            """
            {"version":8,"sources":{"base":{"type":"vector","url":"tilesetId=microsoft.base"},
            "terrain":{"type":"raster","url":"microsoft.terra.main"}},"layers":[
            {"type":"background","paint":{"background-color":"#ffffff"}},
            {"type":"raster","source":"terrain","maxzoom":15,"paint":{"raster-fade-duration":0}}]}
            """u8.ToArray(), "{}"u8.ToArray(), [0, 0, 0, 0], 1, 1);
        var features = VectorTileDecoder.Decode(new MapboxVectorTileBuilder().Build());
        LayerRenderSnapshot layer = new(LayerRenderKind.HybridTiles, 0, 1, true, 1,
            TimeSpan.Zero, 0, 24, 0, 256, Style: 13);
        byte[] pixels = new byte[256 * 256 * 4];
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                int p = (y * 256 + x) * 4;
                pixels[p] = (byte)(x % 8 * 30);
                pixels[p + 1] = (byte)(y % 8 * 30);
                pixels[p + 2] = 40;
                pixels[p + 3] = (byte)(64 + (x + y) % 8 * 24);
            }
        DecodedRasterTile parent = new(new(6, 10, 23), pixels, 256, 256, 0, 0);
        int scale = 1 << (tileZoom - 6);
        int centerX = 10 * scale + scale / 2, centerY = 23 * scale + scale / 2;
        AzureTileAcquisitionSession? source = null;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        async Task<DecodedRasterTile> AcquireAsync(TileId id)
        {
            try
            {
                return (await source!.GetVectorTileAsync(id, timeout.Token)).Background!.Value;
            }
            catch (Exception e)
            {
                Assert.Fail($"Terrain fixture acquisition failed: {e.GetType().Name} (0x{e.HResult:X8}).");
                return default;
            }
        }
        if (liveBaker)
        {
            string? token = new ConfigurationBuilder().AddUserSecrets<ReliefRenderingTests>(true)
                .Build()["AzureMaps:MapServiceToken"];
            if (string.IsNullOrWhiteSpace(token)) Assert.Inconclusive("Azure Maps test user secret is required.");
            source = new(MapStyle.RoadShadedRelief, token!, "en-US");
            parent = await AcquireAsync(new(6, 10, 22));
            centerX = 166;
            centerY = 353;
        }
        double lon = MapCamera.WorldXToLongitude(centerX / Math.Pow(2, tileZoom));
        double lat = MapCamera.WorldYToLatitude(centerY / Math.Pow(2, tileZoom));
        void Activate(MapRenderer target, double zoom, int version, int sourceZoom)
        {
            target.SetCameraTargetImmediately(lon, lat, zoom, 256, 256);
            target.ActivateRasterTileSet(1, 1, version,
                MapCamera.CreateScene(lon, lat, zoom, sourceZoom, 256, 256, 0, 0),
                _ => true, RasterSourceKind.Azure, LayerRenderKind.HybridTiles, false);
        }
        async Task AddAsync(MapRenderer target, DecodedRasterTile raster)
        {
            Assert.IsTrue(await target.QueueHybridTileAsync(new(new(1, raster.Id), features, assets, [],
                new(new(1, raster.Id), raster.Pixels, raster.Width, raster.Height,
                    1, RasterSourceKind.Azure) { TextureTransform = raster.TextureTransform },
                1, 13), TestContext.CancellationToken));
            DrainUploads(target);
        }
        foreach (MapRenderer target in new[] { reference, renderer })
        {
            target.InitializeOffscreenForBenchmark(256, 256);
            target.SetLayerRenderPlan([layer]);
            Activate(target, tileZoom, 1, 6);
            await AddAsync(target, parent);
        }
        int worst = 0, badFrames = 0, edgeWorst = 0;
        for (int frame = 0; frame < 48; frame++)
        {
            double zoom = tileZoom + (frame < 24 ? frame : 47 - frame) * .01;
            Activate(reference, zoom, frame + 2, 6);
            Activate(renderer, zoom, frame + 2, tileZoom);
            if (frame is 8 or 16 or 24 or 32)
            {
                int child = frame / 8 - 1;
                TileId id = new(tileZoom, centerX - 1 + child % 2, centerY - 1 + child / 2);
                await AddAsync(renderer, liveBaker ? await AcquireAsync(id) :
                    AzureTileAcquisitionSession.CropReliefTile(parent, id));
            }
            MapRenderFrame expected = reference.CaptureOffscreenFrameForBenchmark();
            MapRenderFrame actual = renderer.CaptureOffscreenFrameForBenchmark();
            int frameWorst = 0;
            for (int y = 2; y < 254; y++)
                for (int x = 2; x < 254; x++)
                    for (int channel = 0; channel < 3; channel++)
                    {
                        int index = (y * 256 + x) * 4 + channel;
                        int delta = Math.Abs(expected.Pixels.Span[index] - actual.Pixels.Span[index]);
                        frameWorst = Math.Max(frameWorst, delta);
                        if (Math.Abs(x - 128) < 3 || Math.Abs(y - 128) < 3)
                            edgeWorst = Math.Max(edgeWorst, delta);
                    }
            worst = Math.Max(worst, frameWorst);
            if (frame is 0 or 7 or 8 or 16 or 32)
                TestContext.WriteLine($"frame={frame}; error={frameWorst}; center reference={string.Join(",", expected.Pixels.Span.Slice((128 * 256 + 128) * 4, 4).ToArray())}; actual={string.Join(",", actual.Pixels.Span.Slice((128 * 256 + 128) * 4, 4).ToArray())}");
            if (frameWorst > 2) badFrames++;
        }
        TestContext.WriteLine($"Crop zoom={tileZoom}; liveBaker={liveBaker}; frames=48; bad frames={badFrames}; max channel error={worst}; " +
            $"boundary error={edgeWorst}; raster commits={listener.Events("TileUploadCommitSummary").Count()}; " +
            $"vector commits={listener.Events("VectorTileCommitSummary").Count()}; stages={listener.Events("MapFrameStageTiming").Count()}");
        Assert.AreEqual(0, badFrames, $"max channel error={worst}; boundary error={edgeWorst}");
        Assert.HasCount(96, listener.Events("MapFrameStageTiming"));
        Assert.HasCount(6, listener.Events("TileUploadCommitSummary"));
        Assert.HasCount(6, listener.Events("VectorTileCommitSummary"));
    }

    [TestMethod]
    public async Task OrdinaryRasterFallbackRemainsOpaqueDuringPartialReplacement()
    {
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(256, 256);
        double lon = MapCamera.WorldXToLongitude(10.5 / 64);
        double lat = MapCamera.WorldYToLatitude(23.5 / 64);
        renderer.SetLayerRenderPlan([new(LayerRenderKind.RasterTiles, 0, 1, true, 1,
            TimeSpan.Zero, 0, 24, 0, 256)]);
        async Task AddAsync(TileId id)
        {
            Assert.IsTrue(await renderer.QueueRasterUploadAsync(
                new(new(1, id), [32, 64, 128, 255], 1, 1, 1, RasterSourceKind.Custom),
                TestContext.CancellationToken));
            DrainUploads(renderer);
        }
        for (int frame = 0; frame < 80; frame++)
        {
            double zoom = 6 + (frame < 40 ? frame : 79 - frame) * .045;
            renderer.SetCameraTargetImmediately(lon, lat, zoom, 256, 256);
            renderer.ActivateRasterTileSet(1, 1, frame + 1,
                MapCamera.CreateScene(lon, lat, zoom, (int)zoom, 256, 256, 0, 0),
                _ => true, RasterSourceKind.Custom, LayerRenderKind.RasterTiles, false);
            if (frame == 0) await AddAsync(new(6, 10, 23));
            if (frame is 24 or 28 or 32 or 36)
            {
                int child = (frame - 24) / 4;
                await AddAsync(new(7, 20 + child % 2, 46 + child / 2));
            }
            var capture = renderer.CaptureOffscreenFrameForBenchmark();
            for (int y = 2; y < 254; y++)
                for (int x = 2; x < 254; x++)
                {
                    int index = (y * 256 + x) * 4;
                    Assert.AreEqual((byte)32, capture.Pixels.Span[index], $"frame={frame}");
                    Assert.AreEqual((byte)64, capture.Pixels.Span[index + 1], $"frame={frame}");
                    Assert.AreEqual((byte)128, capture.Pixels.Span[index + 2], $"frame={frame}");
                }
        }
    }

    [TestMethod]
    public async Task TerrainZoom15SentinelDoesNotMaskFallbackOnZoomOut()
    {
        using RenderingEventListener listener = new("TileUploadCommitSummary", "VectorTileCommitSummary",
            "RasterCoverageMilestone", "MapFrameStageTiming");
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(256, 256);
        VectorStyleAssets assets = CreateStyle();
        var features = VectorTileDecoder.Decode(new MapboxVectorTileBuilder().Build());
        TileId sentinel = new(15, (10 << 9) + 256, (23 << 9) + 256);
        double lon = MapCamera.WorldXToLongitude((sentinel.X + .5) / 32768);
        double lat = MapCamera.WorldYToLatitude((sentinel.Y + .5) / 32768);
        renderer.SetLayerRenderPlan([new(LayerRenderKind.HybridTiles, 0, 1, true, 1,
            TimeSpan.Zero, 0, 24, 0, 256, Style: 13)]);
        void Activate(double zoom, int sourceZoom, int version)
        {
            renderer.SetCameraTargetImmediately(lon, lat, zoom, 256, 256);
            renderer.ActivateRasterTileSet(1, 1, version,
                MapCamera.CreateScene(lon, lat, zoom, sourceZoom, 256, 256, 0, 0),
                _ => true, RasterSourceKind.Azure, LayerRenderKind.HybridTiles, false);
        }
        async Task AddAsync(TileId id, byte alpha)
        {
            Assert.IsTrue(await renderer.QueueHybridTileAsync(new(new(1, id), features, assets, [],
                new(new(1, id), [0, 0, 0, alpha], 1, 1, 1, RasterSourceKind.Azure), 1, 13),
                TestContext.CancellationToken));
            DrainUploads(renderer);
        }
        Activate(14.8, 6, 1);
        await AddAsync(new(6, 10, 23), 128);
        for (int frame = 0; frame < 82; frame++)
        {
            double zoom = 14.8 + (frame <= 40 ? frame : 81 - frame) * .01;
            Activate(zoom, (int)zoom, frame + 2);
            if (frame == 25) await AddAsync(sentinel, 0);
            int milestones = listener.Events("RasterCoverageMilestone").Length;
            var capture = renderer.CaptureOffscreenFrameForBenchmark();
            double expected = zoom >= 15 ? 238 : 238 * (1 - .5 * 128 / 255);
            for (int y = 2; y < 254; y++)
                for (int x = 2; x < 254; x++)
                    Assert.AreEqual(expected, capture.Pixels.Span[(y * 256 + x) * 4 + 2], 1,
                        $"frame={frame}; zoom={zoom}; pixel={x},{y}");
            if (zoom >= 15)
                Assert.HasCount(milestones, listener.Events("RasterCoverageMilestone"));
        }
        Assert.HasCount(82, listener.Events("MapFrameStageTiming"));
        Assert.HasCount(2, listener.Events("TileUploadCommitSummary"));
        Assert.HasCount(2, listener.Events("VectorTileCommitSummary"));
        TestContext.WriteLine("Zoom15 out-and-back frames=82; intensity errors=0; sentinel and parent atomic commits=2.");
    }

    [TestMethod]
    [DataRow(6, 0, 0)]
    [DataRow(8, 1, 2)]
    [DataRow(15, 0, 0)]
    public async Task AzureTerrainAcquiresAndCompositesAtNativeOverzoomAndHiddenZoom(int zoom, int offsetX, int offsetY)
    {
        string? token = new ConfigurationBuilder().AddUserSecrets<ReliefRenderingTests>(true)
            .Build()["AzureMaps:MapServiceToken"];
        if (string.IsNullOrWhiteSpace(token))
            Assert.Inconclusive("Azure Maps test user secret is required.");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        AzureTileAcquisitionSession source = new(MapStyle.RoadShadedRelief, token!, "en-US");
        Assert.AreEqual(LayerRenderKind.HybridTiles, source.RenderKind);
        Assert.AreEqual(21, source.MaxSourceZoom, "Terrain must not cap base vector resolution to six.");
        TileId id = new(zoom, (10 << (zoom - 6)) + offsetX, (23 << (zoom - 6)) + offsetY);
        DecodedVectorTile decoded;
        DecodedRasterTile? parent = null;
        try
        {
            decoded = await source.GetVectorTileAsync(id, timeout.Token);
            if (zoom is > 6 and < 15)
                parent = (await source.GetVectorTileAsync(new(6, 10, 23), timeout.Token)).Background;
        }
        catch (Exception e)
        {
            Assert.Fail($"Relief acquisition failed: {e.GetType().Name} (0x{e.HResult:X8}).");
            return;
        }
        Assert.IsNotNull(decoded.Background);
        Assert.IsNotNull(decoded.StyleAssets.Relief);
        Assert.AreEqual(1d, decoded.StyleAssets.Relief.GetOpacity(6));
        Assert.AreEqual(0d, decoded.StyleAssets.Relief.GetOpacity(15));
        Assert.AreEqual(TimeSpan.Zero, decoded.StyleAssets.Relief.FadeDuration);
        TestContext.WriteLine($"Style=13; zoom={zoom}; raster width={decoded.Background.Value.Width}");
        if (parent is { } terrain)
        {
            var crop = decoded.Background.Value;
            int width = (int)terrain.Width >> (zoom - 6);
            int height = (int)terrain.Height >> (zoom - 6);
            int left = Math.Max(0, offsetX * width - 1);
            int top = Math.Max(0, offsetY * height - 1);
            Assert.AreEqual(width + 2, (int)crop.Width);
            Assert.AreEqual(height + 2, (int)crop.Height);
            for (int row = 0; row < crop.Height; row++)
            {
                int start = ((top + row) * (int)terrain.Width + left) * 4;
                Assert.IsTrue(terrain.Pixels.AsSpan(start, (int)crop.Width * 4)
                    .SequenceEqual(crop.Pixels.AsSpan(row * (int)crop.Width * 4, (int)crop.Width * 4)),
                    "Overzoom must preserve the descendant and neighboring filter texels in straight alpha.");
            }
        }
        if (zoom == 15)
            Assert.AreSequenceEqual(new byte[4], decoded.Background.Value.Pixels);
        await VerifyFramesAsync(decoded, zoom, timeout.Token, false);
    }

    [TestMethod]
    public void TerrainSupportIsScopedAndReportedAccurately()
    {
        byte[] json = """
            {"version":8,"sources":{"base":{"type":"vector","url":"tilesetId=microsoft.base"},
              "terrain":{"type":"raster","url":"microsoft.terra.main"},
              "other":{"type":"raster","url":"unsupported"}},"layers":[
              {"type":"background","paint":{"background-color":"#ffffff"}},
              {"type":"raster","source":"terrain","minzoom":4,"maxzoom":15,
                "layout":{"visibility":"none"},"paint":{"raster-fade-duration":0}},
              {"type":"raster","source":"other"}]}
            """u8.ToArray();
        VectorStyle ordinary = VectorStyle.Parse(json);
        VectorStyle relief = VectorStyle.Parse(json, supportsAzureRelief: true);
        Assert.IsNull(ordinary.Relief);
        Assert.AreEqual(ordinary.LayerCount + 1, relief.LayerCount);
        Assert.AreEqual(ordinary.UnsupportedLayerCount, relief.UnsupportedLayerCount,
            "Geometry evaluation support is unchanged; raster compatibility is reported separately by event 75.");
        Assert.AreEqual(0d, relief.Relief!.GetOpacity(6), "Hidden terrain must not draw.");
        var issues = VectorStyleCompatibility.Analyze(json, supportsAzureRelief: true);
        Assert.AreEqual(1, issues.Single(i => i.Construct == "raster").Count,
            "Other raster sources must remain explicitly unsupported.");
        Assert.AreEqual(4d, relief.Relief.MinZoom);
        Assert.AreEqual(15d, relief.Relief.MaxZoom);
        Assert.AreSequenceEqual(["microsoft.base"],
            AzureTileAcquisitionSession.GetTilesetIds(MapStyle.RoadShadedRelief, 15));
    }

    [TestMethod]
    [DataRow(6d, 1d)]
    [DataRow(8d, .5)]
    [DataRow(14.99, .5)]
    [DataRow(15d, 0d)]
    public async Task TerrainHonorsAlphaStyleOpacityAndOrderAcrossCachedFrames(double zoom, double opacity)
    {
        TileId id = new((int)zoom, 0, 0);
        byte[] bytes = new MapboxVectorTileBuilder()
            .AddPolygon("land", [[new(0, 0), new(4096, 0), new(4096, 4096), new(0, 4096)]])
            .AddPolygon("detail", [[new(1024, 1024), new(1536, 1024), new(1536, 1536), new(1024, 1536)]])
            .AddPoint("labels", 3072, 1024)
            .AddLine("road", [new(0, 2048), new(4096, 2048)]).Build();
        VectorStyleAssets assets = CreateStyle();
        assets.GlyphAtlas.AddRangeForTest(new VectorGlyphRange("TestFont", 0,
            new Dictionary<int, VectorGlyph> { ['A'] = TestGlyph.RectangleSdf('A').ToVectorGlyph() }));
        // Straight alpha black shade: 50% coverage must darken, not replace,
        // the red land below it. Explicit 50% style opacity halves that effect.
        byte[] pixels = new byte[256 * 256 * 4];
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 128;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        var features = VectorTileDecoder.Decode(bytes);
        var textures = await assets.PrepareTexturesAsync(features, id.Zoom, timeout.Token);
        var decoded = new DecodedVectorTile(id, features, assets, textures,
            new(id, pixels, 256, 256, 0, 0), 0, 0);
        Assert.AreEqual(opacity, assets.Relief!.GetOpacity(zoom));
        await VerifyFramesAsync(decoded, zoom, timeout.Token, true);
    }

    private static VectorStyleAssets CreateStyle(MapStyle style = MapStyle.RoadShadedRelief) => VectorStyleAssets.CreateForTest(style,
        """
        {"version":8,"sources":{"base":{"type":"vector","url":"tilesetId=microsoft.base"},
          "terrain":{"type":"raster","url":"microsoft.terra.main"}},"layers":[
          {"type":"background","paint":{"background-color":"#eeeeee"}},
          {"type":"fill","source":"base","source-layer":"land","paint":{"fill-color":"#ff0000"}},
          {"type":"raster","source":"terrain","maxzoom":15,"paint":{
            "raster-fade-duration":0,"raster-opacity":["step",["zoom"],1,8,0.5]}},
          {"type":"fill","source":"base","source-layer":"detail","paint":{"fill-color":"#0000ff"}},
          {"type":"line","source":"base","source-layer":"road","paint":{"line-color":"#00ff00","line-width":8}},
          {"type":"symbol","source":"base","source-layer":"labels","layout":{
            "text-field":"A","text-font":["TestFont"],"text-size":24,"text-allow-overlap":true},
            "paint":{"text-color":"#ffffff"}}
        ]}
        """u8.ToArray(), "{}"u8.ToArray(), [0, 0, 0, 0], 1, 1);

    private async Task VerifyFramesAsync(DecodedVectorTile decoded, double zoom,
        CancellationToken cancellationToken, bool isSynthetic)
    {
        using RenderingEventListener listener = new("TileUploadCommitSummary", "VectorTileCommitSummary",
            "RasterCoverageMilestone", "VectorPolygonRenderBatch", "VectorLineRenderBatch",
            "VectorLabelRenderBatch", "TileUploadFailed", "RendererFailure");
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(256, 256);
        TileId id = decoded.Id;
        double lon = MapCamera.WorldXToLongitude((id.X + .5) / Math.Pow(2, id.Zoom));
        double lat = MapCamera.WorldYToLatitude((id.Y + .5) / Math.Pow(2, id.Zoom));
        LayerRenderSnapshot layer = new(LayerRenderKind.HybridTiles, 0, 1, true, 1,
            TimeSpan.Zero, 0, 24, 0, 256, Style: 13);
        renderer.SetLayerRenderPlan([layer]);
        renderer.SetCameraTargetImmediately(lon, lat, zoom, 256, 256);
        MapScene scene = MapCamera.CreateScene(lon, lat, zoom, id.Zoom, 256, 256, 0, 0);
        renderer.ActivateRasterTileSet(1, 1, 1, scene, tile => tile == id,
            RasterSourceKind.Azure, LayerRenderKind.HybridTiles, false);
        var raster = decoded.Background!.Value;
        VectorTileData tileData = new(new(1, id), decoded.Features, decoded.StyleAssets,
            decoded.SpriteTextures, new(new(1, id), raster.Pixels, raster.Width, raster.Height,
                1, RasterSourceKind.Azure) { TextureTransform = raster.TextureTransform }, 1, 13);
        // Ready glyph textures isolate terrain composition from symbol fade timing.
        // Raster/vector data still enter through the real atomic hybrid queue.
        renderer.AddVectorTexturesForBenchmark(decoded.SpriteTextures);
        Assert.IsTrue(await renderer.QueueHybridTileAsync(tileData, cancellationToken));
        DrainUploads(renderer);
        MapRenderFrame frame = renderer.CaptureOffscreenFrameForBenchmark();
        Assert.AreEqual(1, Convert.ToInt32(listener.Events("TileUploadCommitSummary").Single().Payload[0]));
        Assert.AreEqual(1, Convert.ToInt32(listener.Events("VectorTileCommitSummary").Single().Payload[1]));
        if (zoom < 15)
            Assert.IsTrue(listener.Events("RasterCoverageMilestone").Any(e => Equals(e.Payload[3], "OpaqueCoverage")));
        // Same committed vector data, without terrain: reproduces the original
        // vector-only path without changing the style or its canvas/fill colors.
        renderer.SetLayerRenderPlan([layer with { Kind = LayerRenderKind.VectorPoints }]);
        MapRenderFrame before = renderer.CaptureOffscreenFrameForBenchmark();
        int changed = ChangedPixels(before, frame);
        TestContext.WriteLine($"Style=13; zoom={zoom}; before relief pixels=0; after changed pixels={changed}; raster commits=1; vector commits=1");
        if (zoom < 15) Assert.IsGreaterThan(100, changed);
        else Assert.AreEqual(0, changed, "maxzoom must hide relief without hiding road geometry.");
        if (isSynthetic)
        {
            Assert.IsGreaterThan(0, Convert.ToInt32(listener.Events("VectorLabelRenderBatch").First().Payload[2]),
                "Ready labels must remain drawable above relief.");
            int sample = (220 * 256 + 220) * 4;
            double alpha = decoded.StyleAssets.Relief!.GetOpacity(zoom) * 128 / 255;
            Assert.AreEqual(255d * (1 - alpha), frame.Pixels.Span[sample + 2], 2,
                "Terrain must alpha blend above the opaque land fill.");
            Assert.AreEqual((byte)255, frame.Pixels.Span[(128 * 256 + 128) * 4 + 1],
                "Roads remain above relief.");
            if (zoom == (int)zoom)
                Assert.AreEqual((byte)255, frame.Pixels.Span[(80 * 256 + 80) * 4],
                    "Later polygon details must stay above relief.");
            renderer.PrepareAndUploadVectorTileForBenchmark(decoded.Features, decoded.StyleAssets,
                id, zoom, 256, 256);
            renderer.SetLayerRenderPlan([layer]);
            for (int i = 0; i < 3; i++)
                Assert.AreEqual(0, ChangedPixels(frame, renderer.CaptureOffscreenFrameForBenchmark()),
                    "Prepared/reused geometry must retain terrain ordering.");

            // Change generations and render kind in both directions.
            VectorStyleAssets roadAssets = CreateStyle(MapStyle.Road);
            roadAssets.GlyphAtlas.AddRangeForTest(new VectorGlyphRange("TestFont", 0,
                new Dictionary<int, VectorGlyph> { ['A'] = TestGlyph.RectangleSdf('A').ToVectorGlyph() }));
            var roadTextures = await roadAssets.PrepareTexturesAsync(decoded.Features, id.Zoom, cancellationToken);
            Assert.IsNull(roadAssets.Relief);
            renderer.SetLayerRenderPlan([layer with { Kind = LayerRenderKind.VectorPoints, Style = (int)MapStyle.Road }]);
            renderer.ActivateRasterTileSet(1, 2, 2, scene, tile => tile == id,
                RasterSourceKind.Azure, LayerRenderKind.VectorPoints, true);
            renderer.AddVectorTexturesForBenchmark(roadTextures);
            Assert.IsTrue(await renderer.QueueVectorTileAsync(tileData with
            {
                Generation = 2, Background = null, Style = (int)MapStyle.Road,
                StyleAssets = roadAssets, SpriteTextures = roadTextures,
            }, cancellationToken));
            Assert.AreEqual(0, ChangedPixels(before, renderer.CaptureOffscreenFrameForBenchmark()));
            renderer.SetLayerRenderPlan([layer]);
            renderer.ActivateRasterTileSet(1, 3, 3, scene, tile => tile == id,
                RasterSourceKind.Azure, LayerRenderKind.HybridTiles, true);
            Assert.IsTrue(await renderer.QueueHybridTileAsync(tileData with
            { Generation = 3, Background = tileData.Background!.Value with { Generation = 3 } }, cancellationToken));
            DrainUploads(renderer);
            Assert.AreEqual(0, ChangedPixels(frame, renderer.CaptureOffscreenFrameForBenchmark()));
        }
        foreach (string name in new[] { "VectorPolygonRenderBatch", "VectorLineRenderBatch", "VectorLabelRenderBatch" })
            if (listener.Events(name).FirstOrDefault() is { } e)
                TestContext.WriteLine($"{name}: {string.Join(", ", e.Payload)}");
        Assert.IsEmpty(listener.Events("TileUploadFailed"));
        Assert.IsEmpty(listener.Events("RendererFailure"));
    }

    private static void DrainUploads(MapRenderer renderer) =>
        typeof(MapRenderer).GetMethod("ProcessRasterPixelUploads", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(renderer, null);

    private static int ChangedPixels(MapRenderFrame first, MapRenderFrame second)
    {
        int changed = 0;
        for (int i = 0; i < first.Pixels.Length; i += 4)
            if (Math.Abs(first.Pixels.Span[i] - second.Pixels.Span[i]) > 2 ||
                Math.Abs(first.Pixels.Span[i + 1] - second.Pixels.Span[i + 1]) > 2 ||
                Math.Abs(first.Pixels.Span[i + 2] - second.Pixels.Span[i + 2]) > 2)
                changed++;
        return changed;
    }
}
