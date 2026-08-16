using BenchmarkDotNet.Attributes;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
[BenchmarkCategory("GPU", "Upload", "Symbols", "VectorTiles")]
public class VectorSymbolUploadBenchmarks
{
    private MapRenderer _renderer = null!;
    private VectorSpriteTextureData[] _textures = null!;

    [GlobalSetup]
    public void Setup()
    {
        _textures = VectorSymbolBenchmarkFixture.Create(256).Textures;
        _renderer = new MapRenderer();
        _renderer.InitializeOffscreenForBenchmark(1, 1);
        _renderer.UploadVectorTexturesAndWaitForGpuForBenchmark(_textures);
    }

    [Benchmark]
    public ulong UploadGeneratedGlyphAndSpriteTextures() =>
        _renderer.UploadVectorTexturesAndWaitForGpuForBenchmark(_textures);

    [GlobalCleanup]
    public void Cleanup() => _renderer.Dispose();
}
