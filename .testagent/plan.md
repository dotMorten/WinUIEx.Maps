# Test Implementation Plan

## Overview

Add comprehensive, deterministic live-rendering coverage to the existing MSTest/WinUI test host. All tests will live in `WinUIEx.Maps.Tests/UITests/MapElementRenderingTests.cs`, use internal renderer readback, save every asserted frame as a PNG, and analyze tolerant color regions with the existing 8-connected `ConnectedComponentAnalyzer`.

The strategy is broad for live rendering because `MapPolygon` and `MapPolyline` are untested live and `MapIcon`/`MapElement` are only partially covered. Production files are assigned once, in dependency/complexity order:

1. shared live-rendering fixture and evidence helpers;
2. `MapPolygon`;
3. `MapPolyline`;
4. `MapIcon`;
5. inherited `MapElement` rendering state and final quality gates.

No production behavior or project architecture will be changed. Preserve the dirty workspace: do not restore, revert, reset, clean, delete, or commit. Do not add another test project. All execution must use an unlocked interactive desktop and `x64`.

## Governing Requirement

> "There should be rendering tests for map elements and all their properties ensuring they render as expected"

### Requirement Checklist

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

`IsEnabled` controls input, not drawing, so test that disabling all three built-in subtypes leaves their rendered bounds and pixel counts unchanged. Preserve `MapIconTests.MapElementStateControlsVisibilityInputAndLayerLocalOrder`; do not claim hit-testing coverage as pixel coverage. `MapElementsLayer.MapElements`, `MapLayer` properties, custom subclasses, and independent layer behavior remain out of scope.

## Commands

- **Build (test project)**: `dotnet build .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64`
- **Build (production, CI-equivalent)**: `dotnet build .\WinUIEx.Maps\WinUIEx.Maps.csproj --no-restore -c Release`
- **Build (sample)**: `dotnet build .\MapSample\MapSample.csproj --no-restore -p:Platform=x64`
- **Build (full workspace)**: `dotnet build .\WinUIEx.Maps.slnx --no-restore -p:Platform=x64`
- **Test (narrow UI class)**: `dotnet run --project .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64 -- --filter "FullyQualifiedName~WinUIEx.Maps.Tests.UITests.MapElementRenderingTests"`
- **Test (full project)**: `dotnet test .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64`
- **Discovery**: `dotnet test .\WinUIEx.Maps.slnx --no-restore -p:Platform=x64 --list-tests`
- **Whitespace**: `git diff --check`
- **Lint**: No separate lint command exists; compiler warnings and `git diff --check` are the lint gates.

Do not run restore as part of implementation or validation.

## Phase Summary

| Phase | Focus | Assigned production files | Named tests |
|---|---|---:|---:|
| 1 | Shared live-rendering fixture and evidence pipeline | 0 | 0 |
| 2 | Polygon fill, contours, holes, and strokes | 1 | 6 |
| 3 | Polyline path and stroke behavior | 1 | 4 |
| 4 | Icon rasterization, movement, and anchoring | 1 | 3 |
| 5 | Inherited enabled/visibility/order matrix and completion gates | 1 | 5 |
| **Total** | **22 effective subtype/property cells** | **4** | **18** |

---

## Phase 1: Live Renderer Fixture and Evidence Helpers

### Overview

Establish the common test class and deterministic readback pipeline before adding property cases. This phase changes tests only and does not independently test a production target.

### File to Create

#### `MapElementRenderingTests.cs`
- **Test File**: `WinUIEx.Maps.Tests/UITests/MapElementRenderingTests.cs`
- **Test Class**: `MapElementRenderingTests`
- **Attributes**: `[TestClass]` and class-level `[DoNotParallelize]`
- **Context**: public `TestContext TestContext { get; set; }`

### Planned Helpers

1. A blank-map setup helper used only inside `MapControlTestHost.LoadMapControlAsync`
   - Configure `MapStyle.Blank`, fixed 640×480 host, deterministic center/zoom, and one visible/opaque `MapElementsLayer`.
   - Construct all `Geopath`, `MapElement`, color, and unparented `PathIcon` values in the UI-thread callback.

2. `CaptureAndSaveAsync` (or style-consistent equivalent)
   - Accept `MapControl`, bounded cancellation token, and a unique phase label.
   - Call `await map.CaptureRenderedFrameAsync(token)`.
   - Save that exact `MapRenderFrame` through `MapRenderFrameTestUtilities.SavePngAsync` to `Path.Combine(TestContext.TestRunResultsDirectory!, $"{TestContext.TestName}-{phase}.png")`.
   - Assert the PNG exists; return the same frame for component assertions.

3. Expected-state polling helper
   - Repeatedly capture renderer frames after live mutation until the expected color/component geometry appears or the bounded token expires.
   - Never use arbitrary sleeps.

4. Component helpers
   - Use `ConnectedComponentAnalyzer.Near` with explicit color tolerance, alpha floor, minimum pixel count, and 8-connectivity.
   - Return colored component count, pixel count, bounds, center, containment, and endpoint/segment evidence.
   - Provide positive and stale-color-absence checks; absence is valid only after the corresponding before-frame proves presence.

5. Deterministic geometry/icon builders
   - Use separated opaque colors.
   - Use long-enough projected lines and contours to keep dashed segments disconnected even under 8-connectivity.
   - Use asymmetric, resource-independent `PathIcon` geometry so anchor and replacement bounds are observable.

### Narrow Validation

Run:

1. `dotnet build .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64`
2. `git diff --check`

### Success Criteria

- [ ] The new class compiles in the existing test project without project-file changes.
- [ ] No controls/XAML objects are retained in test-class fields.
- [ ] Every future assertion can consume and save the same internal readback frame.
- [ ] No network map, font glyph, sleep, whole-window screenshot oracle, or mock is introduced.

---

## Phase 2: Polygon Live Rendering Matrix

### Overview

Test the largest untested live surface first. Establish polygon fill and stroke readback before relying on the same geometry renderer in inherited-state tests.

### File to Test

#### `MapPolygon.cs`
- **Source**: `WinUIEx.Maps/MapPolygon.cs`
- **Test File**: `WinUIEx.Maps.Tests/UITests/MapElementRenderingTests.cs`
- **Test Class**: `MapElementRenderingTests`

### Named Tests and Assertions

1. `MapPolygon_PathRendersAndUpdatesProjectedFillBounds`
   - Properties: `MapPolygon.Path`.
   - Render one known opaque contour and prove a unique fill component has expected minimum pixels, bounds, center, and containment around the projected contour center.
   - Replace `Path` on the same instance with a non-overlapping/differently oriented contour.
   - Assert the component center/bounds move to the expected projected viewport region and old-only pixels disappear.
   - Save `path-before.png` and `path-after.png`.

2. `MapPolygon_PathsRendersHoleAndUpdatesContours`
   - Property: `MapPolygon.Paths`.
   - Render an outer contour plus inner contour and assert the outer fill-colored region/bounds exist while a center sample/component region remains background, proving a hole.
   - Mutate the paths list to remove or relocate the inner contour.
   - Assert the prior hole center becomes fill-colored, component pixel count changes meaningfully, and the outer bounds remain stable.
   - Save `paths-with-hole.png` and `paths-updated.png`.

3. `MapPolygon_FillColorUpdatesInteriorWithoutStalePixels`
   - Property: `MapPolygon.FillColor`.
   - Prove an interior component matches the initial opaque distinctive color.
   - Mutate to a second opaque color and poll readback.
   - Assert the new interior component occupies equivalent bounds/center, the center pixel matches the new color, and no old-color component remains.
   - Save `fill-before.png` and `fill-after.png`.

4. `MapPolygon_StrokeColorOutlinesFillAndUpdatesWithoutStalePixels`
   - Property: `MapPolygon.StrokeColor`.
   - Use contrasting fill/stroke colors and assert stroke-colored pixels surround or align with all sides of the fill bounds while the fill center remains unchanged.
   - Mutate stroke color and assert equivalent border placement in the new color and absence of stale old-stroke components.
   - Save `stroke-color-before.png` and `stroke-color-after.png`.

5. `MapPolygon_StrokeDashedCreatesSeparatedBorderComponents`
   - Property: `MapPolygon.StrokeDashed`.
   - Capture a solid border, then set dashed on the same polygon.
   - Assert the solid stroke forms the expected continuous contour evidence, while the dashed frame contains multiple separated stroke-colored 8-connected components/gaps distributed around the same contour and retains comparable overall extreme bounds.
   - Save `stroke-solid.png` and `stroke-dashed.png`.

6. `MapPolygon_StrokeThicknessIncreasesBorderPixelCount`
   - Property: `MapPolygon.StrokeThickness`.
   - Capture thin and thick strokes on unchanged geometry.
   - Assert thick stroke pixel count and measured border dimension increase by a meaningful tolerance; contour center/placement and fill center remain stable.
   - Save `stroke-thin.png` and `stroke-thick.png`.

### Narrow Validation

Run the narrow UI class command from **Commands**, then `git diff --check`. Confirm all 6 named methods are discovered and every passing test produced all named PNGs under its test-results directory.

### Success Criteria

- [ ] All six public polygon rendering properties have before/after renderer-readback assertions.
- [ ] Hole, dash, border, color, projected placement, and thickness claims use component/pixel evidence.
- [ ] All 12 asserted polygon frames are saved as PNGs.
- [ ] The narrow UI class passes without retries based on sleeps.

---

## Phase 3: Polyline Live Rendering Matrix

### Overview

Cover the second untested live geometry subtype, reusing only the Phase 1 evidence helpers and preserving path endpoints while varying stroke properties.

### File to Test

#### `MapPolyline.cs`
- **Source**: `WinUIEx.Maps/MapPolyline.cs`
- **Test File**: `WinUIEx.Maps.Tests/UITests/MapElementRenderingTests.cs`
- **Test Class**: `MapElementRenderingTests`

### Named Tests and Assertions

1. `MapPolyline_PathUpdatesOrientationBoundsAndProjectedPlacement`
   - Property: `MapPolyline.Path`.
   - Render a known horizontal path and assert one line component spans expected projected endpoints, has horizontal bounds, and is centered in the expected viewport region.
   - Replace it with a known vertical/non-overlapping path.
   - Assert orientation, bounds, center, and projected endpoint regions change while color/thickness evidence remains stable; assert stale old-only path pixels are absent.
   - Save `path-horizontal.png` and `path-vertical.png`.

2. `MapPolyline_StrokeColorUpdatesLineWithoutStalePixels`
   - Property: `MapPolyline.StrokeColor`.
   - Prove the initial opaque color forms the expected path component.
   - Mutate to a second color and assert equivalent bounds/endpoints, the new color along the line, and no remaining old-color component.
   - Save `stroke-color-before.png` and `stroke-color-after.png`.

3. `MapPolyline_StrokeDashedCreatesSeparatedSegments`
   - Property: `MapPolyline.StrokeDashed`.
   - Compare the same path in solid and dashed states.
   - Assert solid continuity, then multiple separated 8-connected dashed components with true pixel gaps, ordered along the path, while first/last segment extremes remain near the projected endpoints.
   - Save `stroke-solid.png` and `stroke-dashed.png`.

4. `MapPolyline_StrokeThicknessIncreasesPerpendicularSizeAndPixelCount`
   - Property: `MapPolyline.StrokeThickness`.
   - Compare thin and thick captures on the same path.
   - Assert perpendicular component dimension and stroke pixel count increase by meaningful tolerances while longitudinal bounds, centerline, and endpoint placement remain stable.
   - Save `stroke-thin.png` and `stroke-thick.png`.

### Narrow Validation

Run the narrow UI class command, then `git diff --check`. Confirm all 10 accumulated tests are discovered and Phase 3 PNG pairs exist.

### Success Criteria

- [ ] All four public polyline rendering properties have mutation-based readback assertions.
- [ ] Orientation, endpoints, color invalidation, real dash gaps, and thickness are asserted.
- [ ] All 8 asserted polyline frames are saved.
- [ ] The accumulated narrow UI class passes.

---

## Phase 4: Map Icon Live Rendering Matrix

### Overview

Replace screenshot-presence-level confidence with deterministic internal readback of icon rasterization, live movement, and asymmetric anchor placement.

### File to Test

#### `MapIcon.cs`
- **Source**: `WinUIEx.Maps/MapIcon.cs`
- **Test File**: `WinUIEx.Maps.Tests/UITests/MapElementRenderingTests.cs`
- **Test Class**: `MapElementRenderingTests`

### Named Tests and Assertions

1. `MapIcon_IconElementReplacementChangesColorAndBounds`
   - Property: `MapIcon.IconElement`.
   - Render a deterministic, unparented asymmetric `PathIcon` and assert its unique color, minimum pixels, bounds, center, and projected-location containment.
   - Replace it with an unparented `PathIcon` having a different shape, dimensions, and color.
   - Assert replacement color/bounds/shape evidence, absence of the old-color component, and continued containment of the projected anchor point.
   - Save `icon-element-before.png` and `icon-element-after.png`.

2. `MapIcon_LocationMovesComponentToProjectedViewportPosition`
   - Property: `MapIcon.Location`.
   - Capture one icon at a known geographic point and assert component center near its projected viewport point.
   - Move the same icon to another known point and assert center displacement direction/magnitude matches projection, old-location pixels disappear, and component color/width/height/pixel count remain within tolerance.
   - Save `location-before.png` and `location-after.png`.

3. `MapIcon_NormalizedAnchorPointShiftsBoundsAroundProjectedLocation`
   - Property: `MapIcon.NormalizedAnchorPoint`.
   - Use asymmetric deterministic geometry at a fixed location.
   - Capture centered anchor and a corner/bottom anchor.
   - Assert bounds shift by the expected logical icon width/height relative to the unchanged projected location, projected point containment/edge relation changes as expected, and color/size remain stable.
   - Save `anchor-center.png` and `anchor-corner.png`.

### Narrow Validation

Run the narrow UI class command, then `git diff --check`. Confirm all 13 accumulated tests are discovered and all icon PNG pairs exist.

### Success Criteria

- [ ] All three icon rendering properties are covered through live mutation.
- [ ] Assertions use internal renderer pixels, not `UiInputInjector.Screenshot`.
- [ ] Icon replacement, movement, and anchor changes each have geometry and stale-pixel checks.
- [ ] The accumulated narrow UI class passes.

---

## Phase 5: Inherited MapElement State, Exhaustiveness, and Quality Gates

### Overview

Complete the nine inherited subtype/property cells. `IsEnabled` and `IsVisible` are independently proven on all three renderer paths. One mixed, same-layer `ZIndex` test cycles all three subtypes through the top position so every subtype's ordering property affects pixels.

### File to Test

#### `MapElement.cs`
- **Source**: `WinUIEx.Maps/MapElement.cs`
- **Test File**: `WinUIEx.Maps.Tests/UITests/MapElementRenderingTests.cs`
- **Test Class**: `MapElementRenderingTests`

### Named Tests and Assertions

1. `MapElement_IsEnabledDoesNotChangeRenderingForAnySubtype`
   - Property/cells: `MapElement.IsEnabled` on `MapIcon`, `MapPolygon`, and `MapPolyline`.
   - Capture distinct components for all three subtypes, set each `IsEnabled = false`, and prove bounds and pixel counts remain unchanged.
   - Save `enabled.png` and `disabled.png`.

2. `MapIcon_IsVisibleRemovesAndRestoresRenderedComponent`
   - Property/cell: `MapElement.IsVisible` on `MapIcon`.
   - Prove the unique icon component is present, set `IsVisible = false`, and prove the component/color and target-region pixels are absent.
   - Restore true and prove equivalent color/bounds/center return.
   - Save `icon-visible.png`, `icon-hidden.png`, and `icon-restored.png`.

3. `MapPolygon_IsVisibleRemovesAndRestoresRenderedComponent`
   - Property/cell: `MapElement.IsVisible` on `MapPolygon`.
   - Apply the same present/absent/restored proof to a unique polygon fill component.
   - Save `polygon-visible.png`, `polygon-hidden.png`, and `polygon-restored.png`.

4. `MapPolyline_IsVisibleRemovesAndRestoresRenderedComponent`
   - Property/cell: `MapElement.IsVisible` on `MapPolyline`.
   - Apply the same present/absent/restored proof to a unique line component and its endpoint regions.
   - Save `polyline-visible.png`, `polyline-hidden.png`, and `polyline-restored.png`.

5. `MapElement_ZIndexReordersMapIconMapPolygonAndMapPolyline`
   - Property/cells: `MapElement.ZIndex` on `MapIcon`, `MapPolygon`, and `MapPolyline`.
   - Place overlapping, distinctively colored instances of all three subtypes in one visible, fully opaque `MapElementsLayer`.
   - Cycle `ZIndex` values so polygon, polyline, then icon is unambiguously topmost.
   - At a shared overlap point, assert the center/top pixel switches to each expected subtype color; also assert each subtype's wider component remains present away from overlap and layer configuration is unchanged.
   - Save `zindex-polygon-top.png`, `zindex-polyline-top.png`, and `zindex-icon-top.png`.

### Narrow Validation

Run the narrow UI class command, then `git diff --check`. Confirm all 18 methods are discovered and all Phase 5 PNGs exist.

### Exhaustive Property/Subtype Traceability

| Effective cell | Named test |
|---|---|
| `MapIcon.IsEnabled` | `MapElement_IsEnabledDoesNotChangeRenderingForAnySubtype` |
| `MapPolygon.IsEnabled` | `MapElement_IsEnabledDoesNotChangeRenderingForAnySubtype` |
| `MapPolyline.IsEnabled` | `MapElement_IsEnabledDoesNotChangeRenderingForAnySubtype` |
| `MapIcon.IconElement` | `MapIcon_IconElementReplacementChangesColorAndBounds` |
| `MapIcon.Location` | `MapIcon_LocationMovesComponentToProjectedViewportPosition` |
| `MapIcon.NormalizedAnchorPoint` | `MapIcon_NormalizedAnchorPointShiftsBoundsAroundProjectedLocation` |
| `MapPolygon.Path` | `MapPolygon_PathRendersAndUpdatesProjectedFillBounds` |
| `MapPolygon.Paths` | `MapPolygon_PathsRendersHoleAndUpdatesContours` |
| `MapPolygon.FillColor` | `MapPolygon_FillColorUpdatesInteriorWithoutStalePixels` |
| `MapPolygon.StrokeColor` | `MapPolygon_StrokeColorOutlinesFillAndUpdatesWithoutStalePixels` |
| `MapPolygon.StrokeDashed` | `MapPolygon_StrokeDashedCreatesSeparatedBorderComponents` |
| `MapPolygon.StrokeThickness` | `MapPolygon_StrokeThicknessIncreasesBorderPixelCount` |
| `MapPolyline.Path` | `MapPolyline_PathUpdatesOrientationBoundsAndProjectedPlacement` |
| `MapPolyline.StrokeColor` | `MapPolyline_StrokeColorUpdatesLineWithoutStalePixels` |
| `MapPolyline.StrokeDashed` | `MapPolyline_StrokeDashedCreatesSeparatedSegments` |
| `MapPolyline.StrokeThickness` | `MapPolyline_StrokeThicknessIncreasesPerpendicularSizeAndPixelCount` |
| `MapIcon.IsVisible` | `MapIcon_IsVisibleRemovesAndRestoresRenderedComponent` |
| `MapPolygon.IsVisible` | `MapPolygon_IsVisibleRemovesAndRestoresRenderedComponent` |
| `MapPolyline.IsVisible` | `MapPolyline_IsVisibleRemovesAndRestoresRenderedComponent` |
| `MapIcon.ZIndex` | `MapElement_ZIndexReordersMapIconMapPolygonAndMapPolyline` |
| `MapPolygon.ZIndex` | `MapElement_ZIndexReordersMapIconMapPolygonAndMapPolyline` |
| `MapPolyline.ZIndex` | `MapElement_ZIndexReordersMapIconMapPolygonAndMapPolyline` |

### Full Validation

Run sequentially without restore:

1. Narrow UI class command.
2. `dotnet build .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64`
3. `dotnet build .\WinUIEx.Maps\WinUIEx.Maps.csproj --no-restore -c Release`
4. `dotnet build .\MapSample\MapSample.csproj --no-restore -p:Platform=x64`
5. `dotnet build .\WinUIEx.Maps.slnx --no-restore -p:Platform=x64`
6. `dotnet test .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64`
7. `dotnet test .\WinUIEx.Maps.slnx --no-restore -p:Platform=x64 --list-tests`
8. `git diff --check`

Discovery must list every one of the 18 named methods above. Record command, exit code, test count, and any environment blocker; do not conceal an interactive-desktop failure.

### Mandatory Coverage-Gap Review and Gates

1. **Inventory review**
   - Reconcile the 22-row traceability table against the bounded inventory.
   - Verify each row has at least one saved renderer-readback PNG and a meaningful component/pixel assertion.
   - Verify all `IsVisible` absence checks first establish presence.
   - Verify all live mutation tests reject stale old pixels.
   - Verify `IsEnabled` preserves rendering, while layer APIs and unsupported custom subclasses have not been mislabeled as element rendering coverage.

2. **`test-gap-analysis` gate — mandatory**
   - Invoke `test-gap-analysis` after tests and full validation.
   - Scope it to the four production target files, the new test file, and the 22 effective property/subtype cells.
   - Require no **No coverage** cell and no unexplained rendering-affecting **Survived** gap.
   - Concentrate pseudo-mutations on visibility filtering, `ZIndex` ordering, icon location/anchor/element publication, geometry path switching, color switching, dashed/solid branching, and thickness arithmetic.
   - Because workspace preservation forbids revert/reset and this task must not alter production code, run this as a non-mutating/static inventory review. Label findings as static/unverified where the skill requires empirical mutation injection. Resolve findings by strengthening tests only, rerun the narrow/full validations, and rerun the gate. Never inject or revert production mutations.

3. **`assertion-quality` gate — mandatory**
   - Invoke `assertion-quality` on every test/helper in `MapElementRenderingTests.cs`.
   - Classify MSTest assertions plus helper-contained component assertions.
   - Reject assertion-free or trivial-only property tests, unawaited async assertions, PNG-existence-only tests, property-value-only tests, and whole-window screenshot-only tests.
   - Require each named test to contain meaningful renderer behavior evidence. Across the class, require equality/color, approximate/tolerance, negative/stale-absence, state-transition, collection/component, and structural geometry categories where applicable.
   - Treat PNG existence as artifact verification only, never as sufficient rendering verification.
   - Resolve every relevant finding, rerun the narrow/full validations, and rerun both quality gates.

4. **Final requirement audit**
   - Check every governing checklist box only from recorded command output and the 22-cell traceability table.
   - Any unexplained gap, weak assertion, missing PNG, undiscovered test, warning/error, or failed command leaves implementation incomplete.

### `status.md` Production

Create/update `.testagent/status.md` during implementation; do not overwrite unrelated existing status information. Record:

- dirty-workspace preservation statement and confirmation that no restore/revert/reset/clean/delete/commit occurred;
- phase status with exact added test names;
- all narrow and full validation commands, timestamps, exit codes, discovered/passed/failed counts, and desktop/environment blockers;
- PNG artifact paths for every before/after/hidden/restored/order frame;
- the completed 22-cell property/subtype matrix;
- `test-gap-analysis` result, including its non-mutating/static limitation and every resolved or outstanding finding;
- `assertion-quality` metrics/result and every resolved or outstanding finding;
- final checklist with evidence links/paths and an explicit complete/incomplete conclusion.

### Final Success Criteria

- [ ] All 18 named live tests pass and cover all 22 effective cells.
- [ ] Each assertion is based on `CaptureRenderedFrameAsync` plus concrete component/pixel behavior.
- [ ] Every asserted frame is saved under `TestContext.TestRunResultsDirectory`.
- [ ] Narrow, full-project, build, sample, workspace, discovery, and whitespace gates pass.
- [ ] Mandatory `test-gap-analysis` reports no unexplained inventory gap.
- [ ] Mandatory `assertion-quality` accepts every new rendering assertion.
- [ ] `.testagent/status.md` contains complete evidence and an honest final status.
- [ ] Dirty workspace and existing architecture remain preserved.
