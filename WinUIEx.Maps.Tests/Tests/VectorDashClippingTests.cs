using System.Diagnostics;
using System.Collections.Immutable;
using System.Numerics;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class VectorDashClippingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(1, 0)]
    [DataRow(2, 0)]
    [DataRow(0, 1)]
    [DataRow(1, 1)]
    [DataRow(0, 2)]
    public void ExtremeOffscreenDashDistancesPreserveVisiblePhase(int cap, int pattern)
    {
        ImmutableArray<double> dashes = pattern switch
        {
            1 => [10, 0, 0, 5],
            2 => [10, 5, 0],
            _ => [10, 5],
        };
        VectorLineStyle style = new(Vector4.One, 2, (VectorLineCap)cap, VectorLineJoin.Miter, dashes);
        var expected = MapRenderer.ExpandVectorLineTriangles(
            [new(-300, 128), new(128, 128), new(128, -300), new(200, -300), new(200, 200)],
            style, 256, 256);
        Stopwatch timer = Stopwatch.StartNew();
        var actual = MapRenderer.ExpandVectorLineTriangles(
            [new(-10_000_020, 128), new(128, 128), new(128, -10_000_020),
             new(200, -10_000_020), new(200, 200)],
            style, 256, 256);
        TestContext.WriteLine($"Offscreen dash expansion: {timer.Elapsed.TotalMilliseconds:F3} ms, {actual.Length} vertices");
        Assert.IsLessThan(200d, timer.Elapsed.TotalMilliseconds,
            "Offscreen distance must not drive iteration over millions of invisible dashes.");
        Assert.AreEqual(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i].X, actual[i].X, 0.00001);
            Assert.AreEqual(expected[i].Y, actual[i].Y, 0.00001);
        }
    }
}
