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
    MapControlTestHost.LoadMapControlAsync(
        MapControlTestUtilities.InitialCenter,
        MapControlTestUtilities.InitialZoomLevel,
        async map =>
        {
            await SetupMapAsync(map);
            UiInputInjector input =
                UiInputInjector.ForElement(MapControlTestHost.Window, map);

            input.Mouse.Wheel(120);

            await WaitForAsync(() => map.ZoomLevel == 6);
        });
```

Use the center/zoom overload whenever the initial camera is test setup rather than behavior
under test. It applies `MapStyle.Blank`, `Center`, and `ZoomLevel` before the control is
attached, so the first rendered camera starts at the requested view instead of animating
from defaults. Tests that intentionally verify programmatic camera animation should keep
setting those properties after `Loaded`.

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

For map pixel assertions, use the internal asynchronous renderer readback:

```csharp
using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
MapRenderFrame frame =
    await map.CaptureRenderedFrameAsync(timeout.Token);
```

The readback waits for the current raster/vector acquisition wave, pending texture
uploads, and vector geometry preparation, then copies the completed D3D back buffer to an
immutable top-down BGRA8 frame. It returns only the map surface, independent of window
position, title-bar size, DPI, foreground activation, or desktop occlusion. Save it when a
PNG artifact is useful:

```csharp
await frame.SavePngAsync(path);
```

Continue to capture the whole host window when the test needs the actual presented window
or input diagnostics:

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

## Tailored vector-tile rendering

Use `MapboxVectorTileBuilder` and `TestVectorTileSource` when a vector feature needs a
precise visual assertion. The builder writes test-only Mapbox Vector Tile protobuf at extent
4096 and supports point, line, polygon, and typed feature properties:

```csharp
TileId tileId = new(4, 8, 8);
byte[] tile = new MapboxVectorTileBuilder()
    .AddPoint(
        "markers",
        2048,
        2048,
        new Dictionary<string, object> { ["label"] = "7" })
    .Build();

TestVectorTileSource source = TestVectorTileSource.Create(
    tileId,
    tile,
    styleJson,
    spriteJson,
    spriteBgraPixels,
    spriteWidth,
    spriteHeight);
source.AddGlyphs("TestFont", TestGlyph.Solid('7'));

map.MapStyle = MapStyle.Blank;
map.Center = new Geopoint(source.TileCenter);
map.ZoomLevel = tileId.Zoom;
map.Layers.Add(new TestVectorTileLayer(source));
```

The internal layer still uses the production scheduler, MVT decoder, style resolver,
texture preparation/upload, collision logic, and renderer. It supplies only its fixed test
tile, so neighboring wrapped tiles cannot duplicate the feature. `TestGlyph.Solid` creates
a deterministic rectangular SDF glyph for geometry assertions rather than typographic
fidelity.

Use `RenderingEventListener` when the semantic result includes intermediate render frames,
such as synchronized label fading. The listener copies only requested events and enables
Verbose rendering events, so a test can assert that `VectorLabelFadeSummary` first reports
active labels/glyphs and later reports zero after `CaptureRenderedFrameAsync` reaches the
fully opaque frame.

## Tailored raster-tile rendering

Use `TestRasterTileSource` and `TestRasterTileLayer` to exercise the production raster
scheduler, GPU upload worker, cache, fade handling, and draw path without HTTP or image
decoding:

```csharp
TileId tileId = new(5, 16, 16);
TestRasterTileSource source = new(
    tileId.Zoom,
    new Dictionary<TileId, TestRasterTile>
    {
        [tileId] = TestRasterTileSource.Solid(256, 255, 0, 0),
    });

map.MapStyle = MapStyle.Blank;
map.Center = new Geopoint(source.GetTileCenter(tileId));
map.ZoomLevel = tileId.Zoom;
map.Layers.Add(new TestRasterTileLayer(source));

MapRenderFrame frame = await map.CaptureRenderedFrameAsync(cancellationToken);
ConnectedComponent tile = Assert.ContainsSingle(
    ConnectedComponentAnalyzer.Find(
        frame,
        ConnectedComponentAnalyzer.Near(255, 0, 0, tolerance: 4),
        minimumPixelCount: 40_000));
```

The source accepts exact BGRA buffers and dimensions, including intentionally malformed
buffers for upload-validation tests. Use distinct solid colors for adjacent-tile placement,
replace a `TestRasterTileLayer` source to test generations, and use large pixel dimensions
for cache-pressure tests; texture byte size follows the supplied pixel dimensions while
screen geometry still follows the tile's zoom. `TestHybridRasterTileLayer` delivers the
same manufactured raster as the background of an empty vector payload when the hybrid
commit path needs coverage.

For hybrid vector-cache pressure, pass `hybridVectorByteSize` to
`TestRasterTileSource`. The test source creates an empty, non-rendering vector payload with
that accounted size while retaining the colored raster background for visual assertions.
Navigate among separated tiles and return to the first tile to prove least-recently-used
eviction through its request count and rendered color.

Coverage milestone tests can listen for `RasterCoverageMilestone` and assert the
`FirstTile`, `FullCoverage`, and `OpaqueCoverage` payload values after capturing a complete
frame. Keep the listener active for the entire control lifetime.

After changing the camera in cache or fallback tests, poll renderer readback for the
expected color rather than sleeping. Acquisition, camera animation, GPU commit, and cache
trimming can complete in different frames, especially under coverage instrumentation.

Use `ConnectedComponentAnalyzer.Find` to locate rendered regions by color. Filters receive
red, green, blue, and alpha values even though the underlying frame is BGRA:

```csharp
ConnectedComponent shield = Assert.ContainsSingle(
    ConnectedComponentAnalyzer.Find(
        frame,
        ConnectedComponentAnalyzer.Near(
            255, 0, 0, tolerance: 8, minimumAlpha: 240),
        minimumPixelCount: 100));
ConnectedComponent text = ConnectedComponentAnalyzer.Find(
        frame,
        ConnectedComponentAnalyzer.Near(
            0, 0, 0, tolerance: 24, minimumAlpha: 240),
        minimumPixelCount: 20)
    .Single(component => shield.Bounds.Contains(component.Bounds));

Assert.AreEqual(frame.Width / 2d, shield.Bounds.CenterX, 3);
Assert.AreEqual(frame.Height / 2d, shield.Bounds.CenterY, 3);
Assert.IsTrue(shield.Bounds.Contains(text.Bounds));
```

The analyzer uses 8-connectivity, so diagonally touching matching pixels belong to the same
component. Set a minimum pixel count to reject antialiasing noise, use a small tolerance for
shader/color rounding, and prefer relative containment and center assertions over fragile
pixel-perfect snapshots.

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
