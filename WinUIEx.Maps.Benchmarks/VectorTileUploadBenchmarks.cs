using BenchmarkDotNet.Attributes;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
[BenchmarkCategory("GPU", "Upload", "VectorTiles")]
public class VectorTileUploadBenchmarks
{
    private MapRenderer _renderer = null!;
    private VectorTileBenchmarkFixture _fixture = null!;

    [Params(
        VectorTileFixture.NewYorkZ10,
        VectorTileFixture.NewYorkZ14,
        VectorTileFixture.TokyoZ16)]
    public VectorTileFixture Fixture { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _fixture = VectorTileBenchmarkFixture.Load(Fixture);
        _renderer = new MapRenderer();
        _renderer.InitializeOffscreenForBenchmark(512, 512);
        _renderer.PrepareAndUploadVectorTileForBenchmark(
            _fixture.Features,
            _fixture.StyleAssets,
            _fixture.Id,
            _fixture.Id.Zoom,
            512,
            512);
    }

    [Benchmark]
    public long PrepareAndUploadParsedTile() =>
        _renderer.PrepareAndUploadVectorTileForBenchmark(
            _fixture.Features,
            _fixture.StyleAssets,
            _fixture.Id,
            _fixture.Id.Zoom,
            512,
            512);

    [GlobalCleanup]
    public void Cleanup() => _renderer.Dispose();
}
