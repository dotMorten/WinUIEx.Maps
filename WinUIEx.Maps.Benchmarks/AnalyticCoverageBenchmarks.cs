using BenchmarkDotNet.Attributes;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
[BenchmarkCategory("GPU", "Rendering")]
public class AnalyticCoverageBenchmarks
{
    private MapRenderer _renderer = null!;

    [Params(false, true)]
    public bool Analytic { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        VectorSymbolBenchmarkFixture fixture = VectorSymbolBenchmarkFixture.Create(64);
        TileId id = new(14, 4823, 6160);
        double longitude = MapCamera.WorldXToLongitude((id.X + 0.5) / (1 << id.Zoom));
        double latitude = MapCamera.WorldYToLatitude((id.Y + 0.5) / (1 << id.Zoom));
        MapScene scene = MapCamera.CreateScene(longitude, latitude, 14, 14, 1024, 768, 0, 0);
        _renderer = new MapRenderer();
        _renderer.InitializeCoveragePrototypeForBenchmark(1024, 768, Analytic);
        _renderer.SetCameraTargetImmediately(longitude, latitude, 14, 1024, 768);
        _renderer.ActivateRasterTileSet(1, 1, 1, scene, static _ => true,
            RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        _renderer.SetLayerRenderPlan(
        [
            new LayerRenderSnapshot(LayerRenderKind.VectorPoints, 0, 1, true, 1,
                TimeSpan.Zero, 0, 24, 0, 256, Style: (int)MapStyle.Road),
        ]);
        _renderer.AddVectorTexturesForBenchmark(fixture.Textures);
        foreach (TileId tile in scene.RequiredTiles.Distinct())
            _renderer.AddVectorTileForBenchmark(new RasterTileKey(1, tile), fixture.Features, fixture.StyleAssets);
        _renderer.RenderOffscreenFrameForBenchmark();
        _renderer.RenderOffscreenFrameForBenchmark();
    }

    [Benchmark]
    public long RenderCapsuleCoverageWithoutPresent() => _renderer.RenderOffscreenFrameForBenchmark();

    [GlobalCleanup]
    public void Cleanup() => _renderer.Dispose();
}
