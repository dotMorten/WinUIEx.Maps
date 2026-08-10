using System.Diagnostics;
using System.Runtime.InteropServices;
using WinUIEx.Maps.Rendering.Diagnostics;
using static WinUIEx.Maps.Rendering.DirectXInterop;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Coordinates camera publication, scene generation, layer ordering, frame rendering, and
/// the renderer-wide GPU upload worker.
/// </summary>
/// <remarks>
/// <para>
/// UI-thread callers publish camera targets and immutable layer snapshots without directly
/// touching render-thread state. Camera fields are copied under <c>_cameraSync</c> and tagged
/// with a monotonically increasing version; the render thread consumes only the newest
/// version, advances pan and zoom animations, and publishes the displayed camera back for
/// hit testing and anchored zoom calculations.
/// </para>
/// <para>
/// Each rendered frame builds a <see cref="MapScene"/>, raises <see cref="SceneChanged"/> for
/// the control to forward to <see cref="RasterTileManager"/> when required tiles change,
/// commits completed raster and icon uploads, and walks the published
/// <see cref="LayerRenderSnapshot"/> plan in order. Raster layers therefore remain behind or
/// between icon layers exactly as arranged by the UI-thread publication.
/// </para>
/// <para>
/// A separate MTA upload thread services decoded raster pixels, icon pixels, and deferred
/// native texture disposal. It creates device resources using a temporarily retained device
/// pointer, then transfers successful completions to the render thread. Generation,
/// reservation, version, and device-epoch checks in the specialized partial definitions
/// prevent stale CPU work or resources from an old device from entering the active frame.
/// </para>
/// </remarks>
internal sealed partial class MapRenderer : DirectXRenderer
{
    private readonly object _cameraSync = new();
    private readonly PanAnimation _panAnimation = new();
    private readonly ZoomAnimation _zoomAnimation = new();
    private readonly HeadingAnimation _headingAnimation = new();
    private readonly PitchAnimation _pitchAnimation = new();
    private readonly AutoResetEvent _uploadRequested = new(false);
    private readonly AutoResetEvent _rasterUploadEnteredRenderLock = new(false);
    private readonly ManualResetEvent _uploadShutdown = new(false);
    private readonly ManualResetEvent _uploadThreadStopped = new(false);
    private Thread? _uploadThread;
    private MapScene? _scene;
    private double _displayLongitude;
    private double _displayLatitude;
    private double _displayZoom;
    private double _displayHeading;
    private double _displayPitch;
    private double _targetLongitude;
    private double _targetLatitude;
    private double _targetZoom;
    private double _targetHeading;
    private double _targetPitch;
    private double _targetViewportWidth;
    private double _targetViewportHeight;
    private int _targetMaximumTileZoom = MapCamera.MaximumTileZoom;
    private MapCenter _targetZoomAnchor;
    private double _targetZoomAnchorHorizontalOffset;
    private double _targetZoomAnchorVerticalOffset;
    private bool _targetHasZoomAnchor;
    private bool _targetIsImmediate;
    private KeyboardNavigationState _keyboardNavigation;
    private long _lastKeyboardNavigationTimestamp;
    private double _publishedLongitude;
    private double _publishedLatitude;
    private double _publishedZoom;
    private double _publishedHeading;
    private double _publishedPitch;
    private double _publishedViewportWidth;
    private double _publishedViewportHeight;
    private bool _hasPublishedCamera;
    private ZoomAnchor? _activeZoomAnchor;
    private double _viewportWidth;
    private double _viewportHeight;
    private int _maximumTileZoom = MapCamera.MaximumTileZoom;
    private long _cameraVersion;
    private long _consumedCameraVersion;
    private long _lastCameraTargetEventTimestamp;
    private int _deviceEpoch;
    private bool _cameraInitialized;
    private bool _uploadDisposed;
    private HashSet<TileId> _lastRequiredTiles = [];
    private LayerRenderSnapshot[] _layerRenderPlan = [];
    private bool[] _visibleMapElementLayers = [];

    public event Action<MapScene>? SceneChanged;

    /// <summary>
    /// Publishes the ordered layer-snapshot plan consumed by the render thread.
    /// </summary>
    public void SetLayerRenderPlan(LayerRenderSnapshot[] plan)
    {
        int layerCount = plan.Length == 0 ? 0 : plan.Max(layer => layer.LayerIndex) + 1;
        bool[] visibleMapElementLayers = new bool[layerCount];
        foreach (LayerRenderSnapshot layer in plan)
        {
            if (layer.Kind == LayerRenderKind.MapElements &&
                layer.IsVisible &&
                layer.Opacity > 0)
            {
                visibleMapElementLayers[layer.LayerIndex] = true;
            }
        }
        lock (RenderLock)
        {
            _layerRenderPlan = plan;
        }
        Volatile.Write(ref _visibleMapElementLayers, visibleMapElementLayers);
        RequestRender();
    }

    internal override void Resume()
    {
        StartUploadThread();
        base.Resume();
    }

    internal void ReleaseDormantResources()
    {
        SuspendBackgroundWork();
        ReleaseResources();
        lock (_iconSync)
        {
            _iconPixels.Clear();
        }
        while (_iconPixelUploads.TryDequeue(out _))
        {
        }
        lock (_mapElementsSync)
        {
            _mapIcons.Rebuild([]);
            _mapGeometries = [];
        }
    }

    internal void SuspendBackgroundWork() => StopUploadThread();

    /// <summary>
    /// Publishes a new camera and viewport target for versioned consumption by the render
    /// thread.
    /// </summary>
    public void SetCameraTarget(
        double longitude,
        double latitude,
        double targetZoom,
        double viewportWidth,
        double viewportHeight,
        double targetHeading = 0,
        double targetPitch = 0)
    {
        bool headingChanged;
        bool pitchChanged;
        double normalizedHeading = MapCamera.NormalizeHeading(targetHeading);
        double normalizedPitch = MapCamera.NormalizePitch(targetPitch);
        lock (_cameraSync)
        {
            headingChanged = _targetHeading != normalizedHeading;
            pitchChanged = _targetPitch != normalizedPitch;
            _targetLongitude = longitude;
            _targetLatitude = latitude;
            _targetZoom = targetZoom;
            _targetHeading = normalizedHeading;
            _targetPitch = normalizedPitch;
            _targetViewportWidth = viewportWidth;
            _targetViewportHeight = viewportHeight;
            _targetHasZoomAnchor = false;
            _targetIsImmediate = false;
            _cameraVersion++;
        }
        if (ShouldTraceCameraTarget())
        {
            MapControlEventSource.Log.CameraTargetChanged(
                longitude,
                latitude,
                targetZoom,
                viewportWidth,
                viewportHeight,
                false);
        }
        if (headingChanged)
        {
            MapControlEventSource.Log.CameraHeadingTargetChanged(
                normalizedHeading,
                false);
        }
        if (pitchChanged)
        {
            MapControlEventSource.Log.CameraPitchTargetChanged(
                normalizedPitch,
                false);
        }
        RequestRender();
    }

    /// <summary>
    /// Publishes a zoom target that keeps the geographic point beneath a viewport offset
    /// stationary throughout the animation.
    /// </summary>
    /// <returns>The camera center required at the target zoom.</returns>
    public MapCenter SetZoomTarget(
        double targetZoom,
        double horizontalOffset,
        double verticalOffset,
        double viewportWidth,
        double viewportHeight,
        double heading = 0,
        double pitch = 0)
    {
        MapCenter targetCenter;
        lock (_cameraSync)
        {
            double longitude = _hasPublishedCamera ? _publishedLongitude : _targetLongitude;
            double latitude = _hasPublishedCamera ? _publishedLatitude : _targetLatitude;
            double zoom = _hasPublishedCamera ? _publishedZoom : _targetZoom;
            MapCenter anchor = MapCamera.LocationAtOffset(
                longitude,
                latitude,
                zoom,
                horizontalOffset,
                verticalOffset,
                heading,
                pitch,
                viewportHeight);
            targetCenter = MapCamera.CenterForLocationAtOffset(
                anchor,
                targetZoom,
                horizontalOffset,
                verticalOffset,
                heading,
                pitch,
                viewportHeight);

            _targetLongitude = targetCenter.Longitude;
            _targetLatitude = targetCenter.Latitude;
            _targetZoom = targetZoom;
            _targetHeading = MapCamera.NormalizeHeading(heading);
            _targetPitch = MapCamera.NormalizePitch(pitch);
            _targetViewportWidth = viewportWidth;
            _targetViewportHeight = viewportHeight;
            _targetZoomAnchor = anchor;
            _targetZoomAnchorHorizontalOffset = horizontalOffset;
            _targetZoomAnchorVerticalOffset = verticalOffset;
            _targetHasZoomAnchor = true;
            _targetIsImmediate = false;
            _cameraVersion++;
        }
        MapControlEventSource.Log.CameraTargetChanged(
            targetCenter.Longitude,
            targetCenter.Latitude,
            targetZoom,
            viewportWidth,
            viewportHeight,
            true);
        RequestRender();
        return targetCenter;
    }

    /// <summary>
    /// Publishes a camera that the render thread applies on its next frame without pan or
    /// zoom interpolation.
    /// </summary>
    /// <remarks>
    /// Direct touch manipulations use this path so the rendered map remains under the
    /// fingers. Discrete input uses the animated camera-target methods instead.
    /// </remarks>
    public void SetCameraTargetImmediately(
        double longitude,
        double latitude,
        double targetZoom,
        double viewportWidth,
        double viewportHeight,
        double targetHeading = 0,
        double targetPitch = 0)
    {
        bool headingChanged;
        bool pitchChanged;
        double normalizedHeading = MapCamera.NormalizeHeading(targetHeading);
        double normalizedPitch = MapCamera.NormalizePitch(targetPitch);
        lock (_cameraSync)
        {
            headingChanged = _targetHeading != normalizedHeading;
            pitchChanged = _targetPitch != normalizedPitch;
            _targetLongitude = longitude;
            _targetLatitude = latitude;
            _targetZoom = targetZoom;
            _targetHeading = normalizedHeading;
            _targetPitch = normalizedPitch;
            _targetViewportWidth = viewportWidth;
            _targetViewportHeight = viewportHeight;
            _targetHasZoomAnchor = false;
            _targetIsImmediate = true;
            _cameraVersion++;
        }
        if (ShouldTraceCameraTarget())
        {
            MapControlEventSource.Log.CameraTargetChanged(
                longitude,
                latitude,
                targetZoom,
                viewportWidth,
                viewportHeight,
                false);
        }
        if (headingChanged)
        {
            MapControlEventSource.Log.CameraHeadingTargetChanged(
                normalizedHeading,
                true);
        }
        if (pitchChanged)
        {
            MapControlEventSource.Log.CameraPitchTargetChanged(
                normalizedPitch,
                true);
        }
        RequestRender();
    }

    /// <summary>
    /// Publishes the currently held navigation keys for render-thread continuous navigation.
    /// </summary>
    public void SetKeyboardNavigation(KeyboardNavigationState navigation)
    {
        lock (_cameraSync)
        {
            _keyboardNavigation = navigation;
            _lastKeyboardNavigationTimestamp = 0;
        }
        RequestRender();
    }

    /// <summary>
    /// Gets the most recently displayed camera for committing a completed keyboard gesture.
    /// </summary>
    public bool TryGetDisplayedCamera(out MapCenter center, out double zoom)
    {
        return TryGetDisplayedCamera(out center, out zoom, out _);
    }

    public bool TryGetDisplayedCamera(
        out MapCenter center,
        out double zoom,
        out double heading)
    {
        return TryGetDisplayedCamera(out center, out zoom, out heading, out _);
    }

    public bool TryGetDisplayedCamera(
        out MapCenter center,
        out double zoom,
        out double heading,
        out double pitch)
    {
        lock (_cameraSync)
        {
            center = new MapCenter(_publishedLongitude, _publishedLatitude);
            zoom = _publishedZoom;
            heading = _publishedHeading;
            pitch = _publishedPitch;
            return _hasPublishedCamera;
        }
    }

    /// <summary>
    /// Determines whether camera-target telemetry is enabled and rate-limits high-frequency
    /// updates to one event per 100 milliseconds.
    /// </summary>
    private bool ShouldTraceCameraTarget()
    {
        if (!MapControlEventSource.Log.IsEnabled(
            System.Diagnostics.Tracing.EventLevel.Informational,
            MapControlEventSource.Keywords.Camera))
        {
            return false;
        }

        long timestamp = Stopwatch.GetTimestamp();
        long previous = Interlocked.Read(ref _lastCameraTargetEventTimestamp);
        if (previous != 0 &&
            Stopwatch.GetElapsedTime(previous, timestamp) < TimeSpan.FromMilliseconds(100))
        {
            return false;
        }

        Interlocked.Exchange(ref _lastCameraTargetEventTimestamp, timestamp);
        return true;
    }

    /// <summary>
    /// Converts a viewport offset using the latest camera state published by the render
    /// thread.
    /// </summary>
    public bool TryGetLocationFromOffset(double offsetX, double offsetY, out MapCenter location)
    {
        lock (_cameraSync)
        {
            if (!_hasPublishedCamera)
            {
                location = default;
                return false;
            }

            return MapCamera.TryGetLocationFromOffset(
                _publishedLongitude,
                _publishedLatitude,
                _publishedZoom,
                _publishedViewportWidth,
                _publishedViewportHeight,
                offsetX,
                offsetY,
                _publishedHeading,
                _publishedPitch,
                out location);
        }
    }

    /// <summary>
    /// Publishes the source-zoom ceiling used by subsequent scene generations.
    /// </summary>
    public void SetMaximumTileZoom(int maximumTileZoom)
    {
        lock (_cameraSync)
        {
            _targetMaximumTileZoom = Math.Clamp(maximumTileZoom, 0, MapCamera.MaximumTileZoom);
            _cameraVersion++;
        }
        RequestRender();
    }

    /// <summary>
    /// Applies camera and upload completions, then draws visible raster and icon layers in
    /// published plan order.
    /// </summary>
    /// <remarks>
    /// Runs on the render thread while the base renderer holds the render lock. A further
    /// frame is requested while any raster fade remains active.
    /// </remarks>
    protected override unsafe void RenderFrame()
    {
        UpdateCameraScene();
        ProcessCompletedRasterUploads();
        ProcessCompletedIconUploads();

        IntPtr context = ContextPointer;
        SetRasterizer(context, _rasterizerPointer);
        SetBlendState(context, _blendStatePointer);
        SetInputLayout(context, _inputLayoutPointer);
        SetVertexBuffer(context, _vertexBufferPointer, (uint)Marshal.SizeOf<TileVertex>());
        SetIndexBuffer(context, _indexBufferPointer);
        SetVertexShader(context, _vertexShaderPointer, _constantBufferPointer);

        MapScene? activeScene = _scene;
        if (activeScene is null)
        {
            return;
        }

        LayerRenderSnapshot[] plan = _layerRenderPlan;
        bool hasRasterFade = false;
        foreach (LayerRenderSnapshot layer in plan)
        {
            if (!layer.IsVisible || layer.Opacity <= 0)
            {
                continue;
            }
            if (layer.Kind == LayerRenderKind.RasterTiles)
            {
                SetBlendState(context, _blendStatePointer);
                SetInputLayout(context, _inputLayoutPointer);
                SetVertexBuffer(
                    context,
                    _vertexBufferPointer,
                    (uint)Marshal.SizeOf<TileVertex>());
                SetIndexBuffer(context, _indexBufferPointer);
                SetVertexShader(
                    context,
                    _vertexShaderPointer,
                    _constantBufferPointer);
                hasRasterFade |= DrawRasterTileLayer(context, layer);
            }
            else
            {
                DrawMapElements(context, layer.LayerIndex, layer.Opacity);
            }
        }
        TrimRasterTileCache();
        if (hasRasterFade)
        {
            RequestRender();
        }
    }

    /// <summary>
    /// Hands the just-released render lock to the bounded raster upload worker when it is
    /// waiting, avoiding starvation during continuous fades or camera animation.
    /// </summary>
    protected override void OnRenderPassCompleted()
    {
        if (Volatile.Read(ref _rasterUploadRenderLockWaiters) != 0)
        {
            _rasterUploadEnteredRenderLock.WaitOne(TimeSpan.FromMilliseconds(16));
        }
    }

    /// <summary>
    /// Advances camera animations, publishes the displayed camera, and raises scene changes
    /// when the required tile set differs.
    /// </summary>
    private void UpdateCameraScene()
    {
        ApplyPublishedCameraTarget();
        if (!_cameraInitialized || _viewportWidth <= 0 || _viewportHeight <= 0)
        {
            return;
        }

        long timestamp = Stopwatch.GetTimestamp();
        _displayZoom = _zoomAnimation.GetZoom(timestamp);
        _displayHeading = _headingAnimation.GetHeading(timestamp);
        _displayPitch = _pitchAnimation.GetPitch(timestamp);
        ZoomAnchor? zoomAnchor = _activeZoomAnchor;
        MapCenter displayCenter = zoomAnchor is null
            ? _panAnimation.GetCenter(timestamp)
            : MapCamera.CenterForLocationAtOffset(
                zoomAnchor.Value.Location,
                _displayZoom,
                zoomAnchor.Value.HorizontalOffset,
                zoomAnchor.Value.VerticalOffset,
                _displayHeading,
                _displayPitch,
                _viewportHeight);
        _displayLongitude = displayCenter.Longitude;
        _displayLatitude = displayCenter.Latitude;
        if (zoomAnchor is not null)
        {
            _panAnimation.Reset(_displayLongitude, _displayLatitude);
            if (!_zoomAnimation.IsActive)
            {
                _activeZoomAnchor = null;
            }
        }
        bool isKeyboardNavigationActive = ApplyKeyboardNavigation(timestamp);
        lock (_cameraSync)
        {
            _publishedLongitude = _displayLongitude;
            _publishedLatitude = _displayLatitude;
            _publishedZoom = _displayZoom;
            _publishedHeading = _displayHeading;
            _publishedPitch = _displayPitch;
            _publishedViewportWidth = _viewportWidth;
            _publishedViewportHeight = _viewportHeight;
            _hasPublishedCamera = true;
        }
        int tileZoom = Math.Min((int)Math.Floor(_displayZoom), _maximumTileZoom);
        MapScene scene = MapCamera.CreateScene(
            _displayLongitude,
            _displayLatitude,
            _displayZoom,
            tileZoom,
            _viewportWidth,
            _viewportHeight,
            _displayHeading,
            _displayPitch);
        _scene = scene;

        HashSet<TileId> requiredTiles = scene.RequiredTiles.ToHashSet();
        if (!_lastRequiredTiles.SetEquals(requiredTiles))
        {
            _lastRequiredTiles = requiredTiles;
            MapControlEventSource.Log.SceneChanged(
                scene.TileZoom,
                scene.RequiredTiles.Count,
                scene.Longitude,
                scene.Latitude,
                scene.Zoom);
            SceneChanged?.Invoke(scene);
        }

        if (_zoomAnimation.IsActive ||
            _panAnimation.IsActive ||
            _headingAnimation.IsActive ||
            _pitchAnimation.IsActive ||
            isKeyboardNavigationActive)
        {
            RequestRender();
        }
    }

    /// <summary>
    /// Applies the latest held-key state once its hold threshold has elapsed.
    /// </summary>
    private bool ApplyKeyboardNavigation(long timestamp)
    {
        KeyboardNavigationState navigation;
        lock (_cameraSync)
        {
            navigation = _keyboardNavigation;
        }

        if (!navigation.HasInput)
        {
            _lastKeyboardNavigationTimestamp = 0;
            return false;
        }

        TimeSpan heldDuration = Stopwatch.GetElapsedTime(navigation.StartTimestamp, timestamp);
        if (heldDuration < KeyboardNavigationState.HoldThreshold)
        {
            return true;
        }

        long previousTimestamp = _lastKeyboardNavigationTimestamp;
        _lastKeyboardNavigationTimestamp = timestamp;
        if (previousTimestamp == 0)
        {
            return true;
        }

        double elapsedSeconds = Stopwatch.GetElapsedTime(previousTimestamp, timestamp).TotalSeconds;
        double distance = Math.Min(_viewportWidth, _viewportHeight) *
            KeyboardNavigationState.PanViewportFractionsPerSecond *
            elapsedSeconds;
        MapCenter center = MapCamera.PanByPixels(
            _displayLongitude,
            _displayLatitude,
            _displayZoom,
            -navigation.HorizontalDirection * distance,
            -navigation.VerticalDirection * distance,
            _displayHeading,
            _displayPitch,
            _viewportHeight);
        _displayLongitude = center.Longitude;
        _displayLatitude = center.Latitude;
        _displayZoom = Math.Clamp(
            _displayZoom + (navigation.ZoomDirection *
                KeyboardNavigationState.ZoomLevelsPerSecond * elapsedSeconds),
            0,
            MapCamera.MaximumTileZoom);
        _activeZoomAnchor = null;
        _panAnimation.Reset(_displayLongitude, _displayLatitude);
        _zoomAnimation.Reset(_displayZoom);
        _headingAnimation.Reset(_displayHeading);
        _pitchAnimation.Reset(_displayPitch);
        return true;
    }

    /// <summary>
    /// Consumes the latest versioned camera target and retargets pan or anchored-zoom
    /// animation state on the render thread.
    /// </summary>
    private void ApplyPublishedCameraTarget()
    {
        double longitude;
        double latitude;
        double zoom;
        double heading;
        double pitch;
        double viewportWidth;
        double viewportHeight;
        int maximumTileZoom;
        MapCenter zoomAnchor;
        double zoomAnchorHorizontalOffset;
        double zoomAnchorVerticalOffset;
        bool hasZoomAnchor;
        bool isImmediate;
        long version;
        lock (_cameraSync)
        {
            version = _cameraVersion;
            if (version == _consumedCameraVersion)
            {
                return;
            }

            longitude = _targetLongitude;
            latitude = _targetLatitude;
            zoom = _targetZoom;
            heading = _targetHeading;
            pitch = _targetPitch;
            viewportWidth = _targetViewportWidth;
            viewportHeight = _targetViewportHeight;
            maximumTileZoom = _targetMaximumTileZoom;
            zoomAnchor = _targetZoomAnchor;
            zoomAnchorHorizontalOffset = _targetZoomAnchorHorizontalOffset;
            zoomAnchorVerticalOffset = _targetZoomAnchorVerticalOffset;
            hasZoomAnchor = _targetHasZoomAnchor;
            isImmediate = _targetIsImmediate;
        }

        long timestamp = Stopwatch.GetTimestamp();
        _viewportWidth = viewportWidth;
        _viewportHeight = viewportHeight;
        _maximumTileZoom = maximumTileZoom;
        if (!_cameraInitialized)
        {
            _displayLongitude = longitude;
            _displayLatitude = latitude;
            _displayZoom = zoom;
            _displayHeading = heading;
            _displayPitch = pitch;
            _panAnimation.Reset(longitude, latitude);
            _zoomAnimation.Reset(zoom);
            _headingAnimation.Reset(heading);
            _pitchAnimation.Reset(pitch);
            _cameraInitialized = true;
        }
        else if (isImmediate)
        {
            _displayLongitude = longitude;
            _displayLatitude = latitude;
            _displayZoom = zoom;
            _displayHeading = heading;
            _displayPitch = pitch;
            _activeZoomAnchor = null;
            _panAnimation.Reset(longitude, latitude);
            _zoomAnimation.Reset(zoom);
            _headingAnimation.Reset(heading);
            _pitchAnimation.Reset(pitch);
        }
        else
        {
            MapCenter center = new(longitude, latitude);
            if (hasZoomAnchor && _zoomAnimation.TargetZoom != zoom)
            {
                _activeZoomAnchor = new ZoomAnchor(
                    zoomAnchor,
                    zoomAnchorHorizontalOffset,
                    zoomAnchorVerticalOffset);
                _panAnimation.Reset(_displayLongitude, _displayLatitude);
            }
            else
            {
                _activeZoomAnchor = null;
            }
            if (!hasZoomAnchor && _panAnimation.Target != center)
            {
                _panAnimation.SetTarget(
                    _displayLongitude,
                    _displayLatitude,
                    longitude,
                    latitude,
                    timestamp);
            }
            if (_zoomAnimation.TargetZoom != zoom)
            {
                _zoomAnimation.SetTarget(_displayZoom, zoom, timestamp);
            }
            if (_headingAnimation.TargetHeading != heading)
            {
                _headingAnimation.SetTarget(
                    _displayHeading,
                    heading,
                    timestamp);
            }
            if (_pitchAnimation.TargetPitch != pitch)
            {
                _pitchAnimation.SetTarget(
                    _displayPitch,
                    pitch,
                    timestamp);
            }
        }
        _consumedCameraVersion = version;
    }


    /// <summary>
    /// Stops and joins the upload worker, releases base rendering resources, clears retained
    /// icon state, and disposes upload synchronization handles.
    /// </summary>
    public override void Dispose()
    {
        if (_uploadDisposed)
        {
            return;
        }
        _uploadDisposed = true;
        StopUploadThread();

        base.Dispose();
        lock (_iconSync)
        {
            _iconPixels.Clear();
        }
        lock (_mapElementsSync)
        {
            _mapIcons.Rebuild([]);
            _mapGeometries = [];
        }
        _uploadRequested.Dispose();
        _rasterUploadEnteredRenderLock.Dispose();
        _uploadShutdown.Dispose();
        _uploadThreadStopped.Dispose();
    }

    private void StartUploadThread()
    {
        if (_uploadDisposed || _uploadThread is not null)
        {
            return;
        }

        _uploadShutdown.Reset();
        _uploadThreadStopped.Reset();
        Thread thread = new(UploadLoop)
        {
            IsBackground = true,
            Name = "Map tile GPU upload thread",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        _uploadThread = thread;
        thread.Start();
    }

    private void StopUploadThread()
    {
        Thread? thread = _uploadThread;
        if (thread is null)
        {
            return;
        }

        _uploadShutdown.Set();
        _uploadRequested.Set();
        if (thread.IsAlive && thread != Thread.CurrentThread)
        {
            _uploadThreadStopped.WaitOne();
        }
        _uploadThread = null;
    }



    /// <summary>
    /// Services queued raster and icon GPU uploads on the dedicated MTA worker until
    /// shutdown, draining deferred texture disposals around each pass.
    /// </summary>
    private void UploadLoop()
    {
        WaitHandle[] handles = [_uploadRequested, _uploadShutdown];
        try
        {
            while (WaitHandle.WaitAny(handles) == 0)
            {
                if (_uploadShutdown.WaitOne(0))
                {
                    return;
                }
                DrainTextureDisposals();
                ProcessRasterPixelUploads();
                ProcessIconPixelUploads();
                DrainTextureDisposals();
            }
        }
        finally
        {
            _uploadThreadStopped.Set();
        }
    }








    /// <summary>
    /// Retains the geographic location and viewport offset that must remain stationary during
    /// an anchored zoom animation.
    /// </summary>
    private readonly record struct ZoomAnchor(
        MapCenter Location,
        double HorizontalOffset,
        double VerticalOffset);

}
