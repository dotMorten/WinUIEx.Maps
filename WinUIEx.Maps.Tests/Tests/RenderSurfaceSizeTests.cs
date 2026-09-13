using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class RenderSurfaceSizeTests
{
    [TestMethod]
    [DataRow(1f, 801u, 601u)]
    [DataRow(1.25f, 1001u, 751u)]
    [DataRow(1.5f, 1201u, 901u)]
    [DataRow(1.75f, 1401u, 1051u)]
    [DataRow(2f, 1601u, 1201u)]
    public void FractionalExtentRoundsUpWithoutChangingLogicalCoordinates(
        float scale, uint width, uint height)
    {
        RenderSurfaceSize size = new(800.25, 600.25, scale, scale);
        Assert.AreEqual(width, size.PixelWidth);
        Assert.AreEqual(height, size.PixelHeight);
        Assert.AreEqual(800.25, size.LogicalWidth);
        Assert.AreEqual(600.25, size.LogicalHeight);
    }

    [TestMethod]
    public void ScaleOnlyChangeHasDistinctSnapshotAndIndependentAxes()
    {
        RenderSurfaceSize original = new(800, 600, 1, 1);
        RenderSurfaceSize scaled = original with { ScaleX = 1.25f, ScaleY = 1.5f };
        Assert.AreNotEqual(original, scaled);
        Assert.AreEqual(1000u, scaled.PixelWidth);
        Assert.AreEqual(900u, scaled.PixelHeight);
        Assert.AreEqual(original.LogicalWidth, scaled.LogicalWidth);
    }

    [TestMethod]
    public void EmptySurfaceStillHasNonzeroBuffers()
    {
        RenderSurfaceSize size = new(0, 0, 1, 1);
        Assert.AreEqual(1u, size.PixelWidth);
        Assert.AreEqual(1u, size.PixelHeight);
    }
}
