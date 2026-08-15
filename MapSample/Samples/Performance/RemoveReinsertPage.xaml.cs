using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MapControl = WinUIEx.Maps.MapControl;

namespace MapSample.Samples.Performance;

public sealed partial class RemoveReinsertPage : Page
{
    private readonly MapControl _map = PerformanceMapFactory.Create(WinUIEx.Maps.MapStyle.Road);

    public RemoveReinsertPage()
    {
        InitializeComponent();
        AutomationProperties.SetAutomationId(_map, "MapSurface");
        MapHost.Children.Add(_map);
        MapToggle.IsChecked = true;
        UpdateStatus(true);
    }

    private void MapToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (!MapHost.Children.Contains(_map))
        {
            MapHost.Children.Add(_map);
        }
        MapToggle.Content = "Map inserted";
        UpdateStatus(true);
    }

    private void MapToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        MapHost.Children.Remove(_map);
        MapToggle.Content = "Map removed";
        UpdateStatus(false);
    }

    private void UpdateStatus(bool inserted) =>
        MapStatus.Text = inserted ? "Map is in the visual tree" : "Map is unloaded";
}
