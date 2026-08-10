using Microsoft.VisualStudio.TestTools.UnitTesting;

using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class RasterTileManagerTests
{
    [TestMethod]
    public void PanAtSameRequestContextDoesNotCancelActiveLoads()
    {
        Assert.IsFalse(RasterTileManager.ShouldCancelActiveRequest(
            "source",
            12,
            "source",
            12));
    }

    [TestMethod]
    [DataRow("replacement", 12)]
    [DataRow("source", 13)]
    public void ChangedRequestContextCancelsActiveLoads(
        string sourceKey,
        int tileZoom)
    {
        Assert.IsTrue(RasterTileManager.ShouldCancelActiveRequest(
            "source",
            12,
            sourceKey,
            tileZoom));
    }

    [TestMethod]
    [DataRow(MapStyle.Blank, "", false)]
    [DataRow(MapStyle.Blank, null, false)]
    [DataRow(MapStyle.Road, "", false)]
    [DataRow(MapStyle.Road, "token", true)]
    public void HiddenAzureLifecycleIsBlankAndTokenSafe(
        MapStyle style,
        string? token,
        bool expected)
    {
        bool hasLayer = MapControl.HasAzureBaseLayer(style);
        bool canAcquire = hasLayer &&
            new AzureTileAcquisitionSession(style, token ?? string.Empty).CanAcquire;

        Assert.AreEqual(style != MapStyle.Blank, hasLayer);
        Assert.AreEqual(expected, canAcquire);
    }

    [TestMethod]
    [DataRow(401, true)]
    [DataRow(403, true)]
    [DataRow(400, false)]
    [DataRow(404, false)]
    [DataRow(429, false)]
    public void AuthenticationFailuresAreLimitedToUnauthorizedResponses(
        int statusCode,
        bool expected)
    {
        Assert.AreEqual(
            expected,
            RasterTileManager.IsAzureAuthenticationFailure(statusCode));
    }

    [TestMethod]
    public async Task ContinuousSchedulerRefillsSlotsBeforeSlowRequestCompletes()
    {
        TaskCompletionSource releaseSlowRequest = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource fourthRequestStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        List<int> started = [];
        object sync = new();

        Task<ContinuousWorkResult> run = RasterTileManager.RunContinuouslyAsync(
            Enumerable.Range(0, 4).ToArray(),
            2,
            static () => true,
            async item =>
            {
                lock (sync)
                {
                    started.Add(item);
                    if (started.Count == 4)
                    {
                        fourthRequestStarted.TrySetResult();
                    }
                }
                if (item == 0)
                {
                    await releaseSlowRequest.Task;
                }
            });

        await fourthRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(run.IsCompleted);
        releaseSlowRequest.SetResult();
        ContinuousWorkResult result = await run;

        Assert.AreSequenceEqual([0, 1, 2, 3], started.Order());
        Assert.AreEqual(4, result.StartedCount);
        Assert.AreEqual(4, result.CompletedCount);
        Assert.AreEqual(2, result.MaximumConcurrency);
        Assert.AreEqual(0, result.DeferredCount);
    }

    [TestMethod]
    public async Task ContinuousSchedulerStopsFeedingSupersededScene()
    {
        TaskCompletionSource twoRequestsStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseRequests = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int startedCount = 0;
        bool canStart = true;

        Task<ContinuousWorkResult> run = RasterTileManager.RunContinuouslyAsync(
            Enumerable.Range(0, 6).ToArray(),
            2,
            () => Volatile.Read(ref canStart),
            async _ =>
            {
                if (Interlocked.Increment(ref startedCount) == 2)
                {
                    twoRequestsStarted.SetResult();
                }
                await releaseRequests.Task;
            });

        await twoRequestsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Volatile.Write(ref canStart, false);
        releaseRequests.SetResult();
        ContinuousWorkResult result = await run;

        Assert.AreEqual(2, result.StartedCount);
        Assert.AreEqual(2, result.CompletedCount);
        Assert.AreEqual(2, result.MaximumConcurrency);
        Assert.AreEqual(4, result.DeferredCount);
    }

    [TestMethod]
    public void SchedulerRequestsOnlyTheActiveSourceLevel()
    {
        MapScene activeSourceScene = MapCamera.CreateScene(
            -122.33,
            47.61,
            12,
            12,
            1200,
            800);
        TileId[] alreadyCachedFallbacks =
        [
            new(8, 40, 87),
            new(10, 164, 357),
        ];

        IReadOnlyList<TileId> requested = RasterTileManager.GetActiveRequestTiles(
            activeSourceScene,
            static _ => true);

        Assert.IsNotEmpty(requested);
        foreach (var id in requested)
        {
            Assert.AreEqual(activeSourceScene.TileZoom, id.Zoom);
        }
        Assert.IsEmpty(requested.Intersect(alreadyCachedFallbacks));
    }

    [TestMethod]
    [DataRow(-1, 0, 22, null)]
    [DataRow(12, 0, 22, 12)]
    [DataRow(23, 0, 22, 22)]
    public void SourceZoomClampsAboveMaximumAndRejectsBelowMinimum(
        int requested,
        int minimum,
        int maximum,
        int? expected)
    {
        Assert.AreEqual(
            expected,
            RasterTileManager.NormalizeSourceZoom(requested, minimum, maximum));
    }

    [TestMethod]
    [DataRow(4, 4, 9, 9, true)]
    [DataRow(5, 4, 9, 9, false)]
    [DataRow(4, 4, 10, 9, false)]
    public void AttemptRecordingRequiresOriginalGenerationAndScene(
        long currentGeneration,
        long workGeneration,
        long attemptedSceneVersion,
        long workSceneVersion,
        bool expected)
    {
        Assert.AreEqual(
            expected,
            RasterTileManager.CanRecordAttempt(
                currentGeneration,
                workGeneration,
                attemptedSceneVersion,
                workSceneVersion));
    }

    [TestMethod]
    [DataRow(4, 2, 3, 5, 5, 6, true)]
    [DataRow(5, 5, 6, 4, 2, 3, true)]
    [DataRow(4, 2, 3, 5, 6, 6, false)]
    [DataRow(5, 31, 6, 4, 0, 3, false)]
    public void TileOverlapDetectsOnlySharedWorldCoverage(
        int firstZoom,
        int firstX,
        int firstY,
        int secondZoom,
        int secondX,
        int secondY,
        bool expected)
    {
        Assert.AreEqual(
            expected,
            MapRenderer.TilesOverlap(
                new TileId(firstZoom, firstX, firstY),
                new TileId(secondZoom, secondX, secondY)));
    }

    [TestMethod]
    public void FallbackRetainsCachedLevelsOnBothSidesOfActiveZoom()
    {
        Assert.AreSequenceEqual(
            [2, 4, 10, 11, 13, 16, 18],
            MapRenderer.SelectFallbackTileZooms(
                [18, 2, 10, 11, 13, 4, 16, 10],
                12));
    }

    [TestMethod]
    public void FallbackHasNoLevelsWhenNothingWasLoaded()
    {
        Assert.IsEmpty(MapRenderer.SelectFallbackTileZooms([], 12));
    }

    [TestMethod]
    public void FallbackLevelSetIsValidDistinctAndBounded()
    {
        IReadOnlyList<int> selected = MapRenderer.SelectFallbackTileZooms(
            Enumerable.Range(-20, 80),
            12);

        Assert.AreEqual(MapRenderer.MaximumFallbackTileLevels, selected.Count);
        Assert.DoesNotContain(12, selected);
        Assert.AreSequenceEqual(selected.Order(), selected);
        foreach (var zoom in selected)
        {
            Assert.IsInRange(0, MapCamera.MaximumTileZoom, zoom);
        }
        Assert.AreEqual(selected.Count, selected.Distinct().Count());
    }

    [TestMethod]
    public void RapidZoomReversalsRetainLoadedLevelsWithoutGrowth()
    {
        int[] reversal = [18, 2, 16, 4];
        IReadOnlyList<int> fallbacks = [];

        for (int index = 1; index < reversal.Length; index++)
        {
            fallbacks = MapRenderer.SelectFallbackTileZooms(
                fallbacks.Append(reversal[index - 1]),
                reversal[index]);
            Assert.IsInRange(1, MapRenderer.MaximumFallbackTileLevels, fallbacks.Count);
            Assert.DoesNotContain(reversal[index], fallbacks);
        }

        Assert.AreSequenceEqual([2, 16, 18], fallbacks);
    }

    [TestMethod]
    [DataRow(0UL, 32UL)]
    [DataRow(15UL, 32UL)]
    [DataRow(32UL, 48UL)]
    [DataRow(112UL, 128UL)]
    [DataRow(160UL, 160UL)]
    [DataRow(ulong.MaxValue, ulong.MaxValue)]
    public void RasterCacheBudgetTracksProtectedViewportBytes(
        ulong protectedMegabytes,
        ulong expectedMegabytes)
    {
        const ulong megabyte = 1024 * 1024;
        ulong protectedBytes = protectedMegabytes == ulong.MaxValue
            ? ulong.MaxValue
            : protectedMegabytes * megabyte;
        ulong expectedBytes = expectedMegabytes == ulong.MaxValue
            ? ulong.MaxValue
            : expectedMegabytes * megabyte;

        Assert.AreEqual(
            expectedBytes,
            MapRenderer.ComputeRasterCacheBudget(protectedBytes));
    }

    [TestMethod]
    [DataRow(4, 7, true)]
    [DataRow(4, 8, false)]
    [DataRow(4.99, 7, true)]
    [DataRow(4.99, 8, false)]
    [DataRow(4, 18, false)]
    public void SceneEnumerationRejectsStaleMuchFinerZoom(
        double displayZoom,
        int tileZoom,
        bool expected)
    {
        Assert.AreEqual(
            expected,
            MapRenderer.CanEnumerateRasterScene(displayZoom, tileZoom));
    }

    [TestMethod]
    public void CachedFinerTileProjectsToDisplayZoomWithoutCreatingFineScene()
    {
        VisibleTile visible = Assert.ContainsSingle(
            MapRenderer.GetVisibleCachedTileInstances(
                new TileId(8, 128, 128),
                0,
                0,
                4,
                512,
                512));

        Assert.AreEqual(Math.Round((double)(16), 10), Math.Round((double)(visible.Size), 10));
        Assert.AreEqual(Math.Round((double)(256), 10), Math.Round((double)(visible.Left), 10));
        Assert.AreEqual(Math.Round((double)(256), 10), Math.Round((double)(visible.Top), 10));
        Assert.AreEqual(128, visible.WorldX);
    }

    [TestMethod]
    public void CachedFinerTileVisibilityFiltersEntriesOutsideViewport()
    {
        Assert.IsEmpty(MapRenderer.GetVisibleCachedTileInstances(
            new TileId(8, 0, 0),
            0,
            0,
            4,
            512,
            512));
    }

    [TestMethod]
    public void CachedTileProjectionIncludesWrappedWorldCopy()
    {
        VisibleTile visible = Assert.ContainsSingle(
            MapRenderer.GetVisibleCachedTileInstances(
                new TileId(2, 0, 2),
                179,
                0,
                2,
                512,
                512));

        Assert.IsInRange(250, 260, visible.Left);
        Assert.AreEqual(4, visible.WorldX);
    }

    [TestMethod]
    public void CachedFallbackUsesSameRelaxedVerticalClampAsActiveScene()
    {
        const double latitude = 64.9;
        const double displayZoom = 0;
        const double viewportWidth = 640;
        const double viewportHeight = 480;
        MapScene scene = MapCamera.CreateScene(
            0,
            latitude,
            displayZoom,
            viewportWidth,
            viewportHeight);
        VisibleTile activeTile = Assert.ContainsSingle(
            scene.VisibleTiles.Where(tile =>
                tile.Id == new TileId(0, 0, 0) &&
                tile.WorldX == 0));
        VisibleTile fallbackTile = Assert.ContainsSingle(
            MapRenderer.GetVisibleCachedTileInstances(
                new TileId(0, 0, 0),
                0,
                latitude,
                displayZoom,
                viewportWidth,
                viewportHeight).Where(tile => tile.WorldX == 0));

        Assert.AreEqual(activeTile.Left, fallbackTile.Left, 0.000000001);
        Assert.AreEqual(activeTile.Top, fallbackTile.Top, 0.000000001);
        Assert.AreEqual(activeTile.Size, fallbackTile.Size, 0.000000001);
    }
}
