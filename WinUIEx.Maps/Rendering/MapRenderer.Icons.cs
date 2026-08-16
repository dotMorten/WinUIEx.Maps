using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using WinUIEx.Maps.Rendering.Diagnostics;
using static WinUIEx.Maps.Rendering.DirectXInterop;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Implements the icon snapshot, texture-upload, culling, batching, and instanced-rendering
/// portion of <see cref="MapRenderer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Icon visuals are tracked, measured, and rasterized by the control-owned
/// <see cref="MapIconService"/> on the UI thread because they are XAML objects. The
/// resulting versioned BGRA buffers enter this partial definition through
/// <c>QueueMapIconTexture</c>; no XAML object crosses into renderer workers.
/// </para>
/// <para>
/// The dedicated upload thread discards superseded versions, retains the current device
/// pointer for each texture creation, and publishes a completion tagged with the current
/// device epoch. The render thread accepts only matching versions and epochs, replaces cache
/// entries, and transfers stale or removed textures to deferred upload-thread disposal.
/// </para>
/// <para>
/// Element snapshots are protected by <c>_mapElementsSync</c>. During a frame, the spatial
/// index supplies candidates in layer and element order; the render thread projects and
/// culls them, batching only consecutive same-texture icons so vector elements retain their
/// exact collection positions. ETW contains only identifiers, dimensions, sanitized failure
/// metadata, and aggregate counts, never icon pixels or XAML content.
/// </para>
/// </remarks>
internal sealed partial class MapRenderer : DirectXRenderer
{
    private const int IconInstanceCapacity = 16384;
    private readonly object _iconSync = new();
    private readonly ConcurrentQueue<MapIconPixelData> _iconPixelUploads = new();
    private readonly ConcurrentQueue<CompletedIconUpload> _completedIconUploads = new();
    private readonly ConcurrentQueue<long> _removedIconTextureIds = new();
    private readonly Dictionary<long, MapIconPixelData> _iconPixels = [];
    private readonly Dictionary<long, TileTexture> _iconTextures = [];
    private readonly VectorSpriteOwnershipTracker _vectorSpriteOwnership = new();
    private readonly object _mapElementsSync = new();
    private readonly MapIconSpatialIndex _mapIcons = new();
    private MapGeometrySnapshot[] _mapGeometries = [];

    /// <summary>
    /// Replaces the render-thread icon snapshot through the synchronized spatial index.
    /// </summary>
    public void SetMapIcons(MapIconSnapshot[] icons)
    {
        lock (_mapElementsSync)
        {
            _mapIcons.Rebuild(icons);
        }
        RequestRender();
    }

    /// <summary>
    /// Atomically replaces immutable icon and vector snapshots from one UI publication.
    /// </summary>
    public void SetMapElements(
        MapIconSnapshot[] icons,
        MapGeometrySnapshot[] geometries)
    {
        lock (_mapElementsSync)
        {
            _mapIcons.Rebuild(icons);
            _mapGeometries = geometries;
        }
        RequestRender();
    }

    /// <summary>
    /// Applies incremental icon snapshot changes and requests a frame with the updated
    /// positions.
    /// </summary>
    public void UpdateMapIcons(IReadOnlyList<MapIconSnapshotUpdate> updates)
    {
        lock (_mapElementsSync)
        {
            _mapIcons.Update(updates);
        }
        RequestRender();
    }

    /// <summary>
    /// Hit-tests the topmost visible icon at a viewport point against the displayed camera.
    /// </summary>
    public bool TryHitTestMapIcon(double viewportX, double viewportY, out int iconIndex)
    {
        return TryHitTestMapElement(viewportX, viewportY, out iconIndex);
    }

    /// <summary>
    /// Hit-tests icons and vector geometry in reverse visual order against the displayed
    /// camera.
    /// </summary>
    public bool TryHitTestMapElement(
        double viewportX,
        double viewportY,
        out int elementIndex)
    {
        double longitude;
        double latitude;
        double zoom;
        double heading;
        double pitch;
        double viewportWidth;
        double viewportHeight;
        lock (_cameraSync)
        {
            if (!_hasPublishedCamera)
            {
                elementIndex = -1;
                return false;
            }

            longitude = _publishedLongitude;
            latitude = _publishedLatitude;
            zoom = _publishedZoom;
            heading = _publishedHeading;
            pitch = _publishedPitch;
            viewportWidth = _publishedViewportWidth;
            viewportHeight = _publishedViewportHeight;
        }

        bool[] visibleLayers = Volatile.Read(ref _visibleMapElementLayers);

        MapGeometryCamera camera = new(
            longitude,
            latitude,
            zoom,
            heading,
            viewportWidth,
            viewportHeight,
            pitch);
        lock (_mapElementsSync)
        {
            bool hasIcon = _mapIcons.TryHitTest(
                longitude,
                latitude,
                zoom,
                viewportWidth,
                viewportHeight,
                viewportX,
                viewportY,
                visibleLayers,
                heading,
                pitch,
                out int iconElementIndex,
                out int iconOrderIndex);
            if (MapGeometryOperations.TryHitTestAbove(
                _mapGeometries,
                visibleLayers,
                camera,
                viewportX,
                viewportY,
                hasIcon ? iconOrderIndex : -1,
                out int geometryElementIndex,
                out _))
            {
                elementIndex = geometryElementIndex;
                return true;
            }

            elementIndex = hasIcon ? iconElementIndex : -1;
            return hasIcon;
        }
    }

    /// <summary>
    /// Validates and versions icon pixels, then queues them for GPU creation on the upload
    /// worker.
    /// </summary>
    public void QueueMapIconTexture(MapIconPixelData data)
    {
        if (!IsValidPixelBuffer(data.Pixels, data.Width, data.Height))
        {
            MapControlEventSource.Log.IconTextureUploadFailed(
                data.TextureId,
                checked((int)data.Width),
                checked((int)data.Height),
                nameof(InvalidDataException),
                0);
            return;
        }

        lock (_iconSync)
        {
            _iconPixels[data.TextureId] = data;
        }
        _iconPixelUploads.Enqueue(data);
        _uploadRequested.Set();
    }

    /// <summary>
    /// Invalidates retained pixels and queues the corresponding GPU texture for render-thread
    /// removal.
    /// </summary>
    public void RemoveMapIconTexture(long textureId)
    {
        lock (_iconSync)
        {
            _iconPixels.Remove(textureId);
        }
        _removedIconTextureIds.Enqueue(textureId);
        RequestRender();
    }

    /// <summary>
    /// Registers source-owned sprite crops with the existing icon upload and device-epoch
    /// pipeline, deduplicating stable texture identities across tiles and sources.
    /// </summary>
    private void QueueVectorSpriteTextures(
        long sourceId,
        IReadOnlyList<VectorSpriteTextureData> textures)
    {
        bool queued = false;
        lock (_iconSync)
        {
            foreach (VectorSpriteTextureData texture in textures)
            {
                if (!_vectorSpriteOwnership.Add(sourceId, texture.TextureId))
                {
                    continue;
                }
                MapIconPixelData data = new(
                    texture.TextureId,
                    1,
                    texture.Pixels,
                    texture.Width,
                    texture.Height);
                _iconPixels[texture.TextureId] = data;
                _iconPixelUploads.Enqueue(data);
                queued = true;
            }
        }
        if (queued)
        {
            _uploadRequested.Set();
        }
    }

    /// <summary>
    /// Releases all sprite identities owned only by a removed vector source.
    /// </summary>
    private void RemoveVectorSpriteTextures(long sourceId)
    {
        bool removed = false;
        lock (_iconSync)
        {
            foreach (long textureId in _vectorSpriteOwnership.RemoveSource(sourceId))
            {
                _iconPixels.Remove(textureId);
                _removedIconTextureIds.Enqueue(textureId);
                removed = true;
            }
        }
        if (removed)
        {
            RequestRender();
        }
    }

    /// <summary>
    /// Creates a bounded pass of icon textures on the upload worker and publishes only
    /// versions valid for the current device epoch.
    /// </summary>
    /// <remarks>
    /// Temporary device references and uncommitted textures are always released on this
    /// worker; accepted completions transfer texture ownership to the render thread.
    /// </remarks>
    private unsafe void ProcessIconPixelUploads()
    {
        int processedCount = 0;
        DrainTextureDisposals();
        while (processedCount < MaximumUploadsPerPass &&
            _iconPixelUploads.TryDequeue(out MapIconPixelData? data))
        {
            if (++processedCount % 8 == 0)
            {
                DrainTextureDisposals();
            }
            if (data is null)
            {
                continue;
            }
            lock (_iconSync)
            {
                if (!_iconPixels.TryGetValue(data.TextureId, out MapIconPixelData? current) ||
                    current.Version != data.Version)
                {
                    continue;
                }
            }

            IntPtr devicePointer;
            int deviceEpoch;
            lock (RenderLock)
            {
                devicePointer = DevicePointer;
                deviceEpoch = _deviceEpoch;
                if (devicePointer == IntPtr.Zero)
                {
                    _iconPixelUploads.Enqueue(data);
                    return;
                }
                Marshal.AddRef(devicePointer);
            }

            TileTexture? completedTexture = null;
            try
            {
                completedTexture = CreateTileTexture(
                    devicePointer,
                    data.Pixels,
                    data.Width,
                    data.Height,
                    "Failed to create a MapIcon shader resource.");

                bool isCurrent;
                lock (_iconSync)
                {
                    isCurrent =
                        _iconPixels.TryGetValue(data.TextureId, out MapIconPixelData? current) &&
                        current.Version == data.Version;
                }
                if (isCurrent &&
                    deviceEpoch == Interlocked.CompareExchange(ref _deviceEpoch, 0, 0))
                {
                    _completedIconUploads.Enqueue(new CompletedIconUpload(
                        data.TextureId,
                        data.Version,
                        deviceEpoch,
                        completedTexture!));
                    completedTexture = null;
                    RequestRender();
                }
            }
            catch (Exception exception)
            {
                MapControlEventSource.Log.IconTextureUploadFailed(
                    data.TextureId,
                    checked((int)data.Width),
                    checked((int)data.Height),
                    exception.GetType().FullName ?? exception.GetType().Name,
                    exception.HResult);
            }
            finally
            {
                completedTexture?.Dispose();
                Marshal.Release(devicePointer);
            }
        }
        DrainTextureDisposals();
        if (!_iconPixelUploads.IsEmpty)
        {
            _uploadRequested.Set();
        }
    }

    /// <summary>
    /// Commits current icon upload completions and removals on the render thread, deferring
    /// replaced or stale texture disposal to the upload worker.
    /// </summary>
    private void ProcessCompletedIconUploads()
    {
        int removedCount = 0;
        int uploadedCount = 0;
        int replacedCount = 0;
        while (_removedIconTextureIds.TryDequeue(out long textureId))
        {
            if (_iconTextures.Remove(textureId, out TileTexture? removed))
            {
                removedCount++;
                QueueTextureDisposal(removed);
            }
        }

        while (_completedIconUploads.TryDequeue(out CompletedIconUpload completed))
        {
            bool isCurrent;
            lock (_iconSync)
            {
                isCurrent =
                    _iconPixels.TryGetValue(completed.TextureId, out MapIconPixelData? current) &&
                    current.Version == completed.Version;
            }
            if (completed.DeviceEpoch != _deviceEpoch || !isCurrent)
            {
                QueueTextureDisposal(completed.Texture);
                continue;
            }

            if (_iconTextures.Remove(completed.TextureId, out TileTexture? previous))
            {
                replacedCount++;
                QueueTextureDisposal(previous);
            }
            _iconTextures.Add(completed.TextureId, completed.Texture);
            uploadedCount++;
        }
        if (removedCount != 0 || uploadedCount != 0 || replacedCount != 0)
        {
            _iconTextureVersion++;
            ClearVectorSymbolFrameCaches();
            MapControlEventSource.Log.IconTextureUploadSummary(
                uploadedCount,
                replacedCount,
                removedCount);
        }
    }
    /// <summary>
    /// Returns spatially narrowed icon candidates in published visual order.
    /// </summary>
    private void GetVisibleMapElements(
        out MapIconSnapshot[] icons,
        out MapGeometrySnapshot[] geometries)
    {
        lock (_mapElementsSync)
        {
            icons = _mapIcons.GetVisible(
                _displayLongitude,
                _displayLatitude,
                _displayZoom,
                _viewportWidth,
                _viewportHeight,
                _displayHeading,
                _displayPitch);
            geometries = _mapGeometries;
        }
    }

    /// <summary>
    /// Draws a consecutive same-texture icon run without crossing another element's visual
    /// order position.
    /// </summary>
    private unsafe IconDrawResult DrawMapIconRun(
        IntPtr context,
        MapIconSnapshot[] icons,
        int startIndex,
        int count,
        double layerOpacity)
    {
        if (count <= 0 ||
            (uint)startIndex >= (uint)icons.Length ||
            !_iconTextures.TryGetValue(
                icons[startIndex].TextureId,
                out TileTexture? texture))
        {
            return new IconDrawResult(Math.Max(0, count), 0, 0, 0);
        }

        List<IconInstance> instances = new(count);
        int endIndex = Math.Min(icons.Length, startIndex + count);
        for (int index = startIndex; index < endIndex; index++)
        {
            MapIconSnapshot icon = icons[index];
            if (icon.TextureId != icons[startIndex].TextureId ||
                !MapCamera.TryProjectLocation(
                    icon.Longitude,
                    icon.Latitude,
                    _displayLongitude,
                    _displayLatitude,
                    _displayZoom,
                    _viewportWidth,
                    _viewportHeight,
                    _displayHeading,
                    _displayPitch,
                    out MapViewportPoint point))
            {
                continue;
            }

            MapViewportPoint topLeft = GetMapIconTopLeft(point, icon);
            double left = topLeft.X;
            double top = topLeft.Y;
            if (!MapCamera.IsRectangleVisible(
                left,
                top,
                icon.Width,
                icon.Height,
                _viewportWidth,
                _viewportHeight))
            {
                continue;
            }
            instances.Add(CreateIconInstance(
                left,
                top,
                icon.Width,
                icon.Height,
                rotation: 0));
        }
        if (instances.Count == 0)
        {
            return new IconDrawResult(count, 0, 0, 0);
        }

        int drawCallCount = 0;
        SetBlendState(context, _premultipliedBlendStatePointer);
        TileConstants layerConstants = new(
            new Vector4(1, 1, 0, 0),
            new Vector4(1, 0, 0, 1),
            new Vector4(1, 0, 1, 0),
            new Vector4((float)layerOpacity, 0, 0, 0));
        UpdateSubresource(context, _constantBufferPointer, &layerConstants);
        SetInputLayout(context, _iconInputLayoutPointer);
        SetVertexBuffers(
            context,
            _vertexBufferPointer,
            (uint)Marshal.SizeOf<TileVertex>(),
            _iconInstanceBufferPointer,
            (uint)Marshal.SizeOf<IconInstance>());
        SetVertexShader(context, _iconVertexShaderPointer);
        SetPixelShader(
            context,
            _iconPixelShaderPointer,
            texture.ViewPointer,
            _samplerPointer,
            _constantBufferPointer);
        Span<IconInstance> remaining = CollectionsMarshal.AsSpan(instances);
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
            drawCallCount++;
            remaining = remaining[chunk.Length..];
        }
        return new IconDrawResult(count, instances.Count, 1, drawCallCount);
    }

    internal static MapViewportPoint GetMapIconTopLeft(
        MapViewportPoint location,
        MapIconSnapshot icon) =>
        new(
            location.X - (icon.Width * icon.NormalizedAnchorX),
            location.Y - (icon.Height * icon.NormalizedAnchorY));

    /// <summary>
    /// Converts a pixel-space rectangle and opacity into normalized-device-coordinate shader
    /// constants.
    /// </summary>
    private TileConstants CreateQuadConstants(
        double left,
        double top,
        double width,
        double height,
        float opacity)
    {
        float scaleX = (float)(2 * width / Viewport.Width);
        float scaleY = (float)(-2 * height / Viewport.Height);
        float offsetX = (float)((2 * left / Viewport.Width) - 1);
        float offsetY = (float)(1 - (2 * top / Viewport.Height));
        return new(
            new(scaleX, scaleY, offsetX, offsetY),
            new(1, 0, 0, 1),
            new(1, 0, 1, 0),
            new(opacity, 0, 0, 0));
    }

    private IconInstance CreateIconInstance(
        double left,
        double top,
        double width,
        double height,
        double rotation)
    {
        double cosine = Math.Cos(rotation);
        double sine = Math.Sin(rotation);
        float horizontalX = (float)(2 * width * cosine / Viewport.Width);
        float horizontalY = (float)(-2 * width * sine / Viewport.Height);
        float verticalX = (float)(-2 * height * sine / Viewport.Width);
        float verticalY = (float)(-2 * height * cosine / Viewport.Height);
        float centerX = (float)(
            (2 * (left + (width / 2)) / Viewport.Width) - 1);
        float centerY = (float)(
            1 - (2 * (top + (height / 2)) / Viewport.Height));
        return new IconInstance(
            new Vector4(
                horizontalX,
                horizontalY,
                verticalX,
                verticalY),
            new Vector4(centerX, centerY, 0, 0));
    }

    /// <summary>
    /// Stores the normalized-device transform consumed by one instanced icon draw.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct IconInstance(
        Vector4 Transform,
        Vector4 Offset);

    private readonly record struct IconDrawResult(
        int CandidateCount,
        int DrawableCount,
        int TextureBatchCount,
        int DrawCallCount)
    {
        public static IconDrawResult operator +(IconDrawResult left, IconDrawResult right) =>
            new(
                left.CandidateCount + right.CandidateCount,
                left.DrawableCount + right.DrawableCount,
                left.TextureBatchCount + right.TextureBatchCount,
                left.DrawCallCount + right.DrawCallCount);
    }

    /// <summary>
    /// Transfers an upload-thread-created icon texture to the render thread together with
    /// the icon version and device epoch that authorize its commit.
    /// </summary>
    private readonly record struct CompletedIconUpload(
        long TextureId,
        long Version,
        int DeviceEpoch,
        TileTexture Texture);
}
