# WinUIEx.Maps

WinUIEx.Maps is a WinUI 3 map control with Azure Maps raster and vector
basemaps, custom raster tile layers, map elements, touch and pointer navigation,
and Direct3D rendering.

## Installation

Install the `WinUIEx.Maps` NuGet package, then add a map control:

```xml
<maps:MapControl
    MapServiceToken="your-azure-maps-token"
    MapStyle="Road" />
```

Azure Maps styles require an Azure Maps token. `MapStyle.Blank` can be used with
custom HTTP(S) tile layers without an Azure token.

## Documentation

- [Documentation learning path](https://github.com/dotMorten/WinUIEx.Maps/blob/main/docs/README.md)
- [Getting started](https://github.com/dotMorten/WinUIEx.Maps/blob/main/docs/getting-started.md)
- [Map elements and interaction](https://github.com/dotMorten/WinUIEx.Maps/blob/main/docs/map-elements-and-interaction.md)
- [Custom raster tiles](https://github.com/dotMorten/WinUIEx.Maps/blob/main/docs/custom-raster-tiles.md)
- [Custom vector tiles](https://github.com/dotMorten/WinUIEx.Maps/blob/main/docs/custom-vector-tiles.md)
- [Configuration and accessibility](https://github.com/dotMorten/WinUIEx.Maps/blob/main/docs/configuration-and-accessibility.md)

## Licensing

WinUIEx.Maps uses a dual source-available license:

- Noncommercial use is free.
- All commercial use requires an active GitHub Sponsorship of at least
  **USD $10 per month** at
  [github.com/sponsors/dotMorten](https://github.com/sponsors/dotMorten).

Using the package constitutes acceptance of the terms in
[LICENSE.md](LICENSE.md). This is not an MIT or OSI-approved open-source
license. Third-party licenses are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
