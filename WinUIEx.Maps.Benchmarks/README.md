# WinUIEx.Maps benchmarks

The benchmark suite measures both execution time and managed allocation with
BenchmarkDotNet:

- `VectorTileParsingBenchmarks` decodes embedded Azure Maps `microsoft.base` PBF fixtures
  captured over urban areas at zoom levels 10, 12, 14, and 16.
- `GpuUploadBenchmarks` creates immutable BGRA textures and shader-resource views, then waits
  for an event query proving that the GPU completed the upload.
- `VectorTileUploadBenchmarks` starts with an already parsed real-world tile, resolves its
  line and polygon styles, prepares projected triangles, creates immutable GPU vertex
  buffers, and waits for GPU completion.
- `RenderFrameBenchmarks` renders a populated 1024x768 raster viewport into an offscreen
  texture and waits for GPU completion. It does not create or present a swap chain, so its
  measurements are not bounded by display refresh or vsync.
- `MapStrokeTessellationBenchmarks` compares segment-only and adaptive round tessellation
  for representative polygon, acute-corner, and dense polyline strokes.
- `VectorRenderFrameBenchmarks` renders a populated 1024x768 vector viewport from retained
  GPU line and polygon geometry through the same offscreen, GPU-completed path.
  `Samples=1` is the production default; `Samples=4` measures an experimental offscreen
  multisampled color target and one resolve per frame. Unsupported four-sample cases fail
  setup explicitly instead of reporting single-sample timings as MSAA results.
  Color-target storage is 3 MiB versus 15 MiB at this viewport, excluding driver overhead.
  Filter by fixture, pitch and samples to avoid running the whole parameter product.
- `VectorSymbolResolutionBenchmarks` generates deterministic point features, glyphs, and a
  sprite atlas, then measures production text/icon style resolution at two label densities.
- `VectorSymbolUploadBenchmarks` uploads the generated glyph and sprite textures and waits
  for GPU completion.
- `VectorSymbolRenderFrameBenchmarks` measures collision, batching, halo/tint shading, and
  offscreen rendering of generated text and icon symbols without presentation. Separate
  methods measure a retained steady-state frame and a frame after a small camera change.

Run from the repository root for the current architecture:

```powershell
dotnet run -c Release --project .\WinUIEx.Maps.Benchmarks\WinUIEx.Maps.Benchmarks.csproj `
  -p:Platform=ARM64 -- --filter "*" --job Short --noOverwrite
```

Use `--job Dry` after changing benchmark setup, then remove the `--job` option for the
default statistically rigorous run. Filter by category with `--anyCategories VectorTiles`,
`Symbols`, `Upload`, or `Rendering`.

For a bounded antialiasing comparison:

```powershell
dotnet run -c Release --project .\WinUIEx.Maps.Benchmarks\WinUIEx.Maps.Benchmarks.csproj `
  -p:Platform=ARM64 -- --filter "*VectorRenderFrameBenchmarks*1920*NewYorkZ14*" --job Short
```

Production presentation now uses supported-device 4x MSAA with a single-sample fallback.
The full-window 1920x1080 NewYorkZ14 ARM64 Snapdragon X Short comparison measured
0.949 versus 1.874 ms for steady frames and 0.986 versus 1.901 ms for camera-changing
frames (1x versus 4x). At pitch 60 these were 1.827/2.823 and 2.032/2.909 ms.
This approximately 0.9–1.0 ms absolute cost is not proof of bad interactive performance.
Fractional-zoom geometry invalidation was already expensive: 21.02/22.97 ms at pitch 0
and 72.86/70.22 ms at pitch 60, with large uncertainty. These are offscreen
CPU-plus-GPU-completion means, not GPU timestamps, presentation deadlines or interactive
p95 measurements; the original 10% p95 goal is not established.
Steady managed allocations remain 1 KB (1.04 KB at pitch 60).

`AnalyticCoverageBenchmarks` isolates hard versus derivative-based capsule coverage in the
existing instanced symbol pipeline. It deliberately substitutes an exact procedural shape
for the glyph mask; it does **not** implement antialiasing for arbitrary retained vector
meshes. Offscreen quality tests also exercise a rotated convex polygon and emit a local
Canvas2D capsule reference with matching coordinates and DPR 1.

On the same Snapdragon X, the initial Short run measured 301.9 versus 308.8 us; the
default run measured 306.4 versus 300.5 us with overlapping uncertainty (hard versus
analytic), both approximately 1 KB managed allocation and the same 3 MiB color target.
Do not interpret that variation as an analytic speedup or a proven interaction p95 budget.
The matched Canvas fixture's mean absolute intensity error over the common 489-pixel
active mask fell from 19.33 to 10.69 (8-bit intensity, 44.7% lower), with 208
intermediate-coverage pixels instead of zero.

The analytic experiment is promising but remains offscreen-only: independent capsules
are not equivalent to connected, translucent stroked paths, and the current triangle
cache does not carry original exterior-boundary attributes. Enabling it indiscriminately
would change joins, polygon holes/internal edges, tile seams, and patterned strokes.
Production instead uses MSAA for general geometry without inferring exterior boundaries
or adding analytic fringes. The bounded analytic boundary experiment is not wired into
production geometry, avoiding combined antialiasing and altered alpha semantics.

`MemoryDiagnoser` reports managed allocations. Native D3D resources and driver memory are
included in elapsed GPU-completed time but not in the managed `Allocated` column.

The benchmark never downloads data. To deliberately replace the committed fixtures, first
configure `AzureMaps:MapServiceToken` in the test project's user secrets, then run:

```powershell
.\WinUIEx.Maps.Benchmarks\Download-AzureFixtures.ps1
```
