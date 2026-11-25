using Microsoft.Maui.Maps;

namespace WorkingWithMaps.Views;

public partial class MapTypesPage : ContentPage
{
    public MapTypesPage()
    {
        InitializeComponent();
    }

    void OnSliderValueChanged(object? sender, ValueChangedEventArgs e)
    {
        double zoomLevel = e.NewValue;
        double latlongDegrees = 360 / (Math.Pow(2, zoomLevel));
        if (map.VisibleRegion != null)
        {
            map.MoveToRegion(new MapSpan(map.VisibleRegion.Center, latlongDegrees, latlongDegrees));
        }
    }

    void OnButtonClicked(object? sender, EventArgs e)
    {
        if (sender is not Button button) throw new ArgumentException("Expected a map-type button.", nameof(sender));
        switch (button.Text)
        {
            case "Street":
                map.MapType = MapType.Street;
                break;
            case "Satellite":
                map.MapType = MapType.Satellite;
                break;
            case "Hybrid":
                map.MapType = MapType.Hybrid;
                break;
        }
    }
}
