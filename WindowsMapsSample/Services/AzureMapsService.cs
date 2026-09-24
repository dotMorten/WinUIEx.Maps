using Azure;
using Azure.Core.GeoJson;
using Azure.Maps.Routing;
using Azure.Maps.Search;
using Azure.Maps.Search.Models;
using System.Text.Json;
using System.Globalization;
using WindowsMapsSample.Models;

namespace WindowsMapsSample.Services;

internal interface IMapsService
{
    Task ValidateAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Place>> SearchAsync(string query, CancellationToken cancellationToken, SearchArea? area = null);
    Task<Place?> ReverseGeocodeAsync(double latitude, double longitude, CancellationToken cancellationToken);
    Task<MapRoute?> RouteAsync(Place origin, Place destination, bool walking, CancellationToken cancellationToken);
}

internal sealed class AzureMapsService : IMapsService
{
    private readonly MapsSearchClient _searchClient;
    private readonly MapsRoutingClient _routingClient;
    private readonly SemaphoreSlim _categoryGate = new(1, 1);
    private volatile string[]? _categoryTerms;

    internal AzureMapsService(string token)
    {
        var searchOptions = new MapsSearchClientOptions();
        var routingOptions = new MapsRoutingClientOptions();
        // Search and route URLs contain personal locations. Do not emit SDK HTTP diagnostics.
        searchOptions.Diagnostics.IsLoggingEnabled = false;
        searchOptions.Diagnostics.IsDistributedTracingEnabled = false;
        routingOptions.Diagnostics.IsLoggingEnabled = false;
        routingOptions.Diagnostics.IsDistributedTracingEnabled = false;
        searchOptions.Retry.MaxRetries = routingOptions.Retry.MaxRetries = 2;
        var credential = new AzureKeyCredential(token.Trim());
        _searchClient = new MapsSearchClient(credential, searchOptions);
        _routingClient = new MapsRoutingClient(credential, routingOptions);
    }

    internal AzureMapsService(MapsSearchClient search, MapsRoutingClient routing)
    {
        _searchClient = search;
        _routingClient = routing;
    }

    public async Task ValidateAsync(CancellationToken cancellationToken) =>
        _ = await _searchClient.FuzzySearchAsync("Seattle", new FuzzySearchOptions { Top = 1 }, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<Place>> SearchAsync(string query, CancellationToken cancellationToken, SearchArea? area = null)
    {
        query = query.Trim();
        var options = new FuzzySearchOptions { Top = 20 };
        if (area is not null)
        {
            options.Coordinates = new GeoPosition(area.Longitude, area.Latitude);
        }
        bool geographicMatch = false;
        Response<SearchAddressResult> response;
        if (await IsCategoryAsync(query, cancellationToken).ConfigureAwait(false))
            response = await _searchClient.FuzzySearchAsync(query, options, cancellationToken).ConfigureAwait(false);
        else
        {
            response = await _searchClient.SearchAddressAsync(query, new SearchAddressOptions { Top = 10 }, cancellationToken).ConfigureAwait(false);
            geographicMatch = response.Value.Results.Any(result => IsExactGeography(result, query));
            if (!geographicMatch)
                response = await _searchClient.FuzzySearchAsync(query, options, cancellationToken).ConfigureAwait(false);
        }
        // The v1 SDK Viewport getter swaps latitude/longitude and throws when viewport is absent.
        // Read this optional field from the SDK response without reflection (also safe under AOT).
        using var document = JsonDocument.Parse(response.GetRawResponse().Content.ToMemory());
        if (!document.RootElement.TryGetProperty("results", out var rawResults) ||
            rawResults.ValueKind != JsonValueKind.Array || rawResults.GetArrayLength() != response.Value.Results.Count)
            throw new JsonException("The search response has no matching results array.");
        return response.Value.Results.Select((result, index) => (Result: result, Index: index))
            // Constrain businesses, not named cities/countries or addresses outside the current map.
            .Where(item => geographicMatch ? IsExactGeography(item.Result, query) :
                area is null || item.Result.SearchAddressResultType != SearchAddressResultType.PointOfInterest ||
                    area.DistanceTo(item.Result.Position.Latitude, item.Result.Position.Longitude) <= area.RadiusInMeters)
            .Select(item =>
        {
            var result = item.Result;
            string address = result.Address?.FreeformAddress ?? query.Trim();
            return new Place(result.PointOfInterest?.Name ?? address, address,
                result.Position.Latitude, result.Position.Longitude, ReadBounds(rawResults[item.Index]));
        }).ToArray();
    }

    private async Task<bool> IsCategoryAsync(string query, CancellationToken cancellationToken)
    {
        if (_categoryTerms is null)
        {
            await _categoryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_categoryTerms is null)
                {
                    var response = await _searchClient.GetPointOfInterestCategoryTreeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    _categoryTerms = response.Value.Categories
                        .SelectMany(category => category.Synonyms.Append(category.Name)).ToArray();
                }
            }
            finally { _categoryGate.Release(); }
        }
        // Azure's category names/synonyms distinguish "coffee" from towns named Coffee.
        const CompareOptions comparison = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        string prefix = query + " ";
        return _categoryTerms.Any(term =>
            CultureInfo.InvariantCulture.CompareInfo.Compare(term, query, comparison) == 0 ||
            CultureInfo.InvariantCulture.CompareInfo.IsPrefix(term, prefix, comparison));
    }

    private static bool IsExactGeography(SearchAddressResultItem result, string query)
    {
        if (result.SearchAddressResultType != SearchAddressResultType.Geography || result.Address is not { } address)
            return false;
        string? name = (result.EntityType is { } entity ? entity.ToString() : "") switch
        {
            "Country" => address.Country,
            "CountrySubdivision" => address.CountrySubdivision,
            "CountrySecondarySubdivision" => address.CountrySecondarySubdivision,
            "Municipality" => address.Municipality,
            "MunicipalitySubdivision" => address.MunicipalitySubdivision,
            _ => null,
        };
        return string.Equals(query, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(query, address.FreeformAddress, StringComparison.OrdinalIgnoreCase);
    }

    private static PlaceBounds? ReadBounds(JsonElement result)
    {
        if (!result.TryGetProperty("viewport", out var viewport) || viewport.ValueKind == JsonValueKind.Null)
            return null;
        if (viewport.ValueKind != JsonValueKind.Object ||
            !viewport.TryGetProperty("topLeftPoint", out var northWest) ||
            !viewport.TryGetProperty("btmRightPoint", out var southEast))
            throw new JsonException("The search viewport has no corners.");
        var bounds = new PlaceBounds(Coordinate(northWest, "lon", 180), Coordinate(southEast, "lat", 90),
            Coordinate(southEast, "lon", 180), Coordinate(northWest, "lat", 90));
        if (bounds.South > bounds.North) throw new JsonException("The search viewport has invalid latitude bounds.");
        return bounds;

        static double Coordinate(JsonElement point, string name, double limit)
        {
            if (point.ValueKind != JsonValueKind.Object || !point.TryGetProperty(name, out var property) ||
                property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out double value) ||
                !double.IsFinite(value) || Math.Abs(value) > limit)
                throw new JsonException("The search viewport has an invalid coordinate.");
            return value;
        }
    }

    public async Task<MapRoute?> RouteAsync(Place origin, Place destination, bool walking, CancellationToken cancellationToken)
    {
        var options = new RouteDirectionOptions
        {
            TravelMode = walking ? TravelMode.Pedestrian : TravelMode.Car,
            InstructionsType = RouteInstructionsType.Text,
            UseTrafficData = false,
        };
        var query = new RouteDirectionQuery(
            [new GeoPosition(origin.Longitude, origin.Latitude), new GeoPosition(destination.Longitude, destination.Latitude)],
            options);
        var response = await _routingClient.GetDirectionsAsync(query, cancellationToken).ConfigureAwait(false);
        var route = response.Value.Routes.FirstOrDefault();
        if (route is null)
            return null;

        var points = route.Legs.SelectMany(leg => leg.Points)
            .Select(point => new Place("", "", point.Latitude, point.Longitude)).ToArray();
        double previousOffset = 0;
        var steps = route.Guidance?.Instructions.Select(step =>
        {
            if (step.PointIndex is not int index || index < 0 || index >= points.Length)
                throw new InvalidDataException("The route instruction has no corresponding geometry point.");
            var position = points[index];
            double? distance = step.RouteOffsetInMeters is int offset ? Math.Max(0, offset - previousOffset) : null;
            if (step.RouteOffsetInMeters is int currentOffset) previousOffset = currentOffset;
            return new RouteStep(step.Message ?? step.Maneuver.ToString() ?? "Instruction unavailable.",
                position.Latitude, position.Longitude, distance, step.Maneuver.ToString());
        }).ToArray() ?? [];
        return new MapRoute(points, steps, route.Summary.LengthInMeters, route.Summary.TravelTimeInSeconds);
    }

    public async Task<Place?> ReverseGeocodeAsync(double latitude, double longitude, CancellationToken cancellationToken)
    {
        var response = await _searchClient.ReverseSearchAddressAsync(
            new ReverseSearchOptions { Coordinates = new GeoPosition(longitude, latitude) }, cancellationToken).ConfigureAwait(false);
        string? address = response.Value.Addresses.FirstOrDefault()?.Address?.FreeformAddress;
        return string.IsNullOrWhiteSpace(address) ? null : new Place(address, address, latitude, longitude);
    }
}
