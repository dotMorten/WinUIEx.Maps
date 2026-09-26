# WinUIEx.Maps

WinUIEx.Maps is a WinUI 3 map control with Azure Maps raster and vector
basemaps, custom raster tile layers, map elements, touch and pointer navigation,
and Direct3D rendering.

<img width="951" height="626" alt="Image" src="https://github.com/user-attachments/assets/0b91ddc2-6474-4819-a73a-946a1f50e134" />

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

- [Documentation Overview](https://github.com/dotMorten/WinUIEx.Maps/blob/main/docs/README.md)
- [Getting started](https://github.com/dotMorten/WinUIEx.Maps/blob/main/docs/getting-started.md)
- [Map elements and interaction](https://github.com/dotMorten/WinUIEx.Maps/blob/main/docs/map-elements-and-interaction.md)
- [Azure traffic and weather](https://github.com/dotMorten/WinUIEx.Maps/blob/main/docs/azure-traffic-and-weather.md)
- [Custom raster tiles](https://github.com/dotMorten/WinUIEx.Maps/blob/main/docs/custom-raster-tiles.md)
- [Custom vector tiles](https://github.com/dotMorten/WinUIEx.Maps/blob/main/docs/custom-vector-tiles.md)
- [Configuration and accessibility](https://github.com/dotMorten/WinUIEx.Maps/blob/main/docs/configuration-and-accessibility.md)

## Licensing

WinUIEx.Maps uses a dual source-available license:

- Noncommercial use is free.
- All commercial use requires an active GitHub Sponsorship of at least
  USD $10 per month at
  [github.com/sponsors/dotMorten](https://github.com/sponsors/dotMorten).

Using the package constitutes acceptance of the terms in
[LICENSE.md](LICENSE.md).
