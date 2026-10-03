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

The packaged sample enables `EventSourceSupport` explicitly, including Release/AOT
configurations. Other AOT hosts must build/publish with `-p:EventSourceSupport=true`
before collecting this provider; enabling collection cannot restore support disabled
in the deployed runtime configuration. Verify provider liveness with a camera/resize
action before recording settled idle. Runtime GC events alone do not prove that
application EventSource support is enabled.

`dotnet build -c Release` is still a CoreCLR run; use `dotnet publish` to validate
Native AOT. When launching published output with folder-mode `winapp run`, pass the
build-generated `AppxManifest.xml`, including its Windows App Runtime package dependency,
not the repository's source manifest. The latter omits build-injected dependencies and
can cause a startup fail-fast even in an unchanged baseline. Verify the published native
executable and absence of `coreclr.dll` before labeling measurements AOT.

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
| `0x100` | Azure and custom vector-tile decoding, styles, sprites, and symbols |
| `0x200` | accessibility semantic snapshots and announcement decisions |
| `0x400` | opt-in per-frame execution and stage timings |
| `0x7FF` | all areas |

Levels are Error (`2`), Warning (`3`), Informational (`4`), and Verbose (`5`).
Informational is the normal diagnostic level. Verbose adds scene, draw-batch, and
opt-in frame timing events; enable it only for short investigations.

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
  --providers WinUIEx-Maps-Rendering:0x7FF:4 `
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
| 10 | `SceneChanged` | Verbose/Camera | required tile set or layer display-zoom eligibility changed |
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
| 53 | `VectorLabelRenderBatch` | Verbose/Icons+VectorTiles | aggregate point-label glyph candidates, drawable glyphs, evaluation failures, unavailable glyphs, texture batches, and draw calls; visible halos add a separate draw pass without doubling glyph or texture-batch counts |
| 54 | `VectorGlyphRangeUnavailable` | Warning/Tiles+VectorTiles+Errors | definitive 400/404 glyph-range response cached as unavailable so remaining tile imagery and symbols can continue |
| 55 | `VectorLabelCollisionSummary` | Verbose/Icons+VectorTiles | screen-space label candidates accepted or suppressed by higher-priority overlapping labels, plus suppressed glyph count |
| 56 | `VectorLineRenderBatch` | Verbose/Tiles+VectorTiles | style-resolved vector line candidates, drawable lines, generated triangle count, evaluation failures, and draw calls |
| 57 | `VectorLineFallbackSummary` | Verbose/Tiles+VectorTiles | retained line-tile instances versus replaced coverage or distant finer fallback suppressed during zoom-out; coarser coverage survives skipped zoom levels |
| 58 | `VectorPolygonRenderBatch` | Verbose/Tiles+VectorTiles | style-resolved polygon candidates, visible tessellated triangles, evaluation failures, replaced coverage or distant finer fallback suppression, and draw calls |
| 59 | `VectorGeometryFallbackOpacitySummary` | Verbose/Tiles+VectorTiles | retained line or polygon opacity during replacement; coarser coverage remains across skipped zoom levels, polygons until eligible replacements are opaque and lines crossfading with readiness; nearer cached ancestors can replace older coverage |
| 60 | `VectorLineSymbolPlacementSummary` | Verbose/Icons+VectorTiles | line-following icon and glyph components resolved from tile geometry, successfully projected along screen-space paths, and drawn after collision suppression |
| 61 | `VectorGeometryFrameCacheSummary` | Verbose/Tiles+VectorTiles | GPU line or polygon frame geometry built or reused for flat or projective panning, with retained vertex and native-buffer byte counts |
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
| 75 | `VectorStyleCompatibilityIssue` | Info/Tiles+VectorTiles | aggregate unsupported or intentionally ignored Style Spec construct and occurrence count; custom styles use `style = -1`; ignored `symbol-avoid-edges` uses issue kind 4 because collisions span visible tiles; kind 5 counts skipped components by sanitized parser reason; kinds 6/7 are reserved legacy flat-extrusion diagnostics and are no longer emitted for supported extrusion paints |
| 76 | `VectorSymbolWorkingMemoryReleased` | Info/Icons+VectorTiles | renderer-owned symbol instance, placement, collision, and accessibility working capacities released when map resources become dormant |
| 77 | `RenderFrameTiming` | Verbose/Frames | renderer/frame-correlated render-lock wait, CPU-side rendering, readback, Present, producer handoff, and total pass duration |
| 78 | `MapFrameStageTiming` | Verbose/Frames | renderer/frame-correlated camera/scene, completion commits, raster, polygon, line, symbol, and remaining frame work |
| 79 | `TilePipelineStageFailed` | Error/Tiles+Errors | supplements unexpected request failures with source kind, tile/generation, stage, exception type, and HRESULT; stage 0 is request admission, 1 acquisition (including decode/assets), 2 renderer admission |
| 80 | `GeometryStreamUploadTiming` | Verbose/Frames | renderer/frame-correlated dynamic geometry upload, discard/no-overwrite and byte counts, and aggregate CPU-side map/copy/unmap duration |
| 81 | `RenderSurfaceChanged` | Info/Device | renderer-correlated logical dimensions, composition scales, physical dimensions, retained color-buffer bytes and selected sample count; presentation uses two single-sample buffers plus a supported-device 4x intermediate, unless startup switch `WinUIEx.Maps.DisableMultisampleAntialiasing` is true; offscreen benchmarks explicitly select samples and use one resolve buffer |
| 82 | `VectorSymbolFallbackSummary` | Verbose/Icons+VectorTiles | cached fallback symbol tiles considered and omitted because their visible footprint has opaque same-source replacement coverage; unrelated missing tiles no longer keep covered old tiers in collision candidates |
| 83 | `VectorRetainedMemory` | Verbose/Frames | renderer/frame-correlated decoded-feature estimates, cached symbol struct payloads, cached line-group index payloads, retained line/polygon geometry-buffer bytes, and running/completed preparation counts |
| 84 | `SymbolInstanceUploadTiming` | Verbose/Frames | renderer/frame-correlated icon/glyph instance uploads, discard/no-overwrite counts, copied bytes, dynamic-buffer capacity and CPU map/copy/unmap time |
| 85 | `IconRasterized` | Verbose/Icons | texture/version-correlated XAML capture dimensions and nontransparent pixel count; distinguishes an empty capture from GPU upload/draw failures without recording pixels |
| 86 | `IconUploadPassTiming` | Verbose/Icons | queued and uploaded map-element versus vector-texture counts, render-lock wait, and total bounded upload-pass duration |
| 88 | `VectorLineComposite` | Verbose/Tiles+VectorTiles | traffic road composite source count, physical width/height, sample count, final opacity, and retained native target bytes; no source identifiers or feature data |
| 89 | `VectorGlyphRangeCacheTrimmed` | Info/Tiles+VectorTiles | aggregate decoded glyph-range LRU eviction count and byte totals; no font, range, or service data |
| 90 | `GeometryScratchMemory` | Verbose/Frames | renderer/frame-correlated process-wide cumulative native geometry scratch allocation and release bytes |
| 91 | `VectorPendingGeometry` | Verbose/VectorTiles | line/polygon pending tile-instance reuse and admission flags, and total pending bytes for that frame-cache owner; no source or tile-content identifiers |
| 92 | `VectorCacheOwnership` | Verbose/Frames | renderer/frame-correlated ownership estimates for decoded features and tile-derived CPU storage, separate pending GPU geometry bytes, and resident tile count |
| 93 | `VectorDashWork` | Verbose/Frames | renderer/frame-correlated process-wide cumulative offscreen dash spans skipped while Frames tracing is enabled |
| 94 | `VectorExtrusionRenderBatch` | Verbose/VectorTiles+Device | aggregate triangles, draws including composite, evaluation failures, retained mesh bytes, and shared color/depth target bytes; zero-draw records also expose failed evaluations with no drawable geometry |
| 95 | `VectorExtrusionPreparationFailed` | Error/VectorTiles+Errors | shared preparation worker failure, containing only the exception type, never feature values or style text |
| 96 | `VectorGlyphRangeBackpressure` | Verbose/VectorTiles | per-provider active glyph-range and waiting-request counts when the 32-load limit is reached; no font, range, URL, or label content |

### Frame-time investigations

Geometry tessellation scratch uses explicitly owned native chunks, grown with
`NativeMemory.Realloc` and freed on upload, completed draw, cancellation, or failure.
This avoids large managed triangle arrays and their delayed GC reclamation. Short-lived
line projection arrays still use the bounded managed pool. Event 90 counts requested
native capacities: successful growth counts the new capacity as allocated and the old
capacity as released, even if realloc grows in place. These are not OS commit counters.
Counters cover all renderers and workers in the process; do not sum them across frames
or renderers. At quiescence their difference is outstanding native scratch payload.
Concurrent worker updates can straddle the two counter reads. Pair with events 63/83
and OS allocation tracing to distinguish live preparation, GC commitment, and driver memory.

Traffic roads share one reusable, surface-sized composite target across public traffic
layers. Event 88 reports its retained bytes, not a new allocation per frame or per layer.
At 1x sampling it retains `width * height * 4` bytes; at 4x it retains five times that
(multisample target plus resolved texture). It is recreated on size/sample changes and
released when no eligible traffic composite is drawn or when device resources are released.
Managed allocation measurements do not include these native GPU resources.

Event **87**, `AzureOverlayRefresh` (Informational/Tiles), records counts of visible live
traffic, radar, and infrared layers considered at a cadence boundary. It contains no source
IDs, timestamps, credentials, or service data. Correlate with generation changes in events
15/30 and request waves to distinguish live refresh from camera work. Built-in Azure layers
are suppressed under `MapStyle.Blank`; fixed weather timestamps do not auto-advance.
Incident point icons use a shared local 32-DIP, 2x sprite atlas selected by numeric category.
Warning triangles are red with white pictograms for major severity (3), otherwise yellow
with dark pictograms.
Events 49 and 51 report sprite preparation/rendering through the existing pipeline; unknown
categories use a warning pictogram. No category values, descriptions, or icon content are
added to events. Marker picking follows the triangular badge bounds; road picking retains its
8-DIP tolerance.
Traffic flow and incidents both use the vector pipeline: correlate commits (49) with
line draws (56) and incident symbol draws (51). Weather uses raster upload events.
Incident descriptions, identifiers, and click-detail text must never appear in ETW.

ID 84 distinguishes bytes copied for small symbol batches from full-buffer discards.
Repeated discards can cause driver backing-allocation churn much larger than the copied
payload. `bufferBytes` is one logical buffer's capacity, not a measurement of driver
residency; `discardCount * bufferBytes` describes the capacity discarded, not guaranteed
physical allocations. Its counters, timing and payload calculations require active
Verbose/Frames listeners.
Icons, vector sprites, glyph bodies and halos share an append/no-overwrite instance
stream. Its cursor persists across frames and discards only when the next batch will not
fit, or after resource recreation or an upload failure. Draws use the reserved instance
offset without changing layer order or batch boundaries.

For route numbers overflowing shields, correlate icon and glyph draws (IDs 51/53)
with fitted-icon counts (ID 69) and text scaling (ID 71). Fitted sprites use their
declared `content` rectangle, not the full image, and honor `textFitWidth` /
`textFitHeight` aspect-ratio rules. Content offsets and borders scale with the
sprite, including fractional pixel ratios and overzoomed source tiles. A synthetic
overzoom reproduction drew one icon and three glyphs with zero evaluation failures,
but 472 text pixels outside the content rectangle before content-aware fitting.
IDs 51/53 distinguish missing assets from this sizing defect; no sprite names,
route numbers, content rectangles, or pixel data belong in ETW.

For gaps at thick line bends, correlate ID 56 with ID 61 to distinguish streamed
and retained vector geometry. Miter joins include both the center wedge and the
outer tip; the configured miter limit still falls back to a bevel. Round joins
in vector and map-element strokes subdivide according to stroke radius, bounding
the arc's chord error to half a logical pixel. Triangle/upload counts can therefore
increase for wide strokes without indicating duplicate features or tile requests.

Line layers also stroke polygon exterior/interior rings. These paths are included in
IDs 56/66 (solid/dashed lines) or 51/60 (patterned lines), not only polygon event 58.
Ring coordinates remain shared with decoded geometry; closure is added to pooled
projection scratch, including background preparation and retained/pending rendering.
Buffered polygon closure edges are excluded by clipping original ring segments to
their source tile; they must not become straight outline seams across the map.

Azure base vector styles use a 512-pixel tile world while the public camera uses 256.
At camera zoom 10, source acquisition and style evaluation use zoom 9. Events 9/10
retain camera-zoom semantics; tile coordinates/tiers in 30/49 use source zoom.
Style visibility, paint, label selection, accessibility, and cache-reuse checks all
convert display zoom at the style-assets boundary; texture preparation already receives
source zoom and must not subtract again. Custom sources and public camera zoom are unchanged.
Hybrid satellite imagery retains its resolution by stitching 256-pixel descendants
through the existing acquisition helper. Terrain opacity uses the converted style zoom.
Azure base requests preserve the style's approved generation parameters (`og`, `cstl`,
`sv`, `jp`, `st`), not just its revision. Missing parameters can change available road
geometry at the same source zoom. These values and the source URL are not logged.
`MapStyle.Road` has a second hidden raster acquisition snapshot for road details, sharing
the existing scheduler, upload limits, cache, generations, and disposal. IDs 30/31/43/45
cover that source; it is not a public layer or a second pipeline. It is visible at native
camera zooms [5,14) and rounds source zoom using 256-pixel tiles: camera zoom 10
requests raster zoom 10, independently of vector/style zoom 9. Do not infer its
loaded tile size from the web SDK's initial 512-pixel default before source metadata
has loaded; verify actual requests. Half-zoom crossings publish scene updates (ID 10)
even if the ordinary base tile coverage is unchanged, so rounded raster requests do not
wait for the next integer zoom or pan. Raster coverage is partitioned so retained parent and child
tiles do not double-blend. Its style-position draw is included in polygon-stage time
(78), like terrain, and stays below vector roads and labels. ID 75 no longer counts
this supported Road raster as unsupported; other embedded raster sources remain reported.
Road-detail PNGs are decoded and filtered as premultiplied alpha, with matching blend
state and opacity scaling. Straight-alpha filtering against transparent black creates
dark fringes even when tile zoom and downloaded pixels are otherwise correct. Existing
terrain and ordinary raster alpha contracts are unchanged. Road detail requests explicitly
include the metadata template's `tileSize=256`; the live regression compares decoded
pixels with a request expanded from that template without logging either request.

ID 83 distinguishes cache-owned vector payloads from retained native frame geometry.
Its payload calculations only run with active Verbose/Frames listeners; disposal-only
counts and byte totals likewise require active Informational/Device or Cache listeners.
It does not report a whole-process heap or GPU residency: array/object headers, referenced
style/glyph/sprite assets, pooled scratch, and obsolete worker-owned inputs are excluded.
Event 83 retains its historical decoded/symbol/index payload meanings. Event 92 reports
the more complete budget estimate: decoded array/object/index storage and reference-deduplicated
strings/geometry, plus resolved immutable line/polygon/symbol arrays, paint arrays, filter
decisions, projection indices and accessibility data. The unchanged 32 MiB CPU soft budget
now charges those ownership estimates; visible/fallback protection and hybrid eviction
coupling remain unchanged. Offscreen tiles can therefore be evicted earlier. Headers and
dictionary entries use conservative 64-bit estimates, not exact heap sizes. Totals are
cached when decoded data or derived results are created/replaced, not by walking feature
graphs each frame. Shared style/sprite/glyph assets and worker-retained obsolete snapshots
are not charged as tile-owned storage. Pair with IDs 41/42 for texture disposal and
upload backlog and ID 76 for dormant scratch release. Geometry preparation has one running
owner; newer requests cancel obsolete same-layer work and capture the latest scene only
after that owner releases its input/device. Other layers wait without canceling useful work.

Pending unpatterned geometry is retained by the existing frame-cache owner, not a new
scheduler. Each owner admits at most its main frame's geometry byte capacity, retaining only
useful visible pending instances. Above that bound it uses the existing streamed path.
Event 91's `retainedBytes` is the owner's total, not additional bytes per event; never sum
it across tile instances. Fade/layer opacity is applied at draw time, and wrapped instances
retain independent transforms. Content changes, pan validity, configuration/device changes,
incorporation, removal and disposal release pending buffers. Patterned fills remain on the
complete rendering path, preserving pattern and relief order. Event 92 separates pending
GPU bytes from both CPU budget estimates and event 83's main-frame GPU bytes.
Stable eligible pending frames should show reuse in 91 and no new dynamic uploads in 80
or native scratch growth in 90. Style builders pool only temporary storage (at most two
arrays per bucket through 1024 elements per styled-record type); published result arrays
remain immutable, including snapshots held by a cancelled worker.

The deterministic `VectorAllocationTests` workload (x64 Debug, .NET 10) measured
3,289,400 versus 2,462,520 managed bytes for forty resolutions of 512 zoom-dependent
lines after adopting compact immutable results and bounded builders (25.1% less).
Twelve identical eligible pending frames previously requested 294,624 native scratch
bytes; the retained path requests zero and performs zero dynamic geometry uploads.
These are synthetic allocation/regression measurements, not Native AOT process-memory
or displayed-FPS claims. `VectorPendingGeometryTests` compares complete and incremental
pixels for flat/projective panning, wrapping, fading and newly arrived patterns.

Navigation allocation traces can also expose unnecessary symbol invalidation and LibTess
scratch churn. Symbol reuse depends on line patterns/opacity and visibility/filter diagnostics,
not unrelated solid-line paint. All style families reuse results while hidden at both zooms,
within unchanged literal zoom-step intervals, or beyond interpolation endpoints. Continuous
interpolation, visibility crossings, evaluation failures, missing assets and text scaling still
invalidate as needed; no zoom quantization or label reduction is applied.

For zoom-heavy label stalls, compare symbol-stage time (78), glyph/failure counts (53),
and GC CPU samples. Each tile lazily retains text-layer layouts independently: changing
one layer no longer reshapes every label. Uniform, feature-independent text-size changes
rescale the original layout when all other dependencies remain reusable. Wrapping is
compared in glyph-em units; pixel spacing/padding remain unchanged and SDF halo widths
are renormalized from their original values. Feature-dependent or otherwise incompatible
changes still reevaluate. Missing-glyph layouts retry when the atlas revision changes,
including at an unchanged camera zoom; unchanged failures retain their diagnostic counts.
The cache does not mutate published symbol arrays, and its retained payload contributes
to the existing tile CPU budget and event 92. Hidden layers release their saved symbols;
styles without text allocate no text-layer cache.

An ARM64 Release/CoreCLR tilted Liberty replay used eight wheel inputs, a matched
1921-by-1162 physical map viewport, and provider mask `0x542` at Verbose. The original
render mean/median/p95 were 92.55/107.68/166.45 ms. Two optimized replays measured
64.51/53.07/157.46 and 51.13/42.90/124.50 ms; mean symbol work fell from 75.25 ms
to 40.19 and 32.37 ms. All captures had zero lost events and no renderer/pipeline errors.
Asynchronous tile arrivals and GC differ between replays, and long frames remain:
these are CPU-side render-pass observations, not GPU timings or displayed-FPS guarantees.

Polygon decoding reuses one LibTess tessellator per decode call. Its scratch pool is never
shared between workers or retained by cached tiles, and published ring/triangle arrays remain
independently owned. An ARM64 Debug/.NET 10 workload decoding 256 rectangles eight times
allocated about 3.34 MB instead of 15.40 MB (78% less). Removing gradient-scaling closures
reduced 10,000 ordinary line preparations from 240,000 bytes to zero and the 40-resolution
line workload from about 2.46 MB to 1.97 MB. A 256-symbol fractional-zoom workload with
changing solid-road widths dropped from 7.35 MB to 3,520 bytes; its regression limit is 64 KB.
These are deterministic managed-allocation measurements, not reductions in whole-process
working set.

For retained process memory, distinguish `System.Runtime`'s
`dotnet.gc.last_collection.memory.committed_size` from live heap payload and fragmentation.
Windows `GPU Process Memory` counters separately expose shared/dedicated residency and total
graphics commitment. These are not interchangeable with logical resource payloads in events
81/83/92, and must not simply be added to process-private memory because accounting can overlap.
Balanced event 90 counters and an empty event 41 disposal backlog do not prove that the GPU
driver or native allocator has returned its high-water commitment.

A subsequent ARM64 Release/CoreCLR navigation capture retained the same 1899-by-1128,
4x-MSAA viewport and recorded no lost events. Raster residency stayed around 32-35 MiB,
with no disposal backlog, but geometry streaming still submitted 13.35 GiB cumulatively
and performed 16,136 discards over 2,242 frames. Settled graphics commitment was about
412 MiB; GC commitment was about 130 MiB, including about 52 MiB of heap fragmentation.
These overlapping counters are not an additive ownership breakdown. This manual replay
was shorter than the preceding run and did not establish a whole-process memory reduction.
Investigate remaining geometry uploads and driver commitment rather than interpreting
cumulative upload bytes as leaked raster textures or assuming reduced allocations alone
will lower working set.

For polygon flashes during replacement, correlate raster eviction (20) with polygon counts
(58), fallback suppression (59), and source activation (30). A road-raster eviction was
incorrectly intersecting every source's fallback tiers with raster texture keys, clearing
vector-only coverage. One recorded sequence drew 367 polygons, evicted three raster tiles,
drew only the background, then restored 843 polygons after scene activation. Raster eviction
now reconciles only raster-backed sources; hybrid raster/vector eviction remains coupled.
An offscreen regression forces actual raster eviction and verifies unchanged polygon/line
pixels across subsequent frames, including pitched views, without waiting for the scheduler
to restore fallback. Use event 20's eviction count for this correlation, not its older
malformed native byte payloads.

Aggressive cross-tier reversals can project coarse dashed fallback paths millions of
pixels beyond the viewport. Dash generation skips whole pattern periods outside padded
stroke coverage, preserving phase through source segments and keeping split caps/joins
offscreen. Event 93 counts skipped spans, not skipped pixels or individual dashes; it is
cumulative across renderers/workers and only advances while Verbose/Frames is enabled.
Do not sum its values across frames. Correlate with line-stage timing (78), dashed
geometry (66), fallback coverage (57/59), and cancellation (15/63).
An aggressive native-AOT replay spanning zoom 4.88–18.88 exposed a 39.7-second
line-stage stall in the old offscreen dash loop. Whole-period skipping reduced the
same synthetic long-path expansion from 584.760 ms to about 0.225 ms, with matching
visible vertices across cap and odd/zero-entry pattern cases. The repeated four-cycle
native replay recorded a 95.7 ms worst line stage, 95 cancellation events, and no lost
events; asynchronous arrivals differed, so these are regression evidence, not a
normalized throughput comparison.

ID 41 writes explicit Int32/Int64/Int32 payload widths. Older builds used an overload
that could produce inflated byte totals in native traces despite correct EventListener
values; do not use those older byte totals as evidence of actual native allocations.

Enable `WinUIEx-Maps-Rendering:0x400:5` to collect frame timings without enabling
verbose tile/symbol events. Join IDs 77 and 78 by `rendererId` and `frameId`.
These are process-local numeric identifiers, not public map or layer IDs. Offscreen
benchmark rendering can emit ID 78, but never ID 77 because it does not Present.

ID 77 measures a render pass after the render thread wakes; it excludes the idle wait
for a render request. `renderMilliseconds` includes CPU-side D3D submission and any
driver stalls, not a GPU timestamp-query duration. `presentMilliseconds` includes
vsync/driver waits. ID 78 excludes readback and presentation; `otherMilliseconds`
includes accessibility, map elements, cache maintenance, and pipeline setup.

Join ID 80 by the same renderer/frame identifiers to isolate dynamic vertex uploads
from line/polygon tessellation and draw submission. `uploadMilliseconds` measures the
combined native Map, memory copy, and Unmap calls for successful streamed geometry
uploads (including map elements and patterned polygons), not GPU execution or immutable
geometry-buffer creation. `uploadCount = discardCount + noOverwriteCount`; `byteCount`
is the total copied bytes, not retained GPU memory. The two fixed-capacity dynamic
vertex buffers append without overwriting previous draws until a wrap discards the
allocation. Their independent cursors persist across frames, so a frame can have zero
discards, or no dynamic uploads when immutable frame caches are reused. ID 80 is emitted
once per completed map frame alongside ID 78, including offscreen rendering, and adds no
per-upload timestamps when Frames tracing is disabled. Compare upload duration against
IDs 77/78 before attributing a long render pass to driver mapping stalls; this aggregate
does not distinguish Map from copy or Unmap.

Do not infer displayed FPS from event counts divided by trace length or from idle
gaps between events. Correlate with presentation/DWM tracing for actual refresh
deadlines and with input timestamps for input-to-display latency. Compare tracing
off versus on before attributing small changes to production work.

Long synthetic gestures must use time-scaled input steps: a fixed 20-step gesture
spread across six seconds supplies input only every 300 ms and is not a continuous
60 Hz navigation workload. The repository touch injector scales steps to the
requested duration. Confirm effective input/render cadence, and use native input
tests for repeatable pan/zoom while loading continues.
`CameraTargetChanged` is rate-limited to one event per 100 ms; its event count is
not an input-event counter. Use injector timestamps or input tracing for input cadence.

Scheduler waves are publication cohorts, not barriers: a newer scene can start work
in a freed slot while useful requests from an older scene remain in flight. Pair
wave Start/Stop and scheduler summaries by generation and scene version, allowing
overlapping intervals. Shared request/upload limits still apply across cohorts and
sources. ID 79 distinguishes unexpected acquisition failures from renderer admission
failures; it does not replace existing request-failure events or prove a network cause.

An ID 79 investigation traced `E_CHANGED_STATE` (`0x8000000C`) thrown synchronously
by WinRT `SendRequestAsync` to populated shared `DefaultRequestHeaders`. Azure and
custom requests now set headers on each request, preserving shared-client caching and
concurrency. The reproduction recorded five errors before the correction and zero
across 172 requests in a three-route replay afterward. For recurrence, inspect request
admission and header ownership before assuming a network failure; keep header values
and request URLs out of traces.

## Reproduce and interpret

For incomplete world views after leaving a detailed city, correlate acquisition failures
(37/79) with glyph loads (52) and commits (49), rather than assuming extrusion preparation
is stalled. World labels can need more than 32 font ranges concurrently. Both providers
now wait for capacity in their shared in-flight deduplication helper instead of failing
tiles with `InvalidDataException` at that limit. Event 96 reports backpressure; the
32-active-load limit is unchanged. Canceled waiters leave promptly without canceling
another tile's shared load, and failures/completions release capacity. Completed ranges
remain owned by the bounded atlas, not by the request helper.
Custom source selection also clamps its physical level to zero before applying explicit
source limits: 512-pixel tiles must not select level -1 and deactivate below camera zoom 1.
A positive `MinSourceZoom` still suppresses acquisition below that configured level.
An ARM64 Release/CoreCLR reproduction (`0x5DF`, Verbose, 1286-by-874 physical viewport)
recorded three acquisition-stage `InvalidDataException` failures while flattening Seattle
and zooming to level 2. A fresh-source replay after the fix recorded 28 backpressure events,
all four required tiles committed, no failures, and zero lost events, without further input.
That replay's four-tile wave took 217 ms with warm HTTP assets; a separate cold-asset
level-zero wave took about 10 seconds but completed automatically. Do not interpret the
warm result as a guaranteed network latency. In packaged comparisons, verify the deployed
library hash: an existing `AppX` staging directory can contain an older DLL than the
just-built project output.

- **Pan/zoom/rotate/tilt:** collect Camera+Tiles+Cache (`0x1C`) at Informational. Use
  `CameraTargetChanged`, `CameraHeadingTargetChanged`, and `CameraPitchTargetChanged` as the intent,
  `TileWaveStart/Stop` as the work, and generation plus sceneVersion as correlation.
  Repeated cancellations without camera/style changes suggest a scheduling regression.
  A Bow request (ID 70) that zooms out to a viewport containing the displayed source center
  keeps Bow easing but omits the additional outward arc. Containment uses the target
  heading, pitch, and wrapped longitude; such transitions must not undershoot target zoom.
- **Missing/slow tiles:** filter IDs 11–18, 31, and 43–46. Compare scheduler duration and counts. HTTP status is
  present for service failures; `failureKind` distinguishes `ServiceResponse`, `Network`,
  and `Decode`. A completed request followed by upload failure localizes the issue to D3D.
  Compare IDs 17, 31, and 44 to distinguish texture creation, render-lock wait, and final
  cache acceptance. ID 45 directly reports first, complete, and opaque coverage.
- **Custom tile sources:** select CustomTiles+Cache+Errors (`0xD0`) and inspect IDs 33–40.
  These events classify custom-source behavior inside the same scheduler, bounded upload
  queue, GPU cache, fallback selector, and render path used by Azure; they do not indicate
  a second pipeline. Correlate by generation (never by a user-provided layer ID). IDs 35/36
  distinguish scheduling/network latency, ID 37 is sanitized HTTP/template/decode failure,
  and IDs 38/39 localize upload or stale-generation behavior. `MapStyle.Blank` removes the
  hidden Azure `TileLayer`, so it should produce no Azure tile/attribution work while custom
  IDs continue.
- **Vector tiles:** select Tiles+VectorTiles (`0x108`) and correlate IDs 11–17, 43–46,
  49–69, and 76. ID 49 confirms that MVT responses reached generation-checked CPU cache commit,
  ID 50 distinguishes asset acquisition from tile decode and reports explicitly unsupported
  style-layer counts. The `style` payload is `-1` for a custom vector source and a
  `MapStyle` value for Azure. ID 52 reports glyph-range latency, ID 54 reports definitive unavailable
  ranges without font or label content, verbose IDs 51/53 summarize point-symbol and
  point-label batching, verbose ID 55 quantifies collision suppression, and verbose ID 56
  reports direct line geometry generation and drawing, while ID 57 identifies replaced
  fallback coverage or distant finer-level suppression during zoom-out (not distant coarser
  coverage during zoom-in), ID 58 summarizes polygon fills, and ID 59
  quantifies polygon fallback coverage and line crossfading, ID 60 reports line-following
  symbol placement, and ID 61
  distinguishes geometry rebuilds from flat or pitched projective frame reuse, while ID 62 confirms
  that tile arrivals were handled incrementally instead of forcing an in-motion rebuild, and ID 63
  separates background geometry preparation from immutable GPU-buffer creation. ID 64 confirms
  labels are withheld as complete groups while glyph textures are still uploading, ID 65
  confirms those complete groups fade after becoming ready, and ID 66 distinguishes dashed
  geometry from sprite-patterned line placement without exposing pattern names, and ID 67
  reports patterned polygon and explicit outline geometry, and ID 68 reports advanced line
  styling usage, and ID 69 reports advanced symbol styling and collision-control usage. None exposes source-layer names, properties, sprite names, URLs, or service
  content. ID 75 identifies unsupported Style Spec construct categories and counts without
  exposing layer IDs, source-layer names, or property values. ID 76 confirms renderer-owned
  managed symbol working capacities were dropped during dormant-resource release. The
  compatibility event's `issueKind` is `1` for
  layer types, `2` for unsupported layout properties, `3` for paint properties, and `4`
  for intentionally ignored layout properties, and `5` for skipped icon/text/line/fill
  components with a fixed parser-enum reason (for example `UnsupportedExpression` or
  `UnsupportedSymbolPlacement`). Kind 5 uses the already parsed style, not a second parse,
  and never includes source-layer names, expression text, or feature data.
  An OpenFreeMap Liberty reproduction loaded 92 components and skipped 29 (ID 50);
  ID 75 originally reported only one raster and one fill-extrusion layer, hiding the
  comparison/placement parsing gaps. Modern `!=`, `<`, `<=`, `>`, and `>=` expressions
  and legacy relational filters now resolve without dropping water, roads, boundaries,
  POIs, and city labels. Zoom-dependent point/line placement participates in glyph
  preparation, cache invalidation, alignment, and accessibility; point placement on
  a line uses its length midpoint. Existing IDs 50/51/53/56/58 expose supported counts,
  evaluation failures, and actual draws. Liberty also uses shorthand hex colors throughout
  road interiors and labels: `#RGB` and `#RGBA` expand with the same premultiplied-alpha
  contract as `#RRGGBB` and `#RRGGBBAA`, including interpolated colors. A successful parse
  alone does not prove correct styling; check per-frame evaluation failures as well.
  The hosted boundary style's redundant `["linear", 1]` is accepted as linear
  interpolation; other extra bases remain unsupported.
  With comparison/placement/color fixes the live style loads 121 components with zero
  parser skips (ID 50). Classic extrusion adds the building layer for 122 components.
  ID 75 still reports unsupported raster relief. Liberty's `render_height` and
  `render_min_height` provide top/base elevations in meters, not additive heights.
  Extrusions use separate lazy meshes, depth-tested roofs/walls, root style lighting,
  and optional repeating patterns. Layer opacity is applied once after visible surfaces
  have been resolved, preventing overlapping walls from accumulating opacity.
  IDs 94/95 expose geometry, native target/mesh ownership, and evaluation/preparation
  failures. Mesh scratch also contributes to ID 90. Dedicated resources are created
  only for drawable extrusions and released when sources become ineligible or are removed.
  Elevated acquisition coverage includes the camera footprint so tall offscreen buildings
  are not culled solely by their ground position. The existing scheduler and preparation
  owner remain shared with ordinary tiles. Labels/icons remain overlays; no terrain,
  shadows, modern material effects, or timed paint/pattern transitions are implemented.
  Kinds 6/7 remain reserved for compatibility with older traces, not emitted for current
  supported extrusion properties.
  Kind `4`, construct `symbol-avoid-edges`,
  confirms the renderer allows complete symbols across tile boundaries and relies on
  viewport-wide collision handling instead of tile-local edge rejection.
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
  For startup latency, compare ID 85 with the first drawable ID 26 and inspect ID 86.
  Map-element textures receive priority over queued vector sprites/glyphs within the same
  bounded upload worker, allowing one vector texture after eight map-element uploads.
  Each pass retains the device once and participates in the render-lock handoff used by
  raster uploads; it must not reacquire the render lock for every glyph.
  ID 85 identifies fully transparent captures even when upload and draw succeed. An
  unchanged icon must retain its texture when another layer is reset; legitimate recaptures
  wait for the reattached XAML capture root to load and reach a XAML rendering pass before
  capturing it. `Loaded` alone does not guarantee that an ImageIcon's image visual is ready.
  Texture batches are per layer, so the same texture used in two layers contributes two
  batches in ID 26; layers render from the first (bottom-most) to the last (top-most).
  `ImageIcon` SVG/bitmap load completion invalidates its raster and produces another
  ID 22/25 update/upload. A successful initial upload can still contain blank pixels if
  the image has not loaded yet; confirm the completion upload before blaming drawing.
- **Device/blank surface:** use Lifecycle+Device+Errors (`0x43`). Pair IDs 5/6, then look for
  ID 8 and HRESULT. Verify resource release and recreation around unload/resume.

An unloaded map retains resources while its owner keeps it available for reuse. Tab
switches and remove/reinsert operations must not time out and discard those caches.
Full XamlRoot teardown still releases dormant resources. The sample viewer explicitly
collects retired samples after top-level sample navigation and unload processing finish;
this is a sample-host policy, not a control timeout. Switching tabs or removing a map
within the same sample does not trigger that host collection. Account for those forced
collections when profiling sample-to-sample navigation.

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
