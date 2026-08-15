using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
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
    private long _vectorTileVersion;

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
        List<VectorTileDrawData> drawTiles = [];
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
            drawTiles,
            ref renderResult,
            ref nextCollisionGroup);
        if (canEnumerateActiveScene)
        {
            MapScene scene = CreateCurrentRasterScene(state.Scene.TileZoom);
            activeFade |= CollectVectorScene(
                layer,
                scene,
                drawTiles,
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

        VectorSymbolPlacement[] allPlacements =
            drawTiles.SelectMany(tile => tile.Placements).ToArray();
        HashSet<long> incompleteLabelGroups = FindIncompleteLabelGroups(
            allPlacements,
            _iconTextures.ContainsKey,
            out int pendingTextureGlyphCount);
        VectorSymbolPlacement[] readyPlacements = allPlacements
            .Where(placement =>
                placement.CollisionGroup < 0 ||
                !incompleteLabelGroups.Contains(placement.CollisionGroup))
            .ToArray();
        LabelCollisionResult collision = ResolveLabelCollisions(readyPlacements);
        renderResult.CandidateLabelCount = collision.CandidateLabelCount;
        renderResult.SuppressedLabelCount = collision.SuppressedLabelCount;
        renderResult.SuppressedGlyphCount = collision.SuppressedGlyphCount;
        renderResult.PendingTextureLabelCount = incompleteLabelGroups.Count;
        renderResult.PendingTextureGlyphCount = pendingTextureGlyphCount;
        int fadingLabelCount = 0;
        int fadingGlyphCount = 0;
        List<VectorSymbolPlacement> drawablePlacements = [];
        foreach (VectorTileDrawData drawTile in drawTiles)
        {
            VectorSymbolPlacement[] placements = drawTile.Placements
                .Where(placement =>
                    placement.CollisionGroup < 0 ||
                    collision.AcceptedGroups.Contains(
                        placement.CollisionGroup))
                .ToArray();
            activeFade |= ApplyLabelTextureFade(
                placements,
                layer.FadeDuration,
                ref fadingLabelCount,
                ref fadingGlyphCount);
            drawablePlacements.AddRange(placements.Select(placement =>
                placement with
                {
                    Opacity = placement.Opacity * drawTile.Opacity,
                }));
        }
        DrawVectorPlacements(
            context,
            drawablePlacements.ToArray(),
            1,
            ref renderResult);

        MapControlEventSource.Log.VectorSymbolRenderBatch(
            layer.Style,
            renderResult.CandidateCount,
            renderResult.DrawableCount,
            renderResult.EvaluationFailureCount,
            renderResult.UnavailableSpriteCount,
            renderResult.TextureBatchCount,
            renderResult.DrawCallCount);
        MapControlEventSource.Log.VectorLabelRenderBatch(
            layer.Style,
            renderResult.GlyphCandidateCount,
            renderResult.DrawableGlyphCount,
            renderResult.EvaluationFailureCount,
            renderResult.UnavailableGlyphCount,
            renderResult.GlyphTextureBatchCount,
            renderResult.GlyphDrawCallCount);
        MapControlEventSource.Log.VectorLabelCollisionSummary(
            layer.Style,
            renderResult.CandidateLabelCount,
            renderResult.CandidateLabelCount -
                renderResult.SuppressedLabelCount,
            renderResult.SuppressedLabelCount,
            renderResult.SuppressedGlyphCount);
        MapControlEventSource.Log.VectorLabelTextureReadinessSummary(
            layer.Style,
            renderResult.PendingTextureLabelCount,
            renderResult.PendingTextureGlyphCount);
        MapControlEventSource.Log.VectorLabelFadeSummary(
            layer.Style,
            fadingLabelCount,
            fadingGlyphCount);
        MapControlEventSource.Log.VectorLineSymbolPlacementSummary(
            layer.Style,
            renderResult.LineSymbolCandidateCount,
            renderResult.LineSymbolProjectedCount,
            renderResult.LineSymbolDrawnCount);
        MapControlEventSource.Log.VectorLineDecorationSummary(
            layer.Style,
            2,
            renderResult.PatternLineCandidateCount,
            renderResult.PatternInstanceCount);
        MapControlEventSource.Log.VectorAdvancedSymbolStyleSummary(
            layer.Style,
            renderResult.RotatedIconCount,
            renderResult.TintedIconCount,
            renderResult.FittedIconCount,
            renderResult.SortedSymbolCount,
            renderResult.CollisionOverrideSymbolCount);
        return activeFade;
    }

    private bool CollectVectorScene(
        LayerRenderSnapshot layer,
        MapScene scene,
        List<VectorTileDrawData> drawTiles,
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
                    drawTiles,
                    ref renderResult,
                    ref nextCollisionGroup);
            }
        }
        return activeFade;
    }

    private bool CollectCachedVectorLevels(
        LayerRenderSnapshot layer,
        IReadOnlySet<int> tileZooms,
        List<VectorTileDrawData> drawTiles,
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
                    drawTiles,
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
        List<VectorTileDrawData> drawTiles,
        ref VectorRenderResult renderResult,
        ref long nextCollisionGroup)
    {
        tile.MarkUsed();
        VectorSymbolResolution resolution = tile.GetSymbols(_displayZoom);
        VectorTileSymbol[] symbols = resolution.Symbols;
        renderResult.CandidateCount +=
            symbols.Count(symbol => symbol.Kind == VectorSymbolKind.Icon);
        renderResult.GlyphCandidateCount += resolution.ResolvedGlyphCount;
        renderResult.EvaluationFailureCount +=
            resolution.EvaluationFailureCount;
        renderResult.UnavailableSpriteCount +=
            resolution.UnavailableSpriteCount;
        renderResult.UnavailableGlyphCount +=
            resolution.UnavailableGlyphCount;
        renderResult.LineSymbolCandidateCount +=
            symbols.Count(symbol => symbol.LinePoints is not null);
        double opacity = ComputeLayerTileOpacity(
            Stopwatch.GetElapsedTime(tile.ReadyTimestamp),
            layer.FadeDuration,
            layer.Opacity);
        VectorSymbolPlacement[] placements = ProjectVectorSymbols(
            symbols,
            visibleTile,
            _viewportWidth,
            _viewportHeight,
            _displayHeading,
            _displayPitch);
        renderResult.LineSymbolProjectedCount +=
            placements.Count(placement => placement.IsLinePlacement);
        renderResult.PatternLineCandidateCount +=
            symbols.Count(symbol => symbol.ContinuousLinePlacement);
        renderResult.PatternInstanceCount +=
            placements.Count(placement =>
                placement.IsContinuousLinePlacement);
        renderResult.RotatedIconCount += symbols.Count(symbol =>
            symbol.Kind == VectorSymbolKind.Icon &&
            Math.Abs(symbol.Rotation) > 1e-7);
        renderResult.TintedIconCount += symbols.Count(symbol =>
            symbol.Kind == VectorSymbolKind.Icon &&
            symbol.IconPaint.IsTinted);
        renderResult.FittedIconCount += symbols.Count(symbol =>
            symbol.Kind == VectorSymbolKind.Icon &&
            symbol.TextFit != VectorIconTextFit.None);
        renderResult.SortedSymbolCount += symbols.Count(symbol =>
            Math.Abs(symbol.SortKey) > 1e-7);
        renderResult.CollisionOverrideSymbolCount += symbols.Count(symbol =>
            symbol.AllowOverlap ||
            symbol.IgnorePlacement ||
            symbol.Optional);
        if (placements.Length == 0)
        {
            return opacity < layer.Opacity;
        }

        AssignSymbolCollisionGroups(placements, ref nextCollisionGroup);
        drawTiles.Add(new VectorTileDrawData(placements, opacity));
        return opacity < layer.Opacity;
    }

    internal static void AssignSymbolCollisionGroups(
        VectorSymbolPlacement[] placements,
        ref long nextCollisionGroup)
    {
        HashSet<(long SymbolGroupId, int PlacementIndex)> splitGroups = [
            .. placements
                .Where(placement =>
                    placement.SymbolGroupId >= 0 &&
                    placement.Optional)
                .Select(placement => (
                    placement.SymbolGroupId,
                    placement.PlacementIndex)),
        ];
        Dictionary<(long SymbolGroupId, int PlacementIndex, int Component),
            long> collisionGroups = [];
        Dictionary<(long SymbolGroupId, int PlacementIndex), long>
            collisionFamilies = [];
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

    private unsafe void DrawVectorPlacements(
        IntPtr context,
        VectorSymbolPlacement[] placements,
        double opacity,
        ref VectorRenderResult renderResult)
    {
        if (placements.Length == 0)
        {
            return;
        }

        VectorSymbolBatch[] batches = BatchVectorSymbolsByTexture(placements);
        SetBlendState(context, _premultipliedBlendStatePointer);
        SetInputLayout(context, _iconInputLayoutPointer);
        SetVertexBuffers(
            context,
            _vertexBufferPointer,
            (uint)Marshal.SizeOf<TileVertex>(),
            _iconInstanceBufferPointer,
            (uint)Marshal.SizeOf<IconInstance>());
        SetVertexShader(context, _iconVertexShaderPointer);

        foreach (VectorSymbolBatch batch in batches)
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
                        (float)(opacity * batch.Opacity),
                        (float)batch.Paint.HaloOffset,
                        0,
                        0))
                : new TileConstants(
                    new Vector4(1, 1, 0, 0),
                    batch.IconPaint.Color,
                    new Vector4(1, 0, 1, 0),
                    new Vector4(
                        (float)(opacity * batch.Opacity),
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
            IconInstance[] instances = new IconInstance[batch.Placements.Length];
            for (int index = 0; index < instances.Length; index++)
            {
                VectorSymbolPlacement placement = batch.Placements[index];
                instances[index] = CreateIconInstance(
                    placement.Left,
                    placement.Top,
                    placement.Width,
                    placement.Height,
                    placement.Rotation);
            }
            renderResult.LineSymbolDrawnCount += batch.Placements.Count(
                placement => placement.IsLinePlacement);

            if (batch.Kind == VectorSymbolKind.Text)
            {
                renderResult.DrawableGlyphCount += instances.Length;
                renderResult.GlyphTextureBatchCount++;
            }
            else
            {
                renderResult.DrawableCount += instances.Length;
                renderResult.TextureBatchCount++;
            }
            Span<IconInstance> remaining = instances;
            while (!remaining.IsEmpty)
            {
                Span<IconInstance> chunk = remaining[..Math.Min(
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
                if (batch.Kind == VectorSymbolKind.Text)
                {
                    renderResult.GlyphDrawCallCount++;
                }
                else
                {
                    renderResult.DrawCallCount++;
                }
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
        Dictionary<
            (long SymbolGroupId, int StyleLayerOrder, VectorTilePoint[] Path),
            List<VectorTileSymbol>>
            lineGroups = [];
        foreach (VectorTileSymbol symbol in symbols)
        {
            if (symbol.LinePoints is not { Length: >= 2 } linePoints)
            {
                AddProjectedPointSymbol(
                    symbol,
                    tile,
                    viewportWidth,
                    viewportHeight,
                    heading,
                    pitch,
                    projected);
                continue;
            }

            long symbolGroupId = symbol.SymbolGroupId >= 0
                ? symbol.SymbolGroupId
                : symbol.LabelId;
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
        return projected.ToArray();
    }

    internal static HashSet<long> FindIncompleteLabelGroups(
        IEnumerable<VectorSymbolPlacement> placements,
        Predicate<long> isTextureAvailable,
        out int pendingGlyphCount)
    {
        HashSet<long> incompleteGroups = [];
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
            return incompleteGroups;
        }

        pendingGlyphCount = placements.Count(placement =>
            placement.Kind == VectorSymbolKind.Text &&
            incompleteGroups.Contains(placement.CollisionGroup));
        return incompleteGroups;
    }

    private bool ApplyLabelTextureFade(
        VectorSymbolPlacement[] placements,
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

    private static void AddProjectedPointSymbol(
        VectorTileSymbol symbol,
        VisibleTile tile,
        double viewportWidth,
        double viewportHeight,
        double heading,
        double pitch,
        List<VectorSymbolPlacement> projected)
    {
            double x = tile.Left + (symbol.X * tile.Size) - (viewportWidth / 2);
            double y = tile.Top + (symbol.Y * tile.Size) - (viewportHeight / 2);
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
                Optional: symbol.Optional);
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
            bool reverse = !continuousPlacement && centerTangent.X < 0;
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
                        Math.PI / 4 ||
                    maximumRelativeRotation - minimumRelativeRotation >
                        Math.PI / 4)
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
            if (valid)
            {
                projected.AddRange(candidate);
            }
        }
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
        const double collisionPadding = 2;
        const double gridCellSize = 64;
        Dictionary<long, LabelCollisionCandidate> candidates = [];
        int sequence = 0;
        foreach (VectorSymbolPlacement placement in placements)
        {
            if (placement.CollisionGroup < 0)
            {
                continue;
            }
            if (!candidates.TryGetValue(
                    placement.CollisionGroup,
                    out LabelCollisionCandidate? candidate))
            {
                GetVectorSymbolBounds(
                    placement,
                    out double left,
                    out double top,
                    out double right,
                    out double bottom);
                candidate = new LabelCollisionCandidate(
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
                    bottom);
                candidates.Add(placement.CollisionGroup, candidate);
            }
            else
            {
                GetVectorSymbolBounds(
                    placement,
                    out double left,
                    out double top,
                    out double right,
                    out double bottom);
                candidate.Left = Math.Min(candidate.Left, left);
                candidate.Top = Math.Min(candidate.Top, top);
                candidate.Right = Math.Max(candidate.Right, right);
                candidate.Bottom = Math.Max(candidate.Bottom, bottom);
            }
            candidate.SortKey = Math.Min(candidate.SortKey, placement.SortKey);
            candidate.AllowOverlap &= placement.AllowOverlap;
            candidate.IgnorePlacement &= placement.IgnorePlacement;
            if (placement.Kind == VectorSymbolKind.Text)
            {
                candidate.GlyphCount++;
            }
        }

        HashSet<long> acceptedGroups = [];
        Dictionary<long, List<LabelCollisionRectangle>> occupiedCells = [];
        int suppressedGlyphCount = 0;
        foreach (LabelCollisionCandidate candidate in candidates.Values
            .OrderByDescending(candidate => candidate.StyleLayerOrder)
            .ThenBy(candidate => candidate.SortKey)
            .ThenBy(candidate => candidate.Sequence))
        {
            LabelCollisionRectangle bounds = new(
                candidate.Left - collisionPadding,
                candidate.Top - collisionPadding,
                candidate.Right + collisionPadding,
                candidate.Bottom + collisionPadding,
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
                suppressedGlyphCount += candidate.GlyphCount;
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
                        occupied = [];
                        occupiedCells.Add(cell, occupied);
                    }
                    occupied.Add(bounds);
                }
            }
        }
        return new LabelCollisionResult(
            acceptedGroups,
            candidates.Count,
            candidates.Count - acceptedGroups.Count,
            suppressedGlyphCount);
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

    private bool ContainsTile(LayerRenderKind renderKind, RasterTileKey key) =>
        renderKind switch
        {
            LayerRenderKind.VectorPoints => _vectorTiles.ContainsKey(key),
            LayerRenderKind.HybridTiles =>
                _vectorTiles.ContainsKey(key) && _rasterTiles.ContainsKey(key),
            _ => _rasterTiles.ContainsKey(key),
        };

    private void RemoveVectorTilesLocked(long sourceId)
    {
        bool removed = false;
        foreach (RasterTileKey key in _vectorTiles.Keys
            .Where(key => key.SourceId == sourceId)
            .ToArray())
        {
            _vectorTiles.Remove(key);
            removed = true;
        }
        if (removed)
        {
            OnVectorTilesChanged(disposeGeometryCaches: true);
        }
    }

    private void ReleaseVectorTiles()
    {
        bool hadTiles = _vectorTiles.Count != 0;
        _vectorTiles.Clear();
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
            MapScene scene = CreateCurrentRasterScene(state.Scene.TileZoom);
            protectedKeys.UnionWith(
                scene.RequiredTiles.Select(id => new RasterTileKey(sourceId, id)));
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
        AzureVectorStyleAssets styleAssets,
        int style)
    {
        private double _resolvedZoom = double.NaN;
        private VectorSymbolResolution _resolved =
            new([], 0, 0);
        private double _resolvedLineZoom = double.NaN;
        private VectorLineResolution _resolvedLines = new([], 0);
        private double _resolvedPolygonZoom = double.NaN;
        private VectorPolygonResolution _resolvedPolygons = new([], 0);

        internal int Style { get; } = style;

        internal long ReadyTimestamp { get; } = Stopwatch.GetTimestamp();

        internal long LastUsedTimestamp { get; private set; } =
            Stopwatch.GetTimestamp();

        internal long ByteSize => features.ByteSize;

        internal VectorSymbolResolution GetSymbols(double zoom)
        {
            if (_resolvedZoom != zoom)
            {
                _resolved = styleAssets.ResolveSymbols(features, zoom);
                _resolvedZoom = zoom;
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

        internal void MarkUsed() => LastUsedTimestamp = Stopwatch.GetTimestamp();
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
    }

    internal readonly record struct LabelCollisionResult(
        HashSet<long> AcceptedGroups,
        int CandidateLabelCount,
        int SuppressedLabelCount,
        int SuppressedGlyphCount);

    private sealed class LabelCollisionCandidate(
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
        internal int GlyphCount { get; set; }
        internal bool AllowOverlap { get; set; } = true;
        internal bool IgnorePlacement { get; set; } = true;
    }

    private readonly record struct LabelCollisionRectangle(
        double Left,
        double Top,
        double Right,
        double Bottom,
        long CollisionFamily);

    private sealed record VectorTileDrawData(
        VectorSymbolPlacement[] Placements,
        double Opacity);

    private readonly record struct VectorBatchKey(
        long TextureId,
        VectorSymbolKind Kind,
        VectorTextPaint Paint,
        VectorIconPaint IconPaint,
        double Opacity);

    private readonly record struct QueuedVectorTile(
        VectorTileData Tile,
        long Reservation);
}
