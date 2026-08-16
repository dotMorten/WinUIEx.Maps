using System.Diagnostics.Tracing;
using System.Text.Json;
using WinUIEx.Maps.Rendering.Diagnostics;

namespace WinUIEx.Maps.Rendering;

internal enum VectorStyleCompatibilityIssueKind
{
    UnsupportedLayerType = 1,
    UnsupportedLayoutProperty = 2,
    UnsupportedPaintProperty = 3,
}

internal readonly record struct VectorStyleCompatibilityIssue(
    VectorStyleCompatibilityIssueKind Kind,
    string Construct,
    int Count);

internal static class VectorStyleCompatibility
{
    private static readonly IReadOnlyDictionary<string, HashSet<string>>
        SupportedLayoutProperties =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            {
                ["background"] = ["visibility"],
                ["fill"] = ["visibility"],
                ["line"] =
                [
                    "visibility",
                    "line-cap",
                    "line-join",
                    "line-miter-limit",
                ],
                ["symbol"] =
                [
                    "visibility",
                    "symbol-placement",
                    "symbol-spacing",
                    "symbol-sort-key",
                    "icon-image",
                    "icon-size",
                    "icon-offset",
                    "icon-anchor",
                    "icon-rotate",
                    "icon-rotation-alignment",
                    "icon-text-fit",
                    "icon-text-fit-padding",
                    "icon-allow-overlap",
                    "icon-ignore-placement",
                    "icon-optional",
                    "text-field",
                    "text-font",
                    "text-size",
                    "text-offset",
                    "text-anchor",
                    "text-radial-offset",
                    "text-letter-spacing",
                    "text-transform",
                    "text-rotation-alignment",
                    "text-variable-anchor",
                    "text-allow-overlap",
                    "text-ignore-placement",
                    "text-optional",
                ],
            };

    private static readonly IReadOnlyDictionary<string, HashSet<string>>
        SupportedPaintProperties =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            {
                ["background"] =
                [
                    "background-color",
                    "background-opacity",
                ],
                ["fill"] =
                [
                    "fill-color",
                    "fill-opacity",
                    "fill-outline-color",
                    "fill-pattern",
                ],
                ["line"] =
                [
                    "line-color",
                    "line-opacity",
                    "line-width",
                    "line-offset",
                    "line-gap-width",
                    "line-blur",
                    "line-dasharray",
                    "line-pattern",
                    "line-gradient",
                ],
                ["symbol"] =
                [
                    "icon-opacity",
                    "icon-color",
                    "text-color",
                    "text-halo-color",
                    "text-halo-width",
                ],
            };

    private static readonly HashSet<string> KnownLayerTypes =
    [
        "background",
        "circle",
        "fill",
        "fill-extrusion",
        "heatmap",
        "hillshade",
        "line",
        "raster",
        "sky",
        "slot",
        "symbol",
    ];

    private static readonly HashSet<string> KnownProperties =
    [
        "background-pattern",
        "fill-antialias",
        "fill-translate",
        "fill-translate-anchor",
        "icon-padding",
        "symbol-avoid-edges",
        "text-halo-blur",
        "text-justify",
        "text-keep-upright",
        "text-line-height",
        "text-max-angle",
        "text-max-width",
        "text-opacity",
        "text-padding",
    ];

    internal static IReadOnlyList<VectorStyleCompatibilityIssue> Analyze(
        ReadOnlyMemory<byte> json)
    {
        using JsonDocument document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                MaxDepth = 64,
            });
        if (!document.RootElement.TryGetProperty(
                "layers",
                out JsonElement layers) ||
            layers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        Dictionary<(VectorStyleCompatibilityIssueKind Kind, string Construct), int>
            counts = [];
        foreach (JsonElement layer in layers.EnumerateArray())
        {
            if (layer.ValueKind != JsonValueKind.Object ||
                !layer.TryGetProperty("type", out JsonElement typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                typeElement.GetString() is not string layerType)
            {
                continue;
            }
            if (!SupportedLayoutProperties.ContainsKey(layerType))
            {
                Add(
                    counts,
                    VectorStyleCompatibilityIssueKind.UnsupportedLayerType,
                    KnownLayerTypes.Contains(layerType) ? layerType : "other");
                continue;
            }
            CountUnsupportedProperties(
                layer,
                "layout",
                layerType,
                SupportedLayoutProperties,
                VectorStyleCompatibilityIssueKind.UnsupportedLayoutProperty,
                counts);
            CountUnsupportedProperties(
                layer,
                "paint",
                layerType,
                SupportedPaintProperties,
                VectorStyleCompatibilityIssueKind.UnsupportedPaintProperty,
                counts);
        }

        return
        [
            .. counts
                .OrderBy(pair => pair.Key.Kind)
                .ThenBy(pair => pair.Key.Construct, StringComparer.Ordinal)
                .Select(pair => new VectorStyleCompatibilityIssue(
                    pair.Key.Kind,
                    pair.Key.Construct,
                    pair.Value)),
        ];
    }

    internal static void Report(int style, ReadOnlyMemory<byte> json)
    {
        if (!MapControlEventSource.Log.IsEnabled(
                EventLevel.Informational,
                MapControlEventSource.Keywords.Tiles |
                MapControlEventSource.Keywords.VectorTiles))
        {
            return;
        }
        foreach (VectorStyleCompatibilityIssue issue in Analyze(json))
        {
            MapControlEventSource.Log.VectorStyleCompatibilityIssue(
                style,
                (int)issue.Kind,
                issue.Construct,
                issue.Count);
        }
    }

    private static void CountUnsupportedProperties(
        JsonElement layer,
        string ownerName,
        string layerType,
        IReadOnlyDictionary<string, HashSet<string>> supportedByType,
        VectorStyleCompatibilityIssueKind kind,
        Dictionary<(VectorStyleCompatibilityIssueKind Kind, string Construct), int>
            counts)
    {
        if (!layer.TryGetProperty(ownerName, out JsonElement owner) ||
            owner.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        HashSet<string> supported = supportedByType[layerType];
        foreach (JsonProperty property in owner.EnumerateObject())
        {
            if (!supported.Contains(property.Name))
            {
                Add(
                    counts,
                    kind,
                    KnownProperties.Contains(property.Name)
                        ? property.Name
                        : "other");
            }
        }
    }

    private static void Add(
        Dictionary<(VectorStyleCompatibilityIssueKind Kind, string Construct), int>
            counts,
        VectorStyleCompatibilityIssueKind kind,
        string construct)
    {
        (VectorStyleCompatibilityIssueKind Kind, string Construct) key =
            (kind, construct);
        counts[key] = counts.GetValueOrDefault(key) + 1;
    }
}
