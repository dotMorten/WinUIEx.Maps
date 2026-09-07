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
    [DataRow(MapStyle.RoadRaster, "", false)]
    [DataRow(MapStyle.RoadRaster, "token", true)]
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

        RasterTileManager.LatestWorkScheduler<int> scheduler = new(2);
        Task<ContinuousWorkResult> run = scheduler.Publish(
            Enumerable.Range(0, 4).ToArray(),
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

        try
        {
            await fourthRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(run.IsCompleted);
        }
        finally
        {
            releaseSlowRequest.TrySetResult();
            await scheduler.CompleteAsync();
        }
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

        RasterTileManager.LatestWorkScheduler<int> scheduler = new(2);
        Task<ContinuousWorkResult> run = scheduler.Publish(
            Enumerable.Range(0, 6).ToArray(),
            () => Volatile.Read(ref canStart),
            async _ =>
            {
                if (Interlocked.Increment(ref startedCount) == 2)
                {
                    twoRequestsStarted.SetResult();
                }
                await releaseRequests.Task;
            });

        try
        {
            await twoRequestsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref canStart, false);
        }
        finally
        {
            releaseRequests.TrySetResult();
            await scheduler.CompleteAsync();
        }
        ContinuousWorkResult result = await run;

        Assert.AreEqual(2, result.StartedCount);
        Assert.AreEqual(2, result.CompletedCount);
        Assert.AreEqual(2, result.MaximumConcurrency);
        Assert.AreEqual(4, result.DeferredCount);
    }

    [TestMethod]
    public async Task LatestSceneRefillsBeforeHeldOldRequestAndPreservesSharedRequest()
    {
        RasterTileManager.LatestWorkScheduler<int> scheduler = new(2);
        TaskCompletionSource oldStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSlow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseOther = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource latestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<int> started = [];
        object sync = new();
        int active = 0;
        int peak = 0;

        async Task ProcessAsync(int item)
        {
            lock (sync)
            {
                started.Add(item);
                peak = Math.Max(peak, ++active);
                if (started.Count == 2)
                {
                    oldStarted.TrySetResult();
                }
                if (item == 22)
                {
                    latestStarted.TrySetResult();
                }
            }
            try
            {
                if (item == 0)
                {
                    await releaseSlow.Task;
                }
                else if (item == 1)
                {
                    await releaseOther.Task;
                }
            }
            finally
            {
                lock (sync)
                {
                    active--;
                }
            }
        }

        Task<ContinuousWorkResult> old = scheduler.Publish(
            [0, 1, 2, 3], static () => true, ProcessAsync);
        try
        {
            await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<ContinuousWorkResult> intermediate = scheduler.Publish(
                [10, 11], static () => true, ProcessAsync);
            Task<ContinuousWorkResult> latest = scheduler.Publish(
                [0, 20, 21, 22], static () => true, ProcessAsync);
            releaseOther.TrySetResult();

            await latestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ContinuousWorkResult latestResult = await latest.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(old.IsCompleted, "The old request must still be held.");
            Assert.AreEqual(0, (await intermediate).StartedCount);
            Assert.AreEqual(3, latestResult.CompletedCount);
            Assert.AreSequenceEqual([20, 21, 22], started.Where(item => item >= 10));
            Assert.AreEqual(1, started.Count(item => item == 0));
            Assert.DoesNotContain(2, started);
            Assert.DoesNotContain(3, started);
            Assert.AreEqual(2, peak);

            Task shutdown = scheduler.CompleteAsync();
            Assert.IsFalse(shutdown.IsCompleted, "Shutdown must join retained old requests.");
            releaseSlow.TrySetResult();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(2, (await old).CompletedCount);
        }
        finally
        {
            releaseSlow.TrySetResult();
            releaseOther.TrySetResult();
            await scheduler.CompleteAsync();
        }
    }

    [TestMethod]
    public async Task LatestScenePublicationsKeepEightTotalWorkers()
    {
        RasterTileManager.LatestWorkScheduler<int> scheduler = new(8);
        TaskCompletionSource full = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<Task<ContinuousWorkResult>> publications = [];
        int active = 0;
        int started = 0;
        int overLimit = 0;

        async Task ProcessAsync(int item)
        {
            int count = Interlocked.Increment(ref active);
            if (count > 8)
            {
                Interlocked.Increment(ref overLimit);
            }
            if (Interlocked.Increment(ref started) == 8)
            {
                full.TrySetResult();
            }
            try
            {
                await release.Task;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        publications.Add(scheduler.Publish(
            Enumerable.Range(0, 32).ToArray(), static () => true, ProcessAsync));
        try
        {
            await full.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (int scene = 1; scene <= 20; scene++)
            {
                publications.Add(scheduler.Publish(
                    Enumerable.Range(scene * 100, 32).ToArray(),
                    static () => true,
                    ProcessAsync));
            }
            Assert.AreEqual(8, Volatile.Read(ref started));
            release.TrySetResult();
            await Task.WhenAll(publications).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(40, started, "Only eight old and 32 latest items may start.");
            Assert.AreEqual(0, overLimit);
            Assert.AreEqual(0, active);
        }
        finally
        {
            release.TrySetResult();
            await scheduler.CompleteAsync();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LatestSchedulerReleasesFailedOrCanceledOwnership(bool canceled)
    {
        RasterTileManager.LatestWorkScheduler<int> scheduler = new(1);
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        InvalidOperationException failure = new("Synthetic acquisition failure");
        Task<ContinuousWorkResult> failed = scheduler.Publish(
            [1], static () => true, async _ =>
            {
                started.TrySetResult();
                await release.Task;
                if (canceled)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                throw failure;
            });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<ContinuousWorkResult> latest = scheduler.Publish(
                [2], static () => true, _ => Task.CompletedTask);
            cancellation.Cancel();
            release.TrySetResult();
            if (canceled)
            {
                await Assert.ThrowsAsync<OperationCanceledException>(
                    () => failed.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            else
            {
                Exception observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                    () => failed.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.AreSame(failure, observed);
            }
            Assert.AreEqual(1, (await latest.WaitAsync(TimeSpan.FromSeconds(5))).CompletedCount);
            Task<ContinuousWorkResult> retry = scheduler.Publish(
                [1], static () => true, _ => Task.CompletedTask);
            Assert.AreEqual(1, (await retry.WaitAsync(TimeSpan.FromSeconds(5))).CompletedCount);
        }
        finally
        {
            release.TrySetResult();
            await scheduler.CompleteAsync();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ManagerStartsLatestSceneWhileOldRequestsAreHeld(bool changeZoom)
    {
        MapScene oldScene = MapCamera.CreateScene(0, 0, 6, 6, 1024, 1024);
        int latestZoom = changeZoom ? 7 : 6;
        MapScene latestScene = MapCamera.CreateScene(90, 0, latestZoom, latestZoom, 1024, 1024);
        TileId[] oldTiles = oldScene.RequiredTiles.Take(8).ToArray();
        TileId[] latestTiles = latestScene.RequiredTiles.Except(oldTiles).Take(3).ToArray();
        Assert.HasCount(8, oldTiles);
        Assert.HasCount(3, latestTiles);
        HeldTileAcquisition acquisition = new(oldTiles, latestTiles);
        using MapRenderer renderer = new();
        using RasterTileManager manager = new(renderer);
        manager.SetLayers([new TileLayerSnapshot(
            42, 1, acquisition, 0, 24, true, 1, TimeSpan.Zero)]);
        manager.UpdateScene(oldScene);
        manager.Resume();
        try
        {
            await acquisition.OldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            manager.UpdateScene(latestScene);
            acquisition.ReleaseOther.TrySetResult();
            await acquisition.LatestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(changeZoom, acquisition.HeldToken.IsCancellationRequested);
            Assert.IsFalse(acquisition.ReleaseHeld.Task.IsCompleted);
            Assert.AreEqual(0, acquisition.OverLimit);
            if (!changeZoom)
            {
                Assert.AreEqual(1, acquisition.HeldStarts);
            }
        }
        finally
        {
            acquisition.ReleaseHeld.TrySetResult();
            acquisition.ReleaseOther.TrySetResult();
        }
    }

    [TestMethod]
    public async Task SkippedEligibilityCheckCanQueueWorkAgain()
    {
        MapScene scene = MapCamera.CreateScene(0, 0, 6, 256, 256);
        EligibilityGateAcquisition acquisition = new(scene.RequiredTiles[0]);
        using MapRenderer renderer = new();
        using RasterTileManager manager = new(renderer);
        manager.SetLayers([new TileLayerSnapshot(
            42, 1, acquisition, 0, 24, true, 1, TimeSpan.Zero)]);
        manager.UpdateScene(scene);
        acquisition.Arm();
        manager.Resume();
        await acquisition.Skipped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        acquisition.Enable();
        manager.UpdateScene(scene);
        await acquisition.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class EligibilityGateAcquisition(TileId tile) : RasterTileAcquisitionSession
    {
        private int _armed;
        private int _checks;
        private int _enabled;
        internal TaskCompletionSource Skipped { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Arm() => Volatile.Write(ref _armed, 1);
        internal void Enable() => Volatile.Write(ref _enabled, 1);
        internal override object SourceKey => this;
        internal override RasterSourceKind SourceKind => RasterSourceKind.Custom;
        internal override int TileSize => 256;
        internal override int MinSourceZoom => tile.Zoom;
        internal override int MaxSourceZoom => tile.Zoom;
        internal override bool CanAcquire
        {
            get
            {
                if (Volatile.Read(ref _enabled) != 0 ||
                    Volatile.Read(ref _armed) == 0 ||
                    Interlocked.Increment(ref _checks) == 1)
                {
                    return true;
                }
                Skipped.TrySetResult();
                return false;
            }
        }
        internal override int GetSourceZoom(MapScene scene) => tile.Zoom;
        internal override bool IncludesTile(TileId id) => id == tile;
        internal override Task<DecodedRasterTile> GetTileAsync(
            TileId id, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return Task.FromResult(new DecodedRasterTile(id, [0, 0, 0, 255], 1, 1, 0, 0));
        }
    }

    private sealed class HeldTileAcquisition(TileId[] oldTiles, TileId[] latestTiles)
        : RasterTileAcquisitionSession
    {
        internal TaskCompletionSource OldStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource LatestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseHeld { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseOther { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken HeldToken { get; private set; }
        internal int OverLimit;
        internal int HeldStarts;
        private int _oldStarted;
        private int _latestStarted;
        private int _active;

        internal override object SourceKey => this;
        internal override RasterSourceKind SourceKind => RasterSourceKind.Custom;
        internal override int TileSize => 256;
        internal override int MinSourceZoom => 0;
        internal override int MaxSourceZoom => 22;
        internal override bool CanAcquire => true;
        internal override int GetSourceZoom(MapScene scene) => scene.TileZoom;
        internal override bool IncludesTile(TileId id) =>
            oldTiles.Contains(id) || latestTiles.Contains(id);

        internal override async Task<DecodedRasterTile> GetTileAsync(
            TileId id, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _active) > 8)
            {
                Interlocked.Increment(ref OverLimit);
            }

            try
            {
                if (oldTiles.Contains(id))
                {
                    if (id == oldTiles[0])
                    {
                        HeldToken = cancellationToken;
                        Interlocked.Increment(ref HeldStarts);
                    }
                    if (Interlocked.Increment(ref _oldStarted) == oldTiles.Length)
                    {
                        OldStarted.TrySetResult();
                    }
                    await (id == oldTiles[0] ? ReleaseHeld.Task : ReleaseOther.Task)
                        .WaitAsync(cancellationToken);
                }
                else if (Interlocked.Increment(ref _latestStarted) == latestTiles.Length)
                {
                    LatestStarted.TrySetResult();
                }
                return new DecodedRasterTile(id, [0, 0, 0, 255], 1, 1, 0, 0);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
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
