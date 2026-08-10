using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Windows.Graphics.Imaging;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Hidden Azure base layer owned by <see cref="MapControl"/> and never exposed through
/// <see cref="MapControl.Layers"/>.
/// </summary>
/// <remarks>
/// The UI thread replaces this dependency object when map style or credential changes and
/// publishes it ahead of public layers. <see cref="CreateSnapshot"/> is the only boundary to
/// rendering workers: it captures style and credential into immutable acquisition state so
/// no background path reads UI-thread properties.
/// </remarks>
internal sealed class AzureTileLayer : TileLayer
{
    private readonly MapStyle _style;
    private readonly string _token;

    /// <summary>
    /// Initializes the hidden base-map layer with the selected Azure style and the
    /// credential that will be captured by later acquisition snapshots.
    /// </summary>
    internal AzureTileLayer(MapStyle style, string? token)
        : base(
            new TileLayerOptions
            {
                TileSize = 256,
                MinSourceZoom = 0,
                MaxSourceZoom = MapCamera.MaximumTileZoom,
                FadeDuration = TimeSpan.FromMilliseconds(250),
            },
            "AzureBaseMap")
    {
        if (style == MapStyle.Blank)
        {
            throw new ArgumentOutOfRangeException(nameof(style));
        }
        _style = style;
        _token = token ?? string.Empty;
    }

    internal MapStyle Style => _style;

    /// <summary>
    /// Determines whether this layer already represents the requested style and credential.
    /// </summary>
    internal bool Matches(MapStyle style, string? token) =>
        _style == style &&
        string.Equals(_token, token ?? string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// Captures Azure style and authentication into a worker-safe immutable session.
    /// </summary>
    /// <remarks>
    /// UI-thread-only. The returned session owns all data used by background requests and
    /// never reads this dependency object.
    /// </remarks>
    internal override TileLayerSnapshot CreateSnapshot() =>
        new(
            RuntimeId,
            Revision,
            new AzureTileAcquisitionSession(_style, _token),
            MinZoom,
            MaxZoom,
            IsVisible,
            Opacity,
            FadeDuration);
}

/// <summary>
/// Immutable Azure acquisition state. All methods are worker-thread-safe and cancellation
/// aware; no method accesses the originating <see cref="AzureTileLayer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Manager workers use this session to select style-specific source zooms, download bounded
/// tile responses, decode BGRA pixels, and optionally compose transparent terrain shading
/// over road tiles. Independent tiles may be acquired concurrently and all network, decode,
/// stitching, and composition stages observe cancellation.
/// </para>
/// <para>
/// Attribution requests run through the same worker lifetime, are sanitized to plain text,
/// and are returned for generation-checked dispatch rather than logged. The credential is
/// carried only in the subscription header and in the private in-process source key; URLs,
/// headers, service content, attribution text, and pixels are excluded from ETW.
/// </para>
/// </remarks>
internal sealed partial class AzureTileAcquisitionSession : RasterTileAcquisitionSession
{
    private const int EncodedTileOverheadAllowance = 1024 * 1024;
    private const string ApiVersion = "2024-04-01";
    private const string RoadTileset = "microsoft.base.road";
    private const string DarkGreyTileset = "microsoft.base.darkgrey";
    private const string ImageryTileset = "microsoft.imagery";
    private const string TerrainTileset = "microsoft.terra.main";
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private readonly AzureRasterSourceKey _sourceKey;
    private readonly MapStyle _style;
    private readonly string _token;

    /// <summary>
    /// Creates immutable Azure request state that can be shared by concurrent acquisition
    /// workers without consulting the originating UI object.
    /// </summary>
    /// <remarks>
    /// The credential is retained only for authenticated requests and equality matching; it
    /// must not be included in diagnostics or exception text.
    /// </remarks>
    internal AzureTileAcquisitionSession(MapStyle style, string token)
    {
        _style = style;
        _token = token;
        _sourceKey = new AzureRasterSourceKey(style, token);
    }

    internal override object SourceKey => _sourceKey;

    internal override RasterSourceKind SourceKind => RasterSourceKind.Azure;

    internal override int TileSize => 256;

    internal override int MinSourceZoom => 0;

    internal override int MaxSourceZoom => GetMaximumTileZoom(_style);

    internal override bool CanAcquire => !string.IsNullOrWhiteSpace(_token);

    internal override bool SupportsAttribution => true;

    internal override int TelemetryStyle => (int)_style;

    /// <summary>
    /// Selects the requested scene zoom, capped at the highest level supported by the style.
    /// </summary>
    internal override int GetSourceZoom(MapScene scene) =>
        Math.Min(scene.TileZoom, MaxSourceZoom);

    /// <summary>
    /// Indicates that Azure base-map coverage is considered available for every valid tile.
    /// </summary>
    internal override bool IncludesTile(TileId id) => true;

    /// <summary>
    /// Downloads and decodes one Azure tile, compositing terrain shading over roads when the
    /// shaded-relief style requires both tilesets.
    /// </summary>
    /// <remarks>
    /// Runs on acquisition workers, performs no UI access, and observes cancellation during
    /// network, decode, and pixel-composition work.
    /// </remarks>
    internal override async Task<DecodedRasterTile> GetTileAsync(
        TileId id,
        CancellationToken cancellationToken)
    {
        DecodedTile tile;
        if (_style == MapStyle.RoadShadedRelief && id.Zoom <= 6)
        {
            int tileSize = GetStyleTileSize(_style, id.Zoom);
            Task<DecodedTile> roadsTask = GetTilePixelsAsync(
                id,
                RoadTileset,
                0,
                MapCamera.MaximumTileZoom,
                tileSize,
                BitmapAlphaMode.Ignore,
                _token,
                cancellationToken);
            Task<DecodedTile> terrainTask = GetTilePixelsAsync(
                id,
                TerrainTileset,
                0,
                6,
                tileSize,
                BitmapAlphaMode.Straight,
                _token,
                cancellationToken);
            await Task.WhenAll(terrainTask, roadsTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            DecodedTile roads = await roadsTask.ConfigureAwait(false);
            DecodedTile terrain = await terrainTask.ConfigureAwait(false);
            if (terrain.Width != roads.Width || terrain.Height != roads.Height)
            {
                throw new InvalidOperationException("Map style layers returned different tile dimensions.");
            }
            long compositeStarted = Stopwatch.GetTimestamp();
            tile = new DecodedTile(
                CompositePixels(roads.Pixels, terrain.Pixels, cancellationToken),
                terrain.Width,
                terrain.Height,
                Math.Max(roads.DownloadMilliseconds, terrain.DownloadMilliseconds),
                Math.Max(roads.DecodeMilliseconds, terrain.DecodeMilliseconds) +
                    Stopwatch.GetElapsedTime(compositeStarted).TotalMilliseconds);
        }
        else
        {
            (string tileset, int minimumZoom, int maximumZoom) = GetPrimaryTileset(_style);
            tile = await GetTilePixelsAsync(
                id,
                tileset,
                minimumZoom,
                maximumZoom,
                GetTileRequestSize(tileset),
                BitmapAlphaMode.Ignore,
                _token,
                cancellationToken).ConfigureAwait(false);
        }

        return new DecodedRasterTile(
            id,
            tile.Pixels,
            tile.Width,
            tile.Height,
            tile.DownloadMilliseconds,
            tile.DecodeMilliseconds);
    }

    /// <summary>
    /// Retrieves, sanitizes, and combines attribution text for every tileset used at a zoom
    /// level.
    /// </summary>
    /// <remarks>
    /// The request credential is sent only in the subscription header and is never included
    /// in the returned attribution or diagnostics.
    /// </remarks>
    internal override async Task<string?> GetAttributionAsync(
        int zoom,
        CancellationToken cancellationToken)
    {
        string[] tilesets = GetTilesetIds(_style, zoom);
        string[] values = await Task.WhenAll(
            tilesets.Select(tileset => GetAttributionForTilesetAsync(
                _token,
                tileset,
                NormalizeTilesetZoom(tileset, zoom),
                cancellationToken))).ConfigureAwait(false);

        return string.Join(
            " ",
            values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal));
    }

    /// <summary>
    /// Returns the Azure tileset identifiers required to render a style at the specified
    /// zoom.
    /// </summary>
    internal static string[] GetTilesetIds(MapStyle style, int zoom)
    {
        if (style == MapStyle.Blank)
        {
            return [];
        }
        if (style == MapStyle.RoadShadedRelief && zoom <= 6)
        {
            return [RoadTileset, TerrainTileset];
        }

        return [GetPrimaryTileset(style).Tileset];
    }

    /// <summary>
    /// Gets the pixel dimension requested from a tileset, accounting for terrain's larger
    /// source tiles.
    /// </summary>
    internal static int GetTileRequestSize(string tileset) =>
        tileset == TerrainTileset ? 512 : 256;

    /// <summary>
    /// Gets the decoded pixel dimension expected for a map style at a zoom level.
    /// </summary>
    internal static int GetStyleTileSize(MapStyle style, int zoom) =>
        style == MapStyle.RoadShadedRelief && zoom <= 6 ? 512 : 256;

    /// <summary>
    /// Gets the highest source zoom available for the selected Azure map style.
    /// </summary>
    internal static int GetMaximumTileZoom(MapStyle style) =>
        style == MapStyle.Satellite ? 19 : MapCamera.MaximumTileZoom;

    /// <summary>
    /// Alpha-composites a straight-alpha BGRA overlay onto an opaque BGRA background.
    /// </summary>
    /// <remarks>
    /// The method periodically checks cancellation during the CPU-bound pixel loop and
    /// always produces an opaque output buffer.
    /// </remarks>
    internal static byte[] CompositePixels(
        byte[] background,
        byte[] overlay,
        CancellationToken cancellationToken = default)
    {
        if (background.Length != overlay.Length || background.Length % 4 != 0)
        {
            throw new ArgumentException("Tile pixel buffers must have matching BGRA dimensions.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] result = new byte[background.Length];
        for (int index = 0; index < result.Length; index += 4)
        {
            if ((index & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            int alpha = overlay[index + 3];
            int inverseAlpha = 255 - alpha;
            result[index] = (byte)(((overlay[index] * alpha) + (background[index] * inverseAlpha) + 127) / 255);
            result[index + 1] = (byte)(((overlay[index + 1] * alpha) + (background[index + 1] * inverseAlpha) + 127) / 255);
            result[index + 2] = (byte)(((overlay[index + 2] * alpha) + (background[index + 2] * inverseAlpha) + 127) / 255);
            result[index + 3] = 255;
        }
        return result;
    }

    /// <summary>
    /// Requests attribution for one tileset and removes service-supplied HTML markup before
    /// exposing the text to the UI.
    /// </summary>
    private async Task<string> GetAttributionForTilesetAsync(
        string token,
        string tileset,
        int zoom,
        CancellationToken cancellationToken)
    {
        const string bounds = "-180,-85.05112878,180,85.05112878";
        string path = $"map/attribution?api-version={ApiVersion}&tilesetId={tileset}&zoom={zoom}&bounds={bounds}";
        using HttpResponseMessage response = await SendAsync(path, token, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"attribution '{tileset}'", cancellationToken).ConfigureAwait(false);

        using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        AttributionResponse? attribution = await JsonSerializer.DeserializeAsync(
            content,
            AzureJsonContext.Default.AttributionResponse,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return string.Join(
            " ",
            attribution?.Copyrights?
                .Select(value => WebUtility.HtmlDecode(HtmlTagRegex().Replace(value, string.Empty)))
                .Where(value => !string.IsNullOrWhiteSpace(value))
            ?? []);
    }

    /// <summary>
    /// Resolves a requested tile to the tileset's supported zoom range and returns decoded
    /// pixels at the requested output size.
    /// </summary>
    private static async Task<DecodedTile> GetTilePixelsAsync(
        TileId requestedId,
        string tileset,
        int minimumZoom,
        int maximumZoom,
        int tileSize,
        BitmapAlphaMode alphaMode,
        string token,
        CancellationToken cancellationToken)
    {
        if (requestedId.Zoom < minimumZoom)
        {
            return await GetZoomedOutTilePixelsAsync(
                requestedId,
                tileset,
                minimumZoom,
                tileSize,
                alphaMode,
                token,
                cancellationToken).ConfigureAwait(false);
        }

        if (requestedId.Zoom > maximumZoom)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedId),
                $"Tile zoom {requestedId.Zoom} exceeds the {tileset} maximum of {maximumZoom}.");
        }

        return await DownloadAndDecodeTileAsync(
            requestedId,
            tileset,
            tileSize,
            alphaMode,
            new BitmapTransform(),
            token,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a tile below a tileset's minimum zoom by downloading, scaling, and stitching
    /// the minimum-zoom descendants that cover it.
    /// </summary>
    /// <remarks>
    /// Descendant downloads run concurrently; cancellation is rechecked while assembling
    /// their pixel buffers.
    /// </remarks>
    private static async Task<DecodedTile> GetZoomedOutTilePixelsAsync(
        TileId requestedId,
        string tileset,
        int minimumZoom,
        int tileSize,
        BitmapAlphaMode alphaMode,
        string token,
        CancellationToken cancellationToken)
    {
        int zoomDifference = minimumZoom - requestedId.Zoom;
        int tilesPerSide = 1 << zoomDifference;
        uint scaledSize = (uint)(tileSize / tilesPerSide);
        Task<DecodedTile>[] tasks = new Task<DecodedTile>[tilesPerSide * tilesPerSide];
        for (int y = 0; y < tilesPerSide; y++)
        {
            for (int x = 0; x < tilesPerSide; x++)
            {
                TileId sourceId = new(
                    minimumZoom,
                    (requestedId.X * tilesPerSide) + x,
                    (requestedId.Y * tilesPerSide) + y);
                BitmapTransform transform = new()
                {
                    ScaledWidth = scaledSize,
                    ScaledHeight = scaledSize,
                    InterpolationMode = BitmapInterpolationMode.Fant,
                };
                tasks[(y * tilesPerSide) + x] = DownloadAndDecodeTileAsync(
                    sourceId,
                    tileset,
                    tileSize,
                    alphaMode,
                    transform,
                    token,
                    cancellationToken);
            }
        }

        DecodedTile[] sourceTiles = await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        long stitchStarted = Stopwatch.GetTimestamp();
        byte[] result = new byte[tileSize * tileSize * 4];
        int sourceStride = (int)scaledSize * 4;
        int destinationStride = tileSize * 4;
        for (int y = 0; y < tilesPerSide; y++)
        {
            for (int x = 0; x < tilesPerSide; x++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] source = sourceTiles[(y * tilesPerSide) + x].Pixels;
                for (int row = 0; row < scaledSize; row++)
                {
                    Buffer.BlockCopy(
                        source,
                        row * sourceStride,
                        result,
                        (((y * (int)scaledSize) + row) * destinationStride) + (x * sourceStride),
                        sourceStride);
                }
            }
        }
        return new DecodedTile(
            result,
            (uint)tileSize,
            (uint)tileSize,
            sourceTiles.Max(tile => tile.DownloadMilliseconds),
            sourceTiles.Max(tile => tile.DecodeMilliseconds) +
                Stopwatch.GetElapsedTime(stitchStarted).TotalMilliseconds);
    }

    /// <summary>
    /// Downloads a bounded encoded tile response and decodes it to validated BGRA pixels
    /// using the supplied image transform and alpha mode.
    /// </summary>
    /// <remarks>
    /// The credential is carried only in the authenticated request header and is not placed
    /// in request paths, errors, or telemetry.
    /// </remarks>
    private static async Task<DecodedTile> DownloadAndDecodeTileAsync(
        TileId id,
        string tileset,
        int tileSize,
        BitmapAlphaMode alphaMode,
        BitmapTransform transform,
        string token,
        CancellationToken cancellationToken)
    {
        long downloadStarted = Stopwatch.GetTimestamp();
        string path = $"map/tile?api-version={ApiVersion}&tilesetId={tileset}&zoom={id.Zoom}&x={id.X}&y={id.Y}&tileSize={tileSize}";
        using HttpResponseMessage response = await SendAsync(path, token, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"tile '{tileset}'", cancellationToken).ConfigureAwait(false);

        int maximumEncodedBytes = checked(
            (tileSize * tileSize * 4) + EncodedTileOverheadAllowance);
        byte[] encodedPixels = await RasterTileHttp.ReadBoundedAsync(
                response.Content,
                maximumEncodedBytes,
                cancellationToken)
            .ConfigureAwait(false);
        double downloadMilliseconds =
            Stopwatch.GetElapsedTime(downloadStarted).TotalMilliseconds;
        long decodeStarted = Stopwatch.GetTimestamp();
        cancellationToken.ThrowIfCancellationRequested();
        using MemoryStream stream = new(encodedPixels, writable: false);
        using Windows.Storage.Streams.IRandomAccessStream randomAccessStream = stream.AsRandomAccessStream();
        BitmapDecoder decoder = await BitmapDecoder
            .CreateAsync(randomAccessStream)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        PixelDataProvider pixelData = await decoder
            .GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                alphaMode,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] pixels = pixelData.DetachPixelData();
        (uint width, uint height) = GetDecodedDimensions(
            decoder.PixelWidth,
            decoder.PixelHeight,
            transform);
        if (!MapRenderer.IsValidPixelBuffer(pixels, width, height))
        {
            throw new InvalidDataException(
                $"Decoded map tile dimensions {width}x{height} do not match its {pixels.Length} BGRA bytes.");
        }
        return new DecodedTile(
            pixels,
            width,
            height,
            downloadMilliseconds,
            Stopwatch.GetElapsedTime(decodeStarted).TotalMilliseconds);
    }

    /// <summary>
    /// Determines the decoder output dimensions from crop bounds, scaling, or the original
    /// bitmap size, in that precedence order.
    /// </summary>
    internal static (uint Width, uint Height) GetDecodedDimensions(
        uint sourceWidth,
        uint sourceHeight,
        BitmapTransform transform)
    {
        if (transform.Bounds.Width > 0 && transform.Bounds.Height > 0)
        {
            return (transform.Bounds.Width, transform.Bounds.Height);
        }

        return (
            transform.ScaledWidth == 0 ? sourceWidth : transform.ScaledWidth,
            transform.ScaledHeight == 0 ? sourceHeight : transform.ScaledHeight);
    }

    /// <summary>
    /// Maps a supported style to its primary tileset and source zoom limits.
    /// </summary>
    private static (string Tileset, int MinimumZoom, int MaximumZoom) GetPrimaryTileset(MapStyle style)
    {
        return style switch
        {
            MapStyle.Road => (RoadTileset, 0, MapCamera.MaximumTileZoom),
            MapStyle.GrayscaleDark => (DarkGreyTileset, 0, MapCamera.MaximumTileZoom),
            MapStyle.Satellite => (ImageryTileset, 1, 19),
            MapStyle.RoadShadedRelief => (RoadTileset, 0, MapCamera.MaximumTileZoom),
            _ => throw new ArgumentOutOfRangeException(nameof(style)),
        };
    }

    /// <summary>
    /// Clamps an attribution zoom to the range accepted by the selected tileset.
    /// </summary>
    private static int NormalizeTilesetZoom(string tileset, int zoom)
    {
        return tileset switch
        {
            ImageryTileset => Math.Clamp(zoom, 1, 19),
            TerrainTileset => Math.Clamp(zoom, 0, 6),
            _ => Math.Clamp(zoom, 0, MapCamera.MaximumTileZoom),
        };
    }

    /// <summary>
    /// Sends an authenticated Azure Maps request while leaving response-content ownership
    /// with the caller.
    /// </summary>
    /// <remarks>
    /// Authentication is applied as a header so the credential never becomes part of a URL.
    /// </remarks>
    private static async Task<HttpResponseMessage> SendAsync(
        string path,
        string token,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = RasterTileHttp.CreateRequest(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("subscription-key", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the process-wide Azure HTTP client with bounded connection lifetime, timeout,
    /// and address-selection behavior.
    /// </summary>
    private static HttpClient CreateHttpClient()
    {
        SocketsHttpHandler handler = new()
        {
            ConnectCallback = ConnectAsync,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri("https://atlas.microsoft.com/"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>
    /// Opens a socket to the first eligible resolved address using short per-address
    /// attempts and transfers ownership of a successful socket to the returned stream.
    /// </summary>
    /// <remarks>
    /// Address selection limits connection attempts by address family, and caller
    /// cancellation terminates the entire connection sequence.
    /// </remarks>
    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            cancellationToken).ConfigureAwait(false);
        IPAddress[] candidates = RasterTileHttp.SelectConnectionAddresses(addresses);
        Exception? lastError = null;

        foreach (IPAddress address in candidates)
        {
            using CancellationTokenSource attemptCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCancellation.CancelAfter(TimeSpan.FromSeconds(1.5));
            Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };

            try
            {
                await socket.ConnectAsync(
                    address,
                    context.DnsEndPoint.Port,
                    attemptCancellation.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = exception;
                socket.Dispose();
            }
            catch (SocketException exception)
            {
                lastError = exception;
                socket.Dispose();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new HttpRequestException(
            $"No address for {context.DnsEndPoint.Host} accepted a connection.",
            lastError);
    }

    /// <summary>
    /// Filters and orders resolved addresses using the shared raster connection policy.
    /// </summary>
    internal static IPAddress[] SelectConnectionAddresses(IEnumerable<IPAddress> addresses) =>
        RasterTileHttp.SelectConnectionAddresses(addresses);

    /// <summary>
    /// Validates an Azure response and, on failure, extracts service error details without
    /// exposing request credentials.
    /// </summary>
    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string resource,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? serviceMessage = null;
        try
        {
            using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            ErrorResponse? error = await JsonSerializer.DeserializeAsync(
                content,
                AzureJsonContext.Default.ErrorResponse,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            serviceMessage = string.Join(
                " ",
                (error?.Error?.Details ?? [])
                    .Select(detail => detail.Message)
                    .Prepend(error?.Error?.Message)
                    .Where(message => !string.IsNullOrWhiteSpace(message))
                    .Distinct(StringComparer.Ordinal));
        }
        catch (JsonException)
        {
        }
        throw new AzureMapsRequestException(
            response.StatusCode,
            $"Azure Maps {resource} request failed with HTTP {(int)response.StatusCode} ({response.StatusCode})." +
            (string.IsNullOrWhiteSpace(serviceMessage) ? string.Empty : $" {serviceMessage}"));
    }

    /// <summary>
    /// Creates the generated regular expression used to remove HTML tags from attribution
    /// strings.
    /// </summary>
    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    /// <summary>
    /// Models the Azure attribution response field consumed before HTML removal and UI
    /// dispatch.
    /// </summary>
    private sealed class AttributionResponse
    {
        [JsonPropertyName("copyrights")]
        public string[]? Copyrights { get; set; }
    }

    /// <summary>
    /// Models the top-level Azure service error response used to construct a request
    /// exception.
    /// </summary>
    private sealed class ErrorResponse
    {
        [JsonPropertyName("error")]
        public ErrorDetail? Error { get; set; }
    }

    /// <summary>
    /// Models a service error message and its nested details for failure interpretation.
    /// </summary>
    /// <remarks>Service text is not forwarded to ETW.</remarks>
    private sealed class ErrorDetail
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("details")]
        public ErrorDetail[]? Details { get; set; }
    }

    [JsonSerializable(typeof(AttributionResponse))]
    [JsonSerializable(typeof(ErrorResponse))]
    private sealed partial class AzureJsonContext : JsonSerializerContext;

    /// <summary>
    /// Combines style and credential for private in-process equality and generation
    /// invalidation.
    /// </summary>
    /// <remarks>
    /// The credential-bearing key must never be logged. Its string representation is
    /// intentionally reduced to the type name.
    /// </remarks>
    private sealed record AzureRasterSourceKey(MapStyle Style, string Token)
    {
        /// <summary>
        /// Returns a non-sensitive diagnostic name without revealing the credential contained
        /// by this equality key.
        /// </summary>
        public override string ToString() => nameof(AzureRasterSourceKey);
    }

    /// <summary>
    /// Carries a transient decoded BGRA buffer and dimensions between Azure decode,
    /// stitching, and composition stages.
    /// </summary>
    private readonly record struct DecodedTile(
        byte[] Pixels,
        uint Width,
        uint Height,
        double DownloadMilliseconds,
        double DecodeMilliseconds);
}

/// <summary>
/// Represents an Azure Maps HTTP failure with the status code needed for sanitized scheduler
/// diagnostics.
/// </summary>
/// <remarks>
/// The exception message may include service-provided error detail for local failure
/// handling, but manager ETW paths emit only status, failure category, and exception type.
/// </remarks>
internal sealed class AzureMapsRequestException : Exception
{
    /// <summary>
    /// Initializes an Azure Maps request failure with its HTTP status and sanitized message.
    /// </summary>
    public AzureMapsRequestException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }
}
