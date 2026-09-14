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
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public Task AsymmetricPinchWithVerticalDriftNeverPitches(bool stretch, bool parallelStart) =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
            {
                await MapControlTestUtilities.SetupMapAsync(map);
                map.ApplyAnimationsEnabled(false);
                map.Pitch = 30;
                await MapControlTestUtilities.WaitForAsync(() =>
                    map.TryGetDisplayedPitch(out double pitch) && pitch == 30);
                UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
                InputPoint center = input.PointAt(0.5, 0.5);
                double dpi = map.XamlRoot.RasterizationScale;
                InputPoint At(double x, double y) =>
                    new(center.X + (int)Math.Round(x * dpi), center.Y + (int)Math.Round(y * dpi));
                int direction = stretch ? 1 : -1;
                double zoom = map.ZoomLevel;
                using var cameraEvents = new RenderingEventListener(
                    "CameraTargetChanged", "CameraPitchTargetChanged");
                bool completed = false;
                var pitches = new List<double>();
                map.AddHandler(UIElement.ManipulationDeltaEvent,
                    new ManipulationDeltaEventHandler((_, _) => pitches.Add(map.Pitch)), true);
                map.AddHandler(UIElement.ManipulationCompletedEvent,
                    new ManipulationCompletedEventHandler((_, _) => completed = true), true);
                // Real multi-stage asymmetric paths, not a centered pinch proxy:
                // first one contact leads vertically (or both drift ambiguously),
                // then unequal spreading/contraction continues with midpoint drift.
                await input.Touch.InjectAsync(
                    [
                        [At(-60, 30), At(-60, parallelStart ? 20 : 10),
                            At(-60 - direction * 20, -5), At(-60 - direction * 45, -35),
                            At(-60 - direction * 45, -65)],
                        [At(60, 30), At(60, parallelStart ? 21 : 30),
                            At(60 + direction * 10, 15), At(60 + direction * 25, -10),
                            At(60 + direction * 25, -40)],
                    ], 700);
                await MapControlTestUtilities.WaitForAsync(() => completed);
                Assert.IsNotEmpty(pitches);
                Assert.IsTrue(pitches.All(p => p == 30),
                    $"Pinch changed pitch: {string.Join(", ", pitches)}");
                Assert.AreEqual(30, map.Pitch);
                Assert.IsNotEmpty(cameraEvents.Events("CameraTargetChanged"),
                    "Verify provider delivery during the injected pinch.");
                Assert.IsEmpty(cameraEvents.Events("CameraPitchTargetChanged"));
                Assert.IsTrue(stretch ? map.ZoomLevel > zoom : map.ZoomLevel < zoom,
                    $"Expected {(stretch ? "stretch" : "pinch")}: {zoom} -> {map.ZoomLevel}");
                await MapControlTestUtilities.WaitForDisplayedCameraAsync(
                    map, map.Center!.Position, map.ZoomLevel);
            });

    [TestMethod]
    [DataRow(-1)]
    [DataRow(1)]
    public Task ParallelVerticalJitterPitchesWithoutChangingZoomOrCenter(int direction) =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
            {
                await MapControlTestUtilities.SetupMapAsync(map);
                map.ApplyAnimationsEnabled(false);
                map.Pitch = 30;
                await MapControlTestUtilities.WaitForAsync(() =>
                    map.TryGetDisplayedPitch(out double pitch) && pitch == 30);
                UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
                InputPoint middle = input.PointAt(0.5, 0.5);
                double dpi = map.XamlRoot.RasterizationScale;
                InputPoint At(double x, double y) => new(
                    middle.X + (int)Math.Round(x * dpi),
                    middle.Y + (int)Math.Round(y * direction * dpi));
                double zoom = map.ZoomLevel;
                var center = map.Center!.Position;
                bool completed = false;
                map.AddHandler(UIElement.ManipulationCompletedEvent,
                    new ManipulationCompletedEventHandler((_, _) => completed = true), true);
                await input.Touch.InjectAsync(
                    [
                        [At(-60, 0), At(-59, 10), At(-61, 25), At(-60, 55), At(-59, 80)],
                        [At(60, 0), At(61, 9), At(60, 24), At(61, 56), At(60, 79)],
                    ], 650);
                await MapControlTestUtilities.WaitForAsync(() => completed);
                Assert.IsTrue(direction < 0 ? map.Pitch > 30 : map.Pitch < 30);
                Assert.AreEqual(zoom, map.ZoomLevel);
                Assert.AreEqual(center, map.Center!.Position);
                Assert.AreEqual(0, map.Heading);
                await MapControlTestUtilities.WaitForAsync(() =>
                    map.TryGetDisplayedPitch(out double pitch) &&
                    Math.Abs(pitch - map.Pitch) < CameraTolerance);
            });

    [TestMethod]
    public Task PitchRecoversAfterContactsLeaveMap() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
            {
                map.Width = 400;
                map.Height = 300;
                await MapControlTestUtilities.SetupMapAsync(map);
                map.ApplyAnimationsEnabled(false);
                map.UpdateLayout();
                UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
                int pressed = 0;
                int released = 0;
                int completed = 0;
                map.AddHandler(UIElement.PointerPressedEvent,
                    new PointerEventHandler((_, _) => pressed++), true);
                map.AddHandler(UIElement.PointerReleasedEvent,
                    new PointerEventHandler((_, _) => released++), true);
                map.AddHandler(UIElement.ManipulationCompletedEvent,
                    new ManipulationCompletedEventHandler((_, _) => completed++), true);
                InputPoint start = input.PointAt(0.5, 0.5);
                // Outside the map, but still inside our own host window.
                InputPoint end = new(input.PointAt(1, 0.5).X + 40, start.Y);
                await input.Touch.SwipeAsync(start, end, 500);
                Assert.AreEqual(1, pressed, $"Start={start}; size={map.ActualWidth}x{map.ActualHeight}; scale={map.XamlRoot.RasterizationScale}");
                await MapControlTestUtilities.WaitForAsync(() => completed == 1);
                await input.Touch.InjectAsync(
                    [
                        [new(start.X - 60, start.Y + 40), new(start.X - 60, start.Y - 40)],
                        [new(start.X + 60, start.Y + 40), new(start.X + 60, start.Y - 40)],
                    ], 500);
                await MapControlTestUtilities.WaitForAsync(() => completed == 2);
                Assert.IsGreaterThan(0, map.Pitch,
                    $"Pressed={pressed}, released={released}, completed={completed}");
            });

    [TestMethod]
    public Task RepeatedNavigationThenPitchRetiresEveryContact() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
            {
                await MapControlTestUtilities.SetupMapAsync(map);
                map.ApplyAnimationsEnabled(false);
                UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
                int completed = 0;
                map.AddHandler(UIElement.ManipulationCompletedEvent,
                    new ManipulationCompletedEventHandler((_, _) => completed++), true);
                int expectedCompletions = 0;
                var trace = new List<string>();
                foreach (RoutedEvent routedEvent in new[]
                {
                    UIElement.PointerPressedEvent, UIElement.PointerReleasedEvent,
                    UIElement.PointerCanceledEvent, UIElement.PointerCaptureLostEvent,
                })
                {
                    map.AddHandler(routedEvent, new PointerEventHandler((_, e) =>
                        trace.Add($"{routedEvent}: {e.Pointer.PointerId}, captures={map.PointerCaptures?.Count}")), true);
                }
                map.AddHandler(UIElement.ManipulationDeltaEvent,
                    new ManipulationDeltaEventHandler((_, e) => trace.Add(
                        $"delta={e.Delta.Translation}, expansion={e.Delta.Expansion}, rotation={e.Delta.Rotation}, pitch={map.Pitch}")), true);
                async Task GestureAsync(Task gesture)
                {
                    await gesture;
                    expectedCompletions++;
                    await MapControlTestUtilities.WaitForAsync(() => completed == expectedCompletions);
                }
                for (int iteration = 0; iteration < 4; iteration++)
                {
                    await GestureAsync(input.Touch.StretchAsync());
                    await GestureAsync(input.Touch.PinchAsync());
                    await GestureAsync(input.Touch.SwipeAsync(
                        input.PointAt(0.4, 0.5), input.PointAt(0.6, 0.5)));
                    await GestureAsync(input.Touch.RotateAsync(30));
                    map.Pitch = 30;
                    await MapControlTestUtilities.WaitForAsync(() =>
                        map.TryGetDisplayedPitch(out double pitch) && pitch == 30);
                    double zoom = map.ZoomLevel;
                    trace.Clear();
                    double heading = map.Heading;
                    BasicGeoposition center = map.Center!.Position;
                    InputPoint start = input.PointAt(0.5, 0.5);
                    int direction = iteration % 2 == 0 ? -1 : 1;
                    await GestureAsync(input.Touch.InjectAsync(
                        [
                            [new(start.X - 60, start.Y), new(start.X - 60, start.Y + direction * 100)],
                            [new(start.X + 60, start.Y), new(start.X + 60, start.Y + direction * 100)],
                        ], 500));
                    Assert.IsTrue(direction < 0 ? map.Pitch > 30 : map.Pitch < 30,
                        $"Iteration {iteration}: pitch={map.Pitch}\n{string.Join("\n", trace)}");
                    Assert.AreEqual(zoom, map.ZoomLevel);
                    Assert.AreEqual(heading, map.Heading);
                    Assert.AreEqual(center, map.Center!.Position);
                    await MapControlTestUtilities.WaitForAsync(() =>
                        map.TryGetDisplayedPitch(out double pitch) &&
                        Math.Abs(pitch - map.Pitch) < CameraTolerance);
                }
            });

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
    public Task TrySetViewAsyncInterruptsTouchInertia() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
            {
                await MapControlTestUtilities.SetupMapAsync(map);
                UiInputInjector input =
                    UiInputInjector.ForElement(MapControlTestHost.Window, map);
                bool sawInertialDelta = false;
                map.AddHandler(
                    UIElement.ManipulationDeltaEvent,
                    new ManipulationDeltaEventHandler((_, e) =>
                    {
                        sawInertialDelta |= e.IsInertial;
                    }),
                    handledEventsToo: true);

                await input.Touch.SwipeAsync(
                    input.PointAt(0.3, 0.5),
                    input.PointAt(0.7, 0.5),
                    durationMilliseconds: 80);
                await MapControlTestUtilities.WaitForAsync(() => sawInertialDelta);
                var target = new Geopoint(new BasicGeoposition
                {
                    Latitude = 30,
                    Longitude = 40,
                });

                Task<bool> view = map.TrySetViewAsync(
                    target,
                    8,
                    null,
                    null,
                    MapAnimationKind.Linear);

                await MapControlTestUtilities.WaitForAsync(() => view.IsCompleted);
                Assert.IsTrue(await view);
                await MapControlTestUtilities.WaitForDisplayedCameraAsync(
                    map,
                    target.Position,
                    8);
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
                -18,
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

    [TestMethod]
    public Task PinchWithIncidentalTwistDoesNotRotate_AndRotationResets() =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
        {
            await MapControlTestUtilities.SetupMapAsync(map);
            map.ApplyAnimationsEnabled(false);
            UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            int completed = 0;
            int started = 0;
            map.AddHandler(UIElement.ManipulationStartedEvent,
                new ManipulationStartedEventHandler((_, _) => started++), true);
            map.AddHandler(UIElement.ManipulationCompletedEvent,
                new ManipulationCompletedEventHandler((_, _) => completed++), true);
            using RenderingEventListener events = new("CameraHeadingTargetChanged");
            InputPoint center = input.PointAt(0.5, 0.5);
            double zoom = map.ZoomLevel;
            await input.Touch.InjectAsync(
                [
                    [new(center.X - 100, center.Y), new(center.X - 50, center.Y - 6)],
                    [new(center.X + 100, center.Y), new(center.X + 50, center.Y + 6)],
                ], 400);
            await MapControlTestUtilities.WaitForAsync(() => completed == 1);
            Assert.IsLessThan(zoom, map.ZoomLevel);
            Assert.AreEqual(0, map.Heading);
            Assert.AreEqual(0, map.Pitch);
            Assert.IsEmpty(events.Events("CameraHeadingTargetChanged"));

            // Repeated signed oscillations must not sum into a deliberate twist.
            await input.Touch.InjectAsync(
                [
                    [new(center.X - 80, center.Y), new(center.X - 79, center.Y - 10),
                     new(center.X - 79, center.Y + 10), new(center.X - 79, center.Y - 10),
                     new(center.X - 80, center.Y)],
                    [new(center.X + 80, center.Y), new(center.X + 79, center.Y + 10),
                     new(center.X + 79, center.Y - 10), new(center.X + 79, center.Y + 10),
                     new(center.X + 80, center.Y)],
                ], 800);
            await MapControlTestUtilities.WaitForAsync(() => completed == started);
            Assert.AreEqual(0, map.Heading);

            await input.Touch.RotateAsync(30, beforeRelease: async () =>
            {
                Assert.AreEqual(-20, MapCamera.ShortestHeadingDelta(0, map.Heading), 2);
                await WaitForDisplayedHeadingAsync(map, map.Heading);
            });
            await MapControlTestUtilities.WaitForAsync(() => completed == started);
            Assert.IsTrue(events.Events("CameraHeadingTargetChanged")
                .Any(e => Convert.ToBoolean(e.Payload[1])));
            double heading = map.Heading;
            await input.Touch.RotateAsync(8);
            await MapControlTestUtilities.WaitForAsync(() => completed == started);
            Assert.AreEqual(heading, map.Heading, CameraTolerance);
        });

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public Task TwoFingerVerticalDragChangesPitchProportionally(bool upward) =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
        {
            await MapControlTestUtilities.SetupMapAsync(map);
            map.ApplyAnimationsEnabled(false);
            map.Pitch = 30;
            await MapControlTestUtilities.WaitForAsync(() =>
                map.TryGetDisplayedPitch(out double pitch) && pitch == 30);
            UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            BasicGeoposition center = map.Center!.Position;
            double zoom = map.ZoomLevel;
            InputPoint start = input.PointAt(0.5, 0.5);
            int distance = upward ? -80 : 80;
            bool completed = false;
            double translation = 0;
            double appliedTranslation = 0;
            double previousPitch = 30;
            var pitchSteps = new List<(double Expected, double Actual)>();
            int pressed = 0;
            int canceled = 0;
            int captureLost = 0;
            double expansion = 0;
            double rotation = 0;
            var trace = new List<string>();
            map.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler((_, e) =>
                {
                    pressed++;
                    trace.Add($"down {e.Pointer.PointerId} at {e.GetCurrentPoint(map).Position}");
                }), true);
            map.AddHandler(UIElement.PointerReleasedEvent,
                new PointerEventHandler((_, e) => trace.Add($"up {e.Pointer.PointerId}")), true);
            map.AddHandler(UIElement.PointerCanceledEvent,
                new PointerEventHandler((_, e) =>
                {
                    canceled++;
                    trace.Add($"cancel {e.Pointer.PointerId}");
                }), true);
            map.AddHandler(UIElement.PointerCaptureLostEvent,
                new PointerEventHandler((_, e) =>
                {
                    captureLost++;
                    trace.Add($"capture-lost {e.Pointer.PointerId}");
                }), true);
            map.AddHandler(UIElement.ManipulationStartedEvent,
                new ManipulationStartedEventHandler((_, _) => trace.Add("started")), true);
            map.AddHandler(UIElement.ManipulationDeltaEvent,
                new ManipulationDeltaEventHandler((_, e) =>
                {
                    if (!e.IsInertial)
                    {
                        translation += e.Delta.Translation.Y;
                        if (map.Pitch != 30)
                        {
                            // Once committed, every native delta has the same
                            // sensitivity, including the first nonzero change.
                            pitchSteps.Add((previousPitch - e.Delta.Translation.Y * 0.25, map.Pitch));
                            appliedTranslation += e.Delta.Translation.Y;
                        }
                        previousPitch = map.Pitch;
                        expansion += e.Delta.Expansion;
                        rotation += e.Delta.Rotation;
                        trace.Add($"delta {e.Delta.Translation}, expansion={e.Delta.Expansion}, rotation={e.Delta.Rotation}, pitch={map.Pitch}");
                    }
                }), true);
            map.AddHandler(UIElement.ManipulationCompletedEvent,
                new ManipulationCompletedEventHandler((_, _) =>
                {
                    completed = true;
                    trace.Add("completed");
                }), true);
            await input.Touch.InjectAsync(
                [
                    [new(start.X - 60, start.Y), new(start.X - 60, start.Y + distance)],
                    [new(start.X + 60, start.Y), new(start.X + 60, start.Y + distance)],
                ], 400);
            await MapControlTestUtilities.WaitForAsync(() => completed);
            // The OS consumes recognition slop before publishing manipulation deltas.
            // Verify sensitivity against native recognized motion, not raw injected pixels.
            Assert.IsGreaterThan(20, Math.Abs(translation));
            Assert.AreEqual(Math.Sign(distance), Math.Sign(translation));
            Assert.IsGreaterThan(0, Math.Abs(appliedTranslation));
            foreach (var step in pitchSteps)
            {
                Assert.AreEqual(step.Expected, step.Actual, CameraTolerance);
            }
            Assert.IsGreaterThanOrEqualTo(16, Math.Abs(translation - appliedTranslation));
            double expected = 30 - appliedTranslation * 0.25;
            Assert.AreEqual(expected, map.Pitch, CameraTolerance,
                $"Native contacts: pressed={pressed}, canceled={canceled}, captureLost={captureLost}; translation={translation}, expansion={expansion}, rotation={rotation}.\n{string.Join("\n", trace)}");
            Assert.AreEqual(center, map.Center!.Position);
            Assert.AreEqual(zoom, map.ZoomLevel, CameraTolerance);
            Assert.AreEqual(0, map.Heading);
            await MapControlTestUtilities.WaitForAsync(() =>
                map.TryGetDisplayedPitch(out double pitch) &&
                Math.Abs(pitch - map.Pitch) < CameraTolerance);
        });

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public Task TwoFingerVerticalDragPitchesWithoutPanOrZoom(bool upward) =>
        MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
        {
            await MapControlTestUtilities.SetupMapAsync(map);
            map.ApplyAnimationsEnabled(false);
            map.Pitch = upward ? 50 : 10;
            await MapControlTestUtilities.WaitForAsync(() =>
                map.TryGetDisplayedPitch(out double pitch) && pitch == map.Pitch);
            UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            BasicGeoposition center = map.Center!.Position;
            double zoom = map.ZoomLevel;
            int completed = 0;
            map.AddHandler(UIElement.ManipulationCompletedEvent,
                new ManipulationCompletedEventHandler((_, _) => completed++), true);
            using RenderingEventListener events = new("CameraPitchTargetChanged");
            var trace = new List<string>();
            foreach (RoutedEvent routedEvent in new[]
            {
                UIElement.PointerPressedEvent, UIElement.PointerReleasedEvent,
                UIElement.PointerCanceledEvent, UIElement.PointerCaptureLostEvent,
            })
            {
                string name = routedEvent == UIElement.PointerPressedEvent ? "down" :
                    routedEvent == UIElement.PointerReleasedEvent ? "up" :
                    routedEvent == UIElement.PointerCanceledEvent ? "cancel" : "capture-lost";
                map.AddHandler(routedEvent, new PointerEventHandler((_, e) =>
                    trace.Add($"{name} {e.Pointer.PointerId} at {e.GetCurrentPoint(map).Position}")), true);
            }
            map.AddHandler(UIElement.ManipulationStartedEvent,
                new ManipulationStartedEventHandler((_, _) => trace.Add("started")), true);
            map.AddHandler(UIElement.ManipulationDeltaEvent,
                new ManipulationDeltaEventHandler((_, e) => trace.Add(
                    $"delta {e.Delta.Translation}, expansion={e.Delta.Expansion}, rotation={e.Delta.Rotation}, inertial={e.IsInertial}, pitch={map.Pitch}")), true);
            map.AddHandler(UIElement.ManipulationCompletedEvent,
                new ManipulationCompletedEventHandler((_, _) => trace.Add("completed")), true);
            InputPoint start = input.PointAt(0.5, upward ? 0.75 : 0.25);
            InputPoint end = input.PointAt(0.5, upward ? 0.25 : 0.75);
            await input.Touch.InjectAsync(
                [
                    [new(start.X - 60, start.Y), new(end.X - 60, end.Y)],
                    [new(start.X + 60, start.Y), new(end.X + 60, end.Y)],
                ], 500, async () =>
                {
                    double expected = upward ? 60 : 0;
                    Assert.AreEqual(expected, map.Pitch, string.Join("\n", trace));
                    await MapControlTestUtilities.WaitForAsync(() =>
                        map.TryGetDisplayedPitch(out double pitch) &&
                        Math.Abs(pitch - expected) < CameraTolerance);
                });
            await MapControlTestUtilities.WaitForAsync(() => completed == 1);
            Assert.AreEqual(center, map.Center!.Position);
            Assert.AreEqual(zoom, map.ZoomLevel, CameraTolerance);
            Assert.AreEqual(0, map.Heading);
            Assert.IsTrue(events.Events("CameraPitchTargetChanged")
                .Any(e => Convert.ToBoolean(e.Payload[1])));

            // A following single-contact gesture must not retain pitch mode.
            double finalPitch = map.Pitch;
            await input.Touch.SwipeAsync(input.PointAt(0.3, 0.5), input.PointAt(0.7, 0.5));
            await MapControlTestUtilities.WaitForAsync(() => completed == 2);
            Assert.AreNotEqual(center.Longitude, map.Center!.Position.Longitude);
            Assert.AreEqual(center.Latitude, map.Center.Position.Latitude, CameraTolerance);
            Assert.AreEqual(finalPitch, map.Pitch);
            Assert.AreEqual(zoom, map.ZoomLevel, CameraTolerance);
        });
}
