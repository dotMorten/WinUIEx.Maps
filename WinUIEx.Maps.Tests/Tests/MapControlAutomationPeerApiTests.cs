using Microsoft.UI.Xaml.Automation.Provider;
using WinUIEx.Maps.Automation.Peers;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests.Tests;

[TestClass]
public sealed class MapControlAutomationPeerApiTests
{
    [TestMethod]
    public void PeerMatchesUwpPublicProviderSurface()
    {
        Type type = typeof(MapControlAutomationPeer);

        Assert.IsTrue(type.IsPublic);
        Assert.IsTrue(type.IsSealed);
        Assert.IsTrue(typeof(IScrollProvider).IsAssignableFrom(type));
        Assert.IsTrue(typeof(ITransformProvider).IsAssignableFrom(type));
        Assert.IsTrue(typeof(ITransformProvider2).IsAssignableFrom(type));
        Assert.IsNotNull(type.GetConstructor([typeof(MapControl)]));

        string[] properties =
        [
            nameof(MapControlAutomationPeer.HorizontallyScrollable),
            nameof(MapControlAutomationPeer.HorizontalScrollPercent),
            nameof(MapControlAutomationPeer.HorizontalViewSize),
            nameof(MapControlAutomationPeer.VerticallyScrollable),
            nameof(MapControlAutomationPeer.VerticalScrollPercent),
            nameof(MapControlAutomationPeer.VerticalViewSize),
            nameof(MapControlAutomationPeer.CanMove),
            nameof(MapControlAutomationPeer.CanResize),
            nameof(MapControlAutomationPeer.CanRotate),
            nameof(MapControlAutomationPeer.CanZoom),
            nameof(MapControlAutomationPeer.MinZoom),
            nameof(MapControlAutomationPeer.MaxZoom),
            nameof(MapControlAutomationPeer.ZoomLevel),
        ];
        foreach (string property in properties)
        {
            Assert.IsNotNull(type.GetProperty(property), property);
        }

        Assert.IsNotNull(type.GetMethod(
            nameof(MapControlAutomationPeer.Scroll),
            [
                typeof(Microsoft.UI.Xaml.Automation.ScrollAmount),
                typeof(Microsoft.UI.Xaml.Automation.ScrollAmount),
            ]));
        Assert.IsNotNull(type.GetMethod(
            nameof(MapControlAutomationPeer.SetScrollPercent),
            [typeof(double), typeof(double)]));
        Assert.IsNotNull(type.GetMethod(
            nameof(MapControlAutomationPeer.Move),
            [typeof(double), typeof(double)]));
        Assert.IsNotNull(type.GetMethod(
            nameof(MapControlAutomationPeer.Resize),
            [typeof(double), typeof(double)]));
        Assert.IsNotNull(type.GetMethod(
            nameof(MapControlAutomationPeer.Rotate),
            [typeof(double)]));
        Assert.IsNotNull(type.GetMethod(
            nameof(MapControlAutomationPeer.Zoom),
            [typeof(double)]));
        Assert.IsNotNull(type.GetMethod(
            nameof(MapControlAutomationPeer.ZoomByUnit),
            [typeof(Microsoft.UI.Xaml.Automation.ZoomUnit)]));
    }

    [TestMethod]
    public void ViewSizePercentagesAccountForZoomHeadingAndPitch()
    {
        MapControlAutomationPeer.GetViewSizePercentages(
            4,
            512,
            256,
            0,
            0,
            out double horizontal,
            out double vertical);

        Assert.AreEqual(12.5, horizontal, 0.000001);
        Assert.AreEqual(6.25, vertical, 0.000001);

        MapControlAutomationPeer.GetViewSizePercentages(
            4,
            512,
            256,
            90,
            0,
            out double rotatedHorizontal,
            out double rotatedVertical);

        Assert.AreEqual(vertical, rotatedHorizontal, 0.000001);
        Assert.AreEqual(horizontal, rotatedVertical, 0.000001);

        MapControlAutomationPeer.GetViewSizePercentages(
            4,
            512,
            256,
            0,
            45,
            out _,
            out double pitchedVertical);

        Assert.IsGreaterThan(vertical, pitchedVertical);
    }

    [TestMethod]
    public void AccessibilityDescriptionIsConciseAndBounded()
    {
        MapAccessibilityFeature[] features =
        [
            CreateFeature("Seattle"),
            CreateFeature("Washington"),
            CreateFeature("Puget Sound"),
            CreateFeature("Lake Washington"),
            CreateFeature("Bellevue"),
            CreateFeature("Ignored"),
        ];

        Assert.AreEqual(
            "Map showing Seattle, Washington, Puget Sound, Lake Washington, and Bellevue.",
            MapControl.CreateAccessibilityDescription(features));
        Assert.AreEqual(
            string.Empty,
            MapControl.CreateAccessibilityDescription([]));
    }

    private static MapAccessibilityFeature CreateFeature(string name) =>
        new(
            name,
            MapAccessibilityFeatureKind.Other,
            0,
            0,
            0,
            0);
}
