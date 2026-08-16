# Custom vector tiles

The same `TileLayer` used for raster sources can display Mapbox Vector Tile (MVT) PBF data.
Set both `TileUrl` and `StyleUrl`; no separate vector-layer type or renderer is required.

## 1. Add a vector tile layer

Start with a blank map:

```xml
<maps:MapControl
    x:Name="Map"
    MapStyle="Blank" />
```

Configure an MVT endpoint and style:

```csharp
using WinUIEx.Maps;

TileLayer vectorLayer = new(
    new TileLayerOptions
    {
        TileUrl = "https://tiles.example.com/{z}/{x}/{y}.pbf",
        StyleUrl = "https://tiles.example.com/styles/road/style.json",
        TileSize = 512,
        MinSourceZoom = 0,
        MaxSourceZoom = 16,
    },
    id: "custom-vector")
{
    Attribution = "Example Maps",
    AttributionLink = new Uri("https://example.com/attribution"),
};

Map.Layers.Add(vectorLayer);
```

When `StyleUrl` is non-null, `TileUrl` is interpreted as an MVT PBF template. When
`StyleUrl` is null, the same layer is treated as a raster image source.

Use the tile size and zoom range documented by the service. For metadata-driven services,
read these values from the service instead of guessing.

## 2. Provide a compatible style

`StyleUrl` must return a Mapbox Style Specification JSON document. See the official
[Mapbox Style Specification](https://docs.mapbox.com/style-spec/guides/).

The style can reference:

- Sprite JSON and sprite atlas images.
- Glyph PBF range templates.
- Relative or absolute HTTP/HTTPS resource URLs.
- Source layers and properties contained in the MVT data.

Relative sprite and glyph URLs resolve from the style URL. Keep all resources reachable
from the application and use provider-authorized origins.

WinUIEx.Maps intentionally implements a subset of the Style Specification. It supports the
background, fill, line, circle, and symbol behavior used by its renderer, including many
expressions, legacy stop functions, text and icon tokens, sprites, glyphs, collision, line
decorations, and common advanced line and symbol properties. Unsupported layer types,
properties, or expressions are skipped rather than treated as full Mapbox compatibility.
Test every style used by the application.

## 3. Add headers and source limits

```csharp
TileLayer vectorLayer = new(new TileLayerOptions
{
    TileUrl = "https://tiles.example.com/{z}/{x}/{y}.pbf",
    StyleUrl = "https://tiles.example.com/styles/default.json",
    RequestHeaders = new Dictionary<string, string>
    {
        ["Authorization"] = $"Bearer {token}",
    },
    Bounds = new TileLayerBounds(-125, 24, -66, 50),
    TileSize = 512,
    MinSourceZoom = 0,
    MaxSourceZoom = 16,
    MinZoom = 3,
    MaxZoom = 20,
});
```

Headers are sent only to the origins explicitly configured by `TileUrl` and `StyleUrl`.
They are not automatically forwarded to a different origin referenced by a downloaded
style. Configure each provider architecture accordingly and never embed credentials in
committed URLs.

## 4. Switch styles at runtime

One vector source can use another compatible style:

```csharp
vectorLayer.StyleUrl =
    "https://tiles.example.com/styles/night/style.json";
```

Changing `StyleUrl` creates a new acquisition session and generation. Obsolete tile and
style work is canceled when possible, and resources from different style generations are
not mixed.

You can also control the entire layer:

```csharp
vectorLayer.Opacity = 0.8;
vectorLayer.IsVisible = true;
vectorLayer.FadeDuration = TimeSpan.FromMilliseconds(200);
```

## 5. Discover configuration from service metadata

Some services publish their tile template, default style, tile dimensions, zoom levels,
attribution, and resource base URL as metadata. Fetch and validate that metadata before
constructing the layer:

```csharp
ServiceMetadata metadata = await LoadAndValidateMetadataAsync(
    serviceUrl,
    cancellationToken);

TileLayer layer = new(new TileLayerOptions
{
    TileUrl = metadata.TileTemplate,
    StyleUrl = metadata.DefaultStyleUrl,
    RequestHeaders = metadata.RequestHeaders,
    TileSize = metadata.TileSize,
    MinSourceZoom = metadata.MinZoom,
    MaxSourceZoom = metadata.MaxZoom,
})
{
    Attribution = metadata.Attribution,
    AttributionLink = metadata.AttributionLink,
};
```

Validation should include:

- Absolute HTTP/HTTPS service and resource URLs.
- Bounded metadata response size.
- Square, supported tile dimensions.
- A non-empty ordered zoom range.
- Correct resolution of relative tile, style, sprite, and glyph resources.
- Provider-specific authentication and attribution rules.

The sample application's
[`ArcGISTileLayer`](../MapSample/Samples/Maps/ArcGISTileLayer.cs) demonstrates this pattern
for an ArcGIS Vector Tile Service. See the
[ArcGIS Vector Tile Service REST documentation](https://developers.arcgis.com/rest/services-reference/enterprise/vector-tile-service/)
for the provider contract.

## 6. Layer custom vectors over Azure

Custom vectors do not require `MapStyle.Blank`. They can render above an Azure base map:

```csharp
Map.MapServiceToken = azureMapsKey;
Map.MapStyle = MapStyle.GrayscaleLight;
Map.Layers.Add(vectorLayer);
```

Public layers retain collection order above the hidden Azure layer. Use a blank style only
when no Azure base map is wanted.

## Troubleshooting

- **Blank layer:** verify both `TileUrl` and `StyleUrl`, and check that PBF tiles are valid
  MVT data.
- **Wrong detail level:** use the service's actual `TileSize`, `MinSourceZoom`, and
  `MaxSourceZoom`.
- **Geometry renders but labels do not:** verify glyph and sprite resources, relative URL
  resolution, response permissions, and supported style expressions.
- **Some style layers are absent:** inspect unsupported Style Specification diagnostics and
  simplify or preprocess the style.
- **Assets return unauthorized:** remember that configured headers are not forwarded to
  unrelated origins referenced by the style.
- **Stale output after switching:** source changes are asynchronous; obsolete work is
  generation-checked and discarded.

Use the `WinUIEx-Maps-Rendering` EventSource for privacy-safe diagnosis. Vector events
separate tile acquisition, style assets, glyphs, symbols, geometry, cache reuse, and
unsupported style constructs without recording URLs, layer identifiers, properties, or
response content. See the repository's
[ETW diagnostics guide](../.github/skills/mapcontrol-etw-diagnostics/SKILL.md).
