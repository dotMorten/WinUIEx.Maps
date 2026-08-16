using WinUIEx.Maps.Tests.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests.UITests.Input;

[TestClass]
[DoNotParallelize]
public sealed class TouchInputTests
{
    private const double CameraTolerance = 0.001;

    [TestMethod]
    public Task Pitch_IsClampedToSupportedRange() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
        {
            map.MapStyle = MapStyle.Blank;
            Assert.AreEqual(0, map.Pitch);

            map.Pitch = 75;
            Assert.AreEqual(60, map.Pitch);
            await MapControlTestUtilities.WaitForAsync(() =>
                map.TryGetDisplayedPitch(out double pitch) &&
                Math.Abs(pitch - 60) < CameraTolerance);

            map.Pitch = -5;
            Assert.AreEqual(0, map.Pitch);
            map.Pitch = double.NaN;
            Assert.AreEqual(0, map.Pitch);
            Assert.AreEqual(0, map.Pitch);
        });

    [TestMethod]
    public Task Stretch_ZoomsIn() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
        {
            await MapControlTestUtilities.SetupMapAsync(map);
            UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            double initialZoom = map.ZoomLevel;
            bool manipulationCompleted = false;
            map.AddHandler(
                UIElement.ManipulationCompletedEvent,
                new ManipulationCompletedEventHandler((_, _) =>
                {
                    manipulationCompleted = true;
                }),
                handledEventsToo: true);

            await input.Touch.StretchAsync();

            await MapControlTestUtilities.WaitForAsync(() => map.ZoomLevel > initialZoom);
            await MapControlTestUtilities.WaitForAsync(() => manipulationCompleted);
            await Task.Delay(100);
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(
                map,
                map.Center!.Position,
                map.ZoomLevel);
        });

    [TestMethod]
    public Task Pinch_ZoomsOut() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
        {
            await MapControlTestUtilities.SetupMapAsync(map);
            UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            double initialZoom = map.ZoomLevel;
            bool manipulationCompleted = false;
            map.AddHandler(
                UIElement.ManipulationCompletedEvent,
                new ManipulationCompletedEventHandler((_, _) =>
                {
                    manipulationCompleted = true;
                }),
                handledEventsToo: true);

            await input.Touch.PinchAsync();

            await MapControlTestUtilities.WaitForAsync(() => map.ZoomLevel < initialZoom);
            await MapControlTestUtilities.WaitForAsync(() => manipulationCompleted);
            await Task.Delay(100);
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(
                map,
                map.Center!.Position,
                map.ZoomLevel);
        });

    [TestMethod]
    public Task ReducedMotionSuppressesTouchInertia() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
            {
                await MapControlTestUtilities.SetupMapAsync(map);
                map.ApplyAnimationsEnabled(false);
                UiInputInjector input =
                    UiInputInjector.ForElement(MapControlTestHost.Window, map);
                BasicGeoposition initialCenter = map.Center!.Position;
                bool sawInertialDelta = false;
                bool manipulationCompleted = false;
                map.AddHandler(
                    UIElement.ManipulationDeltaEvent,
                    new ManipulationDeltaEventHandler((_, e) =>
                    {
                        sawInertialDelta |= e.IsInertial;
                    }),
                    handledEventsToo: true);
                map.AddHandler(
                    UIElement.ManipulationCompletedEvent,
                    new ManipulationCompletedEventHandler((_, _) =>
                    {
                        manipulationCompleted = true;
                    }),
                    handledEventsToo: true);

                await input.Touch.SwipeAsync(
                    input.PointAt(0.3, 0.5),
                    input.PointAt(0.7, 0.5),
                    durationMilliseconds: 80);

                await MapControlTestUtilities.WaitForAsync(() => manipulationCompleted);
                Assert.AreNotEqual(initialCenter, map.Center!.Position);
                Assert.IsFalse(sawInertialDelta);
                await MapControlTestUtilities.WaitForDisplayedCameraAsync(
                    map,
                    map.Center.Position,
                    map.ZoomLevel);
            });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task PinchOrStretch_OffCenter_PreservesLocationUnderGesture(
        bool stretch) =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
        {
            await MapControlTestUtilities.SetupMapAsync(map);
            UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            const double horizontalFraction = 0.3;
            const double verticalFraction = 0.65;
            InputPoint inputPoint = input.PointAt(horizontalFraction, verticalFraction);
            var touchPoints = new Dictionary<uint, Point>();
            bool manipulationCompleted = false;
            map.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler((_, e) =>
                {
                    if (e.Pointer.PointerDeviceType ==
                        Microsoft.UI.Input.PointerDeviceType.Touch)
                    {
                        touchPoints[e.Pointer.PointerId] =
                            e.GetCurrentPoint(map).Position;
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
            BasicGeoposition initialCenter = map.Center!.Position;
            double initialZoom = map.ZoomLevel;
            double viewportWidth = map.ActualWidth;
            double viewportHeight = map.ActualHeight;

            if (stretch)
            {
                await input.Touch.StretchAsync(
                    inputPoint,
                    distance: 60);
                await MapControlTestUtilities.WaitForAsync(
                    () => map.ZoomLevel > initialZoom);
            }
            else
            {
                await input.Touch.PinchAsync(
                    inputPoint,
                    distance: 60);
                await MapControlTestUtilities.WaitForAsync(
                    () => map.ZoomLevel < initialZoom);
            }

            await MapControlTestUtilities.WaitForAsync(() => manipulationCompleted);
            await Task.Delay(100);
            Assert.HasCount(2, touchPoints);
            Point anchorPoint = new(
                touchPoints.Values.Average(point => point.X),
                touchPoints.Values.Average(point => point.Y));
            BasicGeoposition anchoredLocation = MapControlTestUtilities.LocationAtOffset(
                initialCenter,
                initialZoom,
                viewportWidth,
                viewportHeight,
                anchorPoint);
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(
                map,
                map.Center!.Position,
                map.ZoomLevel);
            BasicGeoposition finalLocation =
                MapControlTestUtilities.GetDisplayedLocation(map, anchorPoint);
            MapControlTestUtilities.AssertCoordinatesEqual(
                anchoredLocation,
                finalLocation,
                map.ZoomLevel);
        });

    [TestMethod]
    public Task RotationBelowThresholdKeepsNorthUp() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
        {
            await MapControlTestUtilities.SetupMapAsync(map);
            UiInputInjector input =
                UiInputInjector.ForElement(MapControlTestHost.Window, map);

            await input.Touch.RotateAsync(4);

            Assert.AreEqual(0, map.Heading);
        });

    [TestMethod]
    public Task HeadingNormalizesToCompassRange() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            map =>
        {
            map.Heading = 450;
            Assert.AreEqual(90, map.Heading);

            map.Heading = -90;
            Assert.AreEqual(270, map.Heading);

            map.Heading = double.NaN;
            Assert.AreEqual(0, map.Heading);
            return Task.CompletedTask;
        });

    [TestMethod]
    public Task TwoFingerRotationChangesHeading() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
        {
            await MapControlTestUtilities.SetupMapAsync(map);
            UiInputInjector input =
                UiInputInjector.ForElement(MapControlTestHost.Window, map);
            bool changedBeforeRelease = false;

            await input.Touch.RotateAsync(
                20,
                beforeRelease: () =>
                {
                    changedBeforeRelease =
                        Math.Abs(
                            MapCamera.ShortestHeadingDelta(
                                0,
                                map.Heading)) > 5;
                    return Task.CompletedTask;
                });

            Assert.IsTrue(changedBeforeRelease);
            Assert.IsGreaterThan(
                5,
                Math.Abs(MapCamera.ShortestHeadingDelta(0, map.Heading)));
        });

    [TestMethod]
    public Task RotationEndingNearNorthSnapsBack() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
        {
            await MapControlTestUtilities.SetupMapAsync(map);
            map.Heading = 350;
            await WaitForDisplayedHeadingAsync(map, 350);
            UiInputInjector input =
                UiInputInjector.ForElement(MapControlTestHost.Window, map);
            bool manipulationCompleted = false;
            double headingBeforeRelease = double.NaN;
            map.AddHandler(
                UIElement.ManipulationCompletedEvent,
                new ManipulationCompletedEventHandler((_, _) =>
                {
                    manipulationCompleted = true;
                }),
                handledEventsToo: true);

            await input.Touch.RotateAsync(
                -13,
                beforeRelease: () =>
                {
                    headingBeforeRelease = map.Heading;
                    return Task.CompletedTask;
                });

            await MapControlTestUtilities.WaitForAsync(() => manipulationCompleted);
            Assert.IsGreaterThan(
                0,
                Math.Abs(MapCamera.ShortestHeadingDelta(
                    headingBeforeRelease,
                    0)));
            Assert.IsLessThanOrEqualTo(
                TouchRotationState.SnapThreshold,
                Math.Abs(MapCamera.ShortestHeadingDelta(
                    headingBeforeRelease,
                    0)));
            Assert.AreEqual(0, map.Heading, $"Final heading was {map.Heading}.");
            await WaitForDisplayedHeadingAsync(map, 0);
        });

    private static async Task WaitForDisplayedHeadingAsync(
        MapControl map,
        double expected)
    {
        await MapControlTestUtilities.WaitForAsync(() =>
            map.TryGetDisplayedHeading(out double heading) &&
            Math.Abs(MapCamera.ShortestHeadingDelta(heading, expected)) < 0.01);
    }
}
