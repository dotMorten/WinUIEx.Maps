using Windows.Devices.Geolocation;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests.UITests;

[TestClass]
[DoNotParallelize]
public sealed class MapControlViewTests
{
    private static readonly BasicGeoposition InitialCenter = new()
    {
        Latitude = 10,
        Longitude = 20,
    };

    [TestMethod]
    public Task NoneAppliesCenterZoomHeadingAndPitchImmediately() =>
        MapControlTestHost.LoadMapControlAsync(
            InitialCenter,
            3,
            async map =>
            {
                var center = new Geopoint(new BasicGeoposition
                {
                    Latitude = 47.61,
                    Longitude = -122.33,
                });

                bool succeeded = await map.TrySetViewAsync(
                    center,
                    7.5,
                    450,
                    75,
                    MapAnimationKind.None);

                Assert.IsTrue(succeeded);
                Assert.AreEqual(
                    center.Position.Latitude,
                    map.Center!.Position.Latitude,
                    0.000000001);
                Assert.AreEqual(
                    center.Position.Longitude,
                    map.Center.Position.Longitude,
                    0.000000001);
                Assert.AreEqual(7.5, map.ZoomLevel);
                Assert.AreEqual(90, map.Heading);
                Assert.AreEqual(MapCamera.MaximumPitch, map.Pitch);
                AssertDisplayedView(
                    map,
                    center.Position,
                    7.5,
                    90,
                    MapCamera.MaximumPitch);
            });

    [TestMethod]
    public Task NullOptionalValuesPreserveCurrentViewValues() =>
        MapControlTestHost.LoadMapControlAsync(
            InitialCenter,
            5,
            async map =>
            {
                map.Heading = 25;
                map.Pitch = 15;
                await MapControlTestUtilities.WaitForDisplayedCameraAsync(
                    map,
                    InitialCenter,
                    5);
                var center = new Geopoint(new BasicGeoposition
                {
                    Latitude = -33.86,
                    Longitude = 151.21,
                });

                bool succeeded = await map.TrySetViewAsync(
                    center,
                    null,
                    null,
                    null,
                    MapAnimationKind.None);

                Assert.IsTrue(succeeded);
                Assert.AreEqual(5, map.ZoomLevel);
                Assert.AreEqual(25, map.Heading);
                Assert.AreEqual(15, map.Pitch);
                AssertDisplayedView(map, center.Position, 5, 25, 15);
            });

    [TestMethod]
    [DataRow(MapAnimationKind.Default)]
    [DataRow(MapAnimationKind.Linear)]
    [DataRow(MapAnimationKind.Bow)]
    public Task AnimatedKindsCompleteAtRequestedView(MapAnimationKind animation) =>
        MapControlTestHost.LoadMapControlAsync(
            InitialCenter,
            4,
            async map =>
            {
                var center = new Geopoint(new BasicGeoposition
                {
                    Latitude = 11,
                    Longitude = 21,
                });

                bool succeeded = await map.TrySetViewAsync(
                    center,
                    4.5,
                    30,
                    10,
                    animation);

                Assert.IsTrue(succeeded);
                AssertDisplayedView(map, center.Position, 4.5, 30, 10);
            });

    [TestMethod]
    public Task NewViewRequestReturnsFalseForInterruptedAnimation() =>
        MapControlTestHost.LoadMapControlAsync(
            InitialCenter,
            3,
            async map =>
            {
                Task<bool> interrupted = map.TrySetViewAsync(
                    new Geopoint(new BasicGeoposition
                    {
                        Latitude = 40,
                        Longitude = 80,
                    }),
                    10,
                    180,
                    30,
                    MapAnimationKind.Default);
                var finalCenter = new Geopoint(new BasicGeoposition
                {
                    Latitude = -20,
                    Longitude = -60,
                });

                Task<bool> replacement = map.TrySetViewAsync(
                    finalCenter,
                    6,
                    270,
                    20,
                    MapAnimationKind.None);

                Assert.IsFalse(await interrupted);
                Assert.IsTrue(await replacement);
                AssertDisplayedView(map, finalCenter.Position, 6, 270, 20);
            });

    [TestMethod]
    public Task DirectCameraPropertyChangeInterruptsPendingView() =>
        MapControlTestHost.LoadMapControlAsync(
            InitialCenter,
            3,
            async map =>
            {
                Task<bool> interrupted = map.TrySetViewAsync(
                    new Geopoint(new BasicGeoposition
                    {
                        Latitude = 40,
                        Longitude = 80,
                    }),
                    10,
                    180,
                    30,
                    MapAnimationKind.Default);

                map.ZoomLevel = 4;

                Assert.IsFalse(await interrupted);
                await MapControlTestUtilities.WaitForDisplayedZoomAsync(map, 4);
            });

    [TestMethod]
    public Task CenterAndNullableOverloadsPreserveUnspecifiedValues() =>
        MapControlTestHost.LoadMapControlAsync(
            InitialCenter,
            5,
            async map =>
            {
                Geopoint firstCenter = new(new BasicGeoposition
                {
                    Latitude = 12,
                    Longitude = 22,
                });
                Assert.IsTrue(await map.TrySetViewAsync(firstCenter));
                AssertDisplayedView(map, firstCenter.Position, 5, 0, 0);

                Geopoint secondCenter = new(new BasicGeoposition
                {
                    Latitude = 13,
                    Longitude = 23,
                });
                Assert.IsTrue(await map.TrySetViewAsync(secondCenter, 6));
                AssertDisplayedView(map, secondCenter.Position, 6, 0, 0);

                Geopoint thirdCenter = new(new BasicGeoposition
                {
                    Latitude = 14,
                    Longitude = 24,
                });
                Assert.IsTrue(await map.TrySetViewAsync(
                    thirdCenter,
                    7,
                    45,
                    20));
                AssertDisplayedView(map, thirdCenter.Position, 7, 45, 20);
            });

    [TestMethod]
    public Task InvalidAnimationKindIsRejected() =>
        MapControlTestHost.LoadMapControlAsync(
            InitialCenter,
            5,
            map =>
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                    map.TrySetViewAsync(
                        new Geopoint(InitialCenter),
                        5,
                        0,
                        0,
                        (MapAnimationKind)100));
                return Task.CompletedTask;
            });

    private static void AssertDisplayedView(
        MapControl map,
        BasicGeoposition expectedCenter,
        double expectedZoom,
        double expectedHeading,
        double expectedPitch)
    {
        Assert.IsTrue(map.TryGetDisplayedCamera(
            out BasicGeoposition center,
            out double zoom,
            out double heading,
            out double pitch));
        Assert.AreEqual(expectedCenter.Longitude, center.Longitude, 0.000000001);
        Assert.AreEqual(expectedCenter.Latitude, center.Latitude, 0.000000001);
        Assert.AreEqual(expectedZoom, zoom, 0.001);
        Assert.AreEqual(expectedHeading, heading, 0.001);
        Assert.AreEqual(expectedPitch, pitch, 0.001);
    }
}
