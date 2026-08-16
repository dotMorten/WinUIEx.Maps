# Vector tile fixtures

These immutable Mapbox Vector Tile payloads were captured from the Azure Maps
`microsoft.base` tileset on 2026-08-30 with API version `2024-04-01`.

| File | Location | Zoom | X | Y | Bytes |
|---|---|---:|---:|---:|---:|
| `new-york-z10.pbf` | New York City | 10 | 301 | 385 | 15,200 |
| `seattle-z12.pbf` | Seattle | 12 | 656 | 1,430 | 14,286 |
| `new-york-z14.pbf` | New York City | 14 | 4,823 | 6,160 | 17,920 |
| `tokyo-z16.pbf` | Tokyo | 16 | 58,198 | 25,804 | 14,266 |

The benchmark embeds these files into its assembly and performs no network access.
`Download-AzureFixtures.ps1` is an explicit maintenance tool that replaces them using the
Azure Maps key configured in the test project's user secrets.
