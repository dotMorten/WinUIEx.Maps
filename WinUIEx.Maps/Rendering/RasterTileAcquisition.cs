using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Immutable custom-template acquisition state created by <see cref="TileLayer"/> on the UI
/// thread and consumed only through thread-safe background operations.
/// </summary>
/// <remarks>
/// <para>
/// The session captures the URL template, geographic bounds, XYZ/TMS orientation, source
/// zoom limits, subdomains, and tile size so manager workers never read the originating
/// dependency object. Source-key equality determines whether existing generations and GPU
/// cache entries remain valid.
/// </para>
/// <para>
/// Worker calls expand placeholders deterministically, validate the resulting HTTP or HTTPS
/// URL, use bounded shared HTTP I/O, decode exactly one configured-size BGRA tile, and honor
/// cancellation throughout. Templates, expanded URLs, subdomains, response bodies, and
/// pixels remain request data and are not ETW payloads.
/// </para>
/// </remarks>
internal sealed class CustomRasterTileAcquisitionSession : RasterTileAcquisitionSession
{
    private const int EncodedTileOverheadAllowance = 1024 * 1024;
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private readonly string _tileUrl;
    private readonly TileLayerBounds _bounds;
    private readonly bool _isTms;
    private readonly string[] _subdomains;
    private readonly CustomRasterSourceKey _sourceKey;

    /// <summary>
    /// Captures custom tile-template configuration into immutable state suitable for
    /// concurrent background acquisition.
    /// </summary>
    /// <remarks>
    /// Template URLs and subdomains remain in process for request expansion and equality
    /// only; diagnostic representations must not expose them.
    /// </remarks>
    internal CustomRasterTileAcquisitionSession(
        string tileUrl,
        TileLayerBounds bounds,
        bool isTms,
        int maximumSourceZoom,
        int minimumSourceZoom,
        IEnumerable<string> subdomains,
        int tileSize)
    {
        _tileUrl = tileUrl;
        _bounds = bounds;
        _isTms = isTms;
        MaxSourceZoom = maximumSourceZoom;
        MinSourceZoom = minimumSourceZoom;
        _subdomains = subdomains.ToArray();
        TileSize = tileSize;
        _sourceKey = new CustomRasterSourceKey(
            tileUrl,
            bounds,
            isTms,
            maximumSourceZoom,
            minimumSourceZoom,
            string.Join("\u001f", _subdomains),
            tileSize);
    }

    internal override object SourceKey => _sourceKey;

    internal override RasterSourceKind SourceKind => RasterSourceKind.Custom;

    internal override int TileSize { get; }

    internal override int MinSourceZoom { get; }

    internal override int MaxSourceZoom { get; }

    internal override bool CanAcquire => _tileUrl.Length != 0;

    internal bool IsTms => _isTms;

    /// <summary>
    /// Selects a source zoom adjusted for configured tile size.
    /// </summary>
    internal override int GetSourceZoom(MapScene scene) =>
        GetSourceZoom(scene.Zoom, TileSize);

    /// <summary>
    /// Determines whether a tile intersects the immutable geographic bounds of this source.
    /// </summary>
    internal override bool IncludesTile(TileId id) => IntersectsBounds(id, _bounds);

    /// <summary>
    /// Expands, downloads, bounds, and decodes one custom raster tile as straight-alpha BGRA
    /// pixels.
    /// </summary>
    /// <remarks>
    /// Runs on acquisition workers, honors cancellation during I/O and decoding, and does not
    /// log the expanded template URL or returned pixels.
    /// </remarks>
    internal override async Task<DecodedRasterTile> GetTileAsync(
        TileId id,
        CancellationToken cancellationToken)
    {
        long downloadStarted = Stopwatch.GetTimestamp();
        string requestUrl = ExpandUrl(id);
        using HttpRequestMessage request = RasterTileHttp.CreateRequest(
            HttpMethod.Get,
            requestUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        using HttpResponseMessage response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        int maximumEncodedBytes = checked(
            (TileSize * TileSize * 4) + EncodedTileOverheadAllowance);
        byte[] encoded = await RasterTileHttp.ReadBoundedAsync(
                response.Content,
                maximumEncodedBytes,
                cancellationToken)
            .ConfigureAwait(false);
        double downloadMilliseconds =
            Stopwatch.GetElapsedTime(downloadStarted).TotalMilliseconds;
        long decodeStarted = Stopwatch.GetTimestamp();
        using MemoryStream stream = new(encoded, writable: false);
        using Windows.Storage.Streams.IRandomAccessStream randomAccessStream =
            stream.AsRandomAccessStream();
        BitmapDecoder decoder = await BitmapDecoder
            .CreateAsync(randomAccessStream)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (decoder.PixelWidth != TileSize || decoder.PixelHeight != TileSize)
        {
            throw new InvalidDataException(
                $"Raster tile dimensions do not match the configured TileSize {TileSize}.");
        }

        PixelDataProvider provider = await decoder
            .GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] pixels = provider.DetachPixelData();
        if (!MapRenderer.IsValidPixelBuffer(pixels, decoder.PixelWidth, decoder.PixelHeight))
        {
            throw new InvalidDataException("Decoded raster tile has an invalid BGRA buffer.");
        }

        return new DecodedRasterTile(
            id,
            pixels,
            decoder.PixelWidth,
            decoder.PixelHeight,
            downloadMilliseconds,
            Stopwatch.GetElapsedTime(decodeStarted).TotalMilliseconds);
    }

    /// <summary>
    /// Expands supported XYZ, TMS, quadkey, bounds, and subdomain placeholders for a valid
    /// tile and verifies an HTTP or HTTPS result.
    /// </summary>
    /// <remarks>
    /// The returned URL may contain private source configuration and must not be written to
    /// diagnostics.
    /// </remarks>
    internal string ExpandUrl(TileId id)
    {
        ValidateTileCoordinates(id);
        int y = _isTms ? ((1 << id.Zoom) - 1 - id.Y) : id.Y;
        string subdomain = _subdomains.Length == 0
            ? string.Empty
            : _subdomains[DeterministicSubdomainIndex(id, _subdomains.Length)];
        string result = _tileUrl
            .Replace("{z}", id.Zoom.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{x}", id.X.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{y}", y.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("[level]", id.Zoom.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("[column]", id.X.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("[row]", y.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{quadkey}", GetQuadKey(id.Zoom, id.X, id.Y), StringComparison.OrdinalIgnoreCase)
            .Replace("{bbox-epsg-3857}", GetWebMercatorBounds(id.Zoom, id.X, id.Y), StringComparison.OrdinalIgnoreCase)
            .Replace("{subdomain}", subdomain, StringComparison.OrdinalIgnoreCase);
        if (!Uri.TryCreate(result, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("The expanded tile template is not an HTTP/HTTPS URL.");
        }
        return result;
    }

    /// <summary>
    /// Computes the integral source zoom that preserves scale for non-256-pixel tiles while
    /// bounding oversampling.
    /// </summary>
    internal static int GetSourceZoom(double cameraZoom, int tileSize)
    {
        double offset = Math.Log2(256d / tileSize);
        const int MaximumSourceZoomOffset = 3;
        return Math.Min(
            (int)Math.Floor(cameraZoom + offset),
            (int)Math.Floor(cameraZoom + MaximumSourceZoomOffset));
    }

    /// <summary>
    /// Determines whether a valid Web Mercator tile overlaps geographic layer bounds.
    /// </summary>
    internal static bool IntersectsBounds(TileId id, TileLayerBounds bounds)
    {
        ValidateTileCoordinates(id);
        double count = 1 << id.Zoom;
        double west = (id.X / count * 360) - 180;
        double east = ((id.X + 1) / count * 360) - 180;
        double north = MapCamera.WorldYToLatitude(id.Y / count);
        double south = MapCamera.WorldYToLatitude((id.Y + 1) / count);
        return west < bounds.East &&
            east > bounds.West &&
            south < bounds.North &&
            north > bounds.South;
    }

    /// <summary>
    /// Encodes valid XYZ tile coordinates as a Bing-compatible quadkey.
    /// </summary>
    internal static string GetQuadKey(int zoom, int x, int y)
    {
        ValidateTileCoordinates(new TileId(zoom, x, y));
        Span<char> quadKey = zoom <= 64 ? stackalloc char[zoom] : new char[zoom];
        for (int level = zoom; level > 0; level--)
        {
            int mask = 1 << (level - 1);
            int digit = 0;
            if ((x & mask) != 0)
            {
                digit++;
            }
            if ((y & mask) != 0)
            {
                digit += 2;
            }
            quadKey[zoom - level] = (char)('0' + digit);
        }
        return new string(quadKey);
    }

    /// <summary>
    /// Selects a stable subdomain index from tile coordinates to distribute requests
    /// consistently.
    /// </summary>
    private static int DeterministicSubdomainIndex(TileId id, int count) =>
        (int)((((uint)id.X * 73856093u) ^
            ((uint)id.Y * 19349663u) ^
            ((uint)id.Zoom * 83492791u)) % (uint)count);

    /// <summary>
    /// Formats a tile's EPSG:3857 bounding box with invariant round-trip precision.
    /// </summary>
    private static string GetWebMercatorBounds(int zoom, int x, int y)
    {
        const double extent = 20037508.342789244;
        double count = 1 << zoom;
        double tileSpan = (extent * 2) / count;
        double left = -extent + (x * tileSpan);
        double right = left + tileSpan;
        double top = extent - (y * tileSpan);
        double bottom = top - tileSpan;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{left:R},{bottom:R},{right:R},{top:R}");
    }

    /// <summary>
    /// Verifies that a tile zoom and coordinates lie within the supported pyramid.
    /// </summary>
    private static void ValidateTileCoordinates(TileId id)
    {
        if (id.Zoom is < 0 or > MapCamera.MaximumTileZoom)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }
        int count = 1 << id.Zoom;
        if (id.X < 0 || id.X >= count || id.Y < 0 || id.Y >= count)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }
    }

    /// <summary>
    /// Creates the shared custom-tile HTTP client with bounded lifetime, timeout, and product
    /// identification.
    /// </summary>
    private static HttpClient CreateHttpClient()
    {
        SocketsHttpHandler handler = new()
        {
            ConnectCallback = ConnectAsync,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        HttpClient client = new(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinUIEx.Maps/1.0");
        return client;
    }

    /// <summary>
    /// Connects to the first selected DNS address and transfers successful socket ownership
    /// to a network stream.
    /// </summary>
    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            cancellationToken).ConfigureAwait(false);
        Exception? lastError = null;
        foreach (IPAddress address in RasterTileHttp.SelectConnectionAddresses(addresses))
        {
            Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(
                    address,
                    context.DnsEndPoint.Port,
                    cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                lastError = exception;
                socket.Dispose();
            }
        }
        throw new HttpRequestException("No resolved address accepted a connection.", lastError);
    }

    /// <summary>
    /// Captures every custom-source value that can change acquired pixels for in-process
    /// equality and generation invalidation.
    /// </summary>
    /// <remarks>
    /// This key contains the private URL template and subdomain configuration. Its
    /// <see cref="ToString"/> override intentionally returns only the type name, and the key
    /// must never be sent to ETW or user-facing diagnostics.
    /// </remarks>
    private sealed record CustomRasterSourceKey(
        string TileUrl,
        TileLayerBounds Bounds,
        bool IsTms,
        int MaximumSourceZoom,
        int MinimumSourceZoom,
        string Subdomains,
        int TileSize)
    {
        /// <summary>
        /// Returns a non-sensitive type name instead of exposing the template URL or
        /// subdomains retained by this equality key.
        /// </summary>
        public override string ToString() => nameof(CustomRasterSourceKey);
    }
}

/// <summary>
/// Provides shared bounded HTTP content reading and deterministic DNS-address selection for
/// immutable raster acquisition sessions.
/// </summary>
/// <remarks>
/// The helper owns no source configuration and logs nothing. Callers retain ownership of
/// response objects, while <see cref="ReadBoundedAsync"/> owns only the content stream it
/// opens and enforces both declared and observed byte limits before image decoding.
/// </remarks>
internal static class RasterTileHttp
{
    /// <summary>
    /// Creates a raster request that actually opts into HTTP/2 when supported. Explicit
    /// <see cref="HttpRequestMessage"/> instances otherwise retain their HTTP/1.1 defaults
    /// even when the owning client's defaults request HTTP/2.
    /// </summary>
    internal static HttpRequestMessage CreateRequest(HttpMethod method, string requestUri) =>
        new(method, requestUri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

    /// <summary>
    /// Reads HTTP content into memory while enforcing a hard encoded-byte limit from both
    /// headers and streamed data.
    /// </summary>
    /// <remarks>
    /// Owns and asynchronously disposes the response content stream but leaves the
    /// <see cref="HttpContent"/> lifetime with the caller.
    /// </remarks>
    internal static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException(
                "The encoded raster tile exceeds the configured size limit.");
        }

        await using Stream contentStream = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using MemoryStream buffer = new(Math.Min(
            maximumBytes,
            (int)(content.Headers.ContentLength ?? 16 * 1024)));
        byte[] chunk = new byte[16 * 1024];
        while (true)
        {
            int read = await contentStream.ReadAsync(chunk, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }
            if (buffer.Length + read > maximumBytes)
            {
                throw new InvalidDataException(
                    "The encoded raster tile exceeds the configured size limit.");
            }
            buffer.Write(chunk, 0, read);
        }
    }

    /// <summary>
    /// Chooses at most one IPv4 and one IPv6 address, preferring IPv4, to bound connection
    /// attempts for a DNS result.
    /// </summary>
    internal static IPAddress[] SelectConnectionAddresses(IEnumerable<IPAddress> addresses) =>
        addresses
            .Where(address =>
                address.AddressFamily is AddressFamily.InterNetwork or
                    AddressFamily.InterNetworkV6)
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .GroupBy(address => address.AddressFamily)
            .Select(group => group.First())
            .ToArray();
}
