using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.Input;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class ConnectedComponentAnalyzerTests
{
    [TestMethod]
    public void EightConnectivityJoinsDiagonalPixelsAndReturnsBounds()
    {
        byte[] pixels = new byte[5 * 4 * 4];
        SetPixel(pixels, 5, 1, 1, 255, 0, 0);
        SetPixel(pixels, 5, 2, 2, 255, 0, 0);
        SetPixel(pixels, 5, 3, 2, 255, 0, 0);

        ConnectedComponent component = Assert.ContainsSingle(
            ConnectedComponentAnalyzer.Find(
                new MapRenderFrame(pixels, 5, 4),
                ConnectedComponentAnalyzer.Near(255, 0, 0),
                minimumPixelCount: 3));

        Assert.AreEqual(new PixelBounds(1, 1, 3, 2), component.Bounds);
        Assert.AreEqual(3, component.PixelCount);
    }

    [TestMethod]
    public void ColorToleranceAndMinimumSizeFilterNoise()
    {
        byte[] pixels = new byte[4 * 3 * 4];
        SetPixel(pixels, 4, 0, 0, 252, 3, 2);
        SetPixel(pixels, 4, 2, 1, 250, 4, 1);
        SetPixel(pixels, 4, 3, 1, 255, 0, 0);

        ConnectedComponent component = Assert.ContainsSingle(
            ConnectedComponentAnalyzer.Find(
                new ScreenshotFrame(pixels, 4, 3),
                ConnectedComponentAnalyzer.Near(
                    255,
                    0,
                    0,
                    tolerance: 5),
                minimumPixelCount: 2));

        Assert.AreEqual(new PixelBounds(2, 1, 2, 1), component.Bounds);
    }

    private static void SetPixel(
        byte[] pixels,
        int width,
        int x,
        int y,
        byte red,
        byte green,
        byte blue)
    {
        int offset = ((y * width) + x) * 4;
        pixels[offset] = blue;
        pixels[offset + 1] = green;
        pixels[offset + 2] = red;
        pixels[offset + 3] = byte.MaxValue;
    }
}
