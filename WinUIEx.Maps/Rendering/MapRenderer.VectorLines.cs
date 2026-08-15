using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using WinUIEx.Maps.Rendering.Diagnostics;

namespace WinUIEx.Maps.Rendering;

internal sealed partial class MapRenderer
{
    private const double MinimumVectorLineRasterWidth = 1;
    private const double FullVectorGeometryFallbackZoomDifference = 1;
    private const double MaximumVectorGeometryFallbackZoomDifference = 2;
    private VectorLineFrameCache? _vectorLineFrameCache;

    private unsafe bool DrawVectorLineLayer(
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

        ulong fallbackMask = GetFallbackZoomMask(state);
        if (_displayPitch == 0 &&
            _vectorLineFrameCache is { } cached &&
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
                Dictionary<VectorLineBatchKey, PooledGeometryBuffer>
                    pendingBatches = [];
                List<VectorLineBatchKey> pendingOrder = [];
                try
                {
                    VectorLineRenderResult result = cached.Result;
                    bool activeFade = false;
                    if (!versionsMatch)
                    {
                        VectorLineRenderResult pendingResult = new();
                        activeFade = CollectPendingVectorLines(
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
                                (int)VectorGeometryKind.Line,
                                pendingTileCount,
                                offsetX,
                                offsetY);
                        pendingOrder.Sort(static (left, right) =>
                            left.StyleLayerOrder.CompareTo(
                                right.StyleLayerOrder));
                        DrawReusedVectorLines(
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
                        foreach (VectorLineCachedBatch batch in cached.Batches)
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
                    TraceVectorLineResult(layer, result);
                    MapControlEventSource.Log.VectorGeometryFrameCacheSummary(
                        layer.Style,
                        (int)VectorGeometryKind.Line,
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

        _vectorLineFrameCache?.Dispose();
        _vectorLineFrameCache = null;
        Dictionary<VectorLineBatchKey, PooledGeometryBuffer> batches = [];
        List<VectorLineBatchKey> batchOrder = [];
        try
        {
            VectorLineRenderResult result = new();
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
            bool activeFade = CollectCachedVectorLines(
                layer,
                cachedLevels,
                activeScene,
                batches,
                batchOrder,
                ref result);
            if (activeScene is not null)
            {
                activeFade |= CollectVectorLineScene(
                    layer,
                    activeScene,
                    batches,
                    batchOrder,
                    ref result,
                    includedTiles);
            }

            batchOrder.Sort(CompareVectorLineBatches);
            foreach (VectorLineBatchKey key in batchOrder)
            {
                PooledGeometryBuffer buffer = batches[key];
                DrawGeometryBuffer(
                    context,
                    buffer,
                    key.Color,
                    premultiplied: true);
                result.DrawCallCount += buffer.Chunks.Count;
            }
            TraceVectorLineResult(layer, result);
            int vertexCount = batches.Values.Sum(buffer => buffer.Count);
            long retainedByteSize = 0;
            bool shouldRetain = !activeFade &&
                _displayPitch == 0 &&
                !_zoomAnimation.IsActive &&
                !_headingAnimation.IsActive &&
                !_pitchAnimation.IsActive;
            if (shouldRetain)
            {
                VectorLineCachedBatch[] cachedBatches =
                    CreateVectorLineCachedBatches(
                        DevicePointer,
                        batchOrder,
                        batches);
                retainedByteSize =
                    cachedBatches.Sum(batch => batch.Buffer.ByteSize);
                _vectorLineFrameCache = new VectorLineFrameCache(
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
                (int)VectorGeometryKind.Line,
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

    private bool CollectPendingVectorLines(
        LayerRenderSnapshot layer,
        RasterLayerState state,
        IReadOnlySet<VectorTileInstanceKey> includedTiles,
        Dictionary<VectorLineBatchKey, PooledGeometryBuffer> batches,
        List<VectorLineBatchKey> batchOrder,
        ref VectorLineRenderResult result,
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
            activeFade |= CollectVectorLineTile(
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

    private void DrawReusedVectorLines(
        IntPtr context,
        VectorLineFrameCache cached,
        IReadOnlyDictionary<VectorLineBatchKey, PooledGeometryBuffer> pending,
        IReadOnlyList<VectorLineBatchKey> pendingOrder,
        double offsetX,
        double offsetY,
        ref VectorLineRenderResult pendingResult)
    {
        int cachedIndex = 0;
        int pendingIndex = 0;
        while (cachedIndex < cached.Batches.Length ||
            pendingIndex < pendingOrder.Count)
        {
            if (pendingIndex >= pendingOrder.Count ||
                (cachedIndex < cached.Batches.Length &&
                 CompareVectorLineBatches(
                     cached.Batches[cachedIndex].Key,
                     pendingOrder[pendingIndex]) <= 0))
            {
                VectorLineCachedBatch batch = cached.Batches[cachedIndex++];
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
                VectorLineBatchKey key = pendingOrder[pendingIndex++];
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

    private static VectorLineCachedBatch[] CreateVectorLineCachedBatches(
        IntPtr devicePointer,
        IReadOnlyList<VectorLineBatchKey> batchOrder,
        IReadOnlyDictionary<VectorLineBatchKey, PooledGeometryBuffer> batches,
        CancellationToken cancellationToken = default)
    {
        List<VectorLineCachedBatch> cached = [];
        try
        {
            foreach (VectorLineBatchKey key in batchOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cached.Add(new VectorLineCachedBatch(
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
            foreach (VectorLineCachedBatch batch in cached)
            {
                batch.Buffer.Dispose();
            }
            throw;
        }
    }

    private static void TraceVectorLineResult(
        LayerRenderSnapshot layer,
        VectorLineRenderResult result)
    {
        MapControlEventSource.Log.VectorLineRenderBatch(
            layer.Style,
            result.CandidateLineCount,
            result.DrawableLineCount,
            result.TriangleCount,
            result.EvaluationFailureCount,
            result.DrawCallCount);
        MapControlEventSource.Log.VectorLineDecorationSummary(
            layer.Style,
            1,
            result.DashedLineCount,
            result.DashTriangleCount);
        MapControlEventSource.Log.VectorAdvancedLineStyleSummary(
            layer.Style,
            result.OffsetLineCount,
            result.GapLineCount,
            result.GradientLineCount,
            result.BlurredLineCount,
            result.MiterLineCount);
        if (result.FallbackInstanceCount != 0)
        {
            MapControlEventSource.Log.VectorLineFallbackSummary(
                layer.Style,
                result.FallbackInstanceCount,
                result.DrawnFallbackInstanceCount,
                result.SuppressedFallbackInstanceCount,
                result.MaximumFallbackZoomDifference);
            MapControlEventSource.Log.VectorGeometryFallbackOpacitySummary(
                layer.Style,
                (int)VectorGeometryKind.Line,
                result.FallbackInstanceCount,
                result.FadedFallbackInstanceCount,
                result.SuppressedFallbackInstanceCount,
                result.MinimumFallbackOpacity,
                result.MaximumFallbackOpacity);
        }
    }

    private bool CollectVectorLineScene(
        LayerRenderSnapshot layer,
        MapScene scene,
        Dictionary<VectorLineBatchKey, PooledGeometryBuffer> batches,
        List<VectorLineBatchKey> batchOrder,
        ref VectorLineRenderResult result,
        ISet<VectorTileInstanceKey>? includedTiles = null)
    {
        bool activeFade = false;
        foreach (VisibleTile visibleTile in scene.VisibleTiles)
        {
            RasterTileKey key = new(layer.RuntimeId, visibleTile.Id);
            if (_vectorTiles.TryGetValue(key, out VectorTileCacheEntry? tile))
            {
                includedTiles?.Add(new(key, visibleTile.WorldX));
                activeFade |= CollectVectorLineTile(
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

    private bool CollectCachedVectorLines(
        LayerRenderSnapshot layer,
        IReadOnlySet<int> tileZooms,
        MapScene? activeScene,
        Dictionary<VectorLineBatchKey, PooledGeometryBuffer> batches,
        List<VectorLineBatchKey> batchOrder,
        ref VectorLineRenderResult result)
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
            double zoomDifference = Math.Abs(_displayZoom - key.Id.Zoom);
            result.FallbackInstanceCount += instances.Count;
            result.MaximumFallbackZoomDifference = Math.Max(
                result.MaximumFallbackZoomDifference,
                zoomDifference);
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
            result.DrawnFallbackInstanceCount += instances.Count;
            foreach (VisibleTile instance in instances)
            {
                activeFade |= CollectVectorLineTile(
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

    internal static double ComputeVectorGeometryFallbackOpacity(
        double displayZoom,
        int tileZoom,
        double replacementOpacity)
    {
        if (!double.IsFinite(displayZoom))
        {
            return 0;
        }
        double zoomDifference = Math.Abs(displayZoom - tileZoom);
        double distanceOpacity = zoomDifference <=
            FullVectorGeometryFallbackZoomDifference
            ? 1
            : 1 - Math.Clamp(
                (zoomDifference - FullVectorGeometryFallbackZoomDifference) /
                (MaximumVectorGeometryFallbackZoomDifference -
                    FullVectorGeometryFallbackZoomDifference),
                0,
                1);
        return distanceOpacity *
            (1 - Math.Clamp(replacementOpacity, 0, 1));
    }

    private double GetVectorGeometryReplacementOpacity(
        long sourceId,
        TileId fallbackTile,
        MapScene? activeScene,
        TimeSpan fadeDuration)
    {
        if (activeScene is null)
        {
            return 0;
        }
        bool hasOverlap = false;
        double replacementOpacity = 1;
        foreach (TileId activeTile in activeScene.RequiredTiles)
        {
            if (!TilesOverlap(fallbackTile, activeTile))
            {
                continue;
            }
            hasOverlap = true;
            if (!_vectorTiles.TryGetValue(
                    new RasterTileKey(sourceId, activeTile),
                    out VectorTileCacheEntry? tile))
            {
                return 0;
            }
            replacementOpacity = Math.Min(
                replacementOpacity,
                ComputeLayerTileOpacity(
                    Stopwatch.GetElapsedTime(tile.ReadyTimestamp),
                    fadeDuration,
                    1));
        }
        return hasOverlap ? replacementOpacity : 0;
    }

    private bool CollectVectorLineTile(
        LayerRenderSnapshot layer,
        VisibleTile visibleTile,
        VectorTileCacheEntry tile,
        Dictionary<VectorLineBatchKey, PooledGeometryBuffer> batches,
        List<VectorLineBatchKey> batchOrder,
        ref VectorLineRenderResult result,
        double opacityMultiplier)
    {
        tile.MarkUsed();
        VectorLineResolution resolution = tile.GetLines(_displayZoom);
        result.CandidateLineCount += resolution.Lines.Length;
        result.EvaluationFailureCount += resolution.EvaluationFailureCount;
        double tileOpacity = ComputeLayerTileOpacity(
            Stopwatch.GetElapsedTime(tile.ReadyTimestamp),
            layer.FadeDuration,
            layer.Opacity);
        double opacity = tileOpacity * opacityMultiplier;
        foreach (VectorTileStyledLine line in resolution.Lines)
        {
            VectorLineStyle rasterStyle =
                PrepareVectorLineForRasterization(line.Style);
            MapScreenPoint[] projected =
                ArrayPool<MapScreenPoint>.Shared.Rent(line.Points.Length);
            int triangleCount;
            try
            {
                ProjectVectorLine(
                    line.Points,
                    visibleTile,
                    _viewportWidth,
                    _viewportHeight,
                    _displayHeading,
                    _displayPitch,
                    projected.AsSpan(0, line.Points.Length));
                triangleCount = AppendStyledVectorLineTriangles(
                    projected.AsSpan(0, line.Points.Length),
                    rasterStyle,
                    line.StyleLayerOrder,
                    opacity,
                    _viewportWidth,
                    _viewportHeight,
                    _displayPitch == 0 ? VectorGeometryCachePadding : 0,
                    batches,
                    batchOrder);
            }
            finally
            {
                ArrayPool<MapScreenPoint>.Shared.Return(projected);
            }
            if (triangleCount == 0)
            {
                continue;
            }
            result.DrawableLineCount++;
            result.TriangleCount += triangleCount;
            if (!line.Style.DashArray.IsDefaultOrEmpty)
            {
                result.DashedLineCount++;
                result.DashTriangleCount += triangleCount;
            }
            result.AddAdvancedStyle(line.Style);
        }
        return tileOpacity < layer.Opacity;
    }

    internal static VectorLineStyle PrepareVectorLineForRasterization(
        VectorLineStyle style)
    {
        if (style.Width >= MinimumVectorLineRasterWidth)
        {
            return style;
        }
        double coverage = Math.Clamp(
            style.Width / MinimumVectorLineRasterWidth,
            0,
            1);
        return style with
        {
            Color = style.Color * (float)coverage,
            Width = MinimumVectorLineRasterWidth,
            Gradient = style.Gradient.IsDefaultOrEmpty
                ? []
                : [.. style.Gradient.Select(stop => stop with
                {
                    Color = stop.Color * (float)coverage,
                })],
        };
    }

    private static int AppendStyledVectorLineTriangles(
        ReadOnlySpan<MapScreenPoint> points,
        VectorLineStyle style,
        int styleLayerOrder,
        double opacity,
        double viewportWidth,
        double viewportHeight,
        double viewportPadding,
        Dictionary<VectorLineBatchKey, PooledGeometryBuffer> batches,
        List<VectorLineBatchKey> batchOrder)
    {
        int triangleCount = 0;
        if (style.Blur > 0)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                double amount = (3 - pass) / 3d;
                triangleCount += AppendVectorLineColorRuns(
                    points,
                    style with
                    {
                        Width = style.Width + (style.Blur * amount * 2),
                        Blur = 0,
                    },
                    styleLayerOrder,
                    pass,
                    opacity * (0.1 + (pass * 0.08)),
                    viewportWidth,
                    viewportHeight,
                    viewportPadding,
                    batches,
                    batchOrder);
            }
        }
        triangleCount += AppendVectorLineColorRuns(
            points,
            style with { Blur = 0 },
            styleLayerOrder,
            3,
            opacity,
            viewportWidth,
            viewportHeight,
            viewportPadding,
            batches,
            batchOrder);
        return triangleCount;
    }

    private static int AppendVectorLineColorRuns(
        ReadOnlySpan<MapScreenPoint> points,
        VectorLineStyle style,
        int styleLayerOrder,
        int passOrder,
        double opacity,
        double viewportWidth,
        double viewportHeight,
        double viewportPadding,
        Dictionary<VectorLineBatchKey, PooledGeometryBuffer> batches,
        List<VectorLineBatchKey> batchOrder)
    {
        if (style.Gradient.IsDefaultOrEmpty)
        {
            VectorLineBatchKey key = new(
                styleLayerOrder,
                passOrder,
                style.Color * (float)opacity);
            return AppendVectorLineTriangles(
                points,
                style with { Color = key.Color },
                viewportWidth,
                viewportHeight,
                viewportPadding,
                GetOrCreateVectorLineBuffer(key, batches, batchOrder));
        }

        double[] distances = new double[points.Length];
        for (int index = 1; index < points.Length; index++)
        {
            double deltaX = points[index].X - points[index - 1].X;
            double deltaY = points[index].Y - points[index - 1].Y;
            distances[index] = distances[index - 1] +
                Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        }
        double totalLength = distances[^1];
        if (!double.IsFinite(totalLength) || totalLength <= 1e-7)
        {
            return 0;
        }

        int triangleCount = 0;
        VectorLineStyle segmentStyle = style with
        {
            Cap = VectorLineCap.Butt,
            Join = VectorLineJoin.Bevel,
            Gradient = [],
        };
        Span<MapScreenPoint> segment = stackalloc MapScreenPoint[2];
        for (int index = 0; index + 1 < points.Length; index++)
        {
            double segmentLength = distances[index + 1] - distances[index];
            int subdivisionCount = (int)Math.Clamp(
                Math.Ceiling(segmentLength / 8),
                1,
                64);
            MapScreenPoint start = points[index];
            MapScreenPoint end = points[index + 1];
            for (int subdivision = 0;
                subdivision < subdivisionCount;
                subdivision++)
            {
                double startAmount = subdivision / (double)subdivisionCount;
                double endAmount = (subdivision + 1d) / subdivisionCount;
                double progress = (
                    distances[index] +
                    (segmentLength * ((startAmount + endAmount) / 2))) /
                    totalLength;
                Vector4 color = QuantizeLineGradientColor(
                    ResolveLineGradientColor(style.Gradient, progress) *
                        (float)opacity);
                VectorLineBatchKey key = new(
                    styleLayerOrder,
                    passOrder,
                    color);
                segment[0] = new MapScreenPoint(
                    start.X + ((end.X - start.X) * startAmount),
                    start.Y + ((end.Y - start.Y) * startAmount));
                segment[1] = new MapScreenPoint(
                    start.X + ((end.X - start.X) * endAmount),
                    start.Y + ((end.Y - start.Y) * endAmount));
                triangleCount += AppendVectorLineTriangles(
                    segment,
                    segmentStyle with { Color = color },
                    viewportWidth,
                    viewportHeight,
                    viewportPadding,
                    GetOrCreateVectorLineBuffer(key, batches, batchOrder));
            }
        }
        return triangleCount;
    }

    private static PooledGeometryBuffer GetOrCreateVectorLineBuffer(
        VectorLineBatchKey key,
        Dictionary<VectorLineBatchKey, PooledGeometryBuffer> batches,
        List<VectorLineBatchKey> batchOrder)
    {
        if (!batches.TryGetValue(key, out PooledGeometryBuffer? buffer))
        {
            buffer = new PooledGeometryBuffer();
            batches.Add(key, buffer);
            batchOrder.Add(key);
        }
        return buffer;
    }

    internal static Vector4 ResolveLineGradientColor(
        ImmutableArray<VectorLineGradientStop> stops,
        double progress)
    {
        if (stops.IsDefaultOrEmpty)
        {
            return default;
        }
        progress = Math.Clamp(progress, 0, 1);
        if (progress <= stops[0].Offset)
        {
            return stops[0].Color;
        }
        for (int index = 1; index < stops.Length; index++)
        {
            if (progress > stops[index].Offset)
            {
                continue;
            }
            VectorLineGradientStop previous = stops[index - 1];
            VectorLineGradientStop next = stops[index];
            double amount = (progress - previous.Offset) /
                (next.Offset - previous.Offset);
            return Vector4.Lerp(
                previous.Color,
                next.Color,
                (float)amount);
        }
        return stops[^1].Color;
    }

    internal static VectorLineStyleBatchMetrics GetVectorLineStyleBatchMetrics(
        IReadOnlyList<MapScreenPoint> points,
        VectorLineStyle style,
        double viewportWidth,
        double viewportHeight)
    {
        Dictionary<VectorLineBatchKey, PooledGeometryBuffer> batches = [];
        List<VectorLineBatchKey> order = [];
        try
        {
            int triangleCount = AppendStyledVectorLineTriangles(
                points is MapScreenPoint[] array ? array : points.ToArray(),
                style,
                0,
                1,
                viewportWidth,
                viewportHeight,
                0,
                batches,
                order);
            return new VectorLineStyleBatchMetrics(
                batches.Count,
                triangleCount,
                order.Select(key => key.PassOrder).Distinct().Count());
        }
        finally
        {
            foreach (PooledGeometryBuffer buffer in batches.Values)
            {
                buffer.Dispose();
            }
        }
    }

    private static Vector4 QuantizeLineGradientColor(Vector4 color) =>
        new(
            MathF.Round(color.X * 32) / 32,
            MathF.Round(color.Y * 32) / 32,
            MathF.Round(color.Z * 32) / 32,
            MathF.Round(color.W * 32) / 32);

    internal readonly record struct VectorLineStyleBatchMetrics(
        int BatchCount,
        int TriangleCount,
        int PassCount);

    internal static MapScreenPoint[] ProjectVectorLine(
        IReadOnlyList<VectorTilePoint> points,
        VisibleTile tile,
        double viewportWidth,
        double viewportHeight,
        double heading,
        double pitch)
    {
        MapScreenPoint[] projected = new MapScreenPoint[points.Count];
        ProjectVectorLine(
            points,
            tile,
            viewportWidth,
            viewportHeight,
            heading,
            pitch,
            projected);
        return projected;
    }

    private static void ProjectVectorLine(
        IReadOnlyList<VectorTilePoint> points,
        VisibleTile tile,
        double viewportWidth,
        double viewportHeight,
        double heading,
        double pitch,
        Span<MapScreenPoint> projected)
    {
        for (int index = 0; index < projected.Length; index++)
        {
            projected[index] = ProjectVectorPoint(
                points[index],
                tile,
                viewportWidth,
                viewportHeight,
                heading,
                pitch);
        }
    }

    private static MapScreenPoint ProjectVectorPoint(
        VectorTilePoint point,
        VisibleTile tile,
        double viewportWidth,
        double viewportHeight,
        double heading,
        double pitch)
    {
        double x = tile.Left + (point.X * tile.Size) - (viewportWidth / 2);
        double y = tile.Top + (point.Y * tile.Size) - (viewportHeight / 2);
        MapCamera.TransformViewportOffset(
            x,
            y,
            heading,
            pitch,
            viewportHeight,
            out x,
            out y);
        return new MapScreenPoint(
            x + (viewportWidth / 2),
            y + (viewportHeight / 2));
    }

    internal static MapScreenPoint[] ExpandVectorLineTriangles(
        IReadOnlyList<MapScreenPoint> points,
        VectorLineStyle style,
        double viewportWidth,
        double viewportHeight)
    {
        using PooledGeometryBuffer triangles = new();
        MapScreenPoint[] source = points as MapScreenPoint[] ??
            points.ToArray();
        AppendVectorLineTriangles(
            source,
            style,
            viewportWidth,
            viewportHeight,
            0,
            triangles);
        return triangles.ToArray();
    }

    private static int AppendVectorLineTriangles(
        ReadOnlySpan<MapScreenPoint> points,
        VectorLineStyle style,
        double viewportWidth,
        double viewportHeight,
        double viewportPadding,
        PooledGeometryBuffer triangles)
    {
        if (points.Length < 2 ||
            style.Width <= 0 ||
            viewportWidth <= 0 ||
            viewportHeight <= 0)
        {
            return 0;
        }

        if (style.Offset != 0 || style.GapWidth > 0)
        {
            MapScreenPoint[] center = OffsetVectorLine(points, style.Offset);
            VectorLineStyle centeredStyle = style with
            {
                Offset = 0,
                GapWidth = 0,
            };
            if (style.GapWidth <= 0)
            {
                return AppendVectorLineTriangles(
                    center,
                    centeredStyle,
                    viewportWidth,
                    viewportHeight,
                    viewportPadding,
                    triangles);
            }
            double bandOffset = (style.GapWidth + style.Width) / 2;
            int gapInitialCount = triangles.Count;
            AppendVectorLineTriangles(
                OffsetVectorLine(center, bandOffset),
                centeredStyle,
                viewportWidth,
                viewportHeight,
                viewportPadding,
                triangles);
            AppendVectorLineTriangles(
                OffsetVectorLine(center, -bandOffset),
                centeredStyle,
                viewportWidth,
                viewportHeight,
                viewportPadding,
                triangles);
            return (triangles.Count - gapInitialCount) / 3;
        }

        if (!style.DashArray.IsDefaultOrEmpty)
        {
            return AppendDashedVectorLineTriangles(
                points,
                style with { DashArray = [] },
                style.DashArray.AsSpan(),
                viewportWidth,
                viewportHeight,
                viewportPadding,
                triangles);
        }

        int initialCount = triangles.Count;
        double halfWidth = style.Width / 2;
        for (int index = 0; index + 1 < points.Length; index++)
        {
            MapScreenPoint start = points[index];
            MapScreenPoint end = points[index + 1];
            double deltaX = end.X - start.X;
            double deltaY = end.Y - start.Y;
            double length = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
            if (!double.IsFinite(length) || length <= 1e-7)
            {
                continue;
            }
            double unitX = deltaX / length;
            double unitY = deltaY / length;
            if (style.Cap == VectorLineCap.Square)
            {
                if (index == 0)
                {
                    start = new MapScreenPoint(
                        start.X - (unitX * halfWidth),
                        start.Y - (unitY * halfWidth));
                }
                if (index + 2 == points.Length)
                {
                    end = new MapScreenPoint(
                        end.X + (unitX * halfWidth),
                        end.Y + (unitY * halfWidth));
                }
            }
            if (!RectangleIntersectsViewport(
                    start,
                    end,
                    halfWidth,
                    viewportWidth,
                    viewportHeight,
                    viewportPadding))
            {
                continue;
            }

            double perpendicularX = -unitY * halfWidth;
            double perpendicularY = unitX * halfWidth;
            MapScreenPoint first = new(
                start.X + perpendicularX,
                start.Y + perpendicularY);
            MapScreenPoint second = new(
                end.X + perpendicularX,
                end.Y + perpendicularY);
            MapScreenPoint third = new(
                end.X - perpendicularX,
                end.Y - perpendicularY);
            MapScreenPoint fourth = new(
                start.X - perpendicularX,
                start.Y - perpendicularY);
            AddQuad(triangles, first, second, third, fourth);
        }

        for (int index = 1; index + 1 < points.Length; index++)
        {
            MapScreenPoint previous = points[index - 1];
            MapScreenPoint join = points[index];
            MapScreenPoint next = points[index + 1];
            if (!PointNearViewport(
                    join,
                    halfWidth,
                    viewportWidth,
                    viewportHeight,
                    viewportPadding))
            {
                continue;
            }
            if (style.Join == VectorLineJoin.Round)
            {
                AddCircle(triangles, join, halfWidth);
            }
            else if (style.Join == VectorLineJoin.Miter)
            {
                AddMiterJoin(
                    triangles,
                    previous,
                    join,
                    next,
                    halfWidth,
                    style.MiterLimit);
            }
            else
            {
                AddBevelJoin(triangles, previous, join, next, halfWidth);
            }
        }
        if (style.Cap == VectorLineCap.Round)
        {
            if (PointNearViewport(
                    points[0],
                    halfWidth,
                    viewportWidth,
                    viewportHeight,
                    viewportPadding))
            {
                AddCircle(triangles, points[0], halfWidth);
            }
            if (PointNearViewport(
                    points[^1],
                    halfWidth,
                    viewportWidth,
                    viewportHeight,
                    viewportPadding))
            {
                AddCircle(triangles, points[^1], halfWidth);
            }
        }
        return (triangles.Count - initialCount) / 3;
    }

    private static MapScreenPoint[] OffsetVectorLine(
        ReadOnlySpan<MapScreenPoint> points,
        double offset)
    {
        if (offset == 0)
        {
            return points.ToArray();
        }
        MapScreenPoint[] result = new MapScreenPoint[points.Length];
        for (int index = 0; index < points.Length; index++)
        {
            MapScreenPoint previous = points[Math.Max(0, index - 1)];
            MapScreenPoint current = points[index];
            MapScreenPoint next = points[Math.Min(points.Length - 1, index + 1)];
            bool hasPrevious = TryGetRightNormal(
                previous,
                current,
                out MapScreenPoint previousNormal);
            bool hasNext = TryGetRightNormal(
                current,
                next,
                out MapScreenPoint nextNormal);
            if (!hasPrevious && !hasNext)
            {
                result[index] = current;
                continue;
            }
            if (!hasPrevious)
            {
                previousNormal = nextNormal;
            }
            if (!hasNext)
            {
                nextNormal = previousNormal;
            }
            double miterX = previousNormal.X + nextNormal.X;
            double miterY = previousNormal.Y + nextNormal.Y;
            double miterLength = Math.Sqrt(
                (miterX * miterX) + (miterY * miterY));
            double denominator = miterLength <= 1e-7
                ? 0
                : ((miterX / miterLength) * nextNormal.X) +
                  ((miterY / miterLength) * nextNormal.Y);
            double scale = Math.Abs(denominator) <= 0.25
                ? offset
                : offset / denominator;
            scale = Math.Clamp(
                scale,
                -Math.Abs(offset) * 4,
                Math.Abs(offset) * 4);
            result[index] = miterLength <= 1e-7
                ? new MapScreenPoint(
                    current.X + (nextNormal.X * offset),
                    current.Y + (nextNormal.Y * offset))
                : new MapScreenPoint(
                    current.X + ((miterX / miterLength) * scale),
                    current.Y + ((miterY / miterLength) * scale));
        }
        return result;
    }

    private static bool TryGetRightNormal(
        MapScreenPoint start,
        MapScreenPoint end,
        out MapScreenPoint normal)
    {
        double deltaX = end.X - start.X;
        double deltaY = end.Y - start.Y;
        double length = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        if (!double.IsFinite(length) || length <= 1e-7)
        {
            normal = default;
            return false;
        }
        normal = new MapScreenPoint(deltaY / length, -deltaX / length);
        return true;
    }

    private static int AppendDashedVectorLineTriangles(
        ReadOnlySpan<MapScreenPoint> points,
        VectorLineStyle style,
        ReadOnlySpan<double> dashArray,
        double viewportWidth,
        double viewportHeight,
        double viewportPadding,
        PooledGeometryBuffer triangles)
    {
        const double epsilon = 1e-7;
        int initialCount = triangles.Count;
        int dashIndex = 0;
        double dashRemaining = dashArray[0];
        bool draw = true;
        List<MapScreenPoint> span = [];

        for (int segmentIndex = 0;
            segmentIndex + 1 < points.Length;
            segmentIndex++)
        {
            MapScreenPoint segmentStart = points[segmentIndex];
            MapScreenPoint segmentEnd = points[segmentIndex + 1];
            double deltaX = segmentEnd.X - segmentStart.X;
            double deltaY = segmentEnd.Y - segmentStart.Y;
            double segmentLength = Math.Sqrt(
                (deltaX * deltaX) + (deltaY * deltaY));
            if (!double.IsFinite(segmentLength) || segmentLength <= epsilon)
            {
                continue;
            }

            double consumed = 0;
            while (consumed < segmentLength - epsilon)
            {
                int skippedEntries = 0;
                while (dashRemaining <= epsilon &&
                    skippedEntries++ < dashArray.Length)
                {
                    if (draw && span.Count >= 2)
                    {
                        AppendVectorLineTriangles(
                            CollectionsMarshal.AsSpan(span),
                            style,
                            viewportWidth,
                            viewportHeight,
                            viewportPadding,
                            triangles);
                        span.Clear();
                    }
                    dashIndex = (dashIndex + 1) % dashArray.Length;
                    draw = !draw;
                    dashRemaining = dashArray[dashIndex];
                }
                if (dashRemaining <= epsilon)
                {
                    break;
                }

                double length = Math.Min(
                    dashRemaining,
                    segmentLength - consumed);
                double startAmount = consumed / segmentLength;
                double endAmount = (consumed + length) / segmentLength;
                MapScreenPoint start = new(
                    segmentStart.X + (deltaX * startAmount),
                    segmentStart.Y + (deltaY * startAmount));
                MapScreenPoint end = new(
                    segmentStart.X + (deltaX * endAmount),
                    segmentStart.Y + (deltaY * endAmount));
                if (draw)
                {
                    AddDistinctPoint(span, start);
                    AddDistinctPoint(span, end);
                }
                consumed += length;
                dashRemaining -= length;
            }
        }

        if (draw && span.Count >= 2)
        {
            AppendVectorLineTriangles(
                CollectionsMarshal.AsSpan(span),
                style,
                viewportWidth,
                viewportHeight,
                viewportPadding,
                triangles);
        }
        return (triangles.Count - initialCount) / 3;
    }

    private static void AddDistinctPoint(
        List<MapScreenPoint> points,
        MapScreenPoint point)
    {
        if (points.Count == 0 ||
            Math.Abs(points[^1].X - point.X) > 1e-7 ||
            Math.Abs(points[^1].Y - point.Y) > 1e-7)
        {
            points.Add(point);
        }
    }

    private static void AddQuad(
        PooledGeometryBuffer triangles,
        MapScreenPoint first,
        MapScreenPoint second,
        MapScreenPoint third,
        MapScreenPoint fourth)
    {
        triangles.Add(first);
        triangles.Add(second);
        triangles.Add(third);
        triangles.Add(first);
        triangles.Add(third);
        triangles.Add(fourth);
    }

    private static void AddBevelJoin(
        PooledGeometryBuffer triangles,
        MapScreenPoint previous,
        MapScreenPoint join,
        MapScreenPoint next,
        double radius)
    {
        if (!TryGetPerpendicular(previous, join, radius, out MapScreenPoint first) ||
            !TryGetPerpendicular(join, next, radius, out MapScreenPoint second))
        {
            return;
        }
        triangles.Add(join);
        triangles.Add(new MapScreenPoint(join.X + first.X, join.Y + first.Y));
        triangles.Add(new MapScreenPoint(join.X + second.X, join.Y + second.Y));
        triangles.Add(join);
        triangles.Add(new MapScreenPoint(join.X - first.X, join.Y - first.Y));
        triangles.Add(new MapScreenPoint(join.X - second.X, join.Y - second.Y));
    }

    private static void AddMiterJoin(
        PooledGeometryBuffer triangles,
        MapScreenPoint previous,
        MapScreenPoint join,
        MapScreenPoint next,
        double radius,
        double miterLimit)
    {
        if (!TryGetPerpendicular(previous, join, radius, out MapScreenPoint first) ||
            !TryGetPerpendicular(join, next, radius, out MapScreenPoint second))
        {
            return;
        }
        AddMiterSide(
            triangles,
            previous,
            join,
            next,
            first,
            second,
            radius,
            miterLimit);
        AddMiterSide(
            triangles,
            previous,
            join,
            next,
            new MapScreenPoint(-first.X, -first.Y),
            new MapScreenPoint(-second.X, -second.Y),
            radius,
            miterLimit);
    }

    private static void AddMiterSide(
        PooledGeometryBuffer triangles,
        MapScreenPoint previous,
        MapScreenPoint join,
        MapScreenPoint next,
        MapScreenPoint firstOffset,
        MapScreenPoint secondOffset,
        double radius,
        double miterLimit)
    {
        MapScreenPoint first = new(
            join.X + firstOffset.X,
            join.Y + firstOffset.Y);
        MapScreenPoint second = new(
            join.X + secondOffset.X,
            join.Y + secondOffset.Y);
        if (!TryIntersectLines(
                new MapScreenPoint(
                    previous.X + firstOffset.X,
                    previous.Y + firstOffset.Y),
                first,
                second,
                new MapScreenPoint(
                    next.X + secondOffset.X,
                    next.Y + secondOffset.Y),
                out MapScreenPoint miter))
        {
            AddJoinTriangle(triangles, join, first, second);
            return;
        }
        double miterX = miter.X - join.X;
        double miterY = miter.Y - join.Y;
        double length = Math.Sqrt((miterX * miterX) + (miterY * miterY));
        if (!double.IsFinite(length) || length > radius * miterLimit)
        {
            AddJoinTriangle(triangles, join, first, second);
            return;
        }
        triangles.Add(first);
        triangles.Add(miter);
        triangles.Add(second);
    }

    private static void AddJoinTriangle(
        PooledGeometryBuffer triangles,
        MapScreenPoint join,
        MapScreenPoint first,
        MapScreenPoint second)
    {
        triangles.Add(join);
        triangles.Add(first);
        triangles.Add(second);
    }

    private static bool TryIntersectLines(
        MapScreenPoint firstStart,
        MapScreenPoint firstEnd,
        MapScreenPoint secondStart,
        MapScreenPoint secondEnd,
        out MapScreenPoint intersection)
    {
        double firstX = firstEnd.X - firstStart.X;
        double firstY = firstEnd.Y - firstStart.Y;
        double secondX = secondEnd.X - secondStart.X;
        double secondY = secondEnd.Y - secondStart.Y;
        double denominator = (firstX * secondY) - (firstY * secondX);
        if (Math.Abs(denominator) <= 1e-7)
        {
            intersection = default;
            return false;
        }
        double deltaX = secondStart.X - firstStart.X;
        double deltaY = secondStart.Y - firstStart.Y;
        double amount = ((deltaX * secondY) - (deltaY * secondX)) /
            denominator;
        intersection = new MapScreenPoint(
            firstStart.X + (firstX * amount),
            firstStart.Y + (firstY * amount));
        return double.IsFinite(intersection.X) &&
            double.IsFinite(intersection.Y);
    }

    private static bool TryGetPerpendicular(
        MapScreenPoint start,
        MapScreenPoint end,
        double radius,
        out MapScreenPoint perpendicular)
    {
        double deltaX = end.X - start.X;
        double deltaY = end.Y - start.Y;
        double length = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        if (!double.IsFinite(length) || length <= 1e-7)
        {
            perpendicular = default;
            return false;
        }
        perpendicular = new MapScreenPoint(
            (-deltaY / length) * radius,
            (deltaX / length) * radius);
        return true;
    }

    private static void AddCircle(
        PooledGeometryBuffer triangles,
        MapScreenPoint center,
        double radius)
    {
        const int segmentCount = 8;
        MapScreenPoint previous = new(center.X + radius, center.Y);
        for (int index = 1; index <= segmentCount; index++)
        {
            double angle = index * Math.Tau / segmentCount;
            MapScreenPoint current = new(
                center.X + (Math.Cos(angle) * radius),
                center.Y + (Math.Sin(angle) * radius));
            triangles.Add(center);
            triangles.Add(previous);
            triangles.Add(current);
            previous = current;
        }
    }

    private static bool RectangleIntersectsViewport(
        MapScreenPoint start,
        MapScreenPoint end,
        double padding,
        double viewportWidth,
        double viewportHeight,
        double viewportPadding) =>
        Math.Min(start.X, end.X) - padding <
            viewportWidth + viewportPadding &&
        Math.Min(start.Y, end.Y) - padding <
            viewportHeight + viewportPadding &&
        Math.Max(start.X, end.X) + padding > -viewportPadding &&
        Math.Max(start.Y, end.Y) + padding > -viewportPadding;

    private static bool PointNearViewport(
        MapScreenPoint point,
        double padding,
        double viewportWidth,
        double viewportHeight,
        double viewportPadding) =>
        point.X + padding > -viewportPadding &&
        point.Y + padding > -viewportPadding &&
        point.X - padding < viewportWidth + viewportPadding &&
        point.Y - padding < viewportHeight + viewportPadding;

    private static ulong GetFallbackZoomMask(RasterLayerState state)
    {
        ulong mask = 0;
        foreach (int zoom in state.FallbackTileZooms)
        {
            mask |= 1UL << zoom;
        }
        return mask;
    }

    private readonly record struct VectorLineBatchKey(
        int StyleLayerOrder,
        int PassOrder,
        Vector4 Color);

    private static int CompareVectorLineBatches(
        VectorLineBatchKey left,
        VectorLineBatchKey right)
    {
        int order = left.StyleLayerOrder.CompareTo(right.StyleLayerOrder);
        return order != 0 ? order : left.PassOrder.CompareTo(right.PassOrder);
    }

    private sealed record VectorLineCachedBatch(
        VectorLineBatchKey Key,
        GpuGeometryBuffer Buffer);

    private sealed class VectorLineFrameCache(
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
        VectorLineCachedBatch[] batches,
        HashSet<VectorTileInstanceKey> includedTiles,
        VectorLineRenderResult result,
        int vertexCount,
        long byteSize) : IDisposable
    {
        private long Generation { get; } = state.Generation;

        private long SceneVersion { get; } = state.SceneVersion;

        private long VectorTileVersion { get; } = vectorTileVersion;

        internal double Longitude { get; } = longitude;

        internal double Latitude { get; } = latitude;

        internal VectorLineCachedBatch[] Batches { get; } = batches;

        internal IReadOnlySet<VectorTileInstanceKey> IncludedTiles { get; } =
            includedTiles;

        internal VectorLineRenderResult Result { get; } = result;

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
            foreach (VectorLineCachedBatch batch in Batches)
            {
                batch.Buffer.Dispose();
            }
        }
    }

    private struct VectorLineRenderResult
    {
        public VectorLineRenderResult()
        {
            MinimumFallbackOpacity = 1;
        }

        internal int CandidateLineCount;
        internal int DrawableLineCount;
        internal int TriangleCount;
        internal int EvaluationFailureCount;
        internal int DrawCallCount;
        internal int DashedLineCount;
        internal int DashTriangleCount;
        internal int OffsetLineCount;
        internal int GapLineCount;
        internal int GradientLineCount;
        internal int BlurredLineCount;
        internal int MiterLineCount;
        internal int FallbackInstanceCount;
        internal int DrawnFallbackInstanceCount;
        internal int FadedFallbackInstanceCount;
        internal int SuppressedFallbackInstanceCount;
        internal double MaximumFallbackZoomDifference;
        internal double MinimumFallbackOpacity;
        internal double MaximumFallbackOpacity;

        internal void Add(VectorLineRenderResult other)
        {
            CandidateLineCount += other.CandidateLineCount;
            DrawableLineCount += other.DrawableLineCount;
            TriangleCount += other.TriangleCount;
            EvaluationFailureCount += other.EvaluationFailureCount;
            DrawCallCount += other.DrawCallCount;
            DashedLineCount += other.DashedLineCount;
            DashTriangleCount += other.DashTriangleCount;
            OffsetLineCount += other.OffsetLineCount;
            GapLineCount += other.GapLineCount;
            GradientLineCount += other.GradientLineCount;
            BlurredLineCount += other.BlurredLineCount;
            MiterLineCount += other.MiterLineCount;
        }

        internal void AddAdvancedStyle(VectorLineStyle style)
        {
            OffsetLineCount += style.Offset != 0 ? 1 : 0;
            GapLineCount += style.GapWidth > 0 ? 1 : 0;
            GradientLineCount += !style.Gradient.IsDefaultOrEmpty ? 1 : 0;
            BlurredLineCount += style.Blur > 0 ? 1 : 0;
            MiterLineCount += style.Join == VectorLineJoin.Miter ? 1 : 0;
        }
    }

    private enum VectorGeometryKind
    {
        Line = 1,
        Polygon = 2,
    }
}
