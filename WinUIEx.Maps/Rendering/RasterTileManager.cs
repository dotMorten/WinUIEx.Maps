using System.Collections.Concurrent;
using System.Diagnostics;
using WinUIEx.Maps.Rendering.Diagnostics;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Bridges UI-thread raster snapshot publication and render-thread texture consumption by
/// scheduling versioned, cancellable acquisition work for every source.
/// </summary>
/// <remarks>
/// <para>
/// This type and its workers never access <see cref="TileLayer"/> or dependency properties.
/// <see cref="SetLayers"/> receives immutable UI-thread <see cref="TileLayerSnapshot"/>
/// instances containing worker-safe acquisition sessions, while <see cref="UpdateScene"/>
/// receives render-thread camera scenes through the control's event bridge.
/// </para>
/// <para>The raster pipeline proceeds as follows:</para>
/// <list type="number">
/// <item><description>
/// A <see cref="LayerWorker"/> coalesces scene updates, chooses a normalized source zoom, and
/// advances its generation when source zoom, source identity, or device state changes.
/// </description></item>
/// <item><description>
/// The worker activates that generation in <see cref="MapRenderer"/>, filters ordered
/// required tiles through source bounds, removes cache hits and pending reservations, and
/// continuously feeds a bounded center-prioritized worker set.
/// </description></item>
/// <item><description>
/// Up to eight network/decode operations across all layers enter the shared
/// <c>_networkSlots</c> semaphore. Scene, source, suspension, and lifetime changes cancel
/// linked wave tokens so obsolete work exits promptly.
/// </description></item>
/// <item><description>
/// Decoded BGRA buffers enter the renderer's separately bounded upload queue. Its reservation
/// and semaphore backpressure prevent duplicate or unbounded CPU-to-GPU work; the dedicated
/// upload thread creates textures and the render thread validates and commits them.
/// </description></item>
/// </list>
/// <para>
/// Attribution uses generation-scoped background tasks and a per-zoom cache. Current text is
/// dispatched through <see cref="AttributionChanged"/> and then marshalled by
/// <see cref="MapControl"/> to its UI dispatcher. ETW records lifecycle categories,
/// coordinates, generations, statuses, durations, and aggregate counts only. Source equality
/// keys, credentials, expanded URLs, response content, attribution text, and pixels are never
/// emitted.
/// </para>
/// </remarks>
internal sealed class RasterTileManager : IDisposable
{
    private const int MaximumConcurrentLoads = 8;
    private readonly object _sync = new();
    private readonly MapRenderer _renderer;
    private readonly SemaphoreSlim _networkSlots = new(MaximumConcurrentLoads);
    private readonly RequestConcurrencyTracker _requestConcurrency = new();
    private readonly Dictionary<long, LayerWorker> _workers = [];
    private MapScene? _scene;
    private bool _suspended = true;
    private bool _disposed;

    /// <summary>
    /// Initializes raster scheduling for a renderer and subscribes to device-resource
    /// invalidation so workers can regenerate their tile generations.
    /// </summary>
    internal RasterTileManager(MapRenderer renderer)
    {
        _renderer = renderer;
        _renderer.RasterTileResourcesInvalidated += OnRasterTileResourcesInvalidated;
    }

    internal event EventHandler<RasterAttributionUpdate>? AttributionChanged;

    internal event EventHandler<RasterAuthenticationFailure>? AzureAuthenticationFailed;

    internal int ActiveWorkerCount
    {
        get
        {
            lock (_sync)
            {
                return _workers.Values.Count(worker => worker.IsProcessing);
            }
        }
    }

    internal bool HasScene
    {
        get
        {
            lock (_sync)
            {
                return _scene is not null;
            }
        }
    }

    internal async Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_scene is not null &&
                    _workers.Values.All(worker => worker.IsIdle))
                {
                    return;
                }
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool IsAzureAuthenticationFailure(int statusCode) =>
        statusCode is 401 or 403;

    /// <summary>
    /// Determines whether a layer is visible, configured, and within its display zoom range.
    /// </summary>
    internal static bool ShouldAcquire(TileLayerSnapshot layer, double cameraZoom) =>
        layer.IsVisible &&
        layer.Opacity > 0 &&
        layer.Acquisition.CanAcquire &&
        cameraZoom >= layer.MinZoom &&
        cameraZoom < layer.MaxZoom;

    /// <summary>
    /// Determines whether source identity or source zoom changed enough to cancel an active
    /// request wave.
    /// </summary>
    internal static bool ShouldCancelActiveRequest(
        object activeSourceKey,
        int activeSourceZoom,
        object requestedSourceKey,
        int requestedSourceZoom) =>
        !Equals(activeSourceKey, requestedSourceKey) ||
        activeSourceZoom != requestedSourceZoom;

    /// <summary>
    /// Rejects source zoom below the minimum and caps zoom above the source maximum.
    /// </summary>
    internal static int? NormalizeSourceZoom(
        int requestedSourceZoom,
        int minimumSourceZoom,
        int maximumSourceZoom) =>
        requestedSourceZoom < minimumSourceZoom
            ? null
            : Math.Min(requestedSourceZoom, maximumSourceZoom);

    /// <summary>
    /// Determines whether work may update the attempted set for the scene and generation
    /// that originally scheduled it.
    /// </summary>
    internal static bool CanRecordAttempt(
        long currentGeneration,
        long workGeneration,
        long attemptedSceneVersion,
        long workSceneVersion) =>
        currentGeneration == workGeneration &&
        attemptedSceneVersion == workSceneVersion;

    /// <summary>
    /// Processes ordered work with a bounded set of continuously fed workers, stopping new
    /// starts when the caller reports that a newer scene superseded the input.
    /// </summary>
    internal static async Task<ContinuousWorkResult> RunContinuouslyAsync<T>(
        IReadOnlyList<T> items,
        int maximumConcurrency,
        Func<bool> canStart,
        Func<T, Task> processAsync)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumConcurrency);
        ArgumentNullException.ThrowIfNull(canStart);
        ArgumentNullException.ThrowIfNull(processAsync);
        if (items.Count == 0)
        {
            return new ContinuousWorkResult(0, 0, 0, 0);
        }

        object gate = new();
        int nextIndex = 0;
        int startedCount = 0;
        int completedCount = 0;
        int activeCount = 0;
        int maximumActiveCount = 0;

        async Task ProcessWorkerAsync()
        {
            while (true)
            {
                T item;
                lock (gate)
                {
                    if (nextIndex >= items.Count || !canStart())
                    {
                        return;
                    }
                    item = items[nextIndex++];
                    startedCount++;
                }

                int active = Interlocked.Increment(ref activeCount);
                int peak = Volatile.Read(ref maximumActiveCount);
                while (active > peak)
                {
                    int observed = Interlocked.CompareExchange(
                        ref maximumActiveCount,
                        active,
                        peak);
                    if (observed == peak)
                    {
                        break;
                    }
                    peak = observed;
                }
                try
                {
                    await processAsync(item).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref activeCount);
                    Interlocked.Increment(ref completedCount);
                }
            }
        }

        Task[] workers = Enumerable
            .Range(0, Math.Min(maximumConcurrency, items.Count))
            .Select(_ => ProcessWorkerAsync())
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        return new ContinuousWorkResult(
            startedCount,
            completedCount,
            maximumActiveCount,
            items.Count - startedCount);
    }

    /// <summary>
    /// Filters the active source scene's ordered required tiles through source coverage.
    /// </summary>
    internal static IReadOnlyList<TileId> GetActiveRequestTiles(
        MapScene activeSourceScene,
        Func<TileId, bool> includesTile) =>
        activeSourceScene.RequiredTiles.Where(includesTile).ToArray();

    /// <summary>
    /// Reconciles immutable layer snapshots with per-source workers, resetting renderer
    /// state only when pixel-producing source configuration changes.
    /// </summary>
    /// <remarks>
    /// Removed workers are disposed outside the manager lock. Source equality keys may
    /// contain private configuration and are compared only in process, never logged.
    /// </remarks>
    internal void SetLayers(IReadOnlyList<TileLayerSnapshot> layers)
    {
        List<LayerWorker> removed = [];
        List<LayerWorker> reset = [];
        bool hasAzureSource = layers.Any(
            layer => layer.Acquisition.SourceKind == RasterSourceKind.Azure);
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            HashSet<long> current = layers.Select(layer => layer.RuntimeId).ToHashSet();
            foreach (long id in _workers.Keys.Where(id => !current.Contains(id)).ToArray())
            {
                removed.Add(_workers[id]);
                _workers.Remove(id);
            }

            foreach (TileLayerSnapshot layer in layers)
            {
                bool added = false;
                if (!_workers.TryGetValue(layer.RuntimeId, out LayerWorker? worker))
                {
                    worker = new LayerWorker(
                        _renderer,
                        _networkSlots,
                        _requestConcurrency,
                        layer,
                        OnAttributionChanged,
                        OnAzureAuthenticationFailed);
                    _workers.Add(layer.RuntimeId, worker);
                    added = true;
                }
                if (worker.Update(layer, _scene, _suspended))
                {
                    reset.Add(worker);
                }
                if (layer.Acquisition.SourceKind == RasterSourceKind.Custom &&
                    (added || worker.WasSourceChanged))
                {
                    MapControlEventSource.Log.CustomTileLayerConfigured(
                        added,
                        layer.TileSize,
                        layer.MinSourceZoom,
                        layer.MaxSourceZoom,
                        layer.Acquisition switch
                        {
                            CustomRasterTileAcquisitionSession custom => custom.IsTms,
                            CustomVectorTileAcquisitionSession custom => custom.IsTms,
                            _ => false,
                        });
                }
            }
        }

        foreach (LayerWorker worker in removed)
        {
            RasterSourceKind kind = worker.SourceKind;
            worker.Dispose();
            _renderer.RemoveRasterTileSource(worker.RuntimeId);
            if (kind == RasterSourceKind.Custom)
            {
                MapControlEventSource.Log.CustomTileLayerRemoved();
            }
        }
        foreach (LayerWorker worker in reset)
        {
            _renderer.RemoveRasterTileSource(worker.RuntimeId);
            worker.QueueCurrentScene();
        }
        if (!hasAzureSource)
        {
            OnAttributionChanged(new RasterAttributionUpdate(0, 0, string.Empty));
        }
    }

    /// <summary>
    /// Publishes a new camera scene to every source worker and invalidates prior scene
    /// attempts.
    /// </summary>
    internal void UpdateScene(MapScene scene)
    {
        lock (_sync)
        {
            _scene = scene;
            foreach (LayerWorker worker in _workers.Values)
            {
                worker.UpdateScene(scene, _suspended);
            }
        }
    }

    /// <summary>
    /// Resumes all workers with the latest scene and records the loaded lifecycle transition.
    /// </summary>
    internal void Resume()
    {
        bool resumed;
        lock (_sync)
        {
            resumed = _suspended;
            _suspended = false;
            foreach (LayerWorker worker in _workers.Values)
            {
                worker.UpdateScene(_scene, false);
            }
        }
        if (resumed)
        {
            MapControlEventSource.Log.RenderingResumed("ControlLoaded");
        }
    }

    /// <summary>
    /// Suspends every worker, canceling active tile and attribution work for an unloaded
    /// control.
    /// </summary>
    internal void Suspend()
    {
        bool suspended;
        lock (_sync)
        {
            suspended = !_suspended;
            _suspended = true;
            foreach (LayerWorker worker in _workers.Values)
            {
                worker.Suspend();
            }
        }
        if (suspended)
        {
            MapControlEventSource.Log.RenderingSuspended("ControlUnloaded");
        }
    }

    /// <summary>
    /// Stops and removes source workers while retaining the reusable manager.
    /// </summary>
    internal void ReleaseWorkers(string reason)
    {
        LayerWorker[] workers;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _suspended = true;
            _scene = null;
            workers = _workers.Values.ToArray();
            _workers.Clear();
        }

        foreach (LayerWorker worker in workers)
        {
            worker.Dispose();
            _renderer.RemoveRasterTileSource(worker.RuntimeId);
        }
        MapControlEventSource.Log.RenderingSuspended(reason);
    }

    /// <summary>
    /// Disposes and removes every source worker, unsubscribes renderer callbacks, and releases
    /// the shared network-concurrency semaphore.
    /// </summary>
    public void Dispose()
    {
        Dispose("Dispose");
    }

    internal void Dispose(string reason)
    {
        LayerWorker[] workers;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            workers = _workers.Values.ToArray();
            _workers.Clear();
        }

        foreach (LayerWorker worker in workers)
        {
            worker.Dispose();
            _renderer.RemoveRasterTileSource(worker.RuntimeId);
        }
        _renderer.RasterTileResourcesInvalidated -= OnRasterTileResourcesInvalidated;
        _networkSlots.Dispose();
        MapControlEventSource.Log.RenderingSuspended(reason);
    }

    /// <summary>
    /// Forwards a worker's versioned attribution update to the control-facing event.
    /// </summary>
    private void OnAttributionChanged(RasterAttributionUpdate update) =>
        AttributionChanged?.Invoke(this, update);

    private void OnAzureAuthenticationFailed(RasterAuthenticationFailure failure) =>
        AzureAuthenticationFailed?.Invoke(this, failure);

    /// <summary>
    /// Advances all worker generations after renderer device resources are lost.
    /// </summary>
    /// <remarks>This callback may arrive on a thread-pool thread.</remarks>
    private void OnRasterTileResourcesInvalidated()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            foreach (LayerWorker worker in _workers.Values)
            {
                worker.InvalidateDevice();
            }
        }
    }

    /// <summary>
    /// Owns the serialized scheduling loop, cancellation sources, generation state,
    /// attempted-tile set, and attribution cache for one immutable raster source snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Public manager operations mutate worker state under <c>_sync</c> and coalesce work
    /// through <c>_work</c>. The processing task is the only consumer: it snapshots state
    /// under the lock, then performs renderer calls, network acquisition, decoding, and
    /// attribution outside the lock.
    /// </para>
    /// <para>
    /// A source key change resets caches and advances generation. Scene changes advance
    /// scene version and cancel a wave when its source zoom is no longer current. Device loss
    /// also advances generation and requires renderer activation, ensuring late tasks cannot
    /// commit into a replacement device or source.
    /// </para>
    /// </remarks>
    private sealed class LayerWorker : IDisposable
    {
        private readonly object _sync = new();
        private readonly MapRenderer _renderer;
        private readonly SemaphoreSlim _networkSlots;
        private readonly RequestConcurrencyTracker _requestConcurrency;
        private readonly Action<RasterAttributionUpdate> _attributionChanged;
        private readonly Action<RasterAuthenticationFailure> _authenticationFailed;
        private readonly SemaphoreSlim _work = new(0);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly HashSet<TileId> _attempted = [];
        private readonly ConcurrentDictionary<(int Zoom, long Generation), Task>
            _attributionTasks = [];
        private readonly Dictionary<int, string> _attributionCache = [];
        private readonly HashSet<ProcessingRun> _retiredProcessingRuns = [];
        private ProcessingRun? _processingRun;
        private TileLayerSnapshot _layer;
        private object _sourceKey;
        private MapScene? _scene;
        private CancellationTokenSource? _waveCancellation;
        private CancellationTokenSource? _attributionCancellation;
        private long _generation;
        private long _sceneVersion;
        private long _attemptedSceneVersion = -1;
        private int _activeSourceZoom = -1;
        private bool _authenticationFailureReported;
        private bool _activationRequired = true;
        private bool _pending;
        private bool _suspended = true;
        private bool _disposed;

        /// <summary>
        /// Captures an immutable source snapshot and starts the source's lifetime-bound
        /// background processing task.
        /// </summary>
        internal LayerWorker(
            MapRenderer renderer,
            SemaphoreSlim networkSlots,
            RequestConcurrencyTracker requestConcurrency,
            TileLayerSnapshot layer,
            Action<RasterAttributionUpdate> attributionChanged,
            Action<RasterAuthenticationFailure> authenticationFailed)
        {
            _renderer = renderer;
            _networkSlots = networkSlots;
            _requestConcurrency = requestConcurrency;
            _layer = layer;
            _sourceKey = layer.SourceKey;
            _attributionChanged = attributionChanged;
            _authenticationFailed = authenticationFailed;
            RuntimeId = layer.RuntimeId;
            SourceKind = layer.Acquisition.SourceKind;
        }

        internal long RuntimeId { get; }

        internal RasterSourceKind SourceKind { get; private set; }

        internal bool WasSourceChanged { get; private set; }

        internal bool IsProcessing
        {
            get
            {
                lock (_sync)
                {
                    return _processingRun is not null;
                }
            }
        }

        internal bool IsIdle
        {
            get
            {
                lock (_sync)
                {
                    return _suspended ||
                        _scene is null ||
                        !ShouldAcquire(_layer, _scene.Zoom) ||
                        (!_pending &&
                            _waveCancellation is null &&
                            _attemptedSceneVersion == _sceneVersion);
                }
            }
        }

        /// <summary>
        /// Replaces worker snapshot state, canceling and regenerating work when source
        /// configuration changes.
        /// </summary>
        /// <returns><see langword="true"/> when the pixel-producing source key changed.</returns>
        internal bool Update(TileLayerSnapshot layer, MapScene? scene, bool suspended)
        {
            lock (_sync)
            {
                bool sourceChanged = !Equals(_sourceKey, layer.SourceKey);
                WasSourceChanged = sourceChanged;
                _layer = layer;
                _scene = scene;
                _suspended = suspended;
                if (!suspended)
                {
                    EnsureProcessingLocked();
                }
                SourceKind = layer.Acquisition.SourceKind;
                if (sourceChanged)
                {
                    _sourceKey = layer.SourceKey;
                    _generation++;
                    _authenticationFailureReported = false;
                    _activeSourceZoom = -1;
                    _activationRequired = true;
                    _attempted.Clear();
                    _attributionCache.Clear();
                    CancelWaveLocked("SourceChanged");
                    CancelAttributionLocked();
                }
                else if (!ShouldAcquire(layer, scene?.Zoom ?? 0))
                {
                    CancelWaveLocked("LayerNotEligible");
                }
                if (!sourceChanged)
                {
                    QueueLocked();
                }
                return sourceChanged;
            }
        }

        /// <summary>
        /// Queues eligible work for the currently retained scene.
        /// </summary>
        internal void QueueCurrentScene()
        {
            lock (_sync)
            {
                QueueLocked();
            }
        }

        /// <summary>
        /// Publishes a scene version, clears attempted tiles, and cancels a wave whose source
        /// context no longer matches.
        /// </summary>
        internal void UpdateScene(MapScene? scene, bool suspended)
        {
            lock (_sync)
            {
                _scene = scene;
                _suspended = suspended;
                if (!suspended)
                {
                    EnsureProcessingLocked();
                }
                _sceneVersion++;
                _attempted.Clear();
                if (scene is not null && _activeSourceZoom >= 0)
                {
                    int requestedSourceZoom =
                        NormalizeSourceZoom(
                            _layer.Acquisition.GetSourceZoom(scene),
                            _layer.MinSourceZoom,
                            _layer.MaxSourceZoom) ?? -1;
                    if (ShouldCancelActiveRequest(
                        _sourceKey,
                        _activeSourceZoom,
                        _layer.SourceKey,
                        requestedSourceZoom))
                    {
                        CancelWaveLocked("RequestContextChanged");
                        CancelAttributionLocked();
                    }
                }
                QueueLocked();
            }
        }

        /// <summary>
        /// Marks the worker suspended, advances scene version, and cancels tile and
        /// attribution operations.
        /// </summary>
        internal void Suspend()
        {
            ProcessingRun? processingRun;
            lock (_sync)
            {
                _suspended = true;
                _pending = false;
                _sceneVersion++;
                CancelWaveLocked("ControlUnloaded");
                CancelAttributionLocked();
                processingRun = _processingRun;
                _processingRun = null;
                if (processingRun is not null)
                {
                    _retiredProcessingRuns.Add(processingRun);
                }
            }

            if (processingRun is not null)
            {
                try
                {
                    _ = processingRun.Cancellation.CancelAsync();
                }
                catch (ObjectDisposedException)
                {
                }
            }
            while (_work.Wait(0))
            {
            }
        }

        /// <summary>
        /// Advances generation and requires reactivation after device-backed renderer
        /// resources are invalidated.
        /// </summary>
        internal void InvalidateDevice()
        {
            lock (_sync)
            {
                _attempted.Clear();
                _generation++;
                _activeSourceZoom = -1;
                _activationRequired = true;
                CancelWaveLocked("DeviceInvalidated");
                CancelAttributionLocked();
                QueueLocked();
            }
        }

        /// <summary>
        /// Cancels lifetime, wave, and attribution work; joins owned tasks when safe; and
        /// releases worker synchronization resources.
        /// </summary>
        public void Dispose()
        {
            ProcessingRun[] processingRuns;
            CancellationTokenSource? waveCancellation;
            CancellationTokenSource? attributionCancellation;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _pending = false;
                waveCancellation = _waveCancellation;
                attributionCancellation = _attributionCancellation;
                processingRuns = _retiredProcessingRuns
                    .Append(_processingRun)
                    .Where(run => run is not null)
                    .Cast<ProcessingRun>()
                    .ToArray();
                _processingRun = null;
                _retiredProcessingRuns.Clear();
                _lifetime.Cancel();
            }

            foreach (ProcessingRun run in processingRuns)
            {
                try
                {
                    run.Cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
            try
            {
                waveCancellation?.CancelAsync().GetAwaiter().GetResult();
                attributionCancellation?.CancelAsync().GetAwaiter().GetResult();
            }
            catch (ObjectDisposedException)
            {
            }

            foreach (ProcessingRun run in processingRuns)
            {
                if (Task.CurrentId != run.Task.Id)
                {
                    try
                    {
                        run.Task.GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }
            try
            {
                Task.WhenAll(_attributionTasks.Values).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }

            _waveCancellation?.Dispose();
            _lifetime.Dispose();
            _work.Dispose();
        }

        /// <summary>
        /// Starts the restartable processing loop when the source becomes active.
        /// </summary>
        /// <remarks>The caller must hold the worker synchronization lock.</remarks>
        private void EnsureProcessingLocked()
        {
            if (_disposed || _suspended || _processingRun is not null)
            {
                return;
            }

            CancellationTokenSource cancellation =
                CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            ProcessingRun run = new(cancellation);
            _processingRun = run;
            run.Task = Task.Run(() => ProcessAsync(cancellation.Token));
            _ = run.Task.ContinueWith(
                _ => OnProcessingRunCompleted(run),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void OnProcessingRunCompleted(ProcessingRun run)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_processingRun, run))
                {
                    _processingRun = null;
                }
                _retiredProcessingRuns.Remove(run);
            }
            run.Cancellation.Dispose();
        }

        /// <summary>
        /// Coalesces one work signal when the worker, scene, and layer are eligible.
        /// </summary>
        /// <remarks>The caller must hold the worker synchronization lock.</remarks>
        private void QueueLocked()
        {
            if (_disposed ||
                _suspended ||
                _scene is null ||
                !ShouldAcquire(_layer, _scene.Zoom))
            {
                return;
            }
            if (!_pending)
            {
                _pending = true;
                _work.Release();
            }
        }

        /// <summary>
        /// Requests asynchronous cancellation of the active tile wave and records only its
        /// non-sensitive lifecycle reason.
        /// </summary>
        /// <remarks>The caller must hold the worker synchronization lock.</remarks>
        private void CancelWaveLocked(string reason)
        {
            CancellationTokenSource? cancellation = _waveCancellation;
            if (cancellation is null || cancellation.IsCancellationRequested)
            {
                return;
            }
            MapControlEventSource.Log.TileRequestsCanceled(_generation, reason);
            try
            {
                _ = cancellation.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// Requests asynchronous cancellation of the current attribution operation.
        /// </summary>
        /// <remarks>The caller must hold the worker synchronization lock.</remarks>
        private void CancelAttributionLocked()
        {
            CancellationTokenSource? cancellation = _attributionCancellation;
            if (cancellation is null || cancellation.IsCancellationRequested)
            {
                return;
            }
            try
            {
                _ = cancellation.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// Runs the worker's lifetime loop, activating versioned source scenes and executing
        /// bounded, center-prioritized acquisition waves.
        /// </summary>
        /// <remarks>
        /// Each wave owns a linked cancellation source. The scheduler requests only the
        /// active level; fallback cache generation and ordering remain renderer-owned.
        /// </remarks>
        private async Task ProcessAsync(CancellationToken processingToken)
        {
            while (true)
            {
                await _work.WaitAsync(processingToken).ConfigureAwait(false);
                processingToken.ThrowIfCancellationRequested();

                TileLayerSnapshot layer;
                MapScene scene;
                MapScene sourceScene;
                long generation;
                long sceneVersion;
                int sourceZoom;
                bool clearExistingTiles;
                bool deactivateSource;
                CancellationTokenSource waveCancellation;
                HashSet<TileId> attempted;
                lock (_sync)
                {
                    if (!_pending || _suspended || _scene is null)
                    {
                        continue;
                    }

                    _pending = false;
                    layer = _layer;
                    scene = _scene;
                    sceneVersion = _sceneVersion;
                    if (_attemptedSceneVersion != sceneVersion)
                    {
                        _attempted.Clear();
                        _attemptedSceneVersion = sceneVersion;
                    }
                    attempted = [.. _attempted];
                    int requestedSourceZoom = layer.Acquisition.GetSourceZoom(scene);
                    int? normalizedSourceZoom = NormalizeSourceZoom(
                        requestedSourceZoom,
                        layer.MinSourceZoom,
                        layer.MaxSourceZoom);
                    deactivateSource = !normalizedSourceZoom.HasValue;
                    sourceZoom = normalizedSourceZoom ?? requestedSourceZoom;
                    if (deactivateSource)
                    {
                        _activeSourceZoom = -1;
                        waveCancellation = null!;
                        sourceScene = null!;
                        clearExistingTiles = false;
                        generation = _generation;
                    }
                    else
                    {
                        bool zoomChanged = _activeSourceZoom != sourceZoom;
                        if (_generation == 0 || zoomChanged)
                        {
                            _generation++;
                        }
                        generation = _generation;
                        clearExistingTiles = _activationRequired;
                        _activationRequired = false;
                        _activeSourceZoom = sourceZoom;
                        sourceScene = MapCamera.CreateScene(
                            scene.Longitude,
                            scene.Latitude,
                            scene.Zoom,
                            sourceZoom,
                            scene.ViewportWidth,
                            scene.ViewportHeight,
                            scene.Heading,
                            scene.Pitch);
                        waveCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                            processingToken);
                        _waveCancellation?.Dispose();
                        _waveCancellation = waveCancellation;
                    }
                }

                if (deactivateSource)
                {
                    _renderer.DeactivateRasterTileSource(RuntimeId);
                    continue;
                }
                _renderer.ActivateRasterTileSet(
                    RuntimeId,
                    generation,
                    sceneVersion,
                    sourceScene,
                    layer.Acquisition.IncludesTile,
                    layer.Acquisition.SourceKind,
                    layer.Acquisition.RenderKind,
                    clearExistingTiles);
                CancellationToken cancellationToken = waveCancellation.Token;
                // Cached fallback coverage is renderer-owned. The scheduler requests only
                // the normal active source level and never fills fallback levels.
                IReadOnlyList<TileId> required = GetActiveRequestTiles(
                    sourceScene,
                    layer.Acquisition.IncludesTile);
                RasterTileLookupResult lookup = _renderer.GetMissingRasterTiles(
                    RuntimeId,
                    generation,
                    required);
                TileId[] unattempted = lookup.MissingTiles
                    .Where(id => !attempted.Contains(id))
                    .ToArray();
                int initialCount = Math.Min(MaximumConcurrentLoads, unattempted.Length);

                TraceWaveStart(
                    layer,
                    generation,
                    sceneVersion,
                    sourceZoom,
                    lookup,
                    initialCount);
                QueueAttribution(
                    layer.Acquisition,
                    sourceZoom,
                    generation,
                    processingToken);
                long started = Stopwatch.GetTimestamp();
                TileWaveResult result = new();
                ContinuousWorkResult schedulingResult = default;
                try
                {
                    schedulingResult = await RunContinuouslyAsync(
                        unattempted,
                        MaximumConcurrentLoads,
                        () =>
                        {
                            lock (_sync)
                            {
                                return !_disposed &&
                                    !_suspended &&
                                    !cancellationToken.IsCancellationRequested &&
                                    _generation == generation &&
                                    Equals(_sourceKey, layer.SourceKey) &&
                                    sceneVersion == _sceneVersion;
                            }
                        },
                        async id =>
                        {
                            lock (_sync)
                            {
                                if (CanRecordAttempt(
                                    _generation,
                                    generation,
                                    _attemptedSceneVersion,
                                    sceneVersion))
                                {
                                    _attempted.Add(id);
                                }
                            }
                            await LoadTileAsync(
                                layer.Acquisition,
                                id,
                                generation,
                                result,
                                cancellationToken).ConfigureAwait(false);
                        }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                finally
                {
                    TraceWaveStop(
                        layer.Acquisition.SourceKind,
                        generation,
                        sceneVersion,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        result.CompletedCount,
                        result.FailedCount,
                        result.CanceledCount,
                        schedulingResult.DeferredCount);
                    MapControlEventSource.Log.TileSchedulerSummary(
                        (int)layer.Acquisition.SourceKind,
                        generation,
                        sceneVersion,
                        unattempted.Length,
                        schedulingResult.StartedCount,
                        schedulingResult.CompletedCount,
                        schedulingResult.MaximumConcurrency,
                        schedulingResult.DeferredCount,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds);

                    lock (_sync)
                    {
                        if (ReferenceEquals(_waveCancellation, waveCancellation))
                        {
                            _waveCancellation = null;
                        }
                    }
                    waveCancellation.Dispose();
                }
            }
        }

        /// <summary>
        /// Acquires one tile through the shared network semaphore and transfers valid decoded
        /// pixels to the renderer's generation-checked upload queue.
        /// </summary>
        /// <remarks>
        /// Failure telemetry contains coordinates and status metadata only; it never includes
        /// template URLs, credentials, or pixel buffers.
        /// </remarks>
        private async Task LoadTileAsync(
            RasterTileAcquisitionSession acquisition,
            TileId id,
            long generation,
            TileWaveResult result,
            CancellationToken cancellationToken)
        {
            bool entered = false;
            bool requestActive = false;
            int activeRequests = 0;
            long started = Stopwatch.GetTimestamp();
            try
            {
                await _networkSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
                activeRequests = _requestConcurrency.Enter();
                requestActive = true;
                long uploadWaitStarted;
                double downloadMilliseconds;
                double decodeMilliseconds;
                bool accepted;
                if (acquisition.RenderKind is
                    LayerRenderKind.VectorPoints or LayerRenderKind.HybridTiles)
                {
                    DecodedVectorTile decoded = await acquisition
                        .GetVectorTileAsync(id, cancellationToken)
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    downloadMilliseconds = decoded.DownloadMilliseconds;
                    decodeMilliseconds = decoded.DecodeMilliseconds;
                    uploadWaitStarted = Stopwatch.GetTimestamp();
                    RasterTileKey key = new(RuntimeId, decoded.Id);
                    RasterTileData? background = decoded.Background is
                        DecodedRasterTile raster
                        ? new RasterTileData(
                            key,
                            raster.Pixels,
                            raster.Width,
                            raster.Height,
                            generation,
                            acquisition.SourceKind)
                        : null;
                    VectorTileData vectorTile = new(
                        key,
                        decoded.Features,
                        decoded.StyleAssets,
                        decoded.SpriteTextures,
                        background,
                        generation,
                        acquisition.TelemetryStyle);
                    accepted = acquisition.RenderKind == LayerRenderKind.HybridTiles
                        ? await _renderer.QueueHybridTileAsync(
                            vectorTile,
                            cancellationToken).ConfigureAwait(false)
                        : await _renderer.QueueVectorTileAsync(
                            vectorTile,
                            cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    DecodedRasterTile decoded = await acquisition
                        .GetTileAsync(id, cancellationToken)
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    downloadMilliseconds = decoded.DownloadMilliseconds;
                    decodeMilliseconds = decoded.DecodeMilliseconds;
                    uploadWaitStarted = Stopwatch.GetTimestamp();
                    accepted = await _renderer.QueueRasterUploadAsync(
                        new RasterTileData(
                            new RasterTileKey(RuntimeId, decoded.Id),
                            decoded.Pixels,
                            decoded.Width,
                            decoded.Height,
                            generation,
                            acquisition.SourceKind),
                        cancellationToken).ConfigureAwait(false);
                }
                double uploadWaitMilliseconds =
                    Stopwatch.GetElapsedTime(uploadWaitStarted).TotalMilliseconds;
                if (accepted)
                {
                    Interlocked.Increment(ref result.CompletedCount);
                }
                else
                {
                    Interlocked.Increment(ref result.CanceledCount);
                }
                MapControlEventSource.Log.TileRequestTiming(
                    (int)acquisition.SourceKind,
                    generation,
                    downloadMilliseconds,
                    decodeMilliseconds,
                    uploadWaitMilliseconds,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    activeRequests,
                    _requestConcurrency.Peak);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref result.CanceledCount);
            }
            catch (AzureMapsRequestException exception)
            {
                Interlocked.Increment(ref result.FailedCount);
                ReportAuthenticationFailure(
                    acquisition,
                    generation,
                    (int)exception.StatusCode);
                TraceRequestFailure(
                    acquisition,
                    id,
                    generation,
                    (int)exception.StatusCode,
                    "ServiceResponse",
                    exception.DiagnosticExceptionType);
            }
            catch (HttpRequestException exception)
            {
                Interlocked.Increment(ref result.FailedCount);
                TraceRequestFailure(
                    acquisition,
                    id,
                    generation,
                    (int?)exception.StatusCode ?? 0,
                    "Network",
                    exception.GetType().Name);
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref result.FailedCount);
                TraceRequestFailure(
                    acquisition,
                    id,
                    generation,
                    0,
                    "DecodeOrTemplate",
                    exception.GetType().Name);
            }
            finally
            {
                if (entered)
                {
                    _networkSlots.Release();
                }
                if (requestActive)
                {
                    _requestConcurrency.Exit();
                }
            }
        }

        /// <summary>
        /// Publishes cached attribution or starts one generation-scoped request for a source
        /// zoom.
        /// </summary>
        private void QueueAttribution(
            RasterTileAcquisitionSession acquisition,
            int zoom,
            long generation,
            CancellationToken processingToken)
        {
            if (!acquisition.SupportsAttribution)
            {
                return;
            }
            string? cachedAttribution = null;
            CancellationTokenSource? attributionCancellation = null;
            (int Zoom, long Generation) key = (zoom, generation);
            lock (_sync)
            {
                if (_attributionCache.TryGetValue(zoom, out string? attribution))
                {
                    cachedAttribution = attribution;
                }
                else if (!_attributionTasks.ContainsKey(key))
                {
                    CancelAttributionLocked();
                    attributionCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(processingToken);
                    _attributionCancellation = attributionCancellation;
                }
            }
            if (cachedAttribution is not null)
            {
                _attributionChanged(new RasterAttributionUpdate(
                    RuntimeId,
                    generation,
                    cachedAttribution));
            }
            else if (attributionCancellation is not null)
            {
                _attributionTasks.TryAdd(
                    key,
                    LoadAttributionAndRemoveAsync(
                        key,
                        acquisition,
                        attributionCancellation));
            }
        }

        /// <summary>
        /// Loads attribution, caches and publishes it only while source identity and
        /// generation remain current, then removes task ownership.
        /// </summary>
        /// <remarks>
        /// Cancellation and request failures are contained by the worker; diagnostics expose
        /// status and failure categories rather than request URLs or credentials.
        /// </remarks>
        private async Task LoadAttributionAndRemoveAsync(
            (int Zoom, long Generation) key,
            RasterTileAcquisitionSession acquisition,
            CancellationTokenSource attributionCancellation)
        {
            CancellationToken cancellationToken = attributionCancellation.Token;
            try
            {
                string? attribution = await acquisition
                    .GetAttributionAsync(key.Zoom, cancellationToken)
                    .ConfigureAwait(false);
                if (attribution is null || cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                lock (_sync)
                {
                    if (_disposed ||
                        _generation != key.Generation ||
                        !Equals(_sourceKey, acquisition.SourceKey))
                    {
                        return;
                    }
                    _attributionCache[key.Zoom] = attribution;
                }
                _attributionChanged(new RasterAttributionUpdate(
                    RuntimeId,
                    key.Generation,
                    attribution));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (AzureMapsRequestException exception)
            {
                ReportAuthenticationFailure(
                    acquisition,
                    key.Generation,
                    (int)exception.StatusCode);
                MapControlEventSource.Log.AttributionRequestFailed(
                    acquisition.TelemetryStyle,
                    key.Zoom,
                    (int)exception.StatusCode,
                    "ServiceResponse",
                    exception.GetType().Name);
            }
            catch (HttpRequestException exception)
            {
                MapControlEventSource.Log.AttributionRequestFailed(
                    acquisition.TelemetryStyle,
                    key.Zoom,
                    (int?)exception.StatusCode ?? 0,
                    "Network",
                    exception.GetType().Name);
            }
            catch (Exception exception)
            {
                MapControlEventSource.Log.AttributionRequestFailed(
                    acquisition.TelemetryStyle,
                    key.Zoom,
                    0,
                    "Decode",
                    exception.GetType().Name);
            }
            finally
            {
                _attributionTasks.TryRemove(key, out _);
                lock (_sync)
                {
                    if (ReferenceEquals(_attributionCancellation, attributionCancellation))
                    {
                        _attributionCancellation = null;
                    }
                }
                attributionCancellation.Dispose();
            }
        }

        private void ReportAuthenticationFailure(
            RasterTileAcquisitionSession acquisition,
            long generation,
            int statusCode)
        {
            if (acquisition.SourceKind != RasterSourceKind.Azure ||
                !IsAzureAuthenticationFailure(statusCode))
            {
                return;
            }

            lock (_sync)
            {
                if (_disposed ||
                    _authenticationFailureReported ||
                    _generation != generation ||
                    !Equals(_sourceKey, acquisition.SourceKey))
                {
                    return;
                }
                _authenticationFailureReported = true;
            }

            _authenticationFailed(new RasterAuthenticationFailure(
                RuntimeId,
                generation,
                statusCode));
        }

        /// <summary>
        /// Emits source-specific aggregate telemetry for the start of an acquisition wave.
        /// </summary>
        private static void TraceWaveStart(
            TileLayerSnapshot layer,
            long generation,
            long sceneVersion,
            int sourceZoom,
            RasterTileLookupResult lookup,
            int batchCount)
        {
            if (layer.Acquisition.SourceKind == RasterSourceKind.Azure)
            {
                MapControlEventSource.Log.TileWaveStart(
                    generation,
                    sceneVersion,
                    layer.Acquisition.TelemetryStyle,
                    sourceZoom,
                    lookup.RequiredCount,
                    lookup.CacheHitCount,
                    lookup.PendingCount,
                    batchCount);
            }
            else
            {
                MapControlEventSource.Log.CustomTileWaveStart(
                    generation,
                    sourceZoom,
                    lookup.RequiredCount,
                    lookup.CacheHitCount,
                    lookup.PendingCount,
                    batchCount);
            }
        }

        /// <summary>
        /// Emits source-specific aggregate telemetry for wave duration and completion counts.
        /// </summary>
        private static void TraceWaveStop(
            RasterSourceKind kind,
            long generation,
            long sceneVersion,
            double durationMilliseconds,
            int completedCount,
            int failedCount,
            int canceledCount,
            int remainingCount)
        {
            if (kind == RasterSourceKind.Azure)
            {
                MapControlEventSource.Log.TileWaveStop(
                    generation,
                    sceneVersion,
                    durationMilliseconds,
                    completedCount,
                    failedCount,
                    canceledCount,
                    remainingCount);
            }
            else
            {
                MapControlEventSource.Log.CustomTileWaveStop(
                    generation,
                    durationMilliseconds,
                    completedCount,
                    failedCount,
                    canceledCount,
                    remainingCount);
            }
        }

        /// <summary>
        /// Emits sanitized source-specific request failure metadata without logging URLs,
        /// credentials, response bodies, or pixels.
        /// </summary>
        private static void TraceRequestFailure(
            RasterTileAcquisitionSession acquisition,
            TileId id,
            long generation,
            int statusCode,
            string failureKind,
            string exceptionType)
        {
            if (acquisition.SourceKind == RasterSourceKind.Azure)
            {
                MapControlEventSource.Log.TileRequestFailed(
                    id.Zoom,
                    id.X,
                    id.Y,
                    acquisition.TelemetryStyle,
                    generation,
                    statusCode,
                    failureKind,
                    exceptionType);
            }
            else
            {
                MapControlEventSource.Log.CustomTileRequestFailed(
                    id.Zoom,
                    id.X,
                    id.Y,
                    generation,
                    statusCode,
                    failureKind,
                    exceptionType);
            }
        }

        private sealed class ProcessingRun(CancellationTokenSource cancellation)
        {
            internal CancellationTokenSource Cancellation { get; } = cancellation;

            internal Task Task { get; set; } = Task.CompletedTask;
        }

        /// <summary>
        /// Holds thread-safe aggregate completion counters shared by concurrent requests in
        /// one acquisition wave.
        /// </summary>
        private sealed class TileWaveResult
        {
            internal int CompletedCount;
            internal int FailedCount;
            internal int CanceledCount;
        }
    }

    /// <summary>
    /// Tracks process-local request occupancy for one manager without exposing request
    /// addresses or source identities.
    /// </summary>
    private sealed class RequestConcurrencyTracker
    {
        private int _active;
        private int _peak;

        internal int Peak => Volatile.Read(ref _peak);

        internal int Enter()
        {
            int active = Interlocked.Increment(ref _active);
            int peak = Volatile.Read(ref _peak);
            while (active > peak)
            {
                int observed = Interlocked.CompareExchange(ref _peak, active, peak);
                if (observed == peak)
                {
                    break;
                }
                peak = observed;
            }
            return active;
        }

        internal void Exit() => Interlocked.Decrement(ref _active);
    }
}

internal readonly record struct RasterAuthenticationFailure(
    long RuntimeId,
    long Generation,
    int StatusCode);

/// <summary>
/// Summarizes one bounded continuously fed scheduling run.
/// </summary>
internal readonly record struct ContinuousWorkResult(
    int StartedCount,
    int CompletedCount,
    int MaximumConcurrency,
    int DeferredCount);
