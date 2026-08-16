# Map elements and interaction

`MapElementsLayer` displays lightweight `MapIcon`, `MapPolyline`, and `MapPolygon`
instances. This guide starts with one icon and builds toward hover, tap, and context-menu
behavior.

## 1. Add an element layer

```csharp
using WinUIEx.Maps;

private readonly MapElementsLayer _elements = new();

public MainPage()
{
    InitializeComponent();
    Map.Layers.Add(_elements);
}
```

Public layers render from first to last, so the first layer is bottom-most. The optional
Azure base map remains below every public layer.

## 2. Add an icon

`MapIcon` rasterizes an unparented XAML `IconElement` and anchors it at a geographic
location:

```csharp
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Devices.Geolocation;

FontIcon pinVisual = new()
{
    Glyph = "\uE707",
    FontSize = 28,
    Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red),
};

MapIcon pin = new(
    pinVisual,
    new Geopoint(new BasicGeoposition
    {
        Longitude = -122.3352,
        Latitude = 47.6080,
    }))
{
    NormalizedAnchorPoint = new Windows.Foundation.Point(0.5, 1),
};

_elements.MapElements.Add(pin);
```

The normalized anchor `(0.5, 1)` places the center of the icon's bottom edge on the
location. The default `(0.5, 0.5)` centers the icon.

Reuse one unparented `IconElement` instance across icons that look the same. The control
then shares one raster and GPU texture:

```csharp
_elements.MapElements.Add(new MapIcon(pinVisual, firstLocation));
_elements.MapElements.Add(new MapIcon(pinVisual, secondLocation));
```

Do not add the shared icon visual to another XAML visual tree.

## 3. Add lines and polygons

Create a path from geographic positions:

```csharp
static Geopath CreatePath(
    params (double Longitude, double Latitude)[] positions) =>
    new(positions.Select(position => new BasicGeoposition
    {
        Longitude = position.Longitude,
        Latitude = position.Latitude,
    }));
```

Add a line:

```csharp
MapPolyline route = new()
{
    Path = CreatePath(
        (-122.3480, 47.6200),
        (-122.3370, 47.6150),
        (-122.3260, 47.6180)),
    StrokeColor = Microsoft.UI.Colors.OrangeRed,
    StrokeThickness = 5,
};
_elements.MapElements.Add(route);
```

Line joins are rounded automatically. Set `StrokeDashed = true` for the built-in
deterministic screen-space dash pattern.

Add a polygon:

```csharp
MapPolygon area = new()
{
    Path = CreatePath(
        (-122.3490, 47.6040),
        (-122.3390, 47.5980),
        (-122.3250, 47.6000),
        (-122.3220, 47.6090)),
    FillColor = Windows.UI.Color.FromArgb(72, 138, 43, 226),
    StrokeColor = Microsoft.UI.Colors.MediumPurple,
    StrokeThickness = 4,
};
_elements.MapElements.Add(area);
```

For multiple contours and holes, use `Paths`. Contours use the even-odd fill rule:

```csharp
MapPolygon region = new()
{
    FillColor = Windows.UI.Color.FromArgb(80, 0, 120, 215),
    StrokeColor = Microsoft.UI.Colors.DodgerBlue,
    StrokeThickness = 3,
};
region.Paths.Add(outerBoundary);
region.Paths.Add(holeBoundary);
```

Setting a non-null `Path` clears `Paths`. Adding, replacing, or inserting an item in
`Paths` clears `Path`.

## 4. Control visibility, input, and ordering

Every element has:

- `IsVisible`: includes or removes it from rendering.
- `IsEnabled`: includes or removes it from hit testing while leaving it visible.
- `ZIndex`: orders elements within the same layer.

Larger `ZIndex` values render and hit-test above smaller values. Equal values preserve
collection order. `ZIndex` never reorders separate layers.

The layer itself also has `IsVisible` and `Opacity`.

## 5. Add a hover effect

Subscribe to events on the `MapElementsLayer`. Events report only the topmost visible,
enabled element under the pointer:

```csharp
private readonly Dictionary<MapElement, Windows.UI.Color> _originalColors = new();

private void ConfigureInteraction()
{
    _elements.PointerEntered += Elements_PointerEntered;
    _elements.PointerExited += Elements_PointerExited;
}

private void Elements_PointerEntered(
    object? sender,
    MapElementPointerEventArgs e)
{
    if (TryGetStrokeColor(e.MapElement, out Windows.UI.Color color))
    {
        _originalColors.TryAdd(e.MapElement, color);
        SetStrokeColor(e.MapElement, Microsoft.UI.Colors.Cyan);
        e.MapElement.ZIndex = int.MaxValue;
    }
}

private void Elements_PointerExited(
    object? sender,
    MapElementPointerEventArgs e)
{
    if (_originalColors.Remove(e.MapElement, out Windows.UI.Color color))
    {
        SetStrokeColor(e.MapElement, color);
        e.MapElement.ZIndex = 0;
    }
}

private static bool TryGetStrokeColor(
    MapElement element,
    out Windows.UI.Color color)
{
    switch (element)
    {
        case MapPolygon polygon:
            color = polygon.StrokeColor;
            return true;
        case MapPolyline polyline:
            color = polyline.StrokeColor;
            return true;
        default:
            color = default;
            return false;
    }
}

private static void SetStrokeColor(
    MapElement element,
    Windows.UI.Color color)
{
    if (element is MapPolygon polygon)
    {
        polygon.StrokeColor = color;
    }
    else if (element is MapPolyline polyline)
    {
        polyline.StrokeColor = color;
    }
}
```

For icons, replace `MapIcon.IconElement` with a larger or differently colored shared
visual on entry, then restore the original on exit.

## 6. Inspect an element when it is tapped

```csharp
private void ConfigureTaps()
{
    _elements.Tapped += Elements_Tapped;
}

private void Elements_Tapped(object? sender, MapElementTappedEventArgs e)
{
    BasicGeoposition position = e.Location.Position;
    Details.Text =
        $"{e.MapElement.GetType().Name}: " +
        $"{position.Latitude:F5}, {position.Longitude:F5}";

    e.Handled = true;
}
```

`Location` is the geographic position of the tap. `MapElement` is the topmost hit element.
Set `Handled` when the application has completed the gesture so a parent handler does not
also act on it.

The layer also exposes `PointerMoved`, `PointerPressed`, `PointerReleased`, and
`RightTapped`. Pointer event arguments retain the underlying WinUI event, pointer,
modifiers, and current/intermediate pointer points.

## 7. Show a context action

```csharp
private void Elements_RightTapped(
    object? sender,
    MapElementRightTappedEventArgs e)
{
    if (e.MapElement is not MapIcon icon)
    {
        return;
    }

    MenuFlyoutItem remove = new() { Text = "Remove pin" };
    remove.Click += (_, _) => _elements.MapElements.Remove(icon);

    MenuFlyout menu = new();
    menu.Items.Add(remove);
    menu.ShowAt(Map, new FlyoutShowOptions
    {
        Position = e.GetPosition(Map),
    });
    e.Handled = true;
}
```

Use a confirmation dialog before destructive operations when appropriate.

## 8. Add an element by tapping the map

The map's ordinary WinUI `Tapped` event can create content when no element handled the tap:

```csharp
private void Map_Tapped(object sender, TappedRoutedEventArgs e)
{
    if (Map.TryGetLocationFromOffset(e.GetPosition(Map), out Geopoint location))
    {
        _elements.MapElements.Add(new MapIcon(pinVisual, location));
        e.Handled = true;
    }
}
```

## 9. Scale to larger collections

- Use `MapElementCollection.AddRange` and `RemoveRange` for bulk changes.
- Reuse icon visuals to share raster and GPU resources.
- Avoid repeatedly replacing unchanged properties.
- Mutate an attached `Layers` or `MapElements` collection only on the map's UI thread.
- Built-in element properties publish immutable snapshots and may be changed from a worker
  thread. Creating or changing a XAML `IconElement` remains UI-thread-only.
- Subscribe only to events you use. Map-element hit testing is disabled when no attached
  layer has a relevant subscriber.

## Accessibility responsibility

The map root is keyboard accessible, but lightweight application-authored map elements do
not currently become individual UI Automation children. If an element conveys information
or performs an action, provide an equivalent accessible list, details panel, search result,
or other keyboard-operable UI. Do not rely on color or pointer hover alone.

See the complete sample in
[`MapElementsPage.xaml.cs`](../MapSample/Samples/Interaction/MapElementsPage.xaml.cs).
