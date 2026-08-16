namespace WinUIEx.Maps;

/// <summary>
/// Collects initial raster or Mapbox vector source, visibility-range, and transition
/// settings for a <see cref="TileLayer"/>.
/// </summary>
/// <remarks>
/// This is a mutable, non-dependency-object options container. Property setters do not
/// validate values; the <see cref="TileLayer"/> constructor validates and copies them on the
/// UI thread. Later changes to this object do not affect an existing layer.
/// </remarks>
public sealed class TileLayerOptions
{
    /// <summary>
    /// Gets or sets the HTTP or HTTPS tile template.
    /// </summary>
    /// <remarks>
    /// Supported placeholders are <c>{z}</c>, <c>{x}</c>, <c>{y}</c>,
    /// <c>{quadkey}</c>, <c>{bbox-epsg-3857}</c>, <c>{subdomain}</c>, and the
    /// aliases <c>[level]</c>, <c>[column]</c>, and <c>[row]</c>. The default empty
    /// string disables tile acquisition.
    /// </remarks>
    public string TileUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional HTTP or HTTPS Mapbox Style Specification URL.
    /// </summary>
    /// <remarks>
    /// When non-null, <see cref="TileUrl"/> is interpreted as a Mapbox Vector Tile PBF
    /// template and this URL supplies its style, sprite, and glyph configuration.
    /// The default is <see langword="null"/>, which configures a raster tile layer.
    /// </remarks>
    public string? StyleUrl { get; set; }

    /// <summary>
    /// Gets or sets request headers applied to this layer's tile and style-resource requests.
    /// </summary>
    /// <remarks>
    /// Header names and values are copied when the layer is constructed. Headers are sent
    /// only to the origins explicitly configured by <see cref="TileUrl"/> and
    /// <see cref="StyleUrl"/>; they are not forwarded to a different origin referenced by a
    /// downloaded style.
    /// </remarks>
    public IReadOnlyDictionary<string, string> RequestHeaders { get; set; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Gets or sets the geographic source bounds. The default is the Web Mercator world.
    /// </summary>
    /// <remarks>Bounds must be ordered and cannot cross the antimeridian.</remarks>
    public TileLayerBounds Bounds { get; set; } = TileLayerBounds.World;

    /// <summary>
    /// Gets or sets whether row numbers use the TMS bottom-to-top convention.
    /// </summary>
    /// <value>
    /// <see langword="true"/> for TMS numbering; <see langword="false"/> for XYZ numbering.
    /// The default is <see langword="false"/>.
    /// </value>
    public bool IsTMS { get; set; }

    /// <summary>
    /// Gets or sets the maximum available source zoom, inclusively. The default is 22.
    /// </summary>
    /// <remarks>Valid values are from 0 through 22 and must not be below <see cref="MinSourceZoom"/>.</remarks>
    public int MaxSourceZoom { get; set; } = 22;

    /// <summary>
    /// Gets or sets the minimum available source zoom, inclusively. The default is 0.
    /// </summary>
    /// <remarks>Valid values are from 0 through 22 and must not exceed <see cref="MaxSourceZoom"/>.</remarks>
    public int MinSourceZoom { get; set; }

    /// <summary>
    /// Gets or sets host-name subdomains used by <c>{subdomain}</c>.
    /// </summary>
    /// <remarks>
    /// Values must be nonblank. The layer constructor defensively copies the list.
    /// </remarks>
    public IReadOnlyList<string> Subdomains { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the native width and height of each square source tile, in pixels.
    /// </summary>
    /// <remarks>
    /// The default is 512 and valid values are from 1 through 4096. The downloaded image must
    /// exactly match this size, which also affects source-zoom selection.
    /// </remarks>
    public int TileSize { get; set; } = 512;

    /// <summary>
    /// Gets or sets the inclusive minimum camera zoom at which the layer is visible.
    /// The default is 0.
    /// </summary>
    /// <remarks>
    /// This display threshold is independent of <see cref="MinSourceZoom"/> and must be less
    /// than <see cref="MaxZoom"/>.
    /// </remarks>
    public double MinZoom { get; set; }

    /// <summary>
    /// Gets or sets the exclusive maximum camera zoom at which the layer is visible.
    /// The default is 24.
    /// </summary>
    /// <remarks>
    /// This display threshold is independent of <see cref="MaxSourceZoom"/> and must be
    /// greater than <see cref="MinZoom"/>.
    /// </remarks>
    public double MaxZoom { get; set; } = 24;

    /// <summary>
    /// Gets or sets how long newly downloaded tiles fade in. The default is 300 milliseconds.
    /// </summary>
    /// <remarks>The value must be finite and nonnegative; zero disables the fade.</remarks>
    public TimeSpan FadeDuration { get; set; } = TimeSpan.FromMilliseconds(300);
}
