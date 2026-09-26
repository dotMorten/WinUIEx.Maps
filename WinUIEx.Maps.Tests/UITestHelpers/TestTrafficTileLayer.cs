using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests.UITestHelpers;

internal sealed class TestTrafficTileLayer(
    long sourceId, TileId tileId, byte[] encoded, TrafficFlowStyle? flowStyle = null) : TileLayer
{
    private readonly Session _session = new(tileId, encoded, flowStyle);

    internal override TileLayerSnapshot CreateSnapshot() =>
        new(sourceId, Revision, _session, MinZoom, MaxZoom, IsVisible, Opacity, TimeSpan.Zero);

    private sealed class Session(TileId tileId, byte[] encoded, TrafficFlowStyle? flowStyle)
        : RasterTileAcquisitionSession
    {
        internal override object SourceKey => this;
        internal override RasterSourceKind SourceKind => RasterSourceKind.Custom;
        internal override double LineCompositeOpacity => AzureTrafficLayer.RoadLineOpacity;
        internal override LayerRenderKind RenderKind => LayerRenderKind.VectorPoints;
        internal override int TileSize => 256;
        internal override int MinSourceZoom => tileId.Zoom;
        internal override int MaxSourceZoom => tileId.Zoom;
        internal override bool CanAcquire => true;
        internal override bool IncludesTile(TileId id) => id == tileId;
        internal override int GetSourceZoom(MapScene scene) => tileId.Zoom;
        internal override Task<DecodedRasterTile> GetTileAsync(TileId id, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Traffic fixtures require vector acquisition.");
        internal override Task<DecodedVectorTile> GetVectorTileAsync(TileId id, CancellationToken cancellationToken) =>
            AzureOverlayAcquisitionSession.DecodeTrafficTileAsync(id, encoded, flowStyle,
                cancellationToken: cancellationToken);
    }
}
