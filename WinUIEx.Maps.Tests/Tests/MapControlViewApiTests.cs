using Windows.Devices.Geolocation;

namespace WinUIEx.Maps.Tests.Tests;

[TestClass]
public sealed class MapControlViewApiTests
{
    [TestMethod]
    public void TrySetViewAsyncExposesAllUwpOverloads()
    {
        Type type = typeof(MapControl);
        Type nullableDouble = typeof(double?);

        Assert.IsNotNull(type.GetMethod(
            nameof(MapControl.TrySetViewAsync),
            [typeof(Geopoint)]));
        Assert.IsNotNull(type.GetMethod(
            nameof(MapControl.TrySetViewAsync),
            [typeof(Geopoint), nullableDouble]));
        Assert.IsNotNull(type.GetMethod(
            nameof(MapControl.TrySetViewAsync),
            [
                typeof(Geopoint),
                nullableDouble,
                nullableDouble,
                nullableDouble,
            ]));
        Assert.IsNotNull(type.GetMethod(
            nameof(MapControl.TrySetViewAsync),
            [
                typeof(Geopoint),
                nullableDouble,
                nullableDouble,
                nullableDouble,
                typeof(MapAnimationKind),
            ]));
        Assert.IsTrue(type
            .GetMethods()
            .Where(method => method.Name == nameof(MapControl.TrySetViewAsync))
            .All(method => method.ReturnType == typeof(Task<bool>)));
    }

    [TestMethod]
    public void MapAnimationKindMatchesUwpValues()
    {
        CollectionAssert.AreEqual(
            new[] { 0, 1, 2, 3 },
            Enum.GetValues<MapAnimationKind>()
                .Select(value => (int)value)
                .ToArray());
    }

    [TestMethod]
    public void CameraAnimationKindsUseDistinctProgressCurves()
    {
        const double progress = 0.5;

        Assert.AreEqual(
            progress,
            Rendering.CameraAnimation.Ease(progress, MapAnimationKind.Linear));
        Assert.AreEqual(
            0.75,
            Rendering.CameraAnimation.Ease(progress, MapAnimationKind.Bow));
        Assert.AreEqual(
            0.875,
            Rendering.CameraAnimation.Ease(progress, MapAnimationKind.Default));
    }
}
