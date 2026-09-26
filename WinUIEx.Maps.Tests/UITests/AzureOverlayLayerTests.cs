using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;
using WinUIEx.Maps.Tests.Input;
using Windows.Devices.Geolocation;

namespace WinUIEx.Maps.Tests.UITests;

[TestClass]
[DoNotParallelize]
public sealed class AzureOverlayLayerTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task TrafficRoadOverlapHasUniformOpacity() =>
        MapControlTestHost.LoadMapControlAsync(
            new BasicGeoposition
            {
                Longitude = 360 * (2048.5 / 4096) - 180,
                Latitude = MapCamera.WorldYToLatitude(2048.5 / 4096),
            }, 12, async map =>
        {
            using RenderingEventListener events = new("VectorLineRenderBatch", "VectorLineComposite");
            byte[] encoded = new MapboxVectorTileBuilder()
                .AddLine("flow", [new(512, 2048), new(2304, 2048)],
                    new Dictionary<string, object> { ["traffic_level"] = 0.7 })
                .AddLine("flow", [new(1792, 2048), new(3584, 2048)],
                    new Dictionary<string, object> { ["traffic_level"] = 0.7 }).Build();
            map.Layers.Add(new TestTrafficTileLayer(-1234, new(12, 2048, 2048), encoded,
                TrafficFlowStyle.Relative));
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            MapRenderFrame frame;
            int interior;
            do
            {
                frame = await map.CaptureRenderedFrameAsync(timeout.Token);
                interior = Blue(-64, 0);
                if (interior < Blue(-64, 20) - 20) break;
                await Task.Delay(20, timeout.Token);
            } while (true);
            int overlap = Blue(0, 0);
            Assert.AreEqual((Blue(-64, 20) + 34) / 2d, interior, 2,
                "Road lines must use the internal 0.5 opacity.");
            Assert.IsNotEmpty(events.Events("VectorLineComposite"));
            var batch = events.Events("VectorLineRenderBatch").Last();
            TestContext.WriteLine($"VectorLineRenderBatch: {string.Join(", ", batch.Payload)}");
            TestContext.WriteLine($"Road interior blue={interior}; overlap blue={overlap}.");
            Assert.AreEqual(interior, overlap, 2,
                "Overlapping road segments must receive opacity once, not once per fragment.");

            foreach (int width in new[] { 512, 704, 640 })
            {
                map.Width = width;
                map.Height = 480;
                map.UpdateLayout();
                await MapControlTestUtilities.WaitForAsync(() => map.ActualWidth == width);
                frame = await map.CaptureRenderedFrameAsync(timeout.Token);
                Assert.AreEqual((int)Math.Ceiling(width * map.XamlRoot.RasterizationScale), frame.Width);
                Assert.AreEqual((Blue(-64, 20) + 34) / 2d, Blue(-64, 0), 2);
                Assert.AreEqual(Blue(-64, 0), Blue(0, 0), 2);
                var composite = events.Events("VectorLineComposite").Last();
                Assert.AreEqual(frame.Width, composite.Payload[1]);
                Assert.AreEqual(frame.Height, composite.Payload[2]);
                int samples = (int)composite.Payload[3]!;
                Assert.AreEqual((long)frame.Width * frame.Height * 4 * (samples == 4 ? 5 : 1),
                    composite.Payload[5], "Resize must replace native target memory, not accumulate it.");
            }

            int Blue(double x, double y)
            {
                double scale = frame.Width / map.ActualWidth;
                int px = (int)(frame.Width / 2 + x * scale);
                int py = (int)(frame.Height / 2 + y * scale);
                return frame.Pixels.Span[(py * frame.Width + px) * 4];
            }
        });

    [TestMethod]
    public Task IncidentPictogramsRenderEveryCategory() =>
        MapControlTestHost.LoadMapControlAsync(
            new BasicGeoposition { Longitude = 11.25, Latitude = MapCamera.WorldYToLatitude(8.5 / 16) },
            5, async map =>
        {
            using RenderingEventListener events = new("VectorSymbolRenderBatch");
            MapboxVectorTileBuilder builder = new();
            for (int index = 0; index < 30; index++)
                builder.AddPoint("incidents", 320 + index % 10 * 384, 1024 + index / 10 * 1024,
                    new Dictionary<string, object> { ["icon_category"] = index % 15, ["magnitude"] = index < 15 ? 2 : 3 });
            AzureTrafficLayer traffic = new() { ShowIncidents = true, MinIncidentZoom = 0 };
            map.Layers.Add(new TestTrafficTileLayer(traffic.IncidentRuntimeId, new(4, 8, 8), builder.Build()));
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (true)
            {
                MapRenderFrame frame = await map.CaptureRenderedFrameAsync(timeout.Token);
                double scale = frame.Width / map.ActualWidth;
                int[] foregroundCounts = new int[30];
                int[] backgroundCounts = new int[30];
                for (int index = 0; index < 30; index++)
                {
                    double cx = frame.Width / 2d + (index % 10 - 4.5) * 48 * scale;
                    double cy = frame.Height / 2d + (index / 10 - 1) * 128 * scale;
                    var foreground = index < 15
                        ? ConnectedComponentAnalyzer.Near(32, 44, 56, tolerance: 8)
                        : ConnectedComponentAnalyzer.Near(255, 255, 255, tolerance: 8);
                    var background = index < 15
                        ? ConnectedComponentAnalyzer.Near(255, 213, 79, tolerance: 8)
                        : ConnectedComponentAnalyzer.Near(216, 59, 59, tolerance: 8);
                    for (int y = (int)(cy - 2 * scale); y < cy + 12 * scale; y++)
                    for (int x = (int)(cx - 10 * scale); x < cx + 10 * scale; x++)
                    {
                        int offset = (y * frame.Width + x) * 4;
                        var pixels = frame.Pixels.Span;
                        if (foreground(pixels[offset + 2], pixels[offset + 1], pixels[offset], pixels[offset + 3]))
                            foregroundCounts[index]++;
                        if (background(pixels[offset + 2], pixels[offset + 1], pixels[offset], pixels[offset + 3]))
                            backgroundCounts[index]++;
                    }
                }
                bool allVisible = foregroundCounts.All(count => count >= 8 * scale * scale) &&
                    backgroundCounts.All(count => count >= 40 * scale * scale);
                if (allVisible || DateTimeOffset.UtcNow >= deadline)
                {
                    Assert.IsNotEmpty(events.Events("VectorSymbolRenderBatch"));
                    string path = await frame.SavePngAsync(Path.Combine(
                        AppContext.BaseDirectory, "TestResults", "incident-pictograms.png"));
                    TestContext.AddResultFile(path);
                    var lastEvent = events.Events("VectorSymbolRenderBatch").Last();
                    TestContext.WriteLine($"VectorSymbolRenderBatch: {string.Join(", ", lastEvent.Payload)}");
                    Assert.IsTrue(allVisible,
                        $"Foreground counts: {string.Join(',', foregroundCounts)}; sign counts: {string.Join(',', backgroundCounts)}.");
                    break;
                }
                await Task.Delay(20, timeout.Token);
            }
        });

    [TestMethod]
    public Task IncidentZoomLimitOnlyGatesIncidentSnapshotsAndPreservesCacheIdentity() =>
        MapControlTestHost.LoadUIAsync(() => new Grid(), _ =>
        {
            AzureTrafficLayer traffic = new() { ShowIncidents = true };
            Assert.AreEqual(12d, traffic.MinIncidentZoom);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var snapshots = traffic.CreateSnapshots("token", null, now).ToArray();
            Assert.AreEqual(0d, snapshots[0].MinZoom);
            Assert.AreEqual(12d, snapshots[1].MinZoom);
            Assert.IsTrue(RasterTileManager.ShouldAcquire(snapshots[0], 11.999));
            Assert.IsFalse(RasterTileManager.ShouldAcquire(snapshots[1], 11.999));
            Assert.IsTrue(RasterTileManager.ShouldAcquire(snapshots[1], 12));
            Assert.IsTrue(RasterTileManager.ShouldAcquire(snapshots[1], 12.001));
            traffic.MinIncidentZoom = 13.5;
            var changed = traffic.CreateSnapshots("token", null, now).ToArray();
            Assert.AreEqual(snapshots[1].SourceKey, changed[1].SourceKey);
            Assert.AreEqual(12d, snapshots[1].MinZoom);
            Assert.AreEqual(13.5, changed[1].MinZoom);
            Assert.IsFalse(RasterTileManager.ShouldAcquire(changed[1], 13.499));
            Assert.IsTrue(RasterTileManager.ShouldAcquire(changed[1], 13.5));
            traffic.MinIncidentZoom = 0;
            Assert.IsTrue(RasterTileManager.ShouldAcquire(
                traffic.CreateSnapshots("token", null, now).Last(), 0));
            return Task.CompletedTask;
        });

    [TestMethod]
    public Task AzurePropertyValidationRestoresValuesWithoutDuplicateNotifications() =>
        MapControlTestHost.LoadUIAsync(() => new Grid(), _ =>
        {
            AzureTrafficLayer traffic = new();
            int changes = 0;
            traffic.Changed += (_, _) => changes++;
            traffic.MinIncidentZoom = 13.5;
            Assert.AreEqual(1, changes);
            foreach (double invalid in new[] { -0.01, 24.01, double.NaN, double.NegativeInfinity, double.PositiveInfinity })
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => traffic.MinIncidentZoom = invalid);
                traffic.SetValue(AzureTrafficLayer.MinIncidentZoomProperty, invalid);
                Assert.AreEqual(13.5, traffic.MinIncidentZoom);
                Assert.AreEqual(1, changes);
            }
            traffic.FlowStyle = TrafficFlowStyle.RelativeDark;
            traffic.SetValue(AzureTrafficLayer.FlowStyleProperty, (TrafficFlowStyle)99);
            Assert.AreEqual(TrafficFlowStyle.RelativeDark, traffic.FlowStyle);
            Assert.AreEqual(2, changes);
            traffic.ClearValue(AzureTrafficLayer.MinIncidentZoomProperty);
            Assert.AreEqual(12d, traffic.MinIncidentZoom);
            Assert.AreEqual(3, changes);
            traffic.MinIncidentZoom = 24;
            Assert.AreEqual(24d, traffic.MinIncidentZoom);
            AzureWeatherLayer weather = new() { Kind = WeatherLayerKind.Infrared };
            int weatherChanges = 0;
            weather.Changed += (_, _) => weatherChanges++;
            weather.SetValue(AzureWeatherLayer.KindProperty, (WeatherLayerKind)99);
            Assert.AreEqual(WeatherLayerKind.Infrared, weather.Kind);
            Assert.AreEqual(0, weatherChanges);
            weather.Timestamp = DateTimeOffset.UnixEpoch;
            Assert.AreEqual(1, weatherChanges);
            Assert.AreEqual(0L, weather.GetRefreshVersion(DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        });

    [TestMethod]
    public Task IncidentZoomLimitHidesCachedGeometryAndDisablesPicking() =>
        MapControlTestHost.LoadMapControlAsync(
            new BasicGeoposition { Longitude = 11.25, Latitude = MapCamera.WorldYToLatitude(8.5 / 16) },
            12.5, async map =>
        {
            using RenderingEventListener events = new("SceneChanged");
            AzureTrafficLayer traffic = new() { ShowIncidents = true, MinIncidentZoom = 12.5 };
            byte[] tile = new MapboxVectorTileBuilder().AddPoint("Traffic incident POI", 2048, 2048,
                new Dictionary<string, object> { ["id"] = "zoom-fixture" }).Build();
            var fixture = new TestTrafficTileLayer(traffic.IncidentRuntimeId, new(4, 8, 8), tile)
            {
                MinZoom = traffic.MinIncidentZoom,
            };
            map.Layers.Add(fixture);
            map.Layers.Add(traffic);
            int incidentTaps = 0, mapTaps = 0;
            traffic.IncidentTapped += (_, args) => { incidentTaps++; args.Handled = true; };
            map.Tapped += (_, _) => mapTaps++;
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            await WaitForMarker(true);
            var input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            input.Mouse.Click();
            while (incidentTaps == 0) await Task.Delay(20, timeout.Token);
            int scenesBeforeZoom = events.Events("SceneChanged").Length;
            await map.TrySetViewAsync(map.Center!, 12.25, 0, 0, MapAnimationKind.None);
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, 12.25);
            await WaitForMarker(false);
            Assert.IsGreaterThan(scenesBeforeZoom, events.Events("SceneChanged").Length,
                "Crossing a fractional display bound must notify the scheduler even with unchanged tile coverage.");
            await Task.Delay(TimeSpan.FromMilliseconds(GetDoubleClickTime() + 50), timeout.Token);
            input.Mouse.Click();
            while (mapTaps == 0) await Task.Delay(20, timeout.Token);
            Assert.AreEqual(1, incidentTaps);
            await map.TrySetViewAsync(map.Center, 12.5, 0, 0, MapAnimationKind.None);
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, 12.5);
            await WaitForMarker(true);

            async Task WaitForMarker(bool expected)
            {
                while (true)
                {
                    var frame = await map.CaptureRenderedFrameAsync(timeout.Token);
                    bool visible = ConnectedComponentAnalyzer.Find(frame,
                        ConnectedComponentAnalyzer.Near(255, 213, 79, tolerance: 8))
                        .Sum(component => component.PixelCount) >= 20;
                    if (visible == expected) return;
                    await Task.Delay(20, timeout.Token);
                }
            }
        });

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [TestMethod]
    public Task LiveRasterRefreshKeepsOldPixelsUntilReplacementCommits() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            TaskCompletionSource requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            object refreshIdentity = new();
            var layer = new RefreshFixtureLayer(new RefreshFixtureSession(
                refreshIdentity, false, requested, release));
            map.Layers.Add(layer);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            await WaitForColor(0, 0, 255);
            layer.Replace(new RefreshFixtureSession(refreshIdentity, true, requested, release));
            try
            {
                await requested.Task.WaitAsync(timeout.Token);
                // Read the renderer directly because acquisition is intentionally blocked.
                var renderer = (MapRenderer)typeof(MapControl).GetField(
                    "_renderer", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!.GetValue(map)!;
                var retained = await renderer.CaptureFrameAsync(timeout.Token);
                Assert.IsTrue(ConnectedComponentAnalyzer.Find(retained,
                    ConnectedComponentAnalyzer.Near(0, 0, 255, tolerance: 8),
                    minimumPixelCount: 1000).Length > 0);
            }
            finally
            {
                release.TrySetResult();
            }
            await WaitForColor(0, 255, 0);

            async Task WaitForColor(byte red, byte green, byte blue)
            {
                while (true)
                {
                    var frame = await map.CaptureRenderedFrameAsync(timeout.Token);
                    if (ConnectedComponentAnalyzer.Find(frame,
                        ConnectedComponentAnalyzer.Near(red, green, blue, tolerance: 8),
                        minimumPixelCount: 1000).Length > 0)
                        return;
                    await Task.Delay(20, timeout.Token);
                }
            }
        });

    private sealed class RefreshFixtureLayer(RefreshFixtureSession session) : TileLayer
    {
        private RefreshFixtureSession _session = session;
        internal void Replace(RefreshFixtureSession replacement)
        {
            _session = replacement;
            NotifyChanged(TileUrlProperty);
        }
        internal override TileLayerSnapshot CreateSnapshot() =>
            new(RuntimeId, Revision, _session, 0, 24, IsVisible, Opacity, TimeSpan.Zero);
    }

    private sealed class RefreshFixtureSession(
        object refreshIdentity, bool refreshing, TaskCompletionSource requested,
        TaskCompletionSource release) : RasterTileAcquisitionSession
    {
        internal override object SourceKey => this;
        internal override object RefreshIdentity => refreshIdentity;
        internal override RasterSourceKind SourceKind => RasterSourceKind.Custom;
        internal override int TileSize => 256;
        internal override int MinSourceZoom => 0;
        internal override int MaxSourceZoom => 22;
        internal override bool CanAcquire => true;
        internal override bool IncludesTile(TileId id) => true;
        internal override int GetSourceZoom(MapScene scene) => scene.TileZoom;
        internal override async Task<DecodedRasterTile> GetTileAsync(
            TileId id, CancellationToken cancellationToken)
        {
            if (refreshing)
            {
                requested.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            var tile = TestRasterTileSource.Solid(256, 0,
                refreshing ? (byte)255 : (byte)0, refreshing ? (byte)0 : (byte)255);
            return new(id, tile.Pixels, tile.Width, tile.Height, 0, 0);
        }
    }

    [TestMethod]
    public Task AzureAttributionAggregatesIndependentSourcesAndDropsRemovedLayers() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            map.MapStyle = MapStyle.RoadRaster;
            var first = new AttributionFixtureLayer("first");
            var second = new AttributionFixtureLayer("second");
            map.Layers.Add(first);
            map.Layers.Add(second);
            TextBlock text = Descendants(map).OfType<TextBlock>().Single(t => t.Name == "PART_Attribution");
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!AutomationProperties.GetName(text).Contains("second", StringComparison.Ordinal) &&
                DateTimeOffset.UtcNow < deadline)
                await Task.Delay(20);
            Assert.AreEqual("Map attribution: first, second", AutomationProperties.GetName(text));
            map.Layers.Remove(first);
            Assert.AreEqual("Map attribution: second", AutomationProperties.GetName(text));
            second.Opacity = 0;
            Assert.IsEmpty(text.Inlines);
            map.MapStyle = MapStyle.Blank;
            second.Opacity = 1;
            Assert.IsEmpty(text.Inlines);
        });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }

    private sealed class AttributionFixtureLayer(string text) : AzureTileLayer
    {
        private readonly AttributionFixtureSession _session = new(text);
        internal override IEnumerable<TileLayerSnapshot> CreateSnapshots(
            string token, string? language, DateTimeOffset now) => [Snapshot(_session)];
    }

    private sealed class AttributionFixtureSession(string text) : RasterTileAcquisitionSession
    {
        internal override object SourceKey => this;
        internal override RasterSourceKind SourceKind => RasterSourceKind.Azure;
        internal override int TileSize => 256;
        internal override int MinSourceZoom => 0;
        internal override int MaxSourceZoom => 22;
        internal override bool CanAcquire => true;
        internal override bool SupportsAttribution => true;
        internal override bool IncludesTile(TileId id) => false;
        internal override int GetSourceZoom(MapScene scene) => scene.TileZoom;
        internal override Task<DecodedRasterTile> GetTileAsync(TileId id, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The attribution fixture must not acquire pixels.");
        internal override Task<string?> GetAttributionAsync(int zoom, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(text);
    }

    [TestMethod]
    public Task FlowRendersAzureVectorGeometryWithCongestionColors() =>
        MapControlTestHost.LoadMapControlAsync(
            new BasicGeoposition { Longitude = 11.25, Latitude = MapCamera.WorldYToLatitude(8.5 / 16) },
            4, async map =>
        {
            byte[] tile = new MapboxVectorTileBuilder()
                .AddLine("Traffic flow", [new(512, 1536), new(3584, 1536)],
                    new Dictionary<string, object> { ["traffic_level"] = 0.7 })
                .AddLine("Traffic flow", [new(512, 2560), new(3584, 2560)],
                    new Dictionary<string, object> { ["traffic_level"] = 1d }).Build();
            map.Layers.Add(new TestTrafficTileLayer(42, new(4, 8, 8), tile, TrafficFlowStyle.Relative));
            using RenderingEventListener events = new("VectorLineRenderBatch", "VectorTileCommitSummary");
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            while (true)
            {
                var frame = await map.CaptureRenderedFrameAsync(timeout.Token);
                bool slow = ConnectedComponentAnalyzer.Find(frame,
                    ConnectedComponentAnalyzer.Near(240, 182, 137, tolerance: 8), minimumPixelCount: 50).Length > 0;
                bool free = ConnectedComponentAnalyzer.Find(frame,
                    ConnectedComponentAnalyzer.Near(140, 187, 157, tolerance: 8), minimumPixelCount: 50).Length > 0;
                if (slow && free) break;
                await Task.Delay(20, timeout.Token);
            }
            Assert.IsNotEmpty(events.Events("VectorTileCommitSummary"));
            Assert.IsNotEmpty(events.Events("VectorLineRenderBatch"));
        });

    [TestMethod]
    public Task IncidentTapUsesRenderedGeometryAndDispatchesTypedPayload() =>
        MapControlTestHost.LoadMapControlAsync(
            new BasicGeoposition { Longitude = 11.25, Latitude = MapCamera.WorldYToLatitude(8.5 / 16) },
            4, async map =>
        {
            using RenderingEventListener events = new("VectorLineRenderBatch", "VectorTileCommitSummary");
            TileId tileId = new(4, 8, 8);
            byte[] tile = new MapboxVectorTileBuilder().AddLine("incident",
                [new(512, 2048), new(3584, 2048)],
                new Dictionary<string, object> { ["id"] = "fixture", ["icon_category"] = 1, ["magnitude"] = 2 })
                .Build();
            AzureTrafficLayer traffic = new() { ShowIncidents = true, MinIncidentZoom = 0 };
            // Route a deterministic fixture through the production scheduler under the incident source ID.
            map.Layers.Add(new TestTrafficTileLayer(traffic.IncidentRuntimeId, tileId, tile));
            map.Layers.Add(traffic);
            AzureTrafficIncidentEventArgs? selected = null;
            traffic.IncidentTapped += (_, args) => { selected = args; args.Handled = true; };
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            var frame = await map.CaptureRenderedFrameAsync(timeout.Token);
            var red = ConnectedComponentAnalyzer.Find(frame,
                ConnectedComponentAnalyzer.Near(236, 180, 120, tolerance: 8), minimumPixelCount: 100);
            while (red.Length == 0 && !timeout.IsCancellationRequested)
            {
                await Task.Delay(20, timeout.Token);
                frame = await map.CaptureRenderedFrameAsync(timeout.Token);
                red = ConnectedComponentAnalyzer.Find(frame,
                    ConnectedComponentAnalyzer.Near(236, 180, 120, tolerance: 8), minimumPixelCount: 100);
            }
            Assert.IsTrue(red.Length > 0,
                $"Incident must be drawn before input; commits={events.Events("VectorTileCommitSummary").Length}, line frames={events.Events("VectorLineRenderBatch").Length}");
            int taps = 0;
            map.Tapped += (_, _) => taps++;
            var input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            input.Mouse.Click();
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (selected is null && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(20);
            Assert.IsNotNull(selected, $"A native tap on the rendered incident must raise IncidentTapped; map taps={taps}, geometry center={red[0].Bounds.CenterX},{red[0].Bounds.CenterY}, frame={frame.Width}x{frame.Height}.");
            Assert.AreEqual("fixture", selected.Id);
            Assert.AreEqual(1, selected.Category);
            Assert.AreEqual(2, selected.Magnitude);
            Assert.AreEqual(11.25, selected.Location.Position.Longitude, 0.05);
            Assert.AreEqual(map.ActualWidth / 2, selected.Position.X, 2);
            Assert.AreEqual(map.ActualHeight / 2, selected.Position.Y, 2);
        });

    [TestMethod]
    public Task AzureSnapshotsPreserveOrderingAndImmutableAuthentication() =>
        MapControlTestHost.LoadUIAsync(() => new Grid(), _ =>
        {
            AzureTrafficLayer traffic = new() { ShowIncidents = true };
            AzureWeatherLayer weather = new() { Opacity = 0.5 };
            TileLayer custom = new();
            MapElementsLayer elements = new();
            MapLayer[] layers = [weather, custom, traffic, elements];
            DateTimeOffset now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
            var original = MapControl.CreateLayerSnapshotPublication(
                MapControl.CreateAzureBaseLayer(MapStyle.Satellite, "old"),
                layers, false, "old", "en", now);
            Assert.AreEqual(6, original.RenderPlan.Length);
            Assert.AreSequenceEqual([-1, 0, 1, 2, 2, 3],
                original.RenderPlan.Select(s => s.LayerIndex).ToArray());
            Assert.AreEqual(5, original.RasterLayers.Select(s => s.RuntimeId).Distinct().Count());
            Assert.AreEqual(0.5, original.RasterLayers[1].Opacity);
            Assert.IsTrue(original.RasterLayers.All(s => s.FadeDuration == TimeSpan.Zero));
            var replacement = MapControl.CreateLayerSnapshotPublication(null, layers, true, "new", "fr", now);
            Assert.AreNotEqual(original.RasterLayers[1].SourceKey, replacement.RasterLayers[0].SourceKey);
            var same = weather.CreateSnapshots("old", "en", now).Single();
            Assert.AreEqual(original.RasterLayers[1].SourceKey, same.SourceKey);

            var blank = MapControl.CreateLayerSnapshotPublication(null, layers);
            Assert.AreEqual(2, blank.RenderPlan.Length);
            Assert.AreEqual(custom.RuntimeId, blank.RasterLayers.Single().RuntimeId);
            traffic.ShowIncidents = false;
            Assert.AreEqual(1, traffic.CreateSnapshots("old", null, now).Count());
            return Task.CompletedTask;
        });

    [TestMethod]
    public Task LatestRefreshIsAlignedAndStopsForHiddenOrFixedLayers() =>
        MapControlTestHost.LoadUIAsync(() => new Grid(), _ =>
        {
            AzureWeatherLayer weather = new();
            DateTimeOffset now = new(2026, 9, 24, 12, 3, 20, TimeSpan.Zero);
            Assert.IsNull(weather.Timestamp);
            Assert.AreEqual(TimeSpan.FromSeconds(100), MapControl.GetAzureRefreshDelay([weather], now));
            weather.Kind = WeatherLayerKind.Infrared;
            Assert.AreEqual(TimeSpan.FromSeconds(400), MapControl.GetAzureRefreshDelay([weather], now));
            weather.Timestamp = now;
            Assert.IsNull(MapControl.GetAzureRefreshDelay([weather], now));
            var fixedKey = weather.CreateSnapshots("token", null, now).Single().SourceKey;
            Assert.AreEqual(fixedKey, weather.CreateSnapshots("token", null, now.AddHours(1)).Single().SourceKey);
            weather.Timestamp = null;
            weather.IsVisible = false;
            Assert.IsNull(MapControl.GetAzureRefreshDelay([weather], now));
            weather.IsVisible = true;
            weather.Opacity = 0;
            Assert.IsNull(MapControl.GetAzureRefreshDelay([weather], now));
            AzureTrafficLayer traffic = new();
            Assert.AreEqual(TrafficFlowStyle.Relative, traffic.FlowStyle);
            Assert.IsFalse(traffic.ShowIncidents);
            Assert.AreEqual(TimeSpan.FromSeconds(40), MapControl.GetAzureRefreshDelay([traffic], now));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => traffic.FlowStyle = (TrafficFlowStyle)99);
            traffic.SetValue(AzureTrafficLayer.FlowStyleProperty, (TrafficFlowStyle)99);
            Assert.AreEqual(TrafficFlowStyle.Relative, traffic.FlowStyle);
            return Task.CompletedTask;
        });
}
