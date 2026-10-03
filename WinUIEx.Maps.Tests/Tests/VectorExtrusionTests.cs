using System.Numerics;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

[TestClass]
[DoNotParallelize]
public sealed class VectorExtrusionTests
{
    [TestMethod]
    [DataRow("#ff000000")]
    [DataRow("rgba(255,0,0,0)")]
    [DataRow("hsla(0,100%,50%,0)")]
    public void ExtrusionColorIgnoresAlphaEvenWhenZero(string color)
    {
        var source = CreateSource(100, 0, color);
        var resolution = source.StyleAssets.ResolveExtrusions(VectorTileDecoder.Decode(TileBytes()), 16);
        var extrusion = Assert.ContainsSingle(resolution.Extrusions);
        Assert.AreEqual(new Vector4(1, 0, 0, 1), extrusion.Paint.Color);
        Assert.AreEqual(100d, extrusion.Paint.Height);
        Assert.AreEqual(0, resolution.EvaluationFailures);
        Assert.IsEmpty(source.StyleAssets.ResolvePolygons(VectorTileDecoder.Decode(TileBytes()), 16).Polygons);
    }

    [TestMethod]
    [DataRow(-1, 0)]
    [DataRow(10, 20)]
    public void InvalidElevationIsAnEvaluationFailure(int height, int @base)
    {
        var source = CreateSource(height, @base);
        var resolution = source.StyleAssets.ResolveExtrusions(VectorTileDecoder.Decode(TileBytes()), 16);
        Assert.IsEmpty(resolution.Extrusions);
        Assert.AreEqual(1, resolution.EvaluationFailures);
    }

    [TestMethod]
    public void TransparentColorInterpolationPreservesRgbForExtrusions()
    {
        var source = TestVectorTileSource.Create(new(16, 32768, 32768), TileBytes(),
            """
            {"version":8,"layers":[{
              "type":"fill-extrusion","source-layer":"building","paint":{
                "fill-extrusion-height":100,
                "fill-extrusion-color":["interpolate",["linear"],["zoom"],14,"#ff000000",16,"#0000ff00"]
              }
            }]}
            """, "{}", [0, 0, 0, 0], 1, 1);
        var item = Assert.ContainsSingle(source.StyleAssets.ResolveExtrusions(VectorTileDecoder.Decode(TileBytes()), 15).Extrusions);
        Assert.AreEqual(new Vector4(128f / 255, 0, 128f / 255, 1), item.Paint.Color);
    }

    [TestMethod]
    public void ElevatedCoverageIncludesTheCameraFootprintWithoutChangingOrdinaryScenes()
    {
        var center = CreateSource(100, 0).TileCenter;
        MapScene ordinary = MapCamera.CreateScene(center.Longitude, center.Latitude, 16, 16, 512, 512, 0, 55);
        MapScene elevated = MapCamera.CreateScene(center.Longitude, center.Latitude, 16, 16, 512, 512, 0, 55,
            includeElevatedGeometry: true);
        Assert.IsGreaterThan(ordinary.RequiredTiles.Count, elevated.RequiredTiles.Count);
        foreach (var tile in ordinary.RequiredTiles)
            Assert.Contains(tile, elevated.RequiredTiles);
        double cameraY = MapCamera.GetPerspectiveDistance(512) * Math.Sin(55 * Math.PI / 180);
        var cameraTile = new TileId(16, 32768, (int)Math.Floor(32768.5 + cameraY / 256));
        Assert.Contains(cameraTile, elevated.RequiredTiles);
        Assert.DoesNotContain(cameraTile, ordinary.RequiredTiles);
    }

    [TestMethod]
    public void LightingAnchorTracksHeadingOnlyInViewportMode()
    {
        VectorExtrusionLight Light(string anchor) => new(
            VectorStyleExpression.Literal(VectorStyleValue.FromString(anchor)),
            VectorStyleExpression.Literal(VectorStyleValue.FromArray(
                [VectorStyleValue.FromNumber(1), VectorStyleValue.FromNumber(0), VectorStyleValue.FromNumber(90)])),
            VectorStyleExpression.Literal(VectorStyleValue.FromString("white")),
            VectorStyleExpression.Literal(VectorStyleValue.FromNumber(0.5)));
        Assert.IsTrue(Light("map").TryEvaluate(16, 90, out var map, out _));
        Assert.IsTrue(Light("viewport").TryEvaluate(16, 90, out var viewport, out var tint));
        Assert.AreEqual(-1f, map.Y, 0.0001);
        Assert.AreEqual(1f, viewport.X, 0.0001);
        Assert.AreEqual(Vector4.One, tint);
    }

    [TestMethod]
    public void RectangleMeshHasRoofAndFourWallsAndHonorsCancellation()
    {
        var source = CreateSource(100, 20);
        var extrusion = Assert.ContainsSingle(source.StyleAssets.ResolveExtrusions(VectorTileDecoder.Decode(TileBytes()), 16).Extrusions);
        using ExtrusionMeshBuffer mesh = new();
        VectorExtrusionGeometry.Append(source.TileId, extrusion, mesh, CancellationToken.None);
        Assert.AreEqual(30, mesh.Count);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            VectorExtrusionGeometry.Append(source.TileId, extrusion, mesh, cancelled.Token));
    }

    [TestMethod]
    [DataRow(0d)]
    [DataRow(45d)]
    [DataRow(60d)]
    public void GroundProjectionMatchesExistingCamera(double pitch)
    {
        Vector4 projected = VectorExtrusionGeometry.Project(new(75, 120, 0), 23, pitch, 512, 512);
        MapCamera.TransformViewportOffset(75, 120, 23, pitch, 512, out double x, out double y);
        Assert.AreEqual(x, projected.X / projected.W * 256, 0.0001);
        Assert.AreEqual(y, -projected.Y / projected.W * 256, 0.0001);
        Vector4 elevated = VectorExtrusionGeometry.Project(new(75, 120, 50), 23, pitch, 512, 512);
        if (pitch > 0)
            Assert.IsLessThan(y, -elevated.Y / elevated.W * 256);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(4)]
    public async Task BuildingsHaveVisibleHeightAndReleaseTheirOptionalResources(int samples)
    {
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(512, 512, samples);
        TestVectorTileSource source = CreateSource(100, 0);
        Setup(renderer, source, 55);
        await Settle(renderer);
        MapRenderFrame frame = renderer.CaptureOffscreenFrameForBenchmark();
        int red = 0;
        for (int i = 0; i < frame.Pixels.Length; i += 4)
            if (frame.Pixels.Span[i + 2] > 50 && frame.Pixels.Span[i] < 20)
                red++;
        Assert.IsGreaterThan(500, red, "Extruded roof/wall geometry must contribute pixels.");
        Assert.IsGreaterThan(0L, renderer.ExtrusionResourceBytesForTest);
        renderer.SetLayerRenderPlan([]);
        renderer.CaptureOffscreenFrameForBenchmark();
        Assert.AreEqual(0L, renderer.ExtrusionResourceBytesForTest);
        Assert.AreEqual(0L, renderer.ExtrusionMeshBytesForTest);
    }

    [TestMethod]
    public async Task MeterHeightRaisesTheRoofAndBaseDoesNotAddToHeight()
    {
        MapRenderFrame flat = await Render(CreateSource(0, 0));
        MapRenderFrame raised = await Render(CreateSource(100, 0));
        MapRenderFrame elevatedBase = await Render(CreateSource(100, 20));
        Assert.IsLessThan(TopRed(flat) - 20, TopRed(raised));
        Assert.AreEqual(TopRed(raised), TopRed(elevatedBase));
    }

    [TestMethod]
    [DataRow(1d)]
    [DataRow(0.5)]
    public async Task DepthAndLayerOpacityAreIndependentOfFeatureDrawOrder(double opacity)
    {
        byte[] Build(bool reverse)
        {
            MapboxVectorTileBuilder builder = new();
            foreach (int height in reverse ? new[] { 100, 30 } : new[] { 30, 100 })
            {
                int shift = height == 100 ? 0 : -256;
                builder.AddPolygon("building",
                    [[new(1536 + shift, 1536 + shift), new(2560 + shift, 1536 + shift),
                      new(2560 + shift, 2560 + shift), new(1536 + shift, 2560 + shift)]],
                    new Dictionary<string, object> { ["height"] = height, ["color"] = height == 100 ? "#f00" : "#00f" });
            }
            return builder.Build();
        }
        var json = FormattableString.Invariant($$"""
            {"version":8,"light":{"intensity":0},"layers":[{
              "type":"fill-extrusion","source-layer":"building",
              "paint":{"fill-extrusion-height":["get","height"],"fill-extrusion-color":["get","color"],
                "fill-extrusion-opacity":{{opacity}}}
            }]}
            """);
        TestVectorTileSource Source(byte[] bytes) => TestVectorTileSource.Create(
            new(16, 32768, 32768), bytes, json, "{}", [0, 0, 0, 0], 1, 1);
        var forwardBytes = Build(false);
        var reverseBytes = Build(true);
        var forward = await Render(Source(forwardBytes), forwardBytes);
        var reverse = await Render(Source(reverseBytes), reverseBytes);
        Assert.IsTrue(forward.Pixels.Span.SequenceEqual(reverse.Pixels.Span),
            "Nearest surfaces, not feature order, must control the result.");
        int roof = (220 * 512 + 256) * 4;
        Assert.AreEqual(255 * opacity + 240 * (1 - opacity), forward.Pixels.Span[roof + 2], 2);
        Assert.AreEqual(240 * (1 - opacity), forward.Pixels.Span[roof], 2,
            "Opacity applies once to the entire layer, not once for each overlapping face.");
    }

    [TestMethod]
    public void OrdinaryMapsNeverAllocateExtrusionResources()
    {
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(256, 256);
        for (int i = 0; i < 5; i++)
            renderer.RenderOffscreenFrameForBenchmark();
        Assert.AreEqual(0L, renderer.ExtrusionResourceBytesForTest);
        Assert.AreEqual(0L, renderer.ExtrusionMeshBytesForTest);
    }

    [TestMethod]
    public void OrdinaryVectorMapsNeverAllocateExtrusionResources()
    {
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(512, 512);
        var source = TestVectorTileSource.Create(new(16, 32768, 32768), TileBytes(),
            """{"version":8,"layers":[{"type":"fill","source-layer":"building","paint":{"fill-color":"#f00"}}]}""",
            "{}", [0, 0, 0, 0], 1, 1);
        Setup(renderer, source, 55);
        for (int i = 0; i < 5; i++)
            renderer.RenderOffscreenFrameForBenchmark();
        Assert.AreEqual(0L, renderer.ExtrusionResourceBytesForTest);
        Assert.AreEqual(0L, renderer.ExtrusionMeshBytesForTest);
    }

    [TestMethod]
    public async Task PatternOverridesColorAndKeepsPixelScaleAcrossSourceTileSizes()
    {
        byte[] pixels = new byte[8 * 8 * 4];
        for (int i = 0; i < 64; i++)
        {
            pixels[i * 4] = 255;
            pixels[i * 4 + 1] = pixels[i * 4 + 2] = (byte)(i % 8 < 4 ? 0 : 255);
            pixels[i * 4 + 3] = 255;
        }
        var source = TestVectorTileSource.Create(new(16, 32768, 32768), TileBytes(),
            """
            {"version":8,"light":{"intensity":0},"layers":[{
              "type":"fill-extrusion","source-layer":"building","paint":{
                "fill-extrusion-color":"not-a-color","fill-extrusion-pattern":"stripe",
                "fill-extrusion-height":100,"fill-extrusion-vertical-gradient":false
              }
            }]}
            """, """{"stripe":{"x":0,"y":0,"width":8,"height":8,"pixelRatio":1}}""", pixels, 8, 8);
        var textures = await source.StyleAssets.PrepareTexturesAsync(
            VectorTileDecoder.Decode(TileBytes()), 16, CancellationToken.None);
        Assert.HasCount(1, textures);
        async Task<MapRenderFrame> Draw(int tileSize)
        {
            using MapRenderer renderer = new();
            renderer.InitializeOffscreenForBenchmark(512, 512);
            Setup(renderer, source, 55, tileSize: tileSize);
            renderer.AddVectorTexturesForBenchmark(textures);
            await Settle(renderer);
            return renderer.CaptureOffscreenFrameForBenchmark();
        }
        var small = await Draw(256);
        var large = await Draw(512);
        Assert.IsTrue(small.Pixels.Span.SequenceEqual(large.Pixels.Span),
            "Native source tile dimensions must not change the displayed repeat size.");
        int blue = 0;
        for (int i = 0; i < small.Pixels.Length; i += 4)
            if (small.Pixels.Span[i] > 200 && small.Pixels.Span[i + 2] < 50)
                blue++;
        Assert.IsGreaterThan(500, blue, "The sprite must be visible on the extruded surfaces.");
    }

    [TestMethod]
    public void CourtyardsHaveWallsAndTileClosureEdgesDoNot()
    {
        byte[] courtyard = new MapboxVectorTileBuilder().AddPolygon("building",
            [[new(512, 512), new(3584, 512), new(3584, 3584), new(512, 3584)],
             [new(1536, 1536), new(1536, 2560), new(2560, 2560), new(2560, 1536)]]).Build();
        var source = CreateSource(100, 0);
        using ExtrusionMeshBuffer mesh = new();
        var item = Assert.ContainsSingle(source.StyleAssets.ResolveExtrusions(VectorTileDecoder.Decode(courtyard), 16).Extrusions);
        VectorExtrusionGeometry.Append(source.TileId, item, mesh, CancellationToken.None);
        Assert.AreEqual(72, mesh.Count, "A courtyard needs eight roof triangles and eight wall quads.");

        byte[] clipped = new MapboxVectorTileBuilder().AddPolygon("building",
            [[new(0, 512), new(1536, 512), new(1536, 1536), new(0, 1536)]]).Build();
        using ExtrusionMeshBuffer clippedMesh = new();
        item = Assert.ContainsSingle(source.StyleAssets.ResolveExtrusions(VectorTileDecoder.Decode(clipped), 16).Extrusions);
        VectorExtrusionGeometry.Append(source.TileId, item, clippedMesh, CancellationToken.None);
        Assert.AreEqual(24, clippedMesh.Count, "An artificial tile-edge closure must not become a wall.");
    }

    [TestMethod]
    public async Task FractionalMinZoomCrossingReleasesResourcesAndRebuildsOnReturn()
    {
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(512, 512);
        var source = TestVectorTileSource.Create(new(16, 32768, 32768), TileBytes(),
            """
            {"version":8,"layers":[{"type":"fill-extrusion","source-layer":"building",
            "minzoom":15.5,"paint":{"fill-extrusion-height":100,"fill-extrusion-color":"#f00"}}]}
            """, "{}", [0, 0, 0, 0], 1, 1);
        Setup(renderer, source, 55);
        await Settle(renderer);
        renderer.SetCameraTargetImmediately(source.TileCenter.Longitude, source.TileCenter.Latitude, 15.49, 512, 512, 0, 55);
        renderer.RenderOffscreenFrameForBenchmark();
        Assert.AreEqual(0L, renderer.ExtrusionResourceBytesForTest);
        Assert.AreEqual(0L, renderer.ExtrusionMeshBytesForTest);
        renderer.SetCameraTargetImmediately(source.TileCenter.Longitude, source.TileCenter.Latitude, 15.51, 512, 512, 0, 55);
        await Settle(renderer);
        Assert.IsGreaterThan(0L, renderer.ExtrusionResourceBytesForTest);
    }

    private static int TopRed(MapRenderFrame frame)
    {
        for (int y = 0; y < 512; y++)
        for (int x = 0; x < 512; x++)
        {
            int index = (y * 512 + x) * 4;
            if (frame.Pixels.Span[index + 2] > 50 && frame.Pixels.Span[index] < 20)
                return y;
        }
        Assert.Fail("No building pixels were rendered.");
        return -1;
    }

    private static async Task<MapRenderFrame> Render(TestVectorTileSource source, byte[]? bytes = null)
    {
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(512, 512);
        Setup(renderer, source, 55, bytes);
        await Settle(renderer);
        return renderer.CaptureOffscreenFrameForBenchmark();
    }

    private static byte[] TileBytes() => new MapboxVectorTileBuilder()
        .AddPolygon("building", [[new(1536, 1536), new(2560, 1536), new(2560, 2560), new(1536, 2560)]])
        .Build();

    private static TestVectorTileSource CreateSource(int height, int @base, string color = "#f00") =>
        TestVectorTileSource.Create(new(16, 32768, 32768), TileBytes(),
            $$$"""
            {"version":8,"layers":[{
              "type":"fill-extrusion","source-layer":"building","minzoom":14,
              "paint":{"fill-extrusion-height":{{{height}}},"fill-extrusion-base":{{{@base}}},
                "fill-extrusion-color":"{{{color}}}"}
            }]}
            """, "{}", [0, 0, 0, 0], 1, 1);

    private static void Setup(MapRenderer renderer, TestVectorTileSource source, double pitch, byte[]? bytes = null, int tileSize = 256)
    {
        renderer.SetLayerRenderPlan([new(LayerRenderKind.VectorPoints, 0, 1, true, 1, TimeSpan.Zero, 0, 24, 0, tileSize)]);
        renderer.SetCameraTargetImmediately(source.TileCenter.Longitude, source.TileCenter.Latitude, 16, 512, 512, 0, pitch);
        renderer.ActivateRasterTileSet(1, 1, 1,
            MapCamera.CreateScene(source.TileCenter.Longitude, source.TileCenter.Latitude, 16, 16, 512, 512, 0, pitch),
            id => id == source.TileId, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        renderer.AddVectorTileForBenchmark(new(1, source.TileId), VectorTileDecoder.Decode(bytes ?? TileBytes()), source.StyleAssets);
    }

    private static async Task Settle(MapRenderer renderer)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (renderer.ExtrusionMeshBytesForTest == 0)
        {
            renderer.RenderOffscreenFrameForBenchmark();
            await Task.Delay(10, timeout.Token);
        }
    }
}
