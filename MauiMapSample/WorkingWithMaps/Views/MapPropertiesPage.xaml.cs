using Microsoft.Maui.Controls.Maps;

namespace WorkingWithMaps.Views;

public partial class MapPropertiesPage : ContentPage
{
    public MapPropertiesPage()
    {
        InitializeComponent();

        scrollEnabledCheckBox.CheckedChanged += OnCheckBoxCheckedChanged;
        zoomEnabledCheckBox.CheckedChanged += OnCheckBoxCheckedChanged;
    }

    void OnMapClicked(object? sender, MapClickedEventArgs e)
    {
        statusLabel.Text = $"Map clicked: {e.Location.Latitude:F5}, {e.Location.Longitude:F5}";
    }

    void OnCheckBoxCheckedChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (ReferenceEquals(sender, scrollEnabledCheckBox)) map.IsScrollEnabled = e.Value;
        else if (ReferenceEquals(sender, zoomEnabledCheckBox)) map.IsZoomEnabled = e.Value;
        else if (ReferenceEquals(sender, showUserCheckBox)) map.IsShowingUser = e.Value;
        else if (ReferenceEquals(sender, showTrafficCheckBox)) map.IsTrafficEnabled = e.Value;
    }
}
