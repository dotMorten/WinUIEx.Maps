# Custom raster tiles

`TileLayer` displays square raster image tiles from an HTTP or HTTPS endpoint. This guide
starts with an OpenStreetMap XYZ layer, then adds source limits and advanced configuration.

## 1. Add an OpenStreetMap layer

Use `MapStyle.Blank` to avoid Azure requests:

```xml
<maps:MapControl
    x:Name="Map"
    MapStyle="Blank" />
```

Create the layer:

```csharp
using WinUIEx.Maps;

TileLayer openStreetMap = new(
    new TileLayerOptions
    {
        TileUrl =
            "https://tile.openstreetmap.org/[level]/[column]/[row].png",
        TileSize = 256,
        MaxSourceZoom = 19,
        RequestHeaders = new Dictionary<string, string>
        {
            ["User-Agent"] = "MyMapApp/1.0 (https://example.com/contact)",
        },
    },
    id: "openstreetmap")
{
    Attribution = "© OpenStreetMap contributors",
    AttributionLink =
        new Uri("https://www.openstreetmap.org/copyright"),
};

Map.Layers.Add(openStreetMap);
```

Set `Center` and `ZoomLevel` as described in [Getting started](getting-started.md).

> [!IMPORTANT]
> OpenStreetMap data is open, but the public `tile.openstreetmap.org` servers are a
> donation-funded, best-effort service with a mandatory
> [tile usage policy](https://operations.osmfoundation.org/policies/tiles/). The policy
> requires visible attribution, an identifiable User-Agent, appropriate caching, and no
> bulk downloading or prefetching. WinUIEx.Maps has a bounded runtime cache, but you should
> not assume that it satisfies the policy's persistent caching requirements. For production
> traffic, use an OSM-derived provider whose terms match your application or host tiles
> yourself.

Review the [OpenStreetMap copyright and attribution requirements](https://www.openstreetmap.org/copyright)
before distributing the application.

## 2. Choose a URL template

`TileUrl` accepts these placeholders:

| Placeholder | Meaning |
| --- | --- |
| `{z}` or `[level]` | Source zoom level. |
| `{x}` or `[column]` | Tile column. |
| `{y}` or `[row]` | Tile row. |
| `{quadkey}` | Bing-style quadkey. |
| `{bbox-epsg-3857}` | Tile bounds in EPSG:3857 coordinates. |
| `{subdomain}` | Value selected from `Subdomains`. |

Only absolute HTTP or HTTPS templates are accepted. Prefer HTTPS.

For a subdomain-based endpoint:

```csharp
TileLayer layer = new(new TileLayerOptions
{
    TileUrl = "https://{subdomain}.example.com/{z}/{x}/{y}.png",
    Subdomains = ["a", "b", "c"],
    TileSize = 256,
});
```

Use only subdomains and templates permitted by the provider.

## 3. Match the source tile size

`TileSize` must equal the downloaded image's native width and height. A 512-pixel source is
not interchangeable with a 256-pixel source: tile size also affects source-zoom selection.
Images with mismatched dimensions are rejected.

```csharp
TileSize = 512
```

## 4. Separate source zooms from display zooms

Source zooms describe the levels the server can return:

```csharp
MinSourceZoom = 0,
MaxSourceZoom = 19,
```

Display zooms control when the layer is visible and acquired:

```csharp
MinZoom = 4,
MaxZoom = 18, // exclusive
```

Above `MaxSourceZoom`, the highest available source tiles can be scaled. `MinZoom` is
inclusive and `MaxZoom` is exclusive.

## 5. Configure bounds, TMS, and transitions

Restrict requests to a source's geographic coverage:

```csharp
Bounds = new TileLayerBounds(
    west: -125,
    south: 24,
    east: -66,
    north: 50),
```

Bounds cannot cross the antimeridian.

Set `IsTMS = true` when the endpoint numbers rows from bottom to top instead of XYZ
top-to-bottom numbering.

Control tile appearance:

```csharp
FadeDuration = TimeSpan.FromMilliseconds(150),
```

At runtime, every `MapLayer` also supports:

```csharp
openStreetMap.Opacity = 0.7;
openStreetMap.IsVisible = false;
```

An invisible layer, or a layer with zero opacity, suppresses acquisition that is no longer
needed.

## 6. Add authentication headers

Use `RequestHeaders` only when the provider documents header-based authentication:

```csharp
TileLayer layer = new(new TileLayerOptions
{
    TileUrl = "https://tiles.example.com/{z}/{x}/{y}.png",
    RequestHeaders = new Dictionary<string, string>
    {
        ["Authorization"] = $"Bearer {token}",
    },
});
```

Load tokens from secure configuration. Do not commit them or place them in example URLs.
Headers are copied when the layer is created and are not included in renderer diagnostics.

## 7. Change a layer at runtime

`TileLayer` properties are dependency properties and must be accessed on the map's UI
thread:

```csharp
layer.TileUrl = newTemplate;
layer.Bounds = newBounds;
layer.MaxSourceZoom = 20;
```

Changing source configuration creates a new source generation and cancels obsolete work
when possible. Requests already received by a remote endpoint cannot be recalled.

Layer order is collection order:

```csharp
Map.Layers.Add(baseTiles);     // lower
Map.Layers.Add(weatherTiles);  // higher
```

## Troubleshooting

- **Nothing renders:** verify `TileUrl` is non-empty, HTTPS, and uses supported placeholders.
- **Every tile fails:** confirm the endpoint, authentication, and provider terms.
- **Decoded images are rejected:** match `TileSize` to the native image dimensions.
- **Rows are upside down:** set or clear `IsTMS`.
- **Unexpected zoom behavior:** distinguish `MinSourceZoom`/`MaxSourceZoom` from
  `MinZoom`/`MaxZoom`.
- **Missing attribution:** set `Attribution` and optionally `AttributionLink`; custom layers
  do not receive automatic provider attribution.

For runtime investigation, use the `WinUIEx-Maps-Rendering` EventSource rather than adding
tokens or URLs to application logs. The repository's
[ETW diagnostics guide](../.github/skills/mapcontrol-etw-diagnostics/SKILL.md) describes
the provider and privacy-safe events.
