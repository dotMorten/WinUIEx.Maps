using MapSample.Samples.Interaction;
using MapSample.Samples.Maps;
using MapSample.Samples.Performance;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace MapSample;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    public MainWindow()
    {
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

    private void Navigate(string tag, string title)
    {
        Type pageType = tag switch
        {
            "basemaps" => typeof(BasemapPage),
            "openstreetmap" => typeof(OpenStreetMapPage),
            "arcgis-vector" => typeof(CustomVectorTilesPage),
            "elements" => typeof(MapElementsPage),
            "camera" => typeof(CameraBindingPage),
            "location" => typeof(CurrentLocationPage),
            "stress" => typeof(StressTestPage),
            "tab-lifecycle" => typeof(TabLifecyclePage),
            "remove-reinsert" => typeof(RemoveReinsertPage),
            "reparent" => typeof(ReparentPage),
            "lifetime-stress" => typeof(LifetimeStressPage),
            _ => typeof(HomePage),
        };
        if (SampleFrame.CurrentSourcePageType != pageType)
        {
            SampleFrame.Navigate(pageType);
        }
        AppTitleBar.Subtitle = title;
    }
}
