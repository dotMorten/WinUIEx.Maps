using System.Collections.Concurrent;
using System.Text;
using Windows.Devices.Geolocation;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests.UITestHelpers;

internal sealed class TestRasterTileSource
{
    private readonly IReadOnlyDictionary<TileId, TestRasterTile> _tiles;
    private readonly ConcurrentDictionary<TileId, int> _requestCounts = new();
    private readonly TaskCompletionSource _firstRequest =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TestRasterTileSource(
        int zoom,
        IReadOnlyDictionary<TileId, TestRasterTile> tiles,
        RasterSourceKind sourceKind = RasterSourceKind.Custom,
        int hybridVectorByteSize = 0)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        if (tiles.Count == 0 || tiles.Keys.Any(id => id.Zoom != zoom))
        {
            throw new ArgumentException(
                "Test raster sources require at least one tile at the configured zoom.",
                nameof(tiles));
        }

        Zoom = zoom;
        SourceKind = sourceKind;
        _tiles = tiles.ToDictionary(
            pair => pair.Key,
            pair => pair.Value with { Pixels = pair.Value.Pixels.ToArray() });
        StyleAssets = AzureVectorStyleAssets.CreateForTest(
            MapStyle.Road,
            Encoding.UTF8.GetBytes("""{"version":8,"layers":[]}"""),
            Encoding.UTF8.GetBytes("{}"),
            [0, 0, 0, 0],
            1,
            1);
        HybridFeatures = hybridVectorByteSize == 0
            ? new VectorTileFeatureCollection([])
            : new VectorTileFeatureCollection(
                [
                    new VectorTileFeature(
                        new string('v', hybridVectorByteSize / 2),
                        VectorTileGeometryType.Point,
                        [],
                        [],
                        [],
                        []),
                ]);
    }

    internal int Zoom { get; }

    internal RasterSourceKind SourceKind { get; }

    internal AzureVectorStyleAssets StyleAssets { get; }

    internal VectorTileFeatureCollection HybridFeatures { get; }

    internal Task FirstRequest => _firstRequest.Task;

    internal int TotalRequestCount => _requestCounts.Values.Sum();

    internal int GetRequestCount(TileId id) =>
        _requestCounts.TryGetValue(id, out int count) ? count : 0;

    internal bool Includes(TileId id) => _tiles.ContainsKey(id);

    internal TestRasterTile GetTile(TileId id)
    {
        _requestCounts.AddOrUpdate(id, 1, static (_, count) => count + 1);
        _firstRequest.TrySetResult();
        if (!_tiles.TryGetValue(id, out TestRasterTile tile))
        {
            throw new InvalidOperationException(
                $"The test raster source does not contain tile {id}.");
        }

        return tile with { Pixels = tile.Pixels.ToArray() };
    }

    internal BasicGeoposition GetTileCenter(TileId id)
    {
        if (id.Zoom != Zoom)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        double scale = Math.Pow(2, id.Zoom);
        double worldX = (id.X + 0.5) / scale;
        double worldY = (id.Y + 0.5) / scale;
        return new BasicGeoposition
        {
            Longitude = (worldX * 360) - 180,
            Latitude = Math.Atan(Math.Sinh(
                Math.PI * (1 - (2 * worldY)))) * 180 / Math.PI,
        };
    }

    internal static TestRasterTile Solid(
        int size,
        byte red,
        byte green,
        byte blue,
        byte alpha = byte.MaxValue)
    {
        byte[] pixels = new byte[checked(size * size * 4)];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = alpha;
        }

        return new TestRasterTile(pixels, checked((uint)size), checked((uint)size));
    }

    internal static TestRasterTile VerticalSplit(
        int size,
        (byte Red, byte Green, byte Blue) left,
        (byte Red, byte Green, byte Blue) right)
    {
        byte[] pixels = new byte[checked(size * size * 4)];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                (byte red, byte green, byte blue) =
                    x < size / 2 ? left : right;
                int offset = ((y * size) + x) * 4;
                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
                pixels[offset + 3] = byte.MaxValue;
            }
        }

        return new TestRasterTile(pixels, checked((uint)size), checked((uint)size));
    }
}

internal readonly record struct TestRasterTile(
    byte[] Pixels,
    uint Width,
    uint Height);

internal sealed class TestRasterTileLayer : TileLayer
{
    private TestRasterTileSource _source;
    private int _sourceRevision;

    internal TestRasterTileLayer(TestRasterTileSource source)
        : base(new TileLayerOptions
        {
            TileSize = 256,
            MinSourceZoom = source.Zoom,
            MaxSourceZoom = source.Zoom,
            FadeDuration = TimeSpan.Zero,
        })
    {
        _source = source;
    }

    internal void ReplaceSource(TestRasterTileSource source)
    {
        if (source.Zoom != _source.Zoom)
        {
            throw new ArgumentException(
                "Replacement test sources must use the same zoom.",
                nameof(source));
        }

        _source = source;
        TileUrl =
            $"https://test.invalid/{{z}}/{{x}}/{{y}}?revision={++_sourceRevision}";
    }

    internal override TileLayerSnapshot CreateSnapshot() =>
        new(
            RuntimeId,
            Revision,
            new TestRasterTileAcquisitionSession(_source),
            MinZoom,
            MaxZoom,
            IsVisible,
            Opacity,
            FadeDuration);
}

internal sealed class TestRasterTileAcquisitionSession(
    TestRasterTileSource source) : RasterTileAcquisitionSession
{
    internal override object SourceKey => source;

    internal override RasterSourceKind SourceKind => source.SourceKind;

    internal override LayerRenderKind RenderKind => LayerRenderKind.RasterTiles;

    internal override int TileSize => 256;

    internal override int MinSourceZoom => source.Zoom;

    internal override int MaxSourceZoom => source.Zoom;

    internal override bool CanAcquire => true;

    internal override int GetSourceZoom(MapScene scene) => source.Zoom;

    internal override bool IncludesTile(TileId id) => source.Includes(id);

    internal override Task<DecodedRasterTile> GetTileAsync(
        TileId id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestRasterTile tile = source.GetTile(id);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DecodedRasterTile(
            id,
            tile.Pixels,
            tile.Width,
            tile.Height,
            0,
            0));
    }

    internal override Task<DecodedVectorTile> GetVectorTileAsync(
        TileId id,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "The test raster source does not provide vector tiles.");
}

internal sealed class TestHybridRasterTileLayer : TileLayer
{
    private readonly TestRasterTileSource _source;

    internal TestHybridRasterTileLayer(TestRasterTileSource source)
        : base(new TileLayerOptions
        {
            TileSize = 256,
            MinSourceZoom = source.Zoom,
            MaxSourceZoom = source.Zoom,
            FadeDuration = TimeSpan.Zero,
        })
    {
        _source = source;
    }

    internal override TileLayerSnapshot CreateSnapshot() =>
        new(
            RuntimeId,
            Revision,
            new TestHybridRasterTileAcquisitionSession(_source),
            MinZoom,
            MaxZoom,
            IsVisible,
            Opacity,
            FadeDuration);
}

internal sealed class TestHybridRasterTileAcquisitionSession(
    TestRasterTileSource source) : RasterTileAcquisitionSession
{
    internal override object SourceKey => source;

    internal override RasterSourceKind SourceKind => source.SourceKind;

    internal override LayerRenderKind RenderKind => LayerRenderKind.HybridTiles;

    internal override int TileSize => 256;

    internal override int MinSourceZoom => source.Zoom;

    internal override int MaxSourceZoom => source.Zoom;

    internal override bool CanAcquire => true;

    internal override int GetSourceZoom(MapScene scene) => source.Zoom;

    internal override bool IncludesTile(TileId id) => source.Includes(id);

    internal override Task<DecodedRasterTile> GetTileAsync(
        TileId id,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "The test hybrid source acquires its raster background with vector data.");

    internal override Task<DecodedVectorTile> GetVectorTileAsync(
        TileId id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestRasterTile tile = source.GetTile(id);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DecodedVectorTile(
            id,
            source.HybridFeatures,
            source.StyleAssets,
            [],
            new DecodedRasterTile(
                id,
                tile.Pixels,
                tile.Width,
                tile.Height,
                0,
                0),
            0,
            0));
    }
}
