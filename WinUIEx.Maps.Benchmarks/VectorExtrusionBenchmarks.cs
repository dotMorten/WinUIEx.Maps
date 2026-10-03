using System.Diagnostics;
using System.Text;
using BenchmarkDotNet.Attributes;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
[BenchmarkCategory("VectorTiles", "Extrusions")]
public class VectorExtrusionBenchmarks
{
    public enum SceneKind { Flat, Hidden, Buildings }

    [Params(SceneKind.Flat, SceneKind.Hidden, SceneKind.Buildings)]
    public SceneKind Scene { get; set; }

    private readonly TileId _tile = new(16, 32768, 23000);
    private MapRenderer _renderer = null!;
    private VectorStyledExtrusion[] _extrusions = null!;

    [GlobalSetup]
    public void Setup()
    {
        List<VectorTileFeature> features = [];
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            double left = (x + 0.15) / 8, top = (y + 0.15) / 8;
            VectorTilePoint a = new(left, top), b = new(left + 0.07, top),
                c = new(left + 0.07, top + 0.07), d = new(left, top + 0.07);
            features.Add(new("building", VectorTileGeometryType.Polygon, [], [], [],
                [new([new([a, b, c, d])], [a, b, c, a, c, d])]));
        }
        VectorTileFeatureCollection geometry = new(features.ToArray());
        string type = Scene == SceneKind.Flat ? "fill" : "fill-extrusion";
        string visibility = Scene == SceneKind.Hidden ? "none" : "visible";
        byte[] style = Encoding.UTF8.GetBytes($$"""
            {"version":8,"layers":[{
              "id":"buildings","source":"microsoft.base","source-layer":"building",
              "type":"{{type}}","layout":{"visibility":"{{visibility}}"},
              "paint":{"{{type}}-color":"#c7bda9","fill-extrusion-height":50,"fill-antialias":false}
            }]}
            """);
        var assets = VectorStyleAssets.CreateForTest(MapStyle.Road, style,
            "{}"u8.ToArray(), [0, 0, 0, 0], 1, 1);
        _extrusions = assets.ResolveExtrusions(geometry, 16).Extrusions;
        if (_extrusions.Length != (Scene == SceneKind.Buildings ? 64 : 0))
            throw new InvalidOperationException("Unexpected extrusion fixture resolution.");
        double longitude = MapCamera.WorldXToLongitude((_tile.X + 0.5) / 65536);
        double latitude = MapCamera.WorldYToLatitude((_tile.Y + 0.5) / 65536);
        _renderer = new();
        _renderer.InitializeOffscreenForBenchmark(1024, 768);
        _renderer.SetCameraTargetImmediately(longitude, latitude, 16, 1024, 768, 20, 55);
        _renderer.SetLayerRenderPlan([new(LayerRenderKind.VectorPoints, 0, 1, true, 1,
            TimeSpan.Zero, 0, 24, 0, 256)]);
        _renderer.ActivateRasterTileSet(1, 1, 1,
            MapCamera.CreateScene(longitude, latitude, 16, 16, 1024, 768, 20, 55),
            id => id == _tile, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        _renderer.AddVectorTileForBenchmark(new(1, _tile), geometry, assets);
        long start = Stopwatch.GetTimestamp();
        do
        {
            _renderer.RenderOffscreenFrameForBenchmark();
            if (Scene != SceneKind.Buildings || _renderer.ExtrusionMeshBytesForTest > 0)
                break;
            if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(10))
                throw new TimeoutException("Extrusion geometry did not become drawable.");
            Thread.Sleep(1);
        } while (true);
        _renderer.RenderOffscreenFrameForBenchmark();
        if (Scene != SceneKind.Buildings &&
            (_renderer.ExtrusionResourceBytesForTest != 0 || _renderer.ExtrusionMeshBytesForTest != 0))
            throw new InvalidOperationException("A control scene allocated extrusion resources.");
    }

    [Benchmark]
    [BenchmarkCategory("Preparation")]
    public long PrepareRoofAndWallMesh()
    {
        if (_extrusions.Length == 0)
            return 0;
        using ExtrusionMeshBuffer mesh = new();
        foreach (var extrusion in _extrusions)
            VectorExtrusionGeometry.Append(_tile, extrusion, mesh, CancellationToken.None, 256);
        return mesh.ByteSize;
    }

    [Benchmark]
    [BenchmarkCategory("GPU", "Rendering")]
    public long RenderCompletedFrame() => _renderer.RenderOffscreenFrameForBenchmark();

    [GlobalCleanup]
    public void Cleanup() => _renderer.Dispose();
}
