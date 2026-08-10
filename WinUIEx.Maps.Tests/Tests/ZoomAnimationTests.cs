using WinUIEx.Maps.Rendering;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class ZoomAnimationTests
{
    [TestMethod]
    public void RetargetingStartsFromCurrentDisplayedZoom()
    {
        ZoomAnimation animation = new();
        animation.Reset(4);
        long start = Stopwatch.GetTimestamp();

        animation.SetTarget(4, 5, start);
        double intermediate = animation.GetZoom(start + (Stopwatch.Frequency / 10));
        animation.SetTarget(intermediate, 6, start + (Stopwatch.Frequency / 10));

        Assert.AreEqual(6, animation.TargetZoom);
        Assert.IsInRange(4, 5, intermediate);
    }

    [TestMethod]
    public void EaseOutStartsFastAndSettlesExactlyAtTarget()
    {
        ZoomAnimation animation = new();
        animation.Reset(3);
        long start = Stopwatch.GetTimestamp();
        animation.SetTarget(3, 4, start);

        double early = animation.GetZoom(start + (long)(Stopwatch.Frequency * 0.07));
        double late = animation.GetZoom(start + (long)(Stopwatch.Frequency * 0.21));
        double completed = animation.GetZoom(start + Stopwatch.Frequency);

        Assert.IsTrue(early - 3 > 4 - late);
        Assert.AreEqual(4, completed);
        Assert.IsFalse(animation.IsActive);
    }

    [TestMethod]
    public void TargetIsClampedToSupportedZoomRange()
    {
        ZoomAnimation animation = new();
        animation.Reset(MapCamera.MaximumTileZoom);

        animation.SetTarget(
            MapCamera.MaximumTileZoom,
            MapCamera.MaximumTileZoom + 3,
            Stopwatch.GetTimestamp());

        Assert.AreEqual(MapCamera.MaximumTileZoom, animation.TargetZoom);
        Assert.IsFalse(animation.IsActive);
    }
}
