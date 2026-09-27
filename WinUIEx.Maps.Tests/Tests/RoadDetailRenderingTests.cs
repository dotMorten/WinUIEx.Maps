using Microsoft.Extensions.Configuration;
using System.Reflection;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RoadDetailRenderingTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string Style = """
        {"version":8,"sources":{"base":{"type":"vector","url":"tilesetId=microsoft.base"},
          "roadDetails":{"type":"raster","url":"tilesetId=microsoft.core.raster.roaddetail"}},"layers":[
          {"type":"background","paint":{"background-color":"#ffffff"}},
          {"type":"fill","source":"base","source-layer":"land","paint":{"fill-color":"#ff0000"}},
          {"type":"raster","source":"roadDetails","minzoom":4,"maxzoom":13,
            "paint":{"raster-fade-duration":0}},
          {"type":"fill","source":"base","source-layer":"detail","paint":{"fill-color":"#0000ff"}},
          {"type":"line","source":"base","source-layer":"road","paint":{"line-color":"#00ff00","line-width":8}}]}
        """;

    [TestMethod]
    public void AzureGenerationOptionsFollowStyleWithoutImportingRequestRouting()
    {
        byte[] json = """
            {"version":8,"sources":{"base":{"type":"vector",
              "url":"https://example.invalid/other?og=2864&cstl=vb&sv=9.46&jp=0&st=g%7Cpv%3A0&subscription-key=not-a-real-token&language={{language}}&tilesetId=microsoft.base"}},
              "layers":[]}
            """u8.ToArray();
        Assert.AreEqual("&og=2864&cstl=vb&sv=9.46&jp=0&st=g%7Cpv%3A0",
            VectorStyle.Parse(json).AzureBaseTileParameters);
        Assert.AreEqual(string.Empty, VectorStyle.ParseCustom(json).AzureBaseTileParameters);
    }

    [TestMethod]
    [DataRow(4.99, 5)]
    [DataRow(5d, 5)]
    [DataRow(10d, 10)]
    [DataRow(10.49, 10)]
    [DataRow(10.5, 11)]
    [DataRow(13.99, 13)]
    [DataRow(14d, 13)]
    public void SourceUsesWebZoomAndStablePrivateIdentity(double zoom, int sourceZoom)
    {
        AzureRoadDetailAcquisitionSession source = new("test-token", "en-US");
        MapScene scene = MapCamera.CreateScene(0, 0, zoom, (int)zoom, 256, 256, 0, 0);
        Assert.AreEqual(sourceZoom, source.GetSourceZoom(scene));
        Assert.AreEqual(256, source.TileSize);
        Assert.AreEqual(source.SourceKey, new AzureRoadDetailAcquisitionSession("test-token", "EN-us").SourceKey);
        Assert.AreNotEqual(source.SourceKey, new AzureRoadDetailAcquisitionSession("other-token", "en-US").SourceKey);
        Assert.IsFalse(new AzureRoadDetailAcquisitionSession("", null).CanAcquire);
        Assert.IsFalse(source.IncludesTile(new(3, 0, 0)));
        Assert.IsFalse(source.IncludesTile(new(4, 0, 0)));
        Assert.IsTrue(source.IncludesTile(new(12, 0, 0)));
        Assert.IsTrue(source.IncludesTile(new(13, 0, 0)));
        Assert.IsFalse(source.IncludesTile(new(14, 0, 0)));
        Assert.IsFalse(source.IncludesTile(new(15, 0, 0)));
        string path = source.BuildTileRequestPath(new(9, 82, 179));
        StringAssert.Contains(path, "cstl=vb");
        StringAssert.Contains(path, "language=en-US");
        StringAssert.Contains(path, "tileSize=256");
        Assert.IsFalse(path.Contains("test-token", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(false, 1)]
    [DataRow(true, 3)]
    public void RoundedRasterZoomChangesPublishEvenWithUnchangedBaseCoverage(bool rounded, int expectedPublications)
    {
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(64, 64);
        renderer.SetLayerRenderPlan([
            new(LayerRenderKind.RasterTiles, -1, 2, true, 1, TimeSpan.Zero, 5, 14, 5, 256,
                RasterOverlayParentId: 1, RoundRasterSourceZoom: rounded)]);
        List<MapScene> published = [];
        using RenderingEventListener events = new("SceneChanged");
        MapScene? displayed = null;
        renderer.SceneChanged += scene => published.Add(scene);
        renderer.DisplayedCameraChanged += scene => displayed = scene;
        double lon = MapCamera.WorldXToLongitude(164.5 / 1024);
        double lat = MapCamera.WorldYToLatitude(358.5 / 1024);
        TileId[]? coverage = null;
        foreach (double zoom in new[] { 10.49, 10.51, 10.49 })
        {
            renderer.SetCameraTargetImmediately(lon, lat, zoom, 64, 64);
            renderer.RenderOffscreenFrameForBenchmark();
            Assert.IsNotNull(displayed);
            coverage ??= displayed.RequiredTiles.ToArray();
            Assert.IsTrue(coverage.ToHashSet().SetEquals(displayed.RequiredTiles));
        }
        Assert.HasCount(expectedPublications, published,
            "Rounded raster requests must update at half zooms, even when ordinary tile coverage stays unchanged.");
        Assert.HasCount(expectedPublications, events.Events("SceneChanged"));
    }

    [TestMethod]
    public void RasterSourceRemainsHiddenAndStyleSupportIsScoped()
    {
        var source = new AzureRoadDetailAcquisitionSession("test-token", null);
        TileLayerSnapshot overlay = new(2, 1, source, 5, 14, true, 1, TimeSpan.Zero);
        TileLayerSnapshot parent = new(1, 1, new AzureTileAcquisitionSession(MapStyle.Road, "test-token"),
            0, 24, true, 1, TimeSpan.Zero);
        LayerRenderSnapshot publicLayer = new(LayerRenderKind.MapElements, 0, 3, true, 1, TimeSpan.Zero, 0, 24, 0, 256);
        var publication = LayerSnapshotPublication.PrependHiddenAzure(parent, [publicLayer], [], overlay);
        Assert.AreSequenceEqual(new long[] { 1, 2 }, publication.RasterLayers.Select(s => s.RuntimeId));
        Assert.AreEqual(1L, publication.RenderPlan[1].RasterOverlayParentId);
        Assert.IsTrue(publication.RenderPlan[1].RasterPremultiplied);
        Assert.AreEqual(publicLayer, publication.RenderPlan[2]);
        Assert.IsEmpty(LayerSnapshotPublication.PrependHiddenAzure(null, [publicLayer], [], overlay).RasterLayers);

        byte[] json = System.Text.Encoding.UTF8.GetBytes(Style);
        Assert.IsNull(VectorStyle.ParseCustom(json).RoadDetails);
        var parsed = VectorStyle.Parse(json, supportsAzureRoadDetails: true);
        Assert.AreEqual(2, parsed.RoadDetails!.Order);
        Assert.AreEqual(0d, parsed.RoadDetails.GetOpacity(3.99));
        Assert.AreEqual(1d, parsed.RoadDetails.GetOpacity(4));
        Assert.AreEqual(0d, parsed.RoadDetails.GetOpacity(13));
        using RenderingEventListener listener = new("VectorStyleCompatibilityIssue");
        VectorStyleCompatibility.Report((int)MapStyle.Road, json);
        Assert.IsEmpty(listener.Events("VectorStyleCompatibilityIssue"));
        Assert.IsTrue(VectorStyleCompatibility.Analyze(json).Any(i => i.Construct == "raster"));
        Assert.AreSequenceEqual(new[] { "microsoft.base", AzureRoadDetailAcquisitionSession.Tileset },
            AzureTileAcquisitionSession.GetTilesetIds(MapStyle.Road, 12));
        Assert.AreSequenceEqual(new[] { "microsoft.base" },
            AzureTileAcquisitionSession.GetTilesetIds(MapStyle.Road, 13));
    }

    [TestMethod]
    [DataRow(10d, 10)]
    [DataRow(13.9, 13)]
    public async Task RasterIsBetweenFillsAndBelowRoadsAcrossCachedFrames(double displayZoom, int rasterZoom)
    {
        TileId id = new(9, 82, 179);
        byte[] bytes = new MapboxVectorTileBuilder()
            .AddPolygon("land", [[new(0, 0), new(4096, 0), new(4096, 4096), new(0, 4096)]])
            .AddPolygon("detail", [[new(1024, 1024), new(2048, 1024), new(2048, 2048), new(1024, 2048)]])
            .AddLine("road", [new(0, 2048), new(4096, 2048)]).Build();
        VectorStyleAssets assets = VectorStyleAssets.CreateForTest(MapStyle.Road,
            System.Text.Encoding.UTF8.GetBytes(Style), "{}"u8.ToArray(), [0, 0, 0, 0], 1, 1,
            displayZoomOffset: -1);
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(256, 256);
        double lon = MapCamera.WorldXToLongitude((id.X + .5) / 512);
        double lat = MapCamera.WorldYToLatitude((id.Y + .5) / 512);
        renderer.SetCameraTargetImmediately(lon, lat, displayZoom, 256, 256);
        MapScene scene = MapCamera.CreateScene(lon, lat, displayZoom, 9, 256, 256, 0, 0);
        MapScene rasterScene = MapCamera.CreateScene(lon, lat, displayZoom, rasterZoom, 256, 256, 0, 0);
        LayerRenderSnapshot parent = new(LayerRenderKind.VectorPoints, -1, 1, true, 1, TimeSpan.Zero, 0, 24, 0, 512,
            Style: (int)MapStyle.Road);
        LayerRenderSnapshot overlay = new(LayerRenderKind.RasterTiles, -1, 2, true, 1, TimeSpan.Zero, 5, 14, 5, 256,
            Style: (int)MapStyle.Road, RasterOverlayParentId: 1, RasterPremultiplied: true);
        renderer.SetLayerRenderPlan([parent, overlay]);
        renderer.ActivateRasterTileSet(1, 1, 1, scene, _ => true, RasterSourceKind.Azure, LayerRenderKind.VectorPoints, false);
        renderer.ActivateRasterTileSet(2, 1, 1, rasterScene, _ => true, RasterSourceKind.Azure, LayerRenderKind.RasterTiles, false);
        renderer.AddVectorTileForBenchmark(new(1, id), VectorTileDecoder.Decode(bytes), assets);
        foreach (TileId rasterId in rasterScene.RequiredTiles)
            Assert.IsTrue(await renderer.QueueRasterUploadAsync(
                new(new(2, rasterId), [0, 0, 0, 128], 1, 1, 1, RasterSourceKind.Azure), TestContext.CancellationToken));
        typeof(MapRenderer).GetMethod("ProcessRasterPixelUploads", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(renderer, null);
        for (int i = 0; i < 12; i++)
        {
            MapRenderFrame frame = renderer.CaptureOffscreenFrameForBenchmark();
            Assert.AreEqual(127d, frame.Pixels.Span[(220 * 256 + 220) * 4 + 2], 1,
                "Raster must blend once over land.");
            Assert.AreEqual((byte)255, frame.Pixels.Span[(80 * 256 + 80) * 4],
                "Later polygon details must not be darkened by the raster.");
            Assert.AreEqual((byte)255, frame.Pixels.Span[(128 * 256 + 128) * 4 + 1],
                "Vector roads must be above the raster.");
            await Task.Delay(10, TestContext.CancellationToken);
        }
        renderer.SetLayerRenderPlan([parent]);
        Assert.AreEqual((byte)255, renderer.CaptureOffscreenFrameForBenchmark().Pixels.Span[(220 * 256 + 220) * 4 + 2],
            "Removing the auxiliary snapshot must immediately remove its pixels.");
    }

    [TestMethod]
    [DataRow(1d)]
    [DataRow(.5)]
    public async Task FilteredRoadEdgesDoNotAcquireDarkHalos(double opacity)
    {
        TileId id = new(10, 164, 358);
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(256, 256);
        double lon = MapCamera.WorldXToLongitude((id.X + .5 + .5 / 256) / 1024);
        double lat = MapCamera.WorldYToLatitude((id.Y + .5) / 1024);
        renderer.SetCameraTargetImmediately(lon, lat, 10, 256, 256);
        MapScene scene = MapCamera.CreateScene(lon, lat, 10, 10, 256, 256, 0, 0);
        VectorStyleAssets assets = VectorStyleAssets.CreateForTest(MapStyle.Road,
            """
            {"version":8,"sources":{"base":{"type":"vector","url":"tilesetId=microsoft.base"},"roadDetails":{"type":"raster",
              "url":"tilesetId=microsoft.core.raster.roaddetail"}},"layers":[
              {"type":"background","paint":{"background-color":"#ffffff"}},
              {"type":"raster","source":"roadDetails","paint":{"raster-fade-duration":0}}]}
            """u8.ToArray(), "{}"u8.ToArray(), [0, 0, 0, 0], 1, 1);
        renderer.SetLayerRenderPlan([
            new(LayerRenderKind.VectorPoints, -1, 1, true, 1, TimeSpan.Zero, 0, 24, 0, 512, Style: (int)MapStyle.Road),
            new(LayerRenderKind.RasterTiles, -1, 2, true, opacity, TimeSpan.Zero, 5, 14, 5, 256,
                Style: (int)MapStyle.Road, RasterOverlayParentId: 1, RasterPremultiplied: true)]);
        renderer.ActivateRasterTileSet(1, 1, 1, scene, _ => true, RasterSourceKind.Azure, LayerRenderKind.VectorPoints, false);
        renderer.ActivateRasterTileSet(2, 1, 1, scene, _ => true, RasterSourceKind.Azure, LayerRenderKind.RasterTiles, false);
        renderer.AddVectorTileForBenchmark(new(1, id), VectorTileDecoder.Decode(new MapboxVectorTileBuilder().Build()), assets);
        byte[] pixels = new byte[256 * 256 * 4];
        for (int y = 0; y < 256; y++)
            for (int x = 128; x <= 129; x++)
            {
                int offset = (y * 256 + x) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 200;
                pixels[offset + 3] = 255;
            }
        Assert.IsTrue(await renderer.QueueRasterUploadAsync(
            new(new(2, id), pixels, 256, 256, 1, RasterSourceKind.Azure), TestContext.CancellationToken));
        typeof(MapRenderer).GetMethod("ProcessRasterPixelUploads", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(renderer, null);
        MapRenderFrame frame = renderer.CaptureOffscreenFrameForBenchmark();
        for (int x = 124; x <= 132; x++)
            Assert.IsTrue(frame.Pixels.Span[(128 * 256 + x) * 4] >= 200,
                $"Filtering a gray road over white must not create darker pixels; x={x}.");
        Assert.AreEqual(200 * opacity + 255 * (1 - opacity),
            frame.Pixels.Span[(128 * 256 + 128) * 4], 1,
            "Premultiplied layer opacity must scale color as well as alpha.");
    }

    [TestMethod]
    [DataRow(9, 82, 179)]
    [DataRow(10, 164, 358)]
    public async Task LiveAzureRoadDetailTileDecodesWithTransparency(int zoom, int x, int y)
    {
        string? token = new ConfigurationBuilder().AddUserSecrets<RoadDetailRenderingTests>(true)
            .Build()["AzureMaps:MapServiceToken"];
        if (string.IsNullOrWhiteSpace(token))
            Assert.Inconclusive("Azure Maps test user secret is required.");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        try
        {
            var source = new AzureRoadDetailAcquisitionSession(token, "en-US");
            using PooledByteBuffer metadata = await AzureTileAcquisitionSession.GetTileBytesAsync(
                $"map/tileset?api-version=2.1&tilesetId={AzureRoadDetailAcquisitionSession.Tileset}&cstl=vb",
                token, "application/json", 1024 * 1024, timeout.Token);
            using var document = System.Text.Json.JsonDocument.Parse(metadata.Memory);
            string template = document.RootElement.GetProperty("tiles")[0].GetString()!;
            Assert.AreEqual(source.TileSize, int.Parse(document.RootElement.GetProperty("tileSize").ToString(),
                    System.Globalization.CultureInfo.InvariantCulture),
                "Use the loaded tileset metadata, not the SDK's initial source defaults.");
            Assert.AreEqual(source.MaxSourceZoom, int.Parse(document.RootElement.GetProperty("maxzoom").ToString(),
                    System.Globalization.CultureInfo.InvariantCulture),
                "Eligible raster zooms must stay inside service coverage.");
            DecodedRasterTile tile = await source.GetTileAsync(new(zoom, x, y), timeout.Token);
            Assert.AreEqual(256u, tile.Width);
            Assert.AreEqual(256u, tile.Height);
            Assert.IsTrue(Enumerable.Range(0, tile.Pixels.Length / 4).Any(i => tile.Pixels[i * 4 + 3] == 0));
            Assert.IsTrue(Enumerable.Range(0, tile.Pixels.Length / 4).Any(i => tile.Pixels[i * 4 + 3] > 0));
            Uri referenceUri = new(template.Replace("{z}", zoom.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{x}", x.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{y}", y.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
            Assert.IsTrue(referenceUri.Host == "atlas.microsoft.com" && referenceUri.AbsolutePath == "/map/tile",
                "Reference acquisition must stay on the Azure tile endpoint.");
            using PooledByteBuffer referenceBytes = await AzureTileAcquisitionSession.GetTileBytesAsync(
                referenceUri.PathAndQuery.TrimStart('/'), token, "image/png", 2 * 1024 * 1024, timeout.Token);
            var reference = await AzureTileAcquisitionSession.DecodeTilePixelsAsync(
                referenceBytes, 256, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                new Windows.Graphics.Imaging.BitmapTransform(), 0, timeout.Token);
            Assert.IsTrue(reference.Pixels.AsSpan().SequenceEqual(tile.Pixels),
                "Native acquisition must match pixels requested through Azure's actual tile template.");
            _ = await new AzureTileAcquisitionSession(MapStyle.Road, token, "en-US")
                .GetAttributionAsync(9, timeout.Token);
        }
        catch (Exception e) when (e is not AssertFailedException)
        {
            Assert.Fail($"Road detail acquisition failed: {e.GetType().Name} (0x{e.HResult:X8}).");
        }
    }
}
