using System.Text;
using WinUIEx.Maps.Tests.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Devices.Geolocation;
using Windows.System;
using WinUIEx.Maps.Automation.Peers;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests.UITests.Input;

[TestClass]
[DoNotParallelize]
public sealed class KeyboardInputTests
{
    [TestMethod]
    public Task ClickingMapEnablesKeyboardWithoutShowingFocusRing() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
            {
                await MapControlTestUtilities.SetupMapAsync(map);
                UiInputInjector input =
                    UiInputInjector.ForElement(MapControlTestHost.Window, map);

                input.Mouse.Click();

                await MapControlTestUtilities.WaitForAsync(() =>
                    ReferenceEquals(
                        FocusManager.GetFocusedElement(map.XamlRoot),
                        map));
                Border focusVisual = FindDescendant<Border>(
                    map,
                    "FocusVisual");
                Assert.AreEqual(Visibility.Collapsed, focusVisual.Visibility);
                BasicGeoposition initialCenter = map.Center!.Position;

                input.Keyboard.Press(VirtualKey.Right);

                await MapControlTestUtilities.WaitForAsync(
                    () => map.Center!.Position != initialCenter);
            });

    [TestMethod]
    public Task TappingMapEnablesKeyboardWithoutShowingFocusRing() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
            {
                await MapControlTestUtilities.SetupMapAsync(map);
                UiInputInjector input =
                    UiInputInjector.ForElement(MapControlTestHost.Window, map);

                await input.Touch.TapAsync();

                await MapControlTestUtilities.WaitForAsync(() =>
                    ReferenceEquals(
                        FocusManager.GetFocusedElement(map.XamlRoot),
                        map));
                Border focusVisual = FindDescendant<Border>(
                    map,
                    "FocusVisual");
                Assert.AreEqual(Visibility.Collapsed, focusVisual.Visibility);
                double initialZoom = map.ZoomLevel;

                input.Keyboard.Press(VirtualKey.Add);

                await MapControlTestUtilities.WaitForAsync(
                    () => map.ZoomLevel > initialZoom);
            });

    [TestMethod]
    [DataRow(VirtualKey.Left, -1, 0)]
    [DataRow(VirtualKey.Right, 1, 0)]
    [DataRow(VirtualKey.Up, 0, 1)]
    [DataRow(VirtualKey.Down, 0, -1)]
    public Task ArrowKey_PansMap(
        VirtualKey key,
        int longitudeDirection,
        int latitudeDirection) =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
        {
            await MapControlTestUtilities.SetupMapAsync(map);
            UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            Assert.IsTrue(map.Focus(FocusState.Keyboard));
            BasicGeoposition initialCenter = map.Center!.Position;
            const double distance = 100;
            BasicGeoposition expectedCenter = MapControlTestUtilities.PanByPixels(
                initialCenter,
                map.ZoomLevel,
                -longitudeDirection * distance,
                latitudeDirection * distance);

            input.Keyboard.Press(key);

            await MapControlTestUtilities.WaitForAsync(
                () => map.Center!.Position != initialCenter);
            BasicGeoposition finalCenter = map.Center!.Position;
            Assert.AreEqual(
                expectedCenter.Longitude,
                finalCenter.Longitude,
                MapControlTestUtilities.CoordinateTolerance);
            Assert.AreEqual(
                expectedCenter.Latitude,
                finalCenter.Latitude,
                MapControlTestUtilities.CoordinateTolerance);
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(
                map,
                expectedCenter,
                map.ZoomLevel);
        });

    [TestMethod]
    [DataRow(VirtualKey.Left, 75, 30)]
    [DataRow(VirtualKey.Right, 105, 30)]
    [DataRow(VirtualKey.Up, 90, 40)]
    [DataRow(VirtualKey.Down, 90, 20)]
    public Task ShiftArrow_RotatesOrPitchesMap(
        VirtualKey key,
        double expectedHeading,
        double expectedPitch) =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
            {
                Assert.IsTrue(await map.TrySetViewAsync(
                    map.Center!,
                    map.ZoomLevel,
                    90,
                    30,
                    MapAnimationKind.None));
                UiInputInjector input =
                    UiInputInjector.ForElement(MapControlTestHost.Window, map);
                Assert.IsTrue(map.Focus(FocusState.Keyboard));

                input.Keyboard.Press(key, VirtualKey.LeftShift);

                await MapControlTestUtilities.WaitForAsync(() =>
                    Math.Abs(map.Heading - expectedHeading) < 0.000001 &&
                    Math.Abs(map.Pitch - expectedPitch) < 0.000001);
                await MapControlTestUtilities.WaitForAsync(() =>
                    map.TryGetDisplayedCamera(
                        out _,
                        out _,
                        out double heading,
                        out double pitch) &&
                    Math.Abs(heading - expectedHeading) < 0.000001 &&
                    Math.Abs(pitch - expectedPitch) < 0.000001);
            });

    [TestMethod]
    [DataRow(VirtualKey.Right, true)]
    [DataRow(VirtualKey.Up, false)]
    public Task HeldShiftArrowMovesContinuouslyAndCommitsOnRelease(
        VirtualKey key,
        bool rotates) =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
            {
                Assert.IsTrue(await map.TrySetViewAsync(
                    map.Center!,
                    map.ZoomLevel,
                    90,
                    30,
                    MapAnimationKind.None));
                Assert.IsTrue(map.Focus(FocusState.Keyboard));
                UiInputInjector input =
                    UiInputInjector.ForElement(MapControlTestHost.Window, map);

                input.Keyboard.KeyDown(key, VirtualKey.LeftShift);
                try
                {
                    await MapControlTestUtilities.WaitForAsync(() =>
                        map.TryGetDisplayedCamera(
                            out _,
                            out _,
                            out double heading,
                            out double pitch) &&
                        (rotates ? heading > 92 : pitch > 32));
                    Assert.AreEqual(90, map.Heading, 0.000001);
                    Assert.AreEqual(30, map.Pitch, 0.000001);
                }
                finally
                {
                    input.Keyboard.KeyUp(key, VirtualKey.LeftShift);
                }

                await MapControlTestUtilities.WaitForAsync(() =>
                    (rotates ? map.Heading > 92 : map.Pitch > 32));
                Assert.IsTrue(map.TryGetDisplayedCamera(
                    out _,
                    out _,
                    out double finalHeading,
                    out double finalPitch));
                Assert.AreEqual(map.Heading, finalHeading, 0.25);
                Assert.AreEqual(map.Pitch, finalPitch, 0.25);
            });

    [TestMethod]
    public Task AzureZoomKeyAliasesChangeOneLevel() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
            {
                UiInputInjector input =
                    UiInputInjector.ForElement(MapControlTestHost.Window, map);
                Assert.IsTrue(map.Focus(FocusState.Keyboard));

                await PressAndWaitAsync(input, map, (VirtualKey)187, 6);
                await PressAndWaitAsync(
                    input,
                    map,
                    (VirtualKey)187,
                    7,
                    VirtualKey.LeftShift);
                await PressAndWaitAsync(input, map, VirtualKey.Add, 8);
                await PressAndWaitAsync(input, map, (VirtualKey)189, 7);
                await PressAndWaitAsync(
                    input,
                    map,
                    (VirtualKey)189,
                    6,
                    VirtualKey.LeftShift);
                await PressAndWaitAsync(input, map, VirtualKey.Subtract, 5);
            });

    [TestMethod]
    public Task DescriptionDetailShortcutsToggleDetailedMapState()
    {
        TileId tile = new(5, 10, 12);
        TestVectorTileSource source = CreateAccessibleVectorSource(tile);
        return MapControlTestHost.LoadMapControlAsync(
            source.TileCenter,
            tile.Zoom,
            async map =>
            {
                map.Layers.Add(new TestVectorTileLayer(source));
                using CancellationTokenSource timeout =
                    new(TimeSpan.FromSeconds(10));
                _ = await map.CaptureRenderedFrameAsync(timeout.Token);
                MapControlAutomationPeer peer = CreatePeer(map);
                await MapControlTestUtilities.WaitForAsync(() =>
                    peer.GetFullDescription() == "Map showing Accessible City.");
                UiInputInjector input =
                    UiInputInjector.ForElement(MapControlTestHost.Window, map);
                Assert.IsTrue(map.Focus(FocusState.Keyboard));

                input.Keyboard.Press(
                    VirtualKey.D,
                    VirtualKey.LeftControl,
                    VirtualKey.LeftMenu);

                await MapControlTestUtilities.WaitForAsync(() =>
                    peer.GetFullDescription().Contains(
                        "Zoom level 5",
                        StringComparison.Ordinal));

                input.Keyboard.Press(
                    VirtualKey.D,
                    VirtualKey.LeftControl,
                    VirtualKey.LeftShift);

                await MapControlTestUtilities.WaitForAsync(() =>
                    peer.GetFullDescription() == "Map showing Accessible City.");
            });
    }

    [TestMethod]
    public Task EscapeRestoresKeyboardFocusToMap() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
            {
                Assert.IsTrue(map.Focus(FocusState.Programmatic));
                UiInputInjector input =
                    UiInputInjector.ForElement(MapControlTestHost.Window, map);

                input.Keyboard.Press(VirtualKey.Escape);

                await MapControlTestUtilities.WaitForAsync(
                    () => map.FocusState == FocusState.Keyboard);
            });

    private static async Task PressAndWaitAsync(
        UiInputInjector input,
        MapControl map,
        VirtualKey key,
        double expectedZoom,
        params VirtualKey[] modifiers)
    {
        input.Keyboard.Press(key, modifiers);
        await MapControlTestUtilities.WaitForAsync(
            () => Math.Abs(map.ZoomLevel - expectedZoom) < 0.000001);
        await MapControlTestUtilities.WaitForDisplayedZoomAsync(
            map,
            expectedZoom);
    }

    private static MapControlAutomationPeer CreatePeer(MapControl map) =>
        (MapControlAutomationPeer)(
            FrameworkElementAutomationPeer.FromElement(map) ??
            FrameworkElementAutomationPeer.CreatePeerForElement(map)!);

    private static TestVectorTileSource CreateAccessibleVectorSource(TileId tile)
    {
        AzureVectorStyleAssets assets = AzureVectorStyleAssets.CreateForTest(
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
        return new TestVectorTileSource(
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
    }

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
}
