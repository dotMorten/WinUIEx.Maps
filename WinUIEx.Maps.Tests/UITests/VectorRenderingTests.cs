using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Geolocation;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests.UITests;

[TestClass]
[DoNotParallelize]
public sealed class VectorRenderingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(256, false, 0, 0d)]
    [DataRow(256, true, 0, 0d)]
    [DataRow(512, true, 0, 0d)]
    [DataRow(256, true, 200, 0d)]
    [DataRow(256, true, 0, 0.75)]
    [DataRow(512, true, 0, 0.75)]
    public Task CoarseGeometryCoversSkippedZoomLevelsUntilEligibleDetailIsOpaque(
        int tileSize,
        bool zoomLimitedStyle,
        int fadeMilliseconds,
        double initialZoomOffset) =>
        MapControlTestHost.LoadUIAsync(
            () => new SwapChainPanel { Width = 640, Height = 480 },
            async element =>
            {
                using RenderingEventListener listener = new(
                    "TileSetActivated",
                    "VectorLineFallbackSummary",
                    "VectorPolygonRenderBatch",
                    "VectorGeometryFallbackOpacitySummary");
                using MapRenderer renderer = new();
                const long sourceId = 42;
                TileId coarseId = new(4, 8, 8);
                double initialZoom = coarseId.Zoom + Math.Log2(tileSize / 256d);
                string zoomRange = zoomLimitedStyle
                    ? FormattableString.Invariant(
                        $"\"minzoom\": {initialZoom + initialZoomOffset}, \"maxzoom\": {initialZoom + 1},")
                    : "";
                string detailStyleZoom = (initialZoom + 4.5)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                byte[] coarse = new MapboxVectorTileBuilder()
                    .AddPolygon("coarseLand",
                        [[new(0, 0), new(4096, 0), new(4096, 4096), new(0, 4096)]])
                    .AddLine("coarseRoad", [new(0, 2048), new(4096, 2048)])
                    .Build();
                byte[] detail = new MapboxVectorTileBuilder()
                    .AddPolygon("detailLand",
                        [[new(0, 0), new(4096, 0), new(4096, 4096), new(0, 4096)]])
                    .Build();
                TestVectorTileSource source = TestVectorTileSource.Create(
                    coarseId, coarse,
                    $$"""
                    {
                      "version": 8,
                      "layers": [
                        { "type": "fill", "source-layer": "coarseLand", {{zoomRange}}
                          "paint": { "fill-color": "#00ff00" } },
                        { "type": "fill", "source-layer": "detailLand",
                          "paint": { "fill-color": [
                            "step", ["zoom"], "#ff0000", {{detailStyleZoom}}, "#0000ff"
                          ] } },
                        { "type": "line", "source-layer": "coarseRoad", {{zoomRange}}
                          "paint": { "line-color": "#ffff00", "line-width": 8 } }
                      ]
                    }
                    """,
                    "{}", [0, 0, 0, 0], 1, 1);
                BasicGeoposition center = source.TileCenter;
                renderer.Attach((SwapChainPanel)element);
                renderer.SetLayerRenderPlan(
                    [new LayerRenderSnapshot(LayerRenderKind.VectorPoints,
                        0, sourceId, true, 1, TimeSpan.FromMilliseconds(fadeMilliseconds),
                        0, 24, 0, tileSize)]);
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));

                void Activate(int sourceZoom, double longitude, long generation,
                    Func<TileId, bool> includesTile, double zoomOffset = 0)
                {
                    double displayZoom = sourceZoom + Math.Log2(tileSize / 256d) + zoomOffset;
                    renderer.SetCameraTargetImmediately(
                        longitude, center.Latitude, displayZoom, 640, 480);
                    renderer.ActivateRasterTileSet(sourceId, generation, generation,
                        MapCamera.CreateScene(longitude, center.Latitude,
                            displayZoom, sourceZoom, 640, 480, 0, 0),
                        includesTile, RasterSourceKind.Custom,
                        LayerRenderKind.VectorPoints, false);
                }

                async Task Queue(TileId id, byte[] data, long generation)
                {
                    Assert.IsTrue(await renderer.QueueVectorTileAsync(
                        new VectorTileData(new RasterTileKey(sourceId, id),
                            VectorTileDecoder.Decode(data), source.StyleAssets,
                            [], null, generation, -1), timeout.Token));
                }

                Activate(4, center.Longitude, 1, id => id == coarseId, initialZoomOffset);
                await Queue(coarseId, coarse, 1);
                MapRenderFrame initial = await renderer.CaptureFrameAsync(timeout.Token);
                Assert.IsNotEmpty(FindColor(initial, 0, 255, 0, minimumPixelCount: 20_000));
                Assert.IsNotEmpty(FindColor(initial, 255, 255, 0, minimumPixelCount: 1_000));

                Activate(8, center.Longitude, 2, _ => true);
                MapRenderFrame pending = await renderer.CaptureFrameAsync(timeout.Token);
                string resultsDirectory = Path.Combine(AppContext.BaseDirectory, "TestResults");
                string scenario = FormattableString.Invariant(
                    $"{tileSize}-{zoomLimitedStyle}-{fadeMilliseconds}-{initialZoomOffset}");
                string artifact = Path.Combine(resultsDirectory,
                    $"zoom-pending-{scenario}.png");
                await pending.SavePngAsync(artifact);
                foreach (CapturedRenderingEvent captured in listener.Events(
                    "VectorGeometryFallbackOpacitySummary"))
                {
                    TestContext.WriteLine($"{captured.Name}: {string.Join(", ", captured.Payload)}");
                }
                Assert.IsNotEmpty(FindColor(pending, 0, 255, 0, minimumPixelCount: 100_000),
                    "Coarse land must remain while all detailed requests are withheld.");
                Assert.IsNotEmpty(FindColor(pending, 255, 255, 0, minimumPixelCount: 3_000),
                    "Coarse roads must survive a four-level jump and style maxzoom.");
                Assert.IsTrue(listener.Events("TileSetActivated").Any(captured =>
                    Convert.ToInt32(captured.Payload[1]) == 8 &&
                    Convert.ToInt32(captured.Payload[3]) == 1));
                Assert.IsTrue(listener.Events("VectorGeometryFallbackOpacitySummary").Any(captured =>
                    Convert.ToInt32(captured.Payload[2]) > 0 &&
                    Convert.ToDouble(captured.Payload[5]) == 1));
                Assert.IsTrue(listener.Events("VectorLineFallbackSummary").Any(captured =>
                    Convert.ToInt32(captured.Payload[2]) > 0 &&
                    Convert.ToInt32(captured.Payload[3]) == 0 &&
                    Convert.ToDouble(captured.Payload[4]) >= 4));

                // Interrupt the zoom and pan before any replacement can arrive.
                Activate(7, center.Longitude + 0.1, 3, _ => true);
                MapRenderFrame reversed = await renderer.CaptureFrameAsync(timeout.Token);
                Assert.IsNotEmpty(FindColor(reversed, 0, 255, 0, minimumPixelCount: 100_000));
                Activate(8, center.Longitude, 4, _ => true);
                MapScene detailScene = MapCamera.CreateScene(center.Longitude, center.Latitude,
                    initialZoom + 4, 8, 640, 480, 0, 0);
                TileId firstDetail = detailScene.RequiredTiles
                    .First(id => id.X == 136 && id.Y == 136);
                await Queue(firstDetail, detail, 4);
                MapRenderFrame partial = await renderer.CaptureFrameAsync(timeout.Token);
                Assert.IsNotEmpty(FindColor(partial, 0, 255, 0, minimumPixelCount: 100_000));
                Assert.IsNotEmpty(FindColor(partial, 255, 0, 0, minimumPixelCount: 20_000));

                int completedCoverageEventStart =
                    listener.Events("VectorGeometryFallbackOpacitySummary").Length;
                foreach (TileId id in detailScene.RequiredTiles.Where(id => id != firstDetail))
                {
                    await Queue(id, detail, 4);
                }
                MapRenderFrame complete = await renderer.CaptureFrameAsync(timeout.Token);
                Assert.IsNotEmpty(FindColor(complete, 255, 0, 0, minimumPixelCount: 300_000));
                Assert.IsEmpty(FindColor(complete, 0, 255, 0, minimumPixelCount: 10));
                Assert.IsEmpty(FindColor(complete, 255, 255, 0, minimumPixelCount: 10));
                if (fadeMilliseconds > 0)
                {
                    CapturedRenderingEvent[] fadingEvents =
                        listener.Events("VectorGeometryFallbackOpacitySummary")[completedCoverageEventStart..];
                    Assert.IsTrue(fadingEvents.Any(captured =>
                        Convert.ToInt32(captured.Payload[1]) == 1 &&
                        Convert.ToDouble(captured.Payload[5]) > 0 &&
                        Convert.ToDouble(captured.Payload[6]) < 1),
                        "Roads should crossfade only after every replacement tile arrives.");
                    Assert.IsTrue(fadingEvents.Any(captured =>
                        Convert.ToInt32(captured.Payload[1]) == 2 &&
                        Convert.ToDouble(captured.Payload[5]) == 1),
                        "Land coverage must stay opaque while replacement tiles fade in.");
                }
                await complete.SavePngAsync(Path.Combine(resultsDirectory,
                    $"zoom-complete-{scenario}.png"));

                renderer.SetCameraTargetImmediately(
                    center.Longitude, center.Latitude, initialZoom + 4.75, 640, 480);
                MapRenderFrame overzoomed = await renderer.CaptureFrameAsync(timeout.Token);
                Assert.IsNotEmpty(FindColor(overzoomed, 0, 0, 255, minimumPixelCount: 300_000),
                    "Active tiles must continue evaluating styles at the displayed zoom.");

                Activate(8, center.Longitude + 4, 5, _ => true);
                MapRenderFrame panned = await renderer.CaptureFrameAsync(timeout.Token);
                Assert.IsNotEmpty(FindColor(panned, 0, 255, 0, minimumPixelCount: 100_000),
                    "A new viewport must recover cached coarse coverage after the previous one completed.");
            });

    [TestMethod]
    public Task PitchedPanReusesRetainedVectorGeometry() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            using RenderingEventListener listener = new(
                "VectorGeometryFrameCacheSummary");
            TileId tileId = new(4, 8, 8);
            byte[] tile = new MapboxVectorTileBuilder()
                .AddPolygon(
                    "land",
                    [[
                        new TestTilePoint(800, 800),
                        new TestTilePoint(3296, 800),
                        new TestTilePoint(3296, 3296),
                        new TestTilePoint(800, 3296),
                    ]])
                .AddLine(
                    "roads",
                    [new(600, 2048), new(3496, 2048)])
                .Build();
            TestVectorTileSource source = TestVectorTileSource.Create(
                tileId,
                tile,
                """
                {
                  "version": 8,
                  "layers": [
                    {
                      "type": "fill",
                      "source-layer": "land",
                      "paint": { "fill-color": "#00ff00" }
                    },
                    {
                      "type": "line",
                      "source-layer": "roads",
                      "paint": {
                        "line-color": "#ff0000",
                        "line-width": 4
                      }
                    }
                  ]
                }
                """,
                "{}",
                [0, 0, 0, 0],
                1,
                1);
            BasicGeoposition center = source.TileCenter;
            map.MapStyle = MapStyle.Blank;
            Assert.IsTrue(await map.TrySetViewAsync(
                new Geopoint(center),
                tileId.Zoom,
                null,
                60,
                MapAnimationKind.None));
            map.Layers.Add(new TestVectorTileLayer(
                source,
                TimeSpan.Zero));
            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(5));
            _ = await map.CaptureRenderedFrameAsync(timeout.Token);
            int initialEventCount = listener.Events(
                "VectorGeometryFrameCacheSummary").Length;

            Assert.IsTrue(await map.TrySetViewAsync(
                new Geopoint(new BasicGeoposition
                {
                    Longitude = center.Longitude + 0.5,
                    Latitude = center.Latitude,
                }),
                tileId.Zoom,
                null,
                60,
                MapAnimationKind.None));
            _ = await map.CaptureRenderedFrameAsync(timeout.Token);

            CapturedRenderingEvent[] panEvents = listener.Events(
                "VectorGeometryFrameCacheSummary")[initialEventCount..];
            Assert.IsTrue(panEvents.Any(captured =>
                Convert.ToInt32(captured.Payload[1]) == 1 &&
                Convert.ToInt32(captured.Payload[2]) == 1));
            Assert.IsTrue(panEvents.Any(captured =>
                Convert.ToInt32(captured.Payload[1]) == 2 &&
                Convert.ToInt32(captured.Payload[2]) == 1));
        });

    [TestMethod]
    public Task BackgroundCoversViewportWhileNextZoomTilesArePending() =>
        MapControlTestHost.LoadUIAsync(
            () => new SwapChainPanel { Width = 640, Height = 480 },
            async element =>
            {
                using RenderingEventListener listener = new(
                    "TileSetActivated",
                    "VectorGeometryFallbackOpacitySummary",
                    "VectorPolygonRenderBatch");
                var panel = (SwapChainPanel)element;
                using MapRenderer renderer = new();
                const long sourceId = 42;
                TileId fallbackTileId = new(4, 8, 8);
                byte[] tile = new MapboxVectorTileBuilder()
                    .AddPoint("unused", 2048, 2048)
                    .Build();
                TestVectorTileSource source = TestVectorTileSource.Create(
                    fallbackTileId,
                    tile,
                    """
                    {
                      "version": 8,
                      "layers": [{
                        "type": "background",
                        "paint": { "background-color": "#123456" }
                      }]
                    }
                    """,
                    "{}",
                    [0, 0, 0, 0],
                    1,
                    1);
                BasicGeoposition center = source.TileCenter;
                MapScene fallbackScene = MapCamera.CreateScene(
                    center.Longitude,
                    center.Latitude,
                    4,
                    4,
                    640,
                    480,
                    0,
                    0);
                renderer.Attach(panel);
                renderer.SetLayerRenderPlan(
                    [
                        new LayerRenderSnapshot(
                            LayerRenderKind.VectorPoints,
                            0,
                            sourceId,
                            true,
                            1,
                            TimeSpan.Zero,
                            0,
                            24,
                            0,
                            256),
                    ]);
                renderer.SetCameraTargetImmediately(
                    center.Longitude,
                    center.Latitude,
                    4,
                    640,
                    480);
                renderer.ActivateRasterTileSet(
                    sourceId,
                    1,
                    1,
                    fallbackScene,
                    id => id == fallbackTileId,
                    RasterSourceKind.Custom,
                    LayerRenderKind.VectorPoints,
                    clearExistingTiles: false);
                Assert.IsTrue(await renderer.QueueVectorTileAsync(
                    new VectorTileData(
                        new RasterTileKey(sourceId, fallbackTileId),
                        VectorTileDecoder.Decode(tile),
                        source.StyleAssets,
                        [],
                        null,
                        1,
                        -1),
                    CancellationToken.None));

                using CancellationTokenSource timeout =
                    new(TimeSpan.FromSeconds(10));
                _ = await renderer.CaptureFrameAsync(timeout.Token);

                renderer.SetCameraTargetImmediately(
                    center.Longitude,
                    center.Latitude,
                    5,
                    640,
                    480);
                MapScene pendingScene = MapCamera.CreateScene(
                    center.Longitude,
                    center.Latitude,
                    5,
                    5,
                    640,
                    480,
                    0,
                    0);
                renderer.ActivateRasterTileSet(
                    sourceId,
                    2,
                    2,
                    pendingScene,
                    _ => true,
                    RasterSourceKind.Custom,
                    LayerRenderKind.VectorPoints,
                    clearExistingTiles: false);

                MapRenderFrame pending =
                    await renderer.CaptureFrameAsync(timeout.Token);
                ConnectedComponent background = Assert.ContainsSingle(
                    FindColor(
                        pending,
                        18,
                        52,
                        86,
                        minimumPixelCount: 250_000));

                Assert.IsGreaterThanOrEqualTo(638, background.Bounds.Width);
                Assert.IsGreaterThanOrEqualTo(478, background.Bounds.Height);
                Assert.IsTrue(listener.Events("TileSetActivated").Any(captured =>
                    Convert.ToInt32(captured.Payload[1]) == 5 &&
                    Convert.ToInt32(captured.Payload[3]) == 1));
                Assert.IsTrue(listener.Events(
                    "VectorGeometryFallbackOpacitySummary").Any(captured =>
                        Convert.ToInt32(captured.Payload[2]) > 0));
                Assert.IsTrue(listener.Events(
                    "VectorPolygonRenderBatch").Any(captured =>
                        Convert.ToInt32(captured.Payload[1]) > 0));
            });

    [TestMethod]
    public Task BufferedTranslucentPolygonsDoNotOverlapAcrossTileEdges() =>
        MapControlTestHost.LoadUIAsync(
            () => new SwapChainPanel { Width = 512, Height = 256 },
            async element =>
            {
                var panel = (SwapChainPanel)element;
                using MapRenderer renderer = new();
                const long sourceId = 43;
                const int zoom = 4;
                TileId leftTile = new(zoom, 8, 8);
                TileId rightTile = new(zoom, 9, 8);
                byte[] tile = new MapboxVectorTileBuilder()
                    .AddPolygon(
                        "land",
                        [
                            [
                                new TestTilePoint(-256, -256),
                                new TestTilePoint(4352, -256),
                                new TestTilePoint(4352, 4352),
                                new TestTilePoint(-256, 4352),
                            ],
                        ])
                    .Build();
                TestVectorTileSource source = TestVectorTileSource.Create(
                    leftTile,
                    tile,
                    """
                    {
                      "version": 8,
                      "layers": [
                        {
                          "type": "background",
                          "paint": { "background-color": "#ffffff" }
                        },
                        {
                          "type": "fill",
                          "source-layer": "land",
                          "paint": {
                            "fill-color": "rgba(0, 255, 0, 0.5)",
                            "fill-antialias": false
                          }
                        }
                      ]
                    }
                    """,
                    "{}",
                    [0, 0, 0, 0],
                    1,
                    1);
                double worldX = 9d / (1 << zoom);
                double worldY = 8.5 / (1 << zoom);
                BasicGeoposition center = new()
                {
                    Longitude = (worldX * 360) - 180,
                    Latitude = Math.Atan(Math.Sinh(
                        Math.PI * (1 - (2 * worldY)))) * 180 / Math.PI,
                };
                MapScene scene = MapCamera.CreateScene(
                    center.Longitude,
                    center.Latitude,
                    zoom,
                    zoom,
                    512,
                    256,
                    0,
                    0);
                renderer.Attach(panel);
                renderer.SetCameraTargetImmediately(
                    center.Longitude,
                    center.Latitude,
                    zoom,
                    512,
                    256);
                renderer.SetLayerRenderPlan(
                    [
                        new LayerRenderSnapshot(
                            LayerRenderKind.VectorPoints,
                            0,
                            sourceId,
                            true,
                            1,
                            TimeSpan.Zero,
                            0,
                            24,
                            0,
                            256),
                    ]);
                renderer.ActivateRasterTileSet(
                    sourceId,
                    1,
                    1,
                    scene,
                    id => id == leftTile || id == rightTile,
                    RasterSourceKind.Custom,
                    LayerRenderKind.VectorPoints,
                    clearExistingTiles: false);
                VectorTileFeatureCollection features =
                    VectorTileDecoder.Decode(tile);
                foreach (TileId id in new[] { leftTile, rightTile })
                {
                    Assert.IsTrue(await renderer.QueueVectorTileAsync(
                        new VectorTileData(
                            new RasterTileKey(sourceId, id),
                            features,
                            source.StyleAssets,
                            [],
                            null,
                            1,
                            -1),
                        CancellationToken.None));
                }

                using CancellationTokenSource timeout =
                    new(TimeSpan.FromSeconds(10));
                MapRenderFrame frame =
                    await renderer.CaptureFrameAsync(timeout.Token);
                (byte Red, byte Green, byte Blue) interior =
                    GetPixel(frame, 128, 128);
                (byte Red, byte Green, byte Blue) leftEdge =
                    GetPixel(frame, 252, 128);
                (byte Red, byte Green, byte Blue) rightEdge =
                    GetPixel(frame, 260, 128);

                Assert.AreEqual(interior.Red, leftEdge.Red, 2);
                Assert.AreEqual(interior.Green, leftEdge.Green, 2);
                Assert.AreEqual(interior.Blue, leftEdge.Blue, 2);
                Assert.AreEqual(interior.Red, rightEdge.Red, 2);
                Assert.AreEqual(interior.Green, rightEdge.Green, 2);
                Assert.AreEqual(interior.Blue, rightEdge.Blue, 2);
            });

    [TestMethod]
    public Task LegacyStyleTokensStopsBackgroundAndRgbColorsRender() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            TileId tileId = new(4, 8, 8);
            byte[] tile = new MapboxVectorTileBuilder()
                .AddPolygon(
                    "land",
                    [
                        [
                            new TestTilePoint(1024, 1024),
                            new TestTilePoint(3072, 1024),
                            new TestTilePoint(3072, 3072),
                            new TestTilePoint(1024, 3072),
                        ],
                    ])
                .AddLine(
                    "road",
                    [new(512, 2048), new(3584, 2048)],
                    new Dictionary<string, object>
                    {
                        ["_symbol"] = 3,
                        ["Viz"] = 2,
                    })
                .AddPoint(
                    "labels",
                    2048,
                    2048,
                    new Dictionary<string, object>
                    {
                        ["_name_global"] = "A",
                    })
                .Build();
            TestVectorTileSource source = TestVectorTileSource.Create(
                tileId,
                tile,
                """
                {
                  "version": 8,
                  "layers": [
                    {
                      "type": "background",
                      "paint": {
                        "background-color": "rgb(10, 20, 30)"
                      }
                    },
                    {
                      "type": "fill",
                      "source-layer": "land",
                      "paint": {
                        "fill-color": {
                          "stops": [
                            [3, "rgba(0, 255, 0, 1)"],
                            [5, "rgba(0, 255, 0, 1)"]
                          ]
                        }
                      }
                    },
                    {
                      "type": "line",
                      "source-layer": "road",
                      "filter": ["all",
                        ["==", "_symbol", 3],
                        ["!in", "Viz", 3]
                      ],
                      "layout": {
                        "line-cap": "round",
                        "line-join": "round"
                      },
                      "paint": {
                        "line-color": "#e69973",
                        "line-width": {
                          "base": 1.2,
                          "stops": [[4, 8], [5, 12]]
                        }
                      }
                    },
                    {
                      "type": "symbol",
                      "source-layer": "labels",
                      "layout": {
                        "text-field": "{_name_global}",
                        "text-font": ["TestFont"],
                        "text-size": 32,
                        "text-allow-overlap": true
                      },
                      "paint": {
                        "text-color": "rgba(255, 255, 255, 1)"
                      }
                    }
                  ]
                }
                """,
                "{}",
                [0, 0, 0, 0],
                1,
                1);
            source.AddGlyphs("TestFont", TestGlyph.Solid('A'));

            MapRenderFrame frame = await RenderAsync(map, source);

            Assert.IsNotEmpty(ConnectedComponentAnalyzer.Find(
                frame,
                ConnectedComponentAnalyzer.Near(
                    10,
                    20,
                    30,
                    tolerance: 4,
                    minimumAlpha: 240),
                minimumPixelCount: 1000));
            Assert.IsNotEmpty(ConnectedComponentAnalyzer.Find(
                frame,
                ConnectedComponentAnalyzer.Near(
                    0,
                    255,
                    0,
                    tolerance: 8,
                    minimumAlpha: 240),
                minimumPixelCount: 1000));
            Assert.IsNotEmpty(ConnectedComponentAnalyzer.Find(
                frame,
                ConnectedComponentAnalyzer.Near(
                    230,
                    153,
                    115,
                    tolerance: 8,
                    minimumAlpha: 240),
                minimumPixelCount: 20));
            Assert.IsNotEmpty(ConnectedComponentAnalyzer.Find(
                frame,
                ConnectedComponentAnalyzer.Near(
                    255,
                    255,
                    255,
                    tolerance: 8,
                    minimumAlpha: 240),
                minimumPixelCount: 20));
        });

    [TestMethod]
    public Task ReplacingVectorStyleWithPatternedFillDoesNotReuseMissingGeometryBatch() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            TileId tileId = new(4, 8, 8);
            byte[] tile = new MapboxVectorTileBuilder()
                .AddPolygon(
                    "land",
                    [
                        [
                            new TestTilePoint(512, 512),
                            new TestTilePoint(3584, 512),
                            new TestTilePoint(3584, 3584),
                            new TestTilePoint(512, 3584),
                        ],
                    ])
                .Build();
            TestVectorTileSource solidSource = TestVectorTileSource.Create(
                tileId,
                tile,
                """
                {
                  "version": 8,
                  "layers": [{
                    "type": "fill",
                    "source-layer": "land",
                    "paint": { "fill-color": "#0000ff" }
                  }]
                }
                """,
                "{}",
                [0, 0, 0, 0],
                1,
                1);
            TestVectorTileSource patternSource = TestVectorTileSource.Create(
                tileId,
                tile,
                """
                {
                  "version": 8,
                  "layers": [{
                    "type": "fill",
                    "source-layer": "land",
                    "paint": { "fill-pattern": "land-pattern" }
                  }]
                }
                """,
                """
                {
                  "land-pattern": {
                    "x": 0,
                    "y": 0,
                    "width": 2,
                    "height": 2,
                    "pixelRatio": 1,
                    "visible": true
                  }
                }
                """,
                [
                    0, 0, 255, 255,
                    0, 0, 255, 255,
                    0, 0, 255, 255,
                    0, 0, 255, 255,
                ],
                2,
                2);
            TestVectorTileLayer layer = new(solidSource);
            map.MapStyle = MapStyle.Blank;
            map.Center = new Geopoint(solidSource.TileCenter);
            map.ZoomLevel = tileId.Zoom;
            map.Layers.Add(layer);
            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));

            _ = await map.CaptureRenderedFrameAsync(timeout.Token);
            layer.ReplaceSource(patternSource);
            MapRenderFrame patterned =
                await map.CaptureRenderedFrameAsync(timeout.Token);

            Assert.IsNotEmpty(
                ConnectedComponentAnalyzer.Find(
                    patterned,
                    ConnectedComponentAnalyzer.Near(
                        255,
                        0,
                        0,
                        tolerance: 8,
                        minimumAlpha: 240),
                    minimumPixelCount: 100));
        });

    [TestMethod]
    public Task PointShieldRendersCenteredTextComponents() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            TileId tileId = new(4, 8, 8);
            byte[] tile = new MapboxVectorTileBuilder()
                .AddPoint(
                    "markers",
                    2048,
                    2048,
                    new Dictionary<string, object>
                    {
                        ["label"] = "7",
                    })
                .Build();
            TestVectorTileSource source = CreateShieldSource(
                tileId,
                tile,
                """
                {
                  "version": 8,
                  "layers": [{
                    "type": "symbol",
                    "source-layer": "markers",
                    "layout": {
                      "icon-image": "shield",
                      "icon-text-fit": "both",
                      "text-field": ["get", "label"],
                      "text-font": ["TestFont"],
                      "text-size": 12
                    },
                    "paint": {
                      "text-color": "#000000"
                    }
                  }]
                }
                """,
                '7');
            MapRenderFrame captured = await RenderAsync(map, source);
            ConnectedComponent[] redComponents =
                ConnectedComponentAnalyzer.Find(
                    captured,
                    ConnectedComponentAnalyzer.Near(
                        255,
                        0,
                        0,
                        tolerance: 8,
                        minimumAlpha: 240),
                    minimumPixelCount: 100);
            ConnectedComponent[] blackComponents =
                ConnectedComponentAnalyzer.Find(
                    captured,
                    ConnectedComponentAnalyzer.Near(
                        0,
                        0,
                        0,
                        tolerance: 24,
                        minimumAlpha: 240),
                    minimumPixelCount: 20);

            Assert.AreEqual(640, captured.Width);
            Assert.AreEqual(480, captured.Height);
            Assert.AreEqual(1, redComponents.Length);
            PixelBounds shield = redComponents[0].Bounds;
            ConnectedComponent text = blackComponents.First(component =>
                shield.Contains(component.Bounds));
            Assert.AreEqual(captured.Width / 2d, shield.CenterX, 3);
            Assert.AreEqual(captured.Height / 2d, shield.CenterY, 3);
            Assert.IsTrue(shield.Width >= 20);
            Assert.IsTrue(shield.Height >= 12);
            Assert.IsTrue(shield.Contains(text.Bounds));
        });

    [TestMethod]
    public Task FittedShieldIconsRenderBehindEveryGlyphBatch() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            TileId tileId = new(4, 8, 8);
            const string labels = "ABCDEFGHIJKLMNOPQR";
            MapboxVectorTileBuilder builder = new();
            for (int index = 0; index < labels.Length; index++)
            {
                int column = index % 6;
                int row = index / 6;
                builder.AddPoint(
                    "markers",
                    420 + (column * 650),
                    850 + (row * 1200),
                    new Dictionary<string, object>
                    {
                        ["label"] = labels[index].ToString(),
                    });
            }
            TestVectorTileSource source = CreateShieldSource(
                tileId,
                builder.Build(),
                """
                {
                  "version": 8,
                  "layers": [{
                    "type": "symbol",
                    "source-layer": "markers",
                    "layout": {
                      "icon-image": "shield",
                      "icon-text-fit": "both",
                      "text-field": ["get", "label"],
                      "text-font": ["TestFont"],
                      "text-size": 12
                    },
                    "paint": {
                      "text-color": "#000000"
                    }
                  }]
                }
                """,
                labels.ToCharArray());

            MapRenderFrame frame = await RenderAsync(map, source);
            ConnectedComponent[] shields = FindColor(
                frame,
                255,
                0,
                0,
                minimumPixelCount: 100);
            ConnectedComponent[] glyphs = FindColor(
                frame,
                0,
                0,
                0,
                minimumPixelCount: 10,
                tolerance: 24);

            Assert.AreEqual(labels.Length, shields.Length);
            Assert.IsTrue(shields.All(shield =>
                glyphs.Any(glyph => shield.Bounds.Contains(glyph.Bounds))));
        });

    [TestMethod]
    public Task LineShieldsRenderEveryNumericRouteLabel() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            TileId tileId = new(4, 8, 8);
            byte[] tile = new MapboxVectorTileBuilder()
                .AddLine(
                    "roads",
                    [new(1400, 1300), new(2696, 1300)],
                    new Dictionary<string, object>
                    {
                        ["route"] = 7,
                        ["shield-scale"] = 0.8,
                    })
                .AddLine(
                    "roads",
                    [new(1400, 2800), new(2696, 2800)],
                    new Dictionary<string, object>
                    {
                        ["route"] = 12,
                        ["shield-scale"] = 1d,
                    })
                .Build();
            TestVectorTileSource source = CreateShieldSource(
                tileId,
                tile,
                """
                {
                  "version": 8,
                  "layers": [{
                    "type": "symbol",
                    "source-layer": "roads",
                    "layout": {
                      "symbol-placement": "line",
                      "icon-image": "shield",
                      "icon-text-fit": "both",
                      "icon-rotation-alignment": "viewport",
                      "text-field": ["to-string", ["get", "route"]],
                      "text-font": ["TestFont"],
                      "text-size": [
                        "*",
                        10,
                        ["number", ["get", "shield-scale"], 0.8]
                      ],
                      "text-rotation-alignment": "viewport"
                    },
                    "paint": {
                      "text-color": "#000000"
                    }
                  }]
                }
                """,
                '1',
                '2',
                '7');

            MapRenderFrame frame = await RenderAsync(map, source);
            ConnectedComponent[] shields = FindColor(
                frame,
                255,
                0,
                0,
                minimumPixelCount: 100);
            ConnectedComponent[] glyphs = FindColor(
                frame,
                0,
                0,
                0,
                minimumPixelCount: 10,
                tolerance: 24);

            Assert.AreEqual(2, shields.Length);
            Assert.IsTrue(shields.All(shield =>
                glyphs.Any(glyph => shield.Bounds.Contains(glyph.Bounds))));
            Assert.IsTrue(shields.All(shield =>
                shield.Bounds.Width >= 20 &&
                shield.Bounds.Height >= 12));
        });

    [TestMethod]
    public Task ArcGisLineShieldTextIsCenteredWithoutTextFit() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            TileId tileId = new(4, 8, 8);
            byte[] tile = new MapboxVectorTileBuilder()
                .AddLine(
                    "roads",
                    [new(1400, 2048), new(2696, 2048)],
                    new Dictionary<string, object>
                    {
                        ["_label_class"] = 7,
                        ["Viz"] = 2,
                        ["_name"] = "405",
                        ["_len"] = 3,
                    })
                .Build();
            TestVectorTileSource source = TestVectorTileSource.Create(
                tileId,
                tile,
                """
                {
                  "version": 8,
                  "layers": [{
                    "type": "symbol",
                    "source-layer": "roads",
                    "filter": ["all",
                      ["==", "_label_class", 7],
                      ["!in", "Viz", 3]
                    ],
                    "layout": {
                      "symbol-placement": "line",
                      "symbol-spacing": 250,
                      "icon-image": "shield/{_len}",
                      "icon-rotation-alignment": "viewport",
                      "text-field": "{_name}",
                      "text-font": ["TestFont"],
                      "text-size": 10,
                      "text-rotation-alignment": "viewport"
                    },
                    "paint": {
                      "text-color": "#ffffff"
                    }
                  }]
                }
                """,
                """
                {
                  "shield/3": {
                    "x": 0, "y": 0, "width": 26, "height": 28,
                    "pixelRatio": 1, "visible": true
                  }
                }
                """,
                Enumerable.Range(0, 26 * 28)
                    .SelectMany(_ => new byte[] { 255, 0, 0, 255 })
                    .ToArray(),
                26,
                28);
            source.AddGlyphs(
                "TestFont",
                TestGlyph.Solid('4'),
                TestGlyph.Solid('0'),
                TestGlyph.Solid('5'));

            MapRenderFrame frame = await RenderAsync(map, source);
            ConnectedComponent shield = Assert.ContainsSingle(
                FindColor(
                    frame,
                    0,
                    0,
                    255,
                    minimumPixelCount: 300));
            PixelBounds text = Union(
                FindColor(
                    frame,
                    255,
                    255,
                    255,
                    minimumPixelCount: 5,
                    tolerance: 8));

            Assert.IsTrue(shield.Bounds.Contains(text));
            Assert.AreEqual(shield.Bounds.CenterY, text.CenterY, 2);
        });

    [TestMethod]
    public Task ViewportAlignedLineShieldIsUprightAndCenteredOnRoad() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            TileId tileId = new(4, 8, 8);
            byte[] tile = new MapboxVectorTileBuilder()
                .AddLine(
                    "roads",
                    [new(1024, 3072), new(3072, 1024)],
                    new Dictionary<string, object>
                    {
                        ["route"] = 7,
                    })
                .Build();
            TestVectorTileSource source = CreateShieldSource(
                tileId,
                tile,
                """
                {
                  "version": 8,
                  "layers": [
                    {
                      "type": "line",
                      "source-layer": "roads",
                      "paint": {
                        "line-color": "#00ff00",
                        "line-width": 4
                      }
                    },
                    {
                      "type": "symbol",
                      "source-layer": "roads",
                      "layout": {
                        "symbol-placement": "line",
                        "icon-image": "shield",
                        "icon-text-fit": "both",
                        "icon-rotation-alignment": "viewport",
                        "text-field": ["to-string", ["get", "route"]],
                        "text-font": ["TestFont"],
                        "text-size": 10,
                        "text-rotation-alignment": "viewport"
                      },
                      "paint": {
                        "text-color": "#000000"
                      }
                    }
                  ]
                }
                """,
                '7');

            MapRenderFrame frame = await RenderAsync(map, source);
            ConnectedComponent shield = Assert.ContainsSingle(
                FindColor(frame, 255, 0, 0, minimumPixelCount: 100));
            ConnectedComponent[] roadParts = FindColor(
                frame,
                0,
                255,
                0,
                minimumPixelCount: 20,
                tolerance: 8);
            PixelBounds road = Union(roadParts);

            Assert.IsGreaterThan(shield.Bounds.Height, shield.Bounds.Width);
            Assert.AreEqual(road.CenterX, shield.Bounds.CenterX, 3);
            Assert.AreEqual(road.CenterY, shield.Bounds.CenterY, 3);
            Assert.AreEqual(frame.Width / 2d, shield.Bounds.CenterX, 3);
            Assert.AreEqual(frame.Height / 2d, shield.Bounds.CenterY, 3);
        });

    [TestMethod]
    public Task TextFitShieldWithoutTextIsSuppressed() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            TileId tileId = new(4, 8, 8);
            byte[] tile = new MapboxVectorTileBuilder()
                .AddPoint("markers", 2048, 2048)
                .Build();
            TestVectorTileSource source = CreateShieldSource(
                tileId,
                tile,
                """
                {
                  "version": 8,
                  "layers": [{
                    "type": "symbol",
                    "source-layer": "markers",
                    "layout": {
                      "icon-image": "shield",
                      "icon-text-fit": "both",
                      "text-field": ["get", "missing-label"],
                      "text-font": ["TestFont"],
                      "text-size": 12
                    }
                  }]
                }
                """,
                '7');

            MapRenderFrame frame = await RenderAsync(map, source);

            Assert.IsEmpty(FindColor(
                frame,
                255,
                0,
                0,
                minimumPixelCount: 20));
        });

    [TestMethod]
    [DataRow(false, 0d, 0d, 0d)]
    [DataRow(false, 0.75, 45d, 0d)]
    [DataRow(false, 0.5, 90d, 40d)]
    [DataRow(true, 0d, 0d, 0d)]
    [DataRow(true, 0.75, 45d, 0d)]
    public Task TileEdgeLabelsRenderEveryGlyphDuringZoomAndRotation(
        bool linePlacement, double zoomOffset, double heading, double pitch) =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            map.IsTextScaleFactorEnabled = false;
            TileId tileId = new(4, 8, 8);
            MapboxVectorTileBuilder builder = new();
            if (linePlacement)
            {
                builder.AddLine("labels", [new(64, 256), new(64, 3840)]);
            }
            else
            {
                builder.AddPoint("labels", 64, 2048);
            }
            TestVectorTileSource source = TestVectorTileSource.Create(
                tileId, builder.Build(),
                $$$"""
                {
                  "version": 8,
                  "layers": [
                    {"type": "background", "paint": {"background-color": "#ffffff"}},
                    {
                      "type": "symbol",
                      "source-layer": "labels",
                      "layout": {
                        "symbol-placement": "{{{(linePlacement ? "line" : "point")}}}",
                        "symbol-spacing": 1000,
                        "symbol-avoid-edges": true,
                        "text-field": "ABCDEFG",
                        "text-font": ["TestFont"],
                        "text-size": 24,
                        "text-max-width": 100,
                        "text-rotation-alignment": "viewport"
                      },
                      "paint": {"text-color": "#000000"}
                    }
                  ]
                }
                """,
                "{}", [0, 0, 0, 0], 1, 1);
            source.AddGlyphs(
                "TestFont",
                [.. "ABCDEFG".Select(character => TestGlyph.Solid(character, advance: 18))]);
            await RenderAsync(map, source);
            Assert.IsTrue(await map.TrySetViewAsync(
                new Geopoint(source.TileCenter),
                tileId.Zoom + zoomOffset,
                heading,
                pitch,
                MapAnimationKind.None));
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            MapRenderFrame frame = await map.CaptureRenderedFrameAsync(timeout.Token);

            Assert.HasCount(7, FindColor(frame, 0, 0, 0, minimumPixelCount: 40));
        });

    [TestMethod]
    [DataRow("point", false, "ABBA")]
    [DataRow("point", true, "ABBA")]
    [DataRow("point", false, "AAAA")]
    [DataRow("line", false, "ABBA")]
    [DataRow("line", true, "ABBA")]
    public Task TextHalosDoNotCoverNeighboringGlyphFills(
        string placement,
        bool allowOverlap,
        string label) =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            map.IsTextScaleFactorEnabled = false;
            TileId tileId = new(4, 8, 8);
            MapboxVectorTileBuilder builder = new();
            Dictionary<string, object> properties = new() { ["label"] = label };
            if (placement == "line")
            {
                builder.AddLine(
                    "markers",
                    [new(1000, 2048), new(3096, 2048)],
                    properties);
            }
            else
            {
                builder.AddPoint("markers", 2048, 2048, properties);
            }
            byte[] tile = builder.Build();
            TestVectorTileSource CreateSource(string haloColor)
            {
                TestVectorTileSource source = TestVectorTileSource.Create(
                    tileId,
                    tile,
                    $$"""
                    {
                      "version": 8,
                      "layers": [
                        {
                          "type": "background",
                          "paint": {"background-color": "#0044cc"}
                        },
                        {
                          "type": "symbol",
                          "source-layer": "markers",
                          "layout": {
                            "symbol-placement": "{{placement}}",
                            "text-field": ["get", "label"],
                            "text-font": ["TestFont"],
                            "text-size": 24,
                            "text-allow-overlap": {{allowOverlap.ToString().ToLowerInvariant()}}
                          },
                          "paint": {
                            "text-color": "#000000",
                            "text-halo-color": "{{haloColor}}",
                            "text-halo-width": 3
                          }
                        }
                      ]
                    }
                    """,
                    "{}",
                    [0, 0, 0, 0],
                    1,
                    1);
                source.AddGlyphs(
                    "TestFont",
                    TestGlyph.RectangleSdf('A'),
                    TestGlyph.RectangleSdf('B'));
                return source;
            }

            MapRenderFrame withoutHalo = await RenderAsync(
                map, CreateSource("#00000000"));
            ((TestVectorTileLayer)map.Layers[0]).ReplaceSource(CreateSource("#ffffff"));
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            MapRenderFrame withHalo = await map.CaptureRenderedFrameAsync(timeout.Token);
            int blackPixels = 0;
            int coveredPixels = 0;
            for (int y = 0; y < withoutHalo.Height; y++)
            {
                for (int x = 0; x < withoutHalo.Width; x++)
                {
                    var before = GetPixel(withoutHalo, x, y);
                    if (before.Red > 8 || before.Green > 8 || before.Blue > 8)
                    {
                        continue;
                    }
                    blackPixels++;
                    var after = GetPixel(withHalo, x, y);
                    if (after.Red > 16 || after.Green > 16 || after.Blue > 16)
                    {
                        coveredPixels++;
                    }
                }
            }
            Assert.IsGreaterThan(100, blackPixels);
            Assert.AreEqual(0, coveredPixels, "A neighboring halo covered a black glyph stroke.");
            Assert.IsNotEmpty(FindColor(withHalo, 255, 255, 255, minimumPixelCount: 10));
        });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task TextHaloPassesPreserveOverlappingLabelOrder(bool separateStyleLayers) =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            map.IsTextScaleFactorEnabled = false;
            TileId tileId = new(4, 8, 8);
            string CreateLayer(int? rank) =>
                $$$"""
                {
                  "type": "symbol",
                  "source-layer": "markers",
                  {{{(rank is int value ? $"\"filter\": [\"==\", [\"get\", \"rank\"], {value}]," : "")}}}
                  "layout": {
                    "symbol-sort-key": ["get", "rank"],
                    "text-field": ["get", "label"],
                    "text-font": ["TestFont"],
                    "text-size": 24,
                    "text-allow-overlap": true
                  },
                  "paint": {
                    "text-color": "#000000",
                    "text-halo-color": "#ffffff",
                    "text-halo-width": 3
                  }
                }
                """;
            string layers = separateStyleLayers
                ? $"{CreateLayer(0)},{CreateLayer(1)}"
                : CreateLayer(null);
            TestVectorTileSource CreateSource(bool lower, bool upper)
            {
                MapboxVectorTileBuilder builder = new();
                if (lower)
                {
                    builder.AddPoint("markers", 2048, 2048,
                        new Dictionary<string, object> { ["label"] = "ABBA", ["rank"] = 0 });
                }
                if (upper)
                {
                    builder.AddPoint("markers", 2128, 2048,
                        new Dictionary<string, object> { ["label"] = "ABBA", ["rank"] = 1 });
                }
                TestVectorTileSource source = TestVectorTileSource.Create(
                    tileId, builder.Build(),
                    $$$"""
                    {
                      "version": 8,
                      "layers": [
                        {"type": "background", "paint": {"background-color": "#0044cc"}},
                        {{{layers}}}
                      ]
                    }
                    """,
                    "{}", [0, 0, 0, 0], 1, 1);
                source.AddGlyphs("TestFont",
                    TestGlyph.RectangleSdf('A'), TestGlyph.RectangleSdf('B'));
                return source;
            }

            MapRenderFrame lowerOnly = await RenderAsync(map, CreateSource(true, false));
            TestVectorTileLayer layer = (TestVectorTileLayer)map.Layers[0];
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            layer.ReplaceSource(CreateSource(false, true));
            MapRenderFrame upperOnly = await map.CaptureRenderedFrameAsync(timeout.Token);
            layer.ReplaceSource(CreateSource(true, true));
            MapRenderFrame combined = await map.CaptureRenderedFrameAsync(timeout.Token);
            int overlapPixels = 0;
            for (int y = 0; y < combined.Height; y++)
            {
                for (int x = 0; x < combined.Width; x++)
                {
                    var lower = GetPixel(lowerOnly, x, y);
                    var upper = GetPixel(upperOnly, x, y);
                    if (lower.Red < 8 && lower.Green < 8 && lower.Blue < 8 &&
                        upper.Red > 247 && upper.Green > 247 && upper.Blue > 247)
                    {
                        overlapPixels++;
                        var actual = GetPixel(combined, x, y);
                        Assert.IsTrue(
                            actual.Red > 240 && actual.Green > 240 && actual.Blue > 240,
                            "A lower label's fill covered the upper label's halo.");
                    }
                }
            }
            Assert.IsGreaterThan(10, overlapPixels);
        });

    [TestMethod]
    public Task TextHaloPreservesTranslucentFillAndOpacity() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            map.IsTextScaleFactorEnabled = false;
            TileId tileId = new(4, 8, 8);
            byte[] tile = new MapboxVectorTileBuilder()
                .AddPoint("markers", 2048, 2048).Build();
            TestVectorTileSource source = TestVectorTileSource.Create(
                tileId, tile,
                """
                {
                  "version": 8,
                  "layers": [
                    {"type": "background", "paint": {"background-color": "#0044cc"}},
                    {
                      "type": "symbol",
                      "source-layer": "markers",
                      "layout": {
                        "text-field": "A",
                        "text-font": ["TestFont"],
                        "text-size": 48
                      },
                      "paint": {
                        "text-color": "#00000080",
                        "text-opacity": 0.5,
                        "text-halo-color": "#ffffff",
                        "text-halo-width": 6
                      }
                    }
                  ]
                }
                """,
                "{}", [0, 0, 0, 0], 1, 1);
            source.AddGlyphs("TestFont", TestGlyph.RectangleSdf('A'));
            MapRenderFrame frame = await RenderAsync(map, source);
            Assert.IsNotEmpty(FindColor(frame, 0, 51, 153, minimumPixelCount: 100, tolerance: 2));
            Assert.IsNotEmpty(FindColor(frame, 128, 162, 230, minimumPixelCount: 10, tolerance: 2));
        });

    [TestMethod]
    [DataRow("#00000000", 2)]
    [DataRow("#ffffff", 4)]
    public Task RepeatedGlyphTexturesBatchAcrossCollisionSafeLabels(
        string haloColor, int drawCallCount) =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            using RenderingEventListener listener =
                new("VectorLabelRenderBatch");
            TileId tileId = new(4, 8, 8);
            byte[] tile = new MapboxVectorTileBuilder()
                .AddPoint(
                    "markers",
                    1024,
                    1024,
                    new Dictionary<string, object> { ["label"] = "AB" })
                .AddPoint(
                    "markers",
                    3072,
                    1024,
                    new Dictionary<string, object> { ["label"] = "BA" })
                .AddPoint(
                    "markers",
                    1024,
                    3072,
                    new Dictionary<string, object> { ["label"] = "BA" })
                .AddPoint(
                    "markers",
                    3072,
                    3072,
                    new Dictionary<string, object> { ["label"] = "AB" })
                .Build();
            TestVectorTileSource source = TestVectorTileSource.Create(
                tileId,
                tile,
                $$"""
                {
                  "version": 8,
                  "layers": [{
                    "type": "symbol",
                    "source-layer": "markers",
                    "layout": {
                      "text-field": ["get", "label"],
                      "text-font": ["TestFont"],
                      "text-size": 18
                    },
                    "paint": {
                      "text-color": "#ff00ff",
                      "text-halo-color": "{{haloColor}}",
                      "text-halo-width": 2
                    }
                  }]
                }
                """,
                "{}",
                [0, 0, 0, 0],
                1,
                1);
            source.AddGlyphs(
                "TestFont",
                TestGlyph.Solid('A'),
                TestGlyph.Solid('B'));

            await RenderAsync(map, source);

            CapturedRenderingEvent batch = listener
                .Events("VectorLabelRenderBatch")
                .Last(captured =>
                    Convert.ToInt32(captured.Payload[2]) >= 8);
            Assert.AreEqual(2, Convert.ToInt32(batch.Payload[5]));
            Assert.AreEqual(drawCallCount, Convert.ToInt32(batch.Payload[6]));
        });

    [TestMethod]
    public Task LabelGlyphsFadeTogetherAfterTexturesBecomeReady() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            using RenderingEventListener listener =
                new("VectorLabelFadeSummary");
            TileId tileId = new(4, 8, 8);
            byte[] tile = new MapboxVectorTileBuilder()
                .AddPoint(
                    "markers",
                    2048,
                    2048,
                    new Dictionary<string, object> { ["label"] = "7" })
                .Build();
            TestVectorTileSource source = CreateShieldSource(
                tileId,
                tile,
                """
                {
                  "version": 8,
                  "layers": [{
                    "type": "symbol",
                    "source-layer": "markers",
                    "layout": {
                      "text-field": ["get", "label"],
                      "text-font": ["TestFont"],
                      "text-size": 18
                    },
                    "paint": {
                      "text-color": "#ff00ff"
                    }
                  }]
                }
                """,
                '7');

            BasicGeoposition center = source.TileCenter;
            map.MapStyle = MapStyle.Blank;
            map.IsTextScaleFactorEnabled = false;
            Assert.IsTrue(await map.TrySetViewAsync(
                new Geopoint(center),
                source.TileId.Zoom,
                null,
                null,
                MapAnimationKind.None));
            map.Layers.Add(new TestVectorTileLayer(
                source,
                TimeSpan.FromSeconds(1)));
            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(5));
            MapRenderFrame frame =
                await map.CaptureRenderedFrameAsync(timeout.Token);

            Assert.ContainsSingle(
                FindColor(frame, 255, 0, 255, minimumPixelCount: 20));
            CapturedRenderingEvent[] fadeEvents =
                listener.Events("VectorLabelFadeSummary");
            Assert.IsTrue(fadeEvents.Any(captured =>
                Convert.ToInt32(captured.Payload[1]) > 0 &&
                Convert.ToInt32(captured.Payload[2]) > 0));
            Assert.IsTrue(fadeEvents.Any(captured =>
                Convert.ToInt32(captured.Payload[1]) == 0 &&
                Convert.ToInt32(captured.Payload[2]) == 0));
        });

    [TestMethod]
    public Task TextScaleFactorUpdatesRenderedLabelAndHonorsOptOut() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            TileId tileId = new(4, 8, 8);
            byte[] tile = new MapboxVectorTileBuilder()
                .AddPoint("markers", 2048, 2048)
                .Build();
            TestVectorTileSource source = TestVectorTileSource.Create(
                tileId,
                tile,
                """
                {
                  "version": 8,
                  "layers": [{
                    "type": "symbol",
                    "source-layer": "markers",
                    "layout": {
                      "text-field": "7",
                      "text-font": ["TestFont"],
                      "text-size": 12
                    },
                    "paint": {
                      "text-color": "#ff00ff"
                    }
                  }]
                }
                """,
                "{}",
                [0, 0, 0, 0],
                1,
                1);
            source.AddGlyphs("TestFont", TestGlyph.Solid('7'));
            BasicGeoposition center = source.TileCenter;
            map.MapStyle = MapStyle.Blank;
            map.IsTextScaleFactorEnabled = true;
            map.ApplyTextScaleFactor(1);
            Assert.IsTrue(await map.TrySetViewAsync(
                new Geopoint(center),
                tileId.Zoom,
                null,
                null,
                MapAnimationKind.None));
            map.Layers.Add(new TestVectorTileLayer(source));

            ConnectedComponent normal = await CaptureTextComponentAsync(map);

            map.ApplyTextScaleFactor(2);
            ConnectedComponent scaled = await CaptureTextComponentAsync(
                map,
                component => component.Bounds.Width >= normal.Bounds.Width * 2 - 2);

            Assert.IsGreaterThanOrEqualTo(normal.Bounds.Width * 2 - 2, scaled.Bounds.Width);
            Assert.IsGreaterThanOrEqualTo(normal.Bounds.Height * 2 - 2, scaled.Bounds.Height);
            Assert.IsGreaterThan(normal.PixelCount * 3, scaled.PixelCount);

            map.IsTextScaleFactorEnabled = false;
            map.ApplyTextScaleFactor(2);
            ConnectedComponent disabled = await CaptureTextComponentAsync(
                map,
                component => Math.Abs(component.Bounds.Width - normal.Bounds.Width) <= 1);

            Assert.AreEqual(normal.Bounds.Width, disabled.Bounds.Width, 1);
            Assert.AreEqual(normal.Bounds.Height, disabled.Bounds.Height, 1);
            Assert.AreEqual(normal.PixelCount, disabled.PixelCount, 8);
        });

    private static async Task<ConnectedComponent> CaptureTextComponentAsync(
        MapControl map,
        Func<ConnectedComponent, bool>? expected = null)
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(5));
        while (true)
        {
            MapRenderFrame frame =
                await map.CaptureRenderedFrameAsync(timeout.Token);
            ConnectedComponent[] components = FindColor(
                frame,
                255,
                0,
                255,
                minimumPixelCount: 20);
            if (components.Length == 1 &&
                (expected is null || expected(components[0])))
            {
                return components[0];
            }
        }
    }

    private static TestVectorTileSource CreateShieldSource(
        TileId tileId,
        byte[] tile,
        string styleJson,
        params char[] glyphs)
    {
        byte[] shieldPixels = Enumerable.Range(0, 24 * 16)
            .SelectMany(_ => new byte[] { 0, 0, 255, 255 })
            .ToArray();
        TestVectorTileSource source = TestVectorTileSource.Create(
            tileId,
            tile,
            styleJson,
            """
            {
              "shield": {
                "x": 0, "y": 0, "width": 24, "height": 16,
                "pixelRatio": 1, "visible": true
              }
            }
            """,
            shieldPixels,
            24,
            16);
        source.AddGlyphs(
            "TestFont",
            [.. glyphs.Distinct().Select(character =>
                TestGlyph.Solid(character))]);
        return source;
    }

    private static async Task<MapRenderFrame> RenderAsync(
        MapControl map,
        TestVectorTileSource source,
        TimeSpan? fadeDuration = null)
    {
        BasicGeoposition center = source.TileCenter;
        map.MapStyle = MapStyle.Blank;
        Assert.IsTrue(await map.TrySetViewAsync(
            new Geopoint(center),
            source.TileId.Zoom,
            null,
            null,
            MapAnimationKind.None));
        map.Layers.Add(new TestVectorTileLayer(source, fadeDuration));

        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(5));
        return await map.CaptureRenderedFrameAsync(timeout.Token);
    }

    private static ConnectedComponent[] FindColor(
        MapRenderFrame frame,
        byte red,
        byte green,
        byte blue,
        int minimumPixelCount,
        byte tolerance = 8) =>
        ConnectedComponentAnalyzer.Find(
            frame,
            ConnectedComponentAnalyzer.Near(
                red,
                green,
                blue,
                tolerance,
                minimumAlpha: 240),
            minimumPixelCount);

    private static (byte Red, byte Green, byte Blue) GetPixel(
        MapRenderFrame frame,
        int x,
        int y)
    {
        int offset = ((y * frame.Width) + x) * 4;
        ReadOnlySpan<byte> pixels = frame.Pixels.Span;
        return (
            pixels[offset + 2],
            pixels[offset + 1],
            pixels[offset]);
    }

    private static PixelBounds Union(
        IReadOnlyCollection<ConnectedComponent> components)
    {
        Assert.IsNotEmpty(components);
        int left = components.Min(component => component.Bounds.Left);
        int top = components.Min(component => component.Bounds.Top);
        int right = components.Max(component => component.Bounds.Right);
        int bottom = components.Max(component => component.Bounds.Bottom);
        return new PixelBounds(left, top, right - left, bottom - top);
    }
}
