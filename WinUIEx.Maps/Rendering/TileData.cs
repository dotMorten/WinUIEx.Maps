namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Identifies one XYZ cell in the Web Mercator tile pyramid.
/// </summary>
internal readonly record struct TileId(int Zoom, int X, int Y);

/// <summary>
/// Carries decoded BGRA pixels and dimensions returned by an immutable acquisition session.
/// </summary>
/// <remarks>The buffer remains CPU-owned until the renderer accepts it for upload.</remarks>
internal readonly record struct DecodedRasterTile(
    TileId Id,
    byte[] Pixels,
    uint Width,
    uint Height,
    double DownloadMilliseconds,
    double DecodeMilliseconds);

/// <summary>
/// Combines renderer source identity with tile coordinates for pending and GPU-cache lookup.
/// </summary>
internal readonly record struct RasterTileKey(long SourceId, TileId Id);

/// <summary>
/// Packages decoded tile pixels with source generation and kind for renderer upload
/// validation and privacy-safe diagnostic routing.
/// </summary>
internal readonly record struct RasterTileData(
    RasterTileKey Key,
    byte[] Pixels,
    uint Width,
    uint Height,
    long Generation,
    RasterSourceKind SourceKind);

/// <summary>
/// Associates a decoded raster upload with the reservation that owns its deduplication and
/// backpressure slot.
/// </summary>
internal readonly record struct QueuedRasterTileUpload(
    RasterTileData Tile,
    long Reservation);

/// <summary>
/// Describes one viewport instance of a tile, including its wrapped world column and
/// pixel-space rectangle.
/// </summary>
internal readonly record struct VisibleTile(TileId Id, int WorldX, double Left, double Top, double Size);

/// <summary>
/// Reports required raster tiles partitioned into missing, cached, and already-pending
/// counts for scheduler batching and ETW aggregation.
/// </summary>
internal readonly record struct RasterTileLookupResult(
    IReadOnlyList<TileId> MissingTiles,
    int RequiredCount,
    int CacheHitCount,
    int PendingCount);

/// <summary>
/// Deduplicates in-flight raster tiles with monotonically increasing ownership reservations.
/// </summary>
/// <remarks>
/// The renderer accesses this tracker only under its render lock. A completion may release an
/// entry only with the reservation it received, so a stale completion cannot clear newer
/// work for the same source and tile after generation changes.
/// </remarks>
internal sealed class PendingTileTracker
{
    private readonly Dictionary<RasterTileKey, long> _reservations = [];
    private long _nextReservation;

    /// <summary>
    /// Determines whether work for the tile is already reserved by the current pipeline.
    /// </summary>
    public bool Contains(RasterTileKey key) => _reservations.ContainsKey(key);

    /// <summary>
    /// Reserves a tile for one acquisition/upload generation, returning zero when it is
    /// already pending.
    /// </summary>
    public long TryReserve(RasterTileKey key)
    {
        if (_reservations.ContainsKey(key))
        {
            return 0;
        }
        long reservation = ++_nextReservation;
        _reservations.Add(key, reservation);
        return reservation;
    }

    /// <summary>
    /// Releases a tile only when the supplied reservation still owns its pending entry,
    /// preventing stale completions from clearing newer work.
    /// </summary>
    public void Release(RasterTileKey key, long reservation)
    {
        if (_reservations.TryGetValue(key, out long currentReservation) &&
            currentReservation == reservation)
        {
            _reservations.Remove(key);
        }
    }

    /// <summary>
    /// Removes all pending reservations owned by a raster source that is being deactivated.
    /// </summary>
    public void RemoveSource(long sourceId)
    {
        foreach (RasterTileKey key in _reservations.Keys
            .Where(key => key.SourceId == sourceId)
            .ToArray())
        {
            _reservations.Remove(key);
        }
    }

    /// <summary>
    /// Clears every pending reservation during renderer teardown or pipeline reset.
    /// </summary>
    public void Clear() => _reservations.Clear();
}
