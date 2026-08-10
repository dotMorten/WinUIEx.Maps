using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class HeadingAnimationTests
{
    [TestMethod]
    public void AnimationUsesShortestPathAcrossNorth()
    {
        HeadingAnimation animation = new();
        long start = Stopwatch.GetTimestamp();
        animation.Reset(358);

        animation.SetTarget(358, 2, start);
        double intermediate =
            animation.GetHeading(start + (Stopwatch.Frequency / 10));

        Assert.IsTrue(intermediate > 358 || intermediate < 2);
        Assert.AreEqual(
            2,
            animation.GetHeading(start + Stopwatch.Frequency));
    }

    [TestMethod]
    public void HeadingIsNormalized()
    {
        HeadingAnimation animation = new();

        animation.Reset(450);

        Assert.AreEqual(90, animation.TargetHeading);
        Assert.IsFalse(animation.IsActive);
    }
}
