using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;
using WinUIEx.Maps.Tests.Input;
using WinUIEx.Maps.Tests.UITestHelpers;
using Windows.Devices.Geolocation;

namespace WinUIEx.Maps.Tests.UITests;

[TestClass]
[DoNotParallelize]
public sealed class MapControlLifecycleTests
{
    [TestMethod]
    public Task InitialCameraPropertiesAreAppliedWithoutAnimation()
    {
        BasicGeoposition expectedCenter = new()
        {
            Longitude = -122.33,
            Latitude = 47.61,
        };
        const double expectedZoom = 10;
        const double expectedHeading = 75;
        const double expectedPitch = 35;

        return MapControlTestHost.LoadUIAsync(
            () => new MapControl
            {
                Width = 640,
                Height = 480,
                MapStyle = MapStyle.Blank,
                Center = new Geopoint(expectedCenter),
                ZoomLevel = expectedZoom,
                Heading = expectedHeading,
                Pitch = expectedPitch,
            },
            async root =>
            {
                MapControl map = (MapControl)root;
                await MapControlTestUtilities.WaitForAsync(() =>
                    MapControlTestUtilities.TryGetDisplayedCamera(
                        map,
                        out _,
                        out _));

                Assert.IsTrue(MapControlTestUtilities.TryGetDisplayedCamera(
                    map,
                    out BasicGeoposition displayedCenter,
                    out double displayedZoom));
                Assert.AreEqual(expectedCenter.Longitude, displayedCenter.Longitude, 0.000000001);
                Assert.AreEqual(expectedCenter.Latitude, displayedCenter.Latitude, 0.000000001);
                Assert.AreEqual(expectedZoom, displayedZoom, 0.001);
                Assert.IsTrue(map.TryGetDisplayedHeading(out double displayedHeading));
                Assert.AreEqual(expectedHeading, displayedHeading, 0.001);
                Assert.IsTrue(map.TryGetDisplayedPitch(out double displayedPitch));
                Assert.AreEqual(expectedPitch, displayedPitch, 0.001);
                Assert.IsTrue(map.RasterManagerHasScene);
            });
    }

    [TestMethod]
    public Task UnloadedMapCanBeLoadedAgainWithoutManualDisposal()
    {
        MapControl? map = null;
        Grid? host = null;

        return MapControlTestHost.LoadUIAsync(
            () =>
            {
                map = new MapControl
                {
                    Width = 640,
                    Height = 480,
                    MapStyle = MapStyle.RoadRaster,
                    Center = new Geopoint(new BasicGeoposition
                    {
                        Longitude = -122.33,
                        Latitude = 47.61,
                    }),
                    ZoomLevel = 10,
                };
                host = new Grid();
                host.Children.Add(map);
                return host;
            },
            async _ =>
            {
                Assert.IsNotNull(map);
                Assert.IsNotNull(host);
                MapControl currentMap = map;
                Grid currentHost = host;

                await MapControlTestUtilities.WaitForAsync(
                    () => currentMap.RendererHasDeviceResources);
                Assert.AreEqual(1, currentMap.ActiveRasterWorkerCount);
                Stopwatch unload = Stopwatch.StartNew();
                await RemoveAsync(currentHost, currentMap);
                await MapControlTestUtilities.WaitForAsync(
                    () => currentMap.ActiveRasterWorkerCount == 0);
                unload.Stop();
                Assert.IsLessThan(
                    2000,
                    unload.ElapsedMilliseconds,
                    "Transient unload should not wait for raster workers to exit.");
                Assert.IsFalse(currentMap.RuntimeResourcesReleased);
                Assert.IsTrue(currentMap.RendererHasDeviceResources);
                Assert.AreEqual(0, currentMap.ActiveRasterWorkerCount);
                Stopwatch reload = Stopwatch.StartNew();
                await AddAsync(currentHost, currentMap);
                reload.Stop();
                Assert.IsLessThan(
                    2000,
                    reload.ElapsedMilliseconds,
                    "Reload should resume retained resources without reconstructing them.");
                Assert.AreEqual(1, currentMap.ActiveRasterWorkerCount);

                await MapControlTestUtilities.WaitForDisplayedCameraAsync(
                    currentMap,
                    currentMap.Center!.Position,
                    currentMap.ZoomLevel);
            });
    }

    [TestMethod]
    public Task RapidReparentingLeavesMapRunning()
    {
        MapControl? map = null;
        Grid? leftHost = null;
        Grid? rightHost = null;

        return MapControlTestHost.LoadUIAsync(
            () =>
            {
                map = new MapControl
                {
                    Width = 640,
                    Height = 480,
                    MapStyle = MapStyle.RoadRaster,
                    Center = new Geopoint(new BasicGeoposition
                    {
                        Longitude = -122.33,
                        Latitude = 47.61,
                    }),
                    ZoomLevel = 10,
                };
                leftHost = new Grid();
                rightHost = new Grid();
                leftHost.Children.Add(map);
                return new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(),
                        new ColumnDefinition(),
                    },
                    Children =
                    {
                        leftHost,
                        rightHost,
                    },
                };
            },
            async _ =>
            {
                Assert.IsNotNull(map);
                Assert.IsNotNull(leftHost);
                Assert.IsNotNull(rightHost);
                MapControl currentMap = map;
                Grid current = leftHost;
                Grid destination = rightHost;

                await MapControlTestUtilities.WaitForAsync(
                    () => currentMap.RendererHasDeviceResources);
                for (int index = 0; index < 25; index++)
                {
                    current.Children.Remove(currentMap);
                    destination.Children.Add(currentMap);
                    (current, destination) = (destination, current);
                    await Task.Yield();

                    if (index < 5)
                    {
                        double initialZoom = currentMap.ZoomLevel;
                        UiInputInjector input = UiInputInjector.ForElement(
                            MapControlTestHost.Window,
                            currentMap);
                        input.Mouse.DoubleClick();
                        await MapControlTestUtilities.WaitForAsync(
                            () => currentMap.ZoomLevel > initialZoom);
                    }
                }

                await MapControlTestUtilities.WaitForAsync(
                    () => currentMap.IsLoaded &&
                        currentMap.RendererHasDeviceResources &&
                        currentMap.ActiveRasterWorkerCount == 1);
                await MapControlTestUtilities.WaitForDisplayedCameraAsync(
                    currentMap,
                    currentMap.Center!.Position,
                    currentMap.ZoomLevel);
            });
    }

    private static async Task RemoveAsync(Grid host, MapControl map)
    {
        TaskCompletionSource unloaded = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler handler = (_, _) => unloaded.TrySetResult();
        map.Unloaded += handler;
        host.Children.Remove(map);
        await unloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        map.Unloaded -= handler;
    }

    private static async Task AddAsync(Grid host, MapControl map)
    {
        TaskCompletionSource loaded = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler handler = (_, _) => loaded.TrySetResult();
        map.Loaded += handler;
        host.Children.Add(map);
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        map.Loaded -= handler;
    }
}
