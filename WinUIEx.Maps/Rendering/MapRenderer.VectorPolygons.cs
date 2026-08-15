using System.Diagnostics;
using System.Numerics;
using WinUIEx.Maps.Rendering.Diagnostics;

namespace WinUIEx.Maps.Rendering;

internal sealed partial class MapRenderer
{
    private VectorPolygonFrameCache? _vectorPolygonFrameCache;

    private unsafe bool DrawVectorPolygonLayer(
        IntPtr context,
        LayerRenderSnapshot layer)
    {
        if (!_rasterLayers.TryGetValue(
                layer.RuntimeId,
                out RasterLayerState? state) ||
            state.Scene is null)
        {
            return false;
        }

        ApplyCompletedVectorGeometryPreparation(layer, state);
        if (_displayZoom < layer.MinZoom ||
            _displayZoom >= layer.MaxZoom)
        {
            return false;
        }

        ulong fallbackMask = GetFallbackZoomMask(state);
        if (_displayPitch == 0 &&
            _vectorPolygonFrameCache is { } cached &&
            cached.MatchesConfiguration(
                layer,
                state,
                fallbackMask,
                _displayZoom,
                _displayHeading,
                _viewportWidth,
                _viewportHeight) &&
            TryGetVectorPanOffset(
                cached.Longitude,
                cached.Latitude,
                _displayLongitude,
                _displayLatitude,
                _displayZoom,
                _displayHeading,
                _viewportWidth,
                _viewportHeight,
                out double offsetX,
                out double offsetY) &&
            Math.Abs(offsetX) <= VectorGeometryCachePanLimit &&
            Math.Abs(offsetY) <= VectorGeometryCachePanLimit)
        {
            bool versionsMatch = cached.MatchesVersions(
                state,
                _vectorTileVersion);
            bool deferredRebuild = !versionsMatch &&
                DeferVectorGeometryRebuild(layer, state, fallbackMask);
            if (versionsMatch || deferredRebuild)
            {
                Dictionary<VectorPolygonBatchKey, PooledGeometryBuffer>
                    pendingBatches = [];
                List<VectorPolygonBatchKey> pendingOrder = [];
                try
                {
                    VectorPolygonRenderResult result = cached.Result;
                    bool activeFade = false;
                    if (!versionsMatch)
                    {
                        VectorPolygonRenderResult pendingResult = new();
                        activeFade = CollectPendingVectorPolygons(
                            layer,
                            state,
                            cached.IncludedTiles,
                            pendingBatches,
                            pendingOrder,
                            ref pendingResult,
                            out int pendingTileCount);
                        MapControlEventSource.Log
                            .VectorGeometryDeferredRebuildSummary(
                                layer.Style,
                                (int)VectorGeometryKind.Polygon,
                                pendingTileCount,
                                offsetX,
                                offsetY);
                        pendingOrder.Sort(static (left, right) =>
                            left.StyleLayerOrder.CompareTo(
                                right.StyleLayerOrder));
                        DrawReusedVectorPolygons(
                            context,
                            cached,
                            pendingBatches,
                            pendingOrder,
                            offsetX,
                            offsetY,
                            ref pendingResult);
                        result.Add(pendingResult);
                    }
                    else
                    {
                        foreach (VectorPolygonCachedBatch batch in cached.Batches)
                        {
                            DrawGpuGeometryBuffer(
                                context,
                                batch.Buffer,
                                batch.Key.Color,
                                premultiplied: true,
                                offsetX,
                                offsetY);
                        }
                    }
                    TraceVectorPolygonResult(layer, result);
                    MapControlEventSource.Log.VectorGeometryFrameCacheSummary(
                        layer.Style,
                        (int)VectorGeometryKind.Polygon,
                        1,
                        cached.VertexCount,
                        cached.ByteSize);
                    return activeFade || !versionsMatch;
                }
                finally
                {
                    foreach (PooledGeometryBuffer buffer in pendingBatches.Values)
                    {
                        buffer.Dispose();
                    }
                }
            }
        }

        _vectorPolygonFrameCache?.Dispose();
        _vectorPolygonFrameCache = null;
        Dictionary<VectorPolygonBatchKey, PooledGeometryBuffer> batches = [];
        List<VectorPolygonBatchKey> batchOrder = [];
        try
        {
            VectorPolygonRenderResult result = new();
            bool canEnumerateActiveScene = CanEnumerateRasterScene(
                _displayZoom,
                state.Scene.TileZoom);
            HashSet<int> cachedLevels = [.. state.FallbackTileZooms];
            if (!canEnumerateActiveScene)
            {
                cachedLevels.Add(state.Scene.TileZoom);
            }

            MapScene? activeScene = canEnumerateActiveScene
                ? CreateCurrentRasterScene(state.Scene.TileZoom)
                : null;
            HashSet<VectorTileInstanceKey> includedTiles = [];
            bool activeFade = CollectCachedVectorPolygons(
                layer,
                cachedLevels,
                activeScene,
                batches,
                batchOrder,
                ref result);
            if (activeScene is not null)
            {
                activeFade |= CollectVectorPolygonScene(
                    layer,
                    activeScene,
                    batches,
                    batchOrder,
                    ref result,
                    includedTiles);
            }

            batchOrder.Sort(static (left, right) =>
                left.StyleLayerOrder.CompareTo(right.StyleLayerOrder));
            foreach (VectorPolygonBatchKey key in batchOrder)
            {
                PooledGeometryBuffer buffer = batches[key];
                DrawGeometryBuffer(
                    context,
                    buffer,
                    key.Color,
                    premultiplied: true);
                result.DrawCallCount += buffer.Chunks.Count;
            }
            TraceVectorPolygonResult(layer, result);
            int vertexCount = batches.Values.Sum(buffer => buffer.Count);
            long retainedByteSize = 0;
            bool shouldRetain = !activeFade &&
                _displayPitch == 0 &&
                !_zoomAnimation.IsActive &&
                !_headingAnimation.IsActive &&
                !_pitchAnimation.IsActive;
            if (shouldRetain)
            {
                VectorPolygonCachedBatch[] cachedBatches =
                    CreateVectorPolygonCachedBatches(
                        DevicePointer,
                        batchOrder,
                        batches);
                retainedByteSize =
                    cachedBatches.Sum(batch => batch.Buffer.ByteSize);
                _vectorPolygonFrameCache = new VectorPolygonFrameCache(
                    layer,
                    state,
                    _vectorTileVersion,
                    fallbackMask,
                    _displayLongitude,
                    _displayLatitude,
                    _displayZoom,
                    _displayHeading,
                    _viewportWidth,
                    _viewportHeight,
                    cachedBatches,
                    includedTiles,
                    result,
                    vertexCount,
                    retainedByteSize);
            }
            MapControlEventSource.Log.VectorGeometryFrameCacheSummary(
                layer.Style,
                (int)VectorGeometryKind.Polygon,
                0,
                vertexCount,
                retainedByteSize);
            return activeFade;
        }
        finally
        {
            foreach (PooledGeometryBuffer buffer in batches.Values)
            {
                buffer.Dispose();
            }
        }
    }

    private bool CollectPendingVectorPolygons(
        LayerRenderSnapshot layer,
        RasterLayerState state,
        IReadOnlySet<VectorTileInstanceKey> includedTiles,
        Dictionary<VectorPolygonBatchKey, PooledGeometryBuffer> batches,
        List<VectorPolygonBatchKey> batchOrder,
        ref VectorPolygonRenderResult result,
        out int pendingTileCount)
    {
        pendingTileCount = 0;
        if (!CanEnumerateRasterScene(_displayZoom, state.Scene!.TileZoom))
        {
            return false;
        }

        bool activeFade = false;
        HashSet<RasterTileKey> pendingTiles = [];
        MapScene scene = CreateCurrentRasterScene(state.Scene.TileZoom);
        foreach (VisibleTile visibleTile in scene.VisibleTiles)
        {
            RasterTileKey key = new(layer.RuntimeId, visibleTile.Id);
            if (includedTiles.Contains(new(key, visibleTile.WorldX)) ||
                !_vectorTiles.TryGetValue(key, out VectorTileCacheEntry? tile))
            {
                continue;
            }
            pendingTiles.Add(key);
            activeFade |= CollectVectorPolygonTile(
                layer,
                visibleTile,
                tile,
                batches,
                batchOrder,
                ref result,
                opacityMultiplier: 1);
        }
        pendingTileCount = pendingTiles.Count;
        return activeFade;
    }

    private void DrawReusedVectorPolygons(
        IntPtr context,
        VectorPolygonFrameCache cached,
        IReadOnlyDictionary<VectorPolygonBatchKey, PooledGeometryBuffer> pending,
        IReadOnlyList<VectorPolygonBatchKey> pendingOrder,
        double offsetX,
        double offsetY,
        ref VectorPolygonRenderResult pendingResult)
    {
        int cachedIndex = 0;
        int pendingIndex = 0;
        while (cachedIndex < cached.Batches.Length ||
            pendingIndex < pendingOrder.Count)
        {
            if (pendingIndex >= pendingOrder.Count ||
                (cachedIndex < cached.Batches.Length &&
                 cached.Batches[cachedIndex].Key.StyleLayerOrder <=
                    pendingOrder[pendingIndex].StyleLayerOrder))
            {
                VectorPolygonCachedBatch batch =
                    cached.Batches[cachedIndex++];
                DrawGpuGeometryBuffer(
                    context,
                    batch.Buffer,
                    batch.Key.Color,
                    premultiplied: true,
                    offsetX,
                    offsetY);
            }
            else
            {
                VectorPolygonBatchKey key = pendingOrder[pendingIndex++];
                PooledGeometryBuffer buffer = pending[key];
                DrawGeometryBuffer(
                    context,
                    buffer,
                    key.Color,
                    premultiplied: true);
                pendingResult.DrawCallCount += buffer.Chunks.Count;
            }
        }
    }

    private static VectorPolygonCachedBatch[] CreateVectorPolygonCachedBatches(
        IntPtr devicePointer,
        IReadOnlyList<VectorPolygonBatchKey> batchOrder,
        IReadOnlyDictionary<VectorPolygonBatchKey, PooledGeometryBuffer> batches,
        CancellationToken cancellationToken = default)
    {
        List<VectorPolygonCachedBatch> cached = [];
        try
        {
            foreach (VectorPolygonBatchKey key in batchOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cached.Add(new VectorPolygonCachedBatch(
                    key,
                    CreateGpuGeometryBuffer(
                        devicePointer,
                        batches[key],
                        cancellationToken)));
            }
            return [.. cached];
        }
        catch
        {
            foreach (VectorPolygonCachedBatch batch in cached)
            {
                batch.Buffer.Dispose();
            }
            throw;
        }
    }

    private static void TraceVectorPolygonResult(
        LayerRenderSnapshot layer,
        VectorPolygonRenderResult result)
    {
        MapControlEventSource.Log.VectorPolygonRenderBatch(
            layer.Style,
            result.CandidatePolygonCount,
            result.DrawablePolygonCount,
            result.TriangleCount,
            result.EvaluationFailureCount,
            result.SuppressedFallbackInstanceCount,
            result.DrawCallCount);
        if (result.FallbackInstanceCount != 0)
        {
            MapControlEventSource.Log.VectorGeometryFallbackOpacitySummary(
                layer.Style,
                (int)VectorGeometryKind.Polygon,
                result.FallbackInstanceCount,
                result.FadedFallbackInstanceCount,
                result.SuppressedFallbackInstanceCount,
                result.MinimumFallbackOpacity,
                result.MaximumFallbackOpacity);
        }
    }

    private bool CollectVectorPolygonScene(
        LayerRenderSnapshot layer,
        MapScene scene,
        Dictionary<VectorPolygonBatchKey, PooledGeometryBuffer> batches,
        List<VectorPolygonBatchKey> batchOrder,
        ref VectorPolygonRenderResult result,
        ISet<VectorTileInstanceKey>? includedTiles = null)
    {
        bool activeFade = false;
        foreach (VisibleTile visibleTile in scene.VisibleTiles)
        {
            RasterTileKey key = new(layer.RuntimeId, visibleTile.Id);
            if (_vectorTiles.TryGetValue(key, out VectorTileCacheEntry? tile))
            {
                includedTiles?.Add(new(key, visibleTile.WorldX));
                activeFade |= CollectVectorPolygonTile(
                    layer,
                    visibleTile,
                    tile,
                    batches,
                    batchOrder,
                    ref result,
                    opacityMultiplier: 1);
            }
        }
        return activeFade;
    }

    private bool CollectCachedVectorPolygons(
        LayerRenderSnapshot layer,
        IReadOnlySet<int> tileZooms,
        MapScene? activeScene,
        Dictionary<VectorPolygonBatchKey, PooledGeometryBuffer> batches,
        List<VectorPolygonBatchKey> batchOrder,
        ref VectorPolygonRenderResult result)
    {
        bool activeFade = false;
        foreach ((RasterTileKey key, VectorTileCacheEntry tile) in _vectorTiles)
        {
            if (key.SourceId != layer.RuntimeId ||
                !tileZooms.Contains(key.Id.Zoom))
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
            result.FallbackInstanceCount += instances.Count;
            double opacityMultiplier = ComputeVectorGeometryFallbackOpacity(
                _displayZoom,
                key.Id.Zoom,
                GetVectorGeometryReplacementOpacity(
                    layer.RuntimeId,
                    key.Id,
                    activeScene,
                    layer.FadeDuration));
            result.MinimumFallbackOpacity = Math.Min(
                result.MinimumFallbackOpacity,
                opacityMultiplier);
            result.MaximumFallbackOpacity = Math.Max(
                result.MaximumFallbackOpacity,
                opacityMultiplier);
            if (opacityMultiplier <= 0)
            {
                result.SuppressedFallbackInstanceCount += instances.Count;
                continue;
            }
            if (opacityMultiplier < 1)
            {
                result.FadedFallbackInstanceCount += instances.Count;
            }
            foreach (VisibleTile instance in instances)
            {
                activeFade |= CollectVectorPolygonTile(
                    layer,
                    instance,
                    tile,
                    batches,
                    batchOrder,
                    ref result,
                    opacityMultiplier);
            }
        }
        return activeFade;
    }

    private bool CollectVectorPolygonTile(
        LayerRenderSnapshot layer,
        VisibleTile visibleTile,
        VectorTileCacheEntry tile,
        Dictionary<VectorPolygonBatchKey, PooledGeometryBuffer> batches,
        List<VectorPolygonBatchKey> batchOrder,
        ref VectorPolygonRenderResult result,
        double opacityMultiplier)
    {
        tile.MarkUsed();
        VectorPolygonResolution resolution = tile.GetPolygons(_displayZoom);
        result.CandidatePolygonCount += resolution.Polygons.Length;
        result.EvaluationFailureCount += resolution.EvaluationFailureCount;
        double tileOpacity = ComputeLayerTileOpacity(
            Stopwatch.GetElapsedTime(tile.ReadyTimestamp),
            layer.FadeDuration,
            layer.Opacity);
        double opacity = tileOpacity * opacityMultiplier;
        foreach (VectorTileStyledPolygon polygon in resolution.Polygons)
        {
            VectorPolygonBatchKey key = new(
                polygon.StyleLayerOrder,
                polygon.Style.Color * (float)opacity);
            if (!batches.TryGetValue(
                    key,
                    out PooledGeometryBuffer? buffer))
            {
                buffer = new PooledGeometryBuffer();
                batches.Add(key, buffer);
                batchOrder.Add(key);
            }
            int triangleCount = AppendProjectedVectorPolygonTriangles(
                polygon.FillTriangles,
                visibleTile,
                _viewportWidth,
                _viewportHeight,
                _displayHeading,
                _displayPitch,
                _displayPitch == 0 ? VectorGeometryCachePadding : 0,
                buffer);
            if (triangleCount == 0)
            {
                continue;
            }
            result.DrawablePolygonCount++;
            result.TriangleCount += triangleCount;
        }
        return tileOpacity < layer.Opacity;
    }

    internal static MapScreenPoint[] ProjectVectorPolygonTriangles(
        IReadOnlyList<VectorTilePoint> triangles,
        VisibleTile tile,
        double viewportWidth,
        double viewportHeight,
        double heading,
        double pitch)
    {
        if (triangles.Count < 3)
        {
            return [];
        }
        using PooledGeometryBuffer projected = new();
        AppendProjectedVectorPolygonTriangles(
            triangles,
            tile,
            viewportWidth,
            viewportHeight,
            heading,
            pitch,
            0,
            projected);
        return projected.ToArray();
    }

    private static int AppendProjectedVectorPolygonTriangles(
        IReadOnlyList<VectorTilePoint> triangles,
        VisibleTile tile,
        double viewportWidth,
        double viewportHeight,
        double heading,
        double pitch,
        double viewportPadding,
        PooledGeometryBuffer visible)
    {
        int initialCount = visible.Count;
        for (int index = 0; index + 2 < triangles.Count; index += 3)
        {
            MapScreenPoint first = ProjectVectorPoint(
                triangles[index],
                tile,
                viewportWidth,
                viewportHeight,
                heading,
                pitch);
            MapScreenPoint second = ProjectVectorPoint(
                triangles[index + 1],
                tile,
                viewportWidth,
                viewportHeight,
                heading,
                pitch);
            MapScreenPoint third = ProjectVectorPoint(
                triangles[index + 2],
                tile,
                viewportWidth,
                viewportHeight,
                heading,
                pitch);
            double left = Math.Min(first.X, Math.Min(second.X, third.X));
            double top = Math.Min(first.Y, Math.Min(second.Y, third.Y));
            double right = Math.Max(first.X, Math.Max(second.X, third.X));
            double bottom = Math.Max(first.Y, Math.Max(second.Y, third.Y));
            if (left >= viewportWidth + viewportPadding ||
                top >= viewportHeight + viewportPadding ||
                right <= -viewportPadding ||
                bottom <= -viewportPadding)
            {
                continue;
            }
            visible.Add(first);
            visible.Add(second);
            visible.Add(third);
        }
        return (visible.Count - initialCount) / 3;
    }

    private readonly record struct VectorPolygonBatchKey(
        int StyleLayerOrder,
        Vector4 Color);

    private sealed record VectorPolygonCachedBatch(
        VectorPolygonBatchKey Key,
        GpuGeometryBuffer Buffer);

    private sealed class VectorPolygonFrameCache(
        LayerRenderSnapshot layer,
        RasterLayerState state,
        long vectorTileVersion,
        ulong fallbackMask,
        double longitude,
        double latitude,
        double zoom,
        double heading,
        double viewportWidth,
        double viewportHeight,
        VectorPolygonCachedBatch[] batches,
        HashSet<VectorTileInstanceKey> includedTiles,
        VectorPolygonRenderResult result,
        int vertexCount,
        long byteSize) : IDisposable
    {
        private long Generation { get; } = state.Generation;

        private long SceneVersion { get; } = state.SceneVersion;

        private long VectorTileVersion { get; } = vectorTileVersion;

        internal double Longitude { get; } = longitude;

        internal double Latitude { get; } = latitude;

        internal VectorPolygonCachedBatch[] Batches { get; } = batches;

        internal IReadOnlySet<VectorTileInstanceKey> IncludedTiles { get; } =
            includedTiles;

        internal VectorPolygonRenderResult Result { get; } = result;

        internal int VertexCount { get; } = vertexCount;

        internal long ByteSize { get; } = byteSize;

        internal bool MatchesConfiguration(
            LayerRenderSnapshot currentLayer,
            RasterLayerState currentState,
            ulong currentFallbackMask,
            double currentZoom,
            double currentHeading,
            double currentViewportWidth,
            double currentViewportHeight) =>
            layer.RuntimeId == currentLayer.RuntimeId &&
            layer.Style == currentLayer.Style &&
            layer.Opacity == currentLayer.Opacity &&
            Generation == currentState.Generation &&
            fallbackMask == currentFallbackMask &&
            zoom == currentZoom &&
            heading == currentHeading &&
            viewportWidth == currentViewportWidth &&
            viewportHeight == currentViewportHeight;

        internal bool MatchesVersions(
            RasterLayerState currentState,
            long currentVectorTileVersion) =>
            SceneVersion == currentState.SceneVersion &&
            VectorTileVersion == currentVectorTileVersion;

        public void Dispose()
        {
            foreach (VectorPolygonCachedBatch batch in Batches)
            {
                batch.Buffer.Dispose();
            }
        }
    }

    private struct VectorPolygonRenderResult
    {
        public VectorPolygonRenderResult()
        {
            MinimumFallbackOpacity = 1;
        }

        internal int CandidatePolygonCount;
        internal int DrawablePolygonCount;
        internal int TriangleCount;
        internal int EvaluationFailureCount;
        internal int FallbackInstanceCount;
        internal int FadedFallbackInstanceCount;
        internal int SuppressedFallbackInstanceCount;
        internal double MinimumFallbackOpacity;
        internal double MaximumFallbackOpacity;
        internal int DrawCallCount;

        internal void Add(VectorPolygonRenderResult other)
        {
            CandidatePolygonCount += other.CandidatePolygonCount;
            DrawablePolygonCount += other.DrawablePolygonCount;
            TriangleCount += other.TriangleCount;
            EvaluationFailureCount += other.EvaluationFailureCount;
            DrawCallCount += other.DrawCallCount;
        }
    }
}
