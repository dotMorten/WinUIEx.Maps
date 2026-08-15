using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinUIEx.Maps.Rendering.Diagnostics;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using static WinUIEx.Maps.Rendering.DirectXInterop;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Owns the WinUI swap-chain attachment, D3D11 device lifecycle, synchronization, and
/// dedicated frame-rendering thread shared by concrete map renderers.
/// </summary>
/// <remarks>
/// <para>
/// Panel attachment, loaded/unloaded notifications, resize handling, and swap-chain
/// association originate on the UI thread. Once initialized, <see cref="RequestRender"/>
/// wakes a background MTA render thread, which serializes clear, derived rendering, and
/// present operations.
/// </para>
/// <para>
/// <see cref="RenderLock"/> is the ownership boundary for device state. Device creation,
/// size-dependent resource replacement, renderer-specific resource creation/release, frame
/// drawing, and native pointer capture all occur while that lock prevents resize, device
/// teardown, and drawing from racing. Derived classes may publish CPU-only state through
/// their own locks, but must consume GPU state inside this lock.
/// </para>
/// <para>
/// Unloading stops and joins the render thread while retaining the swap chain and device
/// resources for a fast reload. Final root detachment or disposal additionally releases
/// derived and base COM resources in dependency order. Failures are reduced to fixed
/// operation text, exception type, and HRESULT before ETW emission; this lifecycle layer
/// never emits request URLs, credentials, attribution, or rendered pixels.
/// </para>
/// </remarks>
internal abstract class DirectXRenderer : IDisposable
{
    private readonly object _renderLock = new();
    private readonly AutoResetEvent _renderRequested = new(false);
    private readonly ManualResetEvent _shutdownRequested = new(false);
    private readonly ManualResetEvent _renderThreadStopped = new(true);
    private readonly object _frameCaptureSync = new();
    private readonly ConcurrentQueue<FrameCaptureRequest> _frameCaptureRequests = [];
    private SwapChainPanel? _panel;
    private IntPtr _swapChainPointer;
    private IntPtr _devicePointer;
    private IntPtr _contextPointer;
    private IntPtr _renderTargetPointer;
    private long _swapChainMemoryPressure;
    private Thread? _renderThread;
    private bool _initialized;
    private bool _initializing;
    private bool _threadRunning;
    private bool _disposed;
    private bool _attachmentReconciliationQueued;
    private volatile bool _needsRender;
    private D3D11_VIEWPORT _viewport;
    protected object RenderLock => _renderLock;
    protected IntPtr DevicePointer => _devicePointer;
    protected IntPtr ContextPointer => _contextPointer;
    protected D3D11_VIEWPORT Viewport => _viewport;
    protected bool IsInitialized => _initialized;
    internal bool HasDeviceResources => _initialized;

    /// <summary>
    /// Completes with a top-down BGRA copy of the next render frame after derived
    /// asynchronous work reports that it is ready.
    /// </summary>
    internal Task<MapRenderFrame> CaptureFrameAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_frameCaptureSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var request = new FrameCaptureRequest(cancellationToken);
            _frameCaptureRequests.Enqueue(request);
            RequestRender();
            return request.Task;
        }
    }

    ~DirectXRenderer()
    {
        ReleaseResources(detachSwapChain: false);
    }

    /// <summary>
    /// Attaches the renderer to a swap-chain panel, transfers event ownership from any
    /// previous panel, and starts rendering when the surface is ready.
    /// </summary>
    public void Attach(SwapChainPanel panel)
    {
        if (_panel == panel)
        {
            if (panel.XamlRoot is not null)
            {
                Resume();
            }
            return;
        }

        DetachPanelEvents();
        _panel = panel;
        panel.Loaded += OnPanelLoaded;
        panel.Unloaded += OnPanelUnloaded;
        panel.SizeChanged += OnPanelSizeChanged;
        if (panel.XamlRoot is not null)
        {
            Resume();
        }
    }

    /// <summary>
    /// Marks the frame dirty and wakes the dedicated render thread.
    /// </summary>
    public void RequestRender()
    {
        _needsRender = true;
        _renderRequested.Set();
    }

    /// <summary>
    /// Stops background rendering, detaches panel events, releases all native resources, and
    /// disposes synchronization handles owned by the renderer.
    /// </summary>
    public virtual void Dispose()
    {
        lock (_frameCaptureSync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }

        Suspend();
        ReleaseResources();
        DetachPanelEvents();
        FailFrameCaptures(new ObjectDisposedException(GetType().Name));
        _renderRequested.Dispose();
        _shutdownRequested.Dispose();
        _renderThreadStopped.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Creates renderer-specific device resources while the render lock is held.
    /// </summary>
    protected abstract void CreateRendererResources();

    /// <summary>
    /// Determines whether renderer-specific resources are complete enough to draw a frame.
    /// </summary>
    protected abstract bool HasRendererResources();

    /// <summary>
    /// Records renderer-specific draw work on the dedicated render thread while the render
    /// lock is held.
    /// </summary>
    protected abstract void RenderFrame();

    /// <summary>
    /// Gives derived resource publishers a chance to acquire the render lock after a frame
    /// releases it, preventing continuous animation frames from starving bounded uploads.
    /// </summary>
    protected virtual void OnRenderPassCompleted()
    {
    }

    /// <summary>
    /// Determines whether asynchronous renderer work required by a capture has reached the
    /// render thread.
    /// </summary>
    protected virtual bool CanCompleteFrameCaptures() => true;

    /// <summary>
    /// Releases renderer-specific device resources while the render lock is held.
    /// </summary>
    protected abstract void ReleaseRendererResources();

    /// <summary>
    /// Reconciles panel attachment after WinUI lifecycle notifications settle.
    /// </summary>
    private void OnPanelLoaded(object sender, RoutedEventArgs e) =>
        QueueAttachmentReconciliation();

    /// <summary>
    /// Reconciles panel attachment after WinUI lifecycle notifications settle.
    /// </summary>
    private void OnPanelUnloaded(object sender, RoutedEventArgs e) =>
        QueueAttachmentReconciliation();

    private void QueueAttachmentReconciliation()
    {
        if (_attachmentReconciliationQueued || _panel is null)
        {
            return;
        }

        // Loaded and Unloaded can arrive out of order while an element is reparented.
        // https://github.com/microsoft/microsoft-ui-xaml/issues/1900
        _attachmentReconciliationQueued = _panel.DispatcherQueue.TryEnqueue(() =>
        {
            _attachmentReconciliationQueued = false;
            if (IsPanelAttachedToXamlRootVisualTree())
            {
                Resume();
            }
            else
            {
                Suspend();
            }
        });
    }

    private bool IsPanelAttachedToXamlRootVisualTree()
    {
        if (_panel?.XamlRoot?.Content is not DependencyObject rootContent)
        {
            return false;
        }

        DependencyObject? current = _panel;
        while (current is not null)
        {
            if (ReferenceEquals(current, rootContent))
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    /// <summary>
    /// Recreates swap-chain-size resources for a resized panel or starts initialization if
    /// the renderer is not yet ready.
    /// </summary>
    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_initialized)
        {
            try
            {
                CreateSizeDependentResources();
                RequestRender();
            }
            catch (Exception exception)
            {
                RaiseRendererFailed("The map surface could not be resized.", exception);
            }
        }
        else
        {
            Resume();
        }
    }

    /// <summary>
    /// Starts rendering and recreates device resources when the attached surface is ready.
    /// </summary>
    internal virtual void Resume() => Start();

    /// <summary>
    /// Stops rendering while retaining device resources for a fast visual-tree reload.
    /// </summary>
    internal virtual void Suspend() => StopRenderThread();

    /// <summary>
    /// Stops rendering and releases device resources after the owning XAML root is unloaded.
    /// </summary>
    protected void ReleaseResources(bool detachSwapChain = true)
    {
        StopRenderThread();
        ReleaseDeviceResources(detachSwapChain);
    }

    /// <summary>
    /// Releases dormant device resources and removes native panel event subscriptions.
    /// A later <see cref="Attach"/> call can reconnect the renderer.
    /// </summary>
    protected void ReleaseResourcesAndDetachPanel()
    {
        ReleaseResources();
        DetachPanelEvents();
    }

    /// <summary>
    /// Starts the render thread and device lifecycle once a nonempty XAML surface is
    /// available.
    /// </summary>
    private void Start()
    {
        if (_disposed || _panel?.XamlRoot is null || _panel.ActualWidth <= 0 || _panel.ActualHeight <= 0)
        {
            return;
        }

        EnsureRenderThread();
        EnsureInitialized();
    }

    /// <summary>
    /// Creates the single background MTA thread that services render requests.
    /// </summary>
    private void EnsureRenderThread()
    {
        if (_threadRunning)
        {
            return;
        }

        _shutdownRequested.Reset();
        _renderThreadStopped.Reset();
        _renderThread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = $"{GetType().Name} render thread",
        };
        _renderThread.SetApartmentState(ApartmentState.MTA);
        _threadRunning = true;
        _renderThread.Start();
    }

    /// <summary>
    /// Creates device, swap-chain, renderer, and size-dependent resources as one guarded
    /// initialization attempt.
    /// </summary>
    /// <remarks>
    /// Called from the UI thread because swap-chain attachment touches the
    /// <see cref="SwapChainPanel"/>; drawing subsequently occurs on the render thread.
    /// </remarks>
    private void EnsureInitialized()
    {
        if (_initialized || _initializing || _disposed)
        {
            return;
        }

        _initializing = true;
        long startTimestamp = Stopwatch.GetTimestamp();
        bool succeeded = false;
        MapControlEventSource.Log.DeviceResourcesCreateStart(
            GetType().Name,
            (int)GetPixelWidth(),
            (int)GetPixelHeight());
        try
        {
            CreateDeviceResources();
            CreateSwapChain();
            lock (_renderLock)
            {
                CreateRendererResources();
            }
            CreateSizeDependentResources();
            _initialized = true;
            succeeded = true;
            RequestRender();
        }
        catch (Exception exception)
        {
            ReleaseDeviceResources();
            RaiseRendererFailed("The native map renderer could not be initialized.", exception);
        }
        finally
        {
            MapControlEventSource.Log.DeviceResourcesCreateStop(
                GetType().Name,
                Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                succeeded);
            _initializing = false;
        }
    }

    /// <summary>
    /// Creates the hardware D3D11 device and immediate context and captures owned native
    /// pointers for render-thread calls.
    /// </summary>
    private unsafe void CreateDeviceResources()
    {
        lock (_renderLock)
        {
            D3D_FEATURE_LEVEL[] levels =
            [
                D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
                D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_1,
                D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_0,
                D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_9_3,
            ];

            CreateDevice(levels, out _devicePointer, out _contextPointer);
        }
    }

    /// <summary>
    /// Creates a flip-sequential composition swap chain and attaches it to the XAML panel.
    /// </summary>
    private unsafe void CreateSwapChain()
    {
        lock (_renderLock)
        {
            if (_panel is null || _devicePointer == IntPtr.Zero)
            {
                return;
            }

            DXGI_SWAP_CHAIN_DESC1 description = new()
            {
                Width = GetPixelWidth(),
                Height = GetPixelHeight(),
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                Stereo = false,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                BufferUsage = DXGI_USAGE.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 2,
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
            };

            _swapChainPointer = CreateSwapChainForComposition(_devicePointer, &description);
            SetSwapChain(_panel, _swapChainPointer);
            SetSwapChainMemoryPressure(
                checked((long)description.Width * description.Height * 4 * description.BufferCount));
        }
    }

    /// <summary>
    /// Resizes swap-chain buffers and rebuilds the render-target view and viewport for the
    /// panel's current pixel dimensions.
    /// </summary>
    private unsafe void CreateSizeDependentResources()
    {
        lock (_renderLock)
        {
            if (_swapChainPointer == IntPtr.Zero || _devicePointer == IntPtr.Zero)
            {
                return;
            }

            if (_contextPointer != IntPtr.Zero)
            {
                UnsetRenderTarget(_contextPointer);
            }
            ReleasePointer(ref _renderTargetPointer);
            uint width = GetPixelWidth();
            uint height = GetPixelHeight();
            ResizeBuffers(
                _swapChainPointer,
                2,
                width,
                height,
                DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM);
            SetSwapChainMemoryPressure(checked((long)width * height * 4 * 2));

            IntPtr bufferPointer = GetBackBuffer(_swapChainPointer);
            try
            {
                _renderTargetPointer = CreateView(
                    _devicePointer,
                    bufferPointer,
                    9,
                    "Failed to create the map render target.");
            }
            finally
            {
                ReleasePointer(ref bufferPointer);
            }

            _viewport = new D3D11_VIEWPORT
            {
                Width = width,
                Height = height,
                MinDepth = 0,
                MaxDepth = 1,
            };
        }
    }

    /// <summary>
    /// Gets a nonzero integral swap-chain width from the attached panel.
    /// </summary>
    private uint GetPixelWidth() => (uint)Math.Max(1, Math.Ceiling(_panel?.ActualWidth ?? 1));

    /// <summary>
    /// Gets a nonzero integral swap-chain height from the attached panel.
    /// </summary>
    private uint GetPixelHeight() => (uint)Math.Max(1, Math.Ceiling(_panel?.ActualHeight ?? 1));

    /// <summary>
    /// Waits on render and shutdown signals and dispatches requested frames on the dedicated
    /// render thread.
    /// </summary>
    private void RenderLoop()
    {
        WaitHandle[] handles = [_renderRequested, _shutdownRequested];
        try
        {
            while (WaitHandle.WaitAny(handles) == 0)
            {
                TryRender();
            }
        }
        finally
        {
            _renderThreadStopped.Set();
        }
    }

    /// <summary>
    /// Draws and presents one dirty frame when device and renderer resources are valid.
    /// </summary>
    /// <remarks>
    /// The full clear, draw, and present sequence is serialized by the render lock so UI
    /// resize and teardown cannot mutate resources concurrently.
    /// </remarks>
    private void TryRender()
    {
        if (!_needsRender || !_initialized || _disposed)
        {
            return;
        }

        try
        {
            bool rendered = false;
            lock (_renderLock)
            {
                if (_contextPointer == IntPtr.Zero ||
                    _swapChainPointer == IntPtr.Zero ||
                    _renderTargetPointer == IntPtr.Zero ||
                    !HasRendererResources())
                {
                    return;
                }

                _needsRender = false;
                Clear(_contextPointer, _renderTargetPointer, [0.94f, 0.94f, 0.94f, 1]);
                SetRenderTarget(_contextPointer, _renderTargetPointer);
                SetViewport(_contextPointer, _viewport);
                RenderFrame();
                if (!_frameCaptureRequests.IsEmpty &&
                    CanCompleteFrameCaptures())
                {
                    CompleteFrameCaptures();
                }
                Present(_swapChainPointer);
                rendered = true;
            }
            if (rendered)
            {
                OnRenderPassCompleted();
            }
        }
        catch (Exception exception)
        {
            FailFrameCaptures(exception);
            RaiseRendererFailed("The native map renderer failed while drawing a frame.", exception);
        }
    }

    private unsafe void CompleteFrameCaptures()
    {
        List<FrameCaptureRequest> requests = [];
        while (_frameCaptureRequests.TryDequeue(
            out FrameCaptureRequest? request))
        {
            if (request.IsCompleted)
            {
                request.DisposeCancellationRegistration();
            }
            else
            {
                requests.Add(request);
            }
        }
        if (requests.Count == 0)
        {
            return;
        }

        int width = checked((int)_viewport.Width);
        int height = checked((int)_viewport.Height);
        IntPtr backBuffer = GetBackBuffer(_swapChainPointer);
        IntPtr staging = IntPtr.Zero;
        try
        {
            D3D11_TEXTURE2D_DESC description = new()
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                BindFlags = 0,
                CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
                MiscFlags = 0,
            };
            staging = CreateTexture(
                _devicePointer,
                &description,
                null,
                "Failed to create the map-frame readback texture.");
            byte[] pixels = ReadTextureBgra(
                _contextPointer,
                backBuffer,
                staging,
                width,
                height);
            var frame = new MapRenderFrame(pixels, width, height);
            foreach (FrameCaptureRequest request in requests)
            {
                request.TrySetResult(frame);
            }
        }
        catch (Exception exception)
        {
            foreach (FrameCaptureRequest request in requests)
            {
                request.TrySetException(exception);
            }
            throw;
        }
        finally
        {
            ReleasePointer(ref staging);
            ReleasePointer(ref backBuffer);
        }
    }

    private void FailFrameCaptures(Exception exception)
    {
        while (_frameCaptureRequests.TryDequeue(out FrameCaptureRequest? request))
        {
            request.TrySetException(exception);
        }
    }

    /// <summary>
    /// Signals render-thread shutdown and waits for completion unless called by that thread.
    /// </summary>
    private void StopRenderThread()
    {
        if (!_threadRunning)
        {
            return;
        }

        _threadRunning = false;
        _shutdownRequested.Set();
        _renderRequested.Set();
        if (_renderThread is not null && _renderThread != Thread.CurrentThread)
        {
            _renderThreadStopped.WaitOne();
        }
        _renderThread = null;
        FailFrameCaptures(new InvalidOperationException(
            "The map renderer stopped before the requested frame was captured."));
    }

    /// <summary>
    /// Detaches the swap chain and releases renderer and D3D resources in dependency order
    /// under the render lock.
    /// </summary>
    private void ReleaseDeviceResources(bool detachSwapChain = true)
    {
        lock (_renderLock)
        {
            if (detachSwapChain && _panel is not null && _swapChainPointer != IntPtr.Zero)
            {
                SetSwapChain(_panel, IntPtr.Zero);
            }

            ReleaseRendererResources();
            ReleasePointer(ref _renderTargetPointer);
            ReleasePointer(ref _swapChainPointer);
            ReleasePointer(ref _contextPointer);
            TrimDevice(_devicePointer);
            ReleasePointer(ref _devicePointer);
            SetSwapChainMemoryPressure(0);
            _initialized = false;
        }
    }

    private void SetSwapChainMemoryPressure(long bytes)
    {
        long previous = Interlocked.Exchange(ref _swapChainMemoryPressure, bytes);
        if (previous > 0)
        {
            GC.RemoveMemoryPressure(previous);
        }
        if (bytes > 0)
        {
            GC.AddMemoryPressure(bytes);
        }
    }

    /// <summary>
    /// Removes lifecycle event handlers from the attached panel and clears the association.
    /// </summary>
    private void DetachPanelEvents()
    {
        if (_panel is null)
        {
            return;
        }

        _panel.Loaded -= OnPanelLoaded;
        _panel.Unloaded -= OnPanelUnloaded;
        _panel.SizeChanged -= OnPanelSizeChanged;
        _panel = null;
    }

    /// <summary>
    /// Emits sanitized renderer-failure metadata without logging map content, URLs, or
    /// credentials.
    /// </summary>
    private void RaiseRendererFailed(string message, Exception exception)
    {
        MapControlEventSource.Log.RendererFailure(
            message,
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.HResult);
    }

    private sealed class FrameCaptureRequest
    {
        private readonly TaskCompletionSource<MapRenderFrame> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;
        private readonly CancellationToken _cancellationToken;

        internal FrameCaptureRequest(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            if (cancellationToken.CanBeCanceled)
            {
                _registration = cancellationToken.Register(
                    static state =>
                        ((FrameCaptureRequest)state!).TrySetCanceled(),
                    this);
            }
        }

        internal Task<MapRenderFrame> Task => _completion.Task;

        internal bool IsCompleted => _completion.Task.IsCompleted;

        internal void TrySetResult(MapRenderFrame frame)
        {
            _completion.TrySetResult(frame);
            _registration.Dispose();
        }

        internal void TrySetException(Exception exception)
        {
            _completion.TrySetException(exception);
            _registration.Dispose();
        }

        internal void DisposeCancellationRegistration() =>
            _registration.Dispose();

        private void TrySetCanceled()
        {
            _completion.TrySetCanceled(_cancellationToken);
        }
    }

}

internal sealed record MapRenderFrame(
    ReadOnlyMemory<byte> Pixels,
    int Width,
    int Height);
