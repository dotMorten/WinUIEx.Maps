using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using WinUIEx.Maps.Rendering.Diagnostics;
using static WinUIEx.Maps.Rendering.DirectXInterop;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Implements raster upload backpressure, generation-safe texture commit, cache and fallback
/// management, fading, drawing, and deferred native disposal for <see cref="MapRenderer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Acquisition workers submit decoded BGRA buffers through a bounded semaphore. A
/// <see cref="PendingTileTracker"/> reservation deduplicates each source/tile key and ties
/// capacity ownership to one upload attempt. The dedicated upload thread validates pixels,
/// creates immutable D3D textures, and publishes completions tagged with source generation,
/// reservation, source kind, and device epoch.
/// </para>
/// <para>
/// The render thread releases capacity and reservations for every completion and accepts a
/// texture only when its source generation and device epoch still match. Accepted textures
/// enter the renderer-owned cache; stale and duplicate textures move to the upload thread's
/// disposal queue. This handoff keeps native destruction off acquisition workers and prevents
/// an old scene or lost device from repopulating current GPU state.
/// </para>
/// <para>
/// Frames draw retained fallback zoom levels before the active source level, fade newly
/// committed tiles to the layer opacity, and remove fallbacks after active coverage is
/// opaque. Cache trimming protects visible active and fallback coverage when possible,
/// evicts least-recently-used entries to the byte budget, and preserves layer render order.
/// Device loss disposes all textures, clears generations and reservations, and asynchronously
/// notifies the manager to schedule fresh work.
/// </para>
/// </remarks>
internal sealed partial class MapRenderer
{
    private const int MaximumUploadsPerPass = 32;
    private const int MaximumPendingRasterUploads = 32;
    internal const int MaximumFallbackTileLevels = MapCamera.MaximumTileZoom;
    private const int MaximumSourceZoomOffset = 3;
    private const ulong MinimumRasterCacheBytes = 32 * 1024 * 1024;
    private const ulong RasterCacheHistoryBytes = 16 * 1024 * 1024;
    private const ulong MaximumRasterCacheBytes = 128 * 1024 * 1024;
    private readonly ConcurrentQueue<QueuedRasterTileUpload> _rasterPixelUploads = new();
    private readonly ConcurrentQueue<CompletedRasterTileUpload> _completedRasterUploads = new();
    private readonly ConcurrentQueue<TileTexture> _textureDisposals = new();
    private readonly SemaphoreSlim _pendingRasterUploadCapacity =
        new(MaximumPendingRasterUploads, MaximumPendingRasterUploads);
    private readonly Dictionary<RasterTileKey, TileTexture> _rasterTiles = [];
    private readonly Dictionary<long, RasterLayerState> _rasterLayers = [];
    private readonly PendingTileTracker _pendingRasterTiles = new();
    private ulong _lastReportedRasterCachePressureBytes;
    private int _rasterUploadRenderLockWaiters;

    internal event Action? RasterTileResourcesInvalidated;

    /// <summary>
    /// Activates a versioned raster scene, optionally clearing cached textures or retaining
    /// prior zoom levels as visual fallbacks.
    /// </summary>
    /// <remarks>
    /// Generation changes invalidate pending reservations so late acquisition or upload
    /// completions cannot enter the new tile set.
    /// </remarks>
    public void ActivateRasterTileSet(
        long sourceId,
        long generation,
        long sceneVersion,
        MapScene scene,
        Func<TileId, bool> includesTile,
        RasterSourceKind sourceKind,
        bool clearExistingTiles)
    {
        int retainedTileCount;
        lock (RenderLock)
        {
            if (!_rasterLayers.TryGetValue(sourceId, out RasterLayerState? state))
            {
                state = new RasterLayerState();
                _rasterLayers.Add(sourceId, state);
            }

            bool generationChanged = state.Generation != generation;
            bool sceneChanged = state.SceneVersion != sceneVersion;
            int previousTileZoom = state.Scene?.TileZoom ?? -1;
            if (clearExistingTiles)
            {
                RemoveRasterTilesLocked(sourceId);
                state.FallbackTileZooms.Clear();
            }
            else if (previousTileZoom >= 0 && previousTileZoom != scene.TileZoom)
            {
                UpdateCachedFallbackTileZooms(sourceId, state, scene.TileZoom);
            }

            state.Generation = generation;
            state.SceneVersion = sceneVersion;
            state.Scene = scene;
            state.IncludesTile = includesTile;
            state.SourceKind = sourceKind;
            if (generationChanged)
            {
                _pendingRasterTiles.RemoveSource(sourceId);
            }
            if (generationChanged || sceneChanged)
            {
                state.CoverageStartTimestamp = Stopwatch.GetTimestamp();
                state.FirstCoverageReported = false;
                state.FullCoverageReported = false;
                state.OpaqueCoverageReported = false;
            }
            retainedTileCount = _rasterTiles.Keys.Count(key => key.SourceId == sourceId);
        }

        MapControlEventSource.Log.TileSetActivated(
            generation,
            scene.TileZoom,
            clearExistingTiles,
            retainedTileCount);
        RequestRender();
    }

    /// <summary>
    /// Removes a raster source, its cached textures, layer state, and pending reservations.
    /// </summary>
    public void RemoveRasterTileSource(long sourceId)
    {
        lock (RenderLock)
        {
            RemoveRasterTilesLocked(sourceId);
            _rasterLayers.Remove(sourceId);
            _pendingRasterTiles.RemoveSource(sourceId);
        }
        RequestRender();
    }

    /// <summary>
    /// Stops drawing and requesting a source while retaining its cached textures for
    /// potential reactivation.
    /// </summary>
    public void DeactivateRasterTileSource(long sourceId)
    {
        lock (RenderLock)
        {
            if (_rasterLayers.TryGetValue(sourceId, out RasterLayerState? state))
            {
                state.Scene = null;
                state.FallbackTileZooms.Clear();
            }
        }
        RequestRender();
    }

    /// <summary>
    /// Classifies required tiles for the active generation as cached, pending, or missing.
    /// </summary>
    public RasterTileLookupResult GetMissingRasterTiles(
        long sourceId,
        long generation,
        IEnumerable<TileId> requiredTiles)
    {
        lock (RenderLock)
        {
            if (!_rasterLayers.TryGetValue(sourceId, out RasterLayerState? state) ||
                state.Generation != generation)
            {
                return new RasterTileLookupResult([], 0, 0, 0);
            }

            TileId[] required = requiredTiles.ToArray();
            int hitCount = 0;
            int pendingCount = 0;
            List<TileId> missing = [];
            foreach (TileId id in required)
            {
                RasterTileKey key = new(sourceId, id);
                if (_rasterTiles.ContainsKey(key))
                {
                    hitCount++;
                }
                else if (_pendingRasterTiles.Contains(key))
                {
                    pendingCount++;
                }
                else
                {
                    missing.Add(id);
                }
            }
            MapControlEventSource.Log.TileCacheLookupSummary(
                required.Length,
                hitCount,
                pendingCount,
                missing.Count);
            return new RasterTileLookupResult(
                missing,
                required.Length,
                hitCount,
                pendingCount);
        }
    }

    /// <summary>
    /// Waits for bounded upload capacity, reserves a current missing tile, and queues its
    /// pixels for the GPU upload worker.
    /// </summary>
    /// <remarks>
    /// Cancellation applies while waiting for capacity. Generation, cache, and reservation
    /// checks occur under the render lock before queue ownership is accepted.
    /// </remarks>
    public async Task<bool> QueueRasterUploadAsync(
        RasterTileData tile,
        CancellationToken cancellationToken)
    {
        await _pendingRasterUploadCapacity.WaitAsync(cancellationToken).ConfigureAwait(false);
        long reservation;
        lock (RenderLock)
        {
            reservation = _pendingRasterTiles.TryReserve(tile.Key);
            if (!_rasterLayers.TryGetValue(tile.Key.SourceId, out RasterLayerState? state) ||
                tile.Generation != state.Generation ||
                _rasterTiles.ContainsKey(tile.Key) ||
                reservation == 0)
            {
                if (reservation != 0)
                {
                    _pendingRasterTiles.Release(tile.Key, reservation);
                }
                _pendingRasterUploadCapacity.Release();
                return false;
            }
        }

        _rasterPixelUploads.Enqueue(new QueuedRasterTileUpload(tile, reservation));
        _uploadRequested.Set();
        return true;
    }

    /// <summary>
    /// Verifies that dimensions are nonzero and exactly match a representable BGRA8 buffer.
    /// </summary>
    internal static bool IsValidPixelBuffer(byte[]? pixels, uint width, uint height)
    {
        if (pixels is null || width == 0 || height == 0)
        {
            return false;
        }

        ulong expectedLength = (ulong)width * height * 4;
        return expectedLength <= int.MaxValue && pixels.LongLength == (long)expectedLength;
    }

    /// <summary>
    /// Commits upload-worker completions on the render thread when their generation and
    /// device epoch remain current.
    /// </summary>
    /// <remarks>
    /// Returns queue capacity and reservation ownership for every completion; stale,
    /// duplicate, or superseded textures are transferred to deferred disposal.
    /// </remarks>
    private void ProcessCompletedRasterUploads()
    {
        int acceptedCount = 0;
        int staleDroppedCount = 0;
        int duplicateDroppedCount = 0;
        int acceptedCustomCount = 0;
        int staleCustomCount = 0;
        while (_completedRasterUploads.TryDequeue(out CompletedRasterTileUpload completed))
        {
            _pendingRasterUploadCapacity.Release();
            _pendingRasterTiles.Release(completed.Key, completed.Reservation);
            if (!_rasterLayers.TryGetValue(
                    completed.Key.SourceId,
                    out RasterLayerState? state) ||
                completed.Generation != state.Generation ||
                completed.DeviceEpoch != _deviceEpoch)
            {
                staleDroppedCount++;
                if (completed.SourceKind == RasterSourceKind.Custom)
                {
                    staleCustomCount++;
                }
                QueueTextureDisposal(completed.Texture);
                continue;
            }

            if (_rasterTiles.TryAdd(completed.Key, completed.Texture))
            {
                acceptedCount++;
                if (completed.SourceKind == RasterSourceKind.Custom)
                {
                    acceptedCustomCount++;
                }
            }
            else
            {
                duplicateDroppedCount++;
                QueueTextureDisposal(completed.Texture);
            }
        }
        if (acceptedCount != 0 || staleDroppedCount != 0 || duplicateDroppedCount != 0)
        {
            MapControlEventSource.Log.TileUploadCommitSummary(
                acceptedCount,
                staleDroppedCount,
                duplicateDroppedCount);
            if (acceptedCustomCount != 0 || staleCustomCount != 0)
            {
                MapControlEventSource.Log.CustomTileUploadSummary(
                    acceptedCustomCount,
                    staleCustomCount,
                    _rasterTiles.Keys.Count(key =>
                        _rasterLayers.TryGetValue(key.SourceId, out RasterLayerState? state) &&
                        state.SourceKind == RasterSourceKind.Custom));
            }
        }
    }

    /// <summary>
    /// Processes a bounded batch of queued raster pixels on the upload worker and publishes
    /// GPU textures for render-thread commit.
    /// </summary>
    /// <remarks>
    /// The device reference, epoch, source generations, and exact work items are captured for
    /// each bounded pass. Generation and epoch checks prevent stale work from crossing resets,
    /// and all uncommitted resources return their reservation and capacity.
    /// </remarks>
    private unsafe void ProcessRasterPixelUploads()
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        bool traceTiming = MapControlEventSource.Log.IsEnabled(
            System.Diagnostics.Tracing.EventLevel.Informational,
            MapControlEventSource.Keywords.Tiles | MapControlEventSource.Keywords.Device);
        int queueStartCount = _rasterPixelUploads.Count;
        long textureCreateTicks = 0;
        long renderLockWaitTicks = 0;
        int renderWakeCount = 0;
        int uploadedCount = 0;
        int droppedCount = 0;
        int failedCount = 0;
        int processedCount = 0;
        long lastGeneration = 0;
        DrainTextureDisposals();
        if (_rasterPixelUploads.IsEmpty)
        {
            return;
        }

        IntPtr devicePointer;
        int deviceEpoch;
        Dictionary<long, long> sourceGenerations;
        List<QueuedRasterTileUpload> uploads =
            new(Math.Min(MaximumUploadsPerPass, queueStartCount));
        long renderLockStarted = traceTiming ? Stopwatch.GetTimestamp() : 0;
        _rasterUploadEnteredRenderLock.Reset();
        Interlocked.Increment(ref _rasterUploadRenderLockWaiters);
        try
        {
            lock (RenderLock)
            {
                _rasterUploadEnteredRenderLock.Set();
                if (traceTiming)
                {
                    renderLockWaitTicks += Stopwatch.GetTimestamp() - renderLockStarted;
                }
                devicePointer = DevicePointer;
                deviceEpoch = _deviceEpoch;
                sourceGenerations = _rasterLayers.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Generation);
                if (devicePointer == IntPtr.Zero)
                {
                    return;
                }
                int processingLimit = Math.Min(MaximumUploadsPerPass, queueStartCount);
                while (uploads.Count < processingLimit &&
                    _rasterPixelUploads.TryDequeue(out QueuedRasterTileUpload upload))
                {
                    uploads.Add(upload);
                }
                if (uploads.Count == 0)
                {
                    return;
                }
                Marshal.AddRef(devicePointer);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _rasterUploadRenderLockWaiters);
        }

        try
        {
            foreach (QueuedRasterTileUpload upload in uploads)
            {
                if (++processedCount % 8 == 0)
                {
                    DrainTextureDisposals();
                }

                RasterTileData tile = upload.Tile;
                lastGeneration = tile.Generation;
                if (!sourceGenerations.TryGetValue(
                        tile.Key.SourceId,
                        out long currentGeneration) ||
                    currentGeneration != tile.Generation)
                {
                    droppedCount++;
                    ReleasePendingRasterTile(tile.Key, upload.Reservation);
                    _pendingRasterUploadCapacity.Release();
                    continue;
                }
                if (!IsValidPixelBuffer(tile.Pixels, tile.Width, tile.Height))
                {
                    failedCount++;
                    ReleasePendingRasterTile(tile.Key, upload.Reservation);
                    _pendingRasterUploadCapacity.Release();
                    TraceUploadFailure(
                        tile,
                        "ValidatePixelBuffer",
                        nameof(InvalidDataException),
                        0);
                    continue;
                }

                string operation = "creating the texture";
                TileTexture? completedTexture = null;
                bool completedQueued = false;
                try
                {
                    operation = "creating the texture and shader-resource view";
                    long textureCreateStarted = traceTiming ? Stopwatch.GetTimestamp() : 0;
                    completedTexture = CreateTileTexture(
                        devicePointer,
                        tile.Pixels,
                        tile.Width,
                        tile.Height,
                        "Failed to create a raster tile shader resource.");
                    if (traceTiming)
                    {
                        textureCreateTicks += Stopwatch.GetTimestamp() - textureCreateStarted;
                    }
                    _completedRasterUploads.Enqueue(new CompletedRasterTileUpload(
                        tile.Key,
                        tile.Generation,
                        deviceEpoch,
                        upload.Reservation,
                        tile.SourceKind,
                        completedTexture));
                    completedTexture = null;
                    completedQueued = true;
                    uploadedCount++;
                    RequestRender();
                    renderWakeCount++;
                }
                catch (Exception exception)
                {
                    failedCount++;
                    TraceUploadFailure(
                        tile,
                        operation,
                        exception.GetType().Name,
                        exception.HResult);
                }
                finally
                {
                    if (!completedQueued)
                    {
                        ReleasePendingRasterTile(tile.Key, upload.Reservation);
                        _pendingRasterUploadCapacity.Release();
                    }
                    completedTexture?.Dispose();
                }
            }
        }
        finally
        {
            Marshal.Release(devicePointer);
        }

        DrainTextureDisposals();
        if (!_rasterPixelUploads.IsEmpty)
        {
            _uploadRequested.Set();
        }
        if (uploadedCount != 0 || droppedCount != 0 || failedCount != 0)
        {
            double totalMilliseconds =
                Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            MapControlEventSource.Log.TileUploadSummary(
                uploadedCount,
                droppedCount,
                failedCount,
                totalMilliseconds);
            MapControlEventSource.Log.TileUploadTiming(
                uploadedCount,
                queueStartCount,
                _rasterPixelUploads.Count,
                textureCreateTicks * 1000d / Stopwatch.Frequency,
                renderLockWaitTicks * 1000d / Stopwatch.Frequency,
                totalMilliseconds,
                renderWakeCount);
            MapControlEventSource.Log.TilePipelineBacklog(
                lastGeneration,
                _rasterPixelUploads.Count,
                _completedRasterUploads.Count,
                _textureDisposals.Count,
                MaximumPendingRasterUploads - _pendingRasterUploadCapacity.CurrentCount);
        }
    }

    /// <summary>
    /// Routes sanitized raster upload failure metadata to the Azure or custom-source ETW
    /// event without logging URLs, credentials, or pixel data.
    /// </summary>
    private static void TraceUploadFailure(
        RasterTileData tile,
        string operation,
        string exceptionType,
        int hresult)
    {
        if (tile.SourceKind == RasterSourceKind.Azure)
        {
            MapControlEventSource.Log.TileUploadFailed(
                tile.Key.Id.Zoom,
                tile.Key.Id.X,
                tile.Key.Id.Y,
                tile.Generation,
                operation,
                exceptionType,
                hresult);
        }
        else
        {
            MapControlEventSource.Log.CustomTileUploadFailed(
                tile.Key.Id.Zoom,
                tile.Key.Id.X,
                tile.Key.Id.Y,
                tile.Generation,
                exceptionType,
                hresult);
        }
    }

    /// <summary>
    /// Releases a pending tile reservation under the render-state lock.
    /// </summary>
    private void ReleasePendingRasterTile(RasterTileKey key, long reservation)
    {
        lock (RenderLock)
        {
            _pendingRasterTiles.Release(key, reservation);
        }
    }

    /// <summary>
    /// Draws cached fallback levels before the active raster scene and retires fallbacks once
    /// current coverage is fully opaque.
    /// </summary>
    /// <returns><see langword="true"/> while any drawn tile is still fading.</returns>
    private unsafe bool DrawRasterTileLayer(
        IntPtr context,
        LayerRenderSnapshot layer)
    {
        if (_displayZoom < layer.MinZoom ||
            _displayZoom >= layer.MaxZoom ||
            !_rasterLayers.TryGetValue(layer.RuntimeId, out RasterLayerState? state) ||
            state.Scene is null)
        {
            return false;
        }

        bool canEnumerateActiveScene = CanEnumerateRasterScene(
            _displayZoom,
            state.Scene.TileZoom);
        HashSet<int> cachedLevels = [.. state.FallbackTileZooms];
        if (!canEnumerateActiveScene)
        {
            cachedLevels.Add(state.Scene.TileZoom);
        }

        bool activeFade = DrawCachedRasterLevels(
            context,
            layer,
            cachedLevels);
        if (!canEnumerateActiveScene)
        {
            return activeFade;
        }

        MapScene scene = CreateCurrentRasterScene(state.Scene.TileZoom);
        bool currentFade = DrawRasterScene(context, layer, scene);
        activeFade |= currentFade;
        bool hasOpaqueCoverage = EvaluateAndReportCoverage(
            layer.RuntimeId,
            state,
            scene,
            layer.FadeDuration);
        if (!currentFade && hasOpaqueCoverage)
        {
            state.FallbackTileZooms.Clear();
        }
        return activeFade;
    }

    /// <summary>
    /// Rebuilds a raster scene at a source zoom from the current animated camera and
    /// viewport.
    /// </summary>
    private MapScene CreateCurrentRasterScene(int tileZoom) =>
        MapCamera.CreateScene(
            _displayLongitude,
            _displayLatitude,
            _displayZoom,
            tileZoom,
            _viewportWidth,
            _viewportHeight,
            _displayHeading,
            _displayPitch);

    /// <summary>
    /// Draws every cached tile instance in a scene and reports whether any fade remains
    /// active.
    /// </summary>
    private unsafe bool DrawRasterScene(
        IntPtr context,
        LayerRenderSnapshot layer,
        MapScene scene)
    {
        bool activeFade = false;
        foreach (VisibleTile visibleTile in scene.VisibleTiles)
        {
            if (!_rasterTiles.TryGetValue(
                    new RasterTileKey(layer.RuntimeId, visibleTile.Id),
                    out TileTexture? texture))
            {
                continue;
            }
            activeFade |= DrawRasterTile(context, layer, visibleTile, texture);
        }
        return activeFade;
    }

    /// <summary>
    /// Enumerates visible wrapped instances of retained zoom levels and draws them from lower
    /// to higher source zoom.
    /// </summary>
    private unsafe bool DrawCachedRasterLevels(
        IntPtr context,
        LayerRenderSnapshot layer,
        IReadOnlySet<int> tileZooms)
    {
        if (tileZooms.Count == 0)
        {
            return false;
        }

        SortedDictionary<int, List<CachedRasterTileDraw>> levels = [];
        foreach ((RasterTileKey key, TileTexture texture) in _rasterTiles)
        {
            if (key.SourceId != layer.RuntimeId || !tileZooms.Contains(key.Id.Zoom))
            {
                continue;
            }
            IReadOnlyList<VisibleTile> instances = GetVisibleCachedTileInstances(
                key.Id,
                _displayLongitude,
                _displayLatitude,
                _displayZoom,
                _viewportWidth,
                _viewportHeight,
                _displayHeading,
                _displayPitch);
            if (instances.Count == 0)
            {
                continue;
            }
            if (!levels.TryGetValue(key.Id.Zoom, out List<CachedRasterTileDraw>? draws))
            {
                draws = [];
                levels.Add(key.Id.Zoom, draws);
            }
            draws.AddRange(instances.Select(instance =>
                new CachedRasterTileDraw(instance, texture)));
        }

        bool activeFade = false;
        foreach (List<CachedRasterTileDraw> draws in levels.Values)
        {
            foreach (CachedRasterTileDraw draw in draws)
            {
                activeFade |= DrawRasterTile(context, layer, draw.Tile, draw.Texture);
            }
        }
        return activeFade;
    }

    /// <summary>
    /// Updates one tile's cache-use timestamp, applies fade and layer opacity, and issues its
    /// indexed draw.
    /// </summary>
    /// <returns><see langword="true"/> when the tile has not reached full layer opacity.</returns>
    private unsafe bool DrawRasterTile(
        IntPtr context,
        LayerRenderSnapshot layer,
        VisibleTile visibleTile,
        TileTexture texture)
    {
        texture.MarkUsed();
        double opacity = ComputeLayerTileOpacity(
            Stopwatch.GetElapsedTime(texture.ReadyTimestamp),
            layer.FadeDuration,
            layer.Opacity);
        TileConstants constants = CreateTileConstants(visibleTile, (float)opacity);
        UpdateSubresource(context, _constantBufferPointer, &constants);
        SetPixelShader(
            context,
            _pixelShaderPointer,
            texture.ViewPointer,
            _samplerPointer,
            _constantBufferPointer);
        DrawIndexed(context);
        return opacity < layer.Opacity;
    }

    /// <summary>
    /// Combines clamped layer opacity with linear tile fade progress.
    /// </summary>
    internal static double ComputeLayerTileOpacity(
        TimeSpan elapsed,
        TimeSpan fadeDuration,
        double layerOpacity)
    {
        double fade = fadeDuration <= TimeSpan.Zero
            ? 1
            : Math.Clamp(elapsed.TotalMilliseconds / fadeDuration.TotalMilliseconds, 0, 1);
        return Math.Clamp(layerOpacity, 0, 1) * fade;
    }

    /// <summary>
    /// Determines whether every required in-bounds tile is cached and older than the fade
    /// duration.
    /// </summary>
    private bool EvaluateAndReportCoverage(
        long sourceId,
        RasterLayerState state,
        MapScene scene,
        TimeSpan fadeDuration)
    {
        TileId[] required = scene.RequiredTiles.Where(state.IncludesTile).ToArray();
        int coveredCount = 0;
        int opaqueCount = 0;
        foreach (TileId id in required)
        {
            if (!_rasterTiles.TryGetValue(new RasterTileKey(sourceId, id), out TileTexture? tile))
            {
                continue;
            }
            coveredCount++;
            if (Stopwatch.GetElapsedTime(tile.ReadyTimestamp) >= fadeDuration)
            {
                opaqueCount++;
            }
        }

        if (required.Length == 0)
        {
            return false;
        }

        bool traceCoverage = MapControlEventSource.Log.IsEnabled(
            System.Diagnostics.Tracing.EventLevel.Informational,
            MapControlEventSource.Keywords.Tiles |
                MapControlEventSource.Keywords.Device |
                MapControlEventSource.Keywords.Cache);
        if (traceCoverage)
        {
            if (!state.FirstCoverageReported && coveredCount > 0)
            {
                state.FirstCoverageReported = true;
                TraceCoverageMilestone(state, "FirstTile", required.Length, coveredCount, opaqueCount);
            }
            if (!state.FullCoverageReported && coveredCount == required.Length)
            {
                state.FullCoverageReported = true;
                TraceCoverageMilestone(state, "FullCoverage", required.Length, coveredCount, opaqueCount);
            }
            if (!state.OpaqueCoverageReported && opaqueCount == required.Length)
            {
                state.OpaqueCoverageReported = true;
                TraceCoverageMilestone(state, "OpaqueCoverage", required.Length, coveredCount, opaqueCount);
            }
        }
        return opaqueCount == required.Length;
    }

    /// <summary>
    /// Emits one sanitized viewport-coverage milestone for the current source scene.
    /// </summary>
    private void TraceCoverageMilestone(
        RasterLayerState state,
        string milestone,
        int requiredCount,
        int coveredCount,
        int opaqueCount)
    {
        ulong cacheBytes = _rasterTiles.Values.Aggregate<TileTexture, ulong>(
            0,
            (total, texture) => total + texture.ByteSize);
        MapControlEventSource.Log.RasterCoverageMilestone(
            (int)state.SourceKind,
            state.Generation,
            state.SceneVersion,
            milestone,
            requiredCount,
            coveredCount,
            opaqueCount,
            Stopwatch.GetElapsedTime(state.CoverageStartTimestamp).TotalMilliseconds,
            checked((long)cacheBytes),
            _rasterTiles.Count);
    }

    /// <summary>
    /// Replaces a source's fallback-level set with distinct cached levels other than the new
    /// active zoom.
    /// </summary>
    private void UpdateCachedFallbackTileZooms(
        long sourceId,
        RasterLayerState state,
        int activeTileZoom)
    {
        IEnumerable<int> loadedLevels = _rasterTiles.Keys
            .Where(key => key.SourceId == sourceId)
            .Select(key => key.Id.Zoom);
        state.FallbackTileZooms.Clear();
        state.FallbackTileZooms.UnionWith(
            SelectFallbackTileZooms(loadedLevels, activeTileZoom));
    }

    /// <summary>
    /// Determines whether two tile pyramid cells overlap after scaling them to a common zoom.
    /// </summary>
    internal static bool TilesOverlap(TileId first, TileId second)
    {
        int commonZoom = Math.Max(first.Zoom, second.Zoom);
        long firstScale = 1L << (commonZoom - first.Zoom);
        long secondScale = 1L << (commonZoom - second.Zoom);
        long firstLeft = first.X * firstScale;
        long firstTop = first.Y * firstScale;
        long secondLeft = second.X * secondScale;
        long secondTop = second.Y * secondScale;
        return firstLeft < secondLeft + secondScale &&
            secondLeft < firstLeft + firstScale &&
            firstTop < secondTop + secondScale &&
            secondTop < firstTop + firstScale;
    }

    /// <summary>
    /// Selects distinct valid cached zoom levels, excluding the active level, in ascending
    /// render order.
    /// </summary>
    internal static IReadOnlyList<int> SelectFallbackTileZooms(
        IEnumerable<int> candidates,
        int activeTileZoom) =>
        candidates
            .Where(tileZoom =>
                tileZoom >= 0 &&
                tileZoom <= MapCamera.MaximumTileZoom &&
                tileZoom != activeTileZoom)
            .Distinct()
            .Order()
            .Take(MaximumFallbackTileLevels)
            .ToArray();

    /// <summary>
    /// Determines whether a source zoom is valid and close enough to display zoom for bounded
    /// scene enumeration.
    /// </summary>
    internal static bool CanEnumerateRasterScene(double displayZoom, int tileZoom)
    {
        double normalizedZoom = double.IsFinite(displayZoom)
            ? Math.Clamp(displayZoom, 0, MapCamera.MaximumTileZoom)
            : 0;
        return tileZoom >= 0 &&
            tileZoom <= MapCamera.MaximumTileZoom &&
            tileZoom <= (int)Math.Floor(normalizedZoom) + MaximumSourceZoomOffset;
    }

    /// <summary>
    /// Computes every horizontally wrapped viewport instance of one valid cached tile.
    /// </summary>
    /// <remarks>
    /// Invalid camera, viewport, or tile input returns an empty result rather than producing
    /// unbounded wrap enumeration.
    /// </remarks>
    internal static IReadOnlyList<VisibleTile> GetVisibleCachedTileInstances(
        TileId id,
        double longitude,
        double latitude,
        double displayZoom,
        double viewportWidth,
        double viewportHeight,
        double heading = 0,
        double pitch = 0)
    {
        if (id.Zoom is < 0 or > MapCamera.MaximumTileZoom ||
            id.X < 0 ||
            id.Y < 0 ||
            id.X >= 1 << id.Zoom ||
            id.Y >= 1 << id.Zoom ||
            !double.IsFinite(longitude) ||
            !double.IsFinite(latitude) ||
            !double.IsFinite(displayZoom) ||
            !double.IsFinite(viewportWidth) ||
            !double.IsFinite(viewportHeight) ||
            viewportWidth <= 0 ||
            viewportHeight <= 0)
        {
            return [];
        }

        double normalizedZoom = Math.Clamp(displayZoom, 0, MapCamera.MaximumTileZoom);
        double tileDisplaySize = 256 * Math.Pow(2, normalizedZoom - id.Zoom);
        double worldDisplaySize = 256 * Math.Pow(2, normalizedZoom);
        double centerX = MapCamera.LongitudeToWorldX(longitude) * worldDisplaySize;
        double centerY = MapCamera.LatitudeToWorldY(latitude) * worldDisplaySize;
        centerY = MapCamera.GetEffectiveCameraY(centerY, worldDisplaySize);
        MapCamera.GetMapPlaneViewportBounds(
            viewportWidth,
            viewportHeight,
            heading,
            pitch,
            out double minimumX,
            out double minimumY,
            out double maximumX,
            out double maximumY);
        double viewportWorldLeft = centerX - (viewportWidth / 2);
        double viewportWorldTop = centerY - (viewportHeight / 2);
        double baseLeft = (id.X * tileDisplaySize) - viewportWorldLeft;
        double top = (id.Y * tileDisplaySize) - viewportWorldTop;
        double baseLeftOffset = baseLeft - (viewportWidth / 2);
        double topOffset = top - (viewportHeight / 2);
        if (topOffset >= maximumY || topOffset + tileDisplaySize <= minimumY)
        {
            return [];
        }

        double firstWrapValue = Math.Floor(
            (minimumX - baseLeftOffset - tileDisplaySize) / worldDisplaySize) + 1;
        double lastWrapValue = Math.Ceiling(
            (maximumX - baseLeftOffset) / worldDisplaySize) - 1;
        if (!double.IsFinite(firstWrapValue) ||
            !double.IsFinite(lastWrapValue) ||
            firstWrapValue < long.MinValue ||
            firstWrapValue > long.MaxValue ||
            lastWrapValue < long.MinValue ||
            lastWrapValue > long.MaxValue)
        {
            return [];
        }
        long firstWrap = (long)firstWrapValue;
        long lastWrap = (long)lastWrapValue;
        List<VisibleTile> visibleTiles = [];
        int tileCount = 1 << id.Zoom;
        for (long wrap = firstWrap; wrap <= lastWrap; wrap++)
        {
            double left = baseLeft + (wrap * worldDisplaySize);
            double leftOffset = left - (viewportWidth / 2);
            if (leftOffset >= maximumX ||
                leftOffset + tileDisplaySize <= minimumX)
            {
                continue;
            }
            long worldX = id.X + (wrap * tileCount);
            if (worldX is < int.MinValue or > int.MaxValue)
            {
                continue;
            }
            visibleTiles.Add(new VisibleTile(
                id,
                (int)worldX,
                left,
                top,
                tileDisplaySize));
        }
        return visibleTiles;
    }

    /// <summary>
    /// Evicts least-recently-used raster textures until the viewport-aware byte budget is
    /// met, preferring entries not needed for active coverage or visible fallbacks.
    /// </summary>
    /// <remarks>
    /// Protected entries are evicted only if all unprotected entries are insufficient.
    /// Texture destruction is deferred to the upload worker to preserve ownership ordering.
    /// </remarks>
    private void TrimRasterTileCache()
    {
        ulong cacheBytes = _rasterTiles.Values.Aggregate<TileTexture, ulong>(
            0,
            (total, texture) => total + texture.ByteSize);
        Dictionary<long, LayerRenderSnapshot> eligibleLayers = _layerRenderPlan
            .Where(layer =>
                layer.Kind == LayerRenderKind.RasterTiles &&
                layer.IsVisible &&
                layer.Opacity > 0 &&
                _displayZoom >= layer.MinZoom &&
                _displayZoom < layer.MaxZoom)
            .ToDictionary(layer => layer.RuntimeId);
        HashSet<RasterTileKey> protectedKeys = [];
        foreach ((long sourceId, RasterLayerState state) in _rasterLayers)
        {
            if (!eligibleLayers.TryGetValue(sourceId, out LayerRenderSnapshot layer) ||
                state.Scene is null)
            {
                continue;
            }
            if (!CanEnumerateRasterScene(_displayZoom, state.Scene.TileZoom))
            {
                HashSet<int> visibleLevels = [.. state.FallbackTileZooms, state.Scene.TileZoom];
                protectedKeys.UnionWith(_rasterTiles.Keys.Where(key =>
                    key.SourceId == sourceId &&
                    visibleLevels.Contains(key.Id.Zoom) &&
                    GetVisibleCachedTileInstances(
                        key.Id,
                        _displayLongitude,
                        _displayLatitude,
                        _displayZoom,
                        _viewportWidth,
                        _viewportHeight,
                        _displayHeading,
                        _displayPitch).Count != 0));
                continue;
            }

            MapScene activeScene = CreateCurrentRasterScene(state.Scene.TileZoom);
            TileId[] required = activeScene.RequiredTiles.Where(state.IncludesTile).ToArray();
            protectedKeys.UnionWith(
                required.Select(id => new RasterTileKey(sourceId, id)));
            TileId[] incomplete = required
                .Where(id =>
                    !_rasterTiles.TryGetValue(
                        new RasterTileKey(sourceId, id),
                        out TileTexture? tile) ||
                    Stopwatch.GetElapsedTime(tile.ReadyTimestamp) < layer.FadeDuration)
                .ToArray();
            if (incomplete.Length == 0)
            {
                continue;
            }
            protectedKeys.UnionWith(_rasterTiles.Keys.Where(key =>
                key.SourceId == sourceId &&
                state.FallbackTileZooms.Contains(key.Id.Zoom) &&
                incomplete.Any(active => TilesOverlap(key.Id, active)) &&
                GetVisibleCachedTileInstances(
                    key.Id,
                    _displayLongitude,
                    _displayLatitude,
                    _displayZoom,
                    _viewportWidth,
                    _viewportHeight,
                    _displayHeading,
                    _displayPitch).Count != 0));
        }

        ulong protectedBytes = _rasterTiles
            .Where(pair => protectedKeys.Contains(pair.Key))
            .Aggregate<KeyValuePair<RasterTileKey, TileTexture>, ulong>(
                0,
                (total, pair) => total + pair.Value.ByteSize);
        ulong cacheBudget = ComputeRasterCacheBudget(protectedBytes);
        if (cacheBytes <= cacheBudget)
        {
            _lastReportedRasterCachePressureBytes = 0;
            return;
        }

        if (cacheBytes != _lastReportedRasterCachePressureBytes)
        {
            _lastReportedRasterCachePressureBytes = cacheBytes;
            MapControlEventSource.Log.TileCachePressure(
                checked((long)cacheBytes),
                checked((long)cacheBudget),
                _rasterTiles.Count);
        }

        int evictedCount = 0;
        int customEvictedCount = 0;
        ulong evictedBytes = 0;
        RasterTileKey[] evictionOrder = _rasterTiles
            .Where(pair => !protectedKeys.Contains(pair.Key))
            .OrderBy(pair => pair.Value.LastUsedTimestamp)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (RasterTileKey key in evictionOrder)
        {
            if (!_rasterTiles.TryGetValue(key, out TileTexture? texture))
            {
                continue;
            }
            _rasterTiles.Remove(key);
            QueueTextureDisposal(texture);
            cacheBytes -= texture.ByteSize;
            evictedBytes += texture.ByteSize;
            evictedCount++;
            if (_rasterLayers.TryGetValue(key.SourceId, out RasterLayerState? state) &&
                state.SourceKind == RasterSourceKind.Custom)
            {
                customEvictedCount++;
            }
            if (cacheBytes <= cacheBudget)
            {
                break;
            }
        }
        if (evictedCount != 0)
        {
            foreach ((long sourceId, RasterLayerState state) in _rasterLayers)
            {
                HashSet<int> retainedLevels = _rasterTiles.Keys
                    .Where(key => key.SourceId == sourceId)
                    .Select(key => key.Id.Zoom)
                    .ToHashSet();
                state.FallbackTileZooms.IntersectWith(retainedLevels);
            }
            MapControlEventSource.Log.TileCacheEvicted(
                evictedCount,
                checked((long)evictedBytes),
                checked((long)cacheBytes));
            if (customEvictedCount != 0)
            {
                long customBytes = checked((long)_rasterTiles
                    .Where(pair =>
                        _rasterLayers.TryGetValue(
                            pair.Key.SourceId,
                            out RasterLayerState? state) &&
                        state.SourceKind == RasterSourceKind.Custom)
                    .Aggregate<KeyValuePair<RasterTileKey, TileTexture>, ulong>(
                        0,
                        (total, pair) => total + pair.Value.ByteSize));
                MapControlEventSource.Log.CustomTileCacheSummary(
                    _rasterTiles.Keys.Count(key =>
                        _rasterLayers.TryGetValue(
                            key.SourceId,
                            out RasterLayerState? state) &&
                        state.SourceKind == RasterSourceKind.Custom),
                    customEvictedCount,
                    customBytes);
            }
        }
    }

    /// <summary>
    /// Computes a bounded cache budget that preserves current viewport and fallback textures
    /// plus a small least-recently-used navigation history.
    /// </summary>
    internal static ulong ComputeRasterCacheBudget(ulong protectedBytes)
    {
        if (protectedBytes >= MaximumRasterCacheBytes - RasterCacheHistoryBytes)
        {
            return Math.Max(protectedBytes, MaximumRasterCacheBytes);
        }

        return Math.Clamp(
            protectedBytes + RasterCacheHistoryBytes,
            MinimumRasterCacheBytes,
            MaximumRasterCacheBytes);
    }

    /// <summary>
    /// Builds shader constants for a square visible tile instance.
    /// </summary>
    private TileConstants CreateTileConstants(VisibleTile tile, float opacity)
    {
        TileConstants constants =
            CreateQuadConstants(tile.Left, tile.Top, tile.Size, tile.Size, opacity);
        double radians = MapCamera.NormalizeHeading(_displayHeading) * Math.PI / 180;
        float cosine = (float)Math.Cos(radians);
        float sine = (float)Math.Sin(radians);
        double pitchRadians = MapCamera.NormalizePitch(_displayPitch) * Math.PI / 180;
        return constants with
        {
            Rotation = new Vector4(
                cosine,
                (float)(-(Viewport.Height / Viewport.Width) * sine),
                (float)((Viewport.Width / Viewport.Height) * sine),
                cosine),
            Pitch = new Vector4(
                (float)Math.Cos(pitchRadians),
                (float)Math.Sin(pitchRadians),
                (float)MapCamera.GetPerspectiveDistance(Viewport.Height),
                (float)(Viewport.Height / 2)),
        };
    }

    /// <summary>
    /// Removes all cached textures for a source and transfers them to deferred disposal.
    /// </summary>
    /// <remarks>The caller must hold <see cref="DirectXRenderer.RenderLock"/>.</remarks>
    private void RemoveRasterTilesLocked(long sourceId)
    {
        foreach (RasterTileKey key in _rasterTiles.Keys
            .Where(key => key.SourceId == sourceId)
            .ToArray())
        {
            QueueTextureDisposal(_rasterTiles[key]);
            _rasterTiles.Remove(key);
        }
    }

    /// <summary>
    /// Disposes every raster texture, clears source and reservation state, and asynchronously
    /// notifies resource owners that device-backed tiles were invalidated.
    /// </summary>
    private void ReleaseRasterTileTextures()
    {
        foreach (TileTexture texture in _rasterTiles.Values)
        {
            texture.Dispose();
        }
        _rasterTiles.Clear();
        _rasterLayers.Clear();
        _pendingRasterTiles.Clear();
        ThreadPool.QueueUserWorkItem(
            static state => ((MapRenderer)state!).RasterTileResourcesInvalidated?.Invoke(),
            this,
            preferLocal: false);
    }

    /// <summary>
    /// Transfers texture ownership to the upload worker's disposal queue and wakes it.
    /// </summary>
    private void QueueTextureDisposal(TileTexture texture)
    {
        _textureDisposals.Enqueue(texture);
        _uploadRequested.Set();
    }

    /// <summary>
    /// Disposes all queued textures on the upload worker and emits aggregate, content-free
    /// disposal telemetry.
    /// </summary>
    private void DrainTextureDisposals()
    {
        int disposedCount = 0;
        ulong disposedBytes = 0;
        while (_textureDisposals.TryDequeue(out TileTexture? texture))
        {
            disposedCount++;
            disposedBytes += texture.ByteSize;
            texture.Dispose();
        }
        if (disposedCount != 0 &&
            MapControlEventSource.Log.IsEnabled(
                System.Diagnostics.Tracing.EventLevel.Informational,
                MapControlEventSource.Keywords.Device | MapControlEventSource.Keywords.Cache))
        {
            MapControlEventSource.Log.TextureDisposalSummary(
                disposedCount,
                checked((long)disposedBytes),
                _textureDisposals.Count);
        }
    }

    /// <summary>
    /// Creates an immutable BGRA texture and shader-resource view and transfers both COM
    /// objects to a cache-owned <see cref="TileTexture"/>.
    /// </summary>
    /// <remarks>
    /// Partially created resources remain locally owned and are released if construction
    /// fails.
    /// </remarks>
    private static unsafe TileTexture CreateTileTexture(
        IntPtr devicePointer,
        byte[] pixels,
        uint width,
        uint height,
        string viewFailureMessage)
    {
        IntPtr texturePointer = IntPtr.Zero;
        IntPtr viewPointer = IntPtr.Zero;
        try
        {
            D3D11_TEXTURE2D_DESC description = new()
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new() { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            };
            fixed (byte* pixelPointer = pixels)
            {
                D3D11_SUBRESOURCE_DATA data = new()
                {
                    pSysMem = pixelPointer,
                    SysMemPitch = width * 4,
                };
                texturePointer = CreateTexture(devicePointer, &description, &data);
                viewPointer = CreateView(
                    devicePointer,
                    texturePointer,
                    7,
                    viewFailureMessage);
            }

            TileTexture result = new(texturePointer, viewPointer, width, height);
            texturePointer = IntPtr.Zero;
            viewPointer = IntPtr.Zero;
            return result;
        }
        finally
        {
            ReleasePointer(ref viewPointer);
            ReleasePointer(ref texturePointer);
        }
    }

    /// <summary>
    /// Holds render-lock-protected generation, scene, source-kind, coverage predicate, and
    /// retained fallback levels for one raster source.
    /// </summary>
    private sealed class RasterLayerState
    {
        internal long Generation;
        internal long SceneVersion;
        internal long CoverageStartTimestamp;
        internal bool FirstCoverageReported;
        internal bool FullCoverageReported;
        internal bool OpaqueCoverageReported;
        internal MapScene? Scene;
        internal Func<TileId, bool> IncludesTile = static _ => true;
        internal RasterSourceKind SourceKind;
        internal HashSet<int> FallbackTileZooms { get; } = [];
    }

    /// <summary>
    /// Pairs one wrapped visible tile instance with the cached texture used to draw it.
    /// </summary>
    private readonly record struct CachedRasterTileDraw(
        VisibleTile Tile,
        TileTexture Texture);

    /// <summary>
    /// Transfers an upload-thread-created raster texture to the render thread with the
    /// generation, reservation, source kind, and device epoch needed for validation.
    /// </summary>
    private readonly record struct CompletedRasterTileUpload(
        RasterTileKey Key,
        long Generation,
        int DeviceEpoch,
        long Reservation,
        RasterSourceKind SourceKind,
        TileTexture Texture);
}
