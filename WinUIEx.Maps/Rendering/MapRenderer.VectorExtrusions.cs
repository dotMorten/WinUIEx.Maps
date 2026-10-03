using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using WinUIEx.Maps.Rendering.Diagnostics;
using static WinUIEx.Maps.Rendering.DirectXInterop;

namespace WinUIEx.Maps.Rendering;

internal sealed partial class MapRenderer
{
    private ExtrusionState? _extrusions;
    private bool _drawingExtrusions;
    private bool _extrusionCoverageInvalidated;
    private int _vectorOrderMinimum = int.MinValue, _vectorOrderMaximum = int.MaxValue;

    private bool IsVectorOrderVisible(int order) =>
        order >= _vectorOrderMinimum && order < _vectorOrderMaximum;

    internal MapScene CreateVectorRequestScene(long sourceId, MapScene scene)
    {
        lock (RenderLock)
        {
            return _rasterLayers.TryGetValue(sourceId, out var state) && state.VectorStyleAssets?.HasExtrusions == true
                ? CreateExtrusionScene(scene)
                : MapCamera.CreateLabelCollisionScene(scene);
        }
    }

    private static MapScene CreateExtrusionScene(MapScene scene) => MapCamera.CreateScene(
        scene.Longitude, scene.Latitude, scene.Zoom, scene.TileZoom, scene.ViewportWidth,
        scene.ViewportHeight, scene.Heading, scene.Pitch, MapCamera.LabelCollisionMargin,
        includeElevatedGeometry: true);

    internal long ExtrusionResourceBytesForTest => _extrusions?.Resources?.ByteSize ?? 0;
    internal long ExtrusionMeshBytesForTest =>
        _extrusions?.Frames.Values.Sum(frame =>
            frame.Batches.Select(batch => batch.Buffer).Distinct().Sum(buffer => buffer.ByteSize)) ?? 0;

    private sealed class ExtrusionState : IDisposable
    {
        internal readonly Dictionary<long, ExtrusionFrame> Frames = [];
        internal ExtrusionResources? Resources;
        public void Dispose()
        {
            foreach (var frame in Frames.Values)
                frame.Dispose();
            Frames.Clear();
            Resources?.Dispose();
            Resources = null;
        }
    }

    private sealed record ExtrusionBatch(VisibleTile Tile, int Order, long TextureId,
        double PatternWidth, double PatternHeight, ExtrusionMeshBuffer Buffer)
    {
        internal ReliefRectangle Clip { get; init; } = new(0, 0, 1, 1);
    }
    private sealed record ExtrusionFrame(
        VectorGeometryPreparationKey Key, VectorStyleAssets Assets, ExtrusionBatch[] Batches, int Failures) : IDisposable
    {
        public void Dispose()
        {
            foreach (var buffer in Batches.Select(batch => batch.Buffer).Distinct())
                buffer.Dispose();
        }
    }

    private static ExtrusionBatch[] BuildExtrusionBatches(
        VectorGeometryPreparationInput input, CancellationToken cancellationToken)
    {
        List<ExtrusionBatch> result = [];
        try
        {
            foreach (var tile in input.ExtrusionTiles!)
            {
                Dictionary<(int Order, long Texture, double Width, double Height), ExtrusionMeshBuffer> buffers = [];
                try
                {
                    foreach (var extrusion in tile.Resolution.Extrusions)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var key = (extrusion.Order, extrusion.Paint.TextureId,
                            extrusion.Paint.PatternWidth, extrusion.Paint.PatternHeight);
                        if (!buffers.TryGetValue(key, out var buffer))
                            buffers.Add(key, buffer = new());
                        VectorExtrusionGeometry.Append(tile.Tile.Id, extrusion, buffer, cancellationToken, input.Layer.TileSize);
                    }
                    foreach (var pair in buffers)
                    {
                        pair.Value.Upload(input.DevicePointer, cancellationToken);
                    }
                    foreach (var pair in buffers)
                        result.Add(new(tile.Tile, pair.Key.Order, pair.Key.Texture,
                            pair.Key.Width, pair.Key.Height, pair.Value));
                    buffers.Clear();
                }
                finally
                {
                    foreach (var buffer in buffers.Values)
                        buffer.Dispose();
                }
            }
            return result.ToArray();
        }
        catch
        {
            foreach (var batch in result)
                batch.Buffer.Dispose();
            throw;
        }
    }

    private void AcceptExtrusionFrame(LayerRenderSnapshot layer, RasterLayerState state, PreparedVectorGeometryFrame prepared)
    {
        if (prepared.ExtrusionBatches is not { } batches)
            return;
        _extrusions ??= new();
        if (_extrusions.Frames.Remove(layer.RuntimeId, out var previous))
        {
            List<ExtrusionBatch> retained = [.. batches];
            if (previous.Key.Generation == state.Generation &&
                state.VectorStyleAssets!.CanReuseExtrusions(previous.Key.Zoom, _displayZoom))
            {
                Dictionary<int, HashSet<(TileId Id, int WorldX)>> visibleByZoom = [];
                foreach (var batch in previous.Batches)
                {
                    if (!visibleByZoom.TryGetValue(batch.Tile.Id.Zoom, out var visible))
                    {
                        visible = CreateExtrusionScene(CreateCurrentRasterScene(batch.Tile.Id.Zoom))
                            .VisibleTiles.Select(tile => (tile.Id, tile.WorldX)).ToHashSet();
                        visibleByZoom.Add(batch.Tile.Id.Zoom, visible);
                    }
                    if (!_vectorTiles.ContainsKey(new(layer.RuntimeId, batch.Tile.Id)) ||
                        !visible.Contains((batch.Tile.Id, batch.Tile.WorldX)))
                        continue;
                    List<ReliefRectangle> uncovered = [batch.Clip];
                    foreach (var tile in prepared.IncludedTiles)
                    {
                        double factor = Math.Pow(2, batch.Tile.Id.Zoom - tile.Tile.Id.Zoom);
                        ReliefRectangle replacement = new(
                            tile.WorldX * factor - batch.Tile.WorldX,
                            tile.Tile.Id.Y * factor - batch.Tile.Id.Y,
                            (tile.WorldX + 1) * factor - batch.Tile.WorldX,
                            (tile.Tile.Id.Y + 1) * factor - batch.Tile.Id.Y);
                        List<ReliefRectangle> next = [];
                        foreach (var region in uncovered)
                            region.Subtract(replacement, next);
                        uncovered = next;
                    }
                    foreach (var region in uncovered)
                        retained.Add(batch with { Clip = region });
                }
            }
            var retainedBuffers = retained.Select(batch => batch.Buffer).ToHashSet();
            foreach (var buffer in previous.Batches.Select(batch => batch.Buffer).Distinct())
                if (!retainedBuffers.Contains(buffer))
                    buffer.Dispose();
            batches = retained.ToArray();
        }
        _extrusions.Frames.Add(layer.RuntimeId, new(prepared.Key, state.VectorStyleAssets!, batches,
            prepared.ExtrusionEvaluationFailures));
        if (!_extrusions.Frames.Values.Any(frame => frame.Batches.Any(batch => batch.Buffer.Count != 0)))
        {
            _extrusions.Resources?.Dispose();
            _extrusions.Resources = null;
        }
        if (prepared.ExtrusionEvaluationFailures != 0)
            MapControlEventSource.Log.VectorExtrusionRenderBatch(0, 0, prepared.ExtrusionEvaluationFailures, 0, 0);
        prepared.ExtrusionBatches = null;
    }

    private bool NeedsExtrusionFrame(LayerRenderSnapshot layer, RasterLayerState state) =>
        _extrusions is null || !_extrusions.Frames.TryGetValue(layer.RuntimeId, out var frame) ||
        frame.Key.VectorTileVersion != _vectorTileVersion || frame.Key.SceneVersion != state.SceneVersion ||
        frame.Key.Generation != state.Generation ||
        !state.VectorStyleAssets!.CanReuseExtrusions(frame.Key.Zoom, _displayZoom);

    private bool DrawVectorExtrudedLayer(IntPtr context, LayerRenderSnapshot layer, RasterLayerState state)
    {
        var assets = state.VectorStyleAssets!;
        double zoom = assets.GetStyleZoom(_displayZoom);
        bool eligible = false;
        int layerFailures = 0;
        foreach (var style in assets.ExtrusionLayers)
        {
            var visibility = style.EvaluateVisibility(zoom);
            if (visibility == VectorStyleVisibilityResult.EvaluationFailure)
                layerFailures++;
            else if (visibility == VectorStyleVisibilityResult.Visible)
            {
                if (!style.TryEvaluateLayer(zoom, out var paint))
                    layerFailures++;
                else
                    eligible |= paint.Opacity > 0;
            }
        }
        if (layerFailures != 0)
            MapControlEventSource.Log.VectorExtrusionRenderBatch(0, 0, layerFailures, 0, 0);
        ApplyCompletedVectorGeometryPreparation(layer, state);
        ExtrusionFrame? frame = null;
        _extrusions?.Frames.TryGetValue(layer.RuntimeId, out frame);
        if (eligible && NeedsExtrusionFrame(layer, state))
            DeferVectorGeometryRebuild(layer, state, GetFallbackZoomMask(state));
        if (!eligible || frame is null || frame.Batches.Length == 0 ||
            frame.Key.Generation != state.Generation || !assets.CanReuseExtrusionMembership(frame.Key.Zoom, _displayZoom))
            return DrawVectorPolygonLayer(context, layer) | DrawVectorLineLayer(context, layer);

        bool fading = false;
        _drawingExtrusions = true;
        try
        {
            foreach (var style in assets.ExtrusionLayers)
            {
                if (style.EvaluateVisibility(zoom) != VectorStyleVisibilityResult.Visible ||
                    !style.TryEvaluateLayer(zoom, out var paint) || paint.Opacity == 0)
                    continue;
                _vectorOrderMaximum = style.Order;
                fading |= DrawVectorPolygonLayer(context, layer);
                fading |= DrawVectorLineLayer(context, layer);
                DrawExtrusionStyle(context, layer, frame, style, paint);
                _vectorOrderMinimum = style.Order + 1;
            }
            _vectorOrderMaximum = int.MaxValue;
            fading |= DrawVectorPolygonLayer(context, layer);
            fading |= DrawVectorLineLayer(context, layer);
        }
        finally
        {
            _drawingExtrusions = false;
            _vectorOrderMinimum = int.MinValue;
            _vectorOrderMaximum = int.MaxValue;
        }
        return fading;
    }

    private unsafe void DrawExtrusionStyle(IntPtr context, LayerRenderSnapshot layer,
        ExtrusionFrame frame, VectorExtrusionStyleLayer style, VectorExtrusionLayerPaint paint)
    {
        if (!frame.Batches.Any(batch => batch.Order == style.Order && batch.Buffer.Count != 0 &&
            (batch.TextureId == 0 || _iconTextures.ContainsKey(batch.TextureId))))
            return;
        if (frame.Assets.ExtrusionLight is not { } light ||
            !light.TryEvaluate(frame.Assets.GetStyleZoom(_displayZoom), _displayHeading, out var direction, out var tint))
            throw new InvalidDataException("Extrusion lighting could not be evaluated.");
        var resources = _extrusions!.Resources ??= new(DevicePointer);
        resources.EnsureSurface(DevicePointer, SurfaceSize.PixelWidth, SurfaceSize.PixelHeight, RenderSampleCount);
        double worldSize = MapCamera.TileSize * Math.Pow(2, _displayZoom);
        double centerX = MapCamera.LongitudeToWorldX(_displayLongitude) * worldSize;
        double centerY = MapCamera.LatitudeToWorldY(_displayLatitude) * worldSize;
        double heading = _displayHeading * Math.PI / 180, pitch = _displayPitch * Math.PI / 180;
        double tx = paint.TranslateX, ty = paint.TranslateY;
        int triangles = 0, draws = 0;
        if (paint.ViewportTranslation)
        {
            tx = paint.TranslateX * Math.Cos(heading) - paint.TranslateY * Math.Sin(heading);
            ty = paint.TranslateX * Math.Sin(heading) + paint.TranslateY * Math.Cos(heading);
        }
        try
        {
            SetPixelShader(context, resources.PixelShader, IntPtr.Zero, _patternSamplerPointer, resources.Constants);
            SetDepthRenderTarget(context, resources.Target, resources.DepthView);
            Clear(context, resources.Target, TransparentLineCompositeColor);
            ClearDepth(context, resources.DepthView);
            SetDepthState(context, resources.DepthState);
            SetBlendState(context, resources.BlendState);
            SetInputLayout(context, resources.Layout);
            SetVertexShader(context, resources.VertexShader, resources.Constants);
            foreach (var batch in frame.Batches)
            {
                if (batch.Order != style.Order || batch.Buffer.Count == 0)
                    continue;
                IntPtr view = IntPtr.Zero;
                if (batch.TextureId != 0)
                {
                    if (!_iconTextures.TryGetValue(batch.TextureId, out var texture))
                        continue;
                    texture.MarkUsed();
                    view = texture.ViewPointer;
                }
                double size = worldSize / Math.Pow(2, batch.Tile.Id.Zoom);
                ExtrusionConstants constants = new(
                    new((float)(batch.Tile.WorldX * size - centerX + tx),
                        (float)(batch.Tile.Id.Y * size - centerY + ty), (float)size, 0),
                    new((float)(2 / _viewportWidth), (float)(-2 / _viewportHeight),
                        (float)MapCamera.GetPerspectiveDistance(_viewportHeight), 0),
                    new((float)Math.Cos(heading), (float)Math.Sin(heading), (float)Math.Cos(pitch), (float)Math.Sin(pitch)),
                    direction, tint, new(batch.TextureId != 0 ? 1 : 0, paint.VerticalGradient ? 1 : 0, 0, 0),
                    new((float)batch.Clip.Left, (float)batch.Clip.Top, (float)batch.Clip.Right, (float)batch.Clip.Bottom),
                    new((float)(size / layer.TileSize),
                        batch.PatternWidth > 0 ? (float)(batch.Tile.WorldX * size % batch.PatternWidth / batch.PatternWidth) : 0,
                        batch.PatternHeight > 0 ? (float)(batch.Tile.Id.Y * size % batch.PatternHeight / batch.PatternHeight) : 0, 0));
                UpdateSubresource(context, resources.Constants, &constants);
                SetVertexBuffer(context, batch.Buffer.Pointer, (uint)sizeof(VectorExtrusionVertex));
                SetPixelShader(context, resources.PixelShader, view, _patternSamplerPointer, resources.Constants);
                DrawVertices(context, (uint)batch.Buffer.Count);
                triangles += batch.Buffer.Count / 3;
                draws++;
            }
        }
        finally
        {
            SetDepthState(context, IntPtr.Zero);
            SetRenderTarget(context, RenderTargetPointer);
            SetPixelShader(context, _iconPixelShaderPointer, IntPtr.Zero, _samplerPointer, _constantBufferPointer);
        }
        if (resources.MultisampleColor != IntPtr.Zero)
            ResolveColorTarget(context, resources.Color, resources.MultisampleColor);
        TileConstants composite = CreateQuadConstants(0, 0, _viewportWidth, _viewportHeight,
            (float)(paint.Opacity * layer.Opacity)) with { TextureTransform = new(1, 1, 0, 0) };
        UpdateSubresource(context, _constantBufferPointer, &composite);
        SetBlendState(context, _premultipliedBlendStatePointer);
        SetInputLayout(context, _inputLayoutPointer);
        SetVertexBuffer(context, _vertexBufferPointer, (uint)Marshal.SizeOf<TileVertex>());
        SetIndexBuffer(context, _indexBufferPointer);
        SetVertexShader(context, _vertexShaderPointer, _constantBufferPointer);
        SetPixelShader(context, _iconPixelShaderPointer, resources.View, _samplerPointer, _constantBufferPointer);
        DrawIndexed(context);
        SetPixelShader(context, _iconPixelShaderPointer, IntPtr.Zero, _samplerPointer, _constantBufferPointer);
        if (MapControlEventSource.Log.IsEnabled(System.Diagnostics.Tracing.EventLevel.Verbose,
            MapControlEventSource.Keywords.VectorTiles | MapControlEventSource.Keywords.Device))
            MapControlEventSource.Log.VectorExtrusionRenderBatch(
                triangles, draws + 1, frame.Failures, ExtrusionMeshBytesForTest, resources.ByteSize);
    }

    private void TrimExtrusions()
    {
        if (_extrusions is null)
            return;
        foreach (var id in _extrusions.Frames.Keys.ToArray())
        {
            if (!_rasterLayers.TryGetValue(id, out var state) ||
                state.VectorStyleAssets?.HasExtrusions != true ||
                _extrusions.Frames[id].Key.Generation != state.Generation ||
                !_layerRenderPlan.Any(layer => layer.RuntimeId == id && layer.IsVisible && layer.Opacity > 0 &&
                    _displayZoom >= layer.MinZoom && _displayZoom < layer.MaxZoom) ||
                !state.VectorStyleAssets.ExtrusionLayers.Any(style =>
                    style.EvaluateVisibility(state.VectorStyleAssets.GetStyleZoom(_displayZoom)) == VectorStyleVisibilityResult.Visible &&
                    style.TryEvaluateLayer(state.VectorStyleAssets.GetStyleZoom(_displayZoom), out var paint) && paint.Opacity > 0))
            {
                _extrusions.Frames[id].Dispose();
                _extrusions.Frames.Remove(id);
            }
        }
        if (_extrusions.Frames.Count == 0)
            ReleaseExtrusions();
    }

    private void ReleaseExtrusions()
    {
        _extrusions?.Dispose();
        _extrusions = null;
    }
}
