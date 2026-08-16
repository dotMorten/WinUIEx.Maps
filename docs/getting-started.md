# Getting started

This guide starts with the smallest Azure-backed map, then adds input and programmatic
camera navigation.

## 1. Install the package

Add the NuGet package to a WinUI 3 project:

```powershell
dotnet add package WinUIEx.Maps --prerelease
```

The package currently uses preview versions. Remove `--prerelease` after a stable package
is available.

## 2. Declare a map in XAML

Add the namespace and control:

```xml
<Page
    x:Class="MyApp.MainPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:maps="using:WinUIEx.Maps">

    <maps:MapControl
        x:Name="Map"
        AutomationProperties.Name="Map"
        MapStyle="Road" />
</Page>
```

`MapStyle.Road` is the default and requires an Azure Maps key. The map displays an
accessible missing-token message until a key is configured.

## 3. Get an Azure Maps key

1. Create or select an Azure Maps account in the
   [Azure portal](https://portal.azure.com/).
1. Open the account's **Authentication** page.
1. Copy the Primary Key or Secondary Key.

See Microsoft's guides for
[managing an Azure Maps account](https://learn.microsoft.com/azure/azure-maps/how-to-manage-account-keys)
and [Azure Maps authentication](https://learn.microsoft.com/azure/azure-maps/how-to-manage-authentication).

Do not commit the key. Load it from secure configuration, a development secret store, or
an environment variable:

```csharp
using WinUIEx.Maps;

string token =
    Environment.GetEnvironmentVariable("AZURE_MAPS_SUBSCRIPTION_KEY")
    ?? throw new InvalidOperationException(
        "Set AZURE_MAPS_SUBSCRIPTION_KEY before starting the app.");

Map.MapServiceToken = token;
Map.MapStyle = MapStyle.Road;
```

`MapServiceToken` currently accepts an Azure Maps Primary Key or Secondary Key. Set
`MapStyle="Blank"` when you only need custom layers; a blank map performs no Azure tile or
attribution requests and requires no Azure token.

## 4. Choose a map style

Common choices include:

| Style | Use |
| --- | --- |
| `Road` | Colorful Azure vector road map. |
| `GrayscaleLight` / `GrayscaleDark` | Neutral backgrounds for application overlays. |
| `Night` | Dark, low-light road map. |
| `HighContrastLight` / `HighContrastDark` | Explicit high-contrast Azure styles. |
| `Satellite` | Raster satellite imagery without road labels. |
| `SatelliteWithRoads` | Satellite imagery with vector roads and labels. |
| `RoadShadedRelief` | Vector road map with relief. |
| `Blank` | No Azure data; display only public custom layers. |

Styles with `Raster` in their names select legacy raster variants. All styles except
`Blank` require a valid Azure Maps key.

```csharp
Map.MapStyle = MapStyle.GrayscaleDark;
```

## 5. Navigate with keyboard, mouse, and touch

The map is a keyboard-focusable control and a single tab stop.

| Input | Action |
| --- | --- |
| Arrow keys | Pan by 100 logical pixels; hold for continuous movement. |
| `+`, `=`, or numeric keypad Add | Zoom in one level; hold for continuous zoom. |
| `-`, `_`, or numeric keypad Subtract | Zoom out one level; hold for continuous zoom. |
| Shift+Left / Shift+Right | Rotate 15 degrees; hold for continuous rotation. |
| Shift+Up / Shift+Down | Increase or decrease pitch by 10 degrees. |
| Escape | Return keyboard focus to the map. |
| Left mouse drag | Pan. |
| Mouse wheel | Zoom around the pointer position. |
| Double-click or double-tap | Zoom in around the input position. |
| One-finger drag | Pan directly; release with velocity to use inertia. |
| Pinch or stretch | Zoom directly around the gesture center. |
| Two-finger rotation | Rotate after a small activation threshold; headings near north snap to north on release. |

Windows' **Show animations** preference controls nonessential motion. When animations are
disabled, camera interpolation and touch inertia are suppressed.

## 6. Set the camera with properties

`Center`, `ZoomLevel`, `Heading`, and `Pitch` are dependency properties and can be set in
code or bound in XAML:

```csharp
using Windows.Devices.Geolocation;

Map.Center = new Geopoint(new BasicGeoposition
{
    Longitude = -122.33,
    Latitude = 47.61,
});
Map.ZoomLevel = 12;
Map.Heading = 20;
Map.Pitch = 45;
```

- Longitude wraps around the world.
- Latitude is clamped to the Web Mercator range.
- Zoom is normalized to the supported range from 0 through 22.
- Heading is normalized to 0 inclusive through 360 exclusive.
- Pitch is clamped from 0 through 60 degrees.

For two-way binding:

```xml
<maps:MapControl
    Center="{x:Bind ViewModel.Center, Mode=TwoWay}"
    Heading="{x:Bind ViewModel.Heading, Mode=TwoWay}"
    Pitch="{x:Bind ViewModel.Pitch, Mode=TwoWay}"
    ZoomLevel="{x:Bind ViewModel.ZoomLevel, Mode=TwoWay}" />
```

Input updates the same properties, so a two-way-bound view model follows user navigation.

## 7. Await a camera change

Use `TrySetViewAsync` when work must wait until the requested camera is displayed:

```csharp
Geopoint seattle = new(new BasicGeoposition
{
    Longitude = -122.33,
    Latitude = 47.61,
});

bool displayed = await Map.TrySetViewAsync(
    seattle,
    zoomLevel: 13,
    heading: 0,
    desiredPitch: 35,
    animation: MapAnimationKind.Bow);
```

The result is `false` if a newer view request replaces this request before it is displayed.
Use `MapAnimationKind.None` for an immediate change. `Default`, `Linear`, and `Bow` request
animated changes, but the Windows animation preference can still make them immediate.

To fit an area:

```csharp
using Microsoft.UI.Xaml;

bool displayed = await Map.TrySetViewBoundsAsync(
    bounds,
    new Thickness(48),
    MapAnimationKind.Default);
```

The margin is applied inside the map viewport. The calculation accounts for the current
heading and pitch.

## 8. Convert a pointer position to a location

`TryGetLocationFromOffset` uses the camera currently displayed by the render thread,
including an in-progress animation:

```csharp
private void Map_Tapped(object sender, TappedRoutedEventArgs e)
{
    Point position = e.GetPosition(Map);
    if (Map.TryGetLocationFromOffset(position, out Geopoint location))
    {
        BasicGeoposition coordinate = location.Position;
        Status.Text = $"{coordinate.Latitude:F5}, {coordinate.Longitude:F5}";
    }
}
```

Next, use that location to create interactive content in
[Map elements and interaction](map-elements-and-interaction.md).
