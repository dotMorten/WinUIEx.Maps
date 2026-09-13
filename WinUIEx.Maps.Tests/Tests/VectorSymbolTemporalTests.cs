using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

[TestClass]
[DoNotParallelize]
public sealed class VectorSymbolTemporalTests
{
    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, false)]
    [DataRow(false, true, true)]
    [DataRow(true, false, false)]
    [DataRow(true, false, true)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    public async Task CollisionOverscanKeepsCompetingLabelsStableDuringRepeatedPan(
        bool line, bool vertical, bool farEdge)
    {
        using RenderingEventListener listener = new(
            "VectorLabelRenderBatch", "VectorLabelCollisionSummary",
            "VectorLineSymbolPlacementSummary");
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(256, 256);
        TileId id = new(4, 8, 8);
        MapboxVectorTileBuilder builder = new();
        int delta = farEdge ? -384 : 384;
        int edgeAnchor = farEdge ? 0 : 4096;
        foreach (string name in new[] { "competitor", "winner" })
        {
            int offset = name == "competitor" ? delta : 0;
            // Buffered tile geometry: when the winner exits, its entire source
            // tile exits too, while the competing label still reaches the viewport.
            int x = vertical ? 2048 : edgeAnchor + offset;
            int y = vertical ? edgeAnchor + offset : 2048;
            if (line)
                builder.AddLine(name, [new(x - 512, y), new(x + 512, y)]);
            else
                builder.AddPoint(name, x, y);
        }
        byte[] bytes = builder.Build();
        string placement = line ? "line" : "point";
        string Layer(string name) => $$$"""
            {"type":"symbol","source-layer":"{{{name}}}",
             "layout":{"symbol-placement":"{{{placement}}}","symbol-spacing":256,
               "text-field":"A","text-font":["TestFont"],"text-size":24,"text-padding":32},
             "paint":{"text-color":"{{{(name == "winner" ? "#ff0000" : "#000000")}}}"}}
            """;
        TestVectorTileSource source = TestVectorTileSource.Create(id, bytes,
            """{"version":8,"layers":[""" + Layer("competitor") + "," + Layer("winner") + "]}",
            "{}", [0, 0, 0, 0], 1, 1);
        source.AddGlyphs("TestFont", TestGlyph.RectangleSdf('A'));
        var features = VectorTileDecoder.Decode(bytes);
        var textures = await source.StyleAssets.PrepareTexturesAsync(features, 4, CancellationToken.None);
        var center = source.TileCenter;
        renderer.SetLayerRenderPlan(
        [
            new LayerRenderSnapshot(LayerRenderKind.VectorPoints, 0, 1, true, 1,
                TimeSpan.Zero, 0, 24, 0, 256, Style: (int)MapStyle.Road),
        ]);
        renderer.SetCameraTargetImmediately(center.Longitude, center.Latitude, 4, 256, 256);
        renderer.ActivateRasterTileSet(1, 1, 1,
            MapCamera.CreateScene(center.Longitude, center.Latitude, 4, 4, 256, 256),
            tile => tile == id, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        renderer.AddVectorTexturesForBenchmark(textures);
        renderer.AddVectorTileForBenchmark(new RasterTileKey(1, id), features, source.StyleAssets);
        // Fixed ready tiles: isolate viewport culling from arrivals and texture fades.
        for (int frame = 0; frame < 80; frame++)
        {
            double position = 8 - Math.Min(frame % 40, 39 - frame % 40);
            double along = farEdge ? 256 - position : position;
            double dx = vertical ? 0 : along - edgeAnchor / 16d;
            double dy = vertical ? along - edgeAnchor / 16d : 0;
            var camera = MapCamera.PanByPixels(center.Longitude, center.Latitude, 4, dx, dy);
            renderer.SetCameraTargetImmediately(camera.Longitude, camera.Latitude, 4, 256, 256);
            MapRenderFrame image = renderer.CaptureOffscreenFrameForBenchmark();
            bool hasCompetitorInk = false;
            for (int pixel = 0; pixel < image.Pixels.Length; pixel += 4)
                hasCompetitorInk |= image.Pixels.Span[pixel] < 32 &&
                    image.Pixels.Span[pixel + 1] < 32 && image.Pixels.Span[pixel + 2] < 32;
            Assert.IsFalse(hasCompetitorInk, $"Frame {frame}: the black competitor must not replace the red winner.");
            var collision = listener.Events("VectorLabelCollisionSummary").Last();
            Assert.AreEqual(2, Convert.ToInt32(collision.Payload[1]), $"Frame {frame}: candidates");
            Assert.AreEqual(1, Convert.ToInt32(collision.Payload[2]), $"Frame {frame}: accepted");
            Assert.AreEqual(1, Convert.ToInt32(collision.Payload[3]), $"Frame {frame}: suppressed");
            Assert.AreEqual(1, Convert.ToInt32(listener.Events("VectorLabelRenderBatch").Last().Payload[2]));
            if (line)
                Assert.AreEqual(2, Convert.ToInt32(listener.Events("VectorLineSymbolPlacementSummary").Last().Payload[2]));
        }
    }

    [TestMethod]
    [DataRow(0d, 0d, 256d)]
    [DataRow(35d, 45d, 256d)]
    [DataRow(90d, 60d, 32d)]
    public void CollisionTileCoverageIsBoundedAndPreservesProjection(double heading, double pitch, double size)
    {
        MapScene visible = MapCamera.CreateScene(0, 0, 4, 4, size, size, heading, pitch);
        MapScene placement = MapCamera.CreateLabelCollisionScene(visible);
        Assert.AreEqual(visible.ViewportWidth, placement.ViewportWidth);
        Assert.AreEqual(visible.ViewportHeight, placement.ViewportHeight);
        Assert.IsGreaterThan(visible.RequiredTiles.Count, placement.RequiredTiles.Count);
        Assert.IsLessThan(150, placement.RequiredTiles.Count, "Near-horizon overscan must remain bounded.");
        foreach (VisibleTile tile in visible.VisibleTiles)
            Assert.Contains(tile, placement.VisibleTiles, "Tile coordinates must retain the actual viewport origin.");
        foreach (VisibleTile tile in placement.VisibleTiles)
        {
            var cached = MapRenderer.GetVisibleCachedTileInstances(tile.Id, 0, 0, 4,
                size, size, heading, pitch, MapCamera.LabelCollisionMargin);
            Assert.Contains(tile, cached, "Cached fallback and active candidate coverage must agree.");
        }
        var requests = RasterTileManager.GetActiveRequestTiles(placement, _ => true);
        CollectionAssert.AreEquivalent(placement.RequiredTiles.ToArray(), requests.ToArray());
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void PointLabelsKeepOffscreenGlyphsForReadiness(bool vertical, bool farEdge)
    {
        VectorTileSymbol[] symbols = new[] { -18d, 0d, 18d }
            .Select((offset, index) => new VectorTileSymbol(
                0, 0.5, 0.5, index + 1, 12, 12,
                vertical ? 0 : offset, vertical ? offset : 0,
                VectorSymbolKind.Text, LabelId: 1, SymbolGroupId: 1)).ToArray();
        for (int frame = 0; frame <= 600; frame++)
        {
            double position = 10 - Math.Min(frame, 600 - frame);
            double along = farEdge ? 100 - position : position;
            VisibleTile tile = new(new TileId(4, 8, 8), 8,
                (vertical ? 50 : along) - 128,
                (vertical ? along : 50) - 128, 256);
            var placements = MapRenderer.ProjectVectorSymbols(symbols, tile, 100, 100, 0, 0);
            Assert.HasCount(position > -24 - MapCamera.LabelCollisionMargin ? 3 : 0, placements, $"Frame {frame}");
            long nextGroup = 0;
            MapRenderer.AssignSymbolCollisionGroups(placements, ref nextGroup);
            var incomplete = MapRenderer.FindIncompleteLabelGroups(
                placements, texture => texture != (farEdge ? 3 : 1), out int pending);
            Assert.AreEqual(position > -24 - MapCamera.LabelCollisionMargin ? 3 : 0, pending,
                "An unavailable offscreen glyph must still withhold the complete label.");
            Assert.HasCount(position > -24 - MapCamera.LabelCollisionMargin ? 1 : 0, incomplete);
        }
    }

    [TestMethod]
    public async Task RenderedPointLabelKeepsWholeGroupAtViewportEdge()
    {
        using RenderingEventListener listener = new(
            "VectorLabelRenderBatch", "VectorLabelCollisionSummary",
            "VectorLabelTextureReadinessSummary", "MapFrameStageTiming");
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(256, 256);
        TileId id = new(4, 8, 8);
        byte[] bytes = new MapboxVectorTileBuilder()
            .AddPoint("labels", 2048, 2048).Build();
        TestVectorTileSource source = TestVectorTileSource.Create(id, bytes,
            """
            {"version":8,"layers":[{"type":"symbol","source-layer":"labels",
              "layout":{"text-field":"AAAAA","text-font":["TestFont"],"text-size":24},
              "paint":{"text-color":"#000000"}}]}
            """, "{}", [0, 0, 0, 0], 1, 1);
        source.AddGlyphs("TestFont", TestGlyph.RectangleSdf('A'));
        var features = VectorTileDecoder.Decode(bytes);
        var textures = await source.StyleAssets.PrepareTexturesAsync(features, 4, CancellationToken.None);
        var center = source.TileCenter;
        renderer.SetLayerRenderPlan(
        [
            new LayerRenderSnapshot(LayerRenderKind.VectorPoints, 0, 1, true, 1,
                TimeSpan.Zero, 0, 24, 0, 256, Style: (int)MapStyle.Road),
        ]);
        renderer.SetCameraTargetImmediately(center.Longitude, center.Latitude, 4, 256, 256);
        renderer.ActivateRasterTileSet(1, 1, 1,
            MapCamera.CreateScene(center.Longitude, center.Latitude, 4, 4, 256, 256, 0, 0),
            tile => tile == id, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        renderer.AddVectorTexturesForBenchmark(textures);
        renderer.AddVectorTileForBenchmark(new RasterTileKey(1, id), features, source.StyleAssets);
        for (int frame = 0; frame < 40; frame++)
        {
            double anchorX = 10 - Math.Min(frame, 39 - frame);
            double longitude = center.Longitude + (128 - anchorX) * 360 / 4096;
            renderer.SetCameraTargetImmediately(longitude, center.Latitude, 4, 256, 256);
            MapRenderFrame image = renderer.CaptureOffscreenFrameForBenchmark();
            bool hasInk = false;
            for (int y = 100; y < 156; y++)
                for (int x = 0; x < 60; x++)
                    hasInk |= image.Pixels.Span[(y * 256 + x) * 4] < 32;
            Assert.IsTrue(hasInk, $"Frame {frame}: the visible remainder must be drawn.");
            Assert.AreEqual(5, Convert.ToInt32(listener.Events("VectorLabelRenderBatch").Last().Payload[2]),
                $"Frame {frame}: collision/readiness/draw must receive all five glyphs.");
            var collision = listener.Events("VectorLabelCollisionSummary").Last();
            Assert.AreEqual(1, Convert.ToInt32(collision.Payload[1]));
            Assert.AreEqual(1, Convert.ToInt32(collision.Payload[2]));
            Assert.AreEqual(0, Convert.ToInt32(collision.Payload[3]));
        }
        Assert.HasCount(40, listener.Events("MapFrameStageTiming"));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void PartiallyVisibleLineLabelsSurviveViewportEdges(bool vertical, bool farEdge)
    {
        VectorTilePoint[] path = vertical
            ? [new(0.5, 0), new(0.5, 1)]
            : [new(0, 0.5), new(1, 0.5)];
        VectorTileSymbol[] symbols = new[] { -18d, 0d, 18d }
            .Select(offset => new VectorTileSymbol(
                0, 0, 0, -1, 12, 12, offset, 0, VectorSymbolKind.Text,
                LabelId: 1, LinePoints: path, LineSpacing: 256))
            .ToArray();

        // Traverse out of and back into each edge; one offscreen glyph must not
        // discard the other glyphs or change the label's complete group.
        for (int frame = 0; frame <= 600; frame++)
        {
            double position = 10 - Math.Min(frame, 600 - frame);
            double along = farEdge ? 100 - position : position;
            VisibleTile tile = new(new TileId(4, 8, 8), 8,
                (vertical ? 50 : along) - 128,
                (vertical ? along : 50) - 128, 256);
            VectorSymbolPlacement[] placements = MapRenderer.ProjectVectorSymbols(
                symbols, tile, 100, 100, 0, 0);
            Assert.HasCount(position > -24 - MapCamera.LabelCollisionMargin ? 3 : 0, placements,
                $"Frame {frame}: retain the full label within the finite placement margin.");
        }
    }

    [TestMethod]
    public void LineAnchorsUsePerspectiveCorrectMapPositions()
    {
        VectorTilePoint[] path = [new(0.1, 0.1), new(0.9, 0.9)];
        VectorTileSymbol[] symbols =
        [
            new(0, 0, 0, -1, 16, 16, 0, 0, VectorSymbolKind.Text,
                LabelId: 1, LinePoints: path, LineSpacing: 100, ViewportAligned: true),
        ];
        for (int frame = 0; frame < 60; frame++)
        {
            double size = 256 * Math.Pow(2, frame / 60d);
            VisibleTile tile = new(new TileId(4, 8, 8), 8,
                512 - size / 2, 512 - size / 2, size);
            VectorSymbolPlacement[] placements = MapRenderer.ProjectVectorSymbols(
                symbols, tile, 1024, 1024, 30, 45);
            Assert.IsNotEmpty(placements);
            foreach (VectorSymbolPlacement placement in placements)
            {
                double fraction = (50 + placement.PlacementIndex * 100) /
                    (Math.Sqrt(2) * 0.8 * 256);
                VectorTilePoint anchor = new(0.1 + fraction * 0.8, 0.1 + fraction * 0.8);
                MapScreenPoint expected = MapRenderer.ProjectVectorLine(
                    [anchor], tile, 1024, 1024, 30, 45)[0];
                Assert.AreEqual(expected.X, placement.Left + placement.Width / 2, 0.000001);
                Assert.AreEqual(expected.Y, placement.Top + placement.Height / 2, 0.000001);
            }
        }
    }

    [TestMethod]
    public async Task RenderedZoomFramesKeepReadableLineLabelsWithoutRedistribution()
    {
        using RenderingEventListener listener = new(
            "VectorLineSymbolPlacementSummary", "VectorLabelRenderBatch",
            "VectorLabelCollisionSummary", "MapFrameStageTiming");
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(512, 512);
        TileId id = new(4, 8, 8);
        byte[] bytes = new MapboxVectorTileBuilder()
            .AddLine("labels", [new(205, 2048), new(3891, 2048)]).Build();
        TestVectorTileSource source = TestVectorTileSource.Create(id, bytes,
            """
            {"version":8,"layers":[{"type":"symbol","source-layer":"labels",
              "layout":{"symbol-placement":"line","symbol-spacing":100,
                "text-field":"A","text-font":["TestFont"],"text-size":24,
                "text-allow-overlap":true},"paint":{"text-color":"#000000"}}]}
            """, "{}", [0, 0, 0, 0], 1, 1);
        source.AddGlyphs("TestFont", TestGlyph.RectangleSdf('A'));
        var features = VectorTileDecoder.Decode(bytes);
        var textures = await source.StyleAssets.PrepareTexturesAsync(features, 4, CancellationToken.None);
        var center = source.TileCenter;
        renderer.SetLayerRenderPlan(
        [
            new LayerRenderSnapshot(LayerRenderKind.VectorPoints, 0, 1, true, 1,
                TimeSpan.Zero, 0, 24, 0, 256, Style: (int)MapStyle.Road),
        ]);
        renderer.SetCameraTargetImmediately(center.Longitude, center.Latitude, 4, 512, 512);
        renderer.ActivateRasterTileSet(1, 1, 1,
            MapCamera.CreateScene(center.Longitude, center.Latitude, 4, 4, 512, 512, 0, 0),
            tile => tile == id, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        renderer.AddVectorTexturesForBenchmark(textures);
        renderer.AddVectorTileForBenchmark(new RasterTileKey(1, id), features, source.StyleAssets);
        List<double>? initialAnchors = null;
        for (int frame = 0; frame < 180; frame++)
        {
            double zoom = 4 + (frame < 90 ? frame : 179 - frame) / 90d;
            renderer.SetCameraTargetImmediately(center.Longitude, center.Latitude, zoom, 512, 512);
            MapRenderFrame image = renderer.CaptureOffscreenFrameForBenchmark();
            List<double> anchors = [];
            int start = -1;
            for (int x = 0; x <= 512; x++)
            {
                bool dark = false;
                for (int y = 230; x < 512 && y < 280; y++)
                {
                    int offset = (y * 512 + x) * 4;
                    if (image.Pixels.Span[offset] < 32)
                        dark = true;
                }
                if (dark && start < 0)
                    start = x;
                if (!dark && start >= 0)
                {
                    double size = 256 * Math.Pow(2, zoom - 4);
                    anchors.Add(((start + x - 1) / 2d - 256) / size);
                    Assert.IsInRange(5, 30, x - start, "Glyphs must remain readable screen-sized text.");
                    start = -1;
                }
            }
            Assert.IsNotEmpty(anchors, $"Frame {frame} must display labels during zoom.");
            initialAnchors ??= anchors;
            Assert.HasCount(initialAnchors.Count, anchors, $"Frame {frame} redistributed road labels.");
            for (int index = 0; index < anchors.Count; index++)
                Assert.AreEqual(initialAnchors[index], anchors[index], 0.005,
                    $"Frame {frame}: submitted pixels must follow the same map anchor.");
        }
        Assert.HasCount(180, listener.Events("MapFrameStageTiming"));
        Assert.HasCount(180, listener.Events("VectorLineSymbolPlacementSummary"));
        Assert.IsTrue(listener.Events("VectorLabelRenderBatch")
            .All(e => Convert.ToInt32(e.Payload[2]) == initialAnchors!.Count),
            "Event 53 must agree with the individually read-back visible glyphs.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FractionalZoomKeepsLineLabelsAtTheirMapAnchors(bool interpolateTextSize)
    {
        VectorTilePoint[] path = [new(0.05, 0.5), new(0.95, 0.5)];
        VectorTileSymbol[] symbols =
        [
            new(0, 0, 0, -1, 24, 16, 0, 0, VectorSymbolKind.Text,
                LabelId: 1, LinePoints: path, LineSpacing: 100),
        ];
        Dictionary<int, double> anchors = [];
        for (int frame = 0; frame < 90; frame++)
        {
            double size = 256 * Math.Pow(2, frame / 90d);
            VisibleTile tile = new(new TileId(4, 8, 8), 8, 512 - size / 2, 0, size);
            double textWidth = 24 + (interpolateTextSize ? frame * 0.2 : 0);
            symbols[0] = symbols[0] with { Width = textWidth };
            VectorSymbolPlacement[] placements = MapRenderer.ProjectVectorSymbols(
                symbols, tile, 1024, 1024, 0, 0);
            Assert.IsNotEmpty(placements, $"Frame {frame} must retain readable labels.");
            foreach (VectorSymbolPlacement placement in placements)
            {
                double anchor = (placement.Left + placement.Width / 2 - tile.Left) / size;
                if (anchors.TryGetValue(placement.PlacementIndex, out double previous))
                    Assert.AreEqual(previous, anchor, 0.000001,
                        $"Frame {frame}: zoom must transform the map anchor, not redistribute labels.");
                else
                    anchors.Add(placement.PlacementIndex, anchor);
                Assert.AreEqual(textWidth, placement.Width, 0.000001,
                    "Only the style, not map projection, may resize screen-space text.");
            }
        }
    }

    [TestMethod]
    public async Task CoveredFallbackFootprintRetiresWhileUnrelatedTilesAreMissing()
    {
        using RenderingEventListener listener = new("VectorLabelRenderBatch", "VectorSymbolFallbackSummary");
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(512, 512);
        TileId fallback = new(5, 16, 16);
        TileId current = new(4, 8, 8);
        TileId missing = new(4, 9, 8);
        byte[] bytes = new MapboxVectorTileBuilder().AddPoint("labels", 2048, 2048).Build();
        TestVectorTileSource source = TestVectorTileSource.Create(current, bytes,
            """
            {"version":8,"layers":[{"type":"symbol","source-layer":"labels",
              "layout":{"text-field":"A","text-font":["TestFont"],"text-size":24},
              "paint":{"text-color":"#000000"}}]}
            """, "{}", [0, 0, 0, 0], 1, 1);
        source.AddGlyphs("TestFont", TestGlyph.RectangleSdf('A'));
        var features = VectorTileDecoder.Decode(bytes);
        var textures = await source.StyleAssets.PrepareTexturesAsync(features, 4, CancellationToken.None);
        var center = source.TileCenter;
        renderer.SetLayerRenderPlan(
        [
            new LayerRenderSnapshot(LayerRenderKind.VectorPoints, 0, 1, true, 1,
                TimeSpan.Zero, 0, 24, 0, 256, Style: (int)MapStyle.Road),
        ]);
        renderer.SetCameraTargetImmediately(center.Longitude, center.Latitude, 4, 512, 512);
        renderer.ActivateRasterTileSet(1, 1, 1,
            MapCamera.CreateScene(center.Longitude, center.Latitude, 4, 5, 512, 512, 0, 0),
            tile => tile == fallback, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        renderer.AddVectorTexturesForBenchmark(textures);
        renderer.AddVectorTileForBenchmark(new RasterTileKey(1, fallback), features, source.StyleAssets);
        Assert.IsGreaterThan(0, DarkPixels(renderer.CaptureOffscreenFrameForBenchmark()));
        renderer.ActivateRasterTileSet(1, 1, 2,
            MapCamera.CreateScene(center.Longitude, center.Latitude, 4, 4, 512, 512, 0, 0),
            tile => tile == current || tile == missing, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        Assert.IsGreaterThan(0, DarkPixels(renderer.CaptureOffscreenFrameForBenchmark()),
            "Uncovered fallback labels must remain while their own replacement is missing.");
        renderer.AddVectorTileForBenchmark(new RasterTileKey(1, current),
            VectorTileDecoder.Decode(new MapboxVectorTileBuilder().Build()), source.StyleAssets);
        Assert.AreEqual(0, DarkPixels(renderer.CaptureOffscreenFrameForBenchmark()),
            "A missing unrelated tile must not retain obsolete symbols over opaque local coverage.");
        Assert.AreEqual(0, Convert.ToInt32(listener.Events("VectorLabelRenderBatch")[^1].Payload[2]));
        Assert.AreEqual(1, Convert.ToInt32(listener.Events("VectorSymbolFallbackSummary")[^1].Payload[2]),
            "Event 82 must explain the local coverage retirement.");
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(5)]
    public async Task CoveredSceneChangesDoNotResurrectCachedFallbackLabels(int fallbackZoom)
    {
        using RenderingEventListener listener = new(
            "VectorLabelRenderBatch", "MapFrameStageTiming", "TileSetActivated",
            "VectorLabelCollisionSummary", "VectorLabelFadeSummary",
            "VectorLabelTextureReadinessSummary");
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(256, 256);
        int fallbackCoordinate = (int)(8 * Math.Pow(2, fallbackZoom - 4));
        TileId parent = new(fallbackZoom, fallbackCoordinate, fallbackCoordinate);
        TileId current = new(4, 8, 8);
        int anchor = fallbackZoom < 4 ? 2048 >> (4 - fallbackZoom) : 2048;
        byte[] parentBytes = new MapboxVectorTileBuilder().AddPoint("labels", anchor, anchor).Build();
        byte[] currentBytes = new MapboxVectorTileBuilder().Build();
        TestVectorTileSource source = TestVectorTileSource.Create(current, currentBytes,
            """
            {"version":8,"layers":[{"type":"symbol","source-layer":"labels",
              "layout":{"text-field":"A","text-font":["TestFont"],"text-size":24,
                "text-allow-overlap":true},"paint":{"text-color":"#000000"}}]}
            """, "{}", [0, 0, 0, 0], 1, 1);
        source.AddGlyphs("TestFont", TestGlyph.RectangleSdf('A'));
        var features = VectorTileDecoder.Decode(parentBytes);
        var textures = await source.StyleAssets.PrepareTexturesAsync(features, 4, CancellationToken.None);
        var center = source.TileCenter;
        renderer.SetLayerRenderPlan(
        [
            new LayerRenderSnapshot(LayerRenderKind.VectorPoints, 0, 1, true, 1,
                TimeSpan.Zero, 0, 24, 0, 256, Style: (int)MapStyle.Road),
        ]);
        renderer.SetCameraTargetImmediately(center.Longitude, center.Latitude, 4, 256, 256);
        renderer.ActivateRasterTileSet(1, 1, 1,
            MapCamera.CreateScene(center.Longitude, center.Latitude, 4, fallbackZoom, 256, 256, 0, 0),
            id => id == parent, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        renderer.AddVectorTexturesForBenchmark(textures);
        renderer.AddVectorTileForBenchmark(new RasterTileKey(1, parent), features, source.StyleAssets);
        Assert.IsGreaterThan(0, DarkPixels(renderer.CaptureOffscreenFrameForBenchmark()),
            "The retained parent fixture must initially display a label.");
        renderer.ActivateRasterTileSet(1, 1, 2,
            MapCamera.CreateScene(center.Longitude, center.Latitude, 4, 4, 256, 256, 0, 0),
            id => id == current, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        for (int frame = 0; frame < 5; frame++)
        {
            renderer.SetCameraTargetImmediately(center.Longitude + frame * .001,
                center.Latitude, 4 + frame * .002, 256, 256);
            Assert.IsGreaterThan(0, DarkPixels(renderer.CaptureOffscreenFrameForBenchmark()),
                "Nearest cached parent labels must survive movement until replacement arrives.");
        }
        renderer.AddVectorTileForBenchmark(new RasterTileKey(1, current),
            VectorTileDecoder.Decode(currentBytes), source.StyleAssets);
        Assert.AreEqual(0, DarkPixels(renderer.CaptureOffscreenFrameForBenchmark()),
            "Tile arrival must retire obsolete labels on its first frame, even without a new scene.");

        List<int> framePixels = [];
        for (int step = 0; step < 30; step++)
        {
            double longitude = center.Longitude + step * .001;
            double zoom = 4 + step * .002;
            renderer.SetCameraTargetImmediately(longitude, center.Latitude, zoom, 256, 256);
            renderer.ActivateRasterTileSet(1, 1, step + 3,
                MapCamera.CreateScene(longitude, center.Latitude, zoom, 4, 256, 256, 0, 0),
                id => id == current, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
            for (int frame = 0; frame < 2; frame++)
            {
                framePixels.Add(DarkPixels(renderer.CaptureOffscreenFrameForBenchmark()));
            }
        }
        CapturedRenderingEvent[] frames = listener.Events("MapFrameStageTiming");
        Assert.AreEqual(67, frames.Length);
        Assert.AreEqual(1, frames.Select(e => e.Payload[0]).Distinct().Count(),
            "All frame stages must belong to the same renderer.");
        Assert.AreEqual(67, frames.Select(e => e.Payload[1]).Distinct().Count(),
            "Every readback must represent a distinct submitted frame.");
        CapturedRenderingEvent[] labels = listener.Events("VectorLabelRenderBatch");
        Assert.AreEqual(67, labels.Length);
        Assert.IsTrue(labels.Take(6).All(e => Convert.ToInt32(e.Payload[2]) == 1),
            "Event 53 must confirm that missing coverage retains the fallback glyph.");
        Assert.IsTrue(labels.Skip(6).All(e => Convert.ToInt32(e.Payload[2]) == 0),
            "Event 53 must confirm no fallback glyph draw on any covered frame.");
        Assert.AreEqual(0, framePixels.Count(count => count > 0),
            $"Covered scenes must not resurrect obsolete labels. Consecutive frame dark-pixel counts: {string.Join(',', framePixels)}");
    }

    private static int DarkPixels(MapRenderFrame frame)
    {
        int count = 0;
        ReadOnlySpan<byte> pixels = frame.Pixels.Span;
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i] < 32 && pixels[i + 1] < 32 && pixels[i + 2] < 32)
                count++;
        return count;
    }
}
