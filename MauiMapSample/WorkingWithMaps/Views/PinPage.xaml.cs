using Microsoft.Maui.Controls.Maps;

namespace WorkingWithMaps.Views;

public partial class PinPage : ContentPage
{
    public PinPage()
    {
        InitializeComponent();
    }

    void OnButtonClicked(object? sender, EventArgs e)
    {
        Pin boardwalkPin = new Pin
        {
            Location = new Location(36.9641949, -122.0177232),
            Label = "Boardwalk",
            Address = "Santa Cruz",
            Type = PinType.Place
        };
        boardwalkPin.MarkerClicked += OnMarkerClickedAsync;
        
        Pin wharfPin = new Pin
        {
            Location = new Location(36.9571571, -122.0173544),
            Label = "Wharf",
            Address = "Santa Cruz",
            Type = PinType.Place
        };
        wharfPin.InfoWindowClicked += OnInfoWindowClickedAsync;

        map.Pins.Add(boardwalkPin);
        map.Pins.Add(wharfPin);
    }

    async void OnMarkerClickedAsync(object? sender, PinClickedEventArgs e)
    {
        e.HideInfoWindow = true;
        if (sender is not Pin pin) throw new ArgumentException("Expected a map pin.", nameof(sender));
        await DisplayAlertAsync("Pin Clicked", $"{pin.Label} was clicked.", "Ok");
    }

    async void OnInfoWindowClickedAsync(object? sender, PinClickedEventArgs e)
    {
        if (sender is not Pin pin) throw new ArgumentException("Expected a map pin.", nameof(sender));
        await DisplayAlertAsync("Info Window Clicked", $"The info window was clicked for {pin.Label}.", "Ok");
    }
}
