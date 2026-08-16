---
name: mapcontrol-benchmarks
description: Run and extend WinUIEx.Maps BenchmarkDotNet tests for vector parsing, symbols, D3D uploads, and offscreen frame rendering with timing and allocation measurements.
---

# MapControl benchmarks

Use this skill when measuring MapControl execution time or managed allocations, validating
a performance optimization, adding a permanent benchmark, or comparing benchmark results.
The project is `WinUIEx.Maps.Benchmarks`.

## Benchmark architecture

- `VectorTileParsingBenchmarks` measures only `VectorTileDecoder.Decode` against embedded
  Azure Maps urban PBF fixtures. Resource loading occurs in `GlobalSetup` and is excluded
  from measurements.
- `GpuUploadBenchmarks` measures immutable BGRA texture creation, shader-resource-view
  creation, GPU completion, and resource release. Device and shader initialization occur in
  `GlobalSetup`.
- `VectorTileUploadBenchmarks` begins with parsed PBF features and measures style resolution,
  projected line/polygon triangle preparation, immutable vertex-buffer creation, GPU
  completion, and resource release. Parsing and device initialization occur in
  `GlobalSetup`.
- `RenderFrameBenchmarks` uses the production `MapRenderer` against an offscreen D3D11 render
  target. Each operation clears, renders, and waits for an event query proving GPU
  completion. It never creates or presents a swap chain, so display refresh and vsync do not
  bound the result.
- `VectorRenderFrameBenchmarks` measures steady-state production vector rendering from
  retained GPU line and polygon caches. Fixture parsing, style creation, initial geometry
  preparation, and cache warmup occur in `GlobalSetup`.
- `VectorSymbolResolutionBenchmarks` measures text/icon style resolution and label
  construction using deterministic generated point features, glyph bitmaps, and a sprite
  atlas at two symbol densities.
- `VectorSymbolUploadBenchmarks` measures immutable upload and GPU completion for the
  generated glyph and sprite texture set.
- `VectorSymbolRenderFrameBenchmarks` measures steady-state collision, texture batching,
  halo/tint shading, and offscreen rendering of generated glyph and icon symbols. It
  includes both retained steady-state frames and camera-changing frames that force
  projection/collision preparation. Asset generation, texture upload, and warmup occur in
  `GlobalSetup`.
- `[MemoryDiagnoser]` reports managed allocations. D3D resources and driver allocations are
  native memory and do not appear in the `Allocated` column.

The benchmark executable and every BenchmarkDotNet-generated child process must run in the
machine architecture. The benchmark configuration forwards ARM64 or x64 into MSBuild.
Never use AnyCPU for this project.

## Running benchmarks

Build Release first:

```powershell
dotnet build .\WinUIEx.Maps.Benchmarks\WinUIEx.Maps.Benchmarks.csproj `
  -c Release -p:Platform=ARM64
```

Replace `ARM64` with `x64` on an x64 machine. List available benchmarks without executing:

```powershell
dotnet run -c Release --project `
  .\WinUIEx.Maps.Benchmarks\WinUIEx.Maps.Benchmarks.csproj `
  --no-build -p:Platform=ARM64 -- --list flat
```

Always validate changed setup with a dry run:

```powershell
dotnet run -c Release --project `
  .\WinUIEx.Maps.Benchmarks\WinUIEx.Maps.Benchmarks.csproj `
  --no-build -p:Platform=ARM64 -- `
  --filter "*" --job Dry --noOverwrite
```

Use `--job Short` while iterating. Omit `--job` for the final default run. Redirect verbose
console output to a file and inspect the generated `*-report-github.md` result:

```powershell
dotnet run -c Release --project `
  .\WinUIEx.Maps.Benchmarks\WinUIEx.Maps.Benchmarks.csproj `
  --no-build -p:Platform=ARM64 -- `
  --filter "*VectorTileParsingBenchmarks*" --job Short --noOverwrite `
  *> .\benchmark.log
```

Filter groups with `--anyCategories VectorTiles`, `Symbols`, `Upload`, or `Rendering`. The
`VectorTiles` category includes parsing, preparation/upload, and frame rendering. Every
benchmark must use `--filter` in automated runs so `BenchmarkSwitcher` never opens its
interactive selector.

## Adding benchmarks

1. Identify the comparison axis: before/after change, alternative implementations, input
   scale, runtime, or hardware.
2. Keep fixture creation, network access, D3D device creation, shader compilation, and
   initial warmup outside the measured method.
3. Return a result or perform an observable native operation so the JIT cannot eliminate
   work.
4. Do not add manual repetition loops; BenchmarkDotNet controls invocation count.
5. Add `[MemoryDiagnoser]`, `[RankColumn]`, and meaningful `BenchmarkCategory` values.
6. Keep parameter combinations intentional. Each method and parameter combination is a
   separate benchmark case.
7. Run `--job Dry`, then one representative `--job Short` case before running the full set.

CPU fixtures must be deterministic and immutable during measurement. Synthetic symbol
fixtures should generate fixed feature coordinates, sprite pixels, glyph SDF buffers, and
style inputs in `GlobalSetup`; do not use platform fonts or live asset endpoints. Real-world
downloaded fixtures belong under `WinUIEx.Maps.Benchmarks\Fixtures`, are downloaded only by
an explicit maintenance command, and must never be fetched during benchmark setup or
execution.
Replace the Azure fixtures only when explicitly requested:

```powershell
.\WinUIEx.Maps.Benchmarks\Download-AzureFixtures.ps1
```

The script reads `AzureMaps:MapServiceToken` from the test project's user secrets. Never
place the key in source, fixture metadata, command output, or BenchmarkDotNet parameters.

GPU benchmarks must wait for actual device completion rather than measuring only command
submission. Render benchmarks must use the offscreen path and must not call `Present`, attach
a `SwapChainPanel`, depend on a UI thread, or include vsync.

## Interpreting results

Compare results from the same machine, architecture, power mode, runtime, build
configuration, and fixture set. Treat absolute numbers from different machines as separate
baselines. Check `Error` and `StdDev` before drawing conclusions from small differences.

Use `Mean` for execution time and `Allocated` for managed bytes per operation. GPU upload
and rendering results include the event-query wait, so they represent completed GPU work.
For driver-memory or detailed GPU pipeline diagnosis, use ETW in addition to BenchmarkDotNet.
