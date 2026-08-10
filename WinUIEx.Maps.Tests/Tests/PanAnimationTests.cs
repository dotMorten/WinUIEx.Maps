using WinUIEx.Maps.Rendering;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class PanAnimationTests
{
    [TestMethod]
    public void PixelDragMovesCenterInOppositeDirectionAtCurrentScale()
    {
        MapCenter target = MapCamera.PanByPixels(0, 0, 2, 256, 256);

        Assert.AreEqual(Math.Round((double)(-90), 6), Math.Round((double)(target.Longitude), 6));
        Assert.IsInRange(66.5, 66.6, target.Latitude);
    }

    [TestMethod]
    public void PixelDragWrapsLongitudeAndClampsLatitude()
    {
        MapCenter target = MapCamera.PanByPixels(179, 84, 3, -4096, 4096);

        Assert.IsInRange(-180, 180, target.Longitude);
        Assert.AreEqual(Math.Round((double)(MapCamera.MaximumLatitude), 6), Math.Round((double)(target.Latitude), 6));
    }

    [TestMethod]
    public void DatelineTweenUsesShortestWrappedPath()
    {
        PanAnimation animation = new();
        animation.Reset(179, 0);
        long start = Stopwatch.GetTimestamp();
        animation.SetTarget(179, 0, -179, 0, start);

        MapCenter midpoint = animation.GetCenter(start + (long)(Stopwatch.Frequency * 0.13));

        Assert.IsTrue(Math.Abs(midpoint.Longitude) > 179);
    }

    [TestMethod]
    public void PanEaseOutSettlesExactlyAtTarget()
    {
        PanAnimation animation = new();
        animation.Reset(0, 0);
        long start = Stopwatch.GetTimestamp();
        animation.SetTarget(0, 0, 20, 10, start);

        MapCenter early = animation.GetCenter(start + (long)(Stopwatch.Frequency * 0.065));
        MapCenter late = animation.GetCenter(start + (long)(Stopwatch.Frequency * 0.195));
        MapCenter completed = animation.GetCenter(start + Stopwatch.Frequency);

        Assert.IsTrue(early.Longitude > 20 - late.Longitude);
        Assert.AreEqual(Math.Round((double)(20), 6), Math.Round((double)(completed.Longitude), 6));
        Assert.AreEqual(Math.Round((double)(10), 6), Math.Round((double)(completed.Latitude), 6));
        Assert.IsFalse(animation.IsActive);
    }

    [TestMethod]
    public void PanEaseOutIsContinuousAtCompletion()
    {
        PanAnimation animation = new();
        animation.Reset(0, 0);
        long start = Stopwatch.GetTimestamp();
        animation.SetTarget(0, 0, 20, 10, start);

        MapCenter justBeforeCompletion = animation.GetCenter(
            start + (long)(Stopwatch.Frequency * 0.499));
        MapCenter completed = animation.GetCenter(
            start + (long)(Stopwatch.Frequency * 0.5));

        Assert.IsInRange(0, 0.000001, Math.Abs(completed.Longitude - justBeforeCompletion.Longitude));
        Assert.IsInRange(0, 0.000001, Math.Abs(completed.Latitude - justBeforeCompletion.Latitude));
        Assert.AreEqual(Math.Round((double)(20), 10), Math.Round((double)(completed.Longitude), 10));
        Assert.AreEqual(Math.Round((double)(10), 10), Math.Round((double)(completed.Latitude), 10));
        Assert.IsFalse(animation.IsActive);
    }

    [TestMethod]
    public void RetargetingAdvancesExistingTweenBeforeChangingTarget()
    {
        PanAnimation animation = new();
        animation.Reset(0, 0);
        long start = Stopwatch.GetTimestamp();
        animation.SetTarget(0, 0, 20, 0, start);
        long retargetTimestamp = start + (long)(Stopwatch.Frequency * 0.1);

        animation.SetTarget(0, 0, 40, 0, retargetTimestamp);
        MapCenter retargeted = animation.GetCenter(retargetTimestamp);

        Assert.IsInRange(9.7, 9.8, retargeted.Longitude);
        Assert.AreEqual(40, animation.Target.Longitude);
        Assert.IsTrue(animation.IsActive);
    }

    [TestMethod]
    public void RetargetingDuringDragKeepsAdvancingDisplayedCenter()
    {
        PanAnimation animation = new();
        animation.Reset(0, 0);
        long start = Stopwatch.GetTimestamp();
        MapCenter displayed = new(0, 0);

        for (int frame = 1; frame <= 5; frame++)
        {
            long timestamp = start + (long)(Stopwatch.Frequency * frame * 0.016);
            animation.SetTarget(
                displayed.Longitude,
                displayed.Latitude,
                frame * 2,
                0,
                timestamp);
            displayed = animation.GetCenter(timestamp);
        }

        Assert.IsTrue(displayed.Longitude > 0);
        Assert.IsTrue(animation.IsActive);
        Assert.AreEqual(10, animation.Target.Longitude);
    }
}
