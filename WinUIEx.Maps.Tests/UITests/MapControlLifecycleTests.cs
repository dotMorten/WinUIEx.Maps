using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using WinUIEx.Maps.Rendering;
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

    [TestMethod]
    public async Task RepeatedSymbolMapsReleaseRendererWorkingMemory()
    {
        WeakReference<MapControl>[] warmup =
            await CreateAndUnloadSymbolMapsAsync(2);
        await CollectMapsAsync(warmup);
        long baselineBytes = GC.GetTotalMemory(forceFullCollection: true);
        long baselinePrivateBytes = GetPrivateMemorySize();

        WeakReference<MapControl>[] maps =
            await CreateAndUnloadSymbolMapsAsync(6);
        await CollectMapsAsync(maps);
        long retainedBytes =
            GC.GetTotalMemory(forceFullCollection: true) - baselineBytes;
        long retainedPrivateBytes =
            GetPrivateMemorySize() - baselinePrivateBytes;

        Assert.IsTrue(
            maps.All(map => !map.TryGetTarget(out _)),
            "Every unloaded symbol MapControl should be eligible for collection.");
        Assert.IsLessThanOrEqualTo(
            16L * 1024 * 1024,
            retainedBytes,
            $"Repeated symbol maps retained {retainedBytes:N0} managed bytes.");
        Assert.IsLessThanOrEqualTo(
            128L * 1024 * 1024,
            retainedPrivateBytes,
            $"Repeated symbol maps retained {retainedPrivateBytes:N0} private bytes.");
    }

    [TestMethod]
    public async Task RepeatedRoadMapsAreCollectedWithoutSignificantMemoryGrowth()
    {
        WeakReference<MapControl>[] warmup =
            await CreateAndUnloadRoadMapsAsync(2);
        await CollectMapsAsync(warmup);
        long baselineBytes = GC.GetTotalMemory(forceFullCollection: true);
        long baselinePrivateBytes = GetPrivateMemorySize();

        WeakReference<MapControl>[] maps =
            await CreateAndUnloadRoadMapsAsync(8);
        await CollectMapsAsync(maps);
        long retainedBytes =
            GC.GetTotalMemory(forceFullCollection: true) - baselineBytes;
        long retainedPrivateBytes =
            GetPrivateMemorySize() - baselinePrivateBytes;

        Assert.IsTrue(
            maps.All(map => !map.TryGetTarget(out _)),
            "Every unloaded MapControl should be eligible for garbage collection.");
        Assert.IsLessThanOrEqualTo(
            16L * 1024 * 1024,
            retainedBytes,
            $"Repeated map lifetimes retained {retainedBytes:N0} managed bytes.");
        Assert.IsLessThanOrEqualTo(
            128L * 1024 * 1024,
            retainedPrivateBytes,
            $"Repeated map lifetimes retained {retainedPrivateBytes:N0} private bytes.");
    }

    private static async Task<WeakReference<MapControl>[]>
        CreateAndUnloadRoadMapsAsync(int count)
    {
        List<WeakReference<MapControl>> maps = [];
        await MapControlTestHost.LoadUIAsync(
            () => new Grid(),
            async root =>
            {
                Grid host = (Grid)root;
                for (int index = 0; index < count; index++)
                {
                    maps.Add(await CreateAndUnloadRoadMapAsync(host));
                }
            });
        return maps.ToArray();
    }

    private static async Task<WeakReference<MapControl>[]>
        CreateAndUnloadSymbolMapsAsync(int count)
    {
        List<WeakReference<MapControl>> maps = [];
        await MapControlTestHost.LoadUIAsync(
            () => new Grid(),
            async root =>
            {
                Grid host = (Grid)root;
                for (int index = 0; index < count; index++)
                {
                    maps.Add(await CreateAndUnloadSymbolMapAsync(host));
                }
            });
        return maps.ToArray();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference<MapControl>>
        CreateAndUnloadSymbolMapAsync(Grid host)
    {
        TileId tileId = new(4, 8, 8);
        MapboxVectorTileBuilder tileBuilder = new();
        for (int index = 0; index < 64; index++)
        {
            int column = index % 8;
            int row = index / 8;
            tileBuilder.AddPoint(
                "markers",
                256 + (column * 512),
                256 + (row * 512),
                new Dictionary<string, object>
                {
                    ["label"] = index % 2 == 0 ? "AB" : "BA",
                });
        }
        TestVectorTileSource source = TestVectorTileSource.Create(
            tileId,
            tileBuilder.Build(),
            """
            {
              "version": 8,
              "layers": [{
                "type": "symbol",
                "source-layer": "markers",
                "layout": {
                  "text-field": ["get", "label"],
                  "text-font": ["TestFont"],
                  "text-size": 18
                }
              }]
            }
            """,
            "{}",
            [0, 0, 0, 0],
            1,
            1);
        source.AddGlyphs(
            "TestFont",
            TestGlyph.Solid('A'),
            TestGlyph.Solid('B'));
        MapControl map = new()
        {
            Width = 640,
            Height = 480,
            MapStyle = MapStyle.Blank,
        };
        BasicGeoposition center = source.TileCenter;
        Assert.IsTrue(await map.TrySetViewAsync(
            new Geopoint(center),
            tileId.Zoom,
            null,
            null,
            MapAnimationKind.None));
        map.Layers.Add(new TestVectorTileLayer(source));

        await AddAsync(host, map);
        using (CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(5)))
        {
            await map.CaptureRenderedFrameAsync(timeout.Token);
        }
        await RemoveAsync(host, map);
        return new WeakReference<MapControl>(map);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference<MapControl>>
        CreateAndUnloadRoadMapAsync(Grid host)
    {
        MapControl map = new()
        {
            Width = 640,
            Height = 480,
            MapStyle = MapStyle.Road,
            MapServiceToken = string.Empty,
        };
        map.Layers.Add(new MapElementsLayer
        {
            MapElements =
            {
                CreateLifetimeMapIcon(),
            },
        });
        await AddAsync(host, map);
        await MapControlTestUtilities.WaitForAsync(
            () => map.RendererHasDeviceResources &&
                map.ActiveRasterWorkerCount == 1);
        await Task.Delay(100);
        await RemoveAsync(host, map);
        return new WeakReference<MapControl>(map);
    }

    private static MapIcon CreateLifetimeMapIcon()
    {
        SymbolIcon icon = new(Symbol.Pin);
        AutomationProperties.SetName(icon, "Lifetime marker");
        return new MapIcon(
            icon,
            new Geopoint(new BasicGeoposition()));
    }

    private static async Task CollectMapsAsync(
        WeakReference<MapControl>[] maps)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            if (maps.All(map => !map.TryGetTarget(out _)))
            {
                return;
            }
            await MapControlTestHost.RunAsync(() => { });
            await Task.Delay(50);
        }
    }

    private static long GetPrivateMemorySize()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PrivateMemorySize64;
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
