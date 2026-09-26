using Azure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WindowsMapsSample;
using WindowsMapsSample.Controls;
using WindowsMapsSample.Models;
using WindowsMapsSample.Services;
using WinUIEx.Maps.Tests.UITestHelpers;
using WinUIEx.Maps.Tests.Input;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests.UITests;

[TestClass]
[DoNotParallelize]
public sealed class WindowsMapsSampleUITests
{
    private static bool _resourcesLoaded;
    private static Windows.Graphics.SizeInt32 _windowSize;
    private static ElementTheme _theme;

    [TestCleanup]
    public Task RestoreHostSize() => MapControlTestHost.RunAsync(() =>
    {
        MapControlTestHost.ContentHost.Width = 640;
        MapControlTestHost.ContentHost.Height = 480;
        MapControlTestHost.Window.AppWindow.Resize(_windowSize);
        ((FrameworkElement)MapControlTestHost.Window.Content).RequestedTheme = _theme;
    });

    [TestMethod]
    public Task RouteManeuverIconConverter_SelectsMatchingManeuver() =>
        MapControlTestHost.LoadUIAsync(() => new Grid(), _ =>
        {
            var converter = new RouteStepIconConverter();
            foreach (RouteManeuverIcon icon in Enum.GetValues<RouteManeuverIcon>())
            {
                Assert.AreEqual(Visibility.Visible,
                    converter.Convert(icon, typeof(Visibility), icon.ToString(), string.Empty));
                Assert.AreEqual(Visibility.Collapsed,
                    converter.Convert(icon, typeof(Visibility), "Other", string.Empty));
            }

            return Task.CompletedTask;
        });

    [TestMethod]
    public Task Favorites_AreRestoredWhenPageIsCreated()
    {
        var favorite = new Place("Home", "Seattle, WA", 47.6, -122.33);
        return LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, _) =>
        {
            var favorites = Find<ListView>(page, "FavoritesList");
            await WaitAsync(() => favorites.Items.Count == 1);
            Assert.AreEqual(favorite, Assert.ContainsSingle(favorites.Items));
            var map = Find<MapControl>(page, "Map");
            Assert.HasCount(1, ((WinUIEx.Maps.MapElementsLayer)map.Layers[1]).MapElements);

            await PressAsync(Find<Button>(page, "DirectionsNavigation"));
            var origin = Find<AutoSuggestBox>(page, "OriginInput");
            await WaitAsync(() => origin.IsLoaded && Descendants(origin).OfType<TextBox>().Any());
            Descendants(origin).OfType<TextBox>().Single().Focus(FocusState.Programmatic);
            await WaitAsync(() => origin.ItemsSource is System.Collections.ObjectModel.ObservableCollection<EndpointSuggestion> suggestions &&
                suggestions.Any(suggestion => suggestion.Label == "Home" && suggestion.Place == favorite));

            await PressAsync(Find<Button>(page, "FavoritesNavigation"));
            await PressItemAsync(favorites, 0);
            await WaitAsync(() => Find<TextBlock>(page, "PlaceName").Text == "Home");
            Assert.AreEqual(Visibility.Visible, Find<Button>(page, "EditSelectedFavoriteNameButton").Visibility);
        }, favoritesStore: new MemoryFavoritesStore([favorite]));
    }

    [TestMethod]
    [DataRow("US", true)]
    [DataRow("DK", true)]
    [DataRow("FR", false)]
    [DataRow("AU", true)]
    [DataRow("FJ", true)]
    [DataRow("ZZ", false)]
    public Task StartupRegion_FramesCountryWithoutSearchAndDoesNotResetOnResize(string region, bool savedToken) =>
        LoadSampleAsync(new MemoryTokenStore { Token = savedToken ? "saved-key" : "" }, new FakeMapsService(),
            async (page, _, service) =>
        {
            if (!savedToken)
            {
                Find<PasswordBox>(page, "TokenInput").Password = "test-key";
                await PressAsync(Find<Button>(page, "ValidateButton"));
                await WaitAsync(() => Find<Grid>(page, "Shell").Visibility == Visibility.Visible);
            }
            var map = Find<MapControl>(page, "Map");
            var area = StartupRegion.GetBounds(region);
            var bounds = new Windows.Devices.Geolocation.GeoboundingBox(
                new() { Latitude = area.North, Longitude = area.West },
                new() { Latitude = area.South, Longitude = area.East });
            Assert.IsTrue(MapControl.TryCalculateBoundsView(bounds, new(24), map.ActualWidth, map.ActualHeight,
                0, 0, out var expectedCenter, out double expectedZoom));
            await WaitAsync(() => Math.Abs(map.Center!.Position.Longitude - expectedCenter.Longitude) < 0.001);
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
            Assert.AreEqual(expectedCenter.Latitude, map.Center!.Position.Latitude, 0.001);
            Assert.AreEqual(expectedZoom, map.ZoomLevel, 0.001);
            Assert.IsNull(service.LastSearchArea);
            Assert.AreEqual(0, service.ReverseCalls);
            Assert.IsEmpty(Find<TabView>(page, "SearchTabs").TabItems);

            await map.TrySetViewAsync(new(new() { Latitude = 47.6062, Longitude = -122.3321 }),
                11, 0, 0, MapAnimationKind.None);
            page.Width = 900;
            page.UpdateLayout();
            await WaitAsync(() => map.ActualWidth == 900);
            Assert.AreEqual(-122.3321, map.Center!.Position.Longitude, 0.00001);
            Assert.AreEqual(11, map.ZoomLevel);
        }, startupRegion: region);

    [TestMethod]
    public Task Location_ShowsDotOnRepeatedUseWithoutOpeningPaneOrOverzooming() =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, _) =>
        {
            var map = Find<MapControl>(page, "Map");
            var pane = Find<Border>(page, "Pane");
            pane.Visibility = Visibility.Collapsed;
            var button = Descendants(Find<MapNavigationControl>(page, "Navigation")).OfType<Button>()
                .Single(button => button.Name == "PART_Locate");
            MapIcon? marker = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                await PressAsync(button);
                await WaitAsync(() => ((MapElementsLayer)map.Layers[3]).MapElements.Count == 1);
                var current = Assert.IsInstanceOfType<MapIcon>(((MapElementsLayer)map.Layers[3]).MapElements[0]);
                if (marker is not null) Assert.AreSame(marker, current);
                marker = current;
                await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
                Assert.IsLessThanOrEqualTo(16, map.ZoomLevel);
                Assert.AreEqual(Visibility.Collapsed, pane.Visibility);
                Assert.IsFalse(Find<InfoBar>(page, "Status").IsOpen);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (true)
                {
                    var frame = await map.CaptureRenderedFrameAsync(timeout.Token);
                    if (ConnectedComponentAnalyzer.Find(frame, ConnectedComponentAnalyzer.Near(59, 142, 219, tolerance: 12),
                        minimumPixelCount: 20).Any()) break;
                    await Task.Delay(20, timeout.Token);
                }
            }
        }, new FakeLocationService(false));

    [TestMethod]
    public Task LocationImage_RemainsOpaqueWhenRecaptured()
        => LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, _) =>
        {
            var map = Find<MapControl>(page, "Map");
            using var events = new RenderingEventListener("IconRasterized");
            var locate = Descendants(Find<MapNavigationControl>(page, "Navigation")).OfType<Button>()
                .Single(button => button.Name == "PART_Locate");
            await PressAsync(locate);
            await WaitAsync(() => ((MapElementsLayer)map.Layers[3]).MapElements.Count == 1 &&
                events.Events("IconRasterized").Any(capture => (int)capture.Payload[4]! > 0));
            var marker = (MapIcon)((MapElementsLayer)map.Layers[3]).MapElements[0];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await map.CaptureRenderedFrameAsync(timeout.Token);
            for (int attempt = 0; attempt < 8; attempt++)
            {
                await WaitAsync(() => !marker.IconElement.IsLoaded);
                int captures = events.Events("IconRasterized").Length;
                marker.IconElement.Width = attempt % 2 == 0 ? 33 : 32;
                await WaitAsync(() => events.Events("IconRasterized").Length > captures);
                Assert.IsGreaterThan(0, (int)events.Events("IconRasterized")[captures].Payload[4]!,
                    "Recapturing the loaded location SVG must not publish a transparent texture.");
            }
        }, new FakeLocationService(false));

    [TestMethod]
    public Task Location_RemainsVisibleAfterDroppingPins()
        => LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, service) =>
        {
            var map = Find<MapControl>(page, "Map");
            var locate = Descendants(Find<MapNavigationControl>(page, "Navigation")).OfType<Button>()
                .Single(button => button.Name == "PART_Locate");
            await PressAsync(locate);
            await WaitAsync(() => ((MapElementsLayer)map.Layers[3]).MapElements.Count == 1);
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
            var marker = ((MapElementsLayer)map.Layers[3]).MapElements[0];
            var input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            for (int attempt = 0; attempt < 4; attempt++)
            {
                input.Mouse.Click(input.PointAt(0.8, attempt % 2 == 0 ? 0.35 : 0.65));
                await WaitAsync(() => service.ReverseCalls == attempt + 1 &&
                    Find<TextBlock>(page, "PlaceName").Text == $"Map location {attempt + 1}");
                await AssertDotAsync();
                await PressAsync(locate);
                await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
                await AssertDotAsync();
            }

            async Task AssertDotAsync()
            {
                Assert.AreSame(marker, ((MapElementsLayer)map.Layers[3]).MapElements[0]);
                while (true)
                {
                    var frame = await map.CaptureRenderedFrameAsync(timeout.Token);
                    if (ConnectedComponentAnalyzer.Find(frame, ConnectedComponentAnalyzer.Near(59, 142, 219, tolerance: 12),
                        minimumPixelCount: 20).Any()) return;
                    await Task.Delay(20, timeout.Token);
                }
            }
        }, new FakeLocationService(false));

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Search_UsesMapAreaAndFitsCityOrCountryBounds(bool country) =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" },
            new FakeMapsService { Place = country
                ? new Place("France", "France", 46.6, 2.2, new PlaceBounds(-5.2, 41.3, 9.6, 51.1))
                : new Place("Seattle", "Seattle, WA", 47.6, -122.33, new PlaceBounds(-122.46, 47.49, -122.22, 47.73)) },
            async (page, _, service) =>
        {
            var map = Find<MapControl>(page, "Map");
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
            var input = Find<AutoSuggestBox>(page, "SearchInput");
            input.Text = service.Place.Name;
            await PressAsync(Descendants(input).OfType<Button>().Single(button => button.Name == "QueryButton"));
            await WaitAsync(() => Find<ListView>(page, "SearchResults").Items.Count == 1);
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
            Assert.IsNotNull(service.LastSearchArea);
            Assert.AreEqual(47.6062, service.LastSearchArea.Latitude, 0.001);
            Assert.AreEqual(-122.3321, service.LastSearchArea.Longitude, 0.001);
            Assert.IsGreaterThan(1000, service.LastSearchArea.RadiusInMeters);
            Assert.IsLessThanOrEqualTo(country ? 7 : 12, map.ZoomLevel);
            await PressItemAsync(Find<ListView>(page, "SearchResults"), 0);
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
            Assert.IsLessThanOrEqualTo(country ? 7 : 12, map.ZoomLevel);
        });

    [TestMethod]
    public Task SearchPane_StaysCompactUntilResultsOrDetailsAreShown() =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, _) =>
        {
            var pane = Find<Border>(page, "Pane");
            var header = Find<Grid>(page, "PaneHeader");
            var search = Find<AutoSuggestBox>(page, "SearchInput");
            await WaitAsync(() => pane.ActualHeight > 0 && search.ActualHeight > 0);
            Assert.AreEqual(VerticalAlignment.Top, pane.VerticalAlignment);
            Assert.AreEqual(Visibility.Collapsed, header.Visibility);
            Assert.AreEqual(32, search.ActualHeight, 1);
            Assert.AreEqual(search.ActualHeight + 2, pane.ActualHeight, 1);

            search.Text = "Seattle";
            await PressAsync(Descendants(search).OfType<Button>().Single(button => button.Name == "QueryButton"));
            var results = Find<ListView>(page, "SearchResults");
            await WaitAsync(() => results.Items.Count == 1 && pane.VerticalAlignment == VerticalAlignment.Stretch);
            Assert.AreEqual(Visibility.Visible, header.Visibility);
            await PressItemAsync(results, 0);
            await WaitAsync(() => Find<StackPanel>(page, "PlaceDetails").Visibility == Visibility.Visible);
            Assert.AreEqual(VerticalAlignment.Stretch, pane.VerticalAlignment);

            await PressAsync(Find<Button>(page, "SearchNavigation"));
            await WaitAsync(() => pane.VerticalAlignment == VerticalAlignment.Top &&
                Math.Abs(pane.ActualHeight - search.ActualHeight - 2) <= 1);
            Assert.AreEqual(Visibility.Collapsed, header.Visibility);
            Assert.IsEmpty(results.Items);

            await PressAsync(Find<Button>(page, "DirectionsNavigation"));
            Assert.AreEqual(VerticalAlignment.Stretch, pane.VerticalAlignment);
            Assert.AreEqual(Visibility.Visible, header.Visibility);
            await PressAsync(Find<Button>(page, "SearchNavigation"));
            Assert.AreEqual(VerticalAlignment.Top, pane.VerticalAlignment);
        });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task MapDoubleClick_ZoomsWithoutDroppingOrReplacingPin(bool existingPin)
        => LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(),
            async (page, _, service) =>
        {
            var map = Find<MapControl>(page, "Map");
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
            var input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            var layer = (MapElementsLayer)map.Layers[4];
            var tabs = Find<TabView>(page, "SearchTabs");
            if (existingPin)
            {
                input.Mouse.Click(input.PointAt(0.65, 0.5));
                await WaitAsync(() => Find<TextBlock>(page, "PlaceName").Text == "Map location 1");
            }
            var marker = layer.MapElements.FirstOrDefault();
            var location = (marker as WinUIEx.Maps.MapIcon)?.Location;
            double zoom = map.ZoomLevel;

            input.Mouse.DoubleClick(input.PointAt(0.8, 0.7));

            await WaitAsync(() => map.ZoomLevel == zoom + 1);
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, zoom + 1);
            // Observe beyond the single-click deadline to catch delayed pin placement as well.
            await Task.Delay(TimeSpan.FromMilliseconds(GetDoubleClickTime() + 100));
            Assert.AreEqual(existingPin ? 1 : 0, service.ReverseCalls);
            Assert.HasCount(existingPin ? 1 : 0, layer.MapElements);
            Assert.HasCount(existingPin ? 1 : 0, tabs.TabItems);
            if (existingPin)
            {
                Assert.AreSame(marker, layer.MapElements[0]);
                Assert.AreSame(location, ((WinUIEx.Maps.MapIcon)marker!).Location);
                Assert.AreEqual("Map location 1", Find<TextBlock>(page, "PlaceName").Text);
            }

            input.Mouse.Click(input.PointAt(0.75, 0.4));
            await WaitAsync(() => service.ReverseCalls == (existingPin ? 2 : 1));
            Assert.HasCount(1, layer.MapElements);
            Assert.HasCount(1, tabs.TabItems);
        });

    [TestMethod]
    public Task MapTap_KeepsPinWhileItsAddressResolves()
        => LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" },
            new FakeMapsService { ReverseCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously) },
            async (page, _, service) =>
        {
            var map = Find<MapControl>(page, "Map");
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
            var input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            input.Mouse.Click(input.PointAt(0.8, 0.5));
            var layer = (MapElementsLayer)map.Layers[4];
            await WaitAsync(() => service.ReverseCalls == 1 && layer.MapElements.Count == 1);
            var marker = layer.MapElements[0];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var frame = await map.CaptureRenderedFrameAsync(timeout.Token);
                if (ConnectedComponentAnalyzer.Find(frame, ConnectedComponentAnalyzer.Near(0, 99, 177, tolerance: 12),
                    minimumPixelCount: 20).Any()) break;
                await Task.Delay(20, timeout.Token);
            }
            using var events = new RenderingEventListener("IconRasterized");
            service.ReverseCompletion!.SetResult(service.ReversePosition);
            await WaitAsync(() => Find<TextBlock>(page, "PlaceName").Text == "Map location 1");
            await map.CaptureRenderedFrameAsync(timeout.Token);
            Assert.AreSame(marker, layer.MapElements[0]);
            Assert.IsEmpty(events.Events("IconRasterized"),
                "Resolving a pin's address must not replace its image.");
        });

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public Task Location_AutocompleteFirstOptionSetsEitherEndpoint(bool denied, bool destination) =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, _) =>
        {
            await PressAsync(Find<Button>(page, "DirectionsNavigation"));
            var endpoint = Find<AutoSuggestBox>(page, destination ? "DestinationInput" : "OriginInput");
            await WaitAsync(() => endpoint.IsLoaded && endpoint.ActualWidth > 0);
            Descendants(endpoint).OfType<TextBox>().Single().Focus(FocusState.Programmatic);
            await WaitAsync(() => endpoint.IsSuggestionListOpen);
            var suggestions = Assert.IsInstanceOfType<System.Collections.ObjectModel.ObservableCollection<EndpointSuggestion>>(endpoint.ItemsSource);
            Assert.AreEqual("My location", suggestions[0].Label);
            Assert.IsNull(suggestions[0].Place);
            ListView? list = null;
            await WaitAsync(() =>
            {
                list = VisualTreeHelper.GetOpenPopupsForXamlRoot(page.XamlRoot)
                    .SelectMany(popup => Descendants(popup.Child)).OfType<ListView>()
                    .SingleOrDefault(list => ReferenceEquals(list.ItemsSource, endpoint.ItemsSource));
                return list is { IsLoaded: true };
            });
            await PressItemAsync(list!, 0);
            var map = Find<MapControl>(page, "Map");
            if (denied)
            {
                await WaitAsync(() => Find<InfoBar>(page, "Status").Message.Contains("disabled", StringComparison.Ordinal));
                Assert.IsEmpty(((WinUIEx.Maps.MapElementsLayer)map.Layers[3]).MapElements);
                Assert.IsEmpty(((WinUIEx.Maps.MapElementsLayer)map.Layers[0]).MapElements);
            }
            else
            {
                await WaitAsync(() => endpoint.Text == "My location" && ((WinUIEx.Maps.MapElementsLayer)map.Layers[3]).MapElements.Count == 1);
                Assert.HasCount(1, ((WinUIEx.Maps.MapElementsLayer)map.Layers[3]).MapElements);
                Assert.AreEqual("", Find<AutoSuggestBox>(page, destination ? "OriginInput" : "DestinationInput").Text);
                await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
                Assert.IsFalse(Find<InfoBar>(page, "Status").IsOpen);
                Assert.IsLessThanOrEqualTo(16, map.ZoomLevel);
            }
        }, new FakeLocationService(denied));

    [TestMethod]
    public Task Directions_RendersGeometryAndClearsWhenModeChanges() =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, service) =>
        {
            await PressAsync(Find<Button>(page, "DirectionsNavigation"));
            Find<AutoSuggestBox>(page, "OriginInput").Text = "Seattle";
            Find<AutoSuggestBox>(page, "DestinationInput").Text = "Portland";
            await PressAsync(Find<Button>(page, "GetDirectionsButton"));
            await WaitAsync(() => Find<ListView>(page, "Itinerary").Items.Count == 1);
            var map = Find<MapControl>(page, "Map");
            var layer = (WinUIEx.Maps.MapElementsLayer)map.Layers[0];
            Assert.HasCount(4, layer.MapElements);
            var path = Assert.IsInstanceOfType<MapPolyline>(layer.MapElements[1]).Path;
            Assert.IsNotNull(path);
            Assert.AreEqual(47.6, path.Positions[0].Latitude);
            Assert.AreEqual(45.52, path.Positions[1].Latitude);
            Assert.IsFalse(service.LastWalking);
            var modes = Find<SelectorBar>(page, "TravelModePicker");
            modes.SelectedItem = modes.Items[1];
            await WaitAsync(() => layer.MapElements.Count == 0);
            await PressAsync(Find<Button>(page, "GetDirectionsButton"));
            await WaitAsync(() => layer.MapElements.Count == 4);
            Assert.IsTrue(service.LastWalking);
        });

    [TestMethod]
    [DataRow("success")]
    [DataRow("failure")]
    [DataRow("empty")]
    [DataRow("cancel")]
    public Task Directions_ProgressTracksPendingRequest(string outcome) =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" },
            new FakeMapsService { RouteCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously) }, async (page, _, service) =>
        {
            await PressAsync(Find<Button>(page, "DirectionsNavigation"));
            Find<AutoSuggestBox>(page, "OriginInput").Text = "Seattle";
            Find<AutoSuggestBox>(page, "DestinationInput").Text = "Portland";
            var button = Find<Button>(page, "GetDirectionsButton");
            var progress = Find<ProgressBar>(page, "RouteProgress");
            await PressAsync(button);
            await WaitAsync(() => service.RouteOrigin is not null);
            Assert.AreEqual(Visibility.Visible, progress.Visibility);
            Assert.IsTrue(progress.IsIndeterminate);
            Assert.IsFalse(button.IsEnabled);
            Assert.IsFalse(Find<InfoBar>(page, "Status").IsOpen);
            page.UpdateLayout();
            Assert.IsGreaterThanOrEqualTo(button.ActualHeight, progress.TransformToVisual(button).TransformPoint(default).Y);

            if (outcome == "cancel")
            {
                var modes = Find<SelectorBar>(page, "TravelModePicker");
                modes.SelectedItem = modes.Items[1];
                Assert.IsTrue(service.RouteToken.IsCancellationRequested);
            }
            if (outcome == "failure")
                service.RouteCompletion!.SetException(new RequestFailedException(503, "private"));
            else
                service.RouteCompletion!.SetResult(outcome == "empty" ? null :
                    new MapRoute([service.RouteOrigin!, service.RouteDestination!], [new RouteStep("Head north", 47.6, -122.33)], 1500, 300));
            await WaitAsync(() => progress.Visibility == Visibility.Collapsed && button.IsEnabled);
            Assert.IsFalse(progress.IsIndeterminate);
            if (outcome is "failure" or "empty")
            {
                Assert.IsTrue(Find<InfoBar>(page, "Status").IsOpen);
                Assert.DoesNotContain("private", Find<InfoBar>(page, "Status").Message);
            }
            else if (outcome == "success")
                Assert.HasCount(1, Find<ListView>(page, "Itinerary").Items);
            else
                Assert.IsEmpty(Find<ListView>(page, "Itinerary").Items);
        });

    [TestMethod]
    public Task MapTap_ReusesPinTabWithoutReplacingSearchTabs()
        => LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, service) =>
        {
            var map = Find<MapControl>(page, "Map");
            var tabs = Find<TabView>(page, "SearchTabs");
            var input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            await DropPinAsync(1);
            var pinTab = Assert.IsInstanceOfType<TabViewItem>(tabs.SelectedItem);
            await DropPinAsync(2);
            Assert.HasCount(1, tabs.TabItems);
            Assert.AreSame(pinTab, tabs.SelectedItem);
            Assert.AreEqual("Map location 2", pinTab.Header);

            await PressAsync(Find<Button>(page, "BackToResults"));
            var search = Find<AutoSuggestBox>(page, "SearchInput");
            search.Text = "Seattle";
            await PressAsync(Descendants(search).OfType<Button>().Single(button => button.Name == "QueryButton"));
            await WaitAsync(() => tabs.TabItems.Count == 2);
            var searchTab = Assert.IsInstanceOfType<TabViewItem>(tabs.SelectedItem);
            Assert.AreNotSame(pinTab, searchTab);
            await DropPinAsync(3);
            Assert.HasCount(2, tabs.TabItems);
            Assert.AreSame(pinTab, tabs.SelectedItem);
            Assert.AreEqual("Seattle", searchTab.Header);
            await PressAsync(Descendants(pinTab).OfType<Button>().Single(button => button.Name == "CloseButton"));
            await WaitAsync(() => tabs.TabItems.Count == 1);
            Assert.IsEmpty(((MapElementsLayer)map.Layers[4]).MapElements);
            Assert.HasCount(1, ((MapElementsLayer)map.Layers[2]).MapElements);
            await DropPinAsync(4);
            Assert.HasCount(2, tabs.TabItems);
            Assert.AreNotSame(pinTab, tabs.SelectedItem);
            Assert.IsTrue(tabs.TabItems.Contains(searchTab));

            async Task DropPinAsync(int number)
            {
                await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
                input.Mouse.Click(input.PointAt(0.8, number % 2 == 0 ? 0.35 : 0.65));
                await WaitAsync(() => service.ReverseCalls == number &&
                    Find<TextBlock>(page, "PlaceName").Text == $"Map location {number}");
            }
        });

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public Task ClosingPinTab_RemovesPinAndPreservesRoute(bool withRoute, bool closeInactivePin)
        => LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, _) =>
        {
            var map = Find<MapControl>(page, "Map");
            var tabs = Find<TabView>(page, "SearchTabs");
            TabViewItem? routeTab = null;
            if (withRoute)
            {
                await PressAsync(Find<Button>(page, "DirectionsNavigation"));
                Find<AutoSuggestBox>(page, "OriginInput").Text = "Seattle";
                Find<AutoSuggestBox>(page, "DestinationInput").Text = "Portland";
                await PressAsync(Find<Button>(page, "GetDirectionsButton"));
                await WaitAsync(() => Find<ListView>(page, "Itinerary").Items.Count == 1);
                routeTab = Assert.IsInstanceOfType<TabViewItem>(tabs.SelectedItem);
            }
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
            var input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            input.Mouse.Click(input.PointAt(0.8, 0.6));
            await WaitAsync(() => Find<TextBlock>(page, "PlaceName").Text == "Map location 1");
            var pinTab = Assert.IsInstanceOfType<TabViewItem>(tabs.SelectedItem);
            if (withRoute)
            {
                tabs.SelectedItem = routeTab;
                tabs.SelectedItem = pinTab;
                await WaitAsync(() => ((MapElementsLayer)map.Layers[2]).MapElements.Count == 1);
                if (closeInactivePin) tabs.SelectedItem = routeTab;
            }
            await PressAsync(Descendants(pinTab).OfType<Button>().Single(button => button.Name == "CloseButton"));
            await WaitAsync(() => tabs.TabItems.Count == (withRoute ? 1 : 0));
            Assert.IsEmpty(((MapElementsLayer)map.Layers[4]).MapElements);
            Assert.IsEmpty(((MapElementsLayer)map.Layers[2]).MapElements);
            Assert.AreEqual(Visibility.Collapsed, Find<StackPanel>(page, "PlaceDetails").Visibility);
            if (withRoute)
            {
                Assert.AreSame(routeTab, tabs.SelectedItem);
                Assert.HasCount(4, ((MapElementsLayer)map.Layers[0]).MapElements);
                Assert.HasCount(1, Find<ListView>(page, "Itinerary").Items);
            }
        });

    [TestMethod]
    public Task SubmittedSearch_DoesNotReopenAutocomplete()
        => LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, _) =>
        {
            var input = Find<AutoSuggestBox>(page, "SearchInput");
            Descendants(input).OfType<TextBox>().Single().Focus(FocusState.Programmatic);
            input.Text = "coffee";
            await PressAsync(Descendants(input).OfType<Button>().Single(button => button.Name == "QueryButton"));
            var results = Find<ListView>(page, "SearchResults");
            await WaitAsync(() => results.Items.Count == 1);
            var map = Find<MapControl>(page, "Map");
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
            Assert.IsFalse(input.IsSuggestionListOpen);
            Assert.IsNull(input.ItemsSource);
            Assert.IsFalse(Find<InfoBar>(page, "Status").IsOpen);
            await PressItemAsync(results, 0);
            Assert.AreEqual(Visibility.Visible, Find<StackPanel>(page, "PlaceDetails").Visibility);
            Assert.HasCount(1, Find<TabView>(page, "SearchTabs").TabItems);
        });

    [TestMethod]
    public Task SearchTabs_PreserveResultsAndDetailsAndCanBeClosed() =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, _) =>
        {
            var input = Find<AutoSuggestBox>(page, "SearchInput");
            var results = Find<ListView>(page, "SearchResults");
            var tabs = Find<TabView>(page, "SearchTabs");
            input.Text = "Seattle";
            await PressAsync(Descendants(input).OfType<Button>().Single(button => button.Name == "QueryButton"));
            await WaitAsync(() => tabs.TabItems.Count == 1 && results.Items.Count == 1);
            var first = Assert.IsInstanceOfType<TabViewItem>(tabs.SelectedItem);
            await PressItemAsync(results, 0);
            Assert.AreEqual(Visibility.Collapsed, Find<StackPanel>(page, "SearchOverview").Visibility);
            Assert.AreEqual("Seattle", Find<TextBlock>(page, "PlaceName").Text);
            await PressAsync(Find<Button>(page, "BackToResults"));
            await WaitAsync(() => Find<StackPanel>(page, "SearchOverview").Visibility == Visibility.Visible);
            Assert.HasCount(1, results.Items);

            input.Text = "Portland";
            await PressAsync(Descendants(input).OfType<Button>().Single(button => button.Name == "QueryButton"));
            await WaitAsync(() => tabs.TabItems.Count == 2 && ((Place)results.Items[0]).Name == "Portland");
            var second = Assert.IsInstanceOfType<TabViewItem>(tabs.SelectedItem);
            await PressItemAsync(results, 0);
            var map = Find<MapControl>(page, "Map");
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
            using var cameraEvents = new RenderingEventListener("CameraViewChangeRequested");
            tabs.SelectedItem = first;
            await WaitAsync(() => input.Text == "Seattle" && Find<StackPanel>(page, "SearchOverview").Visibility == Visibility.Visible);
            Assert.AreEqual("Seattle", ((Place)results.Items[0]).Name);
            tabs.SelectedItem = second;
            await WaitAsync(() => Find<TextBlock>(page, "PlaceName").Text == "Portland");
            Assert.AreEqual(Visibility.Visible, Find<StackPanel>(page, "PlaceDetails").Visibility);
            await PressAsync(Descendants(second).OfType<Button>().Single(button => button.Name == "CloseButton"));
            await WaitAsync(() => tabs.TabItems.Count == 1 && input.Text == "Seattle");
            Assert.AreSame(first, tabs.SelectedItem);
            var changes = cameraEvents.Events("CameraViewChangeRequested");
            Assert.IsGreaterThanOrEqualTo(3, changes.Length);
            Assert.IsTrue(changes.All(change => Convert.ToInt32(change.Payload[0]) == (int)MapAnimationKind.None));
        });

    [TestMethod]
    public Task WindowsInk_DrawsAndCommitsGeographicAnnotationsThenClearsThem() =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, _) =>
        {
            var map = Find<MapControl>(page, "Map");
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
            var picker = Find<MapStylePicker>(page, "StylePicker");
            picker.IsInkEnabled = true;
            var canvas = Find<InkCanvas>(page, "MapInk");
            await WaitAsync(() => canvas.IsLoaded && canvas.ActualWidth > 0 && Find<InkToolbar>(page, "DrawingToolbar").ActiveTool is not null);
            var toolbar = Find<InkToolbar>(page, "DrawingToolbar");
            var toolbarBounds = toolbar.TransformToVisual(page).TransformBounds(new(0, 0, toolbar.ActualWidth, toolbar.ActualHeight));
            var canvasBounds = canvas.TransformToVisual(page).TransformBounds(new(0, 0, canvas.ActualWidth, canvas.ActualHeight));
            var favorites = Find<Button>(page, "FavoritesNavigation").TransformToVisual(page).TransformPoint(default);
            Assert.IsLessThanOrEqualTo(canvasBounds.Top, toolbarBounds.Bottom);
            Assert.IsLessThanOrEqualTo(favorites.X, toolbarBounds.Right);
            var highlighter = toolbar.GetToolButton(InkToolbarTool.Highlighter);
            var toolbarInput = UiInputInjector.ForElement(MapControlTestHost.Window, highlighter);
            toolbarInput.Mouse.Click();
            await WaitAsync(() => toolbar.ActiveTool == highlighter);
            Assert.IsFalse(map.IsHitTestVisible);
            Assert.IsFalse(Find<MapNavigationControl>(page, "Navigation").IsEnabled);
            var input = UiInputInjector.ForElement(MapControlTestHost.Window, canvas);
            int collected = 0;
            canvas.InkPresenter.StrokesCollected += (_, _) => collected++;
            await input.Mouse.DragAsync(input.PointAt(0.6, 0.5), input.PointAt(0.8, 0.7), 600);
            await WaitAsync(() => collected > 0);
            picker.IsInkEnabled = false;
            Assert.AreEqual(Visibility.Collapsed, canvas.Visibility);
            Assert.IsTrue(map.IsHitTestVisible);
            var layer = (WinUIEx.Maps.MapElementsLayer)map.Layers[5];
            var line = Assert.IsInstanceOfType<MapPolyline>(Assert.ContainsSingle(layer.MapElements));
            Assert.IsNotNull(line.Path);
            Assert.IsGreaterThan(1, line.Path.Positions.Count);
            var position = line.Path.Positions[0];
            map.ZoomLevel += 1;
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
            Assert.AreEqual(position, line.Path.Positions[0]);
            picker.IsInkEnabled = true;
            await PressAsync(Descendants(page).OfType<Button>().Single(button =>
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(button) == "ClearInk"));
            Assert.IsEmpty(layer.MapElements);
            Assert.IsEmpty(canvas.InkPresenter.StrokeContainer.GetStrokes());
            picker.IsInkEnabled = false;
        });

    [TestMethod]
    public Task MapStylePicker_UpdatesInBothDirections() =>
        MapControlTestHost.LoadUIAsync(() =>
        {
            EnsureResources();
            return new MapStylePicker { SelectedStyle = MapStyle.Night };
        }, async root =>
        {
            await ResizeHostAsync((FrameworkElement)root);
            var picker = (MapStylePicker)root;
            var button = Descendants(picker).OfType<DropDownButton>().Single();
            button.Flyout.ShowAt(button);
            await WaitAsync(() => VisualTreeHelper.GetOpenPopupsForXamlRoot(picker.XamlRoot).Count > 0);
            try
            {
                var content = Assert.IsInstanceOfType<StackPanel>(((Flyout)button.Flyout).Content);
                await WaitAsync(() => content.IsLoaded);
                var choices = Descendants(content).OfType<RadioButton>().ToArray();
                Assert.HasCount(3, choices);
                Assert.IsTrue(choices[0].IsChecked);
                choices[1].IsChecked = true;
                Assert.AreEqual(MapStyle.SatelliteWithRoads, picker.SelectedStyle);
                picker.SelectedStyle = MapStyle.RoadShadedRelief;
                Assert.IsTrue(choices[2].IsChecked);
                Assert.IsFalse(choices[1].IsChecked);
                var ink = Descendants(content).OfType<ToggleSwitch>().Single(toggle => toggle.Name == "PART_Ink");
                ink.IsOn = true;
                Assert.IsTrue(picker.IsInkEnabled);
                picker.IsInkEnabled = false;
                Assert.IsFalse(ink.IsOn);
                var traffic = Descendants(content).OfType<ToggleSwitch>().Single(toggle => toggle.Name == "PART_Traffic");
                Assert.IsFalse(traffic.IsOn);
                traffic.IsOn = true;
                Assert.IsTrue(picker.IsTrafficEnabled);
                picker.IsTrafficEnabled = false;
                Assert.IsFalse(traffic.IsOn);
                var preview = Descendants(picker).OfType<Image>().Single();
                Assert.EndsWith("Terrain.svg", Assert.IsInstanceOfType<SvgImageSource>(preview.Source).UriSource.AbsolutePath);
            }
            finally { button.Flyout.Hide(); }
        });

    [TestMethod]
    public Task TrafficSwitch_AddsOneOverlayBelowApplicationElements() =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), (page, _, _) =>
        {
            var picker = Find<MapStylePicker>(page, "StylePicker");
            var map = Find<MapControl>(page, "Map");
            MapLayer[] original = map.Layers.ToArray();
            picker.IsTrafficEnabled = true;
            AzureTrafficLayer traffic = Assert.IsInstanceOfType<AzureTrafficLayer>(map.Layers[0]);
            Assert.IsTrue(traffic.ShowIncidents);
            Assert.AreEqual(12d, traffic.MinIncidentZoom);
            Assert.AreSequenceEqual(original, map.Layers.Skip(1).ToArray());
            picker.IsTrafficEnabled = true;
            Assert.AreEqual(1, map.Layers.OfType<AzureTrafficLayer>().Count());
            picker.IsTrafficEnabled = false;
            Assert.AreSequenceEqual(original, map.Layers.ToArray());
            return Task.CompletedTask;
        });

    [TestMethod]
    public Task TrafficIncidentClick_ShowsDetailsWithoutDroppingPin() =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, service) =>
        {
            var picker = Find<MapStylePicker>(page, "StylePicker");
            var map = Find<MapControl>(page, "Map");
            picker.IsTrafficEnabled = true;
            var traffic = map.Layers.OfType<AzureTrafficLayer>().Single();
            traffic.MinIncidentZoom = 0;
            Assert.IsTrue(traffic.HasIncidentTappedHandler);
            TileId tileId = new(4, 8, 8);
            byte[] encoded = new MapboxVectorTileBuilder()
                .AddPoint("Traffic incident POI", 2048, 2048, new Dictionary<string, object>
                {
                    ["id"] = "fixture-incident", ["icon_category_0"] = 9,
                    ["description_0"] = "Fixture road works", ["delay"] = 120, ["magnitude"] = 2,
                })
                .AddLine("Traffic incident flow", [new(0, 2048), new(4096, 2048)],
                    new Dictionary<string, object> { ["description"] = "Underlying road segment" })
                .Build();
            map.Layers.Insert(0, new TestTrafficTileLayer(traffic.IncidentRuntimeId, tileId, encoded));
            await map.TrySetViewAsync(new(new()
            {
                Longitude = 11.25, Latitude = MapCamera.WorldYToLatitude(8.5 / 16),
            }), 4, 0, 0, MapAnimationKind.None);
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, 4);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            while (true)
            {
                var frame = await map.CaptureRenderedFrameAsync(timeout.Token);
                if (ConnectedComponentAnalyzer.Find(frame,
                    ConnectedComponentAnalyzer.Near(255, 213, 79, tolerance: 8),
                    minimumPixelCount: 20).Length > 0)
                    break;
                await Task.Delay(20, timeout.Token);
            }
            int unhandledTaps = 0;
            map.Tapped += (_, _) => unhandledTaps++;
            var input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            input.Mouse.Click(input.PointAt(0.5 + 10 / map.ActualWidth, 0.5 + 8 / map.ActualHeight));
            var flyout = (Flyout)page.Resources["TrafficIncidentFlyout"];
            var content = (StackPanel)flyout.Content;
            try
            {
                await WaitAsync(() => content.IsLoaded);
                string text = string.Join("\n", Descendants(content).OfType<TextBlock>().Select(t => t.Text));
                Assert.Contains("Fixture road works", text);
                Assert.Contains("Road works", text);
                Assert.DoesNotContain("fixture-incident", text);
                Assert.Contains("2 min delay", text);
                Assert.DoesNotContain("Underlying road segment", text);
                Assert.AreEqual(0, unhandledTaps);
                Assert.AreEqual(0, service.ReverseCalls);
                Assert.IsEmpty(Find<TabView>(page, "SearchTabs").TabItems);
                picker.IsTrafficEnabled = false;
                await WaitAsync(() => !content.IsLoaded);
            }
            finally
            {
                flyout.Hide();
            }
        });

    [TestMethod]
    [DataRow(ElementTheme.Light)]
    [DataRow(ElementTheme.Dark)]
    public Task TrafficIncidentCallout_UsesTypeDelayAndOptionalLocalTimes(ElementTheme theme) =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, _) =>
        {
            page.RequestedTheme = theme;
            var map = Find<MapControl>(page, "Map");
            DateTimeOffset start = new(2026, 9, 24, 14, 54, 0, TimeSpan.FromHours(-7));
            DateTimeOffset end = start.AddHours(4).AddMinutes(40);
            page.UpdateTrafficIncidentDetails(new(map.Center!, "fixture-id", 6, 3,
                "Stopped traffic", TimeSpan.FromMinutes(18), title: "A road name",
                startTime: start, endTime: end));
            var flyout = (Flyout)page.Resources["TrafficIncidentFlyout"];
            var content = (StackPanel)flyout.Content;
            flyout.ShowAt(map, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Position = new(map.ActualWidth / 2, map.ActualHeight / 2),
            });
            try
            {
                await WaitAsync(() => content.IsLoaded && content.ActualWidth > 0);
                var title = Find<TextBlock>(page, "TrafficIncidentTitle");
                var delay = Find<TextBlock>(page, "TrafficIncidentDelay");
                var description = Find<TextBlock>(page, "TrafficIncidentDescription");
                var startText = Find<TextBlock>(page, "TrafficIncidentStart");
                var endText = Find<TextBlock>(page, "TrafficIncidentEnd");
                Assert.AreEqual("Congestion", title.Text);
                Assert.AreEqual("18 min delay", delay.Text);
                Assert.AreEqual("Stopped traffic", description.Text);
                Assert.AreEqual($"Start: {start.ToLocalTime():ddd, MMM d} - {start.ToLocalTime():t}", startText.Text);
                Assert.AreEqual($"Est. end: {end.ToLocalTime():ddd, MMM d} - {end.ToLocalTime():t}", endText.Text);
                var titlePosition = title.TransformToVisual(content).TransformPoint(default);
                var delayPosition = delay.TransformToVisual(content).TransformPoint(default);
                Assert.AreEqual(titlePosition.Y, delayPosition.Y, 1);
                Assert.IsGreaterThan(titlePosition.X + title.ActualWidth, delayPosition.X);
                Assert.IsGreaterThan(titlePosition.Y, description.TransformToVisual(content).TransformPoint(default).Y);
                DependencyObject? presenter = content;
                while (presenter is not null && presenter is not FlyoutPresenter)
                    presenter = VisualTreeHelper.GetParent(presenter);
                var flyoutPresenter = Assert.IsInstanceOfType<FlyoutPresenter>(presenter);
                Assert.AreEqual(new CornerRadius(12), flyoutPresenter.CornerRadius);
                Assert.AreEqual(new Thickness(16), flyoutPresenter.Padding);
                Assert.AreEqual(theme, flyoutPresenter.ActualTheme);
                Assert.AreNotEqual(
                    Assert.IsInstanceOfType<SolidColorBrush>(title.Foreground).Color,
                    Assert.IsInstanceOfType<SolidColorBrush>(delay.Foreground).Color);
                page.UpdateTrafficIncidentDetails(new(map.Center!, null, null, null));
                Assert.AreEqual("Traffic incident", title.Text);
                Assert.AreEqual(Visibility.Collapsed, delay.Visibility);
                Assert.AreEqual(Visibility.Collapsed, description.Visibility);
                Assert.AreEqual(Visibility.Collapsed, startText.Visibility);
                Assert.AreEqual(Visibility.Collapsed, endText.Visibility);
            }
            finally { flyout.Hide(); }
        });

    [TestMethod]
    public Task FirstRun_RejectsInvalidTokenThenAllowsValidatedRetry() =>
        LoadSampleAsync(new MemoryTokenStore(), new FakeMapsService { RejectValidation = true }, async (page, store, service) =>
        {
            Assert.AreEqual(Visibility.Collapsed, Find<Grid>(page, "Shell").Visibility);
            Assert.AreEqual(MapStyle.Blank, Find<MapControl>(page, "Map").MapStyle);
            var input = Find<PasswordBox>(page, "TokenInput");
            var validate = Find<Button>(page, "ValidateButton");
            Assert.IsFalse(validate.IsEnabled);
            input.Password = "  test-key  ";
            await PressAsync(validate);
            await WaitAsync(() => Find<InfoBar>(page, "TokenStatus").IsOpen);
            Assert.AreEqual("", store.Token);
            Assert.AreEqual(Visibility.Collapsed, Find<Grid>(page, "Shell").Visibility);
            Assert.DoesNotContain("private", Find<InfoBar>(page, "TokenStatus").Message);

            service.RejectValidation = false;
            await PressAsync(validate);
            await WaitAsync(() => Find<Grid>(page, "Shell").Visibility == Visibility.Visible);
            Assert.AreEqual("test-key", store.Token);
            Assert.AreEqual("", input.Password);
            Assert.AreEqual(Visibility.Collapsed, Find<Grid>(page, "Onboarding").Visibility);
        });

    [TestMethod]
    public Task SearchSelection_SavesAndRemovesActualPlace() =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, service) =>
        {
            var search = Find<AutoSuggestBox>(page, "SearchInput");
            search.Text = "Seattle";
            search.ApplyTemplate();
            var submit = Descendants(search).OfType<Button>().Single(button => button.Name == "QueryButton");
            await PressAsync(submit);
            var list = Find<ListView>(page, "SearchResults");
            await WaitAsync(() => list.Items.Count == 1);
            await PressItemAsync(list, 0);
            var favorite = Find<Button>(page, "FavoriteButton");
            await WaitAsync(() => favorite.IsLoaded && favorite.ActualHeight > 0);
            await PressAsync(favorite);
            var favorites = Find<ListView>(page, "FavoritesList");
            await WaitAsync(() => favorites.Items.Count == 1);
            Assert.AreEqual(service.Place, favorites.Items[0]);
            for (int i = 0; i < 2; i++)
            {
                await PressAsync(Find<Button>(page, "FavoritesNavigation"));
                await PressItemAsync(favorites, 0);
                await WaitAsync(() => Find<TextBlock>(page, "PaneTitle").Text == "Search");
                Assert.AreEqual("Delete saved pin", Find<TextBlock>(page, "FavoriteLabel").Text);
            }
            await PressAsync(favorite);
            await WaitAsync(() => favorites.Items.Count == 0);
            var map = Find<MapControl>(page, "Map");
            Assert.IsEmpty(((WinUIEx.Maps.MapElementsLayer)map.Layers[1]).MapElements);
            Assert.IsEmpty(((WinUIEx.Maps.MapElementsLayer)map.Layers[4]).MapElements);
            Assert.AreEqual(Visibility.Collapsed, Find<StackPanel>(page, "PlaceDetails").Visibility);
        });

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public Task MapTap_SelectsLocationAndAllowsSavingEvenWithoutAnAddress(bool noAddress, bool failure) =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" },
            new FakeMapsService { NoReverseAddress = noAddress, RejectReverse = failure }, async (page, _, service) =>
        {
            var map = Find<MapControl>(page, "Map");
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
            var input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            input.Mouse.Click(input.PointAt(0.65, 0.5));
            await WaitAsync(() => service.ReverseCalls == 1 && Find<StackPanel>(page, "PlaceDetails").Visibility == Visibility.Visible);
            Assert.AreEqual(noAddress || failure ? "Dropped pin" : "Map location 1", Find<TextBlock>(page, "PlaceName").Text);
            var selectedLayer = (WinUIEx.Maps.MapElementsLayer)map.Layers[4];
            var pin = Assert.IsInstanceOfType<WinUIEx.Maps.MapIcon>(Assert.ContainsSingle(selectedLayer.MapElements));
            Assert.AreEqual(40d / 42, pin.NormalizedAnchorPoint.Y, 0.0000001);
            Assert.IsInstanceOfType<ImageIcon>(pin.IconElement);
            Assert.IsNotNull(service.ReversePosition);
            Assert.AreEqual(service.ReversePosition.Latitude, pin.Location.Position.Latitude);
            Assert.AreEqual(service.ReversePosition.Longitude, pin.Location.Position.Longitude);
            if (noAddress || failure)
            {
                var status = Find<InfoBar>(page, "Status");
                Assert.IsTrue(status.IsOpen);
                Assert.Contains(noAddress ? "No street address" : "could not be loaded", status.Message);
                Assert.DoesNotContain("private", status.Message);
            }
            await PressAsync(Find<Button>(page, "FavoriteButton"));
            Assert.HasCount(1, Find<ListView>(page, "FavoritesList").Items);
            Assert.AreEqual("Delete saved pin", Find<TextBlock>(page, "FavoriteLabel").Text);
        });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task MapContextMenu_ShowsCoordinatesAndOnlyDropsPinWhenChosen(bool dropPin) =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, service) =>
        {
            var map = Find<MapControl>(page, "Map");
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
            var input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            Windows.Devices.Geolocation.Geopoint? clickedLocation = null;
            map.AddHandler(UIElement.RightTappedEvent,
                new Microsoft.UI.Xaml.Input.RightTappedEventHandler((_, args) =>
                {
                    if (map.TryGetLocationFromOffset(args.GetPosition(map), out var location))
                        clickedLocation = location;
                }), handledEventsToo: true);
            // Consecutive data rows must not be interpreted as a right-button double-click.
            await Task.Delay(TimeSpan.FromMilliseconds(GetDoubleClickTime() + 50));
            input.Mouse.RightClick(input.PointAt(0.8, 0.6));
            var menu = (MenuFlyout)page.Resources["MapContextMenu"];
            await WaitAsync(() => menu.IsOpen && menu.Items[2].IsLoaded && clickedLocation is not null);
            var position = clickedLocation!.Position;
            Assert.AreEqual(FormattableString.Invariant(
                $"Latitude: {position.Latitude:F5}, Longitude: {position.Longitude:F5}"),
                ((MenuFlyoutItem)menu.Items[0]).Text);
            Assert.AreEqual("Drop pin", ((MenuFlyoutItem)menu.Items[2]).Text);
            Assert.IsFalse(((MenuFlyout)page.Resources["PinContextMenu"]).IsOpen);
            Assert.AreEqual(0, service.ReverseCalls);
            var layer = (MapElementsLayer)map.Layers[4];
            Assert.IsEmpty(layer.MapElements);
            Assert.IsEmpty(Find<TabView>(page, "SearchTabs").TabItems);
            if (dropPin)
            {
                InvokeMenuItem((MenuFlyoutItem)menu.Items[2]);
                await WaitAsync(() => Find<TextBlock>(page, "PlaceName").Text == "Map location 1");
                Assert.AreEqual(1, service.ReverseCalls);
                var marker = Assert.IsInstanceOfType<WinUIEx.Maps.MapIcon>(Assert.ContainsSingle(layer.MapElements));
                Assert.AreEqual(position.Latitude, marker.Location.Position.Latitude);
                Assert.AreEqual(position.Longitude, marker.Location.Position.Longitude);
                Assert.HasCount(1, Find<TabView>(page, "SearchTabs").TabItems);
            }
            else
            {
                menu.Hide();
                await WaitAsync(() => !menu.IsOpen && !menu.Items[2].IsLoaded);
                Assert.AreEqual(0, service.ReverseCalls);
                Assert.IsEmpty(layer.MapElements);
            }
        });

    [TestMethod]
    public Task SavedMapPin_IsClickableWithoutReverseGeocodingAgain() =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, service) =>
        {
            var map = Find<MapControl>(page, "Map");
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
            var input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            input.Mouse.Click(input.PointAt(0.65, 0.5));
            await WaitAsync(() => Find<TextBlock>(page, "PlaceName").Text == "Map location 1");
            await PressAsync(Find<Button>(page, "FavoriteButton"));
            var saved = Assert.IsInstanceOfType<WinUIEx.Maps.MapIcon>(
                Assert.ContainsSingle(((WinUIEx.Maps.MapElementsLayer)map.Layers[1]).MapElements));
            input.Mouse.Click(input.PointAt(0.8, 0.7));
            await WaitAsync(() => Find<TextBlock>(page, "PlaceName").Text == "Map location 2");
            Windows.Foundation.Point offset = new(map.ActualWidth * 0.65, map.ActualHeight * 0.5);
            await WaitAsync(() => map.TryHitTestMapElement(new(offset.X, offset.Y - 20), out var hit) && ReferenceEquals(hit, saved));
            input.Mouse.Click(input.PointAt(offset.X / map.ActualWidth, (offset.Y - 20) / map.ActualHeight));
            await WaitAsync(() => Find<TextBlock>(page, "PlaceName").Text == "Map location 1");
            Assert.AreEqual(2, service.ReverseCalls);
            Assert.AreEqual("Delete saved pin", Find<TextBlock>(page, "FavoriteLabel").Text);
        });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task SlowAddressLookup_CannotReplaceNewSelectionOrSavedPin(bool savePin) =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" },
            new FakeMapsService { ReverseCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously) }, async (page, _, service) =>
        {
            var map = Find<MapControl>(page, "Map");
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
            var input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            input.Mouse.Click(input.PointAt(0.65, 0.5));
            await WaitAsync(() => service.ReverseCalls == 1);
            if (savePin)
                await PressAsync(Find<Button>(page, "FavoriteButton"));
            else
            {
                var search = Find<AutoSuggestBox>(page, "SearchInput");
                search.Text = "Seattle";
                await PressAsync(Descendants(search).OfType<Button>().Single(button => button.Name == "QueryButton"));
                var results = Find<ListView>(page, "SearchResults");
                await WaitAsync(() => results.Items.Count == 1);
                await PressItemAsync(results, 0);
                await WaitAsync(() => Find<TextBlock>(page, "PlaceName").Text == service.Place.Name);
            }
            Assert.IsTrue(service.ReverseToken.IsCancellationRequested);
            service.ReverseCompletion!.SetResult(new Place("Stale address", "", 0, 0));
            await Task.Delay(100);
            Assert.AreEqual(savePin ? "Dropped pin" : service.Place.Name, Find<TextBlock>(page, "PlaceName").Text);
            if (savePin)
                Assert.AreEqual("Dropped pin", Assert.IsInstanceOfType<Place>(
                    Assert.ContainsSingle(Find<ListView>(page, "FavoritesList").Items)).Name);
        });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task PinContextMenu_RoutesToClickedPinAndPreservesOrigin(bool saved) =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, service) =>
        {
            var map = Find<MapControl>(page, "Map");
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
            var input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            input.Mouse.Click(input.PointAt(0.65, 0.5));
            await WaitAsync(() => Find<TextBlock>(page, "PlaceName").Text == "Map location 1");
            if (saved)
            {
                await PressAsync(Find<Button>(page, "FavoriteButton"));
                input.Mouse.Click(input.PointAt(0.8, 0.7));
                await WaitAsync(() => Find<TextBlock>(page, "PlaceName").Text == "Map location 2");
            }
            Find<AutoSuggestBox>(page, "OriginInput").Text = "Other endpoint";
            var menu = await OpenPinMenuAsync(page, input, map);
            var command = (MenuFlyoutItem)menu.Items[0];
            InvokeMenuItem(command);
            await WaitAsync(() => Find<Grid>(page, "DirectionsPane").Visibility == Visibility.Visible);
            Assert.AreEqual("Map location 1", Find<AutoSuggestBox>(page, "DestinationInput").Text);
            Assert.AreEqual("Other endpoint", Find<AutoSuggestBox>(page, "OriginInput").Text);
            Assert.AreEqual(saved ? 2 : 1, service.ReverseCalls);
            await PressAsync(Find<Button>(page, "GetDirectionsButton"));
            await WaitAsync(() => Find<ListView>(page, "Itinerary").Items.Count == 1);
            Assert.AreEqual("Map location 1", service.RouteDestination!.Name);
        });

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public Task PinContextMenu_SavesThenDeletesPinFromMapAndList(bool selected) =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, _) =>
        {
            var map = Find<MapControl>(page, "Map");
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
            var input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            input.Mouse.Click(input.PointAt(0.65, 0.5));
            await WaitAsync(() => Find<TextBlock>(page, "PlaceName").Text == "Map location 1");
            var menu = await OpenPinMenuAsync(page, input, map);
            Assert.AreEqual(Visibility.Visible, menu.Items[2].Visibility);
            Assert.AreEqual(Visibility.Collapsed, menu.Items[3].Visibility);
            InvokeMenuItem((MenuFlyoutItem)menu.Items[2]);
            await WaitAsync(() => !menu.IsOpen && !menu.Items[0].IsLoaded && Find<ListView>(page, "FavoritesList").Items.Count == 1);
            // UIA invocation does not insert a mouse click between the two right-clicks.
            await Task.Delay(TimeSpan.FromMilliseconds(GetDoubleClickTime() + 50));
            if (!selected)
            {
                input.Mouse.Click(input.PointAt(0.8, 0.7));
                await WaitAsync(() => Find<TextBlock>(page, "PlaceName").Text == "Map location 2");
            }
            menu = await OpenPinMenuAsync(page, input, map);
            Assert.AreEqual(Visibility.Collapsed, menu.Items[2].Visibility);
            Assert.AreEqual(Visibility.Visible, menu.Items[3].Visibility);
            InvokeMenuItem((MenuFlyoutItem)menu.Items[3]);
            await WaitAsync(() => Find<ListView>(page, "FavoritesList").Items.Count == 0);
            Assert.IsEmpty(((WinUIEx.Maps.MapElementsLayer)map.Layers[1]).MapElements);
            Assert.HasCount(selected ? 0 : 1, ((WinUIEx.Maps.MapElementsLayer)map.Layers[4]).MapElements);
            Assert.AreEqual(selected ? Visibility.Collapsed : Visibility.Visible, Find<StackPanel>(page, "PlaceDetails").Visibility);
            if (!selected) Assert.AreEqual("Map location 2", Find<TextBlock>(page, "PlaceName").Text);
        });

    [TestMethod]
    public Task PlaceDetails_DirectionsToActionIsDirectAndPreservesOrigin() =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, _) =>
        {
            var map = Find<MapControl>(page, "Map");
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, map.Center!.Position, map.ZoomLevel);
            var input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            input.Mouse.Click(input.PointAt(0.65, 0.5));
            await WaitAsync(() => Find<TextBlock>(page, "PlaceName").Text == "Map location 1");
            Find<AutoSuggestBox>(page, "OriginInput").Text = "Other endpoint";
            await PressAsync(Find<Button>(page, "DirectionsToPlaceSecondaryButton"));
            var menu = (MenuFlyout)page.Resources["PinContextMenu"];
            Assert.IsFalse(menu.IsOpen);
            await WaitAsync(() => Find<Grid>(page, "DirectionsPane").Visibility == Visibility.Visible);
            Assert.AreEqual("Map location 1", Find<AutoSuggestBox>(page, "DestinationInput").Text);
            Assert.AreEqual("Other endpoint", Find<AutoSuggestBox>(page, "OriginInput").Text);
        });

    private static async Task<MenuFlyout> OpenPinMenuAsync(MainPage page, UiInputInjector input, MapControl map)
    {
        Windows.Foundation.Point offset = new(map.ActualWidth * 0.65, map.ActualHeight * 0.5 - 20);
        await WaitAsync(() => map.TryHitTestMapElement(offset, out var hit) && hit is not null &&
            map.Layers.OfType<WinUIEx.Maps.MapElementsLayer>().Any(layer => layer.MapElements.Contains(hit)));
        input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
        input.Mouse.RightClick(input.PointAt(offset.X / map.ActualWidth, offset.Y / map.ActualHeight));
        var menu = (MenuFlyout)page.Resources["PinContextMenu"];
        await WaitAsync(() => menu.IsOpen && menu.Items[0].IsLoaded);
        Assert.IsFalse(((MenuFlyout)page.Resources["MapContextMenu"]).IsOpen);
        return menu;
    }

    private static void InvokeMenuItem(MenuFlyoutItem item)
    {
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(item);
        Assert.IsInstanceOfType<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [TestMethod]
    public Task MapNavigation_ReapplyingTemplateDoesNotDuplicateHandlers() =>
        MapControlTestHost.LoadUIAsync(() =>
        {
            EnsureResources();
            return new MapNavigationControl();
        }, async root =>
        {
            await ResizeHostAsync((FrameworkElement)root);
            var control = (MapNavigationControl)root;
            int calls = 0;
            int tiltCalls = 0;
            control.ZoomInRequested += (_, _) => calls++;
            control.TiltRequested += (_, _) => tiltCalls++;
            var template = control.Template;
            control.Template = null;
            control.ApplyTemplate();
            control.Template = template;
            control.ApplyTemplate();
            control.UpdateLayout();
            var button = Descendants(control).OfType<Button>().Single(item => item.Name == "PART_ZoomIn");
            await PressAsync(button);
            await WaitAsync(() => calls != 0);
            Assert.AreEqual(1, calls);
            control.Heading = 90;
            var buttons = Descendants(control).OfType<Button>().ToArray();
            CollectionAssert.AreEqual(new[] { "PART_NorthUp", "PART_Tilt", "PART_Locate", "PART_ZoomIn", "PART_ZoomOut" },
                buttons.Select(item => item.Name).ToArray());
            await PressAsync(buttons[1]);
            Assert.AreEqual(1, tiltCalls);
            var compass = Descendants(control).OfType<FrameworkElement>().Single(item => item.Name == "PART_NorthIndicator");
            Assert.AreEqual(-90d, ((RotateTransform)compass.RenderTransform).Angle);
        });

    [TestMethod]
    public Task Navigation_TogglesTiltAndResetsNorth() =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, _) =>
        {
            var map = Find<MapControl>(page, "Map");
            var buttons = Descendants(Find<MapNavigationControl>(page, "Navigation")).OfType<Button>().ToArray();
            await PressAsync(buttons.Single(button => button.Name == "PART_Tilt"));
            Assert.AreEqual(45d, map.Pitch);
            await PressAsync(buttons.Single(button => button.Name == "PART_Tilt"));
            Assert.AreEqual(0d, map.Pitch);
            map.Heading = 120;
            await PressAsync(buttons.Single(button => button.Name == "PART_NorthUp"));
            Assert.AreEqual(0d, map.Heading);
        });

    [TestMethod]
    public Task Theme_UsesOpaquePanesAndDarkRoadWithoutChangingAerialSelection() =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, _) =>
        {
            var map = Find<MapControl>(page, "Map");
            map.MapServiceToken = ""; // Exercise style selection without making Azure requests.
            var picker = Find<MapStylePicker>(page, "StylePicker");
            var theme = Find<ComboBox>(page, "ThemePicker");
            var root = (FrameworkElement)MapControlTestHost.Window.Content;
            var pane = Find<Border>(page, "Pane");
            picker.SelectedStyle = MapStyle.Road;
            theme.SelectedIndex = 2;
            await WaitAsync(() => page.ActualTheme == ElementTheme.Dark && map.MapStyle == MapStyle.Night);
            Assert.AreEqual(ElementTheme.Dark, root.RequestedTheme);
            Assert.AreEqual(MapStyle.Road, picker.SelectedStyle);
            var dark = Assert.IsInstanceOfType<SolidColorBrush>(pane.Background);
            Assert.AreEqual((byte)255, dark.Color.A);
            Assert.AreEqual(1d, dark.Opacity);
            Assert.AreEqual(1d, pane.Opacity);
            var preview = Descendants(picker).OfType<Image>().Single();
            Assert.EndsWith("RoadDark.svg", Assert.IsInstanceOfType<SvgImageSource>(preview.Source).UriSource.AbsolutePath);

            theme.SelectedIndex = 1;
            await WaitAsync(() => map.MapStyle == MapStyle.Road && page.ActualTheme == ElementTheme.Light);
            var light = Assert.IsInstanceOfType<SolidColorBrush>(pane.Background);
            Assert.AreEqual((byte)255, light.Color.A);
            Assert.IsGreaterThan(dark.Color.R, light.Color.R);
            Assert.EndsWith("RoadLight.svg", Assert.IsInstanceOfType<SvgImageSource>(preview.Source).UriSource.AbsolutePath);
            picker.SelectedStyle = MapStyle.SatelliteWithRoads;
            theme.SelectedIndex = 2;
            await WaitAsync(() => page.ActualTheme == ElementTheme.Dark);
            Assert.AreEqual(MapStyle.SatelliteWithRoads, map.MapStyle);
            Assert.AreEqual(MapStyle.SatelliteWithRoads, picker.SelectedStyle);
            theme.SelectedIndex = 0;
            Assert.AreEqual(ElementTheme.Default, root.RequestedTheme);
            Assert.AreEqual(MapStyle.SatelliteWithRoads, map.MapStyle);
        });

    [TestMethod]
    public Task SearchTabs_SitAgainstMapAndDisappearWhenEmpty()
        => LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), async (page, _, _) =>
        {
            var tabs = Find<TabView>(page, "SearchTabs");
            Assert.AreEqual(Visibility.Collapsed, tabs.Visibility);
            var input = Find<AutoSuggestBox>(page, "SearchInput");
            input.Text = "Seattle";
            await PressAsync(Descendants(input).OfType<Button>().Single(button => button.Name == "QueryButton"));
            await WaitAsync(() => tabs.TabItems.Count == 1 && tabs.ActualHeight > 0);
            page.UpdateLayout();
            Assert.AreEqual(Visibility.Visible, tabs.Visibility);
            var tab = Assert.IsInstanceOfType<TabViewItem>(tabs.SelectedItem);
            var map = Find<MapControl>(page, "Map");
            await WaitAsync(() => tab.IsLoaded && Math.Abs(
                tab.TransformToVisual(page).TransformPoint(new(0, tab.ActualHeight)).Y -
                map.TransformToVisual(page).TransformPoint(default).Y) <= 1);
            double mapTop = map.TransformToVisual(page).TransformPoint(default).Y;
            double tabBottom = tab.TransformToVisual(page).TransformPoint(new(0, tab.ActualHeight)).Y;
            Assert.AreEqual(mapTop, tabBottom, 1, "The tab must meet the map without an empty content row.");
            await PressAsync(Descendants(tab).OfType<Button>().Single(button => button.Name == "CloseButton"));
            await WaitAsync(() => tabs.TabItems.Count == 0);
            Assert.AreEqual(Visibility.Collapsed, tabs.Visibility);
        });

    [TestMethod]
    public Task CommandsAndMapPicker_AreAboveTheMap() =>
        LoadSampleAsync(new MemoryTokenStore { Token = "saved-key" }, new FakeMapsService(), (page, _, _) =>
        {
            var map = Find<MapControl>(page, "Map");
            var toolbar = Find<Grid>(page, "TopCommands");
            double mapTop = map.TransformToVisual(page).TransformPoint(default).Y;
            foreach (var control in new FrameworkElement[] { toolbar, Find<Button>(page, "DirectionsNavigation"), Find<MapStylePicker>(page, "StylePicker") })
            {
                double bottom = control.TransformToVisual(page).TransformPoint(new(0, control.ActualHeight)).Y;
                Assert.IsLessThanOrEqualTo(mapTop, bottom);
            }
            Assert.AreEqual((byte)255, Assert.IsInstanceOfType<SolidColorBrush>(toolbar.Background).Color.A);
            return Task.CompletedTask;
        });

    private static Task LoadSampleAsync(MemoryTokenStore store, FakeMapsService service, Func<MainPage, MemoryTokenStore, FakeMapsService, Task> run,
        ILocationService? locationService = null, string? startupRegion = null, MemoryFavoritesStore? favoritesStore = null) =>
        MapControlTestHost.LoadUIAsync(() =>
        {
            EnsureResources();
            if (startupRegion is not null)
            {
                MapControlTestHost.ContentHost.Width = 1000;
                MapControlTestHost.ContentHost.Height = 740;
            }
            return new MainPage(_ => service, store, MapControlTestHost.Window, MapStyle.Blank, locationService,
                startupRegion ?? "US", favoritesStore ?? new MemoryFavoritesStore());
        }, async root =>
        {
            await ResizeHostAsync((FrameworkElement)root);
            if (startupRegion is null && !string.IsNullOrWhiteSpace(store.Token))
            {
                var map = Find<MapControl>((MainPage)root, "Map");
                await map.TrySetViewAsync(new(new() { Latitude = 47.6062, Longitude = -122.3321 }),
                    11, 0, 0, MapAnimationKind.None);
            }
            await run((MainPage)root, store, service);
        });

    private static async Task ResizeHostAsync(FrameworkElement root)
    {
        MapControlTestHost.ContentHost.Width = 1000;
        MapControlTestHost.ContentHost.Height = 740;
        var window = MapControlTestHost.Window;
        _windowSize = window.AppWindow.Size;
        _theme = ((FrameworkElement)window.Content).RequestedTheme;
        if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.Restore();
        double scale = root.XamlRoot.RasterizationScale;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1020 * scale), (int)(810 * scale)));
        await WaitAsync(() => root.ActualWidth >= 1000 && root.ActualHeight >= 740);
    }

    private static void EnsureResources()
    {
        if (_resourcesLoaded) return;
        Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("ms-appx:///WindowsMapsSample/Themes/Generic.xaml"),
        });
        _resourcesLoaded = true;
    }

    private static T Find<T>(MainPage page, string name) where T : FrameworkElement =>
        Assert.IsInstanceOfType<T>(page.FindName(name));

    private static async Task PressAsync(Button button)
    {
        button.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        await WaitAsync(() =>
        {
            var bounds = button.TransformToVisual(MapControlTestHost.ContentHost)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, button.ActualWidth, button.ActualHeight));
            return button.IsEnabled && bounds.Top >= 0 && bounds.Bottom <= MapControlTestHost.ContentHost.ActualHeight && bounds.Height > 0;
        });
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button);
        var invoke = Assert.IsInstanceOfType<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
        invoke.Invoke();
    }

    private static async Task PressItemAsync(ListView list, int index)
    {
        await WaitAsync(() => list.ContainerFromIndex(index) is ListViewItem { IsLoaded: true });
        var item = (ListViewItem)list.ContainerFromIndex(index);
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(item);
        Assert.IsInstanceOfType<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Sample UI did not reach the expected state.");
            await Task.Delay(20);
        }
    }

    private sealed class MemoryTokenStore : IMapServiceTokenStore
    {
        internal string Token { get; set; } = "";
        public string Load() => Token;
        public void Save(string token) => Token = token;
    }

    private sealed class MemoryFavoritesStore : IFavoritesStore
    {
        private List<Place> _items;

        internal MemoryFavoritesStore(IEnumerable<Place>? items = null) => _items = items?.ToList() ?? [];

        public IReadOnlyList<Place> Load() => _items.ToArray();

        public void Save(IReadOnlyList<Place> favorites) => _items = [.. favorites];
    }

    private sealed class FakeMapsService : IMapsService
    {
        internal bool RejectValidation { get; set; }
        internal bool LastWalking { get; private set; }
        internal Place? RouteOrigin { get; private set; }
        internal Place? RouteDestination { get; private set; }
        internal TaskCompletionSource<MapRoute?>? RouteCompletion { get; set; }
        internal CancellationToken RouteToken { get; private set; }
        internal bool NoReverseAddress { get; set; }
        internal bool RejectReverse { get; set; }
        internal int ReverseCalls { get; private set; }
        internal Place? ReversePosition { get; private set; }
        internal TaskCompletionSource<Place?>? ReverseCompletion { get; set; }
        internal CancellationToken ReverseToken { get; private set; }
        internal Place Place { get; init; } = new("Seattle", "Seattle, WA", 47.6, -122.33);
        public Task ValidateAsync(CancellationToken cancellationToken) =>
            RejectValidation ? Task.FromException(new RequestFailedException(401, "private")) : Task.CompletedTask;
        internal SearchArea? LastSearchArea { get; private set; }
        public Task<IReadOnlyList<Place>> SearchAsync(string query, CancellationToken cancellationToken, SearchArea? area = null)
        {
            LastSearchArea = area;
            return Task.FromResult<IReadOnlyList<Place>>([query == "Portland" ? new Place("Portland", "Portland, OR", 45.52, -122.67) : Place]);
        }
        public Task<Place?> ReverseGeocodeAsync(double latitude, double longitude, CancellationToken cancellationToken)
        {
            ReversePosition = new Place($"Map location {++ReverseCalls}", "Resolved address", latitude, longitude);
            ReverseToken = cancellationToken;
            if (ReverseCompletion is not null) return ReverseCompletion.Task;
            return RejectReverse ? Task.FromException<Place?>(new RequestFailedException(500, "private")) :
                Task.FromResult(NoReverseAddress ? null : ReversePosition);
        }
        public async Task<MapRoute?> RouteAsync(Place origin, Place destination, bool walking, CancellationToken cancellationToken)
        {
            LastWalking = walking;
            RouteOrigin = origin;
            RouteDestination = destination;
            RouteToken = cancellationToken;
            if (RouteCompletion is not null) return await RouteCompletion.Task;
            await Task.Delay(100, cancellationToken);
            return new MapRoute([origin, destination], [new RouteStep("Head north", origin.Latitude, origin.Longitude)], 1500, 300);
        }

    }

    private sealed class FakeLocationService(bool denied) : ILocationService
    {
        public Task<Place> LocateAsync(CancellationToken cancellationToken) =>
            denied ? Task.FromException<Place>(new UnauthorizedAccessException()) :
                Task.FromResult(new Place("My location", "Test accuracy", 47.6, -122.33));
    }
}
