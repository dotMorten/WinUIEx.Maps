using BenchmarkDotNet.Attributes;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
[BenchmarkCategory("CPU", "VectorTiles")]
public class VectorTileParsingBenchmarks
{
    private byte[] _tile = null!;

    [Params(
        VectorTileFixture.NewYorkZ10,
        VectorTileFixture.SeattleZ12,
        VectorTileFixture.NewYorkZ14,
        VectorTileFixture.TokyoZ16)]
    public VectorTileFixture Fixture { get; set; }

    [GlobalSetup]
    public void Setup() =>
        _tile = VectorTileBenchmarkFixture.LoadEncoded(Fixture);

    [Benchmark]
    public int Parse()
    {
        VectorTileFeatureCollection features = VectorTileDecoder.Decode(_tile);
        return features.Features.Length;
    }
}

public enum VectorTileFixture
{
    NewYorkZ10,
    SeattleZ12,
    NewYorkZ14,
    TokyoZ16,
}
