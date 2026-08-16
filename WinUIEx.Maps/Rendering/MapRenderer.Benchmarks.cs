using System.Runtime.InteropServices;

namespace WinUIEx.Maps.Rendering;

internal sealed partial class MapRenderer
{
    internal ulong UploadTextureAndWaitForGpuForBenchmark(
        byte[] pixels,
        uint width,
        uint height)
    {
        if (!IsValidPixelBuffer(pixels, width, height))
        {
            throw new ArgumentException(
                "The benchmark texture must contain tightly packed BGRA8 pixels.",
                nameof(pixels));
        }

        lock (RenderLock)
        {
            using TileTexture texture = CreateTileTexture(
                DevicePointer,
                pixels,
                width,
                height,
                "Failed to create the benchmark texture view.");
            WaitForGpuCompletion();
            return texture.ByteSize;
        }
    }

    internal ulong UploadVectorTexturesAndWaitForGpuForBenchmark(
        IReadOnlyList<VectorSpriteTextureData> textures)
    {
        lock (RenderLock)
        {
            List<TileTexture> uploaded = new(textures.Count);
            ulong byteSize = 0;
            try
            {
                foreach (VectorSpriteTextureData texture in textures)
                {
                    TileTexture uploadedTexture = CreateTileTexture(
                        DevicePointer,
                        texture.Pixels,
                        texture.Width,
                        texture.Height,
                        "Failed to create a benchmark vector-symbol texture.");
                    uploaded.Add(uploadedTexture);
                    byteSize += uploadedTexture.ByteSize;
                }
                WaitForGpuCompletion();
                return byteSize;
            }
            finally
            {
                foreach (TileTexture texture in uploaded)
                {
                    texture.Dispose();
                }
            }
        }
    }

    internal void AddVectorTexturesForBenchmark(
        IReadOnlyList<VectorSpriteTextureData> textures)
    {
        lock (RenderLock)
        {
            foreach (VectorSpriteTextureData texture in textures)
            {
                if (_iconTextures.ContainsKey(texture.TextureId))
                {
                    throw new InvalidOperationException(
                        "The benchmark vector-symbol texture has already been added.");
                }
                _iconTextures.Add(
                    texture.TextureId,
                    CreateTileTexture(
                        DevicePointer,
                        texture.Pixels,
                        texture.Width,
                        texture.Height,
                        "Failed to create a benchmark vector-symbol texture."));
            }
            _iconTextureVersion++;
            ClearVectorSymbolFrameCaches();
            WaitForGpuCompletion();
        }
    }

    internal void AddRasterTileForBenchmark(
        RasterTileKey key,
        byte[] pixels,
        uint width,
        uint height)
    {
        if (!IsValidPixelBuffer(pixels, width, height))
        {
            throw new ArgumentException(
                "The benchmark tile must contain tightly packed BGRA8 pixels.",
                nameof(pixels));
        }

        lock (RenderLock)
        {
            if (!_rasterLayers.ContainsKey(key.SourceId))
            {
                throw new InvalidOperationException(
                    "The benchmark raster source must be activated before adding tiles.");
            }
            if (_rasterTiles.ContainsKey(key))
            {
                throw new InvalidOperationException(
                    "The benchmark raster tile has already been added.");
            }

            _rasterTiles.Add(
                key,
                CreateTileTexture(
                    DevicePointer,
                    pixels,
                    width,
                    height,
                    "Failed to create the benchmark raster tile view."));
            WaitForGpuCompletion();
        }
    }

    internal long PrepareAndUploadVectorTileForBenchmark(
        VectorTileFeatureCollection features,
        VectorStyleAssets styleAssets,
        TileId id,
        double zoom,
        int viewportWidth,
        int viewportHeight)
    {
        double scale = Math.Pow(2, id.Zoom);
        double longitude = MapCamera.WorldXToLongitude((id.X + 0.5) / scale);
        double latitude = MapCamera.WorldYToLatitude((id.Y + 0.5) / scale);
        MapScene scene = MapCamera.CreateScene(
            longitude,
            latitude,
            zoom,
            id.Zoom,
            viewportWidth,
            viewportHeight,
            0,
            0);
        VisibleTile visibleTile = scene.VisibleTiles.First(tile =>
            tile.Id == id && tile.WorldX == id.X);
        LayerRenderSnapshot layer = new(
            LayerRenderKind.VectorPoints,
            LayerIndex: 0,
            RuntimeId: 1,
            IsVisible: true,
            Opacity: 1,
            FadeDuration: TimeSpan.Zero,
            MinZoom: 0,
            MaxZoom: 24,
            MinSourceZoom: 0,
            TileSize: 256,
            Style: (int)MapStyle.Road);
        VectorGeometryPreparationKey key = new(
            layer.RuntimeId,
            layer.Style,
            layer.Opacity,
            Generation: 1,
            SceneVersion: 1,
            VectorTileVersion: 1,
            _deviceEpoch,
            zoom,
            Heading: 0,
            Pitch: 0,
            viewportWidth,
            viewportHeight);

        lock (RenderLock)
        {
            IntPtr devicePointer = DevicePointer;
            Marshal.AddRef(devicePointer);
            VectorGeometryPreparationInput input = new(
                key,
                layer,
                devicePointer,
                longitude,
                latitude,
                zoom,
                Heading: 0,
                Pitch: 0,
                viewportWidth,
                viewportHeight,
                styleAssets.ResolveBackgrounds(zoom),
                [new VectorLinePreparationTile(
                    visibleTile,
                    styleAssets.ResolveLines(features, zoom))],
                [new VectorPolygonPreparationTile(
                    visibleTile,
                    styleAssets.ResolvePolygons(features, zoom))],
                [new VectorTileInstanceKey(
                    new RasterTileKey(layer.RuntimeId, id),
                    visibleTile.WorldX)]);
            using PreparedVectorGeometryFrame prepared =
                BuildVectorGeometryFrame(input, CancellationToken.None);
            WaitForGpuCompletion();
            return ((long)prepared.LineVertexCount << 32) |
                (uint)prepared.PolygonVertexCount;
        }
    }

    internal void AddVectorTileForBenchmark(
        RasterTileKey key,
        VectorTileFeatureCollection features,
        VectorStyleAssets styleAssets)
    {
        lock (RenderLock)
        {
            if (!_rasterLayers.TryGetValue(
                    key.SourceId,
                    out RasterLayerState? state) ||
                state.RenderKind != LayerRenderKind.VectorPoints)
            {
                throw new InvalidOperationException(
                    "The benchmark vector source must be activated before adding tiles.");
            }
            if (_vectorTiles.ContainsKey(key))
            {
                throw new InvalidOperationException(
                    "The benchmark vector tile has already been added.");
            }

            _vectorTiles.Add(
                key,
                new VectorTileCacheEntry(
                    features,
                    styleAssets,
                    (int)MapStyle.Road));
            state.VectorStyleAssets = styleAssets;
            OnVectorTilesChanged();
        }
    }
}
