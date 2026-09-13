using System.Numerics;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class VectorHslColorTests
{
    [TestMethod]
    [DataRow("hsl(0, 100%, 50%)", 1d, 0d, 0d, 1d)]
    [DataRow("hsl(120, 100%, 50%)", 0d, 1d, 0d, 1d)]
    [DataRow("hsl(-120, 100%, 50%)", 0d, 0d, 1d, 1d)]
    [DataRow("HSL(420, 100%, 50%)", 1d, 1d, 0d, 1d)]
    [DataRow("hsla(60, 9%, 86%, 0.82)", 0.715532, 0.715532, 0.694868, 0.82)]
    [DataRow("hsla(60, 5%, 71%, 0.82)", 0.59409, 0.59409, 0.57031, 0.82)]
    [DataRow("hsla(0, 0%, 70%, 0.9)", 0.63, 0.63, 0.63, 0.9)]
    [DataRow("hsla(100, 0%, 100%, 0)", 0d, 0d, 0d, 0d)]
    [DataRow("hsl(100, 100%, 0%)", 0d, 0d, 0d, 1d)]
    public void CssHslColorsProducePremultipliedRgba(string text, double r, double g, double b, double a)
    {
        Assert.IsTrue(VectorTextStyleLayer.TryParseColor(VectorStyleValue.FromString(text), out Vector4 color));
        Assert.AreEqual(r, color.X, 0.000001);
        Assert.AreEqual(g, color.Y, 0.000001);
        Assert.AreEqual(b, color.Z, 0.000001);
        Assert.AreEqual(a, color.W, 0.000001);
    }

    [TestMethod]
    [DataRow("hsl(0, 50, 50%)")]
    [DataRow("hsl(0, 101%, 50%)")]
    [DataRow("hsl(0, 50%, -1%)")]
    [DataRow("hsla(0, 50%, 50%, 2)")]
    [DataRow("hsl(NaN, 50%, 50%)")]
    [DataRow("hsl(Infinity, 50%, 50%)")]
    [DataRow("hsla(0, 50%, 50%, NaN)")]
    [DataRow("rgb(NaN, 0, 0)")]
    [DataRow("rgba(0, 0, 0, NaN)")]
    public void MalformedOrNonfiniteColorsDoNotReachGpuBuffers(string text) =>
        Assert.IsFalse(VectorTextStyleLayer.TryParseColor(VectorStyleValue.FromString(text), out _));
}
