# Configuration and accessibility

WinUIEx.Maps combines WinUI dependency properties with Windows accessibility preferences.
This guide covers ordinary configuration first, then explains what the control adapts
automatically and what remains the host application's responsibility.

## 1. Configure the camera and base map

```xml
<maps:MapControl
    x:Name="Map"
    AutomationProperties.Name="Service area map"
    AutomationProperties.HelpText="Use arrow keys to pan and plus or minus to zoom."
    Center="{x:Bind ViewModel.Center, Mode=TwoWay}"
    Heading="{x:Bind ViewModel.Heading, Mode=TwoWay}"
    IsTextScaleFactorEnabled="True"
    MapStyle="Road"
    Pitch="{x:Bind ViewModel.Pitch, Mode=TwoWay}"
    ZoomLevel="{x:Bind ViewModel.ZoomLevel, Mode=TwoWay}" />
```

The camera properties are UI-thread dependency properties. User navigation updates them,
so two-way bindings remain synchronized.

Use `TrySetViewAsync` for an awaitable camera request and `TrySetViewBoundsAsync` to fit a
geographic area. See [Getting started](getting-started.md).

## 2. Configure layers

```csharp
Map.Layers.Add(baseOverlay);
Map.Layers.Add(labels);
Map.Layers.Add(interactiveElements);

labels.Opacity = 0.75;
interactiveElements.IsVisible = true;
```

The first public layer is bottom-most and the last is top-most. The optional Azure base map
is internal and remains below the public collection.

Every layer supports:

- `IsVisible`
- `Opacity`
- `Attribution`
- `AttributionLink`

Tile layers add source URLs, headers, bounds, source/display zoom ranges, tile size, row
scheme, subdomains, and fade duration.

## 3. Localize Azure map content

Set the map's inherited `Language` property to an IETF language tag:

```xml
<maps:MapControl
    Language="fr"
    MapStyle="Road" />
```

Or change it at runtime:

```csharp
Map.Language = "ja";
```

When `Language` is explicitly set, Azure raster and vector requests use it. Changing the
language replaces the hidden Azure acquisition session so tiles from different languages
are not mixed. When the property is not explicitly set, the language parameter is omitted
and Azure chooses its default and fallback behavior.

Custom tile providers do not automatically receive this language. Select their localized
style or URL according to that provider's contract.

## 4. Respect Windows text scaling

The control monitors `UISettings.TextScaleFactor`. When `IsTextScaleFactorEnabled` is
`true`, Azure and custom vector glyph labels use the effective Windows text scale. XAML
content such as attribution and authentication messages follows normal WinUI text scaling.

```xml
<maps:MapControl IsTextScaleFactorEnabled="True" />
```

Set it to `False` only when the application has a specific, tested reason to opt out.
Larger labels can change collision placement and the amount of visible map text, so test
the application at Windows text sizes up to 200%.

Application-authored raster tiles and icon images can contain fixed-size text that the
control cannot identify or resize. Provide alternatives and avoid baking essential text
into imagery.

## 5. Respect reduced-motion preferences

The control monitors Windows' **Show animations** preference. When animations are disabled,
it suppresses:

- Programmatic camera interpolation, including requests that specify an animated
  `MapAnimationKind`.
- Touch pan inertia.
- Focus-state transitions.
- Raster and vector tile fade transitions.

Direct touch pan, pinch/stretch, and rotation always follow the fingers immediately rather
than waiting for an animation.

No application setting is required. Do not add a separate animation around the map that
reintroduces motion after the user disabled it.

## 6. Configure themes and map contrast

WinUI theme changes invalidate shared XAML map-icon rasters so theme-resource colors can be
rendered again. Use theme resources in custom `IconElement` visuals where possible.

Map imagery does not automatically switch between Azure map styles when Windows enters
high-contrast mode. The application should choose an appropriate style for its design and
tested contrast behavior:

```csharp
Map.MapStyle = useDarkHighContrast
    ? MapStyle.HighContrastDark
    : MapStyle.HighContrastLight;
```

Other useful backgrounds include `GrayscaleLight`, `GrayscaleDark`, and `Night`.
Application overlays must remain distinguishable against every style they can appear over.
Do not communicate state through color alone.

## 7. Keyboard and focus behavior

The map is one tab stop with a visible WinUI focus state. Tab continues to the next control;
the map does not create an internal keyboard trap.

Built-in shortcuts include:

| Shortcut | Action |
| --- | --- |
| Arrow keys | Pan. |
| Plus / minus | Zoom. |
| Shift+Left / Shift+Right | Rotate. |
| Shift+Up / Shift+Down | Change pitch. |
| Escape | Restore keyboard focus to the map. |
| Ctrl+Alt+D or Ctrl+Shift+D | Toggle simplified and detailed viewport descriptions. |

Holding navigation keys produces continuous movement. A quick press uses the discrete
amount documented in [Getting started](getting-started.md).

Application-authored map-element interactions need equivalent keyboard-operable UI because
individual lightweight elements are not currently separate tab stops.

## 8. UI Automation and screen readers

`MapControlAutomationPeer` exposes:

- The control class name and application-supplied automation name/help text.
- Scroll, Transform, and Transform2 provider patterns.
- Current camera state and immediate pan, rotate, and zoom operations.
- A simplified or detailed description of the displayed viewport.
- Polite live-region updates after meaningful map movement settles.

Set a specific name that describes the map's purpose:

```xml
<maps:MapControl
    AutomationProperties.Name="Store locations"
    AutomationProperties.HelpText="Use arrow keys to move around the map. Results are also listed below." />
```

Azure vector styles can contribute bounded visible-feature text to the viewport
description. `MapStyle.Blank` loads no Azure vector data, so it contributes no Azure map
description. Custom tiles and application-authored map elements require application-owned
alternatives.

Attribution is exposed as accessible content, and attribution hyperlinks use their visible
text as their automation name. A missing Azure token is shown through an assertive,
accessible `InfoBar`.

## 9. Provide alternatives for custom content

The control cannot infer the meaning of arbitrary imagery or application data. For
essential custom content:

- Provide a synchronized list, search result, details panel, or table.
- Give interactive controls descriptive automation names.
- Make every action available by keyboard.
- Do not rely only on hover, color, shape, or map position.
- Announce important selection changes through ordinary accessible WinUI UI.
- Preserve provider attribution and explain data limitations when relevant.

`MapIcon`, `MapPolyline`, and `MapPolygon` are lightweight rendering objects and do not
currently create individual UI Automation children. Their pointer and tap events are an
application interaction surface, not a complete accessibility model.

## 10. Test the complete experience

Test the host application, not only the control:

- Keyboard-only navigation and actions.
- Narrator or another supported screen reader.
- Windows text scaling at representative values through 200%.
- Show animations disabled.
- Light, dark, and high-contrast themes.
- Narrow and resized windows.
- Every custom map style, icon, overlay, and attribution state.
- Missing or invalid credentials.

The repository's [WCAG 2.2 accessibility assessment](../WCAG-2.2.md) records current
engineering evidence and known gaps. It is not a formal conformance claim, certification,
ACR, or VPAT.
