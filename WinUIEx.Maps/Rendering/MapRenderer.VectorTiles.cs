using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Numerics;
using WinUIEx.Maps.Rendering.Diagnostics;
using static WinUIEx.Maps.Rendering.DirectXInterop;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Commits decoded Azure vector tiles and renders style-resolved sprite symbols as
/// GPU-instanced textured quads.
/// </summary>
internal sealed partial class MapRenderer
{
    private const long MaximumVectorCacheBytes = 32 * 1024 * 1024;
    private readonly ConcurrentQueue<QueuedVectorTile> _completedVectorTiles = new();
    private readonly Dictionary<RasterTileKey, VectorTileCacheEntry> _vectorTiles = [];
    private readonly Dictionary<long, PreparedVectorSymbolFrame>
        _vectorSymbolFrameCaches = [];
    private readonly VectorSymbolRenderWorkspace _vectorSymbolWorkspace = new();
    private readonly List<MapAccessibilityFeature> _vectorAccessibilityFeatures = [];
    private readonly Dictionary<string, MapAccessibilityFeature>
        _uniqueVectorAccessibilityFeatures =
            new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MapAccessibilityFeature>
        _orderedVectorAccessibilityFeatures = [];
    private readonly AccessibilityFeatureComparer
        _accessibilityFeatureComparer = new();
    private readonly string?[] _lastAccessibilityFeatureNames = new string?[8];
    private int _lastAccessibilityFeatureCount;
    private VectorAccessibilityCacheKey? _lastVectorAccessibilityCacheKey;
    private long _vectorTileVersion;
    private long _iconTextureVersion;
    private long _vectorSymbolFrameBuildCount;

    internal long VectorSymbolFrameBuildCountForBenchmark =>
        _vectorSymbolFrameBuildCount;


    /// <summary>
    /// Reserves shared tile-pipeline capacity, registers referenced sprite crops with the
    /// icon upload pipeline, and queues decoded features for generation-checked commit.
    /// </summary>
    public async Task<bool> QueueVectorTileAsync(
        VectorTileData tile,
        CancellationToken cancellationToken)
    {
        await _pendingRasterUploadCapacity.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        long reservation;
        lock (RenderLock)
        {
            reservation = _pendingRasterTiles.TryReserve(tile.Key);
            if (!_rasterLayers.TryGetValue(
                    tile.Key.SourceId,
                    out RasterLayerState? state) ||
                tile.Generation != state.Generation ||
                state.RenderKind != LayerRenderKind.VectorPoints ||
                _vectorTiles.ContainsKey(tile.Key) ||
                reservation == 0)
            {
                if (reservation != 0)
                {
                    _pendingRasterTiles.Release(tile.Key, reservation);
                }
                _pendingRasterUploadCapacity.Release();
                return false;
            }
            QueueVectorSpriteTextures(
                tile.Key.SourceId,
                tile.SpriteTextures);
        }

        _completedVectorTiles.Enqueue(new QueuedVectorTile(tile, reservation));
        RequestRender();
        return true;
    }

    /// <summary>
    /// Reserves one shared upload slot for satellite pixels and vector labels that must
    /// commit atomically for the same hybrid Azure tile.
    /// </summary>
    public async Task<bool> QueueHybridTileAsync(
        VectorTileData tile,
        CancellationToken cancellationToken)
    {
        if (tile.Background is not RasterTileData background)
        {
            throw new InvalidDataException(
                "A hybrid vector tile has no raster background.");
        }
        if (background.Key != tile.Key ||
            background.Generation != tile.Generation)
        {
            throw new InvalidDataException(
                "A hybrid vector tile background has inconsistent identity.");
        }
        await _pendingRasterUploadCapacity.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        long reservation;
        lock (RenderLock)
        {
            reservation = _pendingRasterTiles.TryReserve(tile.Key);
            if (!_rasterLayers.TryGetValue(
                    tile.Key.SourceId,
                    out RasterLayerState? state) ||
                tile.Generation != state.Generation ||
                state.RenderKind != LayerRenderKind.HybridTiles ||
                reservation == 0)
            {
                if (reservation != 0)
                {
                    _pendingRasterTiles.Release(tile.Key, reservation);
                }
                _pendingRasterUploadCapacity.Release();
                return false;
            }
            if (_vectorTiles.ContainsKey(tile.Key) ||
                _rasterTiles.ContainsKey(tile.Key))
            {
                bool removedVector = _vectorTiles.Remove(tile.Key);
                if (_rasterTiles.Remove(tile.Key, out TileTexture? orphaned))
                {
                    QueueTextureDisposal(orphaned);
                }
                if (removedVector)
                {
                    OnVectorTilesChanged(disposeGeometryCaches: true);
                }
            }
            QueueVectorSpriteTextures(
                tile.Key.SourceId,
                tile.SpriteTextures);
        }

        _rasterPixelUploads.Enqueue(
            new QueuedRasterTileUpload(background, reservation, tile));
        _uploadRequested.Set();
        return true;
    }

    /// <summary>
    /// Accepts decoded vector features only while their source generation remains current.
    /// </summary>
    private void ProcessCompletedVectorTiles()
    {
        int acceptedCount = 0;
        int staleDroppedCount = 0;
        int acceptedPointCount = 0;
        int preparedSpriteCount = 0;
        int style = -1;
        while (_completedVectorTiles.TryDequeue(out QueuedVectorTile completed))
        {
            _pendingRasterUploadCapacity.Release();
            _pendingRasterTiles.Release(
                completed.Tile.Key,
                completed.Reservation);
            if (!_rasterLayers.TryGetValue(
                    completed.Tile.Key.SourceId,
                    out RasterLayerState? state) ||
                state.Generation != completed.Tile.Generation ||
                state.RenderKind != LayerRenderKind.VectorPoints ||
                _vectorTiles.ContainsKey(completed.Tile.Key))
            {
                staleDroppedCount++;
                continue;
            }

            _vectorTiles.Add(
                completed.Tile.Key,
                new VectorTileCacheEntry(
                    completed.Tile.Features,
                    completed.Tile.StyleAssets,
                    completed.Tile.Style));
            state.VectorStyleAssets = completed.Tile.StyleAssets;
            OnVectorTilesChanged();
            acceptedCount++;
            acceptedPointCount += completed.Tile.Features.PointCount;
            preparedSpriteCount += completed.Tile.SpriteTextures.Length;
            style = completed.Tile.Style;
        }

        if (acceptedCount != 0 || staleDroppedCount != 0)
        {
            long cacheBytes = MapControlEventSource.Log.IsEnabled(
                System.Diagnostics.Tracing.EventLevel.Informational,
                MapControlEventSource.Keywords.Tiles |
                    MapControlEventSource.Keywords.VectorTiles)
                ? _vectorTiles.Values.Sum(tile => tile.ByteSize)
                : 0;
            MapControlEventSource.Log.VectorTileCommitSummary(
                style,
                acceptedCount,
                staleDroppedCount,
                acceptedPointCount,
                preparedSpriteCount,
                _vectorTiles.Count,
                cacheBytes);
        }
    }

    /// <summary>
    /// Draws cached fallback levels followed by the current vector-symbol scene.
    /// </summary>
    private unsafe bool DrawVectorPointLayer(
        IntPtr context,
        LayerRenderSnapshot layer)
    {
        if (_displayZoom < layer.MinZoom ||
            _displayZoom >= layer.MaxZoom ||
            !_rasterLayers.TryGetValue(layer.RuntimeId, out RasterLayerState? state) ||
            state.Scene is null)
        {
            return false;
        }

        VectorRenderResult renderResult = default;
        VectorSymbolRenderCacheKey cacheKey = new(
            layer,
            _vectorTileVersion,
            _iconTextureVersion,
            _deviceEpoch,
            state.SceneVersion,
            GetFallbackZoomSignature(state.FallbackTileZooms),
            _displayLongitude,
            _displayLatitude,
            _displayZoom,
            _displayHeading,
            _displayPitch,
            _viewportWidth,
            _viewportHeight,
            Volatile.Read(ref _textScaleFactor));
        if (_vectorSymbolFrameCaches.TryGetValue(
                layer.RuntimeId,
                out PreparedVectorSymbolFrame? cachedFrame) &&
            cachedFrame.Key == cacheKey)
        {
            DrawPreparedVectorBatches(
                context,
                cachedFrame.Batches,
                ref cachedFrame.RenderResult);
            LogVectorRenderResult(layer.Style, cachedFrame.RenderResult);
            return false;
        }
        if (cachedFrame is not null)
        {
            _vectorSymbolFrameCaches.Remove(layer.RuntimeId);
            cachedFrame.Dispose();
        }

        VectorSymbolRenderWorkspace workspace = _vectorSymbolWorkspace;
        _vectorSymbolFrameBuildCount++;
        workspace.ResetFrame();
        long nextCollisionGroup = 0;
        bool canEnumerateActiveScene = CanEnumerateRasterScene(
            _displayZoom,
            state.Scene.TileZoom);
        HashSet<int> cachedLevels = [.. state.FallbackTileZooms];
        if (!canEnumerateActiveScene)
        {
            cachedLevels.Add(state.Scene.TileZoom);
        }

        bool activeFade = CollectCachedVectorLevels(
            layer,
            cachedLevels,
            workspace,
            ref renderResult,
            ref nextCollisionGroup);
        if (canEnumerateActiveScene)
        {
            MapScene scene = CreateCurrentRasterScene(state.Scene.TileZoom);
            activeFade |= CollectVectorScene(
                layer,
                scene,
                workspace,
                ref renderResult,
                ref nextCollisionGroup);
            if (HasCompleteVectorCoverage(
                layer.RuntimeId,
                scene,
                layer.FadeDuration))
            {
                state.FallbackTileZooms.Clear();
            }
        }

        FindIncompleteLabelGroups(
            workspace.Placements,
            _iconTextures.ContainsKey,
            workspace.IncompleteLabelGroups,
            out int pendingTextureGlyphCount);
        ResolveLabelCollisions(
            workspace.Placements,
            workspace.IncompleteLabelGroups,
            workspace);
        renderResult.CandidateLabelCount = workspace.CollisionCandidates.Count;
        renderResult.SuppressedLabelCount =
            workspace.CollisionCandidates.Count -
            workspace.AcceptedCollisionGroups.Count;
        renderResult.SuppressedGlyphCount = workspace.SuppressedGlyphCount;
        renderResult.PendingTextureLabelCount =
            workspace.IncompleteLabelGroups.Count;
        renderResult.PendingTextureGlyphCount = pendingTextureGlyphCount;
        int fadingLabelCount = 0;
        int fadingGlyphCount = 0;
        foreach (VectorTileDrawData drawTile in workspace.DrawTiles)
        {
            Span<VectorSymbolPlacement> placements = CollectionsMarshal.AsSpan(
                workspace.Placements).Slice(
                    drawTile.StartIndex,
                    drawTile.Count);
            activeFade |= ApplyLabelTextureFade(
                placements,
                layer.FadeDuration,
                ref fadingLabelCount,
                ref fadingGlyphCount);
            foreach (VectorSymbolPlacement placement in placements)
            {
                if (placement.CollisionGroup < 0 ||
                    workspace.AcceptedCollisionGroups.Contains(
                        placement.CollisionGroup))
                {
                    workspace.DrawablePlacements.Add(placement with
                    {
                        Opacity = placement.Opacity * drawTile.Opacity,
                    });
                }
            }
        }
        PreparedVectorSymbolBatch[] batches = PrepareVectorBatches(
            workspace.DrawablePlacements,
            ref renderResult);
        DrawPreparedVectorBatches(context, batches, ref renderResult);
        renderResult.FadingLabelCount = fadingLabelCount;
        renderResult.FadingGlyphCount = fadingGlyphCount;
        LogVectorRenderResult(layer.Style, renderResult);
        if (!activeFade)
        {
            _vectorSymbolFrameCaches[layer.RuntimeId] =
                new PreparedVectorSymbolFrame(
                    workspace,
                    cacheKey,
                    batches,
                    renderResult);
        }
        else
        {
            workspace.ReturnPreparedVectorBatches(batches);
        }
        return activeFade;
    }

    private bool CollectVectorScene(
        LayerRenderSnapshot layer,
        MapScene scene,
        VectorSymbolRenderWorkspace workspace,
        ref VectorRenderResult renderResult,
        ref long nextCollisionGroup)
    {
        bool activeFade = false;
        foreach (VisibleTile visibleTile in scene.VisibleTiles)
        {
            if (_vectorTiles.TryGetValue(
                    new RasterTileKey(layer.RuntimeId, visibleTile.Id),
                    out VectorTileCacheEntry? tile))
            {
                activeFade |= CollectVectorTile(
                    layer,
                    visibleTile,
                    tile,
                    workspace,
                    ref renderResult,
                    ref nextCollisionGroup);
            }
        }
        return activeFade;
    }

    private bool CollectCachedVectorLevels(
        LayerRenderSnapshot layer,
        IReadOnlySet<int> tileZooms,
        VectorSymbolRenderWorkspace workspace,
        ref VectorRenderResult renderResult,
        ref long nextCollisionGroup)
    {
        bool activeFade = false;
        foreach ((RasterTileKey key, VectorTileCacheEntry tile) in _vectorTiles)
        {
            if (key.SourceId != layer.RuntimeId ||
                !tileZooms.Contains(key.Id.Zoom))
            {
                continue;
            }
            foreach (VisibleTile instance in GetVisibleCachedTileInstances(
                key.Id,
                _displayLongitude,
                _displayLatitude,
                _displayZoom,
                _viewportWidth,
                _viewportHeight,
                _displayHeading,
                _displayPitch))
            {
                activeFade |= CollectVectorTile(
                    layer,
                    instance,
                    tile,
                    workspace,
                    ref renderResult,
                    ref nextCollisionGroup);
            }
        }
        return activeFade;
    }

    private bool CollectVectorTile(
        LayerRenderSnapshot layer,
        VisibleTile visibleTile,
        VectorTileCacheEntry tile,
        VectorSymbolRenderWorkspace workspace,
        ref VectorRenderResult renderResult,
        ref long nextCollisionGroup)
    {
        tile.MarkUsed();
        VectorSymbolResolution resolution = tile.GetSymbols(
            _displayZoom,
            Volatile.Read(ref _textScaleFactor));
        VectorTileSymbol[] symbols = resolution.Symbols;
        renderResult.GlyphCandidateCount += resolution.ResolvedGlyphCount;
        renderResult.EvaluationFailureCount +=
            resolution.EvaluationFailureCount;
        renderResult.UnavailableSpriteCount +=
            resolution.UnavailableSpriteCount;
        renderResult.UnavailableGlyphCount +=
            resolution.UnavailableGlyphCount;
        foreach (VectorTileSymbol symbol in symbols)
        {
            renderResult.CandidateCount +=
                symbol.Kind == VectorSymbolKind.Icon ? 1 : 0;
            renderResult.LineSymbolCandidateCount +=
                symbol.LinePoints is not null ? 1 : 0;
            renderResult.PatternLineCandidateCount +=
                symbol.ContinuousLinePlacement ? 1 : 0;
            renderResult.RotatedIconCount +=
                symbol.Kind == VectorSymbolKind.Icon &&
                Math.Abs(symbol.Rotation) > 1e-7 ? 1 : 0;
            renderResult.TintedIconCount +=
                symbol.Kind == VectorSymbolKind.Icon &&
                symbol.IconPaint.IsTinted ? 1 : 0;
            renderResult.FittedIconCount +=
                symbol.Kind == VectorSymbolKind.Icon &&
                symbol.TextFit != VectorIconTextFit.None ? 1 : 0;
            renderResult.SortedSymbolCount +=
                Math.Abs(symbol.SortKey) > 1e-7 ? 1 : 0;
            renderResult.CollisionOverrideSymbolCount +=
                symbol.AllowOverlap ||
                symbol.IgnorePlacement ||
                symbol.Optional ? 1 : 0;
        }
        double opacity = ComputeLayerTileOpacity(
            Stopwatch.GetElapsedTime(tile.ReadyTimestamp),
            layer.FadeDuration,
            layer.Opacity);
        int placementStart = workspace.Placements.Count;
        ProjectVectorSymbols(
            symbols,
            visibleTile,
            _viewportWidth,
            _viewportHeight,
            _displayHeading,
            _displayPitch,
            workspace.Placements);
        int placementCount = workspace.Placements.Count - placementStart;
        renderResult.LineSymbolProjectedCount +=
            CountPlacements(
                CollectionsMarshal.AsSpan(workspace.Placements).Slice(
                    placementStart,
                    placementCount),
                static placement => placement.IsLinePlacement);
        renderResult.PatternInstanceCount +=
            CountPlacements(
                CollectionsMarshal.AsSpan(workspace.Placements).Slice(
                    placementStart,
                    placementCount),
                static placement => placement.IsContinuousLinePlacement);
        if (placementCount == 0)
        {
            return opacity < layer.Opacity;
        }

        AssignSymbolCollisionGroups(
            CollectionsMarshal.AsSpan(workspace.Placements).Slice(
                placementStart,
                placementCount),
            ref nextCollisionGroup,
            workspace);
        workspace.DrawTiles.Add(
            new VectorTileDrawData(placementStart, placementCount, opacity));
        return opacity < layer.Opacity;
    }

    internal static void AssignSymbolCollisionGroups(
        VectorSymbolPlacement[] placements,
        ref long nextCollisionGroup)
    {
        VectorSymbolRenderWorkspace workspace = new();
        AssignSymbolCollisionGroups(
            placements.AsSpan(),
            ref nextCollisionGroup,
            workspace);
    }

    private static void AssignSymbolCollisionGroups(
        Span<VectorSymbolPlacement> placements,
        ref long nextCollisionGroup,
        VectorSymbolRenderWorkspace workspace)
    {
        HashSet<(long SymbolGroupId, int PlacementIndex)> splitGroups =
            workspace.SplitCollisionGroups;
        Dictionary<(long SymbolGroupId, int PlacementIndex, int Component),
            long> collisionGroups = workspace.CollisionGroups;
        Dictionary<(long SymbolGroupId, int PlacementIndex), long>
            collisionFamilies = workspace.CollisionFamilies;
        splitGroups.Clear();
        collisionGroups.Clear();
        collisionFamilies.Clear();
        foreach (VectorSymbolPlacement placement in placements)
        {
            if (placement.SymbolGroupId >= 0 && placement.Optional)
            {
                splitGroups.Add((
                    placement.SymbolGroupId,
                    placement.PlacementIndex));
            }
        }
        for (int index = 0; index < placements.Length; index++)
        {
            VectorSymbolPlacement placement = placements[index];
            if (placement.SymbolGroupId < 0)
            {
                continue;
            }
            (long SymbolGroupId, int PlacementIndex) symbolKey =
                (placement.SymbolGroupId, placement.PlacementIndex);
            int component = splitGroups.Contains(symbolKey)
                ? (placement.Kind == VectorSymbolKind.Icon ? 1 : 2)
                : 0;
            (long SymbolGroupId, int PlacementIndex, int Component) key =
                (placement.SymbolGroupId, placement.PlacementIndex, component);
            if (!collisionGroups.TryGetValue(
                    key,
                    out long collisionGroup))
            {
                collisionGroup = nextCollisionGroup++;
                collisionGroups.Add(key, collisionGroup);
            }
            if (!collisionFamilies.TryGetValue(
                    symbolKey,
                    out long collisionFamily))
            {
                collisionFamily = collisionGroup;
                collisionFamilies.Add(symbolKey, collisionFamily);
            }
            placements[index] = placement with
            {
                CollisionGroup = collisionGroup,
                CollisionFamily = collisionFamily,
            };
        }
    }

    private PreparedVectorSymbolBatch[] PrepareVectorBatches(
        IReadOnlyList<VectorSymbolPlacement> placements,
        ref VectorRenderResult renderResult)
    {
        if (placements.Count == 0)
        {
            return [];
        }

        VectorSymbolRenderWorkspace workspace = _vectorSymbolWorkspace;
        workspace.ResetBatches();
        foreach (VectorSymbolPlacement placement in placements)
        {
            if (placement.CollisionGroup < 0 ||
                placement.AllowOverlap ||
                placement.IgnorePlacement)
            {
                workspace.OrderSensitiveLayers.Add(
                    placement.StyleLayerOrder);
            }
        }

        List<PreparedVectorSymbolBatch> prepared = [];
        foreach (VectorSymbolPlacement placement in placements)
        {
            if (workspace.OrderSensitiveLayers.Contains(
                    placement.StyleLayerOrder))
            {
                workspace.OrderSensitivePlacements.Add(placement);
                continue;
            }

            PreparedVectorBatchKey key = new(
                placement.StyleLayerOrder,
                new VectorBatchKey(
                    placement.TextureId,
                    placement.Kind,
                    placement.Paint,
                    placement.IconPaint,
                    placement.Opacity));
            if (!workspace.WorkingBatchIndexes.TryGetValue(
                    key,
                    out int batchIndex))
            {
                batchIndex = workspace.WorkingBatches.Count;
                workspace.WorkingBatchIndexes.Add(key, batchIndex);
                workspace.WorkingBatches.Add(
                    workspace.RentWorkingBatch(key));
            }
            workspace.WorkingBatches[batchIndex].Add(
                CreateIconInstance(
                    placement.Left,
                    placement.Top,
                    placement.Width,
                    placement.Height,
                    placement.Rotation),
                placement.IsLinePlacement);
        }

        workspace.WorkingBatches.Sort(WorkingVectorSymbolBatchComparer.Instance);
        foreach (WorkingVectorSymbolBatch batch in workspace.WorkingBatches)
        {
            prepared.Add(CreatePreparedVectorBatch(
                batch,
                prepared.Count,
                ref renderResult));
        }

        if (workspace.OrderSensitivePlacements.Count != 0)
        {
            foreach (VectorSymbolBatch batch in BatchVectorSymbolsByTexture(
                workspace.OrderSensitivePlacements))
            {
                prepared.Add(CreatePreparedVectorBatch(
                    batch.StyleLayerOrder,
                    batch.TextureId,
                    batch.Kind,
                    batch.Paint,
                    batch.IconPaint,
                    batch.Opacity,
                    batch.Placements,
                    prepared.Count,
                    ref renderResult));
            }
        }
        prepared.Sort(PreparedVectorSymbolBatchComparer.Instance);
        return prepared.ToArray();
    }

    private PreparedVectorSymbolBatch CreatePreparedVectorBatch(
        WorkingVectorSymbolBatch batch,
        int drawSequence,
        ref VectorRenderResult renderResult)
    {
        IconInstance[] instances =
            _vectorSymbolWorkspace.RentInstanceBuffer(batch.Instances.Count);
        CollectionsMarshal.AsSpan(batch.Instances).CopyTo(instances);
        UpdatePreparedVectorBatchCounts(
            batch.Key.Batch.Kind,
            batch.Instances.Count,
            batch.LinePlacementCount,
            ref renderResult);
        return new PreparedVectorSymbolBatch(
            batch.Key.StyleLayerOrder,
            batch.Key.Batch.TextureId,
            batch.Key.Batch.Kind,
            batch.Key.Batch.Paint,
            batch.Key.Batch.IconPaint,
            batch.Key.Batch.Opacity,
            instances,
            batch.Instances.Count,
            drawSequence);
    }

    private PreparedVectorSymbolBatch CreatePreparedVectorBatch(
        int styleLayerOrder,
        long textureId,
        VectorSymbolKind kind,
        VectorTextPaint paint,
        VectorIconPaint iconPaint,
        double opacity,
        IEnumerable<VectorSymbolPlacement> placements,
        int drawSequence,
        ref VectorRenderResult renderResult)
    {
        VectorSymbolPlacement[] placementArray = placements as VectorSymbolPlacement[] ??
            placements.ToArray();
        IconInstance[] instances =
            _vectorSymbolWorkspace.RentInstanceBuffer(placementArray.Length);
        int linePlacementCount = 0;
        for (int index = 0; index < placementArray.Length; index++)
        {
            VectorSymbolPlacement placement = placementArray[index];
            instances[index] = CreateIconInstance(
                placement.Left,
                placement.Top,
                placement.Width,
                placement.Height,
                placement.Rotation);
            if (placement.IsLinePlacement)
            {
                linePlacementCount++;
            }
        }
        UpdatePreparedVectorBatchCounts(
            kind,
            placementArray.Length,
            linePlacementCount,
            ref renderResult);
        return new PreparedVectorSymbolBatch(
            styleLayerOrder,
            textureId,
            kind,
            paint,
            iconPaint,
            opacity,
            instances,
            placementArray.Length,
            drawSequence);
    }

    private static void UpdatePreparedVectorBatchCounts(
        VectorSymbolKind kind,
        int instanceCount,
        int linePlacementCount,
        ref VectorRenderResult renderResult)
    {
        renderResult.LineSymbolDrawnCount += linePlacementCount;
        if (kind == VectorSymbolKind.Text)
        {
            renderResult.DrawableGlyphCount += instanceCount;
            renderResult.GlyphTextureBatchCount++;
            renderResult.GlyphDrawCallCount +=
                (instanceCount + IconInstanceCapacity - 1) /
                IconInstanceCapacity;
        }
        else
        {
            renderResult.DrawableCount += instanceCount;
            renderResult.TextureBatchCount++;
            renderResult.DrawCallCount +=
                (instanceCount + IconInstanceCapacity - 1) /
                IconInstanceCapacity;
        }
    }

    private unsafe void DrawPreparedVectorBatches(
        IntPtr context,
        IReadOnlyList<PreparedVectorSymbolBatch> batches,
        ref VectorRenderResult renderResult)
    {
        if (batches.Count == 0)
        {
            return;
        }

        SetBlendState(context, _premultipliedBlendStatePointer);
        SetInputLayout(context, _iconInputLayoutPointer);
        SetVertexBuffers(
            context,
            _vertexBufferPointer,
            (uint)Marshal.SizeOf<TileVertex>(),
            _iconInstanceBufferPointer,
            (uint)Marshal.SizeOf<IconInstance>());
        SetVertexShader(context, _iconVertexShaderPointer);

        foreach (PreparedVectorSymbolBatch batch in batches)
        {
            if (!_iconTextures.TryGetValue(
                    batch.TextureId,
                    out TileTexture? texture))
            {
                continue;
            }
            texture.MarkUsed();
            TileConstants layerConstants = batch.Kind == VectorSymbolKind.Text
                ? new TileConstants(
                    new Vector4(1, 1, 0, 0),
                    batch.Paint.Color,
                    batch.Paint.HaloColor,
                    new Vector4(
                        (float)batch.Opacity,
                        (float)batch.Paint.HaloOffset,
                        (float)batch.Paint.HaloBlur,
                        0))
                : new TileConstants(
                    new Vector4(1, 1, 0, 0),
                    batch.IconPaint.Color,
                    new Vector4(1, 0, 1, 0),
                    new Vector4(
                        (float)batch.Opacity,
                        batch.IconPaint.IsTinted ? 1 : 0,
                        0,
                        0));
            UpdateSubresource(context, _constantBufferPointer, &layerConstants);
            SetPixelShader(
                context,
                batch.Kind == VectorSymbolKind.Text
                    ? _glyphPixelShaderPointer
                    : _iconPixelShaderPointer,
                texture.ViewPointer,
                _samplerPointer,
                _constantBufferPointer);
            ReadOnlySpan<IconInstance> remaining =
                batch.Instances.AsSpan(0, batch.InstanceCount);
            while (!remaining.IsEmpty)
            {
                ReadOnlySpan<IconInstance> chunk = remaining[..Math.Min(
                    IconInstanceCapacity,
                    remaining.Length)];
                fixed (IconInstance* instancePointer = chunk)
                {
                    WriteDiscardBuffer(
                        context,
                        _iconInstanceBufferPointer,
                        instancePointer,
                        (nuint)(chunk.Length * Marshal.SizeOf<IconInstance>()));
                }
                DrawIndexedInstanced(context, (uint)chunk.Length);
                remaining = remaining[chunk.Length..];
            }
        }
    }

    internal static VectorSymbolPlacement[] ProjectVectorSymbols(
        IReadOnlyList<VectorTileSymbol> symbols,
        VisibleTile tile,
        double viewportWidth,
        double viewportHeight,
        double heading,
        double pitch)
    {
        List<VectorSymbolPlacement> projected = new(symbols.Count);
        ProjectVectorSymbols(
            symbols,
            tile,
            viewportWidth,
            viewportHeight,
            heading,
            pitch,
            projected);
        return projected.ToArray();
    }

    private static void ProjectVectorSymbols(
        IReadOnlyList<VectorTileSymbol> symbols,
        VisibleTile tile,
        double viewportWidth,
        double viewportHeight,
        double heading,
        double pitch,
        List<VectorSymbolPlacement> projected)
    {
        Dictionary<
            (long SymbolGroupId, int StyleLayerOrder, VectorTilePoint[] Path),
            List<VectorTileSymbol>>? lineGroups = null;
        long projectedGroupId = long.MinValue;
        double projectedX = double.NaN;
        double projectedY = double.NaN;
        double anchorX = 0;
        double anchorY = 0;
        foreach (VectorTileSymbol symbol in symbols)
        {
            if (symbol.LinePoints is not { Length: >= 2 } linePoints)
            {
                long groupId = symbol.SymbolGroupId >= 0
                    ? symbol.SymbolGroupId
                    : symbol.LabelId;
                if (groupId != projectedGroupId ||
                    symbol.X != projectedX ||
                    symbol.Y != projectedY)
                {
                    ProjectVectorSymbolAnchor(
                        symbol,
                        tile,
                        viewportWidth,
                        viewportHeight,
                        heading,
                        pitch,
                        out anchorX,
                        out anchorY);
                    projectedGroupId = groupId;
                    projectedX = symbol.X;
                    projectedY = symbol.Y;
                }
                AddProjectedPointSymbolAtAnchor(
                    symbol,
                    tile,
                    viewportWidth,
                    viewportHeight,
                    heading,
                    pitch,
                    anchorX,
                    anchorY,
                    projected);
                continue;
            }

            long symbolGroupId = symbol.SymbolGroupId >= 0
                ? symbol.SymbolGroupId
                : symbol.LabelId;
            lineGroups ??= [];
            (long SymbolGroupId, int StyleLayerOrder, VectorTilePoint[] Path) key =
                (symbolGroupId, symbol.StyleLayerOrder, linePoints);
            if (!lineGroups.TryGetValue(
                    key,
                    out List<VectorTileSymbol>? group))
            {
                group = [];
                lineGroups.Add(key, group);
            }
            group.Add(symbol);
        }
        if (lineGroups is null)
        {
            return;
        }
        foreach (List<VectorTileSymbol> group in lineGroups.Values)
        {
            AddProjectedLineSymbols(
                group,
                tile,
                viewportWidth,
                viewportHeight,
                heading,
                pitch,
                projected);
        }
    }

    internal static HashSet<long> FindIncompleteLabelGroups(
        IEnumerable<VectorSymbolPlacement> placements,
        Predicate<long> isTextureAvailable,
        out int pendingGlyphCount)
    {
        HashSet<long> incompleteGroups = [];
        FindIncompleteLabelGroups(
            placements,
            isTextureAvailable,
            incompleteGroups,
            out pendingGlyphCount);
        return incompleteGroups;
    }

    private static void FindIncompleteLabelGroups(
        IEnumerable<VectorSymbolPlacement> placements,
        Predicate<long> isTextureAvailable,
        HashSet<long> incompleteGroups,
        out int pendingGlyphCount)
    {
        incompleteGroups.Clear();
        pendingGlyphCount = 0;
        foreach (VectorSymbolPlacement placement in placements)
        {
            if (placement.CollisionGroup < 0 ||
                placement.Optional ||
                isTextureAvailable(placement.TextureId))
            {
                continue;
            }

            incompleteGroups.Add(placement.CollisionGroup);
        }
        if (incompleteGroups.Count == 0)
        {
            return;
        }

        pendingGlyphCount = placements.Count(placement =>
            placement.Kind == VectorSymbolKind.Text &&
            incompleteGroups.Contains(placement.CollisionGroup));
    }

    private bool ApplyLabelTextureFade(
        Span<VectorSymbolPlacement> placements,
        TimeSpan fadeDuration,
        ref int fadingLabelCount,
        ref int fadingGlyphCount)
    {
        if (fadeDuration <= TimeSpan.Zero)
        {
            return false;
        }

        Dictionary<long, long> readyTimestamps = [];
        foreach (VectorSymbolPlacement placement in placements)
        {
            if (placement.Kind != VectorSymbolKind.Text ||
                placement.CollisionGroup < 0 ||
                !_iconTextures.TryGetValue(
                    placement.TextureId,
                    out TileTexture? texture))
            {
                continue;
            }

            readyTimestamps[placement.CollisionGroup] =
                Math.Max(
                    readyTimestamps.GetValueOrDefault(
                        placement.CollisionGroup),
                    texture.ReadyTimestamp);
        }

        bool activeFade = false;
        HashSet<long> fadingGroups = [];
        for (int index = 0; index < placements.Length; index++)
        {
            VectorSymbolPlacement placement = placements[index];
            if (placement.CollisionGroup < 0 ||
                !readyTimestamps.TryGetValue(
                    placement.CollisionGroup,
                    out long readyTimestamp))
            {
                continue;
            }

            double opacity = Math.Clamp(
                Stopwatch.GetElapsedTime(readyTimestamp).TotalMilliseconds /
                    fadeDuration.TotalMilliseconds,
                0,
                1);
            if (opacity < 1)
            {
                activeFade = true;
                fadingGroups.Add(placement.CollisionGroup);
                if (placement.Kind == VectorSymbolKind.Text)
                {
                    fadingGlyphCount++;
                }
            }
            placements[index] = placement with
            {
                Opacity = Math.Round(opacity * 16) / 16,
            };
        }
        fadingLabelCount += fadingGroups.Count;
        return activeFade;
    }

    private static void ProjectVectorSymbolAnchor(
        VectorTileSymbol symbol,
        VisibleTile tile,
        double viewportWidth,
        double viewportHeight,
        double heading,
        double pitch,
        out double x,
        out double y)
    {
        x = tile.Left + (symbol.X * tile.Size) - (viewportWidth / 2);
        y = tile.Top + (symbol.Y * tile.Size) - (viewportHeight / 2);
        MapCamera.TransformViewportOffset(
            x,
            y,
            heading,
            pitch,
            viewportHeight,
            out x,
            out y);
        x += viewportWidth / 2;
        y += viewportHeight / 2;
    }

    private static void AddProjectedPointSymbolAtAnchor(
        VectorTileSymbol symbol,
        VisibleTile tile,
        double viewportWidth,
        double viewportHeight,
        double heading,
        double pitch,
        double x,
        double y,
        List<VectorSymbolPlacement> projected)
    {
            double left = x - (symbol.Width / 2) + symbol.OffsetX;
            double top = y - (symbol.Height / 2) + symbol.OffsetY;
            VectorSymbolPlacement placement = new(
                symbol.StyleLayerOrder,
                symbol.TextureId,
                left,
                top,
                symbol.Width,
                symbol.Height,
                symbol.Kind,
                symbol.Paint,
                symbol.LabelId,
                Rotation: symbol.Rotation,
                Opacity: symbol.Opacity,
                IconPaint: symbol.IconPaint,
                SymbolGroupId: symbol.SymbolGroupId,
                SortKey: symbol.SortKey,
                AllowOverlap: symbol.AllowOverlap,
                IgnorePlacement: symbol.IgnorePlacement,
                Optional: symbol.Optional,
                CollisionPadding: symbol.CollisionPadding,
                AvoidEdges: symbol.AvoidEdges);
            GetVectorSymbolBounds(
                placement,
                out double boundsLeft,
                out double boundsTop,
                out double boundsRight,
                out double boundsBottom);
            if (boundsRight <= 0 ||
                boundsBottom <= 0 ||
                boundsLeft >= viewportWidth ||
                boundsTop >= viewportHeight)
            {
                return;
            }
            if (placement.AvoidEdges &&
                !IsPlacementWithinTile(
                    placement,
                    tile,
                    viewportWidth,
                    viewportHeight,
                    heading,
                    pitch))
            {
                return;
            }
            projected.Add(placement);
    }

    private static void AddProjectedLineSymbols(
        IReadOnlyList<VectorTileSymbol> symbols,
        VisibleTile tile,
        double viewportWidth,
        double viewportHeight,
        double heading,
        double pitch,
        List<VectorSymbolPlacement> projected)
    {
        VectorTilePoint[] linePoints = symbols[0].LinePoints!;
        MapScreenPoint[] path = ProjectVectorLine(
            linePoints,
            tile,
            viewportWidth,
            viewportHeight,
            heading,
            pitch);
        double[] distances = new double[path.Length];
        for (int index = 1; index < path.Length; index++)
        {
            double deltaX = path[index].X - path[index - 1].X;
            double deltaY = path[index].Y - path[index - 1].Y;
            double segmentLength = Math.Sqrt(
                (deltaX * deltaX) + (deltaY * deltaY));
            if (!double.IsFinite(segmentLength))
            {
                return;
            }
            distances[index] = distances[index - 1] + segmentLength;
        }
        double pathLength = distances[^1];
        double minimumOffset = symbols.Min(
            symbol => symbol.OffsetX - (symbol.Width / 2));
        double maximumOffset = symbols.Max(
            symbol => symbol.OffsetX + (symbol.Width / 2));
        double symbolLength = maximumOffset - minimumOffset;
        bool continuousPlacement = symbols[0].ContinuousLinePlacement;
        double endpointPadding = continuousPlacement ? 0 : 4;
        if (pathLength < symbolLength + (endpointPadding * 2))
        {
            return;
        }

        double spacing = Math.Max(
            symbols[0].LineSpacing,
            symbolLength + (continuousPlacement ? 0 : 16));
        double usableLength = pathLength - symbolLength -
            (endpointPadding * 2);
        const int maximumPlacementCount = 4096;
        int placementCount = (int)Math.Clamp(
            Math.Floor(usableLength / spacing) + 1,
            1,
            maximumPlacementCount);
        double firstCenter = continuousPlacement
            ? symbolLength / 2
            : (pathLength - ((placementCount - 1) * spacing)) / 2;
        for (int placementIndex = 0;
            placementIndex < placementCount;
            placementIndex++)
        {
            double centerDistance = firstCenter + (placementIndex * spacing);
            if (!TryGetPathPosition(
                    path,
                    distances,
                    centerDistance,
                    out MapScreenPoint centerPosition,
                    out MapScreenPoint centerTangent))
            {
                continue;
            }
            VectorTileSymbol? textSymbol = symbols
                .Where(symbol => symbol.Kind == VectorSymbolKind.Text)
                .Cast<VectorTileSymbol?>()
                .FirstOrDefault();
            bool reverse = !continuousPlacement &&
                (textSymbol?.KeepUpright ?? true) &&
                centerTangent.X < 0;
            double maximumAngle =
                textSymbol?.MaximumAngle ?? Math.PI / 4;
            double centerRotation = Math.Atan2(
                centerTangent.Y,
                centerTangent.X);
            if (reverse)
            {
                centerRotation = NormalizeRadians(centerRotation + Math.PI);
            }
            List<VectorSymbolPlacement> candidate = new(symbols.Count);
            double? previousRotation = null;
            double? previousOffset = null;
            MapScreenPoint previousCenter = default;
            double minimumRelativeRotation = double.PositiveInfinity;
            double maximumRelativeRotation = double.NegativeInfinity;
            bool valid = true;
            foreach (VectorTileSymbol symbol in symbols)
            {
                MapScreenPoint position;
                MapScreenPoint tangent;
                if (symbol.ViewportAligned)
                {
                    position = centerPosition;
                    tangent = centerTangent;
                }
                else
                {
                    double symbolDistance = centerDistance +
                        (reverse ? -symbol.OffsetX : symbol.OffsetX);
                    if (!TryGetSmoothedPathPosition(
                            path,
                            distances,
                            symbolDistance,
                            Math.Clamp(symbol.Height / 4, 2, 6),
                            out position,
                            out tangent))
                    {
                        valid = false;
                        break;
                    }
                }
                double pathRotation = Math.Atan2(tangent.Y, tangent.X);
                if (reverse)
                {
                    pathRotation = NormalizeRadians(pathRotation + Math.PI);
                }
                double relativeRotation = NormalizeRadians(
                    pathRotation - centerRotation);
                minimumRelativeRotation = Math.Min(
                    minimumRelativeRotation,
                    relativeRotation);
                maximumRelativeRotation = Math.Max(
                    maximumRelativeRotation,
                    relativeRotation);
                if (previousRotation is double previous &&
                    Math.Abs(NormalizeRadians(pathRotation - previous)) >
                        maximumAngle ||
                    maximumRelativeRotation - minimumRelativeRotation >
                        maximumAngle)
                {
                    valid = false;
                    break;
                }
                previousRotation = pathRotation;
                double centerX;
                double centerY;
                if (symbol.ViewportAligned)
                {
                    centerX = position.X + symbol.OffsetX;
                    centerY = position.Y + symbol.OffsetY;
                }
                else
                {
                    double normalX = -Math.Sin(pathRotation);
                    double normalY = Math.Cos(pathRotation);
                    centerX = position.X + (normalX * symbol.OffsetY);
                    centerY = position.Y + (normalY * symbol.OffsetY);
                }
                if (!symbol.ViewportAligned &&
                    previousOffset is double offset)
                {
                    double expectedSpacing = Math.Abs(symbol.OffsetX - offset);
                    double deltaX = centerX - previousCenter.X;
                    double deltaY = centerY - previousCenter.Y;
                    double actualSpacing = Math.Sqrt(
                        (deltaX * deltaX) + (deltaY * deltaY));
                    if (expectedSpacing > 1 &&
                        (actualSpacing < expectedSpacing * 0.75 ||
                         actualSpacing > expectedSpacing * 1.25))
                    {
                        valid = false;
                        break;
                    }
                }
                previousOffset = symbol.ViewportAligned
                    ? null
                    : symbol.OffsetX;
                previousCenter = new MapScreenPoint(centerX, centerY);
                double left = centerX - (symbol.Width / 2);
                double top = centerY - (symbol.Height / 2);
                VectorSymbolPlacement placement = new(
                    symbol.StyleLayerOrder,
                    symbol.TextureId,
                    left,
                    top,
                    symbol.Width,
                    symbol.Height,
                    symbol.Kind,
                    symbol.Paint,
                    symbol.LabelId,
                    Rotation: symbol.ViewportAligned
                        ? symbol.Rotation
                        : NormalizeRadians(
                            pathRotation + symbol.Rotation),
                    PlacementIndex: placementIndex,
                    IsLinePlacement: true,
                    Opacity: symbol.Opacity,
                    IconPaint: symbol.IconPaint,
                    SymbolGroupId: symbol.SymbolGroupId,
                    SortKey: symbol.SortKey,
                    AllowOverlap: symbol.AllowOverlap,
                    IgnorePlacement: symbol.IgnorePlacement,
                    Optional: symbol.Optional,
                    CollisionPadding: symbol.CollisionPadding,
                    AvoidEdges: symbol.AvoidEdges,
                    IsContinuousLinePlacement:
                        symbol.ContinuousLinePlacement);
                GetVectorSymbolBounds(
                    placement,
                    out double boundsLeft,
                    out double boundsTop,
                    out double boundsRight,
                    out double boundsBottom);
                if (boundsRight <= 0 ||
                    boundsBottom <= 0 ||
                    boundsLeft >= viewportWidth ||
                    boundsTop >= viewportHeight)
                {
                    valid = false;
                    break;
                }
                candidate.Add(placement);
            }
            if (valid &&
                (!candidate.Any(placement => placement.AvoidEdges) ||
                 candidate.All(placement => IsPlacementWithinTile(
                     placement,
                     tile,
                     viewportWidth,
                     viewportHeight,
                     heading,
                     pitch))))
            {
                projected.AddRange(candidate);
            }
        }
    }

    private static bool IsPlacementWithinTile(
        VectorSymbolPlacement placement,
        VisibleTile tile,
        double viewportWidth,
        double viewportHeight,
        double heading,
        double pitch)
    {
        GetVectorSymbolBounds(
            placement,
            out double left,
            out double top,
            out double right,
            out double bottom);
        MapScreenPoint[] tileCorners =
        [
            ProjectVectorPoint(
                new VectorTilePoint(0, 0),
                tile,
                viewportWidth,
                viewportHeight,
                heading,
                pitch),
            ProjectVectorPoint(
                new VectorTilePoint(1, 0),
                tile,
                viewportWidth,
                viewportHeight,
                heading,
                pitch),
            ProjectVectorPoint(
                new VectorTilePoint(1, 1),
                tile,
                viewportWidth,
                viewportHeight,
                heading,
                pitch),
            ProjectVectorPoint(
                new VectorTilePoint(0, 1),
                tile,
                viewportWidth,
                viewportHeight,
                heading,
                pitch),
        ];
        return IsPointInsideConvexPolygon(
                new MapScreenPoint(left, top),
                tileCorners) &&
            IsPointInsideConvexPolygon(
                new MapScreenPoint(right, top),
                tileCorners) &&
            IsPointInsideConvexPolygon(
                new MapScreenPoint(right, bottom),
                tileCorners) &&
            IsPointInsideConvexPolygon(
                new MapScreenPoint(left, bottom),
                tileCorners);
    }

    private static bool IsPointInsideConvexPolygon(
        MapScreenPoint point,
        IReadOnlyList<MapScreenPoint> polygon)
    {
        double? sign = null;
        for (int index = 0; index < polygon.Count; index++)
        {
            MapScreenPoint first = polygon[index];
            MapScreenPoint second = polygon[(index + 1) % polygon.Count];
            double cross =
                ((second.X - first.X) * (point.Y - first.Y)) -
                ((second.Y - first.Y) * (point.X - first.X));
            if (Math.Abs(cross) <= 1e-7)
            {
                continue;
            }
            double current = Math.Sign(cross);
            if (sign is double expected && current != expected)
            {
                return false;
            }
            sign = current;
        }
        return true;
    }

    private static bool TryGetSmoothedPathPosition(
        IReadOnlyList<MapScreenPoint> path,
        IReadOnlyList<double> distances,
        double distance,
        double radius,
        out MapScreenPoint position,
        out MapScreenPoint tangent)
    {
        if (!TryGetPathPosition(
                path,
                distances,
                distance,
                out position,
                out tangent))
        {
            return false;
        }

        double startDistance = Math.Max(0, distance - radius);
        double endDistance = Math.Min(distances[^1], distance + radius);
        if (endDistance - startDistance <= 1e-7 ||
            !TryGetPathPosition(
                path,
                distances,
                startDistance,
                out MapScreenPoint start,
                out _) ||
            !TryGetPathPosition(
                path,
                distances,
                endDistance,
                out MapScreenPoint end,
                out _))
        {
            return true;
        }

        double deltaX = end.X - start.X;
        double deltaY = end.Y - start.Y;
        double length = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        if (length > 1e-7)
        {
            tangent = new MapScreenPoint(deltaX / length, deltaY / length);
        }
        return true;
    }

    private static bool TryGetPathPosition(
        IReadOnlyList<MapScreenPoint> path,
        IReadOnlyList<double> distances,
        double distance,
        out MapScreenPoint position,
        out MapScreenPoint tangent)
    {
        if (distance < 0 || distance > distances[^1])
        {
            position = default;
            tangent = default;
            return false;
        }
        int segment = 1;
        while (segment < distances.Count && distances[segment] < distance)
        {
            segment++;
        }
        if (segment >= distances.Count)
        {
            segment = distances.Count - 1;
        }
        MapScreenPoint start = path[segment - 1];
        MapScreenPoint end = path[segment];
        double length = distances[segment] - distances[segment - 1];
        if (length <= 1e-7)
        {
            position = default;
            tangent = default;
            return false;
        }
        double progress = (distance - distances[segment - 1]) / length;
        position = new MapScreenPoint(
            start.X + ((end.X - start.X) * progress),
            start.Y + ((end.Y - start.Y) * progress));
        tangent = new MapScreenPoint(
            (end.X - start.X) / length,
            (end.Y - start.Y) / length);
        return true;
    }

    private static double NormalizeRadians(double value)
    {
        value %= Math.Tau;
        if (value > Math.PI)
        {
            value -= Math.Tau;
        }
        else if (value < -Math.PI)
        {
            value += Math.Tau;
        }
        return value;
    }

    private static void GetVectorSymbolBounds(
        VectorSymbolPlacement placement,
        out double left,
        out double top,
        out double right,
        out double bottom)
    {
        double halfWidth = placement.Width / 2;
        double halfHeight = placement.Height / 2;
        double cosine = Math.Abs(Math.Cos(placement.Rotation));
        double sine = Math.Abs(Math.Sin(placement.Rotation));
        double boundsHalfWidth =
            (halfWidth * cosine) + (halfHeight * sine);
        double boundsHalfHeight =
            (halfWidth * sine) + (halfHeight * cosine);
        double centerX = placement.Left + halfWidth;
        double centerY = placement.Top + halfHeight;
        left = centerX - boundsHalfWidth;
        top = centerY - boundsHalfHeight;
        right = centerX + boundsHalfWidth;
        bottom = centerY + boundsHalfHeight;
    }

    internal static LabelCollisionResult ResolveLabelCollisions(
        IEnumerable<VectorSymbolPlacement> placements)
    {
        VectorSymbolRenderWorkspace workspace = new();
        ResolveLabelCollisions(
            placements,
            incompleteGroups: null,
            workspace);
        return new LabelCollisionResult(
            [.. workspace.AcceptedCollisionGroups],
            workspace.CollisionCandidates.Count,
            workspace.CollisionCandidates.Count -
                workspace.AcceptedCollisionGroups.Count,
            workspace.SuppressedGlyphCount);
    }

    private static void ResolveLabelCollisions(
        IEnumerable<VectorSymbolPlacement> placements,
        IReadOnlySet<long>? incompleteGroups,
        VectorSymbolRenderWorkspace workspace)
    {
        const double gridCellSize = 64;
        List<LabelCollisionCandidate> candidates =
            workspace.CollisionCandidates;
        Dictionary<long, int> candidateIndexes =
            workspace.CollisionCandidateIndexes;
        HashSet<long> acceptedGroups =
            workspace.AcceptedCollisionGroups;
        Dictionary<long, List<LabelCollisionRectangle>> occupiedCells =
            workspace.OccupiedCollisionCells;
        candidates.Clear();
        candidateIndexes.Clear();
        acceptedGroups.Clear();
        workspace.ClearOccupiedCollisionCells();
        workspace.SuppressedGlyphCount = 0;
        int sequence = 0;
        foreach (VectorSymbolPlacement placement in placements)
        {
            if (placement.CollisionGroup < 0 ||
                (incompleteGroups?.Contains(placement.CollisionGroup) ?? false))
            {
                continue;
            }
            if (!candidateIndexes.TryGetValue(
                    placement.CollisionGroup,
                    out int candidateIndex))
            {
                GetVectorSymbolBounds(
                    placement,
                    out double left,
                    out double top,
                    out double right,
                    out double bottom);
                candidateIndex = candidates.Count;
                candidates.Add(new LabelCollisionCandidate(
                    placement.CollisionGroup,
                    placement.StyleLayerOrder,
                    placement.SortKey,
                    placement.CollisionFamily >= 0
                        ? placement.CollisionFamily
                        : placement.CollisionGroup,
                    sequence++,
                    left,
                    top,
                    right,
                    bottom));
                candidateIndexes.Add(
                    placement.CollisionGroup,
                    candidateIndex);
            }
            else
            {
                GetVectorSymbolBounds(
                    placement,
                    out double left,
                    out double top,
                    out double right,
                    out double bottom);
                LabelCollisionCandidate existing = candidates[candidateIndex];
                existing.Left = Math.Min(existing.Left, left);
                existing.Top = Math.Min(existing.Top, top);
                existing.Right = Math.Max(existing.Right, right);
                existing.Bottom = Math.Max(existing.Bottom, bottom);
                candidates[candidateIndex] = existing;
            }
            LabelCollisionCandidate candidate = candidates[candidateIndex];
            candidate.SortKey = Math.Min(candidate.SortKey, placement.SortKey);
            candidate.CollisionPadding = Math.Max(
                candidate.CollisionPadding,
                placement.CollisionPadding);
            candidate.AllowOverlap &= placement.AllowOverlap;
            candidate.IgnorePlacement &= placement.IgnorePlacement;
            if (placement.Kind == VectorSymbolKind.Text)
            {
                candidate.GlyphCount++;
            }
            candidates[candidateIndex] = candidate;
        }

        candidates.Sort(LabelCollisionCandidateComparer.Instance);
        foreach (LabelCollisionCandidate candidate in candidates)
        {
            LabelCollisionRectangle bounds = new(
                candidate.Left - candidate.CollisionPadding,
                candidate.Top - candidate.CollisionPadding,
                candidate.Right + candidate.CollisionPadding,
                candidate.Bottom + candidate.CollisionPadding,
                candidate.CollisionFamily);
            int firstCellX = (int)Math.Floor(bounds.Left / gridCellSize);
            int lastCellX = (int)Math.Floor(bounds.Right / gridCellSize);
            int firstCellY = (int)Math.Floor(bounds.Top / gridCellSize);
            int lastCellY = (int)Math.Floor(bounds.Bottom / gridCellSize);
            bool overlaps = false;
            for (int y = firstCellY; y <= lastCellY && !overlaps; y++)
            {
                for (int x = firstCellX; x <= lastCellX && !overlaps; x++)
                {
                    long cell = ((long)x << 32) | (uint)y;
                    if (occupiedCells.TryGetValue(
                            cell,
                            out List<LabelCollisionRectangle>? occupied))
                    {
                        overlaps = occupied.Any(existing =>
                            existing.CollisionFamily != candidate.CollisionFamily &&
                            bounds.Left < existing.Right &&
                            bounds.Right > existing.Left &&
                            bounds.Top < existing.Bottom &&
                            bounds.Bottom > existing.Top);
                    }
                }
            }
            if (overlaps && !candidate.AllowOverlap)
            {
                workspace.SuppressedGlyphCount += candidate.GlyphCount;
                continue;
            }

            acceptedGroups.Add(candidate.CollisionGroup);
            if (candidate.IgnorePlacement)
            {
                continue;
            }
            for (int y = firstCellY; y <= lastCellY; y++)
            {
                for (int x = firstCellX; x <= lastCellX; x++)
                {
                    long cell = ((long)x << 32) | (uint)y;
                    if (!occupiedCells.TryGetValue(
                            cell,
                            out List<LabelCollisionRectangle>? occupied))
                    {
                        occupied = workspace.RentCollisionCell();
                        occupiedCells.Add(cell, occupied);
                    }
                    occupied.Add(bounds);
                }
            }
        }
    }

    internal static VectorSymbolBatch[] BatchVectorSymbolsByTexture(
        IReadOnlyList<VectorSymbolPlacement> placements)
    {
        List<VectorSymbolBatch> batches = [];
        VectorBatchKey? currentKey = null;
        int currentOrder = -1;
        List<VectorSymbolPlacement> currentPlacements = [];
        foreach (VectorSymbolPlacement placement in placements
            .OrderBy(placement => placement.StyleLayerOrder)
            .ThenBy(placement => placement.SortKey))
        {
            VectorBatchKey key = new(
                placement.TextureId,
                placement.Kind,
                placement.Paint,
                placement.IconPaint,
                placement.Opacity);
            if (currentKey is VectorBatchKey previous &&
                (previous != key ||
                 currentOrder != placement.StyleLayerOrder))
            {
                batches.Add(new VectorSymbolBatch(
                    currentOrder,
                    previous.TextureId,
                    previous.Kind,
                    previous.Paint,
                    previous.IconPaint,
                    previous.Opacity,
                    currentPlacements.ToArray()));
                currentPlacements.Clear();
            }
            currentKey = key;
            currentOrder = placement.StyleLayerOrder;
            currentPlacements.Add(placement);
        }
        if (currentKey is VectorBatchKey final)
        {
            batches.Add(new VectorSymbolBatch(
                currentOrder,
                final.TextureId,
                final.Kind,
                final.Paint,
                final.IconPaint,
                final.Opacity,
                currentPlacements.ToArray()));
        }
        return batches.ToArray();
    }

    private bool HasCompleteVectorCoverage(
        long sourceId,
        MapScene scene,
        TimeSpan fadeDuration)
    {
        foreach (TileId id in scene.RequiredTiles)
        {
            if (!_vectorTiles.TryGetValue(
                    new RasterTileKey(sourceId, id),
                    out VectorTileCacheEntry? tile) ||
                Stopwatch.GetElapsedTime(tile.ReadyTimestamp) < fadeDuration)
            {
                return false;
            }
        }
        return scene.RequiredTiles.Count != 0;
    }

    private void CollectVectorAccessibilityFeatures(
        LayerRenderSnapshot layer,
        List<MapAccessibilityFeature> features,
        ref long sceneVersion)
    {
        if (!_rasterLayers.TryGetValue(
                layer.RuntimeId,
                out RasterLayerState? state) ||
            state.Scene is not MapScene scene)
        {
            return;
        }

        sceneVersion = Math.Max(sceneVersion, state.SceneVersion);
        HashSet<int> tileZooms = [scene.TileZoom];
        tileZooms.UnionWith(state.FallbackTileZooms);
        foreach ((RasterTileKey key, VectorTileCacheEntry tile) in _vectorTiles)
        {
            if (key.SourceId != layer.RuntimeId ||
                !tileZooms.Contains(key.Id.Zoom))
            {
                continue;
            }

            VectorTileAccessibilityFeature[] tileFeatures =
                tile.GetAccessibilityFeatures(_displayZoom);
            foreach (VisibleTile instance in GetVisibleCachedTileInstances(
                key.Id,
                _displayLongitude,
                _displayLatitude,
                _displayZoom,
                _viewportWidth,
                _viewportHeight,
                _displayHeading,
                _displayPitch))
            {
                double tileCount = Math.Pow(2, key.Id.Zoom);
                foreach (VectorTileAccessibilityFeature feature in tileFeatures)
                {
                    double x =
                        instance.Left + (feature.X * instance.Size) -
                        (_viewportWidth / 2);
                    double y =
                        instance.Top + (feature.Y * instance.Size) -
                        (_viewportHeight / 2);
                    MapCamera.TransformViewportOffset(
                        x,
                        y,
                        _displayHeading,
                        _displayPitch,
                        _viewportHeight,
                        out x,
                        out y);
                    x += _viewportWidth / 2;
                    y += _viewportHeight / 2;
                    if (x < 0 ||
                        y < 0 ||
                        x > _viewportWidth ||
                        y > _viewportHeight)
                    {
                        continue;
                    }

                    features.Add(new MapAccessibilityFeature(
                        feature.Name,
                        feature.Kind,
                        MapCamera.WorldXToLongitude(
                            (instance.WorldX + feature.X) / tileCount),
                        MapCamera.WorldYToLatitude(
                            (key.Id.Y + feature.Y) / tileCount),
                        feature.StyleLayerOrder,
                        feature.Prominence));
                }
            }
        }
    }

    private void PublishAccessibilitySnapshot(
        MapScene scene,
        List<MapAccessibilityFeature> candidates,
        int style,
        long sceneVersion)
    {
        const int maximumPublishedFeatureCount = 8;
        Dictionary<string, MapAccessibilityFeature> unique =
            _uniqueVectorAccessibilityFeatures;
        unique.Clear();
        foreach (MapAccessibilityFeature candidate in candidates)
        {
            if (!unique.TryGetValue(
                    candidate.Name,
                    out MapAccessibilityFeature? existing) ||
                existing is null ||
                candidate.StyleLayerOrder > existing.StyleLayerOrder ||
                (candidate.StyleLayerOrder == existing.StyleLayerOrder &&
                 candidate.Prominence > existing.Prominence))
            {
                unique[candidate.Name] = candidate;
            }
        }

        List<MapAccessibilityFeature> ordered =
            _orderedVectorAccessibilityFeatures;
        ordered.Clear();
        ordered.AddRange(unique.Values);
        _accessibilityFeatureComparer.Longitude = scene.Longitude;
        _accessibilityFeatureComparer.Latitude = scene.Latitude;
        ordered.Sort(_accessibilityFeatureComparer);
        int featureCount = Math.Min(
            maximumPublishedFeatureCount,
            ordered.Count);
        bool changed = featureCount != _lastAccessibilityFeatureCount;
        for (int index = 0; index < featureCount && !changed; index++)
        {
            changed = !string.Equals(
                ordered[index].Name,
                _lastAccessibilityFeatureNames[index],
                StringComparison.Ordinal);
        }
        if (!changed)
        {
            return;
        }

        MapAccessibilityFeature[] features =
            new MapAccessibilityFeature[featureCount];
        for (int index = 0; index < featureCount; index++)
        {
            MapAccessibilityFeature feature = ordered[index];
            features[index] = feature;
            _lastAccessibilityFeatureNames[index] = feature.Name;
        }
        for (int index = featureCount;
            index < _lastAccessibilityFeatureCount;
            index++)
        {
            _lastAccessibilityFeatureNames[index] = null;
        }
        _lastAccessibilityFeatureCount = featureCount;
        MapControlEventSource.Log.AccessibilitySnapshotPublished(
            style,
            candidates.Count,
            candidates.Count - unique.Count,
            features.Length,
            sceneVersion);
        AccessibilitySnapshotChanged?.Invoke(new MapAccessibilitySnapshot(
            sceneVersion,
            scene.Longitude,
            scene.Latitude,
            scene.Zoom,
            scene.Heading,
            scene.Pitch,
            features));
    }

    private bool ContainsTile(LayerRenderKind renderKind, RasterTileKey key) =>
        renderKind switch
        {
            LayerRenderKind.VectorPoints => _vectorTiles.ContainsKey(key),
            LayerRenderKind.HybridTiles =>
                _vectorTiles.ContainsKey(key) && _rasterTiles.ContainsKey(key),
            _ => _rasterTiles.ContainsKey(key),
        };

    private static bool IsVectorRenderKind(LayerRenderKind renderKind) =>
        renderKind is LayerRenderKind.VectorPoints or LayerRenderKind.HybridTiles;

    private void RemoveVectorTilesLocked(
        long sourceId,
        bool releaseGeometryCaches = false)
    {
        bool removed = false;
        foreach (RasterTileKey key in _vectorTiles.Keys
            .Where(key => key.SourceId == sourceId)
            .ToArray())
        {
            _vectorTiles.Remove(key);
            removed = true;
        }
        if (removed || releaseGeometryCaches)
        {
            OnVectorTilesChanged(disposeGeometryCaches: true);
        }
    }

    private void ReleaseVectorTiles()
    {
        bool hadTiles = _vectorTiles.Count != 0;
        _vectorTiles.Clear();
        foreach (RasterLayerState state in _rasterLayers.Values)
        {
            state.VectorStyleAssets = null;
        }
        if (hadTiles)
        {
            OnVectorTilesChanged();
        }
        DisposeVectorGeometryCaches();
        while (_completedVectorTiles.TryDequeue(out _))
        {
            _pendingRasterUploadCapacity.Release();
        }
    }

    private void TrimVectorTileCache()
    {
        long cacheBytes = _vectorTiles.Values.Sum(tile => tile.ByteSize);
        if (cacheBytes <= MaximumVectorCacheBytes)
        {
            return;
        }

        HashSet<RasterTileKey> protectedKeys = [];
        foreach ((long sourceId, RasterLayerState state) in _rasterLayers)
        {
            if (state.RenderKind is not
                    (LayerRenderKind.VectorPoints or LayerRenderKind.HybridTiles) ||
                state.Scene is null)
            {
                continue;
            }
            protectedKeys.UnionWith(
                state.Scene.RequiredTiles
                    .Where(state.IncludesTile)
                    .Select(id => new RasterTileKey(sourceId, id)));
            MapScene scene = CreateCurrentRasterScene(state.Scene.TileZoom);
            protectedKeys.UnionWith(
                scene.RequiredTiles
                    .Where(state.IncludesTile)
                    .Select(id => new RasterTileKey(sourceId, id)));
            protectedKeys.UnionWith(_vectorTiles.Keys.Where(key =>
                key.SourceId == sourceId &&
                state.FallbackTileZooms.Contains(key.Id.Zoom) &&
                GetVisibleCachedTileInstances(
                    key.Id,
                    _displayLongitude,
                    _displayLatitude,
                    _displayZoom,
                    _viewportWidth,
                    _viewportHeight,
                    _displayHeading,
                    _displayPitch).Count != 0));
        }

        bool removed = false;
        foreach ((RasterTileKey key, VectorTileCacheEntry tile) in _vectorTiles
            .Where(pair => !protectedKeys.Contains(pair.Key))
            .OrderBy(pair => pair.Value.LastUsedTimestamp)
            .ToArray())
        {
            _vectorTiles.Remove(key);
            removed = true;
            if (_rasterLayers.TryGetValue(
                    key.SourceId,
                    out RasterLayerState? state) &&
                state.RenderKind == LayerRenderKind.HybridTiles &&
                _rasterTiles.Remove(key, out TileTexture? texture))
            {
                QueueTextureDisposal(texture);
            }
            cacheBytes -= tile.ByteSize;
            if (cacheBytes <= MaximumVectorCacheBytes)
            {
                break;
            }
        }
        if (removed)
        {
            OnVectorTilesChanged();
        }
    }

    private sealed class VectorTileCacheEntry(
        VectorTileFeatureCollection features,
        VectorStyleAssets styleAssets,
        int style)
    {
        private double _resolvedZoom = double.NaN;
        private double _resolvedTextScaleFactor = double.NaN;
        private VectorSymbolResolution _resolved =
            new([], 0, 0);
        private double _resolvedLineZoom = double.NaN;
        private VectorLineResolution _resolvedLines = new([], 0);
        private double _resolvedPolygonZoom = double.NaN;
        private VectorPolygonResolution _resolvedPolygons = new([], 0);
        private double _resolvedAccessibilityZoom = double.NaN;
        private VectorTileAccessibilityFeature[] _resolvedAccessibility = [];

        internal int Style { get; } = style;

        internal long ReadyTimestamp { get; } = Stopwatch.GetTimestamp();

        internal long LastUsedTimestamp { get; private set; } =
            Stopwatch.GetTimestamp();

        internal long ByteSize => features.ByteSize;

        internal VectorSymbolResolution GetSymbols(
            double zoom,
            double textScaleFactor)
        {
            if (_resolvedZoom != zoom ||
                _resolvedTextScaleFactor != textScaleFactor)
            {
                _resolved = styleAssets.ResolveSymbols(
                    features,
                    zoom,
                    textScaleFactor);
                _resolvedZoom = zoom;
                _resolvedTextScaleFactor = textScaleFactor;
            }
            return _resolved;
        }

        internal VectorLineResolution GetLines(double zoom)
        {
            if (_resolvedLineZoom != zoom)
            {
                _resolvedLines = styleAssets.ResolveLines(features, zoom);
                _resolvedLineZoom = zoom;
            }
            return _resolvedLines;
        }

        internal VectorPolygonResolution GetPolygons(double zoom)
        {
            if (_resolvedPolygonZoom != zoom)
            {
                _resolvedPolygons = styleAssets.ResolvePolygons(features, zoom);
                _resolvedPolygonZoom = zoom;
            }
            return _resolvedPolygons;
        }

        internal VectorTileAccessibilityFeature[] GetAccessibilityFeatures(
            double zoom)
        {
            if (_resolvedAccessibilityZoom != zoom)
            {
                _resolvedAccessibility =
                    styleAssets.ResolveAccessibilityFeatures(features, zoom);
                _resolvedAccessibilityZoom = zoom;
            }
            return _resolvedAccessibility;
        }

        internal void MarkUsed() => LastUsedTimestamp = Stopwatch.GetTimestamp();
    }

    private static int GetFallbackZoomSignature(IReadOnlySet<int> zooms)
    {
        int hash = 17;
        foreach (int zoom in zooms)
        {
            hash ^= HashCode.Combine(zoom);
        }
        return hash;
    }

    private VectorAccessibilityCacheKey CreateVectorAccessibilityCacheKey(
        MapScene scene)
    {
        long sourceStateSignature = 17;
        foreach (LayerRenderSnapshot layer in _layerRenderPlan)
        {
            if (layer.Kind is not (
                    LayerRenderKind.VectorPoints or
                    LayerRenderKind.HybridTiles) ||
                !_rasterLayers.TryGetValue(
                    layer.RuntimeId,
                    out RasterLayerState? state))
            {
                continue;
            }
            sourceStateSignature = HashCode.Combine(
                sourceStateSignature,
                layer.RuntimeId,
                state.SceneVersion,
                GetFallbackZoomSignature(state.FallbackTileZooms));
        }
        return new(
            _vectorTileVersion,
            _layerRenderPlanVersion,
            sourceStateSignature,
            scene.TileZoom,
            _displayLongitude,
            _displayLatitude,
            _displayZoom,
            _displayHeading,
            _displayPitch,
            _viewportWidth,
            _viewportHeight);
    }

    private static int CountPlacements(
        ReadOnlySpan<VectorSymbolPlacement> placements,
        Predicate<VectorSymbolPlacement> predicate)
    {
        int count = 0;
        foreach (VectorSymbolPlacement placement in placements)
        {
            if (predicate(placement))
            {
                count++;
            }
        }
        return count;
    }

    private static void LogVectorRenderResult(
        int style,
        VectorRenderResult renderResult)
    {
        MapControlEventSource.Log.VectorSymbolRenderBatch(
            style,
            renderResult.CandidateCount,
            renderResult.DrawableCount,
            renderResult.EvaluationFailureCount,
            renderResult.UnavailableSpriteCount,
            renderResult.TextureBatchCount,
            renderResult.DrawCallCount);
        MapControlEventSource.Log.VectorLabelRenderBatch(
            style,
            renderResult.GlyphCandidateCount,
            renderResult.DrawableGlyphCount,
            renderResult.EvaluationFailureCount,
            renderResult.UnavailableGlyphCount,
            renderResult.GlyphTextureBatchCount,
            renderResult.GlyphDrawCallCount);
        MapControlEventSource.Log.VectorLabelCollisionSummary(
            style,
            renderResult.CandidateLabelCount,
            renderResult.CandidateLabelCount -
                renderResult.SuppressedLabelCount,
            renderResult.SuppressedLabelCount,
            renderResult.SuppressedGlyphCount);
        MapControlEventSource.Log.VectorLabelTextureReadinessSummary(
            style,
            renderResult.PendingTextureLabelCount,
            renderResult.PendingTextureGlyphCount);
        MapControlEventSource.Log.VectorLabelFadeSummary(
            style,
            renderResult.FadingLabelCount,
            renderResult.FadingGlyphCount);
        MapControlEventSource.Log.VectorLineSymbolPlacementSummary(
            style,
            renderResult.LineSymbolCandidateCount,
            renderResult.LineSymbolProjectedCount,
            renderResult.LineSymbolDrawnCount);
        MapControlEventSource.Log.VectorLineDecorationSummary(
            style,
            2,
            renderResult.PatternLineCandidateCount,
            renderResult.PatternInstanceCount);
        MapControlEventSource.Log.VectorAdvancedSymbolStyleSummary(
            style,
            renderResult.RotatedIconCount,
            renderResult.TintedIconCount,
            renderResult.FittedIconCount,
            renderResult.SortedSymbolCount,
            renderResult.CollisionOverrideSymbolCount);
    }

    private void ClearVectorSymbolFrameCaches()
    {
        foreach (PreparedVectorSymbolFrame frame in
            _vectorSymbolFrameCaches.Values)
        {
            frame.Dispose();
        }
        _vectorSymbolFrameCaches.Clear();
    }

    private void ReleaseVectorSymbolWorkingMemory()
    {
        ClearVectorSymbolFrameCaches();
        VectorSymbolWorkingMemoryStats retained =
            _vectorSymbolWorkspace.GetRetainedMemoryStats();
        int accessibilityCapacity =
            _vectorAccessibilityFeatures.Capacity +
            _orderedVectorAccessibilityFeatures.Capacity +
            _uniqueVectorAccessibilityFeatures.Count;
        if (retained.InstanceBufferCount != 0 ||
            retained.PlacementCapacity != 0 ||
            retained.CollisionCapacity != 0 ||
            accessibilityCapacity != 0)
        {
            MapControlEventSource.Log.VectorSymbolWorkingMemoryReleased(
                retained.InstanceBufferCount,
                retained.InstanceCapacity,
                retained.PlacementCapacity,
                retained.CollisionCapacity,
                accessibilityCapacity);
        }
        _vectorSymbolWorkspace.ReleaseRetainedMemory();
        _vectorAccessibilityFeatures.Clear();
        _vectorAccessibilityFeatures.TrimExcess();
        _uniqueVectorAccessibilityFeatures.Clear();
        _uniqueVectorAccessibilityFeatures.TrimExcess();
        _orderedVectorAccessibilityFeatures.Clear();
        _orderedVectorAccessibilityFeatures.TrimExcess();
        _lastVectorAccessibilityCacheKey = null;
        _lastAccessibilityFeatureCount = 0;
        Array.Clear(_lastAccessibilityFeatureNames);
    }

    private struct VectorRenderResult
    {
        internal int CandidateCount;
        internal int DrawableCount;
        internal int EvaluationFailureCount;
        internal int UnavailableSpriteCount;
        internal int TextureBatchCount;
        internal int DrawCallCount;
        internal int GlyphCandidateCount;
        internal int DrawableGlyphCount;
        internal int UnavailableGlyphCount;
        internal int GlyphTextureBatchCount;
        internal int GlyphDrawCallCount;
        internal int CandidateLabelCount;
        internal int SuppressedLabelCount;
        internal int SuppressedGlyphCount;
        internal int PendingTextureLabelCount;
        internal int PendingTextureGlyphCount;
        internal int LineSymbolCandidateCount;
        internal int LineSymbolProjectedCount;
        internal int LineSymbolDrawnCount;
        internal int PatternLineCandidateCount;
        internal int PatternInstanceCount;
        internal int RotatedIconCount;
        internal int TintedIconCount;
        internal int FittedIconCount;
        internal int SortedSymbolCount;
        internal int CollisionOverrideSymbolCount;
        internal int FadingLabelCount;
        internal int FadingGlyphCount;
    }

    internal readonly record struct LabelCollisionResult(
        HashSet<long> AcceptedGroups,
        int CandidateLabelCount,
        int SuppressedLabelCount,
        int SuppressedGlyphCount);

    private struct LabelCollisionCandidate(
        long collisionGroup,
        int styleLayerOrder,
        double sortKey,
        long collisionFamily,
        int sequence,
        double left,
        double top,
        double right,
        double bottom)
    {
        internal long CollisionGroup { get; } = collisionGroup;
        internal int StyleLayerOrder { get; } = styleLayerOrder;
        internal double SortKey { get; set; } = sortKey;
        internal long CollisionFamily { get; } = collisionFamily;
        internal int Sequence { get; } = sequence;
        internal double Left { get; set; } = left;
        internal double Top { get; set; } = top;
        internal double Right { get; set; } = right;
        internal double Bottom { get; set; } = bottom;
        internal double CollisionPadding { get; set; }
        internal int GlyphCount { get; set; }
        internal bool AllowOverlap { get; set; } = true;
        internal bool IgnorePlacement { get; set; } = true;
    }

    private sealed class LabelCollisionCandidateComparer :
        IComparer<LabelCollisionCandidate>
    {
        internal static LabelCollisionCandidateComparer Instance { get; } = new();

        public int Compare(
            LabelCollisionCandidate x,
            LabelCollisionCandidate y)
        {
            int order = y.StyleLayerOrder.CompareTo(x.StyleLayerOrder);
            if (order != 0)
            {
                return order;
            }
            order = x.SortKey.CompareTo(y.SortKey);
            return order != 0
                ? order
                : x.Sequence.CompareTo(y.Sequence);
        }
    }

    private readonly record struct LabelCollisionRectangle(
        double Left,
        double Top,
        double Right,
        double Bottom,
        long CollisionFamily);

    private readonly record struct VectorTileDrawData(
        int StartIndex,
        int Count,
        double Opacity);

    private readonly record struct VectorBatchKey(
        long TextureId,
        VectorSymbolKind Kind,
        VectorTextPaint Paint,
        VectorIconPaint IconPaint,
        double Opacity);

    private readonly record struct PreparedVectorBatchKey(
        int StyleLayerOrder,
        VectorBatchKey Batch);

    private sealed record PreparedVectorSymbolBatch(
        int StyleLayerOrder,
        long TextureId,
        VectorSymbolKind Kind,
        VectorTextPaint Paint,
        VectorIconPaint IconPaint,
        double Opacity,
        IconInstance[] Instances,
        int InstanceCount,
        int DrawSequence);

    private sealed class PreparedVectorSymbolBatchComparer :
        IComparer<PreparedVectorSymbolBatch>
    {
        internal static PreparedVectorSymbolBatchComparer Instance { get; } =
            new();

        public int Compare(
            PreparedVectorSymbolBatch? x,
            PreparedVectorSymbolBatch? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }
            if (x is null)
            {
                return -1;
            }
            if (y is null)
            {
                return 1;
            }
            int styleOrder = x.StyleLayerOrder.CompareTo(y.StyleLayerOrder);
            return styleOrder != 0
                ? styleOrder
                : x.DrawSequence.CompareTo(y.DrawSequence);
        }
    }

    private sealed class PreparedVectorSymbolFrame(
        VectorSymbolRenderWorkspace workspace,
        VectorSymbolRenderCacheKey key,
        PreparedVectorSymbolBatch[] batches,
        VectorRenderResult renderResult) : IDisposable
    {
        internal VectorSymbolRenderCacheKey Key { get; } = key;
        internal PreparedVectorSymbolBatch[] Batches { get; } = batches;
        internal VectorRenderResult RenderResult = renderResult;

        public void Dispose() => workspace.ReturnPreparedVectorBatches(Batches);
    }

    private sealed class WorkingVectorSymbolBatch
    {
        internal PreparedVectorBatchKey Key { get; private set; }
        internal List<IconInstance> Instances { get; } = [];
        internal int LinePlacementCount { get; private set; }

        internal void Add(IconInstance instance, bool isLinePlacement)
        {
            Instances.Add(instance);
            LinePlacementCount += isLinePlacement ? 1 : 0;
        }

        internal void Reset(PreparedVectorBatchKey key)
        {
            Key = key;
            Instances.Clear();
            LinePlacementCount = 0;
        }
    }

    private sealed class WorkingVectorSymbolBatchComparer :
        IComparer<WorkingVectorSymbolBatch>
    {
        internal static WorkingVectorSymbolBatchComparer Instance { get; } =
            new();

        public int Compare(
            WorkingVectorSymbolBatch? x,
            WorkingVectorSymbolBatch? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }
            if (x is null)
            {
                return -1;
            }
            if (y is null)
            {
                return 1;
            }
            int order = x.Key.StyleLayerOrder.CompareTo(
                y.Key.StyleLayerOrder);
            return order != 0
                ? order
                : x.Key.Batch.Kind.CompareTo(y.Key.Batch.Kind);
        }
    }

    private sealed class AccessibilityFeatureComparer :
        IComparer<MapAccessibilityFeature>
    {
        internal double Longitude { get; set; }
        internal double Latitude { get; set; }

        public int Compare(
            MapAccessibilityFeature? x,
            MapAccessibilityFeature? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }
            if (x is null)
            {
                return -1;
            }
            if (y is null)
            {
                return 1;
            }
            int order = y.StyleLayerOrder.CompareTo(x.StyleLayerOrder);
            if (order != 0)
            {
                return order;
            }
            order = y.Prominence.CompareTo(x.Prominence);
            if (order != 0)
            {
                return order;
            }
            double xDistance =
                Math.Abs(x.Longitude - Longitude) +
                Math.Abs(x.Latitude - Latitude);
            double yDistance =
                Math.Abs(y.Longitude - Longitude) +
                Math.Abs(y.Latitude - Latitude);
            return xDistance.CompareTo(yDistance);
        }
    }

    private readonly record struct VectorSymbolRenderCacheKey(
        LayerRenderSnapshot Layer,
        long VectorTileVersion,
        long IconTextureVersion,
        int DeviceEpoch,
        long SceneVersion,
        int FallbackZoomSignature,
        double Longitude,
        double Latitude,
        double Zoom,
        double Heading,
        double Pitch,
        double ViewportWidth,
        double ViewportHeight,
        double TextScaleFactor);

    private readonly record struct VectorAccessibilityCacheKey(
        long VectorTileVersion,
        long LayerRenderPlanVersion,
        long SourceStateSignature,
        int TileZoom,
        double Longitude,
        double Latitude,
        double Zoom,
        double Heading,
        double Pitch,
        double ViewportWidth,
        double ViewportHeight);

    private readonly record struct VectorSymbolWorkingMemoryStats(
        int InstanceBufferCount,
        int InstanceCapacity,
        int PlacementCapacity,
        int CollisionCapacity);

    private sealed class VectorSymbolRenderWorkspace
    {
        private readonly Stack<List<LabelCollisionRectangle>>
            _availableCollisionCells = [];
        private readonly Stack<WorkingVectorSymbolBatch>
            _availableWorkingBatches = [];
        private readonly Dictionary<int, Stack<IconInstance[]>>
            _availableInstanceBuffers = [];

        internal List<VectorTileDrawData> DrawTiles { get; } = [];
        internal List<VectorSymbolPlacement> Placements { get; } = [];
        internal List<VectorSymbolPlacement> DrawablePlacements { get; } = [];
        internal HashSet<long> IncompleteLabelGroups { get; } = [];
        internal HashSet<(long SymbolGroupId, int PlacementIndex)>
            SplitCollisionGroups { get; } = [];
        internal Dictionary<
            (long SymbolGroupId, int PlacementIndex, int Component),
            long> CollisionGroups { get; } = [];
        internal Dictionary<(long SymbolGroupId, int PlacementIndex), long>
            CollisionFamilies { get; } = [];
        internal List<LabelCollisionCandidate> CollisionCandidates { get; } = [];
        internal Dictionary<long, int> CollisionCandidateIndexes { get; } = [];
        internal HashSet<long> AcceptedCollisionGroups { get; } = [];
        internal Dictionary<long, List<LabelCollisionRectangle>>
            OccupiedCollisionCells { get; } = [];
        internal Dictionary<PreparedVectorBatchKey, int>
            WorkingBatchIndexes { get; } = [];
        internal List<WorkingVectorSymbolBatch> WorkingBatches { get; } = [];
        internal HashSet<int> OrderSensitiveLayers { get; } = [];
        internal List<VectorSymbolPlacement> OrderSensitivePlacements { get; } =
            [];
        internal int SuppressedGlyphCount { get; set; }

        internal void ResetFrame()
        {
            DrawTiles.Clear();
            Placements.Clear();
            DrawablePlacements.Clear();
            IncompleteLabelGroups.Clear();
        }

        internal void ResetBatches()
        {
            WorkingBatchIndexes.Clear();
            foreach (WorkingVectorSymbolBatch batch in WorkingBatches)
            {
                _availableWorkingBatches.Push(batch);
            }
            WorkingBatches.Clear();
            OrderSensitiveLayers.Clear();
            OrderSensitivePlacements.Clear();
        }

        internal WorkingVectorSymbolBatch RentWorkingBatch(
            PreparedVectorBatchKey key)
        {
            WorkingVectorSymbolBatch batch =
                _availableWorkingBatches.TryPop(
                    out WorkingVectorSymbolBatch? available)
                    ? available
                    : new WorkingVectorSymbolBatch();
            batch.Reset(key);
            return batch;
        }

        internal void ClearOccupiedCollisionCells()
        {
            foreach (List<LabelCollisionRectangle> cell in
                OccupiedCollisionCells.Values)
            {
                cell.Clear();
                _availableCollisionCells.Push(cell);
            }
            OccupiedCollisionCells.Clear();
        }

        internal List<LabelCollisionRectangle> RentCollisionCell() =>
            _availableCollisionCells.TryPop(out List<LabelCollisionRectangle>? cell)
                ? cell
                : [];

        internal IconInstance[] RentInstanceBuffer(int minimumLength)
        {
            int length = (int)BitOperations.RoundUpToPowerOf2(
                checked((uint)Math.Max(minimumLength, 16)));
            if (_availableInstanceBuffers.TryGetValue(
                    length,
                    out Stack<IconInstance[]>? buffers) &&
                buffers.TryPop(out IconInstance[]? buffer))
            {
                return buffer;
            }
            return new IconInstance[length];
        }

        internal void ReturnPreparedVectorBatches(
            IEnumerable<PreparedVectorSymbolBatch> batches)
        {
            foreach (PreparedVectorSymbolBatch batch in batches)
            {
                int length = batch.Instances.Length;
                if (!_availableInstanceBuffers.TryGetValue(
                        length,
                        out Stack<IconInstance[]>? buffers))
                {
                    buffers = [];
                    _availableInstanceBuffers.Add(length, buffers);
                }
                buffers.Push(batch.Instances);
            }
        }

        internal VectorSymbolWorkingMemoryStats GetRetainedMemoryStats()
        {
            int instanceBufferCount = 0;
            int instanceCapacity = 0;
            foreach (Stack<IconInstance[]> buffers in
                _availableInstanceBuffers.Values)
            {
                instanceBufferCount += buffers.Count;
                foreach (IconInstance[] buffer in buffers)
                {
                    instanceCapacity += buffer.Length;
                }
            }
            foreach (WorkingVectorSymbolBatch batch in WorkingBatches)
            {
                instanceCapacity += batch.Instances.Capacity;
            }
            foreach (WorkingVectorSymbolBatch batch in _availableWorkingBatches)
            {
                instanceCapacity += batch.Instances.Capacity;
            }
            return new VectorSymbolWorkingMemoryStats(
                instanceBufferCount,
                instanceCapacity,
                Placements.Capacity +
                    DrawablePlacements.Capacity +
                    OrderSensitivePlacements.Capacity,
                CollisionCandidates.Capacity +
                    CollisionCandidateIndexes.Count +
                    AcceptedCollisionGroups.Count +
                    OccupiedCollisionCells.Count);
        }

        internal void ReleaseRetainedMemory()
        {
            foreach (WorkingVectorSymbolBatch batch in WorkingBatches)
            {
                batch.Instances.Clear();
                batch.Instances.TrimExcess();
            }
            foreach (WorkingVectorSymbolBatch batch in _availableWorkingBatches)
            {
                batch.Instances.Clear();
                batch.Instances.TrimExcess();
            }
            foreach (List<LabelCollisionRectangle> cell in
                OccupiedCollisionCells.Values)
            {
                cell.Clear();
                cell.TrimExcess();
            }
            foreach (List<LabelCollisionRectangle> cell in
                _availableCollisionCells)
            {
                cell.Clear();
                cell.TrimExcess();
            }

            DrawTiles.Clear();
            DrawTiles.TrimExcess();
            Placements.Clear();
            Placements.TrimExcess();
            DrawablePlacements.Clear();
            DrawablePlacements.TrimExcess();
            CollisionCandidates.Clear();
            CollisionCandidates.TrimExcess();
            OrderSensitivePlacements.Clear();
            OrderSensitivePlacements.TrimExcess();
            WorkingBatches.Clear();
            WorkingBatches.TrimExcess();
            _availableWorkingBatches.Clear();
            _availableWorkingBatches.TrimExcess();
            _availableCollisionCells.Clear();
            _availableCollisionCells.TrimExcess();
            _availableInstanceBuffers.Clear();
            _availableInstanceBuffers.TrimExcess();
            IncompleteLabelGroups.Clear();
            IncompleteLabelGroups.TrimExcess();
            SplitCollisionGroups.Clear();
            SplitCollisionGroups.TrimExcess();
            CollisionGroups.Clear();
            CollisionGroups.TrimExcess();
            CollisionFamilies.Clear();
            CollisionFamilies.TrimExcess();
            CollisionCandidateIndexes.Clear();
            CollisionCandidateIndexes.TrimExcess();
            AcceptedCollisionGroups.Clear();
            AcceptedCollisionGroups.TrimExcess();
            OccupiedCollisionCells.Clear();
            OccupiedCollisionCells.TrimExcess();
            WorkingBatchIndexes.Clear();
            WorkingBatchIndexes.TrimExcess();
            OrderSensitiveLayers.Clear();
            OrderSensitiveLayers.TrimExcess();
        }
    }

    private readonly record struct QueuedVectorTile(
        VectorTileData Tile,
        long Reservation);
}
