using WinUIEx.Maps.Rendering;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class KeyboardNavigationStateTests
{
    [TestMethod]
    public void EmptyStateHasNoInput()
    {
        KeyboardNavigationState state = new(0, 0, 0, 0);

        Assert.IsFalse(state.HasInput);
    }

    [TestMethod]
    [DataRow(1, 0, 0)]
    [DataRow(0, -1, 0)]
    [DataRow(0, 0, 1)]
    public void DirectionOrZoomMakesStateActive(int horizontal, int vertical, int zoom)
    {
        KeyboardNavigationState state = new(horizontal, vertical, zoom, 1);

        Assert.IsTrue(state.HasInput);
    }

    [TestMethod]
    public void HoldThresholdIsQuarterSecond()
    {
        Assert.AreEqual(TimeSpan.FromMilliseconds(250), KeyboardNavigationState.HoldThreshold);
    }
}
