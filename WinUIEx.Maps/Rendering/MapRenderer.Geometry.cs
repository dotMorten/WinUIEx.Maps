using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using WinUIEx.Maps.Rendering.Diagnostics;
using Windows.Win32.Graphics.Direct3D11;
using static WinUIEx.Maps.Rendering.DirectXInterop;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Implements ordered polygon/polyline rendering and merges vector draws with icon draws at
/// their exact map-element collection positions.
/// </summary>
internal sealed partial class MapRenderer
{
    private const int GeometryVertexCapacity = 65_535;
    private const double VectorGeometryCachePadding = 384;
    private const double VectorGeometryCachePanLimit = 320;

    private unsafe void DrawMapElements(
        IntPtr context,
        int layerIndex,
        double layerOpacity)
    {
        GetVisibleMapElements(
            out MapIconSnapshot[] icons,
            out MapGeometrySnapshot[] geometries);
        int iconIndex = FindFirstLayerIcon(icons, layerIndex);
        int geometryIndex = FindFirstLayerGeometry(geometries, layerIndex);
        IconDrawResult iconResult = default;
        MapGeometryCamera camera = new(
            _displayLongitude,
            _displayLatitude,
            _displayZoom,
            _displayHeading,
            _viewportWidth,
            _viewportHeight,
            _displayPitch);

        while (IsLayerIcon(icons, iconIndex, layerIndex) ||
            IsLayerGeometry(geometries, geometryIndex, layerIndex))
        {
            int iconOrder = IsLayerIcon(icons, iconIndex, layerIndex)
                ? icons[iconIndex].OrderIndex
                : int.MaxValue;
            int geometryOrder = IsLayerGeometry(geometries, geometryIndex, layerIndex)
                ? geometries[geometryIndex].OrderIndex
                : int.MaxValue;
            if (iconOrder < geometryOrder)
            {
                long textureId = icons[iconIndex].TextureId;
                int runStart = iconIndex;
                do
                {
                    iconIndex++;
                }
                while (IsLayerIcon(icons, iconIndex, layerIndex) &&
                    icons[iconIndex].TextureId == textureId &&
                    icons[iconIndex].OrderIndex < geometryOrder);
                iconResult += DrawMapIconRun(
                    context,
                    icons,
                    runStart,
                    iconIndex - runStart,
                    layerOpacity);
            }
            else
            {
                DrawMapGeometry(
                    context,
                    geometries[geometryIndex],
                    camera,
                    layerOpacity);
                geometryIndex++;
            }
        }

        MapControlEventSource.Log.IconRenderBatch(
            iconResult.CandidateCount,
            iconResult.DrawableCount,
            iconResult.TextureBatchCount,
            iconResult.DrawCallCount);
    }

    private unsafe void DrawMapGeometry(
        IntPtr context,
        MapGeometrySnapshot snapshot,
        MapGeometryCamera camera,
        double layerOpacity)
    {
        if (snapshot.IsPolygon && snapshot.FillColor.A != 0)
        {
            DrawGeometryTriangles(
                context,
                MapGeometryOperations.BuildFillTriangles(snapshot.Geometry, camera),
                snapshot.FillColor.ToVector(layerOpacity));
        }
        if (snapshot.StrokeColor.A == 0 || snapshot.StrokeThickness <= 0)
        {
            return;
        }

        MapScreenSegment[] segments = MapGeometryOperations.BuildStrokeSegments(
            snapshot.Geometry,
            snapshot.IsPolygon,
            snapshot.StrokeThickness,
            snapshot.StrokeDashed,
            camera);
        DrawGeometryTriangles(
            context,
            MapGeometryOperations.ExpandStrokeTriangles(
                segments,
                snapshot.StrokeThickness),
            snapshot.StrokeColor.ToVector(layerOpacity));
    }

    private unsafe void DrawGeometryTriangles(
        IntPtr context,
        ReadOnlySpan<MapScreenPoint> points,
        Vector4 color,
        bool premultiplied = false,
        double offsetX = 0,
        double offsetY = 0)
    {
        if (points.Length < 3 || color.W <= 0)
        {
            return;
        }

        PrepareGeometryDraw(
            context,
            color,
            premultiplied,
            offsetX,
            offsetY);
        SetVertexBuffer(
            context,
            _geometryVertexBufferPointer,
            (uint)Marshal.SizeOf<GeometryVertex>());

        GeometryVertex[] vertices = ArrayPool<GeometryVertex>.Shared.Rent(
            Math.Min(points.Length, GeometryVertexCapacity));
        try
        {
            StreamGeometryTriangles(context, points, vertices);
        }
        finally
        {
            ArrayPool<GeometryVertex>.Shared.Return(vertices);
        }
    }

    private unsafe void PrepareGeometryDraw(
        IntPtr context,
        Vector4 color,
        bool premultiplied,
        double offsetX,
        double offsetY)
    {
        GeometryConstants constants = new(
            new Vector4(
                (float)(2 / Viewport.Width),
                (float)(-2 / Viewport.Height),
                (float)(-1 + ((offsetX * 2) / Viewport.Width)),
                (float)(1 - ((offsetY * 2) / Viewport.Height))),
            color,
            Vector4.Zero,
            Vector4.Zero);
        UpdateSubresource(context, _constantBufferPointer, &constants);
        SetBlendState(
            context,
            premultiplied
                ? _premultipliedBlendStatePointer
                : _blendStatePointer);
        SetInputLayout(context, _geometryInputLayoutPointer);
        SetVertexShader(context, _geometryVertexShaderPointer, _constantBufferPointer);
        SetPixelShader(context, _geometryPixelShaderPointer, _constantBufferPointer);
    }

    private unsafe void StreamGeometryTriangles(
        IntPtr context,
        ReadOnlySpan<MapScreenPoint> points,
        GeometryVertex[] vertices)
    {
        int startIndex = 0;
        while (startIndex < points.Length)
        {
            int count = Math.Min(
                GeometryVertexCapacity,
                points.Length - startIndex);
            count -= count % 3;
            if (count == 0)
            {
                break;
            }
            for (int index = 0; index < count; index++)
            {
                MapScreenPoint point = points[startIndex + index];
                vertices[index] = new GeometryVertex(
                    new Vector2((float)point.X, (float)point.Y));
            }

            fixed (GeometryVertex* vertexPointer = vertices)
            {
                WriteDiscardBuffer(
                    context,
                    _geometryVertexBufferPointer,
                    vertexPointer,
                    (nuint)(count * Marshal.SizeOf<GeometryVertex>()));
            }
            DrawVertices(context, (uint)count);
            startIndex += count;
        }
    }

    private unsafe void DrawGeometryBuffer(
        IntPtr context,
        PooledGeometryBuffer buffer,
        Vector4 color,
        bool premultiplied = false,
        double offsetX = 0,
        double offsetY = 0)
    {
        if (buffer.Count < 3 || color.W <= 0)
        {
            return;
        }

        PrepareGeometryDraw(
            context,
            color,
            premultiplied,
            offsetX,
            offsetY);
        SetVertexBuffer(
            context,
            _geometryVertexBufferPointer,
            (uint)Marshal.SizeOf<GeometryVertex>());
        GeometryVertex[] vertices = ArrayPool<GeometryVertex>.Shared.Rent(
            GeometryVertexCapacity);
        try
        {
            foreach (GeometryBufferChunk chunk in buffer.Chunks)
            {
                StreamGeometryTriangles(
                    context,
                    chunk.Buffer.AsSpan(0, chunk.Count),
                    vertices);
            }
        }
        finally
        {
            ArrayPool<GeometryVertex>.Shared.Return(vertices);
        }
    }

    private unsafe void DrawGpuGeometryBuffer(
        IntPtr context,
        GpuGeometryBuffer buffer,
        Vector4 color,
        bool premultiplied = false,
        double offsetX = 0,
        double offsetY = 0)
    {
        if (buffer.VertexCount < 3 || color.W <= 0)
        {
            return;
        }

        PrepareGeometryDraw(
            context,
            color,
            premultiplied,
            offsetX,
            offsetY);
        foreach (GpuGeometryChunk chunk in buffer.Chunks)
        {
            SetVertexBuffer(
                context,
                chunk.BufferPointer,
                (uint)Marshal.SizeOf<GeometryVertex>());
            DrawVertices(context, (uint)chunk.VertexCount);
        }
    }

    private static unsafe GpuGeometryBuffer CreateGpuGeometryBuffer(
        IntPtr devicePointer,
        PooledGeometryBuffer source,
        CancellationToken cancellationToken = default)
    {
        List<GpuGeometryChunk> chunks = [];
        GeometryVertex[] vertices = ArrayPool<GeometryVertex>.Shared.Rent(
            GeometryVertexCapacity);
        try
        {
            foreach (GeometryBufferChunk sourceChunk in source.Chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int index = 0; index < sourceChunk.Count; index++)
                {
                    MapScreenPoint point = sourceChunk.Buffer[index];
                    vertices[index] = new GeometryVertex(
                        new Vector2((float)point.X, (float)point.Y));
                }

                D3D11_BUFFER_DESC description = new()
                {
                    ByteWidth = checked((uint)(
                        sourceChunk.Count * Marshal.SizeOf<GeometryVertex>())),
                    Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE,
                    BindFlags = D3D11_BIND_FLAG.D3D11_BIND_VERTEX_BUFFER,
                };
                fixed (GeometryVertex* vertexPointer = vertices)
                {
                    D3D11_SUBRESOURCE_DATA data = new()
                    {
                        pSysMem = vertexPointer,
                    };
                    IntPtr bufferPointer = CreateBuffer(
                        devicePointer,
                        &description,
                        &data,
                        "Failed to create a cached vector geometry buffer.");
                    chunks.Add(new GpuGeometryChunk(
                        bufferPointer,
                        sourceChunk.Count));
                }
            }
            return new GpuGeometryBuffer(chunks);
        }
        catch
        {
            foreach (GpuGeometryChunk chunk in chunks)
            {
                chunk.Dispose();
            }
            throw;
        }
        finally
        {
            ArrayPool<GeometryVertex>.Shared.Return(vertices);
        }
    }

    private static bool TryGetVectorPanOffset(
        double cachedLongitude,
        double cachedLatitude,
        double currentLongitude,
        double currentLatitude,
        double zoom,
        double heading,
        double viewportWidth,
        double viewportHeight,
        out double offsetX,
        out double offsetY)
    {
        if (!MapCamera.TryProjectLocation(
                cachedLongitude,
                cachedLatitude,
                currentLongitude,
                currentLatitude,
                zoom,
                viewportWidth,
                viewportHeight,
                heading,
                0,
                out MapViewportPoint point))
        {
            offsetX = 0;
            offsetY = 0;
            return false;
        }
        offsetX = point.X - (viewportWidth / 2);
        offsetY = point.Y - (viewportHeight / 2);
        return true;
    }

    private void OnVectorTilesChanged(bool disposeGeometryCaches = false)
    {
        _vectorTileVersion++;
        if (disposeGeometryCaches)
        {
            DisposeVectorGeometryCaches();
        }
    }

    private void DisposeVectorGeometryCaches()
    {
        CancelVectorGeometryPreparation();
        _vectorLineFrameCache?.Dispose();
        _vectorLineFrameCache = null;
        _vectorPolygonFrameCache?.Dispose();
        _vectorPolygonFrameCache = null;
    }

    private static int FindFirstLayerIcon(MapIconSnapshot[] icons, int layerIndex)
    {
        int index = 0;
        while (index < icons.Length && icons[index].LayerIndex < layerIndex)
        {
            index++;
        }
        return index;
    }

    private static int FindFirstLayerGeometry(
        MapGeometrySnapshot[] geometries,
        int layerIndex)
    {
        int index = 0;
        while (index < geometries.Length && geometries[index].LayerIndex < layerIndex)
        {
            index++;
        }
        return index;
    }

    private static bool IsLayerIcon(
        MapIconSnapshot[] icons,
        int index,
        int layerIndex) =>
        (uint)index < (uint)icons.Length && icons[index].LayerIndex == layerIndex;

    private static bool IsLayerGeometry(
        MapGeometrySnapshot[] geometries,
        int index,
        int layerIndex) =>
        (uint)index < (uint)geometries.Length &&
        geometries[index].LayerIndex == layerIndex;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GeometryVertex(Vector2 Position);

    private sealed class PooledGeometryBuffer : IDisposable
    {
        private const int InitialVertexCapacity = 1023;
        private readonly List<GeometryBufferChunk> _chunks = [];

        internal IReadOnlyList<GeometryBufferChunk> Chunks => _chunks;

        internal int Count { get; private set; }

        internal void Add(MapScreenPoint point)
        {
            GeometryBufferChunk chunk = EnsureWritableChunk(1);
            chunk.Buffer[chunk.Count++] = point;
            Count++;
        }

        internal MapScreenPoint[] ToArray()
        {
            MapScreenPoint[] result = new MapScreenPoint[Count];
            int offset = 0;
            foreach (GeometryBufferChunk chunk in _chunks)
            {
                chunk.Buffer.AsSpan(0, chunk.Count).CopyTo(
                    result.AsSpan(offset, chunk.Count));
                offset += chunk.Count;
            }
            return result;
        }

        public void Dispose()
        {
            foreach (GeometryBufferChunk chunk in _chunks)
            {
                ArrayPool<MapScreenPoint>.Shared.Return(chunk.Buffer);
            }
            _chunks.Clear();
            Count = 0;
        }

        private GeometryBufferChunk EnsureWritableChunk(int requestedCount)
        {
            if (_chunks.Count == 0)
            {
                GeometryBufferChunk initial = RentChunk(
                    Math.Min(
                        GeometryVertexCapacity,
                        Math.Max(InitialVertexCapacity, requestedCount)));
                _chunks.Add(initial);
                return initial;
            }

            GeometryBufferChunk current = _chunks[^1];
            if (current.Capacity - current.Count >= requestedCount)
            {
                return current;
            }
            if (current.Capacity < GeometryVertexCapacity &&
                current.Count + requestedCount <= GeometryVertexCapacity)
            {
                int capacity = current.Capacity;
                while (capacity < current.Count + requestedCount)
                {
                    capacity = Math.Min(
                        GeometryVertexCapacity,
                        capacity * 2);
                }
                GeometryBufferChunk grown = RentChunk(capacity);
                current.Buffer.AsSpan(0, current.Count).CopyTo(grown.Buffer);
                grown.Count = current.Count;
                ArrayPool<MapScreenPoint>.Shared.Return(current.Buffer);
                _chunks[^1] = grown;
                return grown;
            }
            if (current.Count < current.Capacity)
            {
                return current;
            }

            GeometryBufferChunk next = RentChunk(
                Math.Min(
                    GeometryVertexCapacity,
                    Math.Max(InitialVertexCapacity, requestedCount)));
            _chunks.Add(next);
            return next;
        }

        private static GeometryBufferChunk RentChunk(int minimumCapacity)
        {
            MapScreenPoint[] buffer =
                ArrayPool<MapScreenPoint>.Shared.Rent(minimumCapacity);
            int capacity = Math.Min(
                GeometryVertexCapacity,
                buffer.Length - (buffer.Length % 3));
            return new GeometryBufferChunk(buffer, capacity);
        }
    }

    private sealed class GeometryBufferChunk(
        MapScreenPoint[] buffer,
        int capacity)
    {
        internal MapScreenPoint[] Buffer { get; } = buffer;

        internal int Capacity { get; } = capacity;

        internal int Count { get; set; }
    }

    private sealed class GpuGeometryBuffer(
        List<GpuGeometryChunk> chunks) : IDisposable
    {
        internal IReadOnlyList<GpuGeometryChunk> Chunks { get; } = chunks;

        internal int VertexCount { get; } =
            chunks.Sum(chunk => chunk.VertexCount);

        internal long ByteSize { get; } =
            chunks.Sum(chunk => chunk.ByteSize);

        public void Dispose()
        {
            foreach (GpuGeometryChunk chunk in Chunks)
            {
                chunk.Dispose();
            }
        }
    }

    private sealed class GpuGeometryChunk(
        IntPtr bufferPointer,
        int vertexCount) : IDisposable
    {
        internal IntPtr BufferPointer { get; private set; } = bufferPointer;

        internal int VertexCount { get; } = vertexCount;

        internal long ByteSize { get; } =
            (long)vertexCount * Marshal.SizeOf<GeometryVertex>();

        public void Dispose()
        {
            IntPtr pointer = BufferPointer;
            ReleasePointer(ref pointer);
            BufferPointer = IntPtr.Zero;
        }
    }

    private readonly record struct VectorTileInstanceKey(
        RasterTileKey Tile,
        int WorldX);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GeometryConstants(
        Vector4 Transform,
        Vector4 Color,
        Vector4 Padding,
        Vector4 Padding2);
}
