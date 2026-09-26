using System.Diagnostics;
using System.Text.Json;
using Windows.Graphics.Imaging;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Immutable live-overlay state consumed by the existing bounded tile scheduler.
/// Refresh identity is local only; stable service URLs revalidate the HTTP cache.
/// </summary>
/// <remarks>
/// Tilesets and weather limits: https://learn.microsoft.com/rest/api/maps/render/get-map-tile.
/// Traffic's 0–22 range is documented by the predecessor Get Traffic Flow/Incident Tile
/// APIs and used by SDK 3; the current Render reference does not specify traffic limits.
/// </remarks>
internal sealed class AzureOverlayAcquisitionSession : RasterTileAcquisitionSession
{
    internal const int IncidentSymbolSize = TrafficIncidentIcons.Size;
    internal const double IncidentLineWidth = 4;
    private const int MaximumEncodedVectorTileBytes = 4 * 1024 * 1024;
    private const string IncidentTileset = "microsoft.traffic.incident";
    private readonly OverlaySourceKey _sourceKey;
    private readonly OverlaySourceKey? _refreshIdentity;

    internal AzureOverlayAcquisitionSession(
        string tileset,
        string token,
        string? language,
        DateTimeOffset? timestamp,
        long refreshVersion = 0,
        bool incidents = false,
        TrafficFlowStyle? flowStyle = null)
    {
        bool weather = tileset is "microsoft.weather.radar.main" or "microsoft.weather.infrared.main";
        bool traffic = tileset is "microsoft.traffic.absolute" or
            "microsoft.traffic.relative" or "microsoft.traffic.delay";
        if ((!weather && !traffic && tileset != IncidentTileset) ||
            incidents != (tileset == IncidentTileset))
            throw new ArgumentException("Unsupported Azure overlay tileset or acquisition mode.", nameof(tileset));
        if (timestamp.HasValue && !weather)
            throw new ArgumentException("Only weather tilesets support timestamps.", nameof(timestamp));
        flowStyle ??= tileset switch
        {
            "microsoft.traffic.absolute" => TrafficFlowStyle.Absolute,
            "microsoft.traffic.relative" => TrafficFlowStyle.Relative,
            "microsoft.traffic.delay" => TrafficFlowStyle.Delay,
            _ => null,
        };
        if (flowStyle is { } style && (!traffic || AzureTrafficLayer.GetTileset(style) != tileset))
            throw new ArgumentException("The flow style does not match the vector tileset.", nameof(flowStyle));
        ArgumentNullException.ThrowIfNull(token);
        _sourceKey = new(
            tileset, token,
            AzureTileAcquisitionSession.GetRequestLanguage(language)?.ToLowerInvariant(),
            NormalizeTimestamp(tileset, timestamp), refreshVersion, incidents, flowStyle);
        _refreshIdentity = timestamp is null ? _sourceKey with { RefreshVersion = 0 } : null;
        MaxSourceZoom = weather ? 15 : 22;
    }

    internal static DateTimeOffset? NormalizeTimestamp(string tileset, DateTimeOffset? timestamp)
    {
        if (timestamp is not DateTimeOffset value)
            return null;
        long interval = tileset switch
        {
            "microsoft.weather.radar.main" => TimeSpan.TicksPerMinute * 5,
            "microsoft.weather.infrared.main" => TimeSpan.TicksPerMinute * 10,
            _ => throw new ArgumentException("Only weather tilesets support timestamps.", nameof(tileset)),
        };
        long rounded = ((value.UtcTicks + interval / 2) / interval) * interval;
        rounded = Math.Min(rounded, DateTimeOffset.MaxValue.UtcTicks / interval * interval);
        return new DateTimeOffset(rounded, TimeSpan.Zero);
    }

    internal override object SourceKey => _sourceKey;
    internal override object? RefreshIdentity => _refreshIdentity;
    internal override RasterSourceKind SourceKind => RasterSourceKind.Azure;
    internal override double LineCompositeOpacity =>
        _sourceKey.Incidents || _sourceKey.FlowStyle is not null ? AzureTrafficLayer.RoadLineOpacity : 1;
    internal override LayerRenderKind RenderKind =>
        _sourceKey.Incidents || _sourceKey.FlowStyle is not null
            ? LayerRenderKind.VectorPoints : LayerRenderKind.RasterTiles;
    internal override int TileSize => 256;
    internal override int MinSourceZoom => 0;
    internal override int MaxSourceZoom { get; }
    internal override bool CanAcquire => !string.IsNullOrWhiteSpace(_sourceKey.Token);
    internal override bool SupportsAttribution => true;
    internal override int GetSourceZoom(MapScene scene) => Math.Clamp(scene.TileZoom, 0, MaxSourceZoom);

    internal override bool IncludesTile(TileId id) =>
        id.Zoom >= 0 && id.Zoom <= MaxSourceZoom &&
        id.X >= 0 && id.Y >= 0 && id.X < (1 << id.Zoom) && id.Y < (1 << id.Zoom);

    internal string GetTileRequestPath(TileId id)
    {
        ValidateTile(id);
        return AzureTileAcquisitionSession.BuildTileRequestPath(
            id, _sourceKey.Tileset, _sourceKey.Language,
            RenderKind == LayerRenderKind.VectorPoints ? null : TileSize, _sourceKey.Timestamp);
    }

    internal override async Task<DecodedRasterTile> GetTileAsync(
        TileId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RenderKind == LayerRenderKind.VectorPoints)
            throw new InvalidOperationException("Traffic tiles require vector acquisition.");
        ValidateTile(id);
        AzureTileAcquisitionSession.DecodedTile tile =
            await AzureTileAcquisitionSession.DownloadAndDecodeTileAsync(
                id, _sourceKey.Tileset, TileSize, BitmapAlphaMode.Straight,
                new BitmapTransform(), _sourceKey.Token, _sourceKey.Language,
                cancellationToken, _sourceKey.Timestamp, revalidate: true,
                requireExactDimensions: true).ConfigureAwait(false);
        return new(id, tile.Pixels, tile.Width, tile.Height,
            tile.DownloadMilliseconds, tile.DecodeMilliseconds);
    }

    internal override async Task<DecodedVectorTile> GetVectorTileAsync(
        TileId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RenderKind != LayerRenderKind.VectorPoints)
            throw new InvalidOperationException("Weather tiles require raster acquisition.");
        long started = Stopwatch.GetTimestamp();
        using PooledByteBuffer encoded = await AzureTileAcquisitionSession.GetTileBytesAsync(
            GetTileRequestPath(id), _sourceKey.Token, "application/vnd.mapbox-vector-tile",
            MaximumEncodedVectorTileBytes, cancellationToken, revalidate: true).ConfigureAwait(false);
        return await DecodeTrafficTileAsync(id, encoded.Memory, _sourceKey.FlowStyle,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, cancellationToken).ConfigureAwait(false);
    }

    internal static Task<DecodedVectorTile> DecodeIncidentTileAsync(
        TileId id,
        ReadOnlyMemory<byte> encoded,
        double downloadMilliseconds = 0,
        CancellationToken cancellationToken = default) =>
        DecodeTrafficTileAsync(id, encoded, null, downloadMilliseconds, cancellationToken);

    internal static async Task<DecodedVectorTile> DecodeTrafficTileAsync(
        TileId id, ReadOnlyMemory<byte> encoded, TrafficFlowStyle? flowStyle,
        double downloadMilliseconds = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (encoded.Length > MaximumEncodedVectorTileBytes)
            throw new InvalidDataException("The traffic tile exceeds the encoded size limit.");
        long started = Stopwatch.GetTimestamp();
        VectorTileFeatureCollection features = VectorTileDecoder.Decode(encoded.Span, cancellationToken);
        VectorStyleAssets assets = flowStyle is { } style
            ? CreateFlowStyle(features, style, cancellationToken)
            : CreateIncidentStyle(features, cancellationToken);
        VectorSpriteTextureData[] textures = await assets.PrepareTexturesAsync(
            features, id.Zoom, cancellationToken).ConfigureAwait(false);
        return new(id, features, assets, textures, null, downloadMilliseconds,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    internal override async Task<string?> GetAttributionAsync(
        int zoom, CancellationToken cancellationToken) =>
        await AzureTileAcquisitionSession.GetAttributionForTilesetAsync(
            _sourceKey.Token, _sourceKey.Tileset, Math.Clamp(zoom, 0, MaxSourceZoom),
            cancellationToken).ConfigureAwait(false);

    private void ValidateTile(TileId id)
    {
        if (!IncludesTile(id))
            throw new ArgumentOutOfRangeException(nameof(id));
    }

    private static VectorStyleAssets CreateFlowStyle(
        VectorTileFeatureCollection features, TrafficFlowStyle style, CancellationToken cancellationToken)
    {
        // Azure's vector-flow sample uses traffic_level: relative fractions or absolute km/h.
        // Dark and reduced modes style relative vectors locally; neither has a vector tileset.
        string colors = style switch
        {
            TrafficFlowStyle.Absolute =>
                """["step",["get","traffic_level"],"#8b1e2d",1,"#d73027",20,"#ef7d22",40,"#e6c229",60,"#27864a"]""",
            TrafficFlowStyle.RelativeDark =>
                """["step",["get","traffic_level"],"#ff647c",0.01,"#ff7a7a",0.6,"#ffac60",0.85,"#ffe066",1,"#65d69a"]""",
            TrafficFlowStyle.Reduced =>
                """["step",["get","traffic_level"],"#8b1e2d",0.01,"#d73027",0.2,"#ef7d22",0.4,"#e6c229",0.6,"#27864a"]""",
            _ =>
                """["step",["get","traffic_level"],"#8b1e2d",0.01,"#d73027",0.6,"#ef7d22",0.85,"#e6c229",1,"#27864a"]""",
        };
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 8);
            writer.WriteStartArray("layers");
            foreach (string layer in features.Features.Select(f => f.SourceLayer).Distinct(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteStartObject();
                writer.WriteString("type", "line");
                writer.WriteString("source-layer", layer);
                if (style == TrafficFlowStyle.Delay)
                {
                    writer.WritePropertyName("filter");
                    writer.WriteRawValue("""
                        ["case",["==",["get","road_closure"],true],true,
                        ["has","traffic_level"],["step",["get","traffic_level"],true,1,false],false]
                        """);
                }
                writer.WriteStartObject("layout");
                writer.WriteString("line-cap", "round");
                writer.WriteString("line-join", "round");
                writer.WriteEndObject();
                writer.WriteStartObject("paint");
                writer.WritePropertyName("line-color");
                writer.WriteRawValue($$"""
                    ["case",["==",["get","road_closure"],true],"#8b1e2d",
                    ["has","traffic_level"],{{colors}},"#808080"]
                    """);
                writer.WritePropertyName("line-width");
                writer.WriteRawValue("""["interpolate",["linear"],["zoom"],5,1.5,12,3,18,6]""");
                writer.WritePropertyName("line-offset");
                writer.WriteRawValue("""
                    ["case",["==",["get","traffic_road_coverage"],"one_side"],
                    ["*",["case",["==",["get","left_hand_traffic"],true],-1,1],
                    ["interpolate",["linear"],["zoom"],5,0.75,12,1.5,18,3]],0]
                    """);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        string identity = $"azure-traffic-flow-{style}";
        return new VectorStyleAssets(identity, VectorStyle.ParseCustom(stream.ToArray()),
            new VectorSpriteAtlas(identity, VectorSpriteAtlas.ParseIndex("{}"u8.ToArray()),
                [0, 0, 0, 0], 1, 1), new VectorGlyphAtlas(identity, provider: null));
    }

    private static VectorStyleAssets CreateIncidentStyle(
        VectorTileFeatureCollection features, CancellationToken cancellationToken)
    {
        // Render API documents the tileset, not its MVT property/source-layer schema.
        // https://atlas.microsoft.com/sdk/javascript/mapcontrol/3/atlas.min.js
        // uses the legacy incident source "Traffic incident POI"; do not assume that
        // name or the separate GeoJSON incidentType schema for the current Render API.
        // Style actual MVT layers and preserve every original property for hit testing.
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 8);
            writer.WriteStartArray("layers");
            foreach (string layer in features.Features.Select(feature => feature.SourceLayer).Distinct(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteStartObject();
                writer.WriteString("type", "line");
                writer.WriteString("source-layer", layer);
                writer.WriteStartObject("paint");
                writer.WriteString("line-color", "#e87900");
                writer.WriteNumber("line-width", IncidentLineWidth);
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteStartObject();
                writer.WriteString("type", "symbol");
                writer.WriteString("source-layer", layer);
                writer.WriteStartArray("filter");
                writer.WriteStringValue("==");
                writer.WriteStringValue("$type");
                writer.WriteStringValue("Point");
                writer.WriteEndArray();
                writer.WriteStartObject("layout");
                writer.WritePropertyName("icon-image");
                TrafficIncidentIcons.WriteImageExpression(writer);
                writer.WriteBoolean("icon-allow-overlap", true);
                writer.WriteBoolean("icon-ignore-placement", true);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        const string identity = TrafficIncidentIcons.Identity;
        return new VectorStyleAssets(identity, VectorStyle.ParseCustom(stream.ToArray()),
            TrafficIncidentIcons.Atlas,
            new VectorGlyphAtlas(identity, provider: null));
    }

    private sealed record OverlaySourceKey(
        string Tileset, string Token, string? Language, DateTimeOffset? Timestamp,
        long RefreshVersion, bool Incidents, TrafficFlowStyle? FlowStyle)
    {
        public override string ToString() => nameof(OverlaySourceKey);
    }
}
