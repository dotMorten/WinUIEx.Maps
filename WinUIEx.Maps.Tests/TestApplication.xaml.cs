using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

public sealed partial class TestApplication : Application
{
    public TestApplication() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var titleBar = new TitleBar
        {
            Title = "UI Tests",
        };
        var contentHost = new ContentControl
        {
            Width = 640,
            Height = 480,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = GridLength.Auto,
        });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(titleBar);
        Grid.SetRow(contentHost, 1);
        root.Children.Add(contentHost);
        var window = new Window
        {
            Title = "UI Tests",
            Content = root,
        };
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(titleBar);

        MapControlTestHost.Initialize(this, window, titleBar, contentHost);
        TypedEventHandler<object, WindowActivatedEventArgs>? activatedHandler = null;
        activatedHandler = (_, eventArgs) =>
        {
            if (eventArgs.WindowActivationState == WindowActivationState.Deactivated)
            {
                return;
            }

            window.Activated -= activatedHandler;
            window.DispatcherQueue.TryEnqueue(MapControlTestHost.CompleteInitialization);
        };
        window.Activated += activatedHandler;
        window.Activate();
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)) / 96d;
        window.AppWindow.Resize(new SizeInt32((int)(800 * scale), (int)(600 * scale)));
        window.AppWindow.Move(new PointInt32(40, 40));
        if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
        }
    }

    internal void Stop() => Exit();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
