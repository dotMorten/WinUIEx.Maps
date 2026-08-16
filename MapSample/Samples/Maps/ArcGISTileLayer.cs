using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinUIEx.Maps;

namespace MapSample.Samples.Maps;

public sealed class ArcGISTileLayer : TileLayer
{
    private const int MaximumServiceMetadataBytes = 4 * 1024 * 1024;
    private const string TemplateMarkerPrefix = "__WINUIEX_";
    private static readonly HttpClient HttpClient = new();

    public const string DefaultServiceUrl =
        "https://basemaps.arcgis.com/arcgis/rest/services/World_Basemap_v2/VectorTileServer";
    public const string NightStyleUrl =
        "https://www.arcgis.com/sharing/rest/content/items/86f556a2d1fd468181855a35e344567f/resources/styles/root.json";
    public const string ModernAntiqueStyleUrl =
        "https://www.arcgis.com/sharing/rest/content/items/effe3475f05a4d608e66fd6eeb2113c0/resources/styles/root.json";

    private ArcGISTileLayer(
        TileLayerOptions options,
        string attribution,
        Uri attributionLink)
        : base(options, "arcgis-vector-basemap")
    {
        Attribution = attribution;
        AttributionLink = attributionLink;
    }

    public static async Task<ArcGISTileLayer> CreateAsync(
        string serviceUrl,
        string? token = null,
        CancellationToken cancellationToken = default)
    {
        Uri serviceUri = ParseServiceUri(serviceUrl);
        IReadOnlyDictionary<string, string> headers = CreateHeaders(token);
        Uri metadataUri = AddJsonFormat(serviceUri);
        using HttpRequestMessage request = new(HttpMethod.Get, metadataUri);
        foreach ((string name, string value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using HttpResponseMessage response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength >
            MaximumServiceMetadataBytes)
        {
            throw new InvalidDataException(
                "The ArcGIS vector tile service metadata is too large.");
        }

        byte[] json = await response.Content.ReadAsByteArrayAsync(
            cancellationToken);
        if (json.Length > MaximumServiceMetadataBytes)
        {
            throw new InvalidDataException(
                "The ArcGIS vector tile service metadata is too large.");
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string tileTemplate = GetFirstString(root, "tiles");
        string defaultStyles = GetRequiredString(root, "defaultStyles");
        JsonElement tileInfo = GetRequiredObject(root, "tileInfo");
        int rows = GetRequiredInt32(tileInfo, "rows");
        int columns = GetRequiredInt32(tileInfo, "cols");
        if (rows != columns || rows is < 1 or > 4096)
        {
            throw new InvalidDataException(
                "The ArcGIS vector tile service must use square tiles.");
        }

        JsonElement lods = GetRequiredArray(tileInfo, "lods");
        int[] levels =
        [
            .. lods.EnumerateArray()
                .Select(lod => GetRequiredInt32(lod, "level")),
        ];
        if (levels.Length == 0)
        {
            throw new InvalidDataException(
                "The ArcGIS vector tile service does not define any levels.");
        }

        string attribution = root.TryGetProperty(
                "copyrightText",
                out JsonElement copyright) &&
            copyright.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(copyright.GetString())
                ? copyright.GetString()!
                : "Esri and contributors";
        Uri resourceBase = new(
            serviceUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        TileLayerOptions options = new()
        {
            TileUrl = ResolveTemplateUrl(resourceBase, tileTemplate),
            StyleUrl = AddJsonFormat(
                new Uri(resourceBase, defaultStyles)).AbsoluteUri,
            RequestHeaders = headers,
            TileSize = rows,
            MinSourceZoom = levels.Min(),
            MaxSourceZoom = levels.Max(),
        };
        return new ArcGISTileLayer(options, attribution, serviceUri);
    }

    private static Uri ParseServiceUri(string serviceUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceUrl);
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out Uri? serviceUri) ||
            (serviceUri.Scheme != Uri.UriSchemeHttp &&
             serviceUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "The ArcGIS vector tile service URL must be an absolute HTTP/HTTPS URL.",
                nameof(serviceUrl));
        }

        return serviceUri;
    }

    private static IReadOnlyDictionary<string, string> CreateHeaders(
        string? token) =>
        string.IsNullOrWhiteSpace(token)
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>
            {
                ["X-Esri-Authorization"] = token.StartsWith(
                    "Bearer ",
                    StringComparison.OrdinalIgnoreCase)
                        ? token
                        : $"Bearer {token}",
            };

    private static Uri AddJsonFormat(Uri uri)
    {
        UriBuilder builder = new(uri);
        string query = builder.Query.TrimStart('?');
        builder.Query = string.IsNullOrEmpty(query)
            ? "f=json"
            : $"{query}&f=json";
        return builder.Uri;
    }

    private static string ResolveTemplateUrl(Uri baseUri, string template)
    {
        string marked = template
            .Replace("{z}", TemplateMarkerPrefix + "Z__", StringComparison.OrdinalIgnoreCase)
            .Replace("{x}", TemplateMarkerPrefix + "X__", StringComparison.OrdinalIgnoreCase)
            .Replace("{y}", TemplateMarkerPrefix + "Y__", StringComparison.OrdinalIgnoreCase);
        string resolved = new Uri(baseUri, marked).AbsoluteUri;
        return resolved
            .Replace(TemplateMarkerPrefix + "Z__", "{z}", StringComparison.Ordinal)
            .Replace(TemplateMarkerPrefix + "X__", "{x}", StringComparison.Ordinal)
            .Replace(TemplateMarkerPrefix + "Y__", "{y}", StringComparison.Ordinal);
    }

    private static string GetFirstString(JsonElement owner, string name)
    {
        JsonElement values = GetRequiredArray(owner, name);
        if (values.GetArrayLength() == 0 ||
            values[0].ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(values[0].GetString()))
        {
            throw new InvalidDataException(
                $"The ArcGIS vector tile service has an invalid '{name}' value.");
        }

        return values[0].GetString()!;
    }

    private static string GetRequiredString(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"The ArcGIS vector tile service has an invalid '{name}' value.");
        }

        return value.GetString()!;
    }

    private static JsonElement GetRequiredObject(
        JsonElement owner,
        string name)
    {
        if (!owner.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"The ArcGIS vector tile service has an invalid '{name}' value.");
        }

        return value;
    }

    private static JsonElement GetRequiredArray(
        JsonElement owner,
        string name)
    {
        if (!owner.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"The ArcGIS vector tile service has an invalid '{name}' value.");
        }

        return value;
    }

    private static int GetRequiredInt32(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out JsonElement value) ||
            !value.TryGetInt32(out int result))
        {
            throw new InvalidDataException(
                $"The ArcGIS vector tile service has an invalid '{name}' value.");
        }

        return result;
    }
}
