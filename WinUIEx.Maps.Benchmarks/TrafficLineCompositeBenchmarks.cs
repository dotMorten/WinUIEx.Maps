using BenchmarkDotNet.Attributes;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
[BenchmarkCategory("GPU", "Rendering", "VectorTiles", "Traffic")]
public class TrafficLineCompositeBenchmarks
{
    [Params(1, 4)]
    public int Samples { get; set; }

    [Params(1, 2)]
    public int Sources { get; set; }

    private MapRenderer _renderer = null!;

    [GlobalSetup(Target = nameof(DirectRoadOpacity))]
    public Task SetupDirect() => SetupAsync(composite: false);

    [GlobalSetup(Target = nameof(CompositedRoadOpacity))]
    public Task SetupComposite() => SetupAsync(composite: true);

    private async Task SetupAsync(bool composite)
    {
        // Reuse urban line geometry, not live traffic or network requests.
        VectorTileBenchmarkFixture fixture = VectorTileBenchmarkFixture.Load(VectorTileFixture.NewYorkZ14);
        var tile = await AzureOverlayAcquisitionSession.DecodeTrafficTileAsync(
            fixture.Id, fixture.Encoded, TrafficFlowStyle.Relative);
        double scale = Math.Pow(2, fixture.Id.Zoom);
        double longitude = MapCamera.WorldXToLongitude((fixture.Id.X + 0.5) / scale);
        double latitude = MapCamera.WorldYToLatitude((fixture.Id.Y + 0.5) / scale);
        MapScene scene = MapCamera.CreateScene(longitude, latitude, fixture.Id.Zoom,
            fixture.Id.Zoom, 1920, 1080, 0, 0);
        _renderer = new MapRenderer();
        _renderer.InitializeOffscreenForBenchmark(1920, 1080, Samples);
        if (_renderer.RenderSampleCount != Samples)
        {
            _renderer.Dispose();
            throw new NotSupportedException("The requested multisample target is not supported.");
        }
        _renderer.SetCameraTargetImmediately(longitude, latitude, fixture.Id.Zoom, 1920, 1080);
        LayerRenderSnapshot[] plan = new LayerRenderSnapshot[Sources];
        for (int sourceId = 1; sourceId <= Sources; sourceId++)
        {
            _renderer.ActivateRasterTileSet(sourceId, 1, 1, scene, static _ => true,
                RasterSourceKind.Azure, LayerRenderKind.VectorPoints, false);
            plan[sourceId - 1] = new(LayerRenderKind.VectorPoints, 0, sourceId, true,
                composite ? 1 : AzureTrafficLayer.RoadLineOpacity, TimeSpan.Zero, 0, 24, 0, 256,
                LineCompositeOpacity: composite ? AzureTrafficLayer.RoadLineOpacity : 1);
            foreach (TileId id in scene.RequiredTiles.Distinct())
                _renderer.AddVectorTileForBenchmark(new(sourceId, id), tile.Features, tile.StyleAssets);
        }
        _renderer.SetLayerRenderPlan(plan);
        _renderer.RenderOffscreenFrameForBenchmark();
        _renderer.RenderOffscreenFrameForBenchmark();
    }

    [Benchmark(Baseline = true)]
    public long DirectRoadOpacity() => _renderer.RenderOffscreenFrameForBenchmark();

    [Benchmark]
    public long CompositedRoadOpacity() => _renderer.RenderOffscreenFrameForBenchmark();

    [GlobalCleanup]
    public void Cleanup() => _renderer.Dispose();
}
