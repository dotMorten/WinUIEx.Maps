using System.Net;
using System.Diagnostics.Tracing;
using Azure;
using Azure.Core.Pipeline;
using Azure.Maps.Routing;
using Azure.Maps.Search;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WindowsMapsSample.Models;
using WindowsMapsSample.Services;
using WindowsMapsSample.ViewModels;

namespace WinUIEx.Maps.Tests.Tests;

[TestClass]
public sealed class WindowsMapsSampleTests
{
    [TestMethod]
    public void StartupRegionsCoverCountriesAndProduceValidViews()
    {
        int supported = 0;
        for (char first = 'A'; first <= 'Z'; first++)
        for (char second = 'A'; second <= 'Z'; second++)
        {
            string code = $"{first}{second}";
            var area = StartupRegion.GetBounds(code);
            if (ReferenceEquals(area, StartupRegion.World)) continue;
            supported++;
            Assert.IsTrue(area.West is >= -180 and <= 180 && area.East is >= -180 and <= 180, code);
            Assert.IsTrue(area.South >= -85 && area.North <= 85 && area.South < area.North, code);
            var bounds = new Windows.Devices.Geolocation.GeoboundingBox(
                new() { Latitude = area.North, Longitude = area.West },
                new() { Latitude = area.South, Longitude = area.East });
            Assert.IsTrue(MapControl.TryCalculateBoundsView(bounds, new(24), 1000, 700, 0, 0,
                out var center, out double zoom), code);
            Assert.IsTrue(double.IsFinite(center.Latitude) && double.IsFinite(center.Longitude) &&
                double.IsFinite(zoom) && zoom >= 0, code);
        }
        Assert.AreEqual(251, supported, "249 ISO regions, Kosovo, and the UK alias.");
    }

    [TestMethod]
    public void StartupRegionsUseMainlandViewsAndHandleDatelineAndUnknownCodes()
    {
        Assert.AreEqual(new PlaceBounds(-124.85, 24.4, -66.85, 49.4), StartupRegion.GetBounds(" us "));
        var france = StartupRegion.GetBounds("FR");
        Assert.IsTrue(france.West > -6 && france.East < 10 && france.South > 40);
        var fiji = StartupRegion.GetBounds("FJ");
        Assert.IsTrue(fiji.West > fiji.East);
        Assert.AreEqual(StartupRegion.GetBounds("GB"), StartupRegion.GetBounds("UK"));
        foreach (string? unknown in new[] { null, "", "ZZ", "001" })
            Assert.AreSame(StartupRegion.World, StartupRegion.GetBounds(unknown));
    }

    [TestMethod]
    public void FailureDiagnostics_ExcludeExceptionMessagesAndServiceContent()
    {
        using var listener = new SampleListener();
        SampleEventSource.Log.UnhandledFailure(new NotSupportedException("private-key-and-location"));
        var failure = Assert.ContainsSingle(listener.Events);
        Assert.AreEqual(1, failure.EventId);
        Assert.AreEqual(EventLevel.Error, failure.Level);
        Assert.AreEqual("System.NotSupportedException", failure.Payload![0]);
        Assert.DoesNotContain("private-key-and-location", string.Join(",", failure.Payload));
    }

    private sealed class SampleListener : EventListener
    {
        internal List<EventWrittenEventArgs> Events { get; } = [];
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "WindowsMapsSample") EnableEvents(eventSource, EventLevel.Error, (EventKeywords)1);
        }
        protected override void OnEventWritten(EventWrittenEventArgs eventData) => Events.Add(eventData);
    }

    [TestMethod]
    public void LatestRequest_ReplacesAndCancelsOldTickets()
    {
        using var request = new LatestRequest();
        var first = request.Begin();
        var second = request.Begin();
        Assert.IsTrue(first.IsCancellationRequested);
        Assert.IsFalse(request.Owns(first));
        Assert.IsTrue(request.IsCurrent(second));
        request.Cancel();
        Assert.IsTrue(second.IsCancellationRequested);
        Assert.IsFalse(request.IsCurrent(second));
    }

    [TestMethod]
    public void Favorites_ToggleEquivalentPlacesAndPersistBetweenSessions()
    {
        var store = new MemoryFavoritesStore();
        var favorites = new Favorites(store);
        var place = new Place("Seattle", "Seattle, WA", 47.6, -122.3);
        favorites.Toggle(place);
        Assert.AreSame(place, Assert.ContainsSingle(favorites.Items));
        Assert.AreEqual(place, Assert.ContainsSingle(new Favorites(store).Items));
        favorites.Toggle(place with { Address = "Updated address" });
        Assert.IsEmpty(favorites.Items);
        Assert.IsEmpty(new Favorites(store).Items);
    }

    [TestMethod]
    public void Favorites_RenamePersistsWithoutChangingPlaceIdentity()
    {
        var store = new MemoryFavoritesStore();
        var place = new Place("Santa Monica Pier", "200 Santa Monica Pier, Santa Monica, CA", 34.008, -118.497);
        var favorites = new Favorites(store);
        favorites.Toggle(place);

        Assert.IsTrue(favorites.Rename(place, "Beach day"));
        var renamed = Assert.ContainsSingle(favorites.Items);
        Assert.AreEqual("Beach day", renamed.Name);
        Assert.AreEqual(place.Address, renamed.Address);
        Assert.IsTrue(favorites.Contains(place));
        Assert.AreEqual(renamed, Assert.ContainsSingle(new Favorites(store).Items));
    }

    [TestMethod]
    [DataRow(401, "rejected")]
    [DataRow(403, "rejected")]
    [DataRow(429, "quota")]
    [DataRow(500, "could not be reached")]
    public void ServiceErrors_DoNotExposeServiceContent(int status, string expected)
    {
        string message = ServiceErrors.Describe(new RequestFailedException(status, "private-key-and-location"));
        Assert.Contains(expected, message);
        Assert.DoesNotContain("private-key-and-location", message);
    }

    [TestMethod]
    public void ServiceErrors_TreatsInvalidRouteGeometryAsRecoverable()
    {
        var error = new ArgumentException("Value does not fall within the expected range.");
        Assert.IsTrue(ServiceErrors.IsExpected(error));
        Assert.Contains("route", ServiceErrors.Describe(error), StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Search_DeserializesCityBoundsAndViewportBias()
    {
        using var handler = new ResponseHandler("""
            {"summary":{"query":"Seattle","numResults":1},"results":[{"type":"Geography","entityType":"Municipality",
            "position":{"lon":-122.33,"lat":47.6},"address":{"freeformAddress":"Seattle, WA","municipality":"Seattle"},
            "viewport":{"topLeftPoint":{"lat":47.73,"lon":-122.46},"btmRightPoint":{"lat":47.49,"lon":-122.22}}}]}
            """);
        using var http = new HttpClient(handler);
        var service = CreateService(http);
        var results = await service.SearchAsync(" Seattle ", CancellationToken.None, new SearchArea(47.6, -122.33, 12000));
        var place = Assert.ContainsSingle(results);
        Assert.AreEqual(47.6, place.Latitude);
        Assert.AreEqual(-122.33, place.Longitude);
        Assert.AreEqual("Seattle, WA", place.Name);
        Assert.AreEqual(new PlaceBounds(-122.46, 47.49, -122.22, 47.73), place.Bounds);
        Assert.IsNotNull(handler.RequestUri);
        Assert.Contains("Seattle", handler.RequestUri.Query);
        Assert.DoesNotContain("radius=", handler.RequestUri.Query);
        Assert.Contains("address", handler.RequestUri.AbsolutePath);
    }

    [TestMethod]
    public async Task Search_PreservesCoffeeBusinessNameAndAddress()
    {
        using var handler = new ResponseHandler("""
            {"summary":{"query":"coffee","numResults":1},"results":[{"type":"POI",
            "position":{"lon":-122.33,"lat":47.6},"poi":{"name":"Neighborhood Coffee"},
            "address":{"freeformAddress":"100 Main Street"}}]}
            """);
        using var http = new HttpClient(handler);
        var place = Assert.ContainsSingle(await CreateService(http).SearchAsync("coffee", CancellationToken.None,
            new SearchArea(47.6, -122.33, 2000)));
        Assert.AreEqual("Neighborhood Coffee", place.Name);
        Assert.AreEqual("100 Main Street", place.Address);
        Assert.AreEqual(47.6, place.Latitude);
        Assert.AreEqual(-122.33, place.Longitude);
        Assert.IsNotNull(handler.RequestUri);
        Assert.Contains("fuzzy", handler.RequestUri.AbsolutePath);
        Assert.Contains("lat=47.6", handler.RequestUri.Query);
        Assert.Contains("lon=-122.33", handler.RequestUri.Query);
    }

    [TestMethod]
    public async Task Search_DoesNotConstrainDistantCountryAndPreservesItsExtent()
    {
        using var handler = new ResponseHandler("""
            {"summary":{"numResults":1},"results":[{"type":"Geography","position":{"lon":2.2,"lat":46.6},
            "address":{"freeformAddress":"France"},"viewport":{"topLeftPoint":{"lat":51.1,"lon":-5.2},
            "btmRightPoint":{"lat":41.3,"lon":9.6}}}]}
            """);
        using var http = new HttpClient(handler);
        var place = Assert.ContainsSingle(await CreateService(http).SearchAsync("France", CancellationToken.None,
            new SearchArea(47.6, -122.33, 2000)));
        Assert.AreEqual(new PlaceBounds(-5.2, 41.3, 9.6, 51.1), place.Bounds);
        Assert.HasCount(2, handler.Requests);
        Assert.DoesNotContain("radius=", handler.RequestUri!.Query);
    }

    [TestMethod]
    public async Task Search_CategoryIntentWinsOverTownWithSameName()
    {
        using var handler = new ResponseHandler("""
            {"summary":{"numResults":1},"results":[{"type":"POI","position":{"lon":-122.33,"lat":47.6},
            "poi":{"name":"Neighborhood Coffee","categories":["coffee shop"]}}]}
            """, addressJson: """
            {"summary":{"numResults":1},"results":[{"type":"Geography","entityType":"Municipality",
            "position":{"lon":-89,"lat":41},"address":{"freeformAddress":"Coffee, IL","municipality":"Coffee"}}]}
            """);
        using var http = new HttpClient(handler);
        var place = Assert.ContainsSingle(await CreateService(http).SearchAsync("coffee", CancellationToken.None,
            new SearchArea(47.6, -122.33, 2000)));
        Assert.AreEqual("Neighborhood Coffee", place.Name);
    }

    [TestMethod]
    public async Task Search_ReusesAzureCategoryNamesAndSynonyms()
    {
        using var handler = new ResponseHandler("""{"summary":{"numResults":0},"results":[]}""");
        using var http = new HttpClient(handler);
        var service = CreateService(http);
        await service.SearchAsync("coffee", CancellationToken.None);
        await service.SearchAsync("cafe", CancellationToken.None);
        Assert.AreEqual(1, handler.Requests.Count(uri => uri.AbsolutePath.Contains("/poi/category/tree/", StringComparison.Ordinal)),
            string.Join(", ", handler.Requests.Select(uri => uri.AbsolutePath)));
        Assert.AreEqual(2, handler.Requests.Count(uri => uri.AbsolutePath.Contains("fuzzy", StringComparison.Ordinal)));
        Assert.IsFalse(handler.Requests.Any(uri => uri.AbsolutePath.Contains("/address/", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Search_CountryWinsOverNearbyBusinessWithSimilarName()
    {
        using var handler = new ResponseHandler("""
            {"summary":{"numResults":1},"results":[{"type":"POI","position":{"lon":-122.33,"lat":47.6},
            "poi":{"name":"Crepe de France","categories":["French restaurant"]}}]}
            """, addressJson: """
            {"summary":{"numResults":1},"results":[{"type":"Geography","entityType":"Country",
            "position":{"lon":2.2,"lat":46.6},"address":{"freeformAddress":"France","country":"France"}}]}
            """);
        using var http = new HttpClient(handler);
        var place = Assert.ContainsSingle(await CreateService(http).SearchAsync("France", CancellationToken.None,
            new SearchArea(47.6, -122.33, 2000)));
        Assert.AreEqual("France", place.Name);
    }

    [TestMethod]
    public async Task Search_KeepsBusinessesWithinExpandedMapArea()
    {
        using var handler = new ResponseHandler("""
            {"summary":{"numResults":2},"results":[
            {"type":"POI","position":{"lon":2.2,"lat":46.6},"poi":{"name":"Distant Coffee"}},
            {"type":"POI","position":{"lon":-122.33,"lat":47.6},"poi":{"name":"Local Coffee"},
            "viewport":{"topLeftPoint":{"lat":47.601,"lon":-122.331},"btmRightPoint":{"lat":47.599,"lon":-122.329}}}]}
            """);
        using var http = new HttpClient(handler);
        var place = Assert.ContainsSingle(await CreateService(http).SearchAsync("coffee", CancellationToken.None,
            new SearchArea(47.6, -122.33, 2000)));
        Assert.AreEqual("Local Coffee", place.Name);
        Assert.AreEqual(new PlaceBounds(-122.331, 47.599, -122.329, 47.601), place.Bounds);
    }

    [TestMethod]
    public void SearchArea_MeasuresAcrossDateLine()
    {
        double distance = new SearchArea(0, 179.9, 30000).DistanceTo(0, -179.9);
        Assert.IsGreaterThan(22000, distance);
        Assert.IsLessThan(22500, distance);
    }

    [TestMethod]
    public async Task Search_InvalidViewportReportsSanitizedError()
    {
        using var handler = new ResponseHandler("""
            {"summary":{"numResults":1},"results":[{"type":"Geography","position":{"lon":2.2,"lat":46.6},
            "viewport":{"topLeftPoint":{"lat":100,"lon":-5.2},"btmRightPoint":{"lat":41.3,"lon":9.6}}}]}
            """);
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsExactlyAsync<System.Text.Json.JsonException>(() =>
            CreateService(http).SearchAsync("France", CancellationToken.None));
        Assert.IsTrue(ServiceErrors.IsExpected(error));
        Assert.Contains("incomplete search information", ServiceErrors.Describe(error));
    }

    [TestMethod]
    public async Task ReverseGeocoding_PreservesTappedCoordinatesAndResolvesAddress()
    {
        using var handler = new ResponseHandler("""
            {"summary":{"numResults":1},"addresses":[{"position":"47.61,-122.34",
            "address":{"freeformAddress":"Selected street address"}}]}
            """);
        using var http = new HttpClient(handler);
        var place = await CreateService(http).ReverseGeocodeAsync(47.6, -122.33, CancellationToken.None);
        Assert.IsNotNull(place);
        Assert.AreEqual("Selected street address", place.Name);
        Assert.AreEqual(47.6, place.Latitude);
        Assert.AreEqual(-122.33, place.Longitude);
        Assert.IsNotNull(handler.RequestUri);
        Assert.Contains("query=47.6,-122.33", Uri.UnescapeDataString(handler.RequestUri.Query));
    }

    [TestMethod]
    public async Task ReverseGeocoding_NoAddressReturnsNoPlace()
    {
        using var handler = new ResponseHandler("""{"summary":{"numResults":0},"addresses":[]}""");
        using var http = new HttpClient(handler);
        Assert.IsNull(await CreateService(http).ReverseGeocodeAsync(0, 0, CancellationToken.None));
    }

    [TestMethod]
    public async Task Search_EmptyResponseReturnsNoPlaces()
    {
        using var handler = new ResponseHandler("""{"summary":{"numResults":0},"results":[]}""");
        using var http = new HttpClient(handler);
        Assert.IsEmpty(await CreateService(http).SearchAsync("unknown", CancellationToken.None));
    }

    [TestMethod]
    [DataRow(false, "car")]
    [DataRow(true, "pedestrian")]
    public async Task Route_UsesSelectedModeAndReturnsGeometryAndItinerary(bool walking, string mode)
    {
        using var handler = new ResponseHandler("""
            {"formatVersion":"0.0.12","routes":[{"summary":{"lengthInMeters":1500,"travelTimeInSeconds":300},
            "legs":[{"summary":{"lengthInMeters":1500,"travelTimeInSeconds":300},
            "points":[{"latitude":47.6,"longitude":-122.33},{"latitude":47.61,"longitude":-122.32}]}],
            "guidance":{"instructions":[{"routeOffsetInMeters":0,"travelTimeInSeconds":0,"pointIndex":0,
            "instructionType":"LOCATION_DEPARTURE","point":{"latitude":47.6,"longitude":-122.33},
            "message":"Head north","maneuver":"DEPART"}]}}]}
            """);
        using var http = new HttpClient(handler);
        var route = await CreateService(http).RouteAsync(
            new Place("Start", "", 47.6, -122.33), new Place("End", "", 47.61, -122.32), walking, CancellationToken.None);
        Assert.IsNotNull(route);
        Assert.HasCount(2, route.Points);
        Assert.AreEqual(-122.33, route.Points[0].Longitude);
        Assert.AreEqual(47.61, route.Points[1].Latitude);
        Assert.AreEqual(1500d, route.Meters);
        Assert.AreEqual(300d, route.Seconds);
        Assert.AreEqual("Head north", Assert.ContainsSingle(route.Steps).Instruction);
        Assert.AreEqual(47.6, route.Steps[0].Latitude);
        Assert.AreEqual(-122.33, route.Steps[0].Longitude);
        Assert.AreEqual(0d, route.Steps[0].Meters);
        Assert.AreEqual("DEPART", route.Steps[0].Maneuver);
        Assert.IsNotNull(handler.RequestUri);
        Assert.Contains($"travelMode={mode}", handler.RequestUri.Query);
        Assert.Contains("instructionsType=text", handler.RequestUri.Query);
        Assert.Contains("47.6,-122.33:47.61,-122.32", Uri.UnescapeDataString(handler.RequestUri.Query));
    }

    [TestMethod]
    [DataRow("TURN_LEFT", RouteManeuverIcon.TurnLeft)]
    [DataRow("TURN_RIGHT", RouteManeuverIcon.TurnRight)]
    [DataRow("BEAR_LEFT", RouteManeuverIcon.BearLeft)]
    [DataRow("KEEP_LEFT", RouteManeuverIcon.ForkLeft)]
    [DataRow("SHARP_RIGHT", RouteManeuverIcon.SharpRight)]
    [DataRow("MERGE_RIGHT", RouteManeuverIcon.MergeRight)]
    [DataRow("TAKE_EXIT_RIGHT", RouteManeuverIcon.ExitRight)]
    [DataRow("ROUNDABOUT_RIGHT", RouteManeuverIcon.RoundaboutRight)]
    [DataRow("MAKE_UTURN", RouteManeuverIcon.UTurn)]
    public void RouteStep_FormatsManeuver(string maneuver, RouteManeuverIcon maneuverIcon)
    {
        var step = new RouteStep("Instruction", 0, 0, 1250, maneuver);
        Assert.AreEqual(maneuverIcon, step.ManeuverIcon);
    }

    [TestMethod]
    public void RouteDistanceFormatter_UsesMetricOrUsCustomaryUnits()
    {
        Assert.AreEqual($"{450:N0} m", RouteDistanceFormatter.Format(450, isMetric: true));
        Assert.AreEqual($"{1.25:N1} km", RouteDistanceFormatter.Format(1250, isMetric: true));
        Assert.AreEqual($"{100 * 3.280839895:N0} ft", RouteDistanceFormatter.Format(100, isMetric: false));
        Assert.AreEqual($"{1250 / 1609.344:N1} mi", RouteDistanceFormatter.Format(1250, isMetric: false));
    }

    [TestMethod]
    public async Task Route_RejectsInstructionWithoutMatchingGeometry()
    {
        using var handler = new ResponseHandler("""
            {"routes":[{"summary":{"lengthInMeters":100,"travelTimeInSeconds":10},
            "legs":[{"points":[{"latitude":47.6,"longitude":-122.33},{"latitude":47.61,"longitude":-122.32}]}],
            "guidance":{"instructions":[{"pointIndex":99,"routeOffsetInMeters":0,"message":"Turn","maneuver":"TURN_RIGHT"}]}}]}
            """);
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => CreateService(http).RouteAsync(
            new Place("A", "", 47.6, -122.33), new Place("B", "", 47.61, -122.32), false, default));
        Assert.Contains("incomplete route information", ServiceErrors.Describe(error));
    }

    [TestMethod]
    public async Task Route_NoRouteIsNotReportedAsSuccess()
    {
        using var handler = new ResponseHandler("""{"formatVersion":"0.0.12","routes":[]}""");
        using var http = new HttpClient(handler);
        Assert.IsNull(await CreateService(http).RouteAsync(
            new Place("A", "", 1, 2), new Place("B", "", 3, 4), false, CancellationToken.None));
    }

    [TestMethod]
    public async Task Validation_RejectsUnauthorizedToken()
    {
        using var handler = new ResponseHandler("""{"error":{"code":"Unauthorized","message":"private"}}""", HttpStatusCode.Unauthorized);
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsExactlyAsync<RequestFailedException>(
            () => CreateService(http).ValidateAsync(CancellationToken.None));
        Assert.AreEqual(401, ex.Status);
    }

    private static AzureMapsService CreateService(HttpClient http)
    {
        var search = new MapsSearchClientOptions { Transport = new HttpClientTransport(http) };
        var route = new MapsRoutingClientOptions { Transport = new HttpClientTransport(http) };
        search.Retry.MaxRetries = route.Retry.MaxRetries = 0;
        return new AzureMapsService(
            new MapsSearchClient(new AzureKeyCredential("unit-test-not-a-key"), search),
            new MapsRoutingClient(new AzureKeyCredential("unit-test-not-a-key"), route));
    }

    private sealed class MemoryFavoritesStore : IFavoritesStore
    {
        private List<Place> _items = [];

        public IReadOnlyList<Place> Load() => _items.ToArray();

        public void Save(IReadOnlyList<Place> favorites) => _items = [.. favorites];
    }

    private sealed class ResponseHandler(string json, HttpStatusCode status = HttpStatusCode.OK, string? addressJson = null) : HttpMessageHandler
    {
        internal Uri? RequestUri { get; private set; }
        internal List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestUri = request.RequestUri;
            Requests.Add(request.RequestUri!);
            string content = request.RequestUri!.AbsolutePath.Contains("/poi/category/tree/", StringComparison.Ordinal)
                ? """{"poiCategories":[{"id":9376002,"name":"Coffee Shop","synonyms":["coffee","cafe"]}]}"""
                : addressJson is not null && request.RequestUri.AbsolutePath.Contains("/address/", StringComparison.Ordinal)
                    ? addressJson : json;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(content,
                    System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
