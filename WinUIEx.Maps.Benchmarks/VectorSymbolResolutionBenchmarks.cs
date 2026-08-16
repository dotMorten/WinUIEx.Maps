using BenchmarkDotNet.Attributes;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
[BenchmarkCategory("CPU", "Symbols", "VectorTiles")]
public class VectorSymbolResolutionBenchmarks
{
    private VectorSymbolBenchmarkFixture _fixture = null!;

    [Params(64, 256)]
    public int SymbolCount { get; set; }

    [GlobalSetup]
    public void Setup() =>
        _fixture = VectorSymbolBenchmarkFixture.Create(SymbolCount);

    [Benchmark]
    public int ResolveGlyphsAndSprites()
    {
        VectorSymbolResolution resolution =
            _fixture.StyleAssets.ResolveSymbols(_fixture.Features, 14);
        return resolution.Symbols.Length +
            resolution.ResolvedGlyphCount +
            resolution.UnavailableGlyphCount +
            resolution.UnavailableSpriteCount +
            resolution.EvaluationFailureCount;
    }
}
