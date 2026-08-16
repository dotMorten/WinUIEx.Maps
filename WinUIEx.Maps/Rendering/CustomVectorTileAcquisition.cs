using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinUIEx.Maps.Rendering.Diagnostics;

namespace WinUIEx.Maps.Rendering;

internal sealed class CustomVectorTileAcquisitionSession :
    RasterTileAcquisitionSession
{
    private const int MaximumEncodedVectorTileBytes = 4 * 1024 * 1024;
    private readonly CustomRasterTileAcquisitionSession _tileTemplate;
    private readonly CustomRequestHeaders _requestHeaders;
    private readonly CustomVectorStyleProvider _styleProvider;
    private readonly CustomVectorSourceKey _sourceKey;

    internal CustomVectorTileAcquisitionSession(
        string tileUrl,
        string styleUrl,
        TileLayerBounds bounds,
        bool isTms,
        int maximumSourceZoom,
        int minimumSourceZoom,
        IEnumerable<string> subdomains,
        int tileSize,
        IReadOnlyDictionary<string, string> requestHeaders)
    {
        _tileTemplate = new CustomRasterTileAcquisitionSession(
            tileUrl,
            bounds,
            isTms,
            maximumSourceZoom,
            minimumSourceZoom,
            subdomains,
            tileSize,
            requestHeaders);
        _requestHeaders = new CustomRequestHeaders(
            requestHeaders,
            tileUrl,
            styleUrl);
        _styleProvider = new CustomVectorStyleProvider(
            styleUrl,
            _requestHeaders);
        _sourceKey = new CustomVectorSourceKey(
            tileUrl,
            styleUrl,
            bounds,
            isTms,
            maximumSourceZoom,
            minimumSourceZoom,
            string.Join("\u001f", subdomains),
            tileSize,
            _requestHeaders.Fingerprint);
    }

    internal override object SourceKey => _sourceKey;

    internal override RasterSourceKind SourceKind => RasterSourceKind.Custom;

    internal override LayerRenderKind RenderKind => LayerRenderKind.VectorPoints;

    internal override int TileSize => _tileTemplate.TileSize;

    internal override int MinSourceZoom => _tileTemplate.MinSourceZoom;

    internal override int MaxSourceZoom => _tileTemplate.MaxSourceZoom;

    internal override bool CanAcquire => _tileTemplate.CanAcquire;

    internal bool IsTms => _tileTemplate.IsTms;

    internal override int GetSourceZoom(MapScene scene) =>
        _tileTemplate.GetSourceZoom(scene);

    internal override bool IncludesTile(TileId id) =>
        _tileTemplate.IncludesTile(id);

    internal override Task<DecodedRasterTile> GetTileAsync(
        TileId id,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "Vector tile sources must use vector acquisition.");

    internal override async Task<DecodedVectorTile> GetVectorTileAsync(
        TileId id,
        CancellationToken cancellationToken)
    {
        long downloadStarted = Stopwatch.GetTimestamp();
        Task<VectorStyleAssets> styleAssetsTask =
            _styleProvider.GetAssetsAsync(cancellationToken);
        string requestUrl = _tileTemplate.ExpandUrl(id);
        byte[] encoded = await CustomTileHttp.GetBytesAsync(
                new Uri(requestUrl),
                "application/vnd.mapbox-vector-tile, application/x-protobuf",
                MaximumEncodedVectorTileBytes,
                _requestHeaders,
                cancellationToken)
            .ConfigureAwait(false);
        VectorStyleAssets styleAssets =
            await styleAssetsTask.ConfigureAwait(false);
        double downloadMilliseconds =
            Stopwatch.GetElapsedTime(downloadStarted).TotalMilliseconds;
        long decodeStarted = Stopwatch.GetTimestamp();
        VectorTileFeatureCollection features = VectorTileDecoder.Decode(
            encoded,
            cancellationToken);
        VectorSpriteTextureData[] spriteTextures =
            await styleAssets.PrepareTexturesAsync(
                    features,
                    id.Zoom,
                    cancellationToken)
                .ConfigureAwait(false);
        return new DecodedVectorTile(
            id,
            features,
            styleAssets,
            spriteTextures,
            Background: null,
            downloadMilliseconds,
            Stopwatch.GetElapsedTime(decodeStarted).TotalMilliseconds);
    }

    private sealed record CustomVectorSourceKey(
        string TileUrl,
        string StyleUrl,
        TileLayerBounds Bounds,
        bool IsTms,
        int MaximumSourceZoom,
        int MinimumSourceZoom,
        string Subdomains,
        int TileSize,
        string RequestHeadersFingerprint)
    {
        public override string ToString() => nameof(CustomVectorSourceKey);
    }
}

internal sealed class CustomVectorStyleProvider
{
    private const int MaximumStyleBytes = 4 * 1024 * 1024;
    private const int MaximumSpriteIndexBytes = 4 * 1024 * 1024;
    private const int MaximumEncodedSpriteBytes = 16 * 1024 * 1024;
    private readonly object _sync = new();
    private readonly Uri _styleUri;
    private readonly string _styleIdentity;
    private readonly CustomRequestHeaders _requestHeaders;
    private Task<VectorStyleAssets>? _assetsTask;

    internal CustomVectorStyleProvider(
        string styleUrl,
        CustomRequestHeaders requestHeaders)
    {
        _styleUri = new Uri(styleUrl, UriKind.Absolute);
        _requestHeaders = requestHeaders;
        _styleIdentity = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{styleUrl}\u001f{requestHeaders.Fingerprint}")));
    }

    internal async Task<VectorStyleAssets> GetAssetsAsync(
        CancellationToken cancellationToken)
    {
        Task<VectorStyleAssets> task;
        lock (_sync)
        {
            if (_assetsTask is { IsCompleted: true, IsCompletedSuccessfully: false })
            {
                _assetsTask = null;
            }
            task = _assetsTask ??= LoadAsync(CancellationToken.None);
        }

        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (task.IsCompleted && !task.IsCompletedSuccessfully)
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_assetsTask, task))
                    {
                        _assetsTask = null;
                    }
                }
            }
        }
    }

    internal static CustomVectorStyleResourceUrls GetResourceUrls(
        ReadOnlyMemory<byte> styleJson,
        Uri styleUri)
    {
        using JsonDocument document = JsonDocument.Parse(
            styleJson,
            new JsonDocumentOptions { MaxDepth = 64 });
        JsonElement root = document.RootElement;
        string? sprite = TryGetOptionalString(root, "sprite");
        string? glyphs = TryGetOptionalString(root, "glyphs");
        Uri resourceBaseUri = GetResourceBaseUri(styleUri);
        return new CustomVectorStyleResourceUrls(
            sprite is null ? null : new Uri(resourceBaseUri, sprite),
            glyphs is null
                ? null
                : ResolveTemplateUrl(resourceBaseUri, glyphs));
    }

    private static Uri GetResourceBaseUri(Uri styleUri)
    {
        string lastSegment = styleUri.Segments[^1];
        if (styleUri.AbsolutePath.EndsWith("/", StringComparison.Ordinal) ||
            lastSegment.Contains('.', StringComparison.Ordinal))
        {
            return styleUri;
        }

        UriBuilder builder = new(styleUri)
        {
            Path = styleUri.AbsolutePath + "/",
        };
        return builder.Uri;
    }

    private async Task<VectorStyleAssets> LoadAsync(
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        byte[] styleJson = await CustomTileHttp.GetBytesAsync(
                _styleUri,
                "application/json",
                MaximumStyleBytes,
                _requestHeaders,
                cancellationToken)
            .ConfigureAwait(false);
        CustomVectorStyleResourceUrls resources =
            GetResourceUrls(styleJson, _styleUri);
        VectorStyle style = VectorStyle.ParseCustom(styleJson);
        VectorSpriteAtlas spriteAtlas;
        int spriteWidth;
        int spriteHeight;
        if (resources.SpriteBaseUri is null)
        {
            spriteAtlas = new VectorSpriteAtlas(
                _styleIdentity,
                new Dictionary<string, VectorSpriteEntry>(),
                [0, 0, 0, 0],
                1,
                1);
            spriteWidth = 1;
            spriteHeight = 1;
        }
        else
        {
            Uri spriteIndexUri = AppendPathSuffix(
                resources.SpriteBaseUri,
                ".json");
            Uri spriteImageUri = AppendPathSuffix(
                resources.SpriteBaseUri,
                ".png");
            Task<byte[]> spriteIndexTask = CustomTileHttp.GetBytesAsync(
                spriteIndexUri,
                "application/json",
                MaximumSpriteIndexBytes,
                _requestHeaders,
                cancellationToken);
            Task<byte[]> spriteImageTask = CustomTileHttp.GetBytesAsync(
                spriteImageUri,
                "image/png",
                MaximumEncodedSpriteBytes,
                _requestHeaders,
                cancellationToken);
            await Task.WhenAll(spriteIndexTask, spriteImageTask)
                .ConfigureAwait(false);
            Dictionary<string, VectorSpriteEntry> entries =
                VectorSpriteAtlas.ParseIndex(await spriteIndexTask.ConfigureAwait(false));
            AzureVectorStyleProvider.DecodedSpriteImage decoded =
                await AzureVectorStyleProvider.DecodeSpriteAsync(
                        await spriteImageTask.ConfigureAwait(false),
                        cancellationToken)
                    .ConfigureAwait(false);
            spriteAtlas = new VectorSpriteAtlas(
                _styleIdentity,
                entries,
                decoded.Pixels,
                decoded.Width,
                decoded.Height);
            spriteWidth = checked((int)decoded.Width);
            spriteHeight = checked((int)decoded.Height);
        }

        IVectorGlyphProvider? glyphProvider =
            resources.GlyphTemplate is null
                ? null
                : new CustomVectorGlyphProvider(
                    resources.GlyphTemplate,
                    _requestHeaders);
        VectorStyleAssets assets = new(
            _styleIdentity,
            style,
            spriteAtlas,
            new VectorGlyphAtlas(_styleIdentity, glyphProvider));
        MapControlEventSource.Log.VectorStyleAssetsLoaded(
            -1,
            style.LayerCount,
            style.UnsupportedLayerCount,
            spriteAtlas.EntryCount,
            spriteWidth,
            spriteHeight,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        VectorStyleCompatibility.Report(-1, styleJson);
        return assets;
    }

    internal static Uri AppendPathSuffix(Uri uri, string suffix)
    {
        UriBuilder builder = new(uri)
        {
            Path = uri.AbsolutePath + suffix,
        };
        return builder.Uri;
    }

    private static string? TryGetOptionalString(
        JsonElement owner,
        string propertyName)
    {
        if (!owner.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                "The vector style contains an invalid resource URL.");
        }
        return value.GetString();
    }

    private static string ResolveTemplateUrl(Uri baseUri, string template)
    {
        const string FontMarker = "__WINUIEX_FONTSTACK__";
        const string RangeMarker = "__WINUIEX_RANGE__";
        string marked = template
            .Replace("{fontstack}", FontMarker, StringComparison.OrdinalIgnoreCase)
            .Replace("{range}", RangeMarker, StringComparison.OrdinalIgnoreCase);
        string resolved = new Uri(baseUri, marked).AbsoluteUri;
        return resolved
            .Replace(FontMarker, "{fontstack}", StringComparison.Ordinal)
            .Replace(RangeMarker, "{range}", StringComparison.Ordinal);
    }
}

internal sealed class CustomVectorGlyphProvider : IVectorGlyphProvider
{
    private const int MaximumGlyphRangeBytes = 2 * 1024 * 1024;
    private const int MaximumCachedRanges = 512;
    private readonly object _sync = new();
    private readonly string _glyphTemplate;
    private readonly CustomRequestHeaders _requestHeaders;
    private readonly Dictionary<VectorGlyphRangeKey, Task<VectorGlyphRange>> _ranges = [];

    internal CustomVectorGlyphProvider(
        string glyphTemplate,
        CustomRequestHeaders requestHeaders)
    {
        _glyphTemplate = glyphTemplate;
        _requestHeaders = requestHeaders;
    }

    public async Task<VectorGlyphRange> GetRangeAsync(
        string fontStack,
        int rangeStart,
        CancellationToken cancellationToken)
    {
        VectorGlyphRangeKey key = new(fontStack, rangeStart);
        Task<VectorGlyphRange> task;
        lock (_sync)
        {
            if (!_ranges.TryGetValue(key, out task!))
            {
                if (_ranges.Count >= MaximumCachedRanges)
                {
                    throw new InvalidDataException(
                        "The vector glyph range cache exceeds its supported limit.");
                }
                task = LoadRangeAsync(key, CancellationToken.None);
                _ranges.Add(key, task);
            }
        }

        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (task.IsCompleted && !task.IsCompletedSuccessfully)
            {
                lock (_sync)
                {
                    if (_ranges.TryGetValue(
                            key,
                            out Task<VectorGlyphRange>? current) &&
                        ReferenceEquals(current, task))
                    {
                        _ranges.Remove(key);
                    }
                }
            }
        }
    }

    private async Task<VectorGlyphRange> LoadRangeAsync(
        VectorGlyphRangeKey key,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        string requestUrl = _glyphTemplate
            .Replace(
                "{fontstack}",
                Uri.EscapeDataString(key.FontStack),
                StringComparison.OrdinalIgnoreCase)
            .Replace(
                "{range}",
                $"{key.RangeStart}-{key.RangeStart + 255}",
                StringComparison.OrdinalIgnoreCase);
        byte[] encoded;
        try
        {
            encoded = await CustomTileHttp.GetBytesAsync(
                    new Uri(requestUrl),
                    "application/x-protobuf",
                    MaximumGlyphRangeBytes,
                    _requestHeaders,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode is
                System.Net.HttpStatusCode.BadRequest or
                System.Net.HttpStatusCode.NotFound)
        {
            MapControlEventSource.Log.VectorGlyphRangeUnavailable(
                -1,
                key.RangeStart,
                (int)exception.StatusCode,
                exception.GetType().Name,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new VectorGlyphRange(
                key.FontStack,
                key.RangeStart,
                new Dictionary<int, VectorGlyph>());
        }

        VectorGlyphRange range = VectorGlyphRangeDecoder.Decode(
            encoded,
            key.FontStack,
            key.RangeStart,
            cancellationToken);
        MapControlEventSource.Log.VectorGlyphRangeLoaded(
            -1,
            range.Glyphs.Count,
            encoded.Length,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return range;
    }
}

internal readonly record struct CustomVectorStyleResourceUrls(
    Uri? SpriteBaseUri,
    string? GlyphTemplate);
