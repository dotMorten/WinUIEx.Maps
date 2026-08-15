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

    private bool DeferVectorGeometryRebuild(
        LayerRenderSnapshot layer,
        RasterLayerState state,
        ulong fallbackMask)
    {
        if (fallbackMask != 0 ||
            _displayPitch != 0 ||
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
            _viewportWidth,
            _viewportHeight);
        lock (_vectorGeometryPreparationSync)
        {
            if (_vectorGeometryPreparationJob?.Key == key)
            {
                return true;
            }
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
        VectorGeometryPreparationJob? previous;
        lock (_vectorGeometryPreparationSync)
        {
            previous = _vectorGeometryPreparationJob;
            _vectorGeometryPreparationJob = job;
        }
        previous?.Cancellation.Cancel();
        _ = Task.Run(
                () => BuildVectorGeometryFrame(input, cancellation.Token))
            .ContinueWith(
                task =>
                {
                    bool stale;
                    lock (_vectorGeometryPreparationSync)
                    {
                        stale = Volatile.Read(ref _uploadDisposed) ||
                            !ReferenceEquals(
                                _vectorGeometryPreparationJob,
                                job);
                        if (!stale)
                        {
                            _completedVectorGeometryPreparations.Enqueue(
                                new CompletedVectorGeometryPreparation(
                                    job,
                                    task));
                        }
                    }
                    if (stale)
                    {
                        cancellation.Dispose();
                        if (task.Status == TaskStatus.RanToCompletion)
                        {
                            task.Result.Dispose();
                        }
                        else
                        {
                            _ = task.Exception;
                        }
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
            _viewportWidth,
            _viewportHeight,
            lineTiles.ToArray(),
            polygonTiles.ToArray(),
            includedTiles);
        return true;
    }

    private void ApplyCompletedVectorGeometryPreparation(
        LayerRenderSnapshot layer,
        RasterLayerState state)
    {
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
                    _viewportWidth,
                    _viewportHeight))
            {
                MapControlEventSource.Log.VectorGeometryPreparationSummary(
                    prepared.Key.Style,
                    0,
                    prepared.LineVertexCount,
                    prepared.PolygonVertexCount,
                    prepared.PreparationMilliseconds,
                    prepared.UploadMilliseconds);
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
                MapControlEventSource.Log.VectorGeometryPreparationSummary(
                    prepared.Key.Style,
                    1,
                    prepared.LineVertexCount,
                    prepared.PolygonVertexCount,
                    prepared.PreparationMilliseconds,
                    prepared.UploadMilliseconds);
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
        VectorGeometryPreparationJob? active;
        List<CompletedVectorGeometryPreparation> completedJobs = [];
        lock (_vectorGeometryPreparationSync)
        {
            active = _vectorGeometryPreparationJob;
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
        active?.Cancellation.Cancel();
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
        CancellationToken cancellationToken)
    {
        long preparationStart = Stopwatch.GetTimestamp();
        PreparedVectorGeometryFrame? prepared = null;
        try
        {
            prepared = new(input);
            foreach (VectorPolygonPreparationTile tile in input.PolygonTiles)
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
                            0,
                            VectorGeometryCachePadding,
                            buffer);
                    if (triangleCount != 0)
                    {
                        prepared.PolygonResult.DrawablePolygonCount++;
                        prepared.PolygonResult.TriangleCount += triangleCount;
                    }
                    if (polygon.Style.OutlineColor is Vector4 outlineColor &&
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
                                ProjectVectorLine(
                                    ring.Points,
                                    tile.Tile,
                                    input.ViewportWidth,
                                    input.ViewportHeight,
                                    input.Heading,
                                    0,
                                    projected.AsSpan(0, ring.Points.Length));
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
                    VectorLineBatchKey key = new(
                        line.StyleLayerOrder,
                        style.Color * (float)input.Layer.Opacity);
                    PooledGeometryBuffer buffer = prepared.GetLineBuffer(key);
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
                            0,
                            projected.AsSpan(0, line.Points.Length));
                        triangleCount = AppendVectorLineTriangles(
                            projected.AsSpan(0, line.Points.Length),
                            style,
                            input.ViewportWidth,
                            input.ViewportHeight,
                            VectorGeometryCachePadding,
                            buffer);
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
                    }
                }
            }

            prepared.Complete();
            prepared.PreparationMilliseconds =
                Stopwatch.GetElapsedTime(
                    preparationStart).TotalMilliseconds;
            cancellationToken.ThrowIfCancellationRequested();
            long uploadStart = Stopwatch.GetTimestamp();
            prepared.PromoteToGpu(
                input.DevicePointer,
                cancellationToken);
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

    private sealed record VectorGeometryPreparationInput(
        VectorGeometryPreparationKey Key,
        LayerRenderSnapshot Layer,
        IntPtr DevicePointer,
        double Longitude,
        double Latitude,
        double Zoom,
        double Heading,
        double ViewportWidth,
        double ViewportHeight,
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
            ViewportWidth = input.ViewportWidth;
            ViewportHeight = input.ViewportHeight;
            IncludedTiles = input.IncludedTiles;
        }

        internal VectorGeometryPreparationKey Key { get; }

        internal double Longitude { get; }

        internal double Latitude { get; }

        internal double Zoom { get; }

        internal double Heading { get; }

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
            LineBatchOrder.Sort(static (left, right) =>
                left.StyleLayerOrder.CompareTo(right.StyleLayerOrder));
            PolygonBatchOrder.Sort(static (left, right) =>
                left.StyleLayerOrder.CompareTo(right.StyleLayerOrder));
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
