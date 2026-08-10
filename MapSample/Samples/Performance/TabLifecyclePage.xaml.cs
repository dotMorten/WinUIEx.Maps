using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MapControl = WinUIEx.Maps.MapControl;

namespace MapSample.Samples.Performance;

public sealed partial class TabLifecyclePage : Page
{
    private readonly MapControl _map = PerformanceMapFactory.Create();
    private int _loadedCount;
    private int _unloadedCount;

    public TabLifecyclePage()
    {
        InitializeComponent();
        AutomationProperties.SetAutomationId(_map, "MapSurface");
        _map.Loaded += Map_Loaded;
        _map.Unloaded += Map_Unloaded;
        MapHost.Children.Add(_map);
        UpdateStatus();
    }

    private void SwitchTab_Click(object sender, RoutedEventArgs e) =>
        LifecycleTabs.SelectedIndex = LifecycleTabs.SelectedIndex == 0 ? 1 : 0;

    private void Map_Loaded(object sender, RoutedEventArgs e)
    {
        _loadedCount++;
        UpdateStatus();
    }

    private void Map_Unloaded(object sender, RoutedEventArgs e)
    {
        _unloadedCount++;
        UpdateStatus();
    }

    private void UpdateStatus() =>
        LifecycleStatus.Text =
            $"Loaded: {_loadedCount:N0}; unloaded: {_unloadedCount:N0}";
}
