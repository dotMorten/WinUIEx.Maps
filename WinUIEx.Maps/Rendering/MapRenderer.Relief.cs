using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Numerics;
using static WinUIEx.Maps.Rendering.DirectXInterop;

namespace WinUIEx.Maps.Rendering;

internal sealed partial class MapRenderer
{
    // Raster fallback's painter order is correct for opaque imagery, not for
    // straight-alpha terrain. Partition map-plane coverage before projection:
    // active tiles win, then the finest retained fallback fills only the holes.
    // The ordinary raster/satellite fade path is deliberately unchanged.
    private unsafe bool DrawExclusiveReliefCoverage(
        IntPtr context, LayerRenderSnapshot layer, RasterLayerState state)
    {
        int activeZoom = state.Scene!.TileZoom;
        List<CachedRasterTileDraw> draws = [];
        foreach (var (key, texture) in _rasterTiles)
        {
            if (key.SourceId != layer.RuntimeId ||
                key.Id.Zoom >= state.VectorStyleAssets!.Relief!.MaxZoom ||
                (key.Id.Zoom != activeZoom && !state.FallbackTileZooms.Contains(key.Id.Zoom)))
                continue;
            foreach (VisibleTile tile in GetVisibleCachedTileInstances(key.Id,
                _displayLongitude, _displayLatitude, _displayZoom, _viewportWidth,
                _viewportHeight, _displayHeading, _displayPitch))
                draws.Add(new(tile, texture));
        }
        draws.Sort((a, b) =>
        {
            int active = (b.Tile.Id.Zoom == activeZoom).CompareTo(a.Tile.Id.Zoom == activeZoom);
            return active != 0 ? active : b.Tile.Id.Zoom.CompareTo(a.Tile.Id.Zoom);
        });
        List<VisibleTile> covered = [];
        List<ReliefRectangle> remaining = [], next = [];
        bool activeFade = false;
        foreach (var draw in draws)
        {
            var tile = draw.Tile;
            ReliefRectangle bounds = new(tile.Left, tile.Top, tile.Left + tile.Size, tile.Top + tile.Size);
            remaining.Clear();
            remaining.Add(bounds);
            foreach (var previous in covered)
            {
                if (!TilesOverlap(tile.Id with { X = tile.WorldX },
                    previous.Id with { X = previous.WorldX }))
                    continue;
                next.Clear();
                foreach (var region in remaining)
                    region.Subtract(new(previous.Left, previous.Top,
                        previous.Left + previous.Size, previous.Top + previous.Size), next);
                (remaining, next) = (next, remaining);
                if (remaining.Count == 0) break;
            }
            if (remaining.Count == 0) continue;
            double opacity = ComputeLayerTileOpacity(
                Stopwatch.GetElapsedTime(draw.Texture.ReadyTimestamp), layer.FadeDuration, layer.Opacity);
            activeFade |= opacity < layer.Opacity;
            draw.Texture.MarkUsed();
            foreach (var region in remaining)
            {
                // Keep the original texture's sampling lattice when clipping a parent.
                var constants = CreateTileConstants(tile with
                { Left = region.Left, Top = region.Top, Size = region.Right - region.Left }, (float)opacity);
                var quad = CreateQuadConstants(region.Left, region.Top,
                    region.Right - region.Left, region.Bottom - region.Top, (float)opacity);
                constants = constants with
                {
                    Transform = quad.Transform,
                    TextureTransform = new Vector4(
                        (float)((region.Right - region.Left) / tile.Size),
                        (float)((region.Bottom - region.Top) / tile.Size),
                        (float)((region.Left - tile.Left) / tile.Size),
                        (float)((region.Top - tile.Top) / tile.Size)),
                };
                Vector4 uv = constants.TextureTransform;
                Vector4 crop = draw.Texture.TextureTransform;
                constants = constants with { TextureTransform = new(
                    uv.X * crop.X, uv.Y * crop.Y,
                    uv.Z * crop.X + crop.Z, uv.W * crop.Y + crop.W) };
                UpdateSubresource(context, _constantBufferPointer, &constants);
                SetPixelShader(context, _pixelShaderPointer, draw.Texture.ViewPointer,
                    _samplerPointer, _constantBufferPointer);
                DrawIndexed(context);
            }
            covered.Add(tile);
        }
        if (CanEnumerateRasterScene(_displayZoom, activeZoom) &&
            EvaluateAndReportCoverage(layer.RuntimeId, state,
                CreateCurrentRasterScene(activeZoom), layer.FadeDuration) && !activeFade)
            state.FallbackTileZooms.Clear();
        return activeFade;
    }

    private readonly record struct ReliefRectangle(double Left, double Top, double Right, double Bottom)
    {
        internal void Subtract(ReliefRectangle other, List<ReliefRectangle> output)
        {
            double left = Math.Max(Left, other.Left), top = Math.Max(Top, other.Top);
            double right = Math.Min(Right, other.Right), bottom = Math.Min(Bottom, other.Bottom);
            if (left >= right || top >= bottom) { output.Add(this); return; }
            if (Top < top) output.Add(new(Left, Top, Right, top));
            if (bottom < Bottom) output.Add(new(Left, bottom, Right, Bottom));
            if (Left < left) output.Add(new(Left, top, left, bottom));
            if (right < Right) output.Add(new(right, top, Right, bottom));
        }
    }

    // Called at the style-order boundary in every polygon path (fresh, prepared,
    // cached, and deferred). Terrain alpha blends over the canvas/land fills and
    // remains below later polygon details, roads, and labels.
    private bool DrawReliefBefore(
        IntPtr context, LayerRenderSnapshot layer, int nextOrder, ref bool hasDrawn)
    {
        if (hasDrawn || layer.Kind != LayerRenderKind.HybridTiles ||
            layer.Style != (int)MapStyle.RoadShadedRelief ||
            !_rasterLayers.TryGetValue(layer.RuntimeId, out var state) ||
            state.VectorStyleAssets?.Relief is not { } relief ||
            relief.Order >= nextOrder)
            return false;
        hasDrawn = true;
        double opacity = relief.GetOpacity(_displayZoom);
        if (opacity <= 0)
            return false;
        SetBlendState(context, _blendStatePointer);
        SetInputLayout(context, _inputLayoutPointer);
        SetVertexBuffer(context, _vertexBufferPointer, (uint)Marshal.SizeOf<TileVertex>());
        SetIndexBuffer(context, _indexBufferPointer);
        SetVertexShader(context, _vertexShaderPointer, _constantBufferPointer);
        return DrawRasterTileLayer(context, layer with
        {
            Opacity = layer.Opacity * opacity,
            FadeDuration = relief.FadeDuration,
        });
    }
}
