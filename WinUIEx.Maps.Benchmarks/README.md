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

`MemoryDiagnoser` reports managed allocations. Native D3D resources and driver memory are
included in elapsed GPU-completed time but not in the managed `Allocated` column.

The benchmark never downloads data. To deliberately replace the committed fixtures, first
configure `AzureMaps:MapServiceToken` in the test project's user secrets, then run:

```powershell
.\WinUIEx.Maps.Benchmarks\Download-AzureFixtures.ps1
```
