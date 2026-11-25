using Microsoft.UI.Xaml;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using Windows.System;
using WinUIEx.Maps.Tests.Input;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests.UITests.Input;

[TestClass]
[DoNotParallelize]
public sealed class MapInteractionEnabledTests
{
    [TestMethod]
    public Task ViewChangedReportsDisplayedCameraOnUiThread() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter, 5, async map =>
            {
                await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, 5);
                BasicGeoposition? observed = null;
                bool onUiThread = false;
                map.ViewChanged += (_, _) =>
                {
                    onUiThread = map.DispatcherQueue.HasThreadAccess;
                    if (map.TryGetLocationFromOffset(
                        new Point(map.ActualWidth / 2, map.ActualHeight / 2), out var center))
                        observed = center.Position;
                };
                Assert.IsTrue(await map.TrySetViewAsync(new Geopoint(new BasicGeoposition
                    { Latitude = 15, Longitude = 25 }), 7, null, null, MapAnimationKind.None));
                await MapControlTestUtilities.WaitForAsync(() =>
                    observed is { } position && Math.Abs(position.Latitude - 15) < 1e-6 &&
                    Math.Abs(position.Longitude - 25) < 1e-6);
                Assert.IsTrue(onUiThread);
            });

    [TestMethod]
    public Task TouchRestrictionsKeepPanAndZoomIndependent() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter, 5, async map =>
            {
                var center = map.Center!.Position;
                await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, center, 5);
                var input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
                map.IsZoomEnabled = false;
                await input.Touch.StretchAsync();
                await map.CaptureRenderedFrameAsync();
                Assert.AreEqual(5, map.ZoomLevel);
                map.IsPanEnabled = false;
                await input.Touch.SwipeAsync(input.PointAt(0.4, 0.5), input.PointAt(0.6, 0.5));
                await map.CaptureRenderedFrameAsync();
                Assert.AreEqual(center.Longitude, map.Center.Position.Longitude, 1e-9);
                Assert.AreEqual(center.Latitude, map.Center.Position.Latitude, 1e-9);
                map.IsZoomEnabled = true;
                await input.Touch.StretchAsync();
                await MapControlTestUtilities.WaitForAsync(() => map.ZoomLevel > 5);
            });

    [TestMethod]
    public Task DisabledZoomBlocksWheelDoubleClickAndKeyboardButNotProgrammaticViews() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter, 5, async map =>
            {
                await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, 5);
                map.IsZoomEnabled = false;
                var input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
                await input.Mouse.WheelAsync(120);
                input.Mouse.DoubleClick();
                await MapControlTestUtilities.WaitForAsync(() => map.FocusState == FocusState.Pointer);
                input.Keyboard.Press(VirtualKey.Add);
                await map.CaptureRenderedFrameAsync();
                Assert.AreEqual(5, map.ZoomLevel);

                Assert.IsTrue(await map.TrySetViewAsync(map.Center, 7, null, null, MapAnimationKind.None));
                await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center.Position, 7);
                map.IsZoomEnabled = true;
                await input.Mouse.WheelAsync(120);
                await MapControlTestUtilities.WaitForAsync(() => map.ZoomLevel == 8);
            });

    [TestMethod]
    public Task DisabledPanBlocksDragAndKeyboardIndependentlyOfZoom() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter, 5, async map =>
            {
                var center = map.Center!.Position;
                await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, center, 5);
                map.IsPanEnabled = false;
                var input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
                await input.Mouse.DragAsync(input.PointAt(0.4, 0.5), input.PointAt(0.6, 0.5));
                input.Mouse.Click();
                await MapControlTestUtilities.WaitForAsync(() => map.FocusState == FocusState.Pointer);
                input.Keyboard.Press(VirtualKey.Right);
                await map.CaptureRenderedFrameAsync();
                Assert.AreEqual(center.Longitude, map.Center.Position.Longitude, 1e-9);
                Assert.AreEqual(center.Latitude, map.Center.Position.Latitude, 1e-9);

                await input.Mouse.WheelAsync(120);
                await MapControlTestUtilities.WaitForAsync(() => map.ZoomLevel == 6);
                Assert.IsTrue(await map.TrySetViewAsync(new Geopoint(new BasicGeoposition
                    { Latitude = 15, Longitude = 25 }), 6, null, null, MapAnimationKind.None));
                map.IsPanEnabled = true;
                await input.Mouse.DragAsync(input.PointAt(0.4, 0.5), input.PointAt(0.6, 0.5));
                await MapControlTestUtilities.WaitForAsync(() => Math.Abs(map.Center.Position.Longitude - 25) > 0.001);
            });

    [TestMethod]
    public Task LocationProjectionRoundTripsWithHeadingAndPitch() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter, 5, async map =>
            {
                Assert.IsTrue(await map.TrySetViewAsync(map.Center!, 5, 35, 40, MapAnimationKind.None));
                var offset = new Point(map.ActualWidth * 0.6, map.ActualHeight * 0.7);
                Assert.IsTrue(map.TryGetLocationFromOffset(offset, out var location));
                Assert.IsTrue(map.TryGetOffsetFromLocation(location, out var projected));
                Assert.AreEqual(offset.X, projected.X, 0.001);
                Assert.AreEqual(offset.Y, projected.Y, 0.001);
            });
}
