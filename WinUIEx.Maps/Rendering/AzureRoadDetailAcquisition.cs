using System.Diagnostics;
using Windows.Graphics.Imaging;

namespace WinUIEx.Maps.Rendering;

/// <summary>Road's embedded raster source, scheduled by the shared tile manager.</summary>
internal sealed class AzureRoadDetailAcquisitionSession : RasterTileAcquisitionSession
{
    internal const string Tileset = "microsoft.core.raster.roaddetail";
    private readonly string _token;
    private readonly string? _language;
    private readonly RoadDetailSourceKey _sourceKey;

    internal AzureRoadDetailAcquisitionSession(string token, string? language)
    {
        _token = token;
        _language = AzureTileAcquisitionSession.GetRequestLanguage(language);
        _sourceKey = new(token, _language?.ToUpperInvariant());
    }

    internal override object SourceKey => _sourceKey;
    internal override RasterSourceKind SourceKind => RasterSourceKind.Azure;
    internal override int TelemetryStyle => (int)MapStyle.Road;
    internal override int TileSize => 256;
    internal override int MinSourceZoom => 5;
    internal override int MaxSourceZoom => 13;
    internal override bool CanAcquire => !string.IsNullOrWhiteSpace(_token);
    // This raster source uses 256-pixel tiles and rounds source zoom. Its tile
    // zoom is one above the 512-pixel vector/style zoom at the same map scale.
    internal override int GetSourceZoom(MapScene scene) => Math.Clamp(
        (int)Math.Floor(scene.Zoom + Math.Log2(256d / TileSize) + .5),
        MinSourceZoom, MaxSourceZoom);
    internal override bool IncludesTile(TileId id) =>
        id.Zoom >= MinSourceZoom && id.Zoom <= MaxSourceZoom;

    internal string BuildTileRequestPath(TileId id) =>
        AzureTileAcquisitionSession.BuildTileRequestPath(
            id, Tileset, _language, tileSize: TileSize, apiVersion: "2.1") + "&cstl=vb";

    internal override async Task<DecodedRasterTile> GetTileAsync(
        TileId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        using PooledByteBuffer encoded = await AzureTileAcquisitionSession.GetTileBytesAsync(
            BuildTileRequestPath(id), _token, "image/png", 2 * 1024 * 1024,
            cancellationToken).ConfigureAwait(false);
        var tile = await AzureTileAcquisitionSession.DecodeTilePixelsAsync(
            encoded, TileSize, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            cancellationToken).ConfigureAwait(false);
        return new(id, tile.Pixels, tile.Width, tile.Height,
            tile.DownloadMilliseconds, tile.DecodeMilliseconds);
    }

    private sealed record RoadDetailSourceKey(string Token, string? Language);
}
