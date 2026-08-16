using BenchmarkDotNet.Attributes;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
[BenchmarkCategory("GPU", "Rendering", "Symbols", "VectorTiles")]
public class VectorSymbolRenderFrameBenchmarks
{
    private const int Width = 1024;
    private const int Height = 768;
    private const int TileZoom = 14;
    private const long SourceId = 3;
    private MapRenderer _renderer = null!;
    private long _preparedFrameBuildCount;
    private double _longitude;
    private double _latitude;
    private bool _cameraToggle;

    [Params(64, 256)]
    public int SymbolsPerTile { get; set; }

    [Params(0, 60)]
    public double Pitch { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        VectorSymbolBenchmarkFixture fixture =
            VectorSymbolBenchmarkFixture.Create(SymbolsPerTile);
        TileId centerTile = new(TileZoom, 4823, 6160);
        double scale = Math.Pow(2, centerTile.Zoom);
        _longitude =
            MapCamera.WorldXToLongitude((centerTile.X + 0.5) / scale);
        _latitude =
            MapCamera.WorldYToLatitude((centerTile.Y + 0.5) / scale);
        MapScene scene = MapCamera.CreateScene(
            _longitude,
            _latitude,
            centerTile.Zoom,
            centerTile.Zoom,
            Width,
            Height,
            0,
            Pitch);

        _renderer = new MapRenderer();
        _renderer.InitializeOffscreenForBenchmark(Width, Height);
        _renderer.SetCameraTargetImmediately(
            _longitude,
            _latitude,
            centerTile.Zoom,
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
        _renderer.AddVectorTexturesForBenchmark(fixture.Textures);
        foreach (TileId id in scene.RequiredTiles.Distinct())
        {
            _renderer.AddVectorTileForBenchmark(
                new RasterTileKey(SourceId, id),
                fixture.Features,
                fixture.StyleAssets);
        }

        _renderer.RenderOffscreenFrameForBenchmark();
        _renderer.RenderOffscreenFrameForBenchmark();
        _preparedFrameBuildCount =
            _renderer.VectorSymbolFrameBuildCountForBenchmark;
    }

    [Benchmark]
    public long RenderGlyphsAndSpritesWithoutPresent()
    {
        long frame = _renderer.RenderOffscreenFrameForBenchmark();
        if (_renderer.VectorSymbolFrameBuildCountForBenchmark !=
            _preparedFrameBuildCount)
        {
            throw new InvalidOperationException(
                "The steady-state symbol frame was unexpectedly rebuilt.");
        }
        return frame;
    }

    [Benchmark]
    public long RenderGlyphsAndSpritesWithCameraChange()
    {
        _cameraToggle = !_cameraToggle;
        _renderer.SetCameraTargetImmediately(
            _longitude + (_cameraToggle ? 0.000001 : -0.000001),
            _latitude,
            TileZoom,
            Width,
            Height,
            targetHeading: 0,
            targetPitch: Pitch);
        return _renderer.RenderOffscreenFrameForBenchmark();
    }

    [GlobalCleanup]
    public void Cleanup() => _renderer.Dispose();
}
