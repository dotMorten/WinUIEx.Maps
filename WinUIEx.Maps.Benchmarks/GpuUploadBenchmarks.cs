using BenchmarkDotNet.Attributes;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
[BenchmarkCategory("GPU", "Upload")]
public class GpuUploadBenchmarks
{
    private MapRenderer _renderer = null!;
    private byte[] _pixels = null!;

    [Params(256, 512)]
    public int TextureSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _pixels = CreatePixels(TextureSize, TextureSize);
        _renderer = new MapRenderer();
        _renderer.InitializeOffscreenForBenchmark(1, 1);
        _renderer.UploadTextureAndWaitForGpuForBenchmark(
            _pixels,
            checked((uint)TextureSize),
            checked((uint)TextureSize));
    }

    [Benchmark]
    public ulong UploadTextureAndWaitForGpu() =>
        _renderer.UploadTextureAndWaitForGpuForBenchmark(
            _pixels,
            checked((uint)TextureSize),
            checked((uint)TextureSize));

    [GlobalCleanup]
    public void Cleanup() => _renderer.Dispose();

    internal static byte[] CreatePixels(int width, int height)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            int pixel = index / 4;
            pixels[index] = (byte)pixel;
            pixels[index + 1] = (byte)(pixel >> 3);
            pixels[index + 2] = (byte)(pixel >> 7);
            pixels[index + 3] = 255;
        }
        return pixels;
    }
}
