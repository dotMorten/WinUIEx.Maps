using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class MapRendererRasterTests
{
    [TestMethod]
    public async Task QueueRasterUploadRejectsTileWithoutActiveSource()
    {
        using MapRenderer renderer = new();
        RasterTileData tile = new(
            new RasterTileKey(42, new TileId(3, 4, 4)),
            [0, 0, 0, 255],
            1,
            1,
            1,
            RasterSourceKind.Custom);

        bool accepted = await renderer.QueueRasterUploadAsync(
            tile,
            CancellationToken.None);

        Assert.IsFalse(accepted);
    }

    [TestMethod]
    public async Task QueueHybridTileRejectsMissingBackground()
    {
        using MapRenderer renderer = new();
        VectorTileData tile = CreateHybridTile(background: null);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            renderer.QueueHybridTileAsync(tile, CancellationToken.None));
    }

    [TestMethod]
    public async Task QueueHybridTileRejectsInconsistentBackground()
    {
        using MapRenderer renderer = new();
        VectorTileData tile = CreateHybridTile(
            new RasterTileData(
                new RasterTileKey(43, new TileId(3, 4, 4)),
                [0, 0, 0, 255],
                1,
                1,
                1,
                RasterSourceKind.Custom));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            renderer.QueueHybridTileAsync(tile, CancellationToken.None));
    }

    [TestMethod]
    public async Task QueueHybridTileRejectsTileWithoutActiveSource()
    {
        using MapRenderer renderer = new();
        RasterTileKey key = new(42, new TileId(3, 4, 4));
        VectorTileData tile = CreateHybridTile(
            new RasterTileData(
                key,
                [0, 0, 0, 255],
                1,
                1,
                1,
                RasterSourceKind.Custom));

        bool accepted = await renderer.QueueHybridTileAsync(
            tile,
            CancellationToken.None);

        Assert.IsFalse(accepted);
    }

    private static VectorTileData CreateHybridTile(RasterTileData? background)
    {
        RasterTileKey key = new(42, new TileId(3, 4, 4));
        return new VectorTileData(
            key,
            new VectorTileFeatureCollection([]),
            AzureVectorStyleAssets.CreateForTest(
                MapStyle.Road,
                """{"version":8,"layers":[]}"""u8.ToArray(),
                "{}"u8.ToArray(),
                [0, 0, 0, 0],
                1,
                1),
            [],
            background,
            1,
            0);
    }
}
