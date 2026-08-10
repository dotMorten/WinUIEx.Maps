using System.Diagnostics.Tracing;

namespace WinUIEx.Maps.Rendering.Diagnostics;

/// <summary>
/// Defines the stable ETW schema for map-control lifecycle, rendering, cache, and pipeline
/// diagnostics.
/// </summary>
/// <remarks>
/// <para>
/// UI, manager-worker, upload, render, and thread-pool paths all write through the single
/// process-wide <see cref="Log"/> instance. Event methods preserve fixed identifiers,
/// levels, keywords, tasks, and start/stop opcodes so a trace can correlate snapshot
/// publication, generations, cancellation, queue pressure, device epochs, cache activity,
/// drawing, and disposal without changing synchronization or ownership.
/// </para>
/// <para>
/// High-frequency and expensive events are level/keyword guarded at the call site or inside
/// the event method. Per-item diagnostics are reserved for failures; normal operation uses
/// durations and aggregate counts so tracing does not become part of the render critical
/// path.
/// </para>
/// Event payloads are intentionally limited to identifiers, dimensions, coordinates,
/// categories, durations, and aggregate counts. Callers must never pass credentials, request
/// URLs, response bodies, or pixel data through string fields.
/// </remarks>
[EventSource(Name = ProviderName)]
internal sealed class MapControlEventSource : EventSource
{
    internal const string ProviderName = "WinUIEx-Maps-Rendering";
    internal static readonly MapControlEventSource Log = new();

    /// <summary>
    /// Defines stable ETW keyword bits used to select lifecycle, device, camera, tile, cache,
    /// icon, error, and custom-source diagnostics.
    /// </summary>
    public static class Keywords
    {
        public const EventKeywords Lifecycle = (EventKeywords)0x1;
        public const EventKeywords Device = (EventKeywords)0x2;
        public const EventKeywords Camera = (EventKeywords)0x4;
        public const EventKeywords Tiles = (EventKeywords)0x8;
        public const EventKeywords Cache = (EventKeywords)0x10;
        public const EventKeywords Icons = (EventKeywords)0x20;
        public const EventKeywords Errors = (EventKeywords)0x40;
        public const EventKeywords CustomTiles = (EventKeywords)0x80;
    }

    /// <summary>
    /// Defines stable ETW task identifiers that group events by control, device, camera,
    /// request, upload, cache, icon, and custom-tile workflow.
    /// </summary>
    public static class Tasks
    {
        public const EventTask Control = (EventTask)1;
        public const EventTask Device = (EventTask)2;
        public const EventTask Camera = (EventTask)3;
        public const EventTask TileWave = (EventTask)4;
        public const EventTask TileRequest = (EventTask)5;
        public const EventTask TileUpload = (EventTask)6;
        public const EventTask Cache = (EventTask)7;
        public const EventTask Icons = (EventTask)8;
        public const EventTask CustomTiles = (EventTask)9;
    }

    /// <summary>
    /// Initializes the process-wide event provider while preventing additional instances.
    /// </summary>
    private MapControlEventSource()
    {
    }

    /// <summary>
    /// Records creation of a map control instance.
    /// </summary>
    [Event(1, Level = EventLevel.Informational, Keywords = Keywords.Lifecycle, Task = Tasks.Control)]
    public void ControlCreated() => WriteEvent(1);

    /// <summary>
    /// Records that a map control entered the loaded visual tree.
    /// </summary>
    [Event(2, Level = EventLevel.Informational, Keywords = Keywords.Lifecycle, Task = Tasks.Control)]
    public void ControlLoaded() => WriteEvent(2);

    /// <summary>
    /// Records that a map control left the loaded visual tree.
    /// </summary>
    [Event(3, Level = EventLevel.Informational, Keywords = Keywords.Lifecycle, Task = Tasks.Control)]
    public void ControlUnloaded() => WriteEvent(3);

    /// <summary>
    /// Retains the former control-disposal event ID for schema compatibility.
    /// </summary>
    /// <remarks>MapControl no longer has a terminal disposed state, so this event is not emitted.</remarks>
    [Event(4, Level = EventLevel.Informational, Keywords = Keywords.Lifecycle, Task = Tasks.Control)]
    public void ControlDisposed() => WriteEvent(4);

    /// <summary>
    /// Starts a paired event for device-resource creation at a renderer surface size.
    /// </summary>
    [Event(
        5,
        Level = EventLevel.Informational,
        Keywords = Keywords.Device,
        Task = Tasks.Device,
        Opcode = EventOpcode.Start)]
    public void DeviceResourcesCreateStart(string rendererType, int width, int height)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Device))
        {
            WriteEvent(5, rendererType ?? string.Empty, width, height);
        }
    }

    /// <summary>
    /// Stops the device-resource creation event with elapsed time and success state.
    /// </summary>
    [Event(
        6,
        Level = EventLevel.Informational,
        Keywords = Keywords.Device,
        Task = Tasks.Device,
        Opcode = EventOpcode.Stop)]
    public void DeviceResourcesCreateStop(
        string rendererType,
        double durationMilliseconds,
        bool succeeded)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Device))
        {
            WriteEvent(6, rendererType ?? string.Empty, durationMilliseconds, succeeded);
        }
    }

    /// <summary>
    /// Records release of renderer device resources and the texture counts they owned.
    /// </summary>
    [Event(7, Level = EventLevel.Informational, Keywords = Keywords.Device, Task = Tasks.Device)]
    public void DeviceResourcesReleased(string rendererType, int tileTextures, int iconTextures)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Device))
        {
            WriteEvent(7, rendererType ?? string.Empty, tileTextures, iconTextures);
        }
    }

    /// <summary>
    /// Records sanitized renderer operation, exception type, and HRESULT metadata.
    /// </summary>
    [Event(
        8,
        Level = EventLevel.Error,
        Keywords = Keywords.Device | Keywords.Errors,
        Task = Tasks.Device)]
    public void RendererFailure(string operation, string exceptionType, int hresult)
    {
        if (IsEnabled(EventLevel.Error, Keywords.Device | Keywords.Errors))
        {
            WriteEvent(8, operation ?? string.Empty, exceptionType ?? string.Empty, hresult);
        }
    }

    /// <summary>
    /// Records a rate-limited camera target and whether zoom anchoring is active.
    /// </summary>
    [Event(9, Level = EventLevel.Informational, Keywords = Keywords.Camera, Task = Tasks.Camera)]
    public void CameraTargetChanged(
        double longitude,
        double latitude,
        double zoom,
        double viewportWidth,
        double viewportHeight,
        bool hasZoomAnchor)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Camera))
        {
            WriteEvent(
                9,
                longitude,
                latitude,
                zoom,
                viewportWidth,
                viewportHeight,
                hasZoomAnchor);
        }
    }

    /// <summary>
    /// Records verbose camera-scene state and its aggregate required-tile count.
    /// </summary>
    [Event(10, Level = EventLevel.Verbose, Keywords = Keywords.Camera, Task = Tasks.Camera)]
    public void SceneChanged(
        int tileZoom,
        int requiredTileCount,
        double longitude,
        double latitude,
        double zoom)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Camera))
        {
            WriteEvent(10, tileZoom, requiredTileCount, longitude, latitude, zoom);
        }
    }

    /// <summary>
    /// Starts a paired Azure tile wave with generation, scene, cache, and batch metadata.
    /// </summary>
    [Event(
        11,
        Level = EventLevel.Informational,
        Keywords = Keywords.Tiles,
        Task = Tasks.TileWave,
        Opcode = EventOpcode.Start)]
    public void TileWaveStart(
        long generation,
        long sceneVersion,
        int style,
        int tileZoom,
        int requiredCount,
        int cacheHitCount,
        int pendingCount,
        int batchCount)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Tiles))
        {
            WriteEvent(
                11,
                generation,
                sceneVersion,
                style,
                tileZoom,
                requiredCount,
                cacheHitCount,
                pendingCount,
                batchCount);
        }
    }

    /// <summary>
    /// Stops an Azure tile wave with duration and aggregate outcome counts.
    /// </summary>
    [Event(
        12,
        Level = EventLevel.Informational,
        Keywords = Keywords.Tiles,
        Task = Tasks.TileWave,
        Opcode = EventOpcode.Stop)]
    public void TileWaveStop(
        long generation,
        long sceneVersion,
        double durationMilliseconds,
        int completedCount,
        int failedCount,
        int canceledCount,
        int remainingCount)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Tiles))
        {
            WriteEvent(
                12,
                generation,
                sceneVersion,
                durationMilliseconds,
                completedCount,
                failedCount,
                canceledCount,
                remainingCount);
        }
    }

    /// <summary>
    /// Records a failed Azure tile request using coordinates and sanitized failure
    /// categories, without request URLs or credentials.
    /// </summary>
    [Event(
        13,
        Level = EventLevel.Error,
        Keywords = Keywords.Tiles | Keywords.Errors,
        Task = Tasks.TileRequest)]
    public void TileRequestFailed(
        int zoom,
        int x,
        int y,
        int style,
        long generation,
        int statusCode,
        string failureKind,
        string exceptionType)
    {
        if (IsEnabled(EventLevel.Error, Keywords.Tiles | Keywords.Errors))
        {
            WriteEvent(
                13,
                zoom,
                x,
                y,
                style,
                generation,
                statusCode,
                failureKind ?? string.Empty,
                exceptionType ?? string.Empty);
        }
    }

    /// <summary>
    /// Records a failed attribution request using style, zoom, status, and sanitized failure
    /// categories.
    /// </summary>
    [Event(
        14,
        Level = EventLevel.Error,
        Keywords = Keywords.Tiles | Keywords.Errors,
        Task = Tasks.TileRequest)]
    public void AttributionRequestFailed(
        int style,
        int zoom,
        int statusCode,
        string failureKind,
        string exceptionType)
    {
        if (IsEnabled(EventLevel.Error, Keywords.Tiles | Keywords.Errors))
        {
            WriteEvent(
                14,
                style,
                zoom,
                statusCode,
                failureKind ?? string.Empty,
                exceptionType ?? string.Empty);
        }
    }

    /// <summary>
    /// Records cancellation of a tile generation with a fixed lifecycle reason.
    /// </summary>
    [Event(
        15,
        Level = EventLevel.Informational,
        Keywords = Keywords.Tiles,
        Task = Tasks.TileRequest)]
    public void TileRequestsCanceled(long generation, string reason)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Tiles))
        {
            WriteEvent(15, generation, reason ?? string.Empty);
        }
    }

    /// <summary>
    /// Records a GPU upload failure for a tile using operation and exception metadata only.
    /// </summary>
    [Event(
        16,
        Level = EventLevel.Error,
        Keywords = Keywords.Tiles | Keywords.Device | Keywords.Errors,
        Task = Tasks.TileUpload)]
    public void TileUploadFailed(
        int zoom,
        int x,
        int y,
        long generation,
        string operation,
        string exceptionType,
        int hresult)
    {
        if (IsEnabled(
            EventLevel.Error,
            Keywords.Tiles | Keywords.Device | Keywords.Errors))
        {
            WriteEvent(
                16,
                zoom,
                x,
                y,
                generation,
                operation ?? string.Empty,
                exceptionType ?? string.Empty,
                hresult);
        }
    }

    /// <summary>
    /// Records aggregate raster upload outcomes and processing duration.
    /// </summary>
    [Event(
        17,
        Level = EventLevel.Informational,
        Keywords = Keywords.Tiles | Keywords.Device,
        Task = Tasks.TileUpload)]
    public void TileUploadSummary(
        int uploadedCount,
        int droppedCount,
        int failedCount,
        double durationMilliseconds)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Tiles | Keywords.Device))
        {
            WriteEvent(17, uploadedCount, droppedCount, failedCount, durationMilliseconds);
        }
    }

    /// <summary>
    /// Records aggregate required, hit, pending-deduplicated, and missing tile counts.
    /// </summary>
    [Event(18, Level = EventLevel.Informational, Keywords = Keywords.Cache, Task = Tasks.Cache)]
    public void TileCacheLookupSummary(
        int requiredCount,
        int hitCount,
        int pendingDedupCount,
        int missCount)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Cache))
        {
            WriteEvent(18, requiredCount, hitCount, pendingDedupCount, missCount);
        }
    }

    /// <summary>
    /// Records raster cache size when it exceeds its byte budget.
    /// </summary>
    [Event(19, Level = EventLevel.Warning, Keywords = Keywords.Cache, Task = Tasks.Cache)]
    public void TileCachePressure(long cacheBytes, long budgetBytes, int entryCount)
    {
        if (IsEnabled(EventLevel.Warning, Keywords.Cache))
        {
            WriteEvent(19, cacheBytes, budgetBytes, entryCount);
        }
    }

    /// <summary>
    /// Records aggregate raster cache eviction count and byte totals.
    /// </summary>
    [Event(20, Level = EventLevel.Informational, Keywords = Keywords.Cache, Task = Tasks.Cache)]
    public void TileCacheEvicted(int entryCount, long bytesEvicted, long remainingBytes)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Cache))
        {
            WriteEvent(20, entryCount, bytesEvicted, remainingBytes);
        }
    }

    /// <summary>
    /// Records aggregate icon instances and textures in a published snapshot.
    /// </summary>
    [Event(21, Level = EventLevel.Informational, Keywords = Keywords.Icons, Task = Tasks.Icons)]
    public void IconSnapshotPublished(int instanceCount, int textureCount)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Icons))
        {
            WriteEvent(21, instanceCount, textureCount);
        }
    }

    /// <summary>
    /// Records aggregate element and instance counts for an incremental icon publication.
    /// </summary>
    [Event(22, Level = EventLevel.Informational, Keywords = Keywords.Icons, Task = Tasks.Icons)]
    public void IconUpdatesPublished(int changedElementCount, int instanceUpdateCount)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Icons))
        {
            WriteEvent(22, changedElementCount, instanceUpdateCount);
        }
    }

    /// <summary>
    /// Records icon rasterization failure metadata without capturing visual or pixel content.
    /// </summary>
    [Event(
        23,
        Level = EventLevel.Error,
        Keywords = Keywords.Icons | Keywords.Errors,
        Task = Tasks.Icons)]
    public void IconRasterizationFailed(long textureId, string exceptionType, int hresult)
    {
        if (IsEnabled(EventLevel.Error, Keywords.Icons | Keywords.Errors))
        {
            WriteEvent(
                23,
                textureId,
                exceptionType ?? string.Empty,
                hresult);
        }
    }

    /// <summary>
    /// Records icon texture upload failure metadata and dimensions without pixel data.
    /// </summary>
    [Event(
        24,
        Level = EventLevel.Error,
        Keywords = Keywords.Icons | Keywords.Device | Keywords.Errors,
        Task = Tasks.Icons)]
    public void IconTextureUploadFailed(
        long textureId,
        int width,
        int height,
        string exceptionType,
        int hresult)
    {
        if (IsEnabled(
            EventLevel.Error,
            Keywords.Icons | Keywords.Device | Keywords.Errors))
        {
            WriteEvent(
                24,
                textureId,
                width,
                height,
                exceptionType ?? string.Empty,
                hresult);
        }
    }

    /// <summary>
    /// Records aggregate icon texture uploads, replacements, and removals.
    /// </summary>
    [Event(25, Level = EventLevel.Informational, Keywords = Keywords.Icons, Task = Tasks.Icons)]
    public void IconTextureUploadSummary(
        int uploadedCount,
        int replacedCount,
        int removedCount)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Icons))
        {
            WriteEvent(25, uploadedCount, replacedCount, removedCount);
        }
    }

    /// <summary>
    /// Records verbose aggregate culling, texture batching, and draw-call counts for icons.
    /// </summary>
    [Event(26, Level = EventLevel.Verbose, Keywords = Keywords.Icons, Task = Tasks.Icons)]
    public void IconRenderBatch(
        int visibleInstanceCount,
        int drawableInstanceCount,
        int textureBatchCount,
        int drawCallCount)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Icons))
        {
            WriteEvent(
                26,
                visibleInstanceCount,
                drawableInstanceCount,
                textureBatchCount,
                drawCallCount);
        }
    }

    /// <summary>
    /// Records rendering suspension with a fixed control-lifecycle reason.
    /// </summary>
    [Event(27, Level = EventLevel.Informational, Keywords = Keywords.Lifecycle, Task = Tasks.Control)]
    public void RenderingSuspended(string reason)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Lifecycle))
        {
            WriteEvent(27, reason ?? string.Empty);
        }
    }

    /// <summary>
    /// Records rendering resumption with a fixed control-lifecycle reason.
    /// </summary>
    [Event(28, Level = EventLevel.Informational, Keywords = Keywords.Lifecycle, Task = Tasks.Control)]
    public void RenderingResumed(string reason)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Lifecycle))
        {
            WriteEvent(28, reason ?? string.Empty);
        }
    }

    /// <summary>
    /// Records sanitized control-operation failure metadata.
    /// </summary>
    [Event(
        29,
        Level = EventLevel.Error,
        Keywords = Keywords.Lifecycle | Keywords.Errors,
        Task = Tasks.Control)]
    public void ControlFailure(string operation, string exceptionType, int hresult)
    {
        if (IsEnabled(EventLevel.Error, Keywords.Lifecycle | Keywords.Errors))
        {
            WriteEvent(
                29,
                operation ?? string.Empty,
                exceptionType ?? string.Empty,
                hresult);
        }
    }

    /// <summary>
    /// Records activation of a raster generation, including source zoom and cache-retention
    /// state.
    /// </summary>
    [Event(30, Level = EventLevel.Informational, Keywords = Keywords.Tiles, Task = Tasks.TileRequest)]
    public void TileSetActivated(
        long generation,
        int tileZoom,
        bool clearedExistingTiles,
        int retainedTileCount)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Tiles))
        {
            WriteEvent(30, generation, tileZoom, clearedExistingTiles, retainedTileCount);
        }
    }

    /// <summary>
    /// Records aggregate accepted, stale, and duplicate render-thread upload commits.
    /// </summary>
    [Event(
        31,
        Level = EventLevel.Informational,
        Keywords = Keywords.Tiles | Keywords.Device,
        Task = Tasks.TileUpload)]
    public void TileUploadCommitSummary(
        int acceptedCount,
        int staleDroppedCount,
        int duplicateDroppedCount)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Tiles | Keywords.Device))
        {
            WriteEvent(31, acceptedCount, staleDroppedCount, duplicateDroppedCount);
        }
    }

    /// <summary>
    /// Records a fixed layer operation and aggregate layer and element counts.
    /// </summary>
    [Event(32, Level = EventLevel.Informational, Keywords = Keywords.Icons, Task = Tasks.Icons)]
    public void LayersChanged(
        string operation,
        int layerCount,
        int mapElementsLayerCount,
        int elementCount)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Icons))
        {
            WriteEvent(
                32,
                operation ?? string.Empty,
                layerCount,
                mapElementsLayerCount,
                elementCount);
        }
    }

    /// <summary>
    /// Records non-sensitive custom tile configuration shape without template URLs or
    /// subdomains.
    /// </summary>
    [Event(33, Level = EventLevel.Informational, Keywords = Keywords.CustomTiles, Task = Tasks.CustomTiles)]
    public void CustomTileLayerConfigured(
        bool added,
        int tileSize,
        int minimumSourceZoom,
        int maximumSourceZoom,
        bool isTms)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.CustomTiles))
        {
            WriteEvent(33, added, tileSize, minimumSourceZoom, maximumSourceZoom, isTms);
        }
    }

    /// <summary>
    /// Records removal of a custom tile layer without source configuration.
    /// </summary>
    [Event(34, Level = EventLevel.Informational, Keywords = Keywords.CustomTiles, Task = Tasks.CustomTiles)]
    public void CustomTileLayerRemoved()
    {
        if (IsEnabled(EventLevel.Informational, Keywords.CustomTiles))
        {
            WriteEvent(34);
        }
    }

    /// <summary>
    /// Starts a paired custom tile wave with generation, cache, and batch counts.
    /// </summary>
    [Event(
        35,
        Level = EventLevel.Informational,
        Keywords = Keywords.CustomTiles,
        Task = Tasks.CustomTiles,
        Opcode = EventOpcode.Start)]
    public void CustomTileWaveStart(
        long generation,
        int sourceZoom,
        int requiredCount,
        int cacheHitCount,
        int pendingCount,
        int batchCount)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.CustomTiles))
        {
            WriteEvent(
                35,
                generation,
                sourceZoom,
                requiredCount,
                cacheHitCount,
                pendingCount,
                batchCount);
        }
    }

    /// <summary>
    /// Stops a custom tile wave with duration and aggregate outcome counts.
    /// </summary>
    [Event(
        36,
        Level = EventLevel.Informational,
        Keywords = Keywords.CustomTiles,
        Task = Tasks.CustomTiles,
        Opcode = EventOpcode.Stop)]
    public void CustomTileWaveStop(
        long generation,
        double durationMilliseconds,
        int completedCount,
        int failedCount,
        int canceledCount,
        int remainingCount)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.CustomTiles))
        {
            WriteEvent(
                36,
                generation,
                durationMilliseconds,
                completedCount,
                failedCount,
                canceledCount,
                remainingCount);
        }
    }

    /// <summary>
    /// Records a failed custom tile request using coordinates and sanitized categories,
    /// without the expanded URL.
    /// </summary>
    [Event(
        37,
        Level = EventLevel.Error,
        Keywords = Keywords.CustomTiles | Keywords.Errors,
        Task = Tasks.CustomTiles)]
    public void CustomTileRequestFailed(
        int zoom,
        int x,
        int y,
        long generation,
        int statusCode,
        string failureKind,
        string exceptionType)
    {
        if (IsEnabled(EventLevel.Error, Keywords.CustomTiles | Keywords.Errors))
        {
            WriteEvent(
                37,
                zoom,
                x,
                y,
                generation,
                statusCode,
                failureKind ?? string.Empty,
                exceptionType ?? string.Empty);
        }
    }

    /// <summary>
    /// Records a custom tile upload failure without URL or pixel content.
    /// </summary>
    [Event(
        38,
        Level = EventLevel.Error,
        Keywords = Keywords.CustomTiles | Keywords.Device | Keywords.Errors,
        Task = Tasks.CustomTiles)]
    public void CustomTileUploadFailed(
        int zoom,
        int x,
        int y,
        long generation,
        string exceptionType,
        int hresult)
    {
        if (IsEnabled(
            EventLevel.Error,
            Keywords.CustomTiles | Keywords.Device | Keywords.Errors))
        {
            WriteEvent(
                38,
                zoom,
                x,
                y,
                generation,
                exceptionType ?? string.Empty,
                hresult);
        }
    }

    /// <summary>
    /// Records aggregate accepted and stale custom uploads and their cache entry count.
    /// </summary>
    [Event(
        39,
        Level = EventLevel.Informational,
        Keywords = Keywords.CustomTiles | Keywords.Device,
        Task = Tasks.CustomTiles)]
    public void CustomTileUploadSummary(int acceptedCount, int staleCount, int cacheEntryCount)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.CustomTiles | Keywords.Device))
        {
            WriteEvent(39, acceptedCount, staleCount, cacheEntryCount);
        }
    }

    /// <summary>
    /// Records aggregate custom tile cache entries, evictions, and retained bytes.
    /// </summary>
    [Event(
        40,
        Level = EventLevel.Informational,
        Keywords = Keywords.CustomTiles | Keywords.Cache,
        Task = Tasks.CustomTiles)]
    public void CustomTileCacheSummary(int entryCount, int evictedCount, long cacheBytes)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.CustomTiles | Keywords.Cache))
        {
            WriteEvent(40, entryCount, evictedCount, cacheBytes);
        }
    }

    /// <summary>
    /// Records aggregate texture disposal count, bytes, and remaining queue depth.
    /// </summary>
    [Event(
        41,
        Level = EventLevel.Informational,
        Keywords = Keywords.Device | Keywords.Cache,
        Task = Tasks.Cache)]
    public void TextureDisposalSummary(
        int disposedCount,
        long disposedBytes,
        int remainingCount)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Device | Keywords.Cache))
        {
            WriteEvent(41, disposedCount, disposedBytes, remainingCount);
        }
    }

    /// <summary>
    /// Records raster upload-pipeline queue depths and occupied capacity for one generation.
    /// </summary>
    [Event(
        42,
        Level = EventLevel.Informational,
        Keywords = Keywords.Tiles | Keywords.Device | Keywords.Cache,
        Task = Tasks.TileUpload)]
    public void TilePipelineBacklog(
        long generation,
        int decodedQueueCount,
        int completedQueueCount,
        int disposalQueueCount,
        int occupiedUploadSlots)
    {
        if (IsEnabled(
            EventLevel.Informational,
            Keywords.Tiles | Keywords.Device | Keywords.Cache))
        {
            WriteEvent(
                42,
                generation,
                decodedQueueCount,
                completedQueueCount,
                disposalQueueCount,
                occupiedUploadSlots);
        }
    }

    /// <summary>
    /// Records successful request, decode, and upload-queue timing without source addresses
    /// or response content.
    /// </summary>
    [Event(
        43,
        Level = EventLevel.Verbose,
        Keywords = Keywords.Tiles,
        Task = Tasks.TileRequest)]
    public void TileRequestTiming(
        int sourceKind,
        long generation,
        double downloadMilliseconds,
        double decodeMilliseconds,
        double uploadWaitMilliseconds,
        double totalMilliseconds,
        int activeRequests,
        int peakRequests)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Tiles))
        {
            WriteEvent(
                43,
                sourceKind,
                generation,
                downloadMilliseconds,
                decodeMilliseconds,
                uploadWaitMilliseconds,
                totalMilliseconds,
                activeRequests,
                peakRequests);
        }
    }

    /// <summary>
    /// Records aggregate GPU texture creation, render-lock wait, and wake behavior for one
    /// bounded upload pass.
    /// </summary>
    [Event(
        44,
        Level = EventLevel.Informational,
        Keywords = Keywords.Tiles | Keywords.Device,
        Task = Tasks.TileUpload)]
    public void TileUploadTiming(
        int uploadedCount,
        int queueStartCount,
        int queueRemainingCount,
        double textureCreateMilliseconds,
        double renderLockWaitMilliseconds,
        double totalMilliseconds,
        int renderWakeCount)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Tiles | Keywords.Device))
        {
            WriteEvent(
                44,
                uploadedCount,
                queueStartCount,
                queueRemainingCount,
                textureCreateMilliseconds,
                renderLockWaitMilliseconds,
                totalMilliseconds,
                renderWakeCount);
        }
    }

    /// <summary>
    /// Records first, complete, and opaque viewport coverage milestones with aggregate
    /// counts and cache size only.
    /// </summary>
    [Event(
        45,
        Level = EventLevel.Informational,
        Keywords = Keywords.Tiles | Keywords.Device | Keywords.Cache,
        Task = Tasks.TileUpload)]
    public void RasterCoverageMilestone(
        int sourceKind,
        long generation,
        long sceneVersion,
        string milestone,
        int requiredCount,
        int coveredCount,
        int opaqueCount,
        double elapsedMilliseconds,
        long cacheBytes,
        int cacheEntryCount)
    {
        if (IsEnabled(
            EventLevel.Informational,
            Keywords.Tiles | Keywords.Device | Keywords.Cache))
        {
            WriteEvent(
                45,
                sourceKind,
                generation,
                sceneVersion,
                milestone ?? string.Empty,
                requiredCount,
                coveredCount,
                opaqueCount,
                elapsedMilliseconds,
                cacheBytes,
                cacheEntryCount);
        }
    }

    /// <summary>
    /// Records one continuously fed scheduler run and whether a newer scene interrupted
    /// further starts.
    /// </summary>
    [Event(
        46,
        Level = EventLevel.Informational,
        Keywords = Keywords.Tiles,
        Task = Tasks.TileWave)]
    public void TileSchedulerSummary(
        int sourceKind,
        long generation,
        long sceneVersion,
        int candidateCount,
        int startedCount,
        int completedCount,
        int maximumConcurrency,
        int deferredCount,
        double durationMilliseconds)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Tiles))
        {
            WriteEvent(
                46,
                sourceKind,
                generation,
                sceneVersion,
                candidateCount,
                startedCount,
                completedCount,
                maximumConcurrency,
                deferredCount,
                durationMilliseconds);
        }
    }

    /// <summary>
    /// Records a normalized heading target and whether it bypasses interpolation.
    /// </summary>
    [Event(
        47,
        Level = EventLevel.Informational,
        Keywords = Keywords.Camera,
        Task = Tasks.Camera)]
    public void CameraHeadingTargetChanged(double heading, bool isImmediate)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Camera))
        {
            WriteEvent(47, heading, isImmediate);
        }
    }

    /// <summary>
    /// Records a normalized pitch target and whether it bypasses interpolation.
    /// </summary>
    [Event(
        48,
        Level = EventLevel.Informational,
        Keywords = Keywords.Camera,
        Task = Tasks.Camera)]
    public void CameraPitchTargetChanged(double pitch, bool isImmediate)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Camera))
        {
            WriteEvent(48, pitch, isImmediate);
        }
    }
}
