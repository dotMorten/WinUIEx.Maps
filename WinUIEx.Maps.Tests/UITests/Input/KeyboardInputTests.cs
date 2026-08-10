using WinUIEx.Maps.Tests.Input;
using Microsoft.UI.Xaml;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Devices.Geolocation;
using Windows.System;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests.UITests.Input;

[TestClass]
[DoNotParallelize]
public sealed class KeyboardInputTests
{
    [TestMethod]
    [DataRow(VirtualKey.Left, -1, 0)]
    [DataRow(VirtualKey.Right, 1, 0)]
    [DataRow(VirtualKey.Up, 0, 1)]
    [DataRow(VirtualKey.Down, 0, -1)]
    public Task ArrowKey_PansMap(
        VirtualKey key,
        int longitudeDirection,
        int latitudeDirection) =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            await MapControlTestUtilities.SetupMapAsync(map);
            UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            input.Mouse.Click();
            await MapControlTestUtilities.WaitForAsync(
                () => map.FocusState == FocusState.Pointer);
            BasicGeoposition initialCenter = map.Center!.Position;
            double distance = Math.Min(map.ActualWidth, map.ActualHeight) / 2;
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
}
