using BenchmarkDotNet.Attributes;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
[BenchmarkCategory("GPU", "Rendering")]
public class RenderFrameBenchmarks
{
    private const int Width = 1024;
    private const int Height = 768;
    private const int TileSize = 256;
    private const long SourceId = 1;
    private MapRenderer _renderer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _renderer = new MapRenderer();
        _renderer.InitializeOffscreenForBenchmark(Width, Height);

        MapScene scene = MapCamera.CreateScene(
            0,
            0,
            3,
            3,
            Width,
            Height,
            0,
            0);
        _renderer.SetCameraTargetImmediately(
            scene.Longitude,
            scene.Latitude,
            scene.Zoom,
            Width,
            Height);
        _renderer.ActivateRasterTileSet(
            SourceId,
            generation: 1,
            sceneVersion: 1,
            scene,
            static _ => true,
            RasterSourceKind.Custom,
            LayerRenderKind.RasterTiles,
            clearExistingTiles: false);
        _renderer.SetLayerRenderPlan(
        [
            new LayerRenderSnapshot(
                LayerRenderKind.RasterTiles,
                LayerIndex: 0,
                RuntimeId: SourceId,
                IsVisible: true,
                Opacity: 1,
                FadeDuration: TimeSpan.Zero,
                MinZoom: 0,
                MaxZoom: 24,
                MinSourceZoom: 0,
                TileSize),
        ]);

        byte[] pixels = GpuUploadBenchmarks.CreatePixels(TileSize, TileSize);
        foreach (TileId id in scene.RequiredTiles.Distinct())
        {
            _renderer.AddRasterTileForBenchmark(
                new RasterTileKey(SourceId, id),
                pixels,
                TileSize,
                TileSize);
        }

        _renderer.RenderOffscreenFrameForBenchmark();
        _renderer.RenderOffscreenFrameForBenchmark();
    }

    [Benchmark]
    public long RenderRasterFrameWithoutPresent() =>
        _renderer.RenderOffscreenFrameForBenchmark();

    [GlobalCleanup]
    public void Cleanup() => _renderer.Dispose();
}
