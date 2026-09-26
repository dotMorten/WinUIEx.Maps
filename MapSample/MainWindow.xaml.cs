using MapSample.Samples.Interaction;
using MapSample.Samples.Maps;
using MapSample.Samples.Performance;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics;

namespace MapSample;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    public MainWindow()
    {
        WinUIEx.WindowManager.Get(this).PersistenceId = "MainWindow";
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ResizeWindow(1280, 820);
        SampleNavigation.SelectedItem = HomeItem;
        Navigate("home", "Home");
    }

    private void ResizeWindow(double width, double height)
    {
        IntPtr window = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        double scale = GetDpiForWindow(window) / 96d;
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(width * scale),
            (int)Math.Round(height * scale)));
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        SampleNavigation.IsPaneOpen = !SampleNavigation.IsPaneOpen;

    internal void NavigateHome() => SampleNavigation.SelectedItem = HomeItem;

    private void SampleNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem item &&
            item.Tag is string tag)
        {
            Navigate(tag, item.Content?.ToString() ?? string.Empty);
        }
    }

    private string? _currentSampleTag;

    private void Navigate(string tag, string title)
    {
        Type pageType = tag switch
        {
            "basemaps" => typeof(BasemapPage),
            "azure-traffic" or "azure-weather" => typeof(AzureOverlaysPage),
            "openstreetmap" => typeof(OpenStreetMapPage),
            "arcgis-vector" => typeof(CustomVectorTilesPage),
            "elements" => typeof(MapElementsPage),
            "camera" => typeof(CameraBindingPage),
            "set-view" => typeof(TrySetViewPage),
            "location" => typeof(CurrentLocationPage),
            "stress" => typeof(StressTestPage),
            "tab-lifecycle" => typeof(TabLifecyclePage),
            "remove-reinsert" => typeof(RemoveReinsertPage),
            "reparent" => typeof(ReparentPage),
            "lifetime-stress" => typeof(LifetimeStressPage),
            _ => typeof(HomePage),
        };
        if (SampleFrame.CurrentSourcePageType != pageType || _currentSampleTag != tag)
        {
            FrameworkElement? previousSample = SampleFrame.Content as FrameworkElement;
            if (previousSample is not null)
                previousSample.Unloaded += PreviousSample_Unloaded;
            if (SampleFrame.Navigate(pageType, tag))
                _currentSampleTag = tag;
            else if (previousSample is not null)
                previousSample.Unloaded -= PreviousSample_Unloaded;
        }
        AppTitleBar.Subtitle = title;
    }

    private static void PreviousSample_Unloaded(object sender, RoutedEventArgs e)
    {
        FrameworkElement previousSample = (FrameworkElement)sender;
        previousSample.Unloaded -= PreviousSample_Unloaded;
        // Wait for navigation and queued control unload handlers to release their references.
        previousSample.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, static () =>
        {
            _ = Task.Run(static () =>
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            });
        });
    }
}
