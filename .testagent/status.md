# Test Generation Status

## Phase 1: Live Renderer Fixture and Evidence Helpers

- **Status:** SUCCESS
- **Tests created:** 0 (fixture/helpers only, as planned)
- **File created:** `WinUIEx.Maps.Tests/UITests/MapElementRenderingTests.cs`
- Added the shared `[TestClass]` / `[DoNotParallelize]` fixture, deterministic blank-map setup, internal renderer capture/poll/save pipeline, 8-connected color-component evidence helpers, stale-color checks, projected endpoint sampling, geographic path builders, and resource-independent asymmetric `PathIcon` builders.
- No production files or existing test files were changed.
- The WinUI MSTest extension does not expose `TestContext.TestRunResultsDirectory` as a compile-time property. The helper reads the `TestRunResultsDirectory` context property by name, falls back to the test executable's `TestResults` directory when unavailable, and registers every PNG with `TestContext.AddResultFile`.

### Exact command results

1. `dotnet build .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64`
   - Initial exit code: `1`
   - Result: `1 Warning(s), 1 Error(s)`
   - Error: `CS1061: 'TestContext' does not contain a definition for 'TestRunResultsDirectory'`
   - Fix: use the WinUI-compatible test-context property lookup described above.
2. `dotnet build .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64`
   - Final exit code: `0`
   - Result: `0 Warning(s), 0 Error(s)`
   - Elapsed: `00:00:15.07`
3. `git diff --check`
   - Exit code: `0`
   - Output: none

### Discovery note

- Baseline attempt: `dotnet test .\WinUIEx.Maps.slnx --no-restore -p:Platform=x64 --list-tests`
- Exit code: `1`
- Result: Microsoft.Testing.Platform reports that the VSTest target is unsupported with the .NET 10 SDK unless the new `dotnet test` experience is enabled.
- Phase 1 intentionally adds no test methods, and its plan requires only the narrow build and whitespace validations above.

## Phase 2: Polygon Live Rendering Matrix

- **Status:** SUCCESS
- **Tests created/passing:** 6/6
- **Harness discovery delta:** +6
- **File updated:** `WinUIEx.Maps.Tests/UITests/MapElementRenderingTests.cs`
- Added all six planned live `MapPolygon` mutation tests for `Path`, `Paths`, `FillColor`, `StrokeColor`, `StrokeDashed`, and `StrokeThickness`.
- Every assertion reads the internal renderer frame. The tests use 8-connected color components plus concrete pixel counts, bounds, centers, containment, hole/gap samples, stale-color absence, and before/after geometry comparisons.
- All 12 planned PNG evidence files were created and registered. No production file was changed.

### Exact command results

1. `dotnet build .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64`
   - Initial exit code: `1`
   - Result: `4 Error(s), 1 Warning(s)`
   - Errors: four `CS0103` references to an unqualified `Colors`; fixed by using `Microsoft.UI.Colors.Transparent`.
2. `dotnet build .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64`
   - Exit code: `0`
   - Result: `0 Warning(s), 0 Error(s)`
   - Elapsed: `00:00:11.96`
3. Narrow UI class command:
   `dotnet run --project .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64 -- --filter "FullyQualifiedName~WinUIEx.Maps.Tests.UITests.MapElementRenderingTests"`
   - First exit code: `2`; `0/6` passed because missing `TestRunResultsDirectory` threw `KeyNotFoundException`. Fixed with `TryGetValue` plus the existing fallback directory.
   - Second exit code: `2`; `5/6` passed. The dashed-border gap sample landed on a dash; renderer evidence showed the actual gap and the sample was corrected from `(150,100)` to `(155,100)`.
   - Final exit code: `0`
   - Final result: `6 passed, 0 failed, 0 skipped`
   - Duration: `8s 533ms`
4. Harness command:
   `dotnet test .\WinUIEx.Maps.slnx --no-restore -p:Platform=x64 --list-tests`
   - Exit code: `1`
   - Result: the known Microsoft.Testing.Platform/.NET 10 VSTest-target incompatibility.
5. MTP-native fallback discovery:
   `dotnet run --project .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64 -- --list-tests --filter "FullyQualifiedName~WinUIEx.Maps.Tests.UITests.MapElementRenderingTests"`
   - Exit code: `0`
   - Result: exactly 6 tests found; discovery duration `222ms`.
   - Discovered:
     - `MapPolygon_PathRendersAndUpdatesProjectedFillBounds`
     - `MapPolygon_PathsRendersHoleAndUpdatesContours`
     - `MapPolygon_FillColorUpdatesInteriorWithoutStalePixels`
     - `MapPolygon_StrokeColorOutlinesFillAndUpdatesWithoutStalePixels`
     - `MapPolygon_StrokeDashedCreatesSeparatedBorderComponents`
     - `MapPolygon_StrokeThicknessIncreasesBorderPixelCount`

### PNG evidence

All files are under `WinUIEx.Maps.Tests\bin\x64\Debug\net10.0-windows10.0.19041.0\TestResults`:

- `MapPolygon_PathRendersAndUpdatesProjectedFillBounds-path-before.png`
- `MapPolygon_PathRendersAndUpdatesProjectedFillBounds-path-after.png`
- `MapPolygon_PathsRendersHoleAndUpdatesContours-paths-with-hole.png`
- `MapPolygon_PathsRendersHoleAndUpdatesContours-paths-updated.png`
- `MapPolygon_FillColorUpdatesInteriorWithoutStalePixels-fill-before.png`
- `MapPolygon_FillColorUpdatesInteriorWithoutStalePixels-fill-after.png`
- `MapPolygon_StrokeColorOutlinesFillAndUpdatesWithoutStalePixels-stroke-color-before.png`
- `MapPolygon_StrokeColorOutlinesFillAndUpdatesWithoutStalePixels-stroke-color-after.png`
- `MapPolygon_StrokeDashedCreatesSeparatedBorderComponents-stroke-solid.png`
- `MapPolygon_StrokeDashedCreatesSeparatedBorderComponents-stroke-dashed.png`
- `MapPolygon_StrokeThicknessIncreasesBorderPixelCount-stroke-thin.png`
- `MapPolygon_StrokeThicknessIncreasesBorderPixelCount-stroke-thick.png`

The 12 files were verified nonempty; sizes were `2386, 2386, 2386, 2423, 2383, 2383, 2440, 2444, 2693, 2428, 2450, 2441` bytes.

6. `git diff --check`
   - Exit code: `0`
   - Output: none

## Phase 3: Polyline Live Rendering Matrix

- **Status:** SUCCESS
- **Tests created/passing:** 4/4; accumulated class result 10/10
- **Harness discovery delta:** +4 (6 before Phase 3; 10 after Phase 3)
- **File updated:** `WinUIEx.Maps.Tests/UITests/MapElementRenderingTests.cs`
- Added all four planned live `MapPolyline` mutation tests for `Path`, `StrokeColor`, `StrokeDashed`, and `StrokeThickness`.
- Every assertion uses internal renderer readback. The tests pin projected orientation, bounds, centers, endpoints, stale-path removal, color invalidation, separated 8-connected dash components with a concrete gap pixel, and perpendicular thickness/pixel-count growth.
- All 8 planned PNG evidence files were created, registered, and verified nonempty. No production file was changed.

### Exact command results

1. `dotnet build .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64`
   - Exit code: `0`
   - Result: `0 Warning(s), 0 Error(s)`
   - Elapsed: `00:00:12.06`
2. Accumulated narrow UI class command:
   `dotnet run --project .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64 -- --filter "FullyQualifiedName~WinUIEx.Maps.Tests.UITests.MapElementRenderingTests"`
   - Exit code: `0`
   - Result: `10 passed, 0 failed, 0 skipped`
   - Duration: `13s 531ms`
3. MTP-native accumulated discovery:
   `dotnet run --project .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64 -- --list-tests --filter "FullyQualifiedName~WinUIEx.Maps.Tests.UITests.MapElementRenderingTests"`
   - Exit code: `0`
   - Result: exactly 10 tests found; discovery duration `223ms`.
   - Phase 3 discoveries:
     - `MapPolyline_PathUpdatesOrientationBoundsAndProjectedPlacement`
     - `MapPolyline_StrokeColorUpdatesLineWithoutStalePixels`
     - `MapPolyline_StrokeDashedCreatesSeparatedSegments`
     - `MapPolyline_StrokeThicknessIncreasesPerpendicularSizeAndPixelCount`
4. `git diff --check`
   - Exit code: `0`
   - Output: none

### PNG evidence

All files are under `WinUIEx.Maps.Tests\bin\x64\Debug\net10.0-windows10.0.19041.0\TestResults`:

- `MapPolyline_PathUpdatesOrientationBoundsAndProjectedPlacement-path-horizontal.png` — 2386 bytes
- `MapPolyline_PathUpdatesOrientationBoundsAndProjectedPlacement-path-vertical.png` — 2388 bytes
- `MapPolyline_StrokeColorUpdatesLineWithoutStalePixels-stroke-color-before.png` — 2386 bytes
- `MapPolyline_StrokeColorUpdatesLineWithoutStalePixels-stroke-color-after.png` — 2386 bytes
- `MapPolyline_StrokeDashedCreatesSeparatedSegments-stroke-solid.png` — 2386 bytes
- `MapPolyline_StrokeDashedCreatesSeparatedSegments-stroke-dashed.png` — 2399 bytes
- `MapPolyline_StrokeThicknessIncreasesPerpendicularSizeAndPixelCount-stroke-thin.png` — 2381 bytes
- `MapPolyline_StrokeThicknessIncreasesPerpendicularSizeAndPixelCount-stroke-thick.png` — 2383 bytes

## Phase 4: Map Icon Live Rendering Matrix

- **Status:** SUCCESS
- **Tests created/passing:** 3/3; accumulated class result 13/13
- **Harness discovery delta:** +3 (10 before Phase 4; 13 after Phase 4)
- **File updated:** `WinUIEx.Maps.Tests/UITests/MapElementRenderingTests.cs`
- Added all three planned live `MapIcon` mutation tests for `IconElement`, `Location`, and `NormalizedAnchorPoint`.
- Every assertion uses internal renderer readback with deterministic `PathIcon` geometry. The tests pin unique colors, connected-component bounds/centers/pixel counts, projected viewport placement, old-color or old-location pixel removal, shape replacement, stable movement geometry, and the expected 16-pixel anchor shift.
- All 6 planned PNG evidence files were created, registered, and verified nonempty. No production file was changed.

### Exact command results

1. Baseline MTP-native accumulated discovery:
   `dotnet run --project .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64 -- --list-tests --filter "FullyQualifiedName~WinUIEx.Maps.Tests.UITests.MapElementRenderingTests"`
   - Exit code: `0`
   - Result: exactly 10 tests found; discovery duration `226ms`.
2. `dotnet build .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64`
   - Exit code: `0`
   - Result: `0 Warning(s), 0 Error(s)`
   - Elapsed: `00:00:11.99`
3. Accumulated narrow UI class command:
   `dotnet run --project .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64 -- --filter "FullyQualifiedName~WinUIEx.Maps.Tests.UITests.MapElementRenderingTests"`
   - First exit code: `2`; `11/13` passed. Two containment assertions used the MSTest bound/actual argument order incorrectly; corrected without changing production code.
   - Second exit code: `2`; `12/13` passed. The initial pin's colored geometry ended above its texture anchor; the test retained deterministic asymmetric geometry but used the flag shape whose colored bounds contain the projected anchor as planned.
   - Final exit code: `0`
   - Final result: `13 passed, 0 failed, 0 skipped`
   - Test duration: `17.611s`; total command duration: `00:00:32.3953361`
4. Final MTP-native accumulated discovery:
   `dotnet run --project .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64 -- --list-tests --filter "FullyQualifiedName~WinUIEx.Maps.Tests.UITests.MapElementRenderingTests"`
   - Exit code: `0`
   - Result: exactly 13 tests found; discovery duration `261ms`.
   - Phase 4 discoveries:
     - `MapIcon_IconElementReplacementChangesColorAndBounds`
     - `MapIcon_LocationMovesComponentToProjectedViewportPosition`
     - `MapIcon_NormalizedAnchorPointShiftsBoundsAroundProjectedLocation`
5. `git diff --check`
   - Exit code: `0`
   - Output: none

### PNG evidence

All files are under `WinUIEx.Maps.Tests\bin\x64\Debug\net10.0-windows10.0.19041.0\TestResults`:

- `MapIcon_IconElementReplacementChangesColorAndBounds-icon-element-before.png` — 2400 bytes
- `MapIcon_IconElementReplacementChangesColorAndBounds-icon-element-after.png` — 2388 bytes
- `MapIcon_LocationMovesComponentToProjectedViewportPosition-location-before.png` — 2383 bytes
- `MapIcon_LocationMovesComponentToProjectedViewportPosition-location-after.png` — 2384 bytes
- `MapIcon_NormalizedAnchorPointShiftsBoundsAroundProjectedLocation-anchor-center.png` — 2405 bytes
- `MapIcon_NormalizedAnchorPointShiftsBoundsAroundProjectedLocation-anchor-corner.png` — 2403 bytes

## Phase 5: Inherited MapElement State

- **Status:** SUCCESS
- **Validation recorded:** `2026-08-29T14:47:00-07:00`
- **Tests created/passing:** 5/5; accumulated class result 18/18
- **Discovery delta:** +5 (13 before Phase 5; 18 after Phase 5)
- **File updated:** `WinUIEx.Maps.Tests/UITests/MapElementRenderingTests.cs`
- Added:
  - `MapElement_IsEnabledDoesNotChangeRenderingForAnySubtype`
  - `MapIcon_IsVisibleRemovesAndRestoresRenderedComponent`
  - `MapPolygon_IsVisibleRemovesAndRestoresRenderedComponent`
  - `MapPolyline_IsVisibleRemovesAndRestoresRenderedComponent`
  - `MapElement_ZIndexReordersMapIconMapPolygonAndMapPolyline`
- All tests use internal renderer readback and concrete color-component, bounds, center, endpoint, overlap-pixel, stale-color, and restored-geometry assertions.
- The mixed-subtype test keeps all three elements in one visible, fully opaque `MapElementsLayer` and proves polygon, polyline, then icon becomes the top pixel as each subtype's `ZIndex` changes.
- The dirty workspace was preserved. No restore, revert, reset, clean, delete, or commit operation was performed.
- Per the coordinator request, the mandatory external skills gates and full validation were not performed in this phase.

### Exact command results

1. `dotnet build .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64`
   - Exit code: `0`
   - Result: `0 Warning(s), 0 Error(s)`
   - Elapsed: `00:00:12.5061680`
2. Accumulated narrow UI class command:
   `dotnet run --project .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64 -- --filter "FullyQualifiedName~WinUIEx.Maps.Tests.UITests.MapElementRenderingTests"`
   - Exit code: `0`
   - Result: `18 passed, 0 failed, 0 skipped`
   - Duration: `23s 751ms`
3. MTP-native accumulated discovery:
   `dotnet run --project .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64 -- --list-tests --filter "FullyQualifiedName~WinUIEx.Maps.Tests.UITests.MapElementRenderingTests"`
   - Exit code: `0`
   - Result: exactly `18` tests found
   - Discovery duration: `220ms`
   - Phase 5 discoveries were the five methods listed above.
4. `git diff --check`
   - Exit code: `0`
   - Output: none

### PNG evidence

All 14 Phase 5 files are under `WinUIEx.Maps.Tests\bin\x64\Debug\net10.0-windows10.0.19041.0\TestResults` and are nonempty, including:

- `MapElement_IsEnabledDoesNotChangeRenderingForAnySubtype-enabled.png`
- `MapElement_IsEnabledDoesNotChangeRenderingForAnySubtype-disabled.png`
- `MapIcon_IsVisibleRemovesAndRestoresRenderedComponent-icon-visible.png` — 2386 bytes
- `MapIcon_IsVisibleRemovesAndRestoresRenderedComponent-icon-hidden.png` — 2358 bytes
- `MapIcon_IsVisibleRemovesAndRestoresRenderedComponent-icon-restored.png` — 2386 bytes
- `MapPolygon_IsVisibleRemovesAndRestoresRenderedComponent-polygon-visible.png` — 2388 bytes
- `MapPolygon_IsVisibleRemovesAndRestoresRenderedComponent-polygon-hidden.png` — 2358 bytes
- `MapPolygon_IsVisibleRemovesAndRestoresRenderedComponent-polygon-restored.png` — 2388 bytes
- `MapPolyline_IsVisibleRemovesAndRestoresRenderedComponent-polyline-visible.png` — 2385 bytes
- `MapPolyline_IsVisibleRemovesAndRestoresRenderedComponent-polyline-hidden.png` — 2358 bytes
- `MapPolyline_IsVisibleRemovesAndRestoresRenderedComponent-polyline-restored.png` — 2385 bytes
- `MapElement_ZIndexReordersMapIconMapPolygonAndMapPolyline-zindex-polygon-top.png` — 2489 bytes
- `MapElement_ZIndexReordersMapIconMapPolygonAndMapPolyline-zindex-polyline-top.png` — 2471 bytes
- `MapElement_ZIndexReordersMapIconMapPolygonAndMapPolyline-zindex-icon-top.png` — 2500 bytes

## Final Validation and Quality Review

- **Final status:** COMPLETE for the requested MapElement rendering scope.
- **Files added by this pipeline:** `WinUIEx.Maps.Tests/UITests/MapElementRenderingTests.cs`, `.testagent/research.md`, `.testagent/plan.md`, and `.testagent/status.md`.
- Existing dirty work was preserved. No restore, revert, reset, clean, delete, or commit command was used.
- The requested `code-testing-extensions` and the gates' `test-analysis-extensions` skills were unavailable; conventions were inferred from the repository's UI-test skill and existing MSTest code.

### Final command evidence

1. `dotnet build .\WinUIEx.Maps.slnx --no-restore --no-incremental -p:Platform=x64`
   - Exit code: `0`
   - Result: full workspace built; `0 Warning(s), 0 Error(s)`; elapsed `00:00:27.43`.
2. `dotnet run --project .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64 -- --filter "FullyQualifiedName~WinUIEx.Maps.Tests.UITests.MapElementRenderingTests"`
   - Exit code: `0`
   - Result: `18 passed, 0 failed, 0 skipped`; duration `23s 751ms`.
   - Produced and registered 40 nonempty renderer-readback PNGs.
3. `dotnet test .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64`
   - Exit code: `1`
   - Blocked before execution by the known Microsoft.Testing.Platform 2.3.3/.NET 10 VSTest-target incompatibility.
4. `dotnet run --project .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64`
   - Exit code: `2`
   - Earlier full-run result: `379 total, 369 passed, 10 failed, 0 skipped`; the then-current 17 rendering tests all passed. The subsequently added `IsEnabled` rendering test passed in the clean focused run above.
   - Unrelated failures: `TouchRotationWaitsForActivationThreshold`; four `ArrowKey_PansMap` rows; `MouseWheel_ZoomsIn`; `MouseWheel_ZoomsOut`; two `MouseWheel_OffCenter_PreservesLocationUnderPointer` rows; and `RotationEndingNearNorthSnapsBack`.
5. `dotnet build .\WinUIEx.Maps\WinUIEx.Maps.csproj --no-restore -c Release`
   - Exit code: `0`; `0 Warning(s), 0 Error(s)`.
6. `dotnet build .\MapSample\MapSample.csproj --no-restore -p:Platform=x64`
   - Exit code: `0`; `0 Warning(s), 0 Error(s)`.
7. `dotnet run --project .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64 -- --list-tests`
   - Exit code: `0`; discovered the earlier 379-test inventory, including the original 17 new methods. The focused runner compiled and executed the subsequently added 18th method.
   - One earlier parallel discovery attempt collided on WinUI XAML compiler `input.json`; the required sequential retry passed.
8. `git diff --check`
   - Exit code: `0`; no output.

### Requirement/property matrix

| Effective rendering cell | Test evidence |
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

`IsEnabled` affects input rather than drawing; the rendering test proves that disabling each subtype preserves its pixels.

### Mandatory gates

- **`test-gap-analysis`: PASS (static/non-mutating).** The final inventory has direct live mutation/readback evidence for all 22 effective cells, including `IsEnabled` across all three subtypes. Visibility filtering, subtype ordering, icon publication/movement/anchor, geometry replacement, color replacement, dashed branching, and thickness changes are covered by concrete color, absence, bounds, center, component-count, or pixel-count assertions.
- **`assertion-quality`: PASS.** All 18 tests contain behavior-specific pixel assertions plus shared helper assertions. Zero tests are assertion-free, trivial-only, self-referential, or single-category. PNG existence is only artifact evidence, never the rendering oracle.
- **Prompt-scenario audit: PASS.** Every renderer-supported subtype and every public rendering-affecting property maps to a named test above. All tests call the exact element property, use `CaptureRenderedFrameAsync`, save the asserted frame, and analyze real pixels/components. Layer visibility/opacity/order are used only to isolate element rendering.
