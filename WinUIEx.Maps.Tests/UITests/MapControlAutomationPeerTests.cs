using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinUIEx.Maps.Automation.Peers;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;
using Windows.Devices.Geolocation;

namespace WinUIEx.Maps.Tests.UITests;

[TestClass]
[DoNotParallelize]
public sealed class MapControlAutomationPeerTests
{
    private static readonly BasicGeoposition InitialCenter = new()
    {
        Latitude = 20,
        Longitude = 30,
    };

    [TestMethod]
    public Task ExposesScrollTransformAndTransform2Patterns() =>
        MapControlTestHost.LoadMapControlAsync(
            InitialCenter,
            5,
            map =>
            {
                MapControlAutomationPeer peer = CreatePeer(map);

                Assert.AreSame(peer, peer.GetPattern(PatternInterface.Scroll));
                Assert.AreSame(peer, peer.GetPattern(PatternInterface.Transform));
                Assert.AreSame(peer, peer.GetPattern(PatternInterface.Transform2));
                Assert.AreEqual(nameof(MapControl), peer.GetClassName());
                Assert.AreEqual("Map", peer.GetName());
                Assert.AreEqual("Interactive map.", peer.GetFullDescription());
                Assert.Contains("arrow keys", peer.GetHelpText());
                Assert.IsTrue(peer.CanMove);
                Assert.IsTrue(peer.CanResize);
                Assert.IsTrue(peer.CanRotate);
                Assert.IsTrue(peer.CanZoom);
                Assert.AreEqual(0, peer.MinZoom);
                Assert.AreEqual(MapCamera.MaximumTileZoom, peer.MaxZoom);
                Assert.AreEqual(5, peer.ZoomLevel, 0.000001);
                Assert.IsTrue(peer.HorizontallyScrollable);
                Assert.IsTrue(peer.VerticallyScrollable);
                Assert.IsInRange(0, 100, peer.HorizontalScrollPercent);
                Assert.IsInRange(0, 100, peer.VerticalScrollPercent);
                Assert.IsInRange(0, 100, peer.HorizontalViewSize);
                Assert.IsInRange(0, 100, peer.VerticalViewSize);
                return Task.CompletedTask;
            });

    [TestMethod]
    public Task ApplicationAutomationPropertiesOverridePeerDefaults() =>
        MapControlTestHost.LoadMapControlAsync(
            InitialCenter,
            5,
            map =>
            {
                AutomationProperties.SetName(map, "Store locator");
                AutomationProperties.SetHelpText(map, "Choose a nearby store.");
                AutomationProperties.SetFullDescription(
                    map,
                    "A map of stores near the selected address.");
                MapControlAutomationPeer peer = CreatePeer(map);

                Assert.AreEqual("Store locator", peer.GetName());
                Assert.AreEqual("Choose a nearby store.", peer.GetHelpText());
                Assert.AreEqual(
                    "A map of stores near the selected address.",
                    peer.GetFullDescription());
                return Task.CompletedTask;
            });

    [TestMethod]
    public Task VisibleVectorLabelsUpdateMapStateAfterTheSceneSettles()
    {
        TileId tile = new(5, 10, 12);
        VectorStyleAssets assets = VectorStyleAssets.CreateForTest(
            MapStyle.BlankAccessible,
            Encoding.UTF8.GetBytes(
                """
                {
                  "version": 8,
                  "layers": [{
                    "type": "symbol",
                    "source-layer": "place",
                    "layout": { "text-field": ["get", "name"] }
                  }]
                }
                """),
            Encoding.UTF8.GetBytes("{}"),
            [0, 0, 0, 0],
            1,
            1);
        TestVectorTileSource source = new(
            tile,
            new MapboxVectorTileBuilder()
                .AddPoint(
                    "place",
                    2048,
                    2048,
                    new Dictionary<string, object>
                    {
                        ["name"] = "Accessible City",
                    })
                .Build(),
            assets);

        return MapControlTestHost.LoadMapControlAsync(
            source.TileCenter,
            tile.Zoom,
            async map =>
            {
                map.Layers.Add(new TestVectorTileLayer(source));
                using CancellationTokenSource timeout =
                    new(TimeSpan.FromSeconds(10));
                _ = await map.CaptureRenderedFrameAsync(timeout.Token);
                TextBlock mapState = FindDescendant<TextBlock>(
                    map,
                    "PART_MapState");
                await MapControlTestUtilities.WaitForAsync(() =>
                    mapState.Text == "Map showing Accessible City.");

                MapControlAutomationPeer peer = CreatePeer(map);
                Assert.AreEqual(
                    "Map showing Accessible City.",
                    peer.GetFullDescription());
            });
    }

    [TestMethod]
    public Task ScrollMovesByLogicalViewportAmountsWithoutAnimation() =>
        MapControlTestHost.LoadMapControlAsync(
            InitialCenter,
            5,
            async map =>
            {
                MapControlAutomationPeer peer = CreatePeer(map);
                double worldSize = 256 * Math.Pow(2, 5);
                double expectedLongitude = InitialCenter.Longitude +
                    ((map.ActualWidth * 0.1) / worldSize * 360);
                double expectedLatitude = MapCamera.WorldYToLatitude(
                    MapCamera.LatitudeToWorldY(InitialCenter.Latitude) -
                    (map.ActualHeight / worldSize));
                var expected = new MapCenter(
                    expectedLongitude,
                    expectedLatitude);

                peer.Scroll(
                    ScrollAmount.SmallIncrement,
                    ScrollAmount.LargeDecrement);

                AssertCenter(map, expected);
                await MapControlTestUtilities.WaitForDisplayedCameraAsync(
                    map,
                    new BasicGeoposition
                    {
                        Longitude = expected.Longitude,
                        Latitude = expected.Latitude,
                    },
                    5);
                AssertDisplayed(map, expected, 5, 0);
            });

    [TestMethod]
    public Task SetScrollPercentChangesBothAxesAndHonorsNoScroll() =>
        MapControlTestHost.LoadMapControlAsync(
            InitialCenter,
            5,
            async map =>
            {
                MapControlAutomationPeer peer = CreatePeer(map);
                double expectedLatitude = MapCamera.WorldYToLatitude(0.25);

                peer.SetScrollPercent(75, 25);

                Assert.AreEqual(90, map.Center!.Position.Longitude, 0.000001);
                Assert.AreEqual(
                    expectedLatitude,
                    map.Center.Position.Latitude,
                    0.000001);
                peer.SetScrollPercent(
                    ScrollPatternIdentifiers.NoScroll,
                    50);
                Assert.AreEqual(90, map.Center.Position.Longitude, 0.000001);
                Assert.AreEqual(0, map.Center.Position.Latitude, 0.000001);
                await MapControlTestUtilities.WaitForDisplayedCameraAsync(
                    map,
                    map.Center.Position,
                    5);
            });

    [TestMethod]
    public Task MoveAndRotateUseRelativeImmediateCameraChanges() =>
        MapControlTestHost.LoadMapControlAsync(
            InitialCenter,
            5,
            async map =>
            {
                MapControlAutomationPeer peer = CreatePeer(map);
                double worldSize = 256 * Math.Pow(2, 5);
                double expectedLongitude = InitialCenter.Longitude -
                    (40 / worldSize * 360);
                double expectedLatitude = MapCamera.WorldYToLatitude(
                    MapCamera.LatitudeToWorldY(InitialCenter.Latitude) +
                    (20 / worldSize));
                var expected = new MapCenter(
                    expectedLongitude,
                    expectedLatitude);

                peer.Move(40, -20);
                peer.Rotate(450);

                AssertCenter(map, expected);
                Assert.AreEqual(90, map.Heading, 0.000001);
                await MapControlTestUtilities.WaitForAsync(
                    () => map.TryGetDisplayedCamera(
                        out BasicGeoposition center,
                        out double zoom,
                        out double heading,
                        out _) &&
                        Math.Abs(center.Longitude - expected.Longitude) < 0.000001 &&
                        Math.Abs(center.Latitude - expected.Latitude) < 0.000001 &&
                        Math.Abs(zoom - 5) < 0.000001 &&
                        Math.Abs(heading - 90) < 0.000001);
                AssertDisplayed(map, expected, 5, 90);
            });

    [TestMethod]
    public Task ZoomAndZoomByUnitUseMapZoomLevels() =>
        MapControlTestHost.LoadMapControlAsync(
            InitialCenter,
            5,
            async map =>
            {
                MapControlAutomationPeer peer = CreatePeer(map);

                peer.Zoom(8.5);
                Assert.AreEqual(8.5, map.ZoomLevel, 0.000001);
                peer.ZoomByUnit(ZoomUnit.SmallIncrement);
                Assert.AreEqual(9.5, map.ZoomLevel, 0.000001);
                peer.ZoomByUnit(ZoomUnit.LargeDecrement);
                Assert.AreEqual(4.5, map.ZoomLevel, 0.000001);
                peer.ZoomByUnit(ZoomUnit.NoAmount);
                Assert.AreEqual(4.5, map.ZoomLevel, 0.000001);
                peer.Zoom(-100);
                Assert.AreEqual(peer.MinZoom, map.ZoomLevel, 0.000001);
                peer.Zoom(100);
                Assert.AreEqual(peer.MaxZoom, map.ZoomLevel, 0.000001);
                await MapControlTestUtilities.WaitForDisplayedZoomAsync(
                    map,
                    peer.MaxZoom);
            });

    [TestMethod]
    public Task ResizeChangesTheAssociatedMapDimensions() =>
        MapControlTestHost.LoadMapControlAsync(
            InitialCenter,
            5,
            async map =>
            {
                MapControlAutomationPeer peer = CreatePeer(map);

                peer.Resize(640, 360);

                await MapControlTestUtilities.WaitForAsync(
                    () => Math.Abs(map.ActualWidth - 640) < 0.01 &&
                        Math.Abs(map.ActualHeight - 360) < 0.01);
                Assert.AreEqual(640, map.Width);
                Assert.AreEqual(360, map.Height);
            });

    [TestMethod]
    public Task ProviderMethodsValidateArgumentsAndEnabledState() =>
        MapControlTestHost.LoadMapControlAsync(
            InitialCenter,
            5,
            map =>
            {
                MapControlAutomationPeer peer = CreatePeer(map);

                Assert.Throws<ArgumentOutOfRangeException>(
                    () => peer.SetScrollPercent(-2, 50));
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => peer.SetScrollPercent(50, double.NaN));
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => peer.Move(double.PositiveInfinity, 0));
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => peer.Resize(0, 100));
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => peer.Rotate(double.NaN));
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => peer.Zoom(double.NaN));
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => peer.ZoomByUnit((ZoomUnit)int.MaxValue));

                map.IsEnabled = false;
                Assert.Throws<InvalidOperationException>(
                    () => peer.Scroll(
                        ScrollAmount.NoAmount,
                        ScrollAmount.NoAmount));
                Assert.Throws<InvalidOperationException>(() => peer.Zoom(5));
                return Task.CompletedTask;
            });

    private static MapControlAutomationPeer CreatePeer(MapControl map) =>
        (MapControlAutomationPeer)(
            FrameworkElementAutomationPeer.FromElement(map) ??
            FrameworkElementAutomationPeer.CreatePeerForElement(map)!);

    private static T FindDescendant<T>(
        DependencyObject root,
        string name)
        where T : FrameworkElement
    {
        if (root is T match &&
            string.Equals(match.Name, name, StringComparison.Ordinal))
        {
            return match;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            try
            {
                return FindDescendant<T>(
                    VisualTreeHelper.GetChild(root, index),
                    name);
            }
            catch (AssertFailedException)
            {
            }
        }

        throw new AssertFailedException(
            $"Could not find {typeof(T).Name} named {name}.");
    }

    private static void AssertCenter(MapControl map, MapCenter expected)
    {
        Assert.AreEqual(
            expected.Longitude,
            map.Center!.Position.Longitude,
            0.000001);
        Assert.AreEqual(
            expected.Latitude,
            map.Center.Position.Latitude,
            0.000001);
    }

    private static void AssertDisplayed(
        MapControl map,
        MapCenter expected,
        double zoom,
        double heading)
    {
        Assert.IsTrue(map.TryGetDisplayedCamera(
            out BasicGeoposition center,
            out double displayedZoom,
            out double displayedHeading,
            out _));
        Assert.AreEqual(expected.Longitude, center.Longitude, 0.000001);
        Assert.AreEqual(expected.Latitude, center.Latitude, 0.000001);
        Assert.AreEqual(zoom, displayedZoom, 0.000001);
        Assert.AreEqual(heading, displayedHeading, 0.000001);
    }
}
