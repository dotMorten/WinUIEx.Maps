---
name: winuiex-maps
description: Build WinUI 3 maps with Azure Maps basemaps, custom tiles, map elements, camera navigation, and accessible interactions.
---

# WinUIEx.Maps

Use this skill when adding `WinUIEx.Maps` to a WinUI 3 app or changing an existing
`MapControl`. Prefer the documented public API: `MapControl`, `TileLayer`,
`MapElementsLayer`, `MapIcon`, `MapPolyline`, and `MapPolygon`.

## Start with the appropriate base map

Use an Azure style only when the application has an Azure Maps key, loaded from secure
configuration rather than source code:

```xml
<Page
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:maps="using:WinUIEx.Maps">

    <maps:MapControl
        x:Name="Map"
        MapServiceToken="AZURE_MAPS_SUBSCRIPTION_KEY"
        AutomationProperties.Name="Map"
        MapStyle="Road" />
</Page>
```

Set `MapStyle.Blank` for a custom-only map. It makes no Azure tile or attribution
requests and needs no Azure token:

```csharp
Map.MapStyle = MapStyle.Blank;
```

## Navigate through map properties

Set or bind `Center`, `ZoomLevel`, `Heading`, and `Pitch`; user interaction input updates the same
properties. Use `TrySetViewAsync` when dependent work must wait for the view to display the new location.

```csharp
Geopoint destination = new(new BasicGeoposition
{
    Longitude = -122.3352,
    Latitude = 47.6080,
});

bool displayed = await Map.TrySetViewAsync(
    destination,
    zoomLevel: 13,
    heading: 0,
    desiredPitch: 35,
    animation: MapAnimationKind.Bow);
```

Treat a `false` result as a newer camera request superseding this one. Use
`TryGetLocationFromOffset` for pointer-to-location conversion because it uses the
currently displayed camera, including an active animation.

## Add custom raster or vector tiles

Use `TileLayer` for a custom HTTP(S) source. The public layer order is draw order:
the first layer is lowest, and every public layer is above the Azure base map.

```csharp
TileLayer streets = new(
    new TileLayerOptions
    {
        TileUrl = "https://tiles.example.com/{z}/{x}/{y}.png",
        TileSize = 256,
        MinSourceZoom = 0,
        MaxSourceZoom = 19,
    },
    id: "streets")
{
    Attribution = "Example provider",
};

Map.Layers.Add(streets);
```

- Use only absolute HTTP(S) templates. Supported aliases include `{z}`, `{x}`, `{y}`,
  `{quadkey}`, `{bbox-epsg-3857}`, `{subdomain}`, `[level]`, `[column]`, and `[row]`.
- Match `TileSize` to the source image's native width and height. A 512-pixel source is
  not interchangeable with a 256-pixel source.
- Use `IsTMS = true` only for bottom-to-top TMS rows.
- `MinSourceZoom`/`MaxSourceZoom` describe server levels. `MinZoom` is inclusive and
  `MaxZoom` is exclusive display/acquisition limits.
- Set `StyleUrl` only when `TileUrl` serves Mapbox Vector Tile PBF data. It supplies the
  Mapbox style, sprites, and glyphs required for rendering.
- Configure provider-required headers with `RequestHeaders`, and visibly present all
  provider-required attribution. Never commit credentials.

## Add overlays and interaction

Use `MapElementsLayer` for icons, lines, and polygons. Add it after tile layers when it
must appear above them. Reuse the same unparented `IconElement` for visually identical
icons so they share a raster and GPU texture.

```csharp
var pins = new MapElementsLayer();
Map.Layers.Add(pins);

var pinVisual = new FontIcon
{
    Glyph = "\uE707",
    FontSize = 28,
    Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red),
};

pins.MapElements.Add(new MapIcon(pinVisual, destination)
{
    NormalizedAnchorPoint = new Point(0.5, 1),
});

pins.Tapped += (sender, args) =>
{
    if (args.MapElement is MapIcon)
    {
        args.Handled = true;
    }
};
```

`ZIndex` orders elements only within a `MapElementsLayer`; use layer position to order
separate groups. `IsVisible` controls rendering, and `IsEnabled` controls element hit
testing. Subscribe only to the map-element events the app needs.

## Respect thread and lifetime boundaries

Create and mutate `MapControl`, `TileLayer`, `MapElementsLayer`, attached collections,
and XAML `IconElement` instances on the map's UI thread. Built-in map-element properties
may be updated off-thread, but do not create or modify their XAML icon visuals there.

For large updates, use `MapElementCollection.AddRange` and `RemoveRange` rather than
repeated single-item changes. Do not retain or reuse an icon visual after adding it to a
separate XAML visual tree.

## Additional Documentation

More documentation can be found in https://github.com/dotMorten/WinUIEx.Maps/blob/main/docs/README.md

Full API reference is available in ..\lib\net10.0-windows10.0.19041\WinUIEx.Maps.xml