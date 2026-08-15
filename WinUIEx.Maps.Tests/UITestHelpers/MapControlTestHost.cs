using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.CompilerServices;
using Windows.Devices.Geolocation;

namespace WinUIEx.Maps.Tests.UITestHelpers;

internal static class MapControlTestHost
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly object Sync = new();

    private static DispatcherQueue? _dispatcherQueue;
    private static Window? _window;
    private static TitleBar? _titleBar;
    private static ContentControl? _contentHost;
    private static TestApplication? _application;
    private static Thread? _uiThread;
    private static TaskCompletionSource? _startupCompletion;
    private static TaskCompletionSource? _uiThreadStopped;

    internal static DispatcherQueue DispatcherQueue =>
        _dispatcherQueue ?? throw new InvalidOperationException("The WinUI test host has not started.");

    internal static Window Window =>
        _window ?? throw new InvalidOperationException("The WinUI test host has not started.");

    internal static ContentControl ContentHost =>
        _contentHost ?? throw new InvalidOperationException("The WinUI test host has not started.");

    internal static Task LoadMapControlAsync(
        Action<MapControl> onLoad,
        [CallerMemberName] string testName = "")
    {
        ArgumentNullException.ThrowIfNull(onLoad);
        return LoadMapControlAsync(map =>
        {
            onLoad(map);
            return Task.CompletedTask;
        }, testName);
    }

    internal static async Task LoadMapControlAsync(
        Func<MapControl, Task> onLoad,
        [CallerMemberName] string testName = "")
    {
        ArgumentNullException.ThrowIfNull(onLoad);

        await LoadUIAsync(
            CreateMapControl,
            element => onLoad((MapControl)element),
            testName);
    }

    internal static Task LoadMapControlAsync(
        BasicGeoposition initialCenter,
        double initialZoomLevel,
        Func<MapControl, Task> onLoad,
        [CallerMemberName] string testName = "")
    {
        ArgumentNullException.ThrowIfNull(onLoad);

        return LoadUIAsync(
            () =>
            {
                MapControl map = CreateMapControl();
                map.MapStyle = MapStyle.Blank;
                map.Center = new Geopoint(initialCenter);
                map.ZoomLevel = initialZoomLevel;
                return map;
            },
            element => onLoad((MapControl)element),
            testName);
    }

    internal static Task LoadUIAsync(
        Func<UIElement> createUI,
        Action<UIElement> onLoad,
        [CallerMemberName] string testName = "")
    {
        ArgumentNullException.ThrowIfNull(onLoad);
        return LoadUIAsync(
            createUI,
            element =>
            {
                onLoad(element);
                return Task.CompletedTask;
            },
            testName);
    }

    internal static async Task LoadUIAsync(
        Func<UIElement> createUI,
        Func<UIElement, Task> onLoad,
        [CallerMemberName] string testName = "")
    {
        ArgumentNullException.ThrowIfNull(createUI);
        ArgumentNullException.ThrowIfNull(onLoad);

        await EnsureInitializedAsync();
        await RunAsync(async () =>
        {
            UIElement element = createUI() ??
                throw new InvalidOperationException("The UI factory returned null.");
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler loadedHandler = (_, _) => loaded.TrySetResult();
            FrameworkElement? frameworkElement = element as FrameworkElement;
            if (frameworkElement is not null)
            {
                frameworkElement.Loaded += loadedHandler;
            }

            try
            {
                if (ContentHost.Content is not null)
                {
                    throw new InvalidOperationException("The WinUI test host already contains UI.");
                }

                if (_titleBar is not null)
                {
                    _titleBar.Subtitle = testName;
                }
                ContentHost.Content = element;
                if (frameworkElement is { IsLoaded: false })
                {
                    await loaded.Task.WaitAsync(StartupTimeout);
                }

                await onLoad(element);
            }
            finally
            {
                if (frameworkElement is not null)
                {
                    frameworkElement.Loaded -= loadedHandler;
                }
                if (ReferenceEquals(ContentHost.Content, element))
                {
                    ContentHost.Content = null;
                }
                if (_titleBar is not null)
                {
                    _titleBar.Subtitle = string.Empty;
                }
            }
        });
    }

    internal static Task RunAsync(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return RunAsync(() =>
        {
            callback();
            return Task.CompletedTask;
        });
    }

    internal static Task<T> RunAsync<T>(Func<T> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return RunAsync(() => Task.FromResult(callback()));
    }

    internal static Task<T> RunAsync<T>(Func<Task<T>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var completed = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                completed.TrySetResult(await callback());
            }
            catch (Exception exception)
            {
                completed.TrySetException(exception);
            }
        }))
        {
            completed.TrySetException(
                new InvalidOperationException("The WinUI test host dispatcher rejected the test operation."));
        }

        return completed.Task;
    }

    internal static Task RunAsync(Func<Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return RunAsync(async () =>
        {
            await callback();
            return true;
        });
    }

    internal static void Initialize(
        TestApplication application,
        Window window,
        TitleBar titleBar,
        ContentControl contentHost)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(titleBar);
        ArgumentNullException.ThrowIfNull(contentHost);
        lock (Sync)
        {
            if (_window is not null)
            {
                throw new InvalidOperationException("The WinUI test host is already running.");
            }

            _application = application;
            _dispatcherQueue = window.DispatcherQueue;
            _titleBar = titleBar;
            _contentHost = contentHost;
            _window = window;
        }
    }

    internal static void CompleteInitialization()
    {
        lock (Sync)
        {
            if (_window is null)
            {
                throw new InvalidOperationException(
                    "The WinUI test host cannot complete initialization before a window exists.");
            }

            _startupCompletion?.TrySetResult();
        }
    }

    private static MapControl CreateMapControl() =>
        new()
        {
            Width = 640,
            Height = 480,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.Colors.Transparent),
        };

    internal static async Task ShutdownAsync()
    {
        DispatcherQueue? dispatcherQueue;
        Task? uiThreadStopped;
        lock (Sync)
        {
            dispatcherQueue = _dispatcherQueue;
            uiThreadStopped = _uiThreadStopped?.Task;
        }

        if (dispatcherQueue is null || uiThreadStopped is null)
        {
            return;
        }

        var shutdownQueued = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_contentHost is not null)
                {
                    _contentHost.Content = null;
                }

                _window?.Close();
                _application?.Stop();
                shutdownQueued.TrySetResult();
            }
            catch (Exception exception)
            {
                shutdownQueued.TrySetException(exception);
            }
        }))
        {
            throw new InvalidOperationException(
                "The WinUI test host dispatcher rejected shutdown.");
        }

        await shutdownQueued.Task.WaitAsync(StartupTimeout);
        await uiThreadStopped.WaitAsync(StartupTimeout);
        lock (Sync)
        {
            _application = null;
            _titleBar = null;
            _contentHost = null;
            _window = null;
            _dispatcherQueue = null;
            _uiThread = null;
            _startupCompletion = null;
            _uiThreadStopped = null;
        }
    }

    private static Task EnsureInitializedAsync()
    {
        lock (Sync)
        {
            if (_window is not null)
            {
                return Task.CompletedTask;
            }

            if (_startupCompletion is not null)
            {
                return _startupCompletion.Task.WaitAsync(StartupTimeout);
            }

            _startupCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _uiThreadStopped = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _uiThread = new Thread(StartApplication)
            {
                IsBackground = true,
                Name = "WinUI test application thread",
            };
            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.Start();
            return _startupCompletion.Task.WaitAsync(StartupTimeout);
        }
    }

    private static void StartApplication()
    {
        try
        {
            Application.Start(_ =>
            {
                DispatcherQueue dispatcherQueue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(dispatcherQueue));
                new TestApplication();
            });
        }
        catch (Exception exception)
        {
            lock (Sync)
            {
                _startupCompletion?.TrySetException(exception);
            }
        }
        finally
        {
            lock (Sync)
            {
                _uiThreadStopped?.TrySetResult();
            }
        }
    }
}
