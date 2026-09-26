using System;
using MapSample.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Devices.Geolocation;
using WinUIEx.Maps;

namespace MapSample.Samples.Maps;

public sealed partial class AzureOverlaysPage : Page
{
    private readonly AzureTrafficLayer _traffic = new() { ShowIncidents = true };
    private readonly AzureWeatherLayer _weather = new() { Opacity = 0.65 };

    public AzureOverlaysPage()
    {
        InitializeComponent();
        Map.MapServiceToken = MapServiceTokenStore.Current;
        Map.Center = new Geopoint(new BasicGeoposition { Longitude = -122.33, Latitude = 47.61 });
        Map.ZoomLevel = 10;
        Map.Layers.Add(_weather);
        Map.Layers.Add(_traffic);
        _traffic.IncidentTapped += (_, args) =>
        {
            Status.Text = $"{args.Description ?? args.IncidentType ?? "Traffic incident"}; " +
                $"ID {args.Id ?? "(not supplied)"}; category {args.Category}; magnitude {args.Magnitude}" +
                (args.Delay is { } delay ? $"; delay {delay.TotalMinutes:0.#} min" : "");
            args.Handled = true;
        };
        FlowPicker.ItemsSource = Enum.GetValues<TrafficFlowStyle>();
        FlowPicker.SelectedItem = TrafficFlowStyle.RelativeDark;
        WeatherPicker.ItemsSource = Enum.GetValues<WeatherLayerKind>();
        WeatherPicker.SelectedItem = WeatherLayerKind.Radar;
        TimePicker.ItemsSource = new[] { "Latest", "15 minutes ago", "30 minutes ago" };
        TimePicker.SelectedIndex = 0;
    }

    private void OptionsChanged(object sender, SelectionChangedEventArgs args)
    {
        if (FlowPicker?.SelectedItem is TrafficFlowStyle flow)
            _traffic.FlowStyle = flow;
        if (WeatherPicker?.SelectedItem is WeatherLayerKind kind)
            _weather.Kind = kind;
        if (TimePicker?.SelectedIndex is int index && index >= 0)
            _weather.Timestamp = index == 0 ? null : DateTimeOffset.UtcNow.AddMinutes(-15 * index);
    }

    private void ToggleChanged(object sender, RoutedEventArgs args) =>
        _traffic.ShowIncidents = ((CheckBox)sender).IsChecked == true;

    private void IncidentZoomChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsFinite(args.NewValue))
            _traffic.MinIncidentZoom = args.NewValue;
        else
            sender.Value = _traffic.MinIncidentZoom;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        bool weather = e.Parameter is "azure-weather";
        Map.ZoomLevel = weather ? 10 : 12;
        _weather.IsVisible = weather;
        _traffic.IsVisible = !weather;
        TrafficOptions.Visibility = weather ? Visibility.Collapsed : Visibility.Visible;
        WeatherOptions.Visibility = weather ? Visibility.Visible : Visibility.Collapsed;
        Heading.Text = weather ? "Azure weather" : "Azure traffic";
        Description.Text = weather
            ? "Radar and infrared overlays using the token configured on Home."
            : "Compare traffic flow styles and tap an incident. Uses the token configured on Home.";
        Status.Text = weather
            ? "Latest imagery refreshes automatically. Fixed frames do not advance."
            : "Traffic refreshes every minute. Incidents appear at or above the selected minimum zoom.";
    }
}
