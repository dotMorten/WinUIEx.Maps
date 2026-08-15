using WinUIEx.Maps.Tests.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests.UITests.Input;

[TestClass]
[DoNotParallelize]
public sealed class MouseInputTests
{
    [TestMethod]
    public Task DoubleClick_ZoomsIn() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
        {
            await MapControlTestUtilities.SetupMapAsync(map);
            UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            double initialZoom = map.ZoomLevel;

            input.Mouse.DoubleClick();

            await MapControlTestUtilities.WaitForAsync(() => map.ZoomLevel > initialZoom);
            Assert.AreEqual(initialZoom + 1, map.ZoomLevel, 0.001);
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(
                map,
                map.Center!.Position,
                initialZoom + 1);
        });

    [TestMethod]
    public Task DoubleClick_OffCenter_PreservesLocationUnderPointer() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
        {
            await MapControlTestUtilities.SetupMapAsync(map);
            UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            const double horizontalFraction = 0.3;
            const double verticalFraction = 0.35;
            InputPoint inputPoint = input.PointAt(horizontalFraction, verticalFraction);
            Point? viewportPoint = null;
            map.AddHandler(
                UIElement.DoubleTappedEvent,
                new DoubleTappedEventHandler((_, e) =>
                {
                    viewportPoint = e.GetPosition(map);
                }),
                handledEventsToo: true);
            BasicGeoposition initialCenter = map.Center!.Position;
            double initialZoom = map.ZoomLevel;
            double viewportWidth = map.ActualWidth;
            double viewportHeight = map.ActualHeight;

            input.Mouse.DoubleClick(inputPoint);

            await MapControlTestUtilities.WaitForAsync(() => map.ZoomLevel > initialZoom);
            await MapControlTestUtilities.WaitForAsync(() => viewportPoint.HasValue);
            Point anchorPoint = viewportPoint.GetValueOrDefault();
            BasicGeoposition anchoredLocation = MapControlTestUtilities.LocationAtOffset(
                initialCenter,
                initialZoom,
                viewportWidth,
                viewportHeight,
                anchorPoint);
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(
                map,
                map.Center!.Position,
                initialZoom + 1);
            BasicGeoposition finalLocation =
                MapControlTestUtilities.GetDisplayedLocation(map, anchorPoint);
            MapControlTestUtilities.AssertCoordinatesEqual(
                anchoredLocation,
                finalLocation,
                map.ZoomLevel);
        });

    [TestMethod]
    public Task DoubleClick_LowZoomNearIceland_PreservesLocationUnderPointer() =>
        MapControlTestHost.LoadMapControlAsync(
            new BasicGeoposition(),
            0,
            async map =>
        {
            map.MapStyle = WinUIEx.Maps.MapStyle.Blank;
            map.Center = new Geopoint(new BasicGeoposition
            {
                Latitude = 0,
                Longitude = 0,
            });
            map.ZoomLevel = 0;
            await MapControlTestUtilities.WaitForDisplayedZoomAsync(map, 0);
            UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            InputPoint inputPoint = input.PointAt(0.48, 0.37);
            Point? viewportPoint = null;
            BasicGeoposition? anchoredLocation = null;
            map.AddHandler(
                UIElement.DoubleTappedEvent,
                new DoubleTappedEventHandler((_, e) =>
                {
                    viewportPoint = e.GetPosition(map);
                    anchoredLocation =
                        MapControlTestUtilities.GetDisplayedLocation(map, viewportPoint.Value);
                }),
                handledEventsToo: true);

            input.Mouse.DoubleClick(inputPoint);

            await MapControlTestUtilities.WaitForAsync(() => map.ZoomLevel == 1);
            await MapControlTestUtilities.WaitForAsync(() => viewportPoint.HasValue);
            await MapControlTestUtilities.WaitForAsync(() => anchoredLocation.HasValue);
            Point anchorPoint = viewportPoint.GetValueOrDefault();
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(
                map,
                map.Center!.Position,
                1);
            BasicGeoposition finalLocation =
                MapControlTestUtilities.GetDisplayedLocation(map, anchorPoint);
            MapControlTestUtilities.AssertCoordinatesEqual(
                anchoredLocation.GetValueOrDefault(),
                finalLocation,
                map.ZoomLevel);
        });

    [TestMethod]
    public Task MouseWheel_ZoomsIn() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
        {
            await MapControlTestUtilities.SetupMapAsync(map);
            UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            InputPoint inputPoint = input.Mouse.Center;
            double initialZoom = map.ZoomLevel;
            map.AddHandler(
                UIElement.PointerWheelChangedEvent,
                new PointerEventHandler((_, _) => { }),
                handledEventsToo: true);
            input.Mouse.Click(input.PointAt(0.4, 0.4));
            await MapControlTestUtilities.WaitForAsync(
                () => map.FocusState == FocusState.Pointer);

            await input.Mouse.WheelAsync(inputPoint, 120);

            await MapControlTestUtilities.WaitForAsync(() => map.ZoomLevel > initialZoom);
            Assert.AreEqual(initialZoom + 1, map.ZoomLevel, 0.001);
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(
                map,
                map.Center!.Position,
                initialZoom + 1);
        });

    [TestMethod]
    public Task MouseWheel_ZoomsOut() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
        {
            await MapControlTestUtilities.SetupMapAsync(map);
            UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            InputPoint inputPoint = input.Mouse.Center;
            double initialZoom = map.ZoomLevel;
            map.AddHandler(
                UIElement.PointerWheelChangedEvent,
                new PointerEventHandler((_, _) => { }),
                handledEventsToo: true);
            input.Mouse.Click(input.PointAt(0.4, 0.4));
            await MapControlTestUtilities.WaitForAsync(
                () => map.FocusState == FocusState.Pointer);

            await input.Mouse.WheelAsync(inputPoint, -120);

            await MapControlTestUtilities.WaitForAsync(() => map.ZoomLevel < initialZoom);
            Assert.AreEqual(initialZoom - 1, map.ZoomLevel, 0.001);
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(
                map,
                map.Center!.Position,
                initialZoom - 1);
        });

    [TestMethod]
    [DataRow(120)]
    [DataRow(-120)]
    public Task MouseWheel_OffCenter_PreservesLocationUnderPointer(int wheelDelta) =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
        {
            await MapControlTestUtilities.SetupMapAsync(map);
            UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            const double horizontalFraction = 0.3;
            const double verticalFraction = 0.35;
            InputPoint inputPoint = input.PointAt(horizontalFraction, verticalFraction);
            Point? viewportPoint = null;
            map.AddHandler(
                UIElement.PointerWheelChangedEvent,
                new PointerEventHandler((_, e) =>
                {
                    viewportPoint = e.GetCurrentPoint(map).Position;
                }),
                handledEventsToo: true);
            input.Mouse.Click();
            await MapControlTestUtilities.WaitForAsync(
                () => map.FocusState == FocusState.Pointer);
            BasicGeoposition initialCenter = map.Center!.Position;
            double initialZoom = map.ZoomLevel;
            double viewportWidth = map.ActualWidth;
            double viewportHeight = map.ActualHeight;
            double expectedZoom = map.ZoomLevel + Math.Sign(wheelDelta);

            await input.Mouse.WheelAsync(inputPoint, wheelDelta);

            await MapControlTestUtilities.WaitForAsync(
                () => map.ZoomLevel == expectedZoom);
            await MapControlTestUtilities.WaitForAsync(() => viewportPoint.HasValue);
            Point anchorPoint = viewportPoint.GetValueOrDefault();
            BasicGeoposition anchoredLocation = MapControlTestUtilities.LocationAtOffset(
                initialCenter,
                initialZoom,
                viewportWidth,
                viewportHeight,
                anchorPoint);
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(
                map,
                map.Center!.Position,
                expectedZoom);
            BasicGeoposition finalLocation =
                MapControlTestUtilities.GetDisplayedLocation(map, anchorPoint);
            MapControlTestUtilities.AssertCoordinatesEqual(
                anchoredLocation,
                finalLocation,
                map.ZoomLevel);
        });

    [TestMethod]
    [DataRow(100, 0)]
    [DataRow(-100, 0)]
    [DataRow(0, 100)]
    [DataRow(0, -100)]
    public Task MouseDrag_PansMapByDragDistance(int horizontalDelta, int verticalDelta) =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
        {
            await MapControlTestUtilities.SetupMapAsync(map);
            UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            BasicGeoposition initialCenter = map.Center!.Position;
            double deliveredHorizontalDelta = 0;
            double deliveredVerticalDelta = 0;
            bool manipulationCompleted = false;
            map.AddHandler(
                UIElement.ManipulationDeltaEvent,
                new ManipulationDeltaEventHandler((_, e) =>
                {
                    if (e.PointerDeviceType == PointerDeviceType.Mouse &&
                        (InputKeyboardSource.GetKeyStateForCurrentThread(
                            VirtualKey.LeftButton) & CoreVirtualKeyStates.Down) != 0)
                    {
                        deliveredHorizontalDelta += e.Delta.Translation.X;
                        deliveredVerticalDelta += e.Delta.Translation.Y;
                    }
                }),
                handledEventsToo: true);
            map.AddHandler(
                UIElement.ManipulationCompletedEvent,
                new ManipulationCompletedEventHandler((_, _) =>
                {
                    manipulationCompleted = true;
                }),
                handledEventsToo: true);
            InputPoint start = input.Mouse.Center;
            var end = new InputPoint(
                start.X + horizontalDelta,
                start.Y + verticalDelta);

            await input.Mouse.DragAsync(start, end);

            await MapControlTestUtilities.WaitForAsync(() => manipulationCompleted);
            if (horizontalDelta == 0)
            {
                Assert.AreEqual(0, deliveredHorizontalDelta);
                Assert.AreEqual(Math.Sign(verticalDelta), Math.Sign(deliveredVerticalDelta));
            }
            else
            {
                Assert.AreEqual(Math.Sign(horizontalDelta), Math.Sign(deliveredHorizontalDelta));
                Assert.AreEqual(0, deliveredVerticalDelta);
            }

            BasicGeoposition expectedCenter = MapControlTestUtilities.PanByPixels(
                initialCenter,
                map.ZoomLevel,
                deliveredHorizontalDelta,
                deliveredVerticalDelta);
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
