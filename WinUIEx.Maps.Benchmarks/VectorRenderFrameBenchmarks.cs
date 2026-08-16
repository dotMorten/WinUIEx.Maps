using BenchmarkDotNet.Attributes;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
[BenchmarkCategory("GPU", "Rendering", "VectorTiles")]
public class VectorRenderFrameBenchmarks
{
    private const int Width = 1024;
    private const int Height = 768;
    private const long SourceId = 2;
    private MapRenderer _renderer = null!;
    private double _longitude;
    private double _latitude;
    private int _zoom;
    private bool _cameraToggle;

    [Params(
        VectorTileFixture.NewYorkZ10,
        VectorTileFixture.NewYorkZ14,
        VectorTileFixture.TokyoZ16)]
    public VectorTileFixture Fixture { get; set; }

    [Params(0, 60)]
    public double Pitch { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        VectorTileBenchmarkFixture fixture =
            VectorTileBenchmarkFixture.Load(Fixture);
        _zoom = fixture.Id.Zoom;
        double scale = Math.Pow(2, fixture.Id.Zoom);
        _longitude =
            MapCamera.WorldXToLongitude((fixture.Id.X + 0.5) / scale);
        _latitude =
            MapCamera.WorldYToLatitude((fixture.Id.Y + 0.5) / scale);
        MapScene scene = MapCamera.CreateScene(
            _longitude,
            _latitude,
            fixture.Id.Zoom,
            fixture.Id.Zoom,
            Width,
            Height,
            0,
            Pitch);

        _renderer = new MapRenderer();
        _renderer.InitializeOffscreenForBenchmark(Width, Height);
        _renderer.SetCameraTargetImmediately(
            _longitude,
            _latitude,
            fixture.Id.Zoom,
            Width,
            Height,
            targetHeading: 0,
            targetPitch: Pitch);
        _renderer.ActivateRasterTileSet(
            SourceId,
            generation: 1,
            sceneVersion: 1,
            scene,
            static _ => true,
            RasterSourceKind.Custom,
            LayerRenderKind.VectorPoints,
            clearExistingTiles: false);
        _renderer.SetLayerRenderPlan(
        [
            new LayerRenderSnapshot(
                LayerRenderKind.VectorPoints,
                LayerIndex: 0,
                RuntimeId: SourceId,
                IsVisible: true,
                Opacity: 1,
                FadeDuration: TimeSpan.Zero,
                MinZoom: 0,
                MaxZoom: 24,
                MinSourceZoom: 0,
                TileSize: 256,
                Style: (int)MapStyle.Road),
        ]);
        foreach (TileId id in scene.RequiredTiles.Distinct())
        {
            _renderer.AddVectorTileForBenchmark(
                new RasterTileKey(SourceId, id),
                fixture.Features,
                fixture.StyleAssets);
        }

        _renderer.RenderOffscreenFrameForBenchmark();
        _renderer.RenderOffscreenFrameForBenchmark();
    }

    [Benchmark]
    public long RenderVectorFrameWithoutPresent() =>
        _renderer.RenderOffscreenFrameForBenchmark();

    [Benchmark]
    public long RenderVectorFrameWithCameraChangeWithoutPresent()
    {
        _cameraToggle = !_cameraToggle;
        _renderer.SetCameraTargetImmediately(
            _longitude + (_cameraToggle ? 0.000001 : -0.000001),
            _latitude,
            _zoom,
            Width,
            Height,
            targetHeading: 0,
            targetPitch: Pitch);
        return _renderer.RenderOffscreenFrameForBenchmark();
    }

    [GlobalCleanup]
    public void Cleanup() => _renderer.Dispose();
}
