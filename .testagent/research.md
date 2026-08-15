# Test Generation Research

## Project Overview
- **Path**: `E:\GitHub\dotMorten\WinUIEx.Maps`
- **Language**: C# / WinUI 3 / Direct3D 11
- **Framework**: .NET 10 (`net10.0-windows10.0.19041.0`; sample uses Windows SDK 26100)
- **Test Framework**: MSTest 4.3.3 on the self-hosted Microsoft.Testing.Platform executable
- **Project system**: SDK-style
- **Dependency format and versions**: `PackageReference`; tests use MSTest 4.3.3, Microsoft.Testing.Extensions.CodeCoverage 18.10.0, Microsoft.WindowsAppSDK 2.4.0, Windows SDK BuildTools 10.0.28000.2526, and CsWin32 0.3.321. There is no mocking library.
- **New-file registration**: implicit SDK `Compile` glob; a new `*.cs` file needs no project edit.
- **Local SDK**: 10.0.301. The repository has no `global.json`.
- **Required local guidance**: `.github/skills/winui-control-ui-tests/SKILL.md` was read and is authoritative for the live UI architecture.
- **Testing-extension note**: the coordinator reported `code-testing-extensions` unavailable. Commands and conventions below are therefore inferred from the current test project, its UI-test skill, and `.github/workflows/CIBuild.yml`.
- **Working-tree constraint**: the workspace was already dirty, including rendering and UI-test work. Treat all current files as authoritative; do not restore, revert, reset, clean, delete, or commit anything.

## User Requirement and Checklist

> "There should be rendering tests for map elements and all their properties ensuring they render as expected"

The implementation is complete only when every box below is satisfied:

- [ ] Every renderer-supported `MapElement` subtype is exercised live: `MapIcon`, `MapPolygon`, and `MapPolyline`.
- [ ] Every public property has its expected rendering behavior exercised, including inherited `IsEnabled`, `IsVisible`, and `ZIndex`.
- [ ] Each property has a concrete behavioral/pixel assertion, not merely a no-throw, event, snapshot-state, or property-value assertion.
- [ ] Tests use the existing WinUI UI-test framework: `[TestClass]`, class-level `[DoNotParallelize]`, and `MapControlTestHost.LoadMapControlAsync`.
- [ ] Pixel evidence comes from internal renderer readback, `await map.CaptureRenderedFrameAsync(token)`, after the renderer reaches the requested state.
- [ ] Asserted frames are also saved as PNG screenshots under `TestContext.TestRunResultsDirectory`; whole-window `UiInputInjector.Screenshot` is diagnostic only and is not the primary oracle.
- [ ] Use `ConnectedComponentAnalyzer` (8-connected components) for colored regions, holes, line segments/dashes, bounds, centers, containment, and pixel counts where appropriate.
- [ ] Use layer visibility, opacity, and layer order only when needed to isolate the element assertion. Keep one visible, fully opaque `MapElementsLayer` for element-local `ZIndex`; do not turn this scope into duplicate `MapLayer` tests.
- [ ] The narrowest clean UI-test command passes.
- [ ] Relevant project and full-workspace build/test/discovery commands pass on an unlocked interactive desktop.
- [ ] `git diff --check` passes.
- [ ] Final `test-gap-analysis` gate is run against this inventory and reports no unexplained property/subtype gap.
- [ ] Final `assertion-quality` gate is run and accepts every new rendering assertion.

`IsEnabled` controls input rather than drawing, so its rendering contract is that disabling an element leaves its pixels unchanged. Exercise that contract for all three built-in subtypes. Preserve the existing live input coverage in `MapIconTests.MapElementStateControlsVisibilityInputAndLayerLocalOrder`; this suite verifies only the rendering half of the property contract. `MapElementsLayer.MapElements` and `MapLayer` properties are outside the requested element-property surface.

## Bounded Exhaustive Target Inventory

The source documentation and a repository-wide derivation search identify exactly three production subclasses. Arbitrary custom subclasses have no drawing contract and are excluded.

| Declaring type | Rendering-affecting public property | Required live assertion |
|---|---|---|
| `MapElement` | `IsEnabled` | For all three built-in subtypes, capture uniquely colored components, disable the elements, and prove their bounds and pixel counts remain unchanged. |
| `MapElement` | `IsVisible` | For **each** built-in subtype, capture a uniquely colored component while visible, set false, recapture, and prove that component is absent; optionally restore true and prove it returns. |
| `MapElement` | `ZIndex` | Put overlapping, differently colored element types in one layer; change `ZIndex` and assert the center/top pixels switch to the expected element. Equal-order collection behavior need not duplicate existing unit tests. |
| `MapIcon` | `IconElement` | Render a deterministic unparented `PathIcon`, replace it with a differently colored/shaped `PathIcon`, and assert old pixels disappear and the replacement component has the expected color/bounds. |
| `MapIcon` | `Location` | Move the same icon between known geographic points and assert its component center moves to the corresponding projected viewport position while size/color stay stable. |
| `MapIcon` | `NormalizedAnchorPoint` | Use asymmetric deterministic geometry; change center anchor to a corner/bottom anchor and assert the component bounds shift by the expected logical width/height relative to the projected location. |
| `MapPolyline` | `Path` | Set/update a path with known endpoints and assert the rendered component's orientation, bounds, and projected placement change accordingly. |
| `MapPolyline` | `StrokeColor` | Render an opaque distinctive color and assert a single line component of that color, with no stale old-color component after mutation. |
| `MapPolyline` | `StrokeDashed` | Compare solid and dashed captures; assert gaps/multiple separated 8-connected components along the same path while preserving overall endpoints. |
| `MapPolyline` | `StrokeThickness` | Compare thin and thick captures and assert the perpendicular component dimension/pixel count increases by a meaningful tolerance. |
| `MapPolygon` | `Path` | Render/update one known contour and assert fill bounds, center, and projected placement. |
| `MapPolygon` | `Paths` | Render an outer contour plus inner contour and assert one outer fill region with an unfilled center/hole; mutate the list and prove the frame changes. |
| `MapPolygon` | `FillColor` | Assert the polygon interior is the requested opaque color and changes to a second color without stale pixels. |
| `MapPolygon` | `StrokeColor` | Use a contrasting fill and stroke; assert the stroke-colored component surrounds/aligns with the fill bounds. |
| `MapPolygon` | `StrokeDashed` | Assert separated stroke components/gaps around a known contour rather than only inspecting generated segments. |
| `MapPolygon` | `StrokeThickness` | Compare captures and assert outward/inward border thickness or stroke pixel count increases while contour placement remains stable. |

This is 13 subtype-declared property cells plus three inherited properties. Because `IsEnabled`, `IsVisible`, and `ZIndex` must cover all three draw paths, the effective matrix has 22 subtype/property cells.

## Dependency Graph
- **Leaf types (target graph)**: `MapElement` (base state: `IsVisible`, `ZIndex`; `IsEnabled` is input-only).
- **Mid-layer target types**: `MapIcon`, `MapPolygon`, and `MapPolyline`, each depending on `MapElement` and framework geography/visual types. `MapPolygon`/`MapPolyline` also publish immutable `MapGeometryData`.
- **Hosting/publication dependencies**: `MapElementsLayer` owns the elements; `MapControl.RebuildMapElementSnapshots` filters `IsVisible`, sorts by `ZIndex` then collection position, and publishes `MapIconSnapshot`/`MapGeometrySnapshot`.
- **Rendering dependencies**: `MapIconService` rasterizes `IconElement` and publishes dimensions/location/anchor; `MapRenderer.Icons.cs` uploads and draws icon textures; `MapRenderer.Geometry.cs` draws polygon fills and polygon/polyline strokes; `MapGeometryOperations` projects/tessellates geometry and generates screen-space dashes.
- **Observation dependencies**: `MapControl.CaptureRenderedFrameAsync` waits for current acquisition/upload work and calls renderer back-buffer readback. `ConnectedComponentAnalyzer` consumes top-down BGRA8 frames with 8-connectivity. `MapRenderFrameTestUtilities.SavePngAsync` writes PNG artifacts.
- **External-only dependencies**: WinUI `IconElement`/`PathIcon`, `Geopoint`/`Geopath`, `Windows.UI.Color`, and D3D. No mock should replace these in live rendering tests.

## Build & Test Commands

Run from the repository root. UI execution requires an unlocked interactive desktop.

- **Restore when needed**: `dotnet restore .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj -p:Platform=x64`
- **Build (target test project)**: `dotnet build .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64`
- **Build (production project, CI-equivalent)**: `dotnet build .\WinUIEx.Maps\WinUIEx.Maps.csproj --no-restore -c Release`
- **Build (sample)**: `dotnet build .\MapSample\MapSample.csproj --no-restore -p:Platform=x64`
- **Build (full workspace)**: `dotnet build .\WinUIEx.Maps.slnx --no-restore -p:Platform=x64`
- **Test (scoped — narrowest clean UI fix cycle)**: `dotnet run --project .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64 -- --filter "FullyQualifiedName~WinUIEx.Maps.Tests.UITests.MapElementRenderingTests"`
- **Test (full test project/workspace)**: `dotnet test .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64`
- **Test (harness-equivalent — discovery check)**: `dotnet test .\WinUIEx.Maps.slnx --no-restore -p:Platform=x64 --list-tests`
- **Discovery acceptance**: output must list every new `MapElementRenderingTests` method/data case. Because this is the sole test project, solution discovery is full-workspace discovery.
- **Whitespace gate**: `git diff --check`
- **Lint**: no separate lint command/configuration was found; compiler warnings and `git diff --check` are the available gates.

The existing CI workflow builds Release but currently comments out execution. Its documented MTP filter form is `dotnet run ... -- --filter "FullyQualifiedName!~UITests"`, supporting the scoped filter form above. Do not launch the packaged sample executable directly.

## Scope
- **Boundary**: comprehensive live rendering behavior for the renderer-supported `MapElement` hierarchy only.
- **Production targets**:
  - `WinUIEx.Maps/MapElement.cs`
  - `WinUIEx.Maps/MapIcon.cs`
  - `WinUIEx.Maps/MapPolygon.cs`
  - `WinUIEx.Maps/MapPolyline.cs`
- **Supporting implementation read for interpretation only**:
  - `WinUIEx.Maps/MapControl.cs`
  - `WinUIEx.Maps/MapElementsLayer.cs`
  - `WinUIEx.Maps/MapLayer.cs`
  - `WinUIEx.Maps/MapIconService.cs`
  - `WinUIEx.Maps/Rendering/MapIconData.cs`
  - `WinUIEx.Maps/Rendering/MapGeometrySnapshot.cs`
  - `WinUIEx.Maps/Rendering/MapRenderer.Icons.cs`
  - `WinUIEx.Maps/Rendering/MapRenderer.Geometry.cs`
- **Expected new test location**: `WinUIEx.Maps.Tests/UITests/MapElementRenderingTests.cs`; centralizing the matrix avoids scattering shared render/readback helpers.
- **Representative existing tests (maximum two)**:
  - `WinUIEx.Maps.Tests/UITests/MapIconTests.cs`
  - `WinUIEx.Maps.Tests/UITests/RasterRenderingTests.cs`
- **Explicit exclusions**: input-only `IsEnabled`, collection validation, property argument validation/defaults, hit testing, custom unsupported subclasses, tile/vector-style rendering, camera/input behavior, and independent layer visibility/opacity/order coverage.

## Files to Test

### High Priority
| File | Classes/Functions | Testability | Estimated Coverage | Notes |
|---|---|---|---|---|
| `WinUIEx.Maps/MapPolygon.cs` | `MapPolygon`; `Path`, `Paths`, `FillColor`, `StrokeColor`, `StrokeDashed`, `StrokeThickness` | High with live D3D host | Untested live | No current UI pixel tests; largest property surface. |
| `WinUIEx.Maps/MapPolyline.cs` | `MapPolyline`; `Path`, `StrokeColor`, `StrokeDashed`, `StrokeThickness` | High with live D3D host | Untested live | Unit tests cover geometry math, not the renderer output. |
| `WinUIEx.Maps/MapIcon.cs` | `MapIcon`; `IconElement`, `Location`, `NormalizedAnchorPoint` | High with deterministic `PathIcon` | Partial live | One baseline screen capture exists; property changes lack renderer-readback pixel assertions. |
| `WinUIEx.Maps/MapElement.cs` | inherited `IsVisible`, `ZIndex` | High via all built-in subtypes | Partial live | Existing live checks are hit-test based, not pixel based. |

### Medium Priority
| File | Classes/Functions | Testability | Estimated Coverage | Notes |
|---|---|---|---|---|
| `WinUIEx.Maps/MapIconService.cs` | icon raster/snapshot publication | Medium | Indirect only | Supporting dependency; cover through `MapIcon` properties, not as a separate target. |
| `WinUIEx.Maps/Rendering/MapRenderer.Geometry.cs` | ordered fill/stroke drawing | Medium | Unit math only for requested paths | Exercise indirectly through polygon/polyline readback. |
| `WinUIEx.Maps/Rendering/MapRenderer.Icons.cs` | icon upload/culling/drawing | Medium | Partial | Exercise indirectly through icon readback. |

### Low Priority / Skip
| File | Reason |
|---|---|
| `WinUIEx.Maps/MapElementsLayer.cs` | Host dependency, not a `MapElement` subtype or element property target. |
| `WinUIEx.Maps/MapLayer.cs` | Layer behavior is outside scope; use only for isolation. |
| `MapSample/**` | Sample application is not the requested test boundary. |

## Existing Tests & Coverage Classification

Static pairing was run once with the namespace-aware Roslyn `find-untested-sources` engine. It found 64 production files, 48 test files, 46 paired production files, and 18 unpaired files.

- `MapElement.cs` → `Tests/MapElementTests.cs`, `Tests/MapLayerTests.cs`, `UITests/MapIconTests.cs`: **partial for this requirement**. Defaults/change publication and hit-test behavior exist; no readback proves `IsVisible` or `ZIndex` changes pixels.
- `MapIcon.cs` → `Tests/MapElementTests.cs`, `Tests/MapLayerTests.cs`, `UITestHelpers/MapControlTestUtilities.cs`, `UITests/MapIconTests.cs`: **partial**. `MapIcon_RendersAtCenter` only searches a host-window screenshot for any red pixel. Anchor placement is unit-tested; icon replacement, movement, anchor mutation, and element visibility/order lack internal-readback pixel assertions.
- `MapPolygon.cs` → `Tests/MapElementTests.cs`, `Tests/MapGeometryTests.cs`: **untested live / partial unit**. API surface, snapshots, tessellation, holes, dashes, hit testing, and colors are tested without a rendered frame.
- `MapPolyline.cs` → `Tests/MapElementTests.cs`, `Tests/MapGeometryTests.cs`: **untested live / partial unit**. API surface, projection, dash generation, caps, and hit testing are unit-tested without a rendered frame.

No numeric coverage percentage is claimed. The pairing result is a parse-only static heuristic: a type-name reference establishes pairing, not line, branch, mutation, assertion, or live-rendering coverage.

## Existing Test Projects
- **Project file**: `WinUIEx.Maps.Tests/WinUIEx.Maps.Tests.csproj`
- **Target source project**: `WinUIEx.Maps/WinUIEx.Maps.csproj` through `ProjectReference`
- **Relevant test files**:
  - `WinUIEx.Maps.Tests/UITests/MapIconTests.cs`
  - `WinUIEx.Maps.Tests/Tests/MapElementTests.cs`
  - `WinUIEx.Maps.Tests/Tests/MapGeometryTests.cs`
  - `WinUIEx.Maps.Tests/UITests/RasterRenderingTests.cs`
  - `WinUIEx.Maps.Tests/UITests/VectorRenderingTests.cs`
  - `WinUIEx.Maps.Tests/Tests/ConnectedComponentAnalyzerTests.cs`

There is no separate UI test project. Unit and live UI tests share the unpackaged WinExe test host.

## Testing Patterns
- Put the new UI matrix in a `[TestClass]` with class-level `[DoNotParallelize]`; never disable parallelism assembly-wide.
- Every test returns the `Task` from `MapControlTestHost.LoadMapControlAsync`. Create maps, layers, `Geopath`, brushes, and deterministic unparented `PathIcon` objects inside the UI-thread callback. Do not retain controls or XAML objects in fields.
- Configure `MapStyle.Blank`, a deterministic center/zoom, and the host's fixed 640×480 map. Avoid Azure/network dependencies.
- Prefer opaque, separated colors and resource-independent `PathIcon` geometry. Do not use font glyphs as pixel oracles.
- Capture with a bounded `CancellationTokenSource` only after the requested element state is published. For asynchronous mutations, repeatedly read back until the expected color/geometry appears rather than sleeping.
- Use `ConnectedComponentAnalyzer.Near` with small color/shader tolerances, alpha floors, and minimum pixel counts. Assert component count, bounds, center, containment, absence of stale colors, and relative size changes. Its connectivity is explicitly 8-neighbor.
- Add a helper that captures and saves the same `MapRenderFrame` to `Path.Combine(TestContext.TestRunResultsDirectory!, $"{TestContext.TestName}-{phase}.png")`; assert the file path exists when screenshot production is itself required.
- Prefer before/after mutation within one control lifetime for property tests. This verifies invalidation and prevents a constructor-only test from passing while live updates are broken.
- Avoid arbitrary delays. `CaptureRenderedFrameAsync` is the synchronization boundary for current acquisition, texture upload, vector preparation, and completed D3D back-buffer readback.

## Recommendations
1. Add the common capture/save/color-component helpers and a deterministic blank-map fixture in `UITests/MapElementRenderingTests.cs`.
2. Implement baseline live-render tests for polygon and polyline first; these establish the missing geometry draw path before property mutation cases.
3. Complete polygon `Path`/`Paths`/fill/stroke/dash/thickness cases, then the analogous polyline cases.
4. Replace screenshot-presence-level icon confidence with renderer-readback tests for `IconElement`, `Location`, and `NormalizedAnchorPoint`.
5. Finish with inherited `IsEnabled` and `IsVisible` across all three subtypes and a mixed-subtype, one-layer `ZIndex` test.
6. Run the scoped class command after each group, then solution discovery, full tests/builds, and `git diff --check`.
7. Invoke the mandatory `test-gap-analysis` skill against the 16-property inventory (22 effective subtype/property cells), then invoke `assertion-quality`. Do not finish implementation with an omitted cell, smoke-only assertion, or unexplained gate finding.

### Risks / Blockers
- Live tests require an unlocked, connected interactive Windows desktop and architecture-compatible WinUI runtime. x64 is the repository convention; use x86/ARM64 when validating those targets.
- Antialiasing and blending make exact full-image snapshots fragile. Use color tolerances and relative component geometry, while still saving PNG evidence.
- A transparent blank map can make absence tests ambiguous unless each target uses a unique opaque color and the test first proves that component was present.
- Dashes that touch diagonally merge under 8-connectivity; choose path length, thickness, and scale that leave real pixel gaps.
- `CaptureRenderedFrameAsync` is internal but available because `WinUIEx.Maps` grants `InternalsVisibleTo("WinUIEx.Maps.Tests")`.
