---
name: mapcontrol-etw-diagnostics
description: Collect and analyze MapControl rendering ETW events for runtime failures, tile latency, cache pressure, camera behavior, icons, and D3D resource problems.
---

# MapControl ETW diagnostics

Use this skill whenever a problem depends on runtime behavior: blank or stale maps, pan or
zoom glitches, slow/missing tiles, authentication/network/decode failures, cache growth,
device loss, GPU upload failures, missing icons, icon stress regressions, suspension, or
resume. Diagnose these issues from a trace before adding ad-hoc logging or changing
render-thread behavior.

## Provider and collection

The stable provider is **`WinUIEx-Maps-Rendering`**. Its implementation and
canonical event IDs are in
`WinUIEx.Maps/Rendering/Diagnostics/MapControlEventSource.cs`. Event IDs are compatibility
surface: never renumber or reuse them.

Keywords:

| Mask | Area |
|---:|---|
| `0x01` | control/render lifecycle |
| `0x02` | D3D device and GPU resources |
| `0x04` | camera and scene |
| `0x08` | unified raster scheduling, requests, and uploads (Azure event family) |
| `0x10` | unified source-keyed raster cache and pending-request deduplication |
| `0x20` | icon raster, textures, instances, and draw batches |
| `0x40` | failures |
| `0x80` | custom-source classification within the unified raster pipeline |
| `0x100` | Azure vector-tile decoding, styles, sprites, and point symbols |
| `0x200` | accessibility semantic snapshots and announcement decisions |
| `0x3FF` | all areas |

Levels are Error (`2`), Warning (`3`), Informational (`4`), and Verbose (`5`).
Informational is the normal diagnostic level. Verbose adds `SceneChanged` and the
per-render `IconRenderBatch`; enable it only for short camera/icon investigations.

From the repository root, build and launch the packaged sample for the current machine
architecture with the repository workflow:

```powershell
& "$env:USERPROFILE\.copilot\installed-plugins\awesome-copilot\winui\skills\winui-dev-workflow\BuildAndRun.ps1" .\MapSample\MapSample.csproj
```

If that plugin location differs, locate the script rather than hardcoding a machine path:

```powershell
Get-ChildItem "$env:USERPROFILE\.copilot" -Filter BuildAndRun.ps1 -Recurse |
    Where-Object FullName -Like '*winui-dev-workflow*'
```

The launcher prints the sample PID. In a second PowerShell window, collect all
Informational events:

```powershell
dotnet-trace collect --process-id <PID> `
  --providers WinUIEx-Maps-Rendering:0x3FF:4 `
  --output .\mapcontrol.nettrace
```

For a short icon/camera trace, change the final level to `5`. To reduce volume, select
keywords, for example tiles + cache + errors = `0x58`, or lifecycle + device + errors =
`0x43`. Install `dotnet-trace` with `dotnet tool install --global dotnet-trace` if the
command is not present. Use a `dotnet` tool matching the process architecture. Stop
collection with Enter or Ctrl+C after reproducing once.

Open `.nettrace` in PerfView or Visual Studio's diagnostics tools. `dotnet-trace convert
mapcontrol.nettrace --format Speedscope` is useful for correlated CPU stacks, but event
payload inspection is best in PerfView's Events view.

## Stable event catalog

| ID | Event | Level/keyword | Meaning |
|---:|---|---|---|
| 1–3 | `ControlCreated`, `ControlLoaded`, `ControlUnloaded` | Info/Lifecycle | control lifetime |
| 4 | `ControlDisposed` | Info/Lifecycle | reserved legacy ID; no longer emitted |
| 5–6 | `DeviceResourcesCreateStart/Stop` | Info/Device | paired creation duration and success |
| 7 | `DeviceResourcesReleased` | Info/Device | released tile/icon texture counts |
| 8 | `RendererFailure` | Error/Device+Errors | resize, initialization, or render failure |
| 9 | `CameraTargetChanged` | Info/Camera | requested center/zoom/viewport; emitted on target changes, not every frame |
| 10 | `SceneChanged` | Verbose/Camera | required tile set changed |
| 11–12 | `TileWaveStart/Stop` | Info/Tiles | generation/scene-correlated batch, duration, completion/failure/cancel counts |
| 13 | `TileRequestFailed` | Error/Tiles+Errors | tile/style/generation/status and sanitized failure category |
| 14 | `AttributionRequestFailed` | Error/Tiles+Errors | style/zoom/status and sanitized failure category |
| 15 | `TileRequestsCanceled` | Info/Tiles | generation and cancellation reason |
| 16 | `TileUploadFailed` | Error/Tiles+Device+Errors | tile/generation/GPU operation/HRESULT |
| 17 | `TileUploadSummary` | Info/Tiles+Device | background GPU texture creation, pre-commit drops/failures, and duration |
| 18 | `TileCacheLookupSummary` | Info/Cache | aggregate hit/pending-dedup/miss counts |
| 19–20 | `TileCachePressure`, `TileCacheEvicted` | Warning+Info/Cache | budget pressure and aggregate eviction result |
| 21–22 | `IconSnapshotPublished`, `IconUpdatesPublished` | Info/Icons | visible data crossing the UI/render boundary |
| 23–24 | `IconRasterizationFailed`, `IconTextureUploadFailed` | Error/Icons+Errors | XAML raster or GPU texture failure |
| 25 | `IconTextureUploadSummary` | Info/Icons | aggregate upload/replacement/removal |
| 26 | `IconRenderBatch` | Verbose/Icons | visible/drawable instances, texture batches, draw calls |
| 27–28 | `RenderingSuspended`, `RenderingResumed` | Info/Lifecycle | unload/dispose and reload behavior |
| 29 | `ControlFailure` | Error/Lifecycle+Errors | template or required configuration failure |
| 30 | `TileSetActivated` | Info/Tiles | generation/tile-zoom transition and retained cache state |
| 31 | `TileUploadCommitSummary` | Info/Tiles+Device | textures accepted into the active cache versus stale/duplicate completions |
| 32 | `LayersChanged` | Info/Icons | layer collection add/remove/reset/replacement or a `MapElementsLayer.MapElements` replacement, with current layer and element counts |
| 33 | `CustomTileLayerConfigured` | Info/CustomTiles | sanitized custom-session add/reconfiguration details: size, source zooms, and scheme |
| 34 | `CustomTileLayerRemoved` | Info/CustomTiles | custom session lifecycle ended; no user-supplied ID is recorded |
| 35–36 | `CustomTileWaveStart/Stop` | Info/CustomTiles | custom-source waves from the unified scheduler, with generation-correlated counts and duration |
| 37 | `CustomTileRequestFailed` | Error/CustomTiles+Errors | coordinates, generation, status, and sanitized failure kind/type |
| 38 | `CustomTileUploadFailed` | Error/CustomTiles+Device+Errors | coordinates, generation, exception type, and HRESULT |
| 39 | `CustomTileUploadSummary` | Info/CustomTiles+Device | custom-source acceptances from the shared GPU upload queue/cache |
| 40 | `CustomTileCacheSummary` | Info/CustomTiles+Cache | compatibility summary emitted when the shared raster cache evicts |
| 41 | `TextureDisposalSummary` | Info/Device+Cache | aggregate D3D tile/icon texture releases, released bytes, and disposal backlog |
| 42 | `TilePipelineBacklog` | Info/Tiles+Device+Cache | generation-correlated decoded/completed/disposal queue counts and occupied bounded upload slots |
| 43 | `TileRequestTiming` | Verbose/Tiles | sanitized successful download/decode/upload-wait/total timing and request concurrency |
| 44 | `TileUploadTiming` | Info/Tiles+Device | upload-pass queue depth, texture creation, render-lock wait, total duration, and render wakes |
| 45 | `RasterCoverageMilestone` | Info/Tiles+Device+Cache | first-tile, complete, and opaque viewport coverage time/counts plus cache bytes |
| 46 | `TileSchedulerSummary` | Info/Tiles | continuously fed scheduler candidates, starts, completions, peak concurrency, deferrals, and duration |
| 47 | `CameraHeadingTargetChanged` | Info/Camera | normalized heading target and whether direct manipulation bypasses interpolation |
| 48 | `CameraPitchTargetChanged` | Info/Camera | normalized pitch target and whether direct manipulation bypasses interpolation |
| 49 | `VectorTileCommitSummary` | Info/Tiles+VectorTiles | generation-checked vector commits, stale drops, decoded point and prepared sprite counts, and CPU cache size |
| 50 | `VectorStyleAssetsLoaded` | Info/Tiles+VectorTiles | successful Style Spec/sprite load, supported and explicitly skipped layer counts, atlas dimensions, and duration |
| 51 | `VectorSymbolRenderBatch` | Verbose/Icons+VectorTiles | aggregate point-symbol candidates, drawable instances, typed evaluation failures, unavailable sprites, texture batches, and draw calls |
| 52 | `VectorGlyphRangeLoaded` | Info/Tiles+VectorTiles | successful bounded glyph-range acquisition and decode with sanitized glyph/byte counts and duration |
| 53 | `VectorLabelRenderBatch` | Verbose/Icons+VectorTiles | aggregate point-label glyph candidates, drawable glyphs, evaluation failures, unavailable glyphs, texture batches, and draw calls |
| 54 | `VectorGlyphRangeUnavailable` | Warning/Tiles+VectorTiles+Errors | definitive 400/404 glyph-range response cached as unavailable so remaining tile imagery and symbols can continue |
| 55 | `VectorLabelCollisionSummary` | Verbose/Icons+VectorTiles | screen-space label candidates accepted or suppressed by higher-priority overlapping labels, plus suppressed glyph count |
| 56 | `VectorLineRenderBatch` | Verbose/Tiles+VectorTiles | style-resolved vector line candidates, drawable lines, generated triangle count, evaluation failures, and draw calls |
| 57 | `VectorLineFallbackSummary` | Verbose/Tiles+VectorTiles | retained line-tile instances drawn from adjacent zooms versus distant fallback instances suppressed to prevent over-generalized cross-screen strokes |
| 58 | `VectorPolygonRenderBatch` | Verbose/Tiles+VectorTiles | style-resolved polygon candidates, visible tessellated triangles, evaluation failures, distant fallback suppression, and draw calls |
| 59 | `VectorGeometryFallbackOpacitySummary` | Verbose/Tiles+VectorTiles | retained line or polygon instances smoothly faded by zoom distance and overlapping active-tile readiness instead of abruptly disappearing |
| 60 | `VectorLineSymbolPlacementSummary` | Verbose/Icons+VectorTiles | line-following icon and glyph components resolved from tile geometry, successfully projected along screen-space paths, and drawn after collision suppression |
| 61 | `VectorGeometryFrameCacheSummary` | Verbose/Tiles+VectorTiles | GPU line or polygon frame geometry built or reused for translation-only panning, with retained vertex and native-buffer byte counts |
| 62 | `VectorGeometryDeferredRebuildSummary` | Verbose/Tiles+VectorTiles | whole-scene line or polygon rebuild deferred during active panning while newly available tiles are rendered incrementally, with pending tile count and translated cache offset |
| 63 | `VectorGeometryPreparationSummary` | Informational/Tiles+VectorTiles | background line/polygon vertex preparation accepted or discarded, separating CPU preparation from immutable GPU-buffer creation |
| 64 | `VectorLabelTextureReadinessSummary` | Verbose/Icons+VectorTiles | whole labels and glyphs withheld until every texture required by each label is available |
| 65 | `VectorLabelFadeSummary` | Verbose/Icons+VectorTiles | complete labels and glyphs currently fading from the newest required glyph texture |
| 66 | `VectorLineDecorationSummary` | Verbose/Tiles+VectorTiles | dashed-line candidates and triangles (`decorationKind=1`), or patterned-line candidates and projected sprite instances (`decorationKind=2`) |
| 67 | `VectorPolygonDecorationSummary` | Verbose/Tiles+VectorTiles | patterned polygon/triangle counts and explicit outline triangle counts |
| 68 | `VectorAdvancedLineStyleSummary` | Verbose/Tiles+VectorTiles | line counts using offsets, gap/casing widths, gradients, blur, and true miter joins |
| 69 | `VectorAdvancedSymbolStyleSummary` | Verbose/Icons+VectorTiles | counts of rotated, tinted, text-fitted, sorted, and collision-overridden symbols |
| 70 | `CameraViewChangeRequested` | Info/Camera | programmatic view animation kind and nullable camera-field presence without application data |
| 71 | `TextScaleFactorChanged` | Info/Icons+VectorTiles | effective vector-label scale and whether control text scaling is enabled |
| 72 | `AccessibilitySnapshotPublished` | Info/VectorTiles+Accessibility | displayed semantic candidates, deduplication, bounded publication count, and scene version |
| 73 | `AccessibilityAnnouncementDecision` | Info/Accessibility | feature count and whether a settled semantic update raised or suppressed a live-region announcement |
| 74 | `AnimationsEnabledChanged` | Info/Camera+Accessibility | effective system animation preference changed, suppressing camera interpolation, touch inertia, focus transitions, and layer fades when disabled |

## Reproduce and interpret

- **Pan/zoom/rotate/tilt:** collect Camera+Tiles+Cache (`0x1C`) at Informational. Use
  `CameraTargetChanged`, `CameraHeadingTargetChanged`, and `CameraPitchTargetChanged` as the intent,
  `TileWaveStart/Stop` as the work, and generation plus sceneVersion as correlation.
  Repeated cancellations without camera/style changes suggest a scheduling regression.
- **Missing/slow tiles:** filter IDs 11–18, 31, and 43–46. Compare scheduler duration and counts. HTTP status is
  present for service failures; `failureKind` distinguishes `ServiceResponse`, `Network`,
  and `Decode`. A completed request followed by upload failure localizes the issue to D3D.
  Compare IDs 17, 31, and 44 to distinguish texture creation, render-lock wait, and final
  cache acceptance. ID 45 directly reports first, complete, and opaque coverage.
- **Custom raster tiles:** select CustomTiles+Cache+Errors (`0xD0`) and inspect IDs 33–40.
  These events classify custom-source behavior inside the same scheduler, bounded upload
  queue, GPU cache, fallback selector, and render path used by Azure; they do not indicate
  a second pipeline. Correlate by generation (never by a user-provided layer ID). IDs 35/36
  distinguish scheduling/network latency, ID 37 is sanitized HTTP/template/decode failure,
  and IDs 38/39 localize upload or stale-generation behavior. `MapStyle.Blank` removes the
  hidden Azure `TileLayer`, so it should produce no Azure tile/attribution work while custom
  IDs continue.
- **Azure vector tiles:** select Tiles+VectorTiles (`0x108`) and correlate IDs 11–17, 43–46,
  and 49–69. ID 49 confirms that MVT responses reached generation-checked CPU cache commit,
  ID 50 distinguishes asset acquisition from tile decode and reports explicitly unsupported
  style-layer counts, ID 52 reports glyph-range latency, ID 54 reports definitive unavailable
  ranges without font or label content, verbose IDs 51/53 summarize point-symbol and
  point-label batching, verbose ID 55 quantifies collision suppression, and verbose ID 56
  reports direct line geometry generation and drawing, while ID 57 identifies distant
  fallback suppression during zoom transitions, ID 58 summarizes polygon fills, and ID 59
  quantifies fallback crossfading, ID 60 reports line-following symbol placement, and ID 61
  distinguishes geometry rebuilds from translation-only frame reuse, while ID 62 confirms
  that tile arrivals were handled incrementally instead of forcing an in-motion rebuild, and ID 63
  separates background geometry preparation from immutable GPU-buffer creation. ID 64 confirms
  labels are withheld as complete groups while glyph textures are still uploading, ID 65
  confirms those complete groups fade after becoming ready, and ID 66 distinguishes dashed
  geometry from sprite-patterned line placement without exposing pattern names, and ID 67
  reports patterned polygon and explicit outline geometry, and ID 68 reports advanced line
  styling usage, and ID 69 reports advanced symbol styling and collision-control usage. None exposes source-layer names, properties, sprite names, URLs, or service
  content.
- **Cache/dedup:** inspect ID 18 over time. A high `pendingDedupCount` is expected while a
  wave is active. Repeated misses for the same stable scene or evictions that cannot return
  below the viewport-aware budget reported by ID 19 indicate shared raster-cache behavior
  to investigate. The budget retains protected visible/fallback textures plus 16 MiB of
  navigation history, normally with a 32–128 MiB range. Protected coverage may exceed the
  128 MiB soft cap for large viewports, 512px tiles, or multiple raster layers and is never
  evicted. Cache identity is source plus tile coordinate, so equal coordinates in different
  layers remain independent. Pair
  eviction events with ID 41; a growing `remainingCount` means texture release is not
  keeping pace with cache churn. ID 42 separates transient decoded/GPU-completed work from
  resident cache entries; `occupiedUploadSlots` is bounded at 32.
- **Icons:** use Icons+Errors (`0x60`) at Verbose for a short reproduction. Compare snapshot
  instances to drawable instances, texture count/batches, and draw calls. IDs 23/24
  distinguish UI-thread XAML rasterization from background GPU upload. Use ID 32 to
  correlate layer ownership/replacement with IDs 21–22 and verify expected current counts.
  Texture batches are per layer, so the same texture used in two layers contributes two
  batches in ID 26; layers render from the first (bottom-most) to the last (top-most).
- **Device/blank surface:** use Lifecycle+Device+Errors (`0x43`). Pair IDs 5/6, then look for
  ID 8 and HRESULT. Verify resource release and recreation around unload/resume.

## Privacy and Copilot analysis rules

The provider never records `MapServiceToken`, source keys, tile templates, expanded request
URLs, hidden or public layer IDs, subdomains, query strings, headers,
response bodies, service error text, icon pixel bytes, or attribution text. Tile identity,
style enum, HTTP status, sanitized failure category, exception type, and HRESULT are safe
diagnostic metadata. Do not add secret-bearing values to an event, even temporarily.

When analyzing a trace, Copilot should:

1. State the selected provider, keyword mask, level, process, and reproduction interval.
2. Build a timeline by generation and sceneVersion; pair Start/Stop events.
3. Separate service/network/decode, GPU/device, cache, and icon-raster stages.
4. Quantify counts and durations before proposing a cause.
5. Cite event IDs/names and payload values supporting each conclusion.
6. Treat missing expected events as evidence about which boundary was not crossed.
7. If code changes behavior, update the provider schema without renumbering existing IDs,
   add `EventListener` coverage, and update this catalog.
