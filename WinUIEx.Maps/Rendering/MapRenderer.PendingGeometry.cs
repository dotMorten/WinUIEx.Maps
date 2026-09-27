using System.Runtime.CompilerServices;

namespace WinUIEx.Maps.Rendering;

internal sealed partial class MapRenderer
{
    // Pending coverage borrows at most the byte capacity of the frame it supplements.
    // It has no navigation history, independent budget, or worker ownership.
    private sealed class PendingGeometryCache<TKey, TResult> : IDisposable where TKey : notnull
    {
        internal readonly Dictionary<VectorTileInstanceKey, PendingGeometryTile<TKey, TResult>> Tiles = [];
        internal long ByteSize { get; private set; }

        internal void Remove(VectorTileInstanceKey key)
        {
            if (Tiles.Remove(key, out var tile))
            {
                ByteSize -= tile.ByteSize;
                tile.Dispose();
            }
        }

        internal void Trim(IReadOnlySet<VectorTileInstanceKey> visible)
        {
            foreach (var key in Tiles.Keys.ToArray())
                if (!visible.Contains(key))
                    Remove(key);
        }

        internal void Admit(VectorTileInstanceKey key, PendingGeometryTile<TKey, TResult> tile,
            long maximumBytes, IntPtr device)
        {
            if (tile.ByteSize == 0 || tile.ByteSize > maximumBytes - ByteSize)
                return;
            tile.Upload(device);
            Tiles.Add(key, tile);
            ByteSize += tile.ByteSize;
        }

        public void Dispose()
        {
            foreach (var tile in Tiles.Values)
                tile.Dispose();
            Tiles.Clear();
            ByteSize = 0;
        }
    }

    private sealed class PendingGeometryTile<TKey, TResult>(
        VectorTileCacheEntry content, double longitude, double latitude,
        Dictionary<TKey, NativeGeometryBuffer> buffers, List<TKey> order, TResult result) : IDisposable
        where TKey : notnull
    {
        internal VectorTileCacheEntry Content { get; } = content;
        internal double Longitude { get; } = longitude;
        internal double Latitude { get; } = latitude;
        internal List<TKey> Order { get; } = order;
        internal TResult Result { get; } = result;
        internal Dictionary<TKey, PendingGeometryBuffer> Buffers { get; } =
            buffers.ToDictionary(pair => pair.Key, pair => new PendingGeometryBuffer(pair.Value));
        internal long ByteSize { get; } = buffers.Values.Sum(buffer => (long)buffer.Count * Unsafe.SizeOf<GeometryVertex>());
        internal bool Retained { get; private set; }

        internal void Upload(IntPtr device)
        {
            foreach (var buffer in Buffers.Values)
                buffer.Upload(device);
            Retained = true;
        }

        public void Dispose()
        {
            foreach (var buffer in Buffers.Values)
                buffer.Dispose();
        }
    }

    private sealed class PendingGeometryBuffer(NativeGeometryBuffer native) : IDisposable
    {
        internal NativeGeometryBuffer? Native { get; private set; } = native;
        internal GpuGeometryBuffer? Gpu { get; private set; }
        internal void Upload(IntPtr device)
        {
            Gpu = CreateGpuGeometryBuffer(device, Native!);
            Native!.Dispose();
            Native = null;
        }
        public void Dispose()
        {
            Native?.Dispose();
            Native = null;
            Gpu?.Dispose();
            Gpu = null;
        }
    }

    private readonly record struct PendingGeometryDraw(
        PendingGeometryBuffer Buffer, MapViewportProjectiveTransform Transform);

    private bool TryGetPendingGeometry<TKey, TResult>(
        PendingGeometryCache<TKey, TResult> cache, VectorTileInstanceKey key,
        VectorTileCacheEntry content, out PendingGeometryTile<TKey, TResult>? tile,
        out MapViewportProjectiveTransform transform) where TKey : notnull
    {
        transform = default;
        if (cache.Tiles.TryGetValue(key, out tile))
        {
            if (ReferenceEquals(tile.Content, content) &&
                TryGetVectorPanTransform(tile.Longitude, tile.Latitude,
                    _displayLongitude, _displayLatitude, _displayZoom, _displayHeading,
                    _displayPitch, _viewportWidth, _viewportHeight,
                    out transform, out double x, out double y) &&
                Math.Abs(x) <= VectorGeometryCachePanLimit &&
                Math.Abs(y) <= VectorGeometryCachePanLimit)
                return true;
            cache.Remove(key);
        }
        tile = null;
        return false;
    }

    private void PrunePendingGeometry<TKey, TResult>(PendingGeometryCache<TKey, TResult>? cache)
        where TKey : notnull
    {
        if (cache is null)
            return;
        foreach (var pair in cache.Tiles.ToArray())
            if (!_vectorTiles.TryGetValue(pair.Key.Tile, out var tile) ||
                !ReferenceEquals(tile, pair.Value.Content))
                cache.Remove(pair.Key);
    }

    private int DrawPendingGeometry(IntPtr context, PendingGeometryDraw draw, System.Numerics.Vector4 color)
    {
        if (draw.Buffer.Gpu is { } gpu)
        {
            DrawGpuGeometryBuffer(context, gpu, color, premultiplied: true, draw.Transform);
            return gpu.Chunks.Count;
        }
        DrawGeometryBuffer(context, draw.Buffer.Native!, color, premultiplied: true);
        return draw.Buffer.Native!.Chunks.Count;
    }
}
