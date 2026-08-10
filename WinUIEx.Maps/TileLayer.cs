using Microsoft.UI.Xaml;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps;

/// <summary>
/// Displays a bounded raster tile source described by an HTTP or HTTPS URL template.
/// </summary>
/// <remarks>
/// <para>
/// A template may use XYZ aliases <c>{z}</c>, <c>{x}</c>, and <c>{y}</c>, their
/// <c>[level]</c>, <c>[column]</c>, and <c>[row]</c> equivalents, or
/// <c>{quadkey}</c>, <c>{bbox-epsg-3857}</c>, and <c>{subdomain}</c>. Set
/// <see cref="IsTMS"/> when the endpoint numbers rows from bottom to top. <see cref="Bounds"/>
/// limits requests geographically, and <see cref="Subdomains"/> supplies values for
/// <c>{subdomain}</c>.
/// </para>
/// <para>
/// <see cref="TileSize"/> must match the native pixel width and height returned by the
/// source; mismatched images are rejected. Tile size also affects source-zoom selection, so a
/// 512-pixel source is not interchangeable with a 256-pixel source merely because both are
/// square. <see cref="MinSourceZoom"/> and <see cref="MaxSourceZoom"/> describe available
/// source levels. <see cref="MinZoom"/> and <see cref="MaxZoom"/> independently control the
/// inclusive lower and exclusive upper camera zoom at which the layer is displayed and
/// acquired. <see cref="FadeDuration"/> controls the transition for newly available tiles.
/// </para>
/// <para>
/// This type is a <see cref="DependencyObject"/>. Construct it and access all layer
/// properties on the owning UI thread. The control captures an immutable snapshot at that
/// boundary; acquisition, decoding, upload, and rendering workers never read the dependency
/// object. Changing source configuration or moving to a new source zoom cancels obsolete work
/// when possible. Network concurrency and the upload queue are bounded, pending tiles are
/// deduplicated, and accepted textures are cached and evicted under a memory budget. These
/// safeguards provide backpressure but do not make cancellation instantaneous or guarantee
/// that a request already received by an endpoint can be withdrawn.
/// </para>
/// <para>
/// Configure only endpoints whose terms permit this use. Respect rate limits, caching rules,
/// authentication requirements, and any required user agent or headers supported by the
/// endpoint. Custom sources do not receive automatic attribution from this layer; the
/// application must visibly present all attribution required by the provider.
/// </para>
/// <para>
/// Although the class is not sealed, deriving from it does not expose a public custom
/// acquisition or rendering extension point.
/// </para>
/// </remarks>
/// <example>
/// The following token-free OpenStreetMap layer uses 256-pixel XYZ tiles over a blank map.
/// The application must also visibly display
/// <c>&#xA9; OpenStreetMap contributors</c> and comply with the OpenStreetMap tile usage
/// policy.
/// <code>
/// using WinUIEx.Maps;
///
/// var map = new MapControl
/// {
///     MapStyle = MapStyle.Blank,
/// };
/// var layer = new TileLayer(new TileLayerOptions
/// {
///     TileUrl = "https://tile.openstreetmap.org/[level]/[column]/[row].png",
///     TileSize = 256,
///     MaxSourceZoom = 19,
/// });
///
/// map.Layers.Add(layer);
/// </code>
/// </example>
public class TileLayer : MapLayer
{
    private static long _nextRuntimeId;
    private bool _isRestoringValue;
    private string _tileUrl = string.Empty;
    private TileLayerBounds _bounds = TileLayerBounds.World;
    private int _maxSourceZoom = 22;
    private int _minSourceZoom;
    private IReadOnlyList<string> _subdomains = Array.Empty<string>();
    private int _tileSize = 512;
    private double _minZoom;
    private double _maxZoom = 24;
    private TimeSpan _fadeDuration = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Initializes a raster tile layer.
    /// </summary>
    /// <param name="options">Initial rendering options, or <see langword="null"/> for defaults.</param>
    /// <param name="id">
    /// A stable application-facing identifier. A random identifier is generated when null or blank.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// A required value in <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The URL, subdomains, or relationship between minimum and maximum zoom values is
    /// invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Bounds, a zoom value, tile size, or fade duration is outside its supported range.
    /// </exception>
    /// <remarks>
    /// The options are validated and copied into dependency properties. Subsequent changes
    /// to the options object do not affect the layer. Construct the layer on its intended UI
    /// thread.
    /// </remarks>
    public TileLayer(TileLayerOptions? options = null, string? id = null)
    {
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
        RuntimeId = Interlocked.Increment(ref _nextRuntimeId);
        TileLayerOptions value = options ?? new TileLayerOptions();
        ValidateOptions(value);
        TileUrl = value.TileUrl;
        Bounds = value.Bounds;
        IsTMS = value.IsTMS;
        MaxSourceZoom = value.MaxSourceZoom;
        MinSourceZoom = value.MinSourceZoom;
        Subdomains = value.Subdomains;
        TileSize = value.TileSize;
        MinZoom = value.MinZoom;
        MaxZoom = value.MaxZoom;
        FadeDuration = value.FadeDuration;
    }

    /// <summary>Gets the stable application-facing identifier of this layer.</summary>
    /// <value>
    /// The supplied nonblank identifier, or a generated identifier when none was supplied.
    /// </value>
    /// <remarks>
    /// This identifier is not sent to the tile endpoint and is distinct from internal source
    /// generations used for caching and cancellation.
    /// </remarks>
    public string Id { get; }

    /// <summary>
    /// Gets or sets the HTTP or HTTPS tile template.
    /// </summary>
    /// <remarks>
    /// It may contain <c>{z}</c>, <c>{x}</c>, <c>{y}</c>, <c>{quadkey}</c>,
    /// <c>{bbox-epsg-3857}</c>, <c>{subdomain}</c>, <c>[level]</c>,
    /// <c>[column]</c>, and <c>[row]</c>. An empty value disables acquisition. Changing the
    /// template creates a new source identity and cancels obsolete acquisition work.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// The assigned value is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The assigned value is neither empty nor an absolute HTTP or HTTPS URI.
    /// </exception>
    public string TileUrl
    {
        get => (string)GetValue(TileUrlProperty);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            ValidateTileUrl(value);
            SetValue(TileUrlProperty, value);
        }
    }

    /// <summary>Identifies the <see cref="TileUrl"/> dependency property.</summary>
    public static readonly DependencyProperty TileUrlProperty = Register(
        nameof(TileUrl), typeof(string), string.Empty);

    /// <summary>Gets or sets the non-wrapping geographic coverage of the tile source.</summary>
    /// <value>The source bounds. The default is <see cref="TileLayerBounds.World"/>.</value>
    /// <remarks>
    /// Tiles outside these bounds are not requested. Bounds cannot cross the antimeridian.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The assigned bounds are not finite, ordered, or within the Web Mercator world.
    /// </exception>
    public TileLayerBounds Bounds
    {
        get => (TileLayerBounds)GetValue(BoundsProperty);
        set
        {
            ValidateBounds(value);
            SetValue(BoundsProperty, value);
        }
    }

    /// <summary>Identifies the <see cref="Bounds"/> dependency property.</summary>
    public static readonly DependencyProperty BoundsProperty = Register(
        nameof(Bounds), typeof(TileLayerBounds), TileLayerBounds.World);

    /// <summary>
    /// Gets or sets whether source rows use TMS bottom-to-top rather than XYZ top-to-bottom
    /// numbering.
    /// </summary>
    /// <value><see langword="true"/> for TMS row numbering; otherwise, <see langword="false"/>. The default is <see langword="false"/>.</value>
    /// <remarks>
    /// When enabled, the row is flipped at the selected source zoom before URL placeholders
    /// are expanded.
    /// </remarks>
    public bool IsTMS
    {
        get => (bool)GetValue(IsTMSProperty);
        set => SetValue(IsTMSProperty, value);
    }

    /// <summary>Identifies the <see cref="IsTMS"/> dependency property.</summary>
    public static readonly DependencyProperty IsTMSProperty = Register(
        nameof(IsTMS), typeof(bool), false);

    /// <summary>Gets or sets the highest source zoom that may be requested.</summary>
    /// <value>An inclusive source zoom from 0 through 22. The default is 22.</value>
    /// <remarks>
    /// Requests above this level reuse the maximum source level and scale its tiles for
    /// display.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is outside 0 through 22 or is less than <see cref="MinSourceZoom"/>.
    /// </exception>
    public int MaxSourceZoom
    {
        get => (int)GetValue(MaxSourceZoomProperty);
        set
        {
            ValidateSourceZoom(value, nameof(value));
            if (value < MinSourceZoom)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "MaxSourceZoom must be at least MinSourceZoom.");
            }
            SetValue(MaxSourceZoomProperty, value);
        }
    }

    /// <summary>Identifies the <see cref="MaxSourceZoom"/> dependency property.</summary>
    public static readonly DependencyProperty MaxSourceZoomProperty = Register(
        nameof(MaxSourceZoom), typeof(int), 22);

    /// <summary>Gets or sets the lowest source zoom that may be requested.</summary>
    /// <value>An inclusive source zoom from 0 through 22. The default is 0.</value>
    /// <remarks>
    /// The layer does not acquire tiles when the source zoom selected from the camera and
    /// <see cref="TileSize"/> is below this level.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is outside 0 through 22 or exceeds <see cref="MaxSourceZoom"/>.
    /// </exception>
    public int MinSourceZoom
    {
        get => (int)GetValue(MinSourceZoomProperty);
        set
        {
            ValidateSourceZoom(value, nameof(value));
            if (value > MaxSourceZoom)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "MinSourceZoom must not exceed MaxSourceZoom.");
            }
            SetValue(MinSourceZoomProperty, value);
        }
    }

    /// <summary>Identifies the <see cref="MinSourceZoom"/> dependency property.</summary>
    public static readonly DependencyProperty MinSourceZoomProperty = Register(
        nameof(MinSourceZoom), typeof(int), 0);

    /// <summary>
    /// Gets or sets subdomain values used by <c>{subdomain}</c>.
    /// </summary>
    /// <value>An ordered list of nonblank subdomain strings. The default list is empty.</value>
    /// <remarks>
    /// The assigned values are defensively copied and exposed read-only. Requests distribute
    /// deterministically across the supplied values. A template containing
    /// <c>{subdomain}</c> requires at least one value for meaningful expansion.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// The assigned list is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The assigned list contains a <see langword="null"/>, empty, or whitespace value.
    /// </exception>
    public IReadOnlyList<string> Subdomains
    {
        get => (IReadOnlyList<string>)GetValue(SubdomainsProperty);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            SetValue(SubdomainsProperty, CopySubdomains(value));
        }
    }

    /// <summary>Identifies the <see cref="Subdomains"/> dependency property.</summary>
    public static readonly DependencyProperty SubdomainsProperty = Register(
        nameof(Subdomains), typeof(IReadOnlyList<string>), Array.Empty<string>());

    /// <summary>
    /// Gets or sets the native width and height, in pixels, of every square source tile.
    /// </summary>
    /// <value>A pixel size from 1 through 4096. The default is 512.</value>
    /// <remarks>
    /// The downloaded image dimensions must exactly match this value. It also participates in
    /// source-zoom selection; configure the source's real native size rather than a desired
    /// display size. Common values are 256 and 512.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is outside 1 through 4096.
    /// </exception>
    public int TileSize
    {
        get => (int)GetValue(TileSizeProperty);
        set
        {
            ValidateTileSize(value, nameof(value));
            SetValue(TileSizeProperty, value);
        }
    }

    /// <summary>Identifies the <see cref="TileSize"/> dependency property.</summary>
    public static readonly DependencyProperty TileSizeProperty = Register(
        nameof(TileSize), typeof(int), 512);

    /// <summary>Gets or sets the inclusive camera zoom at which the layer becomes active.</summary>
    /// <value>A finite camera zoom from 0 through 24. The default is 0.</value>
    /// <remarks>
    /// This display range is independent of the source zoom range. Outside the display range,
    /// the layer is not drawn and does not acquire tiles.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is non-finite, outside 0 through 24, or not less than
    /// <see cref="MaxZoom"/>.
    /// </exception>
    public double MinZoom
    {
        get => (double)GetValue(MinZoomProperty);
        set
        {
            ValidateLayerZoom(value, nameof(value));
            if (value >= MaxZoom)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "MinZoom must be less than MaxZoom.");
            }
            SetValue(MinZoomProperty, value);
        }
    }

    /// <summary>Identifies the <see cref="MinZoom"/> dependency property.</summary>
    public static readonly DependencyProperty MinZoomProperty = Register(
        nameof(MinZoom), typeof(double), 0d);

    /// <summary>Gets or sets the exclusive camera zoom at which the layer becomes inactive.</summary>
    /// <value>A finite camera zoom from 0 through 24. The default is 24.</value>
    /// <remarks>
    /// This display range is independent of the source zoom range. Outside the display range,
    /// the layer is not drawn and does not acquire tiles.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is non-finite, outside 0 through 24, or not greater than
    /// <see cref="MinZoom"/>.
    /// </exception>
    public double MaxZoom
    {
        get => (double)GetValue(MaxZoomProperty);
        set
        {
            ValidateLayerZoom(value, nameof(value));
            if (value <= MinZoom)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "MaxZoom must be greater than MinZoom.");
            }
            SetValue(MaxZoomProperty, value);
        }
    }

    /// <summary>Identifies the <see cref="MaxZoom"/> dependency property.</summary>
    public static readonly DependencyProperty MaxZoomProperty = Register(
        nameof(MaxZoom), typeof(double), 24d);

    /// <summary>
    /// Gets or sets the duration over which newly available tiles reach the layer opacity.
    /// </summary>
    /// <value>
    /// A finite, nonnegative duration. The default is 300 milliseconds; use
    /// <see cref="TimeSpan.Zero"/> for immediate display.
    /// </value>
    /// <remarks>
    /// Fading applies when a tile is committed for rendering and does not delay network
    /// acquisition or decoding.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The duration is negative or its total milliseconds are not finite.
    /// </exception>
    public TimeSpan FadeDuration
    {
        get => (TimeSpan)GetValue(FadeDurationProperty);
        set
        {
            ValidateFadeDuration(value, nameof(value));
            SetValue(FadeDurationProperty, value);
        }
    }

    /// <summary>Identifies the <see cref="FadeDuration"/> dependency property.</summary>
    public static readonly DependencyProperty FadeDurationProperty = Register(
        nameof(FadeDuration), typeof(TimeSpan), TimeSpan.FromMilliseconds(300));

    internal long RuntimeId { get; }

    /// <summary>
    /// Captures all dependency-object state into an immutable worker-safe snapshot.
    /// </summary>
    /// <remarks>
    /// UI-thread-only. Overrides must read all required layer/dependency-property state here
    /// and return an immutable <see cref="RasterTileAcquisitionSession"/>. Scheduler workers
    /// may call that session concurrently; they never call this method or access this layer.
    /// This method is internal so deriving from <see cref="TileLayer"/> does not create a
    /// public tile-source extensibility contract.
    /// </remarks>
    internal virtual TileLayerSnapshot CreateSnapshot()
    {
        string tileUrl = TileUrl;
        TileLayerBounds bounds = Bounds;
        bool isTms = IsTMS;
        int maximumSourceZoom = MaxSourceZoom;
        int minimumSourceZoom = MinSourceZoom;
        string[] subdomains = Subdomains.ToArray();
        int tileSize = TileSize;
        return new TileLayerSnapshot(
            RuntimeId,
            Revision,
            new CustomRasterTileAcquisitionSession(
                tileUrl,
                bounds,
                isTms,
                maximumSourceZoom,
                minimumSourceZoom,
                subdomains,
                tileSize),
            MinZoom,
            MaxZoom,
            IsVisible,
            Opacity,
            FadeDuration);
    }

    private static DependencyProperty Register(string name, Type type, object defaultValue) =>
        DependencyProperty.Register(
            name,
            type,
            typeof(TileLayer),
            new PropertyMetadata(defaultValue, OnTilePropertyChanged));

    private static void OnTilePropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        TileLayer layer = (TileLayer)dependencyObject;
        if (layer._isRestoringValue)
        {
            return;
        }

        object fallback = layer.GetFallback(args.Property);
        if (!layer.TryAccept(args.Property, args.NewValue))
        {
            layer._isRestoringValue = true;
            try
            {
                layer.SetValue(args.Property, fallback);
            }
            finally
            {
                layer._isRestoringValue = false;
            }
            return;
        }

        layer.NotifyTilePropertyChanged(args.Property);
    }

    private bool TryAccept(DependencyProperty property, object value)
    {
        if (property == TileUrlProperty && value is string tileUrl && IsValidTileUrl(tileUrl))
        {
            _tileUrl = tileUrl;
        }
        else if (property == BoundsProperty && value is TileLayerBounds bounds && TileLayerBounds.IsValid(bounds))
        {
            _bounds = bounds;
        }
        else if (property == IsTMSProperty && value is bool)
        {
        }
        else if (property == MaxSourceZoomProperty && value is int maxSourceZoom &&
            IsValidSourceZoom(maxSourceZoom) && maxSourceZoom >= _minSourceZoom)
        {
            _maxSourceZoom = maxSourceZoom;
        }
        else if (property == MinSourceZoomProperty && value is int minSourceZoom &&
            IsValidSourceZoom(minSourceZoom) && minSourceZoom <= _maxSourceZoom)
        {
            _minSourceZoom = minSourceZoom;
        }
        else if (property == SubdomainsProperty && value is IReadOnlyList<string> subdomains &&
            AreValidSubdomains(subdomains))
        {
            _subdomains = CopySubdomains(subdomains);
            if (!ReferenceEquals(value, _subdomains))
            {
                _isRestoringValue = true;
                try
                {
                    SetValue(SubdomainsProperty, _subdomains);
                }
                finally
                {
                    _isRestoringValue = false;
                }
            }
        }
        else if (property == TileSizeProperty && value is int tileSize && IsValidTileSize(tileSize))
        {
            _tileSize = tileSize;
        }
        else if (property == MinZoomProperty && value is double minZoom &&
            IsValidLayerZoom(minZoom) && minZoom < _maxZoom)
        {
            _minZoom = minZoom;
        }
        else if (property == MaxZoomProperty && value is double maxZoom &&
            IsValidLayerZoom(maxZoom) && maxZoom > _minZoom)
        {
            _maxZoom = maxZoom;
        }
        else if (property == FadeDurationProperty && value is TimeSpan duration &&
            IsValidFadeDuration(duration))
        {
            _fadeDuration = duration;
        }
        else
        {
            return false;
        }
        return true;
    }

    private object GetFallback(DependencyProperty property) =>
        property == TileUrlProperty ? _tileUrl :
        property == BoundsProperty ? _bounds :
        property == IsTMSProperty ? IsTMS :
        property == MaxSourceZoomProperty ? _maxSourceZoom :
        property == MinSourceZoomProperty ? _minSourceZoom :
        property == SubdomainsProperty ? _subdomains :
        property == TileSizeProperty ? _tileSize :
        property == MinZoomProperty ? _minZoom :
        property == MaxZoomProperty ? _maxZoom :
        _fadeDuration;

    private void NotifyTilePropertyChanged(DependencyProperty property)
    {
        NotifyChanged(property);
    }

    private static void ValidateOptions(TileLayerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.TileUrl);
        ValidateTileUrl(options.TileUrl);
        ValidateBounds(options.Bounds);
        ValidateSourceZoom(options.MinSourceZoom, nameof(options.MinSourceZoom));
        ValidateSourceZoom(options.MaxSourceZoom, nameof(options.MaxSourceZoom));
        if (options.MinSourceZoom > options.MaxSourceZoom)
        {
            throw new ArgumentException("MinSourceZoom must not exceed MaxSourceZoom.", nameof(options));
        }
        ArgumentNullException.ThrowIfNull(options.Subdomains);
        _ = CopySubdomains(options.Subdomains);
        ValidateTileSize(options.TileSize, nameof(options.TileSize));
        ValidateLayerZoom(options.MinZoom, nameof(options.MinZoom));
        ValidateLayerZoom(options.MaxZoom, nameof(options.MaxZoom));
        if (options.MinZoom >= options.MaxZoom)
        {
            throw new ArgumentException("MinZoom must be less than MaxZoom.", nameof(options));
        }
        ValidateFadeDuration(options.FadeDuration, nameof(options.FadeDuration));
    }

    internal static void ValidateTileUrl(string value)
    {
        if (!IsValidTileUrl(value))
        {
            throw new ArgumentException("TileUrl must be empty or an absolute HTTP/HTTPS template.", nameof(value));
        }
    }

    private static bool IsValidTileUrl(string value) =>
        value.Length == 0 ||
        (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
         (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));

    private static void ValidateBounds(TileLayerBounds value)
    {
        if (!TileLayerBounds.IsValid(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static bool IsValidSourceZoom(int value) => value is >= 0 and <= 22;

    private static void ValidateSourceZoom(int value, string name)
    {
        if (!IsValidSourceZoom(value))
        {
            throw new ArgumentOutOfRangeException(name, value, "Source zoom must be from 0 through 22.");
        }
    }

    private static bool IsValidTileSize(int value) => value is >= 1 and <= 4096;

    private static void ValidateTileSize(int value, string name)
    {
        if (!IsValidTileSize(value))
        {
            throw new ArgumentOutOfRangeException(name, value, "TileSize must be from 1 through 4096.");
        }
    }

    private static bool IsValidLayerZoom(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 24;

    private static void ValidateLayerZoom(double value, string name)
    {
        if (!IsValidLayerZoom(value))
        {
            throw new ArgumentOutOfRangeException(name, value, "Layer zoom must be finite and from 0 through 24.");
        }
    }

    private static bool IsValidFadeDuration(TimeSpan value) =>
        value >= TimeSpan.Zero && double.IsFinite(value.TotalMilliseconds);

    private static void ValidateFadeDuration(TimeSpan value, string name)
    {
        if (!IsValidFadeDuration(value))
        {
            throw new ArgumentOutOfRangeException(name, value, "FadeDuration must be finite and nonnegative.");
        }
    }

    private static IReadOnlyList<string> CopySubdomains(IReadOnlyList<string> values)
    {
        string[] copy = values.ToArray();
        if (!AreValidSubdomains(copy))
        {
            throw new ArgumentException("Subdomains cannot contain null or blank values.", nameof(values));
        }
        return Array.AsReadOnly(copy);
    }

    private static bool AreValidSubdomains(IReadOnlyList<string> values) =>
        values.All(value => !string.IsNullOrWhiteSpace(value));
}
