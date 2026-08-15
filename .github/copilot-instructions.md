# WinUIEx.Maps repository instructions

## Architecture and boundaries

- `WinUIEx.Maps/MapControl.cs` owns the WinUI control, dependency properties, input, XAML icon
  rasterization, and publication of immutable icon snapshots.
- `MapControl.Layers` owns ordered `MapLayer` instances; the first layer is bottom-most.
  `MapLayer.IsVisible` and `MapLayer.Opacity` apply to all layer kinds. `TileLayer` exposes
  dependency properties, directly inherits `MapLayer`, and is the single representation of
  every public raster source. It is non-sealed only for internal acquisition specialization;
  there is no public tile-source abstraction. Only immutable `TileLayerSnapshot` and
  `RasterTileAcquisitionSession` values cross to rendering and scheduling; the internal Azure
  session may return raster pixels or decoded MVT point geometry.
  `MapElementsLayer.MapElements` owns observable lightweight elements. Both collections are
  replaceable, reject null/duplicate references, and must transfer subscriptions without
  retaining removed layers or elements.
- `WinUIEx.Maps/Rendering/MapRenderer.*.cs` owns render-thread scene state, D3D resources,
  the source-keyed raster texture cache, vector point cache and GPU point textures, icon
  textures, the heterogeneous render plan, and draw batching. `RasterTileManager` owns the
  one latest-scene scheduler, cancellation, bounded request/decode/upload work, shared
  backpressure, and per-source generations for Azure raster/vector and custom raster
  sessions. Do not add a parallel Azure/custom/vector manager or independent backpressure
  pipeline.
- `MapControl` owns an internal `AzureTileLayer` that is never inserted into or exposed
  through public `Layers`. A non-Blank style creates/configures that hidden layer and its
  immutable Azure acquisition session; `Blank` sets it to null. Snapshot publication
  prepends it below the unchanged public layer plan. Azure style/tileset/authentication,
  raster/vector selection, MVT decoding, maximum-zoom, and attribution behavior stays in
  `AzureTileLayer`/`AzureTileAcquisitionSession`. Vector geometry is decoded on acquisition
  workers, committed to the renderer's bounded source cache, and drawn as GPU-instanced
  point textures without rasterizing vector tiles into image tiles.
- Preserve `MapLayer : DependencyObject` and the lightweight, non-`DependencyObject`
  `MapElement`/`MapIcon` model. Changes may arrive off the UI
  thread, but collection mutation, XAML access, and icon rasterization stay on the UI
  thread. Publish snapshots/aggregate updates across the boundary; do not pass mutable
  XAML objects to render or worker threads. Every `TileLayer` public property is
  UI-thread-only. `TileLayer.CreateSnapshot` is UI-thread-only and must capture all data;
  acquisition-session methods may run concurrently on workers, must be immutable and
  thread-safe, must honor `CancellationToken` promptly, and must never access a
  `TileLayer`, dependency property, or other `DependencyObject`.
- Preserve layer identity/order in immutable render and icon snapshots. Render the Azure
  base map first unless `MapStyle.Blank`, then render `TileLayer` and `MapElementsLayer`
  entries strictly bottom-to-top. Batch shared icon textures within, never across, layer
  boundaries.
- `MapStyle.Blank` must perform no Azure tile or attribution request and must not require
  an Azure token. Custom tile templates accept only HTTP(S); ETW must never contain a
  template, expanded URL, subdomain, public layer ID, or response content.
- Keep D3D access under the renderer's synchronization and keep network/decode work off the
  UI and render threads. Preserve the unified global request/upload backpressure, source
  generations, `CancelAsync`/WinRT cancellation, pending reservations, nearest-covered
  coarser fallback, device epochs, raster GPU/vector CPU cache accounting and eviction, and
  continuous native texture disposal when changing asynchronous tile work.

## Runtime diagnosis and eventing

- Diagnose runtime rendering, performance, tile, cache, icon, and device issues ETW-first
  with the `WinUIEx-Maps-Rendering` EventSource. Follow
  `.github/skills/mapcontrol-etw-diagnostics/SKILL.md`; do not introduce UI error callbacks
  or ad-hoc `Debug.WriteLine`/console logging as a substitute.
- New rendering/performance behavior must add or update an appropriately leveled and
  keyworded event when existing events cannot explain it. Keep existing event IDs stable,
  avoid Informational per-frame/per-item events, guard expensive payload creation with
  `IsEnabled`, and update EventListener tests plus the skill catalog.
- Never log the Azure Maps token, authorization/header values, URLs, query strings,
  response bodies/service text, attribution text, pixel buffers, or other secrets. Prefer
  tile coordinates, style, numeric status/HRESULT, sanitized categories, counts,
  durations, generations, and exception type names.

## Build and tests

- Projects target `net8.0-windows10.0.19041.0` and support x86, x64, and ARM64.
  Never switch WinUI executable projects to AnyCPU.
- Architecture-specific examples use x64; select x86 or ARM64 when validating those
  targets.
- From the repository root, run the unit suite with:
  `dotnet test .\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj --no-restore -p:Platform=x64`.
- Validate the packaged sample with:
  `dotnet build .\MapSample\MapSample.csproj --no-restore -p:Platform=x64`.
  Use the `winui-dev-workflow` `BuildAndRun.ps1`/`winapp run` flow when launch behavior must
  be tested; never execute the packaged `.exe` directly.
- Use `winapp ui` for packaged-app discovery, inspection, navigation, input, and screenshots
  whenever it supports the required operation. Do not use `UIAutomationClient` for work
  that WinApp CLI can perform.
- Before finishing, run `git diff --check`. Add focused unit tests beside the existing
  xUnit tests. Event schema and privacy changes require `EventListener` tests; public API
  removals/additions should have reflection coverage.
- Preserve existing intentional uncommitted work. Do not rewrite unrelated code or commit
  unless explicitly requested.
