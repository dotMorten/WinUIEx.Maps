using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using WinUIEx.Maps.Rendering.Diagnostics;

namespace WinUIEx.Maps.Rendering;

internal sealed partial class MapRenderer
{
    private readonly ConcurrentQueue<CompletedVectorGeometryPreparation>
        _completedVectorGeometryPreparations = new();
    private readonly object _vectorGeometryPreparationSync = new();
    private VectorGeometryPreparationJob? _vectorGeometryPreparationJob;
    private VectorGeometryPreparationJob? _runningVectorGeometryPreparationJob;
    internal Action? VectorGeometryPreparationStartingForTest { get; set; }

    internal int ActiveVectorGeometryPreparations
    {
        get
        {
            lock (_vectorGeometryPreparationSync)
                return _runningVectorGeometryPreparationJob is null ? 0 : 1;
        }
    }

    private bool DeferVectorGeometryRebuild(
        LayerRenderSnapshot layer,
        RasterLayerState state,
        ulong fallbackMask)
    {
        if (fallbackMask != 0 ||
            _zoomAnimation.IsActive ||
            _headingAnimation.IsActive ||
            _pitchAnimation.IsActive ||
            !CanEnumerateRasterScene(_displayZoom, state.Scene!.TileZoom))
        {
            return false;
        }

        VectorGeometryPreparationKey key = new(
            layer.RuntimeId,
            layer.Style,
            layer.Opacity,
            state.Generation,
            state.SceneVersion,
            _vectorTileVersion,
            _deviceEpoch,
            _displayZoom,
            _displayHeading,
            _displayPitch,
            _viewportWidth,
            _viewportHeight);
        lock (_vectorGeometryPreparationSync)
        {
            if (_vectorGeometryPreparationJob?.Key == key)
            {
                return true;
            }
            if (_runningVectorGeometryPreparationJob is { } running)
            {
                // The current renderer scene is the replaceable pending request.
                // Capture it only after obsolete work releases its input/device.
                if (running.Key.RuntimeId == key.RuntimeId)
                    running.Cancellation.Cancel();
                return true;
            }
            if (!_completedVectorGeometryPreparations.IsEmpty)
                return true;
        }

        if (!TryCaptureVectorGeometryPreparation(
                layer,
                state,
                key,
                out VectorGeometryPreparationInput input))
        {
            return true;
        }

        CancellationTokenSource cancellation = new();
        VectorGeometryPreparationJob job = new(key, cancellation);
        lock (_vectorGeometryPreparationSync)
        {
            _vectorGeometryPreparationJob = job;
            _runningVectorGeometryPreparationJob = job;
        }
        _ = Task.Run(
                () => BuildVectorGeometryFrame(input, cancellation.Token,
                    VectorGeometryPreparationStartingForTest))
            .ContinueWith(
                task =>
                {
                    bool stale;
                    lock (_vectorGeometryPreparationSync)
                    {
                        stale = Volatile.Read(ref _uploadDisposed) ||
                            cancellation.IsCancellationRequested ||
                            !ReferenceEquals(
                                _vectorGeometryPreparationJob,
                                job);
                        if (stale && ReferenceEquals(_vectorGeometryPreparationJob, job))
                            _vectorGeometryPreparationJob = null;
                        if (!stale)
                        {
                            _runningVectorGeometryPreparationJob = null;
                            _completedVectorGeometryPreparations.Enqueue(
                                new CompletedVectorGeometryPreparation(
                                    job,
                                    task));
                        }
                    }
                    if (stale)
                    {
                        if (task.Status == TaskStatus.RanToCompletion)
                        {
                            task.Result.Dispose();
                        }
                        else
                        {
                            _ = task.Exception;
                        }
                        lock (_vectorGeometryPreparationSync)
                        {
                            _runningVectorGeometryPreparationJob = null;
                            cancellation.Dispose();
                        }
                        if (!Volatile.Read(ref _uploadDisposed))
                            RequestRender();
                        return;
                    }
                    RequestRender();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        return true;
    }

    private bool TryCaptureVectorGeometryPreparation(
        LayerRenderSnapshot layer,
        RasterLayerState state,
        VectorGeometryPreparationKey key,
        out VectorGeometryPreparationInput input)
    {
        List<VectorLinePreparationTile> lineTiles = [];
        List<VectorPolygonPreparationTile> polygonTiles = [];
        HashSet<VectorTileInstanceKey> includedTiles = [];
        MapScene scene = CreateCurrentRasterScene(state.Scene!.TileZoom);
        foreach (VisibleTile visibleTile in scene.VisibleTiles)
        {
            RasterTileKey tileKey = new(layer.RuntimeId, visibleTile.Id);
            if (!_vectorTiles.TryGetValue(
                    tileKey,
                    out VectorTileCacheEntry? tile))
            {
                continue;
            }

            tile.MarkUsed();
            double tileOpacity = ComputeLayerTileOpacity(
                Stopwatch.GetElapsedTime(tile.ReadyTimestamp),
                layer.FadeDuration,
                layer.Opacity);
            if (tileOpacity < layer.Opacity)
            {
                input = null!;
                return false;
            }

            includedTiles.Add(new(tileKey, visibleTile.WorldX));
            lineTiles.Add(new(
                visibleTile,
                tile.GetLines(_displayZoom)));
            polygonTiles.Add(new(
                visibleTile,
                tile.GetPolygons(_displayZoom)));
        }

        IntPtr devicePointer = DevicePointer;
        if (devicePointer == IntPtr.Zero)
        {
            input = null!;
            return false;
        }
        Marshal.AddRef(devicePointer);
        input = new VectorGeometryPreparationInput(
            key,
            layer,
            devicePointer,
            _displayLongitude,
            _displayLatitude,
            _displayZoom,
            _displayHeading,
            _displayPitch,
            _viewportWidth,
            _viewportHeight,
            state.VectorStyleAssets?.ResolveBackgrounds(_displayZoom) ??
                new VectorBackgroundResolution([], 0),
            lineTiles.ToArray(),
            polygonTiles.ToArray(),
            includedTiles);
        return true;
    }

    private void ApplyCompletedVectorGeometryPreparation(
        LayerRenderSnapshot layer,
        RasterLayerState state)
    {
        if (_completedVectorGeometryPreparations.TryPeek(out var next) &&
            next.Job.Key.RuntimeId != layer.RuntimeId)
        {
            foreach (LayerRenderSnapshot candidate in _layerRenderPlan)
            {
                if (candidate.RuntimeId == next.Job.Key.RuntimeId &&
                    candidate.IsVisible && candidate.Opacity > 0 &&
                    _displayZoom >= candidate.MinZoom && _displayZoom < candidate.MaxZoom &&
                    _rasterLayers.ContainsKey(candidate.RuntimeId))
                {
                    return;
                }
            }
        }
        while (_completedVectorGeometryPreparations.TryDequeue(
            out CompletedVectorGeometryPreparation? completed))
        {
            if (completed is null)
            {
                continue;
            }
            completed.Job.Cancellation.Dispose();
            if (completed.Task.Status != TaskStatus.RanToCompletion)
            {
                _ = completed.Task.Exception;
                lock (_vectorGeometryPreparationSync)
                {
                    if (ReferenceEquals(
                            _vectorGeometryPreparationJob,
                            completed.Job))
                    {
                        _vectorGeometryPreparationJob = null;
                    }
                }
                continue;
            }

            PreparedVectorGeometryFrame prepared = completed.Task.Result;
            bool currentJob;
            lock (_vectorGeometryPreparationSync)
            {
                currentJob = ReferenceEquals(
                    _vectorGeometryPreparationJob,
                    completed.Job);
                if (currentJob)
                {
                    _vectorGeometryPreparationJob = null;
                }
            }
            if (!currentJob ||
                !prepared.Key.Matches(
                    layer,
                    state,
                    _vectorTileVersion,
                    _deviceEpoch,
                    _displayZoom,
                    _displayHeading,
                    _displayPitch,
                    _viewportWidth,
                    _viewportHeight))
            {
                prepared.ReportCompletion(accepted: false);
                prepared.Dispose();
                continue;
            }

            VectorLineCachedBatch[]? lineBatches =
                prepared.TakeLineBatches();
            VectorPolygonCachedBatch[]? polygonBatches =
                prepared.TakePolygonBatches();
            try
            {
                VectorLineFrameCache lineCache = new(
                    layer,
                    state,
                    prepared.Key.VectorTileVersion,
                    0,
                    prepared.Longitude,
                    prepared.Latitude,
                    prepared.Zoom,
                    prepared.Heading,
                    prepared.Pitch,
                    prepared.ViewportWidth,
                    prepared.ViewportHeight,
                    lineBatches,
                    prepared.IncludedTiles,
                    prepared.LineResult,
                    prepared.LineVertexCount,
                    lineBatches.Sum(batch => batch.Buffer.ByteSize));
                VectorPolygonFrameCache? polygonCache =
                    prepared.PolygonResult.HasPatternPolygons
                        ? null
                        : new(
                            layer,
                            state,
                            prepared.Key.VectorTileVersion,
                            0,
                            prepared.Longitude,
                            prepared.Latitude,
                            prepared.Zoom,
                            prepared.Heading,
                            prepared.Pitch,
                            prepared.ViewportWidth,
                            prepared.ViewportHeight,
                            polygonBatches,
                            prepared.IncludedTiles,
                            prepared.PolygonResult,
                            prepared.PolygonVertexCount,
                            polygonBatches.Sum(batch =>
                                batch.Buffer.ByteSize));
                _vectorLineFrameCache?.Dispose();
                _vectorPolygonFrameCache?.Dispose();
                _vectorLineFrameCache = lineCache;
                _vectorPolygonFrameCache = polygonCache;
                lineBatches = null;
                if (polygonCache is not null)
                {
                    polygonBatches = null;
                }
                prepared.ReportCompletion(accepted: true);
            }
            finally
            {
                if (lineBatches is not null)
                {
                    foreach (VectorLineCachedBatch batch in lineBatches)
                    {
                        batch.Buffer.Dispose();
                    }
                }
                if (polygonBatches is not null)
                {
                    foreach (VectorPolygonCachedBatch batch in polygonBatches)
                    {
                        batch.Buffer.Dispose();
                    }
                }
                prepared.Dispose();
            }
        }
    }

    private void CancelVectorGeometryPreparation()
    {
        List<CompletedVectorGeometryPreparation> completedJobs = [];
        lock (_vectorGeometryPreparationSync)
        {
            _runningVectorGeometryPreparationJob?.Cancellation.Cancel();
            _vectorGeometryPreparationJob = null;
            while (_completedVectorGeometryPreparations.TryDequeue(
                out CompletedVectorGeometryPreparation? completed))
            {
                if (completed is not null)
                {
                    completedJobs.Add(completed);
                }
            }
        }
        foreach (CompletedVectorGeometryPreparation completed in completedJobs)
        {
            completed.Job.Cancellation.Dispose();
            if (completed.Task.Status == TaskStatus.RanToCompletion)
            {
                completed.Task.Result.Dispose();
            }
            else
            {
                _ = completed.Task.Exception;
            }
        }
    }

    private static PreparedVectorGeometryFrame BuildVectorGeometryFrame(
        VectorGeometryPreparationInput input,
        CancellationToken cancellationToken,
        Action? starting = null)
    {
        bool tracePreparation = MapControlEventSource.Log.IsEnabled(
            System.Diagnostics.Tracing.EventLevel.Informational,
            MapControlEventSource.Keywords.Tiles | MapControlEventSource.Keywords.VectorTiles);
        long preparationStart = tracePreparation ? Stopwatch.GetTimestamp() : 0;
        PreparedVectorGeometryFrame? prepared = null;
        try
        {
            starting?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            prepared = new(input) { TraceTiming = tracePreparation };
            VectorPolygonPreparationTile[] polygonTiles = input.PolygonTiles;
            foreach (VectorPolygonPreparationTile tile in polygonTiles)
            {
                foreach (VectorTileStyledPolygon polygon in tile.Resolution.Polygons)
                {
                    if (polygon.Style.HasPattern)
                    {
                        prepared.PolygonResult.HasPatternPolygons = true;
                        break;
                    }
                }
                if (prepared.PolygonResult.HasPatternPolygons)
                    break;
            }
            if (prepared.PolygonResult.HasPatternPolygons)
                polygonTiles = [];
            prepared.PolygonResult.CandidatePolygonCount +=
                input.Backgrounds.Backgrounds.Length;
            prepared.PolygonResult.EvaluationFailureCount +=
                input.Backgrounds.EvaluationFailureCount;
            if (!prepared.PolygonResult.HasPatternPolygons)
                AppendVectorBackgrounds(
                input.Backgrounds,
                input.Layer.Opacity,
                input.ViewportWidth,
                input.ViewportHeight,
                VectorGeometryCachePadding,
                prepared);
            foreach (VectorPolygonPreparationTile tile in polygonTiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                prepared.PolygonResult.CandidatePolygonCount +=
                    tile.Resolution.Polygons.Length;
                prepared.PolygonResult.EvaluationFailureCount +=
                    tile.Resolution.EvaluationFailureCount;
                foreach (VectorTileStyledPolygon polygon in
                    tile.Resolution.Polygons)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (polygon.Style.HasPattern)
                    {
                        prepared.PolygonResult.HasPatternPolygons = true;
                        continue;
                    }
                    VectorPolygonBatchKey key = new(
                        polygon.StyleLayerOrder,
                        VectorPolygonBatchKind.Fill,
                        polygon.Style.Color * (float)input.Layer.Opacity);
                    PooledGeometryBuffer buffer =
                        prepared.GetPolygonBuffer(key);
                    int triangleCount =
                        AppendProjectedVectorPolygonTriangles(
                            polygon.FillTriangles,
                            tile.Tile,
                            input.ViewportWidth,
                            input.ViewportHeight,
                            input.Heading,
                            input.Pitch,
                            VectorGeometryCachePadding,
                            polygon.Style.TranslateX,
                            polygon.Style.TranslateY,
                            polygon.Style.TranslateAnchor,
                            buffer);
                    if (triangleCount != 0)
                    {
                        prepared.PolygonResult.DrawablePolygonCount++;
                        prepared.PolygonResult.TriangleCount += triangleCount;
                    }
                    if (polygon.Style.Antialias &&
                        polygon.Style.OutlineColor is Vector4 outlineColor &&
                        outlineColor.W > 0)
                    {
                        VectorPolygonBatchKey outlineKey = new(
                            polygon.StyleLayerOrder,
                            VectorPolygonBatchKind.Outline,
                            outlineColor * (float)input.Layer.Opacity);
                        PooledGeometryBuffer outlineBuffer =
                            prepared.GetPolygonBuffer(outlineKey);
                        VectorLineStyle outlineStyle = new(
                            outlineKey.Color,
                            1,
                            VectorLineCap.Butt,
                            VectorLineJoin.Miter);
                        foreach (VectorTileRing ring in polygon.Rings)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            MapScreenPoint[] projected =
                                ArrayPool<MapScreenPoint>.Shared.Rent(
                                    ring.Points.Length);
                            try
                            {
                                for (int index = 0;
                                    index < ring.Points.Length;
                                    index++)
                                {
                                    projected[index] = ProjectVectorPoint(
                                        ring.Points[index],
                                        tile.Tile,
                                        input.ViewportWidth,
                                        input.ViewportHeight,
                                        input.Heading,
                                        input.Pitch,
                                        polygon.Style.TranslateX,
                                        polygon.Style.TranslateY,
                                        polygon.Style.TranslateAnchor);
                                }
                                prepared.PolygonResult.OutlineTriangleCount +=
                                    AppendVectorLineTriangles(
                                        projected.AsSpan(
                                            0,
                                            ring.Points.Length),
                                        outlineStyle,
                                        input.ViewportWidth,
                                        input.ViewportHeight,
                                        VectorGeometryCachePadding,
                                        outlineBuffer);
                            }
                            finally
                            {
                                ArrayPool<MapScreenPoint>.Shared.Return(
                                    projected);
                            }
                        }
                    }
                }
            }

            foreach (VectorLinePreparationTile tile in input.LineTiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                prepared.LineResult.CandidateLineCount +=
                    tile.Resolution.Lines.Length;
                prepared.LineResult.EvaluationFailureCount +=
                    tile.Resolution.EvaluationFailureCount;
                foreach (VectorTileStyledLine line in tile.Resolution.Lines)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    VectorLineStyle style =
                        PrepareVectorLineForRasterization(line.Style);
                    MapScreenPoint[] projected =
                        ArrayPool<MapScreenPoint>.Shared.Rent(
                            line.Points.Length);
                    int triangleCount;
                    try
                    {
                        ProjectVectorLine(
                            line.Points,
                            tile.Tile,
                            input.ViewportWidth,
                            input.ViewportHeight,
                            input.Heading,
                            input.Pitch,
                            projected.AsSpan(0, line.Points.Length));
                        triangleCount = AppendStyledVectorLineTriangles(
                            projected.AsSpan(0, line.Points.Length),
                            style,
                            line.StyleLayerOrder,
                            input.Layer.Opacity,
                            input.ViewportWidth,
                            input.ViewportHeight,
                            VectorGeometryCachePadding,
                            prepared.LineBatches,
                            prepared.LineBatchOrder);
                    }
                    finally
                    {
                        ArrayPool<MapScreenPoint>.Shared.Return(projected);
                    }
                    if (triangleCount != 0)
                    {
                        prepared.LineResult.DrawableLineCount++;
                        prepared.LineResult.TriangleCount += triangleCount;
                        if (!line.Style.DashArray.IsDefaultOrEmpty)
                        {
                            prepared.LineResult.DashedLineCount++;
                            prepared.LineResult.DashTriangleCount +=
                                triangleCount;
                        }
                        prepared.LineResult.AddAdvancedStyle(line.Style);
                    }
                }
            }

            prepared.Complete();
            if (tracePreparation)
                prepared.PreparationMilliseconds =
                    Stopwatch.GetElapsedTime(preparationStart).TotalMilliseconds;
            cancellationToken.ThrowIfCancellationRequested();
            long uploadStart = tracePreparation ? Stopwatch.GetTimestamp() : 0;
            prepared.PromoteToGpu(
                input.DevicePointer,
                cancellationToken);
            if (tracePreparation)
                prepared.UploadMilliseconds =
                    Stopwatch.GetElapsedTime(uploadStart).TotalMilliseconds;
            return prepared;
        }
        catch
        {
            prepared?.Dispose();
            throw;
        }
        finally
        {
            Marshal.Release(input.DevicePointer);
        }
    }

    private static void AppendVectorBackgrounds(
        VectorBackgroundResolution resolution,
        double layerOpacity,
        double viewportWidth,
        double viewportHeight,
        double viewportPadding,
        PreparedVectorGeometryFrame prepared)
    {
        foreach (VectorResolvedBackground background in
            resolution.Backgrounds)
        {
            Vector4 color =
                background.Style.Color * (float)layerOpacity;
            if (color.W <= 0)
            {
                continue;
            }

            VectorPolygonBatchKey key = new(
                background.StyleLayerOrder,
                VectorPolygonBatchKind.Fill,
                color);
            PooledGeometryBuffer buffer =
                prepared.GetPolygonBuffer(key);
            double left = -viewportPadding;
            double top = -viewportPadding;
            double right = viewportWidth + viewportPadding;
            double bottom = viewportHeight + viewportPadding;
            buffer.Add(new MapScreenPoint(left, top));
            buffer.Add(new MapScreenPoint(right, top));
            buffer.Add(new MapScreenPoint(right, bottom));
            buffer.Add(new MapScreenPoint(left, top));
            buffer.Add(new MapScreenPoint(right, bottom));
            buffer.Add(new MapScreenPoint(left, bottom));
            prepared.PolygonResult.DrawablePolygonCount++;
            prepared.PolygonResult.TriangleCount += 2;
        }
    }

    private sealed record VectorGeometryPreparationInput(
        VectorGeometryPreparationKey Key,
        LayerRenderSnapshot Layer,
        IntPtr DevicePointer,
        double Longitude,
        double Latitude,
        double Zoom,
        double Heading,
        double Pitch,
        double ViewportWidth,
        double ViewportHeight,
        VectorBackgroundResolution Backgrounds,
        VectorLinePreparationTile[] LineTiles,
        VectorPolygonPreparationTile[] PolygonTiles,
        HashSet<VectorTileInstanceKey> IncludedTiles);

    private readonly record struct VectorLinePreparationTile(
        VisibleTile Tile,
        VectorLineResolution Resolution);

    private readonly record struct VectorPolygonPreparationTile(
        VisibleTile Tile,
        VectorPolygonResolution Resolution);

    private readonly record struct VectorGeometryPreparationKey(
        long RuntimeId,
        int Style,
        double Opacity,
        long Generation,
        long SceneVersion,
        long VectorTileVersion,
        int DeviceEpoch,
        double Zoom,
        double Heading,
        double Pitch,
        double ViewportWidth,
        double ViewportHeight)
    {
        internal bool Matches(
            LayerRenderSnapshot layer,
            RasterLayerState state,
            long vectorTileVersion,
            int deviceEpoch,
            double zoom,
            double heading,
            double pitch,
            double viewportWidth,
            double viewportHeight) =>
            RuntimeId == layer.RuntimeId &&
            Style == layer.Style &&
            Opacity == layer.Opacity &&
            Generation == state.Generation &&
            SceneVersion == state.SceneVersion &&
            VectorTileVersion == vectorTileVersion &&
            DeviceEpoch == deviceEpoch &&
            Zoom == zoom &&
            Heading == heading &&
            Pitch == pitch &&
            ViewportWidth == viewportWidth &&
            ViewportHeight == viewportHeight;
    }

    private sealed record VectorGeometryPreparationJob(
        VectorGeometryPreparationKey Key,
        CancellationTokenSource Cancellation);

    private sealed record CompletedVectorGeometryPreparation(
        VectorGeometryPreparationJob Job,
        Task<PreparedVectorGeometryFrame> Task);

    private sealed class PreparedVectorGeometryFrame : IDisposable
    {
        internal PreparedVectorGeometryFrame(
            VectorGeometryPreparationInput input)
        {
            Key = input.Key;
            Longitude = input.Longitude;
            Latitude = input.Latitude;
            Zoom = input.Zoom;
            Heading = input.Heading;
            Pitch = input.Pitch;
            ViewportWidth = input.ViewportWidth;
            ViewportHeight = input.ViewportHeight;
            IncludedTiles = input.IncludedTiles;
        }

        internal VectorGeometryPreparationKey Key { get; }

        internal double Longitude { get; }

        internal double Latitude { get; }

        internal double Zoom { get; }

        internal double Heading { get; }

        internal double Pitch { get; }

        internal double ViewportWidth { get; }

        internal double ViewportHeight { get; }

        internal HashSet<VectorTileInstanceKey> IncludedTiles { get; }

        internal Dictionary<VectorLineBatchKey, PooledGeometryBuffer>
            LineBatches { get; } = [];

        internal List<VectorLineBatchKey> LineBatchOrder { get; } = [];

        internal Dictionary<VectorPolygonBatchKey, PooledGeometryBuffer>
            PolygonBatches { get; } = [];

        internal List<VectorPolygonBatchKey> PolygonBatchOrder { get; } = [];

        internal VectorLineRenderResult LineResult;

        internal VectorPolygonRenderResult PolygonResult;

        internal int LineVertexCount { get; private set; }

        internal int PolygonVertexCount { get; private set; }

        internal double PreparationMilliseconds { get; set; }

        internal double UploadMilliseconds { get; set; }

        internal bool TraceTiming { get; init; }

        internal void ReportCompletion(bool accepted)
        {
            if (TraceTiming)
                MapControlEventSource.Log.VectorGeometryPreparationSummary(
                    Key.Style,
                    accepted ? 1 : 0,
                    LineVertexCount,
                    PolygonVertexCount,
                    PreparationMilliseconds,
                    UploadMilliseconds);
        }

        private VectorLineCachedBatch[]? CachedLineBatches { get; set; }

        private VectorPolygonCachedBatch[]? CachedPolygonBatches { get; set; }

        internal PooledGeometryBuffer GetLineBuffer(VectorLineBatchKey key)
        {
            if (!LineBatches.TryGetValue(
                    key,
                    out PooledGeometryBuffer? buffer))
            {
                buffer = new PooledGeometryBuffer();
                LineBatches.Add(key, buffer);
                LineBatchOrder.Add(key);
            }
            return buffer;
        }

        internal PooledGeometryBuffer GetPolygonBuffer(
            VectorPolygonBatchKey key)
        {
            if (!PolygonBatches.TryGetValue(
                    key,
                    out PooledGeometryBuffer? buffer))
            {
                buffer = new PooledGeometryBuffer();
                PolygonBatches.Add(key, buffer);
                PolygonBatchOrder.Add(key);
            }
            return buffer;
        }

        internal void Complete()
        {
            LineBatchOrder.Sort(CompareVectorLineBatches);
            PolygonBatchOrder.Sort(CompareVectorPolygonBatches);
            LineVertexCount =
                LineBatches.Values.Sum(buffer => buffer.Count);
            PolygonVertexCount =
                PolygonBatches.Values.Sum(buffer => buffer.Count);
            LineResult.DrawCallCount =
                LineBatches.Values.Sum(buffer => buffer.Chunks.Count);
            PolygonResult.DrawCallCount =
                PolygonBatches.Values.Sum(buffer => buffer.Chunks.Count);
        }

        internal void PromoteToGpu(
            IntPtr devicePointer,
            CancellationToken cancellationToken)
        {
            VectorLineCachedBatch[]? lineBatches = null;
            VectorPolygonCachedBatch[]? polygonBatches = null;
            try
            {
                lineBatches = CreateVectorLineCachedBatches(
                    devicePointer,
                    LineBatchOrder,
                    LineBatches,
                    cancellationToken);
                polygonBatches = CreateVectorPolygonCachedBatches(
                    devicePointer,
                    PolygonBatchOrder,
                    PolygonBatches,
                    cancellationToken);
                CachedLineBatches = lineBatches;
                CachedPolygonBatches = polygonBatches;
                lineBatches = null;
                polygonBatches = null;
            }
            finally
            {
                if (lineBatches is not null)
                {
                    foreach (VectorLineCachedBatch batch in lineBatches)
                    {
                        batch.Buffer.Dispose();
                    }
                }
                if (polygonBatches is not null)
                {
                    foreach (VectorPolygonCachedBatch batch in polygonBatches)
                    {
                        batch.Buffer.Dispose();
                    }
                }
                DisposeCpuBuffers();
            }
        }

        internal VectorLineCachedBatch[] TakeLineBatches()
        {
            VectorLineCachedBatch[] result =
                CachedLineBatches ??
                throw new InvalidOperationException(
                    "Prepared line geometry has not been uploaded.");
            CachedLineBatches = null;
            return result;
        }

        internal VectorPolygonCachedBatch[] TakePolygonBatches()
        {
            VectorPolygonCachedBatch[] result =
                CachedPolygonBatches ??
                throw new InvalidOperationException(
                    "Prepared polygon geometry has not been uploaded.");
            CachedPolygonBatches = null;
            return result;
        }

        public void Dispose()
        {
            DisposeCpuBuffers();
            if (CachedLineBatches is not null)
            {
                foreach (VectorLineCachedBatch batch in CachedLineBatches)
                {
                    batch.Buffer.Dispose();
                }
                CachedLineBatches = null;
            }
            if (CachedPolygonBatches is not null)
            {
                foreach (VectorPolygonCachedBatch batch in CachedPolygonBatches)
                {
                    batch.Buffer.Dispose();
                }
                CachedPolygonBatches = null;
            }
        }

        private void DisposeCpuBuffers()
        {
            foreach (PooledGeometryBuffer buffer in LineBatches.Values)
            {
                buffer.Dispose();
            }
            foreach (PooledGeometryBuffer buffer in PolygonBatches.Values)
            {
                buffer.Dispose();
            }
            LineBatches.Clear();
            PolygonBatches.Clear();
        }
    }
}
