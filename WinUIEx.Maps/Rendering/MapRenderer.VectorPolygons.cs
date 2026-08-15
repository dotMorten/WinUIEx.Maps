using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using WinUIEx.Maps.Rendering.Diagnostics;
using static WinUIEx.Maps.Rendering.DirectXInterop;

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
                        pendingOrder.Sort(CompareVectorPolygonBatches);
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
        Dictionary<VectorPolygonBatchKey, List<TileVertex>> patternBatches = [];
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
                patternBatches,
                batchOrder,
                ref result);
            if (activeScene is not null)
            {
                activeFade |= CollectVectorPolygonScene(
                    layer,
                    activeScene,
                    batches,
                    patternBatches,
                    batchOrder,
                    ref result,
                    includedTiles);
            }

            batchOrder.Sort(CompareVectorPolygonBatches);
            foreach (VectorPolygonBatchKey key in batchOrder)
            {
                if (key.Kind == VectorPolygonBatchKind.Pattern)
                {
                    result.DrawCallCount += DrawVectorPolygonPattern(
                        context,
                        patternBatches[key],
                        key.TextureId,
                        key.Opacity);
                }
                else
                {
                    PooledGeometryBuffer buffer = batches[key];
                    DrawGeometryBuffer(
                        context,
                        buffer,
                        key.Color,
                        premultiplied: true);
                    result.DrawCallCount += buffer.Chunks.Count;
                }
            }
            TraceVectorPolygonResult(layer, result);
            int vertexCount = batches.Values.Sum(buffer => buffer.Count);
            long retainedByteSize = 0;
            bool shouldRetain = !activeFade &&
                patternBatches.Count == 0 &&
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
        Dictionary<VectorPolygonBatchKey, List<TileVertex>>
            ignoredPatternBatches = [];
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
                ignoredPatternBatches,
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
                 CompareVectorPolygonBatches(
                     cached.Batches[cachedIndex].Key,
                     pendingOrder[pendingIndex]) <= 0))
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
        MapControlEventSource.Log.VectorPolygonDecorationSummary(
            layer.Style,
            result.PatternPolygonCount,
            result.PatternTriangleCount,
            result.OutlineTriangleCount);
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
        Dictionary<VectorPolygonBatchKey, List<TileVertex>> patternBatches,
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
                    patternBatches,
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
        Dictionary<VectorPolygonBatchKey, List<TileVertex>> patternBatches,
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
                    patternBatches,
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
        Dictionary<VectorPolygonBatchKey, List<TileVertex>> patternBatches,
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
            int triangleCount = 0;
            if (polygon.Style.HasPattern)
            {
                result.HasPatternPolygons = true;
                result.PatternPolygonCount++;
                VectorPolygonBatchKey patternKey = new(
                    polygon.StyleLayerOrder,
                    VectorPolygonBatchKind.Pattern,
                    default,
                    polygon.Style.PatternTextureId,
                    polygon.Style.Opacity * opacity);
                if (!patternBatches.TryGetValue(
                        patternKey,
                        out List<TileVertex>? patternBuffer))
                {
                    patternBuffer = [];
                    patternBatches.Add(patternKey, patternBuffer);
                    batchOrder.Add(patternKey);
                }
                triangleCount = AppendProjectedVectorPolygonPattern(
                    polygon.FillTriangles,
                    visibleTile,
                    _viewportWidth,
                    _viewportHeight,
                    _displayHeading,
                    _displayPitch,
                    _displayPitch == 0 ? VectorGeometryCachePadding : 0,
                    polygon.Style.PatternWidth,
                    polygon.Style.PatternHeight,
                    patternBuffer);
            }
            else
            {
                VectorPolygonBatchKey fillKey = new(
                    polygon.StyleLayerOrder,
                    VectorPolygonBatchKind.Fill,
                    polygon.Style.Color * (float)opacity);
                PooledGeometryBuffer fillBuffer =
                    GetOrCreatePolygonBuffer(
                        fillKey,
                        batches,
                        batchOrder);
                triangleCount = AppendProjectedVectorPolygonTriangles(
                    polygon.FillTriangles,
                    visibleTile,
                    _viewportWidth,
                    _viewportHeight,
                    _displayHeading,
                    _displayPitch,
                    _displayPitch == 0 ? VectorGeometryCachePadding : 0,
                    fillBuffer);
            }
            int outlineTriangleCount = AppendVectorPolygonOutline(
                polygon,
                visibleTile,
                opacity,
                batches,
                batchOrder);
            if (triangleCount == 0)
            {
                result.OutlineTriangleCount += outlineTriangleCount;
                continue;
            }
            result.DrawablePolygonCount++;
            result.TriangleCount += triangleCount;
            if (polygon.Style.HasPattern)
            {
                result.PatternTriangleCount += triangleCount;
            }
            result.OutlineTriangleCount += outlineTriangleCount;
        }
        return tileOpacity < layer.Opacity;
    }

    private int AppendVectorPolygonOutline(
        VectorTileStyledPolygon polygon,
        VisibleTile tile,
        double opacity,
        Dictionary<VectorPolygonBatchKey, PooledGeometryBuffer> batches,
        List<VectorPolygonBatchKey> batchOrder)
    {
        if (polygon.Style.OutlineColor is not Vector4 outlineColor ||
            outlineColor.W <= 0)
        {
            return 0;
        }
        VectorPolygonBatchKey key = new(
            polygon.StyleLayerOrder,
            VectorPolygonBatchKind.Outline,
            outlineColor * (float)opacity);
        PooledGeometryBuffer buffer = GetOrCreatePolygonBuffer(
            key,
            batches,
            batchOrder);
        VectorLineStyle outlineStyle = new(
            key.Color,
            1,
            VectorLineCap.Butt,
            VectorLineJoin.Miter);
        return AppendVectorPolygonOutlineTriangles(
            polygon.Rings,
            tile,
            _viewportWidth,
            _viewportHeight,
            _displayHeading,
            _displayPitch,
            _displayPitch == 0 ? VectorGeometryCachePadding : 0,
            outlineStyle,
            buffer);
    }

    internal static MapScreenPoint[] ExpandVectorPolygonOutlineTriangles(
        IReadOnlyList<VectorTileRing> rings,
        VisibleTile tile,
        double viewportWidth,
        double viewportHeight,
        double heading,
        double pitch)
    {
        using PooledGeometryBuffer triangles = new();
        AppendVectorPolygonOutlineTriangles(
            rings,
            tile,
            viewportWidth,
            viewportHeight,
            heading,
            pitch,
            0,
            new VectorLineStyle(
                Vector4.One,
                1,
                VectorLineCap.Butt,
                VectorLineJoin.Miter),
            triangles);
        return triangles.ToArray();
    }

    private static int AppendVectorPolygonOutlineTriangles(
        IReadOnlyList<VectorTileRing> rings,
        VisibleTile tile,
        double viewportWidth,
        double viewportHeight,
        double heading,
        double pitch,
        double viewportPadding,
        VectorLineStyle outlineStyle,
        PooledGeometryBuffer buffer)
    {
        int triangleCount = 0;
        foreach (VectorTileRing ring in rings)
        {
            MapScreenPoint[] projected =
                ArrayPool<MapScreenPoint>.Shared.Rent(ring.Points.Length);
            try
            {
                ProjectVectorLine(
                    ring.Points,
                    tile,
                    viewportWidth,
                    viewportHeight,
                    heading,
                    pitch,
                    projected.AsSpan(0, ring.Points.Length));
                triangleCount += AppendVectorLineTriangles(
                    projected.AsSpan(0, ring.Points.Length),
                    outlineStyle,
                    viewportWidth,
                    viewportHeight,
                    viewportPadding,
                    buffer);
            }
            finally
            {
                ArrayPool<MapScreenPoint>.Shared.Return(projected);
            }
        }
        return triangleCount;
    }

    internal static Vector2[] GetVectorPolygonPatternTextureCoordinates(
        IReadOnlyList<VectorTilePoint> points,
        VisibleTile tile,
        double patternWidth,
        double patternHeight)
    {
        double phaseX = PositiveModulo(
            tile.WorldX * tile.Size,
            patternWidth);
        double phaseY = PositiveModulo(
            tile.Id.Y * tile.Size,
            patternHeight);
        return points.Select(point => new Vector2(
            (float)((phaseX + (point.X * tile.Size)) / patternWidth),
            (float)((phaseY + (point.Y * tile.Size)) / patternHeight)))
            .ToArray();
    }

    private static PooledGeometryBuffer GetOrCreatePolygonBuffer(
        VectorPolygonBatchKey key,
        Dictionary<VectorPolygonBatchKey, PooledGeometryBuffer> batches,
        List<VectorPolygonBatchKey> batchOrder)
    {
        if (!batches.TryGetValue(key, out PooledGeometryBuffer? buffer))
        {
            buffer = new PooledGeometryBuffer();
            batches.Add(key, buffer);
            batchOrder.Add(key);
        }
        return buffer;
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

    private static int AppendProjectedVectorPolygonPattern(
        IReadOnlyList<VectorTilePoint> triangles,
        VisibleTile tile,
        double viewportWidth,
        double viewportHeight,
        double heading,
        double pitch,
        double viewportPadding,
        double patternWidth,
        double patternHeight,
        List<TileVertex> visible)
    {
        int initialCount = visible.Count;
        double phaseX = PositiveModulo(
            tile.WorldX * tile.Size,
            patternWidth);
        double phaseY = PositiveModulo(
            tile.Id.Y * tile.Size,
            patternHeight);
        for (int index = 0; index + 2 < triangles.Count; index += 3)
        {
            VectorTilePoint firstSource = triangles[index];
            VectorTilePoint secondSource = triangles[index + 1];
            VectorTilePoint thirdSource = triangles[index + 2];
            MapScreenPoint first = ProjectVectorPoint(
                firstSource,
                tile,
                viewportWidth,
                viewportHeight,
                heading,
                pitch);
            MapScreenPoint second = ProjectVectorPoint(
                secondSource,
                tile,
                viewportWidth,
                viewportHeight,
                heading,
                pitch);
            MapScreenPoint third = ProjectVectorPoint(
                thirdSource,
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
            visible.Add(CreatePatternVertex(
                first,
                firstSource,
                tile.Size,
                phaseX,
                phaseY,
                patternWidth,
                patternHeight));
            visible.Add(CreatePatternVertex(
                second,
                secondSource,
                tile.Size,
                phaseX,
                phaseY,
                patternWidth,
                patternHeight));
            visible.Add(CreatePatternVertex(
                third,
                thirdSource,
                tile.Size,
                phaseX,
                phaseY,
                patternWidth,
                patternHeight));
        }
        return (visible.Count - initialCount) / 3;
    }

    private static TileVertex CreatePatternVertex(
        MapScreenPoint projected,
        VectorTilePoint source,
        double tileSize,
        double phaseX,
        double phaseY,
        double patternWidth,
        double patternHeight) =>
        new(
            new Vector2((float)projected.X, (float)projected.Y),
            new Vector2(
                (float)((phaseX + (source.X * tileSize)) / patternWidth),
                (float)((phaseY + (source.Y * tileSize)) / patternHeight)));

    private static double PositiveModulo(double value, double modulus)
    {
        double result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private unsafe int DrawVectorPolygonPattern(
        IntPtr context,
        List<TileVertex> vertices,
        long textureId,
        double opacity)
    {
        if (vertices.Count < 3 ||
            opacity <= 0 ||
            !_iconTextures.TryGetValue(textureId, out TileTexture? texture))
        {
            return 0;
        }
        TileConstants constants = new(
            new Vector4(
                (float)(2 / Viewport.Width),
                (float)(-2 / Viewport.Height),
                -1,
                1),
            new Vector4(1, 0, 0, 1),
            new Vector4(1, 0, 1, 0),
            new Vector4((float)opacity, 0, 0, 0));
        UpdateSubresource(context, _constantBufferPointer, &constants);
        SetBlendState(context, _premultipliedBlendStatePointer);
        SetInputLayout(context, _inputLayoutPointer);
        SetVertexBuffer(
            context,
            _patternVertexBufferPointer,
            (uint)Marshal.SizeOf<TileVertex>());
        SetVertexShader(
            context,
            _vertexShaderPointer,
            _constantBufferPointer);
        SetPixelShader(
            context,
            _iconPixelShaderPointer,
            texture.ViewPointer,
            _patternSamplerPointer,
            _constantBufferPointer);

        int drawCallCount = 0;
        Span<TileVertex> remaining = CollectionsMarshal.AsSpan(vertices);
        while (!remaining.IsEmpty)
        {
            int count = Math.Min(GeometryVertexCapacity, remaining.Length);
            count -= count % 3;
            if (count == 0)
            {
                break;
            }
            fixed (TileVertex* vertexPointer = remaining)
            {
                WriteDiscardBuffer(
                    context,
                    _patternVertexBufferPointer,
                    vertexPointer,
                    (nuint)(count * Marshal.SizeOf<TileVertex>()));
            }
            DrawVertices(context, (uint)count);
            drawCallCount++;
            remaining = remaining[count..];
        }
        return drawCallCount;
    }

    private readonly record struct VectorPolygonBatchKey(
        int StyleLayerOrder,
        VectorPolygonBatchKind Kind,
        Vector4 Color,
        long TextureId = 0,
        double Opacity = 1);

    private enum VectorPolygonBatchKind
    {
        Fill,
        Pattern,
        Outline,
    }

    private static int CompareVectorPolygonBatches(
        VectorPolygonBatchKey left,
        VectorPolygonBatchKey right)
    {
        int order = left.StyleLayerOrder.CompareTo(right.StyleLayerOrder);
        return order != 0 ? order : left.Kind.CompareTo(right.Kind);
    }

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
        internal int OutlineTriangleCount;
        internal int PatternPolygonCount;
        internal int PatternTriangleCount;
        internal bool HasPatternPolygons;
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
            OutlineTriangleCount += other.OutlineTriangleCount;
            PatternPolygonCount += other.PatternPolygonCount;
            PatternTriangleCount += other.PatternTriangleCount;
            HasPatternPolygons |= other.HasPatternPolygons;
            EvaluationFailureCount += other.EvaluationFailureCount;
            DrawCallCount += other.DrawCallCount;
        }
    }
}
