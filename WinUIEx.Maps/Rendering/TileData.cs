using System.Collections.Immutable;
using System.Numerics;

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
/// Carries decoded point and line features, immutable style state, and the bounded sprite
/// crops referenced by one vector tile.
/// </summary>
internal readonly record struct DecodedVectorTile(
    TileId Id,
    VectorTileFeatureCollection Features,
    VectorStyleAssets StyleAssets,
    VectorSpriteTextureData[] SpriteTextures,
    DecodedRasterTile? Background,
    double DownloadMilliseconds,
    double DecodeMilliseconds);

/// <summary>
/// Packages decoded vector features and style state with source generation for render-thread
/// cache commit.
/// </summary>
internal readonly record struct VectorTileData(
    RasterTileKey Key,
    VectorTileFeatureCollection Features,
    VectorStyleAssets StyleAssets,
    VectorSpriteTextureData[] SpriteTextures,
    RasterTileData? Background,
    long Generation,
    int Style);

internal enum MapAccessibilityFeatureKind
{
    Other,
    Place,
    AdministrativeArea,
    Road,
    Transit,
    Water,
    NaturalFeature,
    Landmark,
}

internal sealed record VectorTileAccessibilityFeature(
    string Name,
    MapAccessibilityFeatureKind Kind,
    double X,
    double Y,
    int StyleLayerOrder,
    double Prominence);

internal sealed record MapAccessibilityFeature(
    string Name,
    MapAccessibilityFeatureKind Kind,
    double Longitude,
    double Latitude,
    int StyleLayerOrder,
    double Prominence);

internal sealed record MapAccessibilitySnapshot(
    long SceneVersion,
    double Longitude,
    double Latitude,
    double Zoom,
    double Heading,
    double Pitch,
    MapAccessibilityFeature[] Features);

/// <summary>
/// Carries one lazily cropped premultiplied sprite buffer through the existing icon upload
/// and device-epoch pipeline.
/// </summary>
internal sealed record VectorSpriteTextureData(
    long TextureId,
    byte[] Pixels,
    uint Width,
    uint Height);

/// <summary>
/// Describes one style-resolved point symbol in tile-local coordinates and display pixels.
/// </summary>
internal readonly record struct VectorTileSymbol(
    int StyleLayerOrder,
    double X,
    double Y,
    long TextureId,
    double Width,
    double Height,
    double OffsetX,
    double OffsetY,
    VectorSymbolKind Kind = VectorSymbolKind.Icon,
    VectorTextPaint Paint = default,
    int LabelId = -1,
    VectorTilePoint[]? LinePoints = null,
    double LineSpacing = 250,
    double Opacity = 1,
    bool ContinuousLinePlacement = false,
    double Rotation = 0,
    VectorIconPaint IconPaint = default,
    long SymbolGroupId = -1,
    double SortKey = 0,
    bool AllowOverlap = false,
    bool IgnorePlacement = false,
    bool Optional = false,
    VectorIconTextFit TextFit = VectorIconTextFit.None,
    bool ViewportAligned = false,
    Vector4 TextFitPadding = default,
    double CollisionPadding = 2,
    bool AvoidEdges = false,
    bool KeepUpright = true,
    double MaximumAngle = Math.PI / 4);

/// <summary>
/// Describes one projected vector symbol rectangle ready for texture batching.
/// </summary>
internal readonly record struct VectorSymbolPlacement(
    int StyleLayerOrder,
    long TextureId,
    double Left,
    double Top,
    double Width,
    double Height,
    VectorSymbolKind Kind = VectorSymbolKind.Icon,
    VectorTextPaint Paint = default,
    int LabelId = -1,
    long CollisionGroup = -1,
    double Rotation = 0,
    int PlacementIndex = 0,
    bool IsLinePlacement = false,
    double Opacity = 1,
    bool IsContinuousLinePlacement = false,
    VectorIconPaint IconPaint = default,
    long SymbolGroupId = -1,
    double SortKey = 0,
    bool AllowOverlap = false,
    bool IgnorePlacement = false,
    bool Optional = false,
    long CollisionFamily = -1,
    double CollisionPadding = 2,
    bool AvoidEdges = false);

/// <summary>
/// Carries resolved point symbols and privacy-safe aggregate failures for one display zoom.
/// </summary>
internal sealed record VectorSymbolResolution(
    VectorTileSymbol[] Symbols,
    int EvaluationFailureCount,
    int UnavailableSpriteCount,
    int ResolvedGlyphCount = 0,
    int UnavailableGlyphCount = 0);

/// <summary>
/// Groups projected symbols sharing one sprite texture.
/// </summary>
internal sealed record VectorSymbolBatch(
    int StyleLayerOrder,
    long TextureId,
    VectorSymbolKind Kind,
    VectorTextPaint Paint,
    VectorIconPaint IconPaint,
    double Opacity,
    VectorSymbolPlacement[] Placements);

internal enum VectorSymbolKind
{
    Icon,
    Text,
}

internal readonly record struct VectorTextPaint(
    Vector4 Color,
    Vector4 HaloColor,
    double HaloOffset,
    double HaloBlur = 0);

internal readonly record struct VectorIconPaint(
    Vector4 Color,
    bool IsTinted)
{
    internal static VectorIconPaint Default { get; } =
        new(Vector4.One, false);
}

internal enum VectorIconTextFit
{
    None,
    Width,
    Height,
    Both,
}

internal sealed record VectorTileStyledLine(
    int StyleLayerOrder,
    VectorTilePoint[] Points,
    VectorLineStyle Style);

internal sealed record VectorLineResolution(
    VectorTileStyledLine[] Lines,
    int EvaluationFailureCount);

internal readonly record struct VectorLineStyle(
    Vector4 Color,
    double Width,
    VectorLineCap Cap,
    VectorLineJoin Join,
    ImmutableArray<double> DashArray = default,
    double Offset = 0,
    double GapWidth = 0,
    double Blur = 0,
    double MiterLimit = 2,
    ImmutableArray<VectorLineGradientStop> Gradient = default);

internal readonly record struct VectorLineGradientStop(
    double Offset,
    Vector4 Color);

internal enum VectorLineCap
{
    Butt,
    Round,
    Square,
}

internal enum VectorLineJoin
{
    Bevel,
    Round,
    Miter,
}

internal sealed record VectorTileStyledPolygon(
    int StyleLayerOrder,
    VectorTileRing[] Rings,
    VectorTilePoint[] FillTriangles,
    VectorFillStyle Style);

internal sealed record VectorPolygonResolution(
    VectorTileStyledPolygon[] Polygons,
    int EvaluationFailureCount);

internal readonly record struct VectorFillStyle(
    Vector4 Color,
    Vector4? OutlineColor = null,
    long PatternTextureId = 0,
    double PatternWidth = 0,
    double PatternHeight = 0,
    double Opacity = 1,
    double TranslateX = 0,
    double TranslateY = 0,
    VectorTranslateAnchor TranslateAnchor = VectorTranslateAnchor.Map,
    bool Antialias = true)
{
    internal bool HasPattern => PatternTextureId != 0;
}

internal enum VectorTranslateAnchor
{
    Map,
    Viewport,
}

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
    long Reservation,
    VectorTileData? VectorTile = null);

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
