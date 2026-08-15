using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Geolocation;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests.UITests;

[TestClass]
[DoNotParallelize]
public sealed class RasterRenderingTests
{
    [TestMethod]
    public Task AdjacentTilesRenderInTheirExpectedViewportQuadrants() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            const int zoom = 3;
            Dictionary<TileId, TestRasterTile> tiles = new()
            {
                [new(zoom, 3, 3)] = TestRasterTileSource.Solid(256, 255, 0, 0),
                [new(zoom, 4, 3)] = TestRasterTileSource.Solid(256, 0, 255, 0),
                [new(zoom, 3, 4)] = TestRasterTileSource.Solid(256, 0, 0, 255),
                [new(zoom, 4, 4)] = TestRasterTileSource.Solid(256, 255, 255, 0),
            };
            TestRasterTileSource source = new(zoom, tiles);

            MapRenderFrame frame = await RenderAsync(
                map,
                source,
                new BasicGeoposition { Latitude = 0, Longitude = 0 });

            AssertQuadrant(frame, 255, 0, 0, left: 0, top: 0);
            AssertQuadrant(frame, 0, 255, 0, left: frame.Width / 2, top: 0);
            AssertQuadrant(frame, 0, 0, 255, left: 0, top: frame.Height / 2);
            AssertQuadrant(
                frame,
                255,
                255,
                0,
                left: frame.Width / 2,
                top: frame.Height / 2);
            Assert.IsTrue(tiles.Keys.All(id => source.GetRequestCount(id) == 1));
        });

    [TestMethod]
    [DataRow((int)RasterSourceKind.Custom)]
    [DataRow((int)RasterSourceKind.Azure)]
    public Task MalformedTileIsRejectedWhileValidNeighborStillRenders(
        int sourceKind) =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            const int zoom = 3;
            TileId invalidId = new(zoom, 3, 3);
            TileId validId = new(zoom, 4, 3);
            TestRasterTileSource source = new(
                zoom,
                new Dictionary<TileId, TestRasterTile>
                {
                    [invalidId] = new TestRasterTile([255, 0, 255, 255], 256, 256),
                    [validId] = TestRasterTileSource.Solid(256, 0, 255, 0),
                },
                (RasterSourceKind)sourceKind);

            MapRenderFrame frame = await RenderAsync(
                map,
                source,
                new BasicGeoposition { Latitude = 0, Longitude = 0 });

            Assert.AreEqual(
                0,
                FindColor(frame, 255, 0, 255, minimumPixelCount: 10).Length);
            ConnectedComponent valid = Assert.ContainsSingle(
                FindColor(frame, 0, 255, 0, minimumPixelCount: 10_000));
            Assert.AreEqual(frame.Width / 2, valid.Bounds.Left, 2);
            Assert.AreEqual(0, valid.Bounds.Top, 2);
            Assert.AreEqual(256, valid.Bounds.Width, 2);
            Assert.AreEqual(frame.Height / 2, valid.Bounds.Height, 2);
            Assert.IsTrue(source.GetRequestCount(invalidId) >= 1);
            Assert.AreEqual(1, source.GetRequestCount(validId));
        });

    [TestMethod]
    public Task RotatedRasterTilePreservesTextureOrientation() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            const int zoom = 5;
            TileId tileId = new(zoom, 16, 16);
            TestRasterTileSource source = new(
                zoom,
                new Dictionary<TileId, TestRasterTile>
                {
                    [tileId] = TestRasterTileSource.VerticalSplit(
                        256,
                        (224, 32, 32),
                        (32, 32, 224)),
                });
            map.MapStyle = MapStyle.Blank;
            map.Center = new Geopoint(source.GetTileCenter(tileId));
            map.ZoomLevel = zoom;
            map.Heading = 90;
            map.Layers.Add(new TestRasterTileLayer(source));

            MapRenderFrame frame = await CaptureAsync(map);

            ConnectedComponent red = Assert.ContainsSingle(
                FindColor(frame, 224, 32, 32, minimumPixelCount: 20_000));
            ConnectedComponent blue = Assert.ContainsSingle(
                FindColor(frame, 32, 32, 224, minimumPixelCount: 20_000));
            Assert.AreEqual(frame.Width / 2d, red.Bounds.CenterX, 2);
            Assert.AreEqual(frame.Width / 2d, blue.Bounds.CenterX, 2);
            Assert.AreEqual(256, red.Bounds.Width, 3);
            Assert.AreEqual(256, blue.Bounds.Width, 3);
            Assert.AreEqual(128, red.Bounds.Height, 3);
            Assert.AreEqual(128, blue.Bounds.Height, 3);
            Assert.AreNotEqual(red.Bounds.CenterY, blue.Bounds.CenterY);
        });

    [TestMethod]
    public Task NewLayerGenerationReplacesAnInFlightLargeTile() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            const int sourceZoom = 6;
            const int displayZoom = 5;
            Dictionary<TileId, TestRasterTile> firstTiles = [];
            Dictionary<TileId, TestRasterTile> replacementTiles = [];
            for (int y = 30; y <= 33; y++)
            {
                for (int x = 30; x <= 33; x++)
                {
                    TileId id = new(sourceZoom, x, y);
                    firstTiles.Add(
                        id,
                        TestRasterTileSource.Solid(1024, 255, 128, 0));
                    replacementTiles.Add(
                        id,
                        TestRasterTileSource.Solid(1024, 128, 0, 255));
                }
            }
            TestRasterTileSource firstSource = new(
                sourceZoom,
                firstTiles);
            TestRasterTileSource replacementSource = new(
                sourceZoom,
                replacementTiles);
            TestRasterTileLayer layer = ConfigureMap(
                map,
                firstSource,
                new BasicGeoposition { Latitude = 0, Longitude = 0 },
                displayZoom);

            await WaitForAsync(() => firstSource.TotalRequestCount >= 8);
            layer.ReplaceSource(replacementSource);

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));
            MapRenderFrame frame =
                await map.CaptureRenderedFrameAsync(timeout.Token);

            ConnectedComponent tile = Assert.ContainsSingle(
                FindColor(frame, 128, 0, 255, minimumPixelCount: 100_000));
            Assert.AreEqual(frame.Width / 2d, tile.Bounds.CenterX, 2);
            Assert.AreEqual(frame.Height / 2d, tile.Bounds.CenterY, 2);
            Assert.IsTrue(firstSource.TotalRequestCount >= 8);
            Assert.IsTrue(replacementSource.TotalRequestCount >= 8);
            Assert.AreEqual(
                0,
                FindColor(frame, 255, 128, 0, minimumPixelCount: 10).Length);
        });

    [TestMethod]
    public Task CachePressureEvictsLeastRecentlyUsedOffscreenTile() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            const int zoom = 5;
            TileId firstId = new(zoom, 4, 16);
            TileId secondId = new(zoom, 12, 16);
            TileId thirdId = new(zoom, 20, 16);
            TestRasterTileSource source = new(
                zoom,
                new Dictionary<TileId, TestRasterTile>
                {
                    [firstId] = TestRasterTileSource.Solid(2048, 192, 32, 32),
                    [secondId] = TestRasterTileSource.Solid(2048, 32, 192, 32),
                    [thirdId] = TestRasterTileSource.Solid(2048, 32, 32, 192),
                });
            _ = ConfigureMap(map, source, source.GetTileCenter(firstId));

            MapRenderFrame first = await CaptureAsync(map);
            AssertViewportTile(first, 192, 32, 32);

            map.Center = new Geopoint(source.GetTileCenter(secondId));
            MapRenderFrame second = await CaptureUntilColorAsync(
                map,
                32,
                192,
                32);
            AssertViewportTile(second, 32, 192, 32);

            map.Center = new Geopoint(source.GetTileCenter(thirdId));
            MapRenderFrame third = await CaptureUntilColorAsync(
                map,
                32,
                32,
                192);
            AssertViewportTile(third, 32, 32, 192);

            map.Center = new Geopoint(source.GetTileCenter(firstId));
            await WaitForAsync(() => source.GetRequestCount(firstId) >= 2);
            MapRenderFrame reloaded = await CaptureUntilColorAsync(
                map,
                192,
                32,
                32);
            AssertViewportTile(reloaded, 192, 32, 32);
            Assert.AreEqual(2, source.GetRequestCount(firstId));
            Assert.AreEqual(1, source.GetRequestCount(secondId));
            Assert.AreEqual(1, source.GetRequestCount(thirdId));
        });

    [TestMethod]
    public Task HybridTileCommitsRasterBackgroundWithEmptyVectorPayload() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            const int zoom = 5;
            TileId tileId = new(zoom, 16, 16);
            TestRasterTileSource source = new(
                zoom,
                new Dictionary<TileId, TestRasterTile>
                {
                    [tileId] = TestRasterTileSource.Solid(256, 0, 192, 192),
                });
            map.MapStyle = MapStyle.Blank;
            map.Center = new Geopoint(source.GetTileCenter(tileId));
            map.ZoomLevel = zoom;
            map.Layers.Add(new TestHybridRasterTileLayer(source));

            MapRenderFrame frame = await CaptureAsync(map);

            ConnectedComponent background = Assert.ContainsSingle(
                FindColor(frame, 0, 192, 192, minimumPixelCount: 40_000));
            Assert.AreEqual(frame.Width / 2d, background.Bounds.CenterX, 2);
            Assert.AreEqual(frame.Height / 2d, background.Bounds.CenterY, 2);
            Assert.AreEqual(256, background.Bounds.Width, 2);
            Assert.AreEqual(256, background.Bounds.Height, 2);
            Assert.AreEqual(1, source.GetRequestCount(tileId));
        });

    [TestMethod]
    public Task RasterCoverageReportsFirstFullAndOpaqueMilestones() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            using RenderingEventListener listener =
                new("RasterCoverageMilestone");
            const int zoom = 5;
            TileId tileId = new(zoom, 16, 16);
            TestRasterTileSource source = new(
                zoom,
                new Dictionary<TileId, TestRasterTile>
                {
                    [tileId] = TestRasterTileSource.Solid(256, 160, 64, 192),
                });

            MapRenderFrame frame = await RenderAsync(
                map,
                source,
                source.GetTileCenter(tileId));

            AssertViewportTile(frame, 160, 64, 192);
            string[] milestones =
            [
                .. listener.Events("RasterCoverageMilestone")
                    .Select(captured => (string)captured.Payload[3]!),
            ];
            Assert.Contains("FirstTile", milestones);
            Assert.Contains("FullCoverage", milestones);
            Assert.Contains("OpaqueCoverage", milestones);
        });

    [TestMethod]
    public Task HybridVectorCacheEvictsLeastRecentlyUsedOffscreenTile() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            const int zoom = 5;
            const int vectorBytesPerTile = 18 * 1024 * 1024;
            TileId firstId = new(zoom, 4, 16);
            TileId secondId = new(zoom, 12, 16);
            TileId thirdId = new(zoom, 20, 16);
            TestRasterTileSource source = new(
                zoom,
                new Dictionary<TileId, TestRasterTile>
                {
                    [firstId] = TestRasterTileSource.Solid(256, 176, 48, 48),
                    [secondId] = TestRasterTileSource.Solid(256, 48, 176, 48),
                    [thirdId] = TestRasterTileSource.Solid(256, 48, 48, 176),
                },
                hybridVectorByteSize: vectorBytesPerTile);
            map.MapStyle = MapStyle.Blank;
            map.Center = new Geopoint(source.GetTileCenter(firstId));
            map.ZoomLevel = zoom;
            map.Layers.Add(new TestHybridRasterTileLayer(source));

            AssertViewportTile(await CaptureAsync(map), 176, 48, 48);

            map.Center = new Geopoint(source.GetTileCenter(secondId));
            AssertViewportTile(
                await CaptureUntilColorAsync(map, 48, 176, 48),
                48,
                176,
                48);

            map.Center = new Geopoint(source.GetTileCenter(thirdId));
            AssertViewportTile(
                await CaptureUntilColorAsync(map, 48, 48, 176),
                48,
                48,
                176);

            map.Center = new Geopoint(source.GetTileCenter(firstId));
            await WaitForAsync(() => source.GetRequestCount(firstId) >= 2);
            AssertViewportTile(
                await CaptureUntilColorAsync(map, 176, 48, 48),
                176,
                48,
                48);
            Assert.AreEqual(2, source.GetRequestCount(firstId));
        });

    [TestMethod]
    public Task HybridQueueAtomicallyReplacesPreviouslyCommittedTile() =>
        MapControlTestHost.LoadUIAsync(
            () => new SwapChainPanel { Width = 640, Height = 480 },
            async element =>
            {
                var panel = (SwapChainPanel)element;
                using MapRenderer renderer = new();
                const long sourceId = 42;
                const long generation = 7;
                const int zoom = 5;
                TileId tileId = new(zoom, 16, 16);
                RasterTileKey key = new(sourceId, tileId);
                TestRasterTileSource source = new(
                    zoom,
                    new Dictionary<TileId, TestRasterTile>
                    {
                        [tileId] = TestRasterTileSource.Solid(256, 0, 0, 0),
                    });
                BasicGeoposition center = source.GetTileCenter(tileId);
                MapScene scene = MapCamera.CreateScene(
                    center.Longitude,
                    center.Latitude,
                    zoom,
                    zoom,
                    640,
                    480,
                    0,
                    0);
                renderer.Attach(panel);
                renderer.SetCameraTargetImmediately(
                    center.Longitude,
                    center.Latitude,
                    zoom,
                    640,
                    480);
                renderer.SetLayerRenderPlan(
                    [
                        new LayerRenderSnapshot(
                            LayerRenderKind.HybridTiles,
                            0,
                            sourceId,
                            true,
                            1,
                            TimeSpan.Zero,
                            0,
                            24,
                            zoom,
                            256),
                    ]);
                renderer.ActivateRasterTileSet(
                    sourceId,
                    generation,
                    1,
                    scene,
                    id => id == tileId,
                    RasterSourceKind.Custom,
                    LayerRenderKind.HybridTiles,
                    clearExistingTiles: false);

                Assert.IsTrue(await renderer.QueueHybridTileAsync(
                    CreateHybridTileData(
                        source,
                        key,
                        generation,
                        TestRasterTileSource.Solid(256, 208, 48, 48)),
                    CancellationToken.None));
                using CancellationTokenSource timeout =
                    new(TimeSpan.FromSeconds(10));
                MapRenderFrame first =
                    await renderer.CaptureFrameAsync(timeout.Token);
                AssertViewportTile(first, 208, 48, 48);

                Assert.IsTrue(await renderer.QueueHybridTileAsync(
                    CreateHybridTileData(
                        source,
                        key,
                        generation,
                        TestRasterTileSource.Solid(256, 48, 48, 208)),
                    CancellationToken.None));
                MapRenderFrame replacement =
                    await renderer.CaptureFrameAsync(timeout.Token);

                AssertViewportTile(replacement, 48, 48, 208);
                Assert.AreEqual(
                    0,
                    FindColor(replacement, 208, 48, 48, 10).Length);
            });

    private static async Task<MapRenderFrame> RenderAsync(
        MapControl map,
        TestRasterTileSource source,
        BasicGeoposition center)
    {
        _ = ConfigureMap(map, source, center);
        return await CaptureAsync(map);
    }

    private static TestRasterTileLayer ConfigureMap(
        MapControl map,
        TestRasterTileSource source,
        BasicGeoposition center,
        double? displayZoom = null)
    {
        TestRasterTileLayer layer = new(source);
        map.MapStyle = MapStyle.Blank;
        map.Center = new Geopoint(center);
        map.ZoomLevel = displayZoom ?? source.Zoom;
        map.Layers.Add(layer);
        return layer;
    }

    private static async Task<MapRenderFrame> CaptureAsync(MapControl map)
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(10));
        return await map.CaptureRenderedFrameAsync(timeout.Token);
    }

    private static async Task<MapRenderFrame> CaptureUntilColorAsync(
        MapControl map,
        byte red,
        byte green,
        byte blue)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        do
        {
            MapRenderFrame frame = await CaptureAsync(map);
            if (FindColor(frame, red, green, blue, minimumPixelCount: 40_000).Length == 1)
            {
                return frame;
            }

            await Task.Delay(20);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail("The expected raster color was not rendered before the timeout.");
        return default;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("The expected raster request was not observed before the timeout.");
    }

    private static VectorTileData CreateHybridTileData(
        TestRasterTileSource source,
        RasterTileKey key,
        long generation,
        TestRasterTile raster) =>
        new(
            key,
            source.HybridFeatures,
            source.StyleAssets,
            [],
            new RasterTileData(
                key,
                raster.Pixels,
                raster.Width,
                raster.Height,
                generation,
                source.SourceKind),
            generation,
            0);

    private static void AssertQuadrant(
        MapRenderFrame frame,
        byte red,
        byte green,
        byte blue,
        int left,
        int top)
    {
        ConnectedComponent component = Assert.ContainsSingle(
            FindColor(frame, red, green, blue, minimumPixelCount: 10_000));
        Assert.AreEqual(left + (left == 0 ? 64 : 0), component.Bounds.Left, 2);
        Assert.AreEqual(top, component.Bounds.Top, 2);
        Assert.AreEqual(256, component.Bounds.Width, 2);
        Assert.AreEqual(frame.Height / 2, component.Bounds.Height, 2);
    }

    private static void AssertViewportTile(
        MapRenderFrame frame,
        byte red,
        byte green,
        byte blue)
    {
        ConnectedComponent component = Assert.ContainsSingle(
            FindColor(frame, red, green, blue, minimumPixelCount: 40_000));
        Assert.AreEqual(frame.Width / 2d, component.Bounds.CenterX, 2);
        Assert.AreEqual(frame.Height / 2d, component.Bounds.CenterY, 2);
        Assert.AreEqual(256, component.Bounds.Width, 2);
        Assert.AreEqual(256, component.Bounds.Height, 2);
    }

    private static ConnectedComponent[] FindColor(
        MapRenderFrame frame,
        byte red,
        byte green,
        byte blue,
        int minimumPixelCount) =>
        ConnectedComponentAnalyzer.Find(
            frame,
            ConnectedComponentAnalyzer.Near(
                red,
                green,
                blue,
                tolerance: 4,
                minimumAlpha: 250),
            minimumPixelCount);
}
