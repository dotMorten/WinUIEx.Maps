using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class VectorCoverageTests
{
    [TestMethod]
    public void GeometryReplacementRequiresEligibleSameSourceOpaqueCoverage()
    {
        MapScene scene = MapCamera.CreateScene(0, 0, 8, 800, 600);
        TileId eligible = scene.RequiredTiles[0];
        TileId coarse = new(4, eligible.X >> 4, eligible.Y >> 4);
        using MapRenderer renderer = new();
        renderer.ActivateRasterTileSet(
            42, 1, 1, scene, id => id == eligible,
            RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        renderer.ActivateRasterTileSet(
            43, 1, 1, scene, _ => true,
            RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        VectorStyleAssets style = VectorStyleAssets.CreateForTest(
            MapStyle.Road, """{"version":8,"layers":[]}"""u8.ToArray(),
            "{}"u8.ToArray(), [0, 0, 0, 0], 1, 1);
        renderer.AddVectorTileForBenchmark(
            new RasterTileKey(43, eligible), new VectorTileFeatureCollection([]), style);
        Assert.AreEqual(0, renderer.GetVectorGeometryReplacementOpacity(
            42, coarse, scene, TimeSpan.Zero));
        renderer.AddVectorTileForBenchmark(
            new RasterTileKey(42, eligible), new VectorTileFeatureCollection([]), style);
        Assert.AreEqual(1, renderer.GetVectorGeometryReplacementOpacity(
            42, coarse, scene, TimeSpan.Zero));
        Assert.IsLessThan(0.01, renderer.GetVectorGeometryReplacementOpacity(
            42, coarse, scene, TimeSpan.FromDays(1)));
        Assert.AreEqual(0, renderer.GetVectorGeometryReplacementOpacity(
            42, coarse, null, TimeSpan.Zero));
    }

    [TestMethod]
    public void NearerFallbackReplacesOlderGeometryOnlyWhenItCoversTheVisibleFootprint()
    {
        MapScene scene = MapCamera.CreateScene(0, 0, 8, 800, 600);
        TileId[] eligible = scene.RequiredTiles.Where(id => id.X >= 128 && id.Y >= 128).ToArray();
        TileId coarse = new(4, 8, 8);
        using MapRenderer renderer = new();
        renderer.ActivateRasterTileSet(
            42, 1, 1, scene, id => eligible.Contains(id),
            RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        renderer.ActivateRasterTileSet(
            43, 1, 1, scene, _ => true,
            RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        VectorStyleAssets style = VectorStyleAssets.CreateForTest(
            MapStyle.Road, """{"version":8,"layers":[]}"""u8.ToArray(),
            "{}"u8.ToArray(), [0, 0, 0, 0], 1, 1);
        HashSet<int> levels = [4, 6, 7];
        TileId first = eligible[0];
        renderer.AddVectorTileForBenchmark(
            new RasterTileKey(42, first), new VectorTileFeatureCollection([]), style);
        Assert.AreEqual(0, renderer.GetVectorGeometryReplacementOpacity(
            42, coarse, scene, TimeSpan.Zero, levels));
        TileId nearer = new(6, 32, 32);
        renderer.AddVectorTileForBenchmark(
            new RasterTileKey(43, nearer), new VectorTileFeatureCollection([]), style);
        Assert.AreEqual(0, renderer.GetVectorGeometryReplacementOpacity(
            42, coarse, scene, TimeSpan.Zero, levels));
        renderer.AddVectorTileForBenchmark(
            new RasterTileKey(42, nearer), new VectorTileFeatureCollection([]), style);
        Assert.AreEqual(1, renderer.GetVectorGeometryReplacementOpacity(
            42, coarse, scene, TimeSpan.Zero, levels));
        Assert.AreEqual(0, renderer.GetVectorGeometryReplacementOpacity(
            42, nearer, scene, TimeSpan.Zero, levels));
        Assert.IsLessThan(0.01, renderer.GetVectorGeometryReplacementOpacity(
            42, coarse, scene, TimeSpan.FromDays(1), levels));
    }

    [TestMethod]
    public void CoverageRequiresOnlyEligibleOpaqueTiles()
    {
        MapScene scene = MapCamera.CreateScene(0, 0, 4, 800, 600);
        TileId eligible = scene.RequiredTiles[0];
        Func<TileId, bool> includesTile = id => id == eligible;
        using MapRenderer renderer = new();
        renderer.ActivateRasterTileSet(
            42, 1, 1, scene, includesTile,
            RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        Assert.IsFalse(renderer.HasCompleteVectorCoverage(
            42, scene, includesTile, TimeSpan.Zero));

        VectorStyleAssets style = VectorStyleAssets.CreateForTest(
            MapStyle.Road,
            """{"version":8,"layers":[]}"""u8.ToArray(),
            "{}"u8.ToArray(), [0, 0, 0, 0], 1, 1);
        renderer.AddVectorTileForBenchmark(
            new RasterTileKey(42, eligible), new VectorTileFeatureCollection([]), style);

        Assert.IsTrue(renderer.HasCompleteVectorCoverage(
            42, scene, includesTile, TimeSpan.Zero));
        Assert.IsFalse(renderer.HasCompleteVectorCoverage(
            42, scene, includesTile, TimeSpan.FromDays(1)));
        Assert.IsFalse(renderer.HasCompleteVectorCoverage(
            42, scene, static _ => true, TimeSpan.Zero));
        Assert.IsFalse(renderer.HasCompleteVectorCoverage(
            42, scene, static _ => false, TimeSpan.Zero));
        Assert.IsFalse(renderer.HasCompleteVectorCoverage(
            43, scene, includesTile, TimeSpan.Zero));
    }
}
