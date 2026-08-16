using System.Text;
using Windows.Devices.Geolocation;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests.UITestHelpers;

internal sealed class TestVectorTileSource
{
    private readonly byte[] _tile;

    internal TestVectorTileSource(
        TileId tileId,
        byte[] tile,
        VectorStyleAssets styleAssets)
    {
        ArgumentNullException.ThrowIfNull(tile);
        ArgumentNullException.ThrowIfNull(styleAssets);
        TileId = tileId;
        _tile = tile.ToArray();
        StyleAssets = styleAssets;
    }

    internal TileId TileId { get; }

    internal VectorStyleAssets StyleAssets { get; }

    internal BasicGeoposition TileCenter
    {
        get
        {
            double scale = Math.Pow(2, TileId.Zoom);
            double worldX = (TileId.X + 0.5) / scale;
            double worldY = (TileId.Y + 0.5) / scale;
            return new BasicGeoposition
            {
                Longitude = (worldX * 360) - 180,
                Latitude = Math.Atan(Math.Sinh(
                    Math.PI * (1 - (2 * worldY)))) * 180 / Math.PI,
            };
        }
    }

    internal static TestVectorTileSource Create(
        TileId tileId,
        byte[] tile,
        string styleJson,
        string spriteJson,
        byte[] spritePixels,
        uint spriteWidth,
        uint spriteHeight) =>
        new(
            tileId,
            tile,
            VectorStyleAssets.CreateForTest(
                MapStyle.Road,
                Encoding.UTF8.GetBytes(styleJson),
                Encoding.UTF8.GetBytes(spriteJson),
                spritePixels,
                spriteWidth,
                spriteHeight));

    internal void AddGlyphs(
        string fontStack,
        params TestGlyph[] glyphs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fontStack);
        ArgumentNullException.ThrowIfNull(glyphs);
        foreach (IGrouping<int, TestGlyph> range in glyphs.GroupBy(
            glyph => glyph.Character / 256 * 256))
        {
            StyleAssets.GlyphAtlas.AddRangeForTest(new VectorGlyphRange(
                fontStack,
                range.Key,
                range.ToDictionary(
                    glyph => (int)glyph.Character,
                    glyph => glyph.ToVectorGlyph())));
        }
    }

    internal byte[] GetTile(TileId id)
    {
        if (id != TileId)
        {
            throw new InvalidOperationException(
                $"The test vector source does not contain tile {id}.");
        }
        return _tile;
    }

}

internal readonly record struct TestGlyph(
    char Character,
    uint Width,
    uint Height,
    int Left,
    int Top,
    uint Advance,
    byte[] Bitmap)
{
    internal static TestGlyph Solid(
        char character,
        uint width = 6,
        uint height = 8,
        uint advance = 7)
    {
        int textureWidth = checked((int)width + ((int)VectorGlyph.SdfBuffer * 2));
        int textureHeight = checked((int)height + ((int)VectorGlyph.SdfBuffer * 2));
        return new TestGlyph(
            character,
            width,
            height,
            0,
            checked((int)height),
            advance,
            Enumerable.Repeat(
                byte.MaxValue,
                checked(textureWidth * textureHeight))
            .ToArray());
    }

    internal VectorGlyph ToVectorGlyph() =>
        new(Character, Bitmap.ToArray(), Width, Height, Left, Top, Advance);
}

internal sealed class TestVectorTileLayer : TileLayer
{
    private TestVectorTileSource _source;

    internal TestVectorTileLayer(
        TestVectorTileSource source,
        TimeSpan? fadeDuration = null)
        : base(new TileLayerOptions
        {
            TileSize = 256,
            MinSourceZoom = source.TileId.Zoom,
            MaxSourceZoom = source.TileId.Zoom,
            FadeDuration = fadeDuration ?? TimeSpan.Zero,
        })
    {
        _source = source;
    }

    internal void ReplaceSource(TestVectorTileSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        NotifyChanged(TileUrlProperty);
    }

    internal override TileLayerSnapshot CreateSnapshot() =>
        new(
            RuntimeId,
            Revision,
            new TestVectorTileAcquisitionSession(_source),
            MinZoom,
            MaxZoom,
            IsVisible,
            Opacity,
            FadeDuration);
}

internal sealed class TestVectorTileAcquisitionSession(
    TestVectorTileSource source) : RasterTileAcquisitionSession
{
    internal override object SourceKey => source;

    internal override RasterSourceKind SourceKind => RasterSourceKind.Custom;

    internal override LayerRenderKind RenderKind => LayerRenderKind.VectorPoints;

    internal override int TileSize => 256;

    internal override int MinSourceZoom => source.TileId.Zoom;

    internal override int MaxSourceZoom => source.TileId.Zoom;

    internal override bool CanAcquire => true;

    internal override int GetSourceZoom(MapScene scene) => source.TileId.Zoom;

    internal override bool IncludesTile(TileId id) => id == source.TileId;

    internal override Task<DecodedRasterTile> GetTileAsync(
        TileId id,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "The test vector source does not provide raster tiles.");

    internal override async Task<DecodedVectorTile> GetVectorTileAsync(
        TileId id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VectorTileFeatureCollection features = VectorTileDecoder.Decode(
            source.GetTile(id));
        VectorSpriteTextureData[] textures =
            await source.StyleAssets.PrepareTexturesAsync(
                features,
                id.Zoom,
                cancellationToken);
        return new DecodedVectorTile(
            id,
            features,
            source.StyleAssets,
            textures,
            null,
            0,
            0);
    }
}
