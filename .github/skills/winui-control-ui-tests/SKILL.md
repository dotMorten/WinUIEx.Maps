---
name: winui-control-ui-tests
description: Write live MSTest UI tests for WinUI controls using a shared host window, per-test control lifetime, native mouse, keyboard, touch injection, renderer synchronization, and screenshots.
---

# WinUI control UI tests

Use this skill when adding or changing tests that must load a real WinUI control, inject
desktop input, observe focus or camera behavior, wait for rendered state, or capture the
control's pixels. These are interactive integration tests, not isolated unit tests.

The repository implementation is in `WinUIEx.Maps.Tests` and UI specific tests in the `UITests` subfolder. Preserve its unpackaged, self-hosted Microsoft.Testing.Platform executable, x86/x64/ARM64 support, shared application host window, and per-test control lifetime.

## Test architecture

The executable starts Microsoft.Testing.Platform without creating a WinUI application or
window. The first `MapControlTestHost.LoadUIAsync` call lazily starts
`TestApplication` on a dedicated STA thread, creates one shared host window, and publishes
its dispatcher through `MapControlTestHost`. Runner shutdown closes the application only
when a UI test initialized it. Because the self-hosted executable owns both the runner and
WinUI application, referenced control-library PRI resources and `Themes/Generic.xaml` load
normally. The host window does not permanently contain the control under test.
Its `TitleBar` keeps the window title and shows the current UI test method as its subtitle;
preserve the `[CallerMemberName]` flow through every `LoadUIAsync` overload.

Each test must use `MapControlTestHost.LoadMapControlAsync`:

```csharp
[TestMethod]
public Task MouseWheel_ZoomsIn() =>
    MapControlTestHost.LoadMapControlAsync(async map =>
    {
        await SetupMapAsync(map);
        UiInputInjector input =
            UiInputInjector.ForElement(MapControlTestHost.Window, map);

        input.Mouse.Wheel(120);

        await WaitForAsync(() => map.ZoomLevel == 6);
    });
```

For another WinUI control, use `LoadUIAsync`:

```csharp
await MapControlTestHost.LoadUIAsync(
    () => new Button { Content = "Test" },
    async element =>
    {
        var button = (Button)element;
        // The callback runs on the WinUI thread after Loaded.
        await ExerciseControlAsync(button);
    });
```

`LoadUIAsync` creates and attaches the element on the UI thread, waits for `Loaded`, invokes
the callback, and removes the element in a `finally` block. `LoadMapControlAsync` additionally
disposes the map after detachment. Do not use test fields or `[TestInitialize]` to retain
controls across tests.

## UI-thread rules

The creator and callback execute on the WinUI thread. Create and configure dependency
objects, brushes, `IconElement` instances, layers, and collections inside those callbacks.
Do not keep static XAML objects such as a shared `FontIcon`; they are thread-affine and may
retain a `XamlRoot` or visual parent across tests.

Async callbacks resume on the WinUI dispatcher synchronization context. Poll control
properties directly inside the callback rather than repeatedly dispatching with
`MapControlTestHost.RunAsync`.

## Input injection

Create one target after the control is loaded and arranged:

```csharp
UiInputInjector input =
    UiInputInjector.ForElement(MapControlTestHost.Window, control);
```

The input target uses its UI Automation bounding rectangle in screen coordinates. Before
injecting input, the helpers foreground the host window and verify that the target point is
inside the host root window. Tests require an unlocked interactive desktop.

Available helpers:

| Input | Examples |
|---|---|
| Mouse | `input.Mouse.Click()`, `DoubleClick()`, `await WheelAsync(120)` |
| Keyboard | `input.Keyboard.Press(VirtualKey.Left)` |
| Touch | `await input.Touch.TapAsync()`, `DoubleTapAsync()`, `SwipeAsync(start, end)`, `PinchAsync()`, `StretchAsync()` |
| Screenshot | `await input.Screenshot.SaveAsync(path)` |

Mouse and keyboard use `SendInput`. Touch uses `InitializeTouchInjection` and
`InjectTouchInput`. Inject complete down/update/up sequences and release contacts in a
`finally` block. Do not replace native injection with direct protected-method calls or
raised routed events; those approaches do not test focus routing, hit testing, or the
platform gesture recognizer.

For keyboard tests, first establish the intended focus mode, usually with a mouse click,
then wait for the expected `FocusState` before sending the key.

## Target state versus rendered state

Many control properties represent the requested target and change synchronously when input
is handled. The renderer can still be animating toward that target. A test that only waits
for `Center` or `ZoomLevel` can finish in milliseconds without exercising the animation or
showing the final frame.

For `MapControl`, use `TryGetLocationFromOffset` to observe the camera most recently
published by the render thread:

```csharp
private static bool TryGetDisplayedCenter(
    MapControl map,
    out BasicGeoposition center)
{
    var viewportCenter = new Point(
        map.ActualWidth / 2,
        map.ActualHeight / 2);
    if (map.TryGetLocationFromOffset(viewportCenter, out Geopoint location))
    {
        center = location.Position;
        return true;
    }

    center = default;
    return false;
}
```

Wait for the initial displayed camera before injecting input. Otherwise the renderer may
consume the initial and final targets together, initialize directly at the final value, and
skip interpolation. After input, assert the requested property immediately if appropriate,
then wait separately for the displayed camera to reach the expected center and zoom.

Use tolerances for projected floating-point coordinates. Assert both axes so a horizontal
pan proves the expected longitude change and unchanged latitude, and vice versa.

Direct touch pan and pinch are intentionally immediate; do not require camera animation for
those gestures. A generated touch flick may continue through inertia and must be awaited.

## Rendering and icons

`Loaded` and nonzero `ActualWidth` do not prove that D3D has rendered a frame or that an
icon raster has uploaded. A `MapIcon` passes through:

1. UI-thread XAML rasterization.
2. Renderer texture upload.
3. Snapshot publication.
4. Render-thread visibility and draw batching.

Create icon XAML on the UI thread and keep it unparented:

```csharp
var pin = new PathIcon
{
    Width = 16,
    Height = 16,
    Data = new EllipseGeometry
    {
        Center = new Point(8, 8),
        RadiusX = 8,
        RadiusY = 8,
    },
    Foreground = new SolidColorBrush(Colors.Red),
};
var layer = new MapElementsLayer();
layer.MapElements.Add(new MapIcon(pin, location));
map.Layers.Add(layer);
```

Prefer resource-independent `PathIcon` geometry when a test needs to assert pixels. A
`FontIcon` depends on font availability and glyph resolution in the VSTest-hosted XAML
environment and can rasterize as transparent while the upload and draw pipeline otherwise
works correctly.

When diagnosing a missing icon, use the `mapcontrol-etw-diagnostics` skill with provider
`WinUIEx-Maps-Rendering`, keyword mask `0x60`, and Verbose level. Correlate
`IconSnapshotPublished`, `IconRasterizationFailed`, `IconTextureUploadSummary`, and
`IconRenderBatch`. Do not add debug output or UI callbacks as a substitute.

## Screenshots

Capture the host window with:

```csharp
string path = Path.Combine(
    TestContext.TestRunResultsDirectory!,
    $"{TestContext.TestName}.png");
await input.Screenshot.SaveAsync(path);
```

The helper captures the native host-window rectangle with GDI `BitBlt`, which includes the
visible swap-chain output, then writes BGRA pixels as PNG with `BitmapEncoder`. Keep the
window foreground and unobscured. Screen capture can include another overlapping window,
so use it for interactive test diagnostics and visual assertions only on a controlled
desktop.

Capture after the displayed renderer state reaches the desired point. A screenshot taken
immediately after changing `Center` or `ZoomLevel` can legitimately show an earlier
animation frame.

## Waiting and assertions

Use bounded polling with a clear failure:

```csharp
private static async Task WaitForAsync(Func<bool> condition)
{
    DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (condition())
        {
            return;
        }

        await Task.Delay(20);
    }

    Assert.Fail("The expected UI state was not reached before the timeout.");
}
```

Wait for a meaningful observable boundary, not an arbitrary delay. Short delays inside an
injector are acceptable only for platform gesture timing, such as double-click spacing or
interpolated touch frames.

Prefer exact semantic assertions:

- Expected target value and direction.
- Unchanged perpendicular coordinate.
- Expected focus state.
- Final displayed camera after animation.
- Output screenshot existence and nonzero dimensions when capture is part of the test.

## Validation

Architecture-specific examples use x64. Select x86 or ARM64 when validating those
targets.

```powershell
dotnet run --project .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64
```

Then run:

```powershell
dotnet test .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64
dotnet build .\MapSample\MapSample.csproj --no-restore -p:Platform=x64
git diff --check
```

Apply `[DoNotParallelize]` to every UI test class because they share one window and native
foreground input. Do not apply it at assembly level: non-UI unit tests must remain eligible
for parallel execution. Do not run UI tests in a locked or disconnected desktop session,
and do not launch the packaged sample executable directly.
