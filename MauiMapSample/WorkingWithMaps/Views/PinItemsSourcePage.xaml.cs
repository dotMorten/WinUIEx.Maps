using WorkingWithMaps.ViewModels;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;

namespace WorkingWithMaps.Views;

public partial class PinItemsSourcePage : ContentPage
{
    public PinItemsSourcePage()
    {
        InitializeComponent();
        BindingContext = new PinItemsSourcePageViewModel();
        map.MoveToRegion(MapSpan.FromCenterAndRadius(new Location(39.8283459, -98.5794797), Distance.FromMiles(1500)));
    }

    void OnMapClicked(object? sender, MapClickedEventArgs e)
    {
        statusLabel.Text = $"Map clicked: {e.Location.Latitude:F5}, {e.Location.Longitude:F5}";
    }

    void OnClustersToggled(object? sender, ToggledEventArgs e) => map.IsClusteringEnabled = e.Value;
}
