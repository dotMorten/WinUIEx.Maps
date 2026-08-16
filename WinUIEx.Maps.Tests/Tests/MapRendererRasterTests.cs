using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;
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

    [TestMethod]
    public async Task RemovingVectorSourceDropsSourceLevelVectorStyleAssets()
    {
        WeakReference assets = await RetainAndReleaseVectorStyleAssetsAsync();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.IsFalse(
            assets.IsAlive,
            "Renderer resource release must not retain source-level vector style assets.");
    }

    private static async Task<WeakReference> RetainAndReleaseVectorStyleAssetsAsync()
    {
        using MapRenderer renderer = new();
        const long sourceId = 42;
        TileId id = new(3, 4, 4);
        MapScene scene = MapCamera.CreateScene(0, 0, 3, 3, 640, 480, 0, 0);
        renderer.ActivateRasterTileSet(
            sourceId,
            1,
            1,
            scene,
            tile => tile == id,
            RasterSourceKind.Custom,
            LayerRenderKind.VectorPoints,
            clearExistingTiles: false);

        VectorStyleAssets assets = VectorStyleAssets.CreateForTest(
            MapStyle.Road,
            """{"version":8,"layers":[]}"""u8.ToArray(),
            "{}"u8.ToArray(),
            new byte[8 * 1024 * 1024],
            1024,
            2048);
        WeakReference reference = new(assets);
        Assert.IsTrue(await renderer.QueueVectorTileAsync(
            new VectorTileData(
                new RasterTileKey(sourceId, id),
                new VectorTileFeatureCollection([]),
                assets,
                [],
                null,
                1,
                0),
            CancellationToken.None));

        MethodInfo processCompleted = typeof(MapRenderer).GetMethod(
            "ProcessCompletedVectorTiles",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "Could not find the vector tile completion method.");
        processCompleted.Invoke(renderer, null);
        renderer.RemoveRasterTileSource(sourceId);
        return reference;
    }

    private static VectorTileData CreateHybridTile(RasterTileData? background)
    {
        RasterTileKey key = new(42, new TileId(3, 4, 4));
        return new VectorTileData(
            key,
            new VectorTileFeatureCollection([]),
            VectorStyleAssets.CreateForTest(
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
