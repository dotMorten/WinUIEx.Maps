using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Geolocation;

namespace MapSample.Samples.Maps;

public sealed partial class CustomVectorTilesPage : Page
{
    private ArcGISTileLayer? _layer;
    private bool _started;

    public CustomVectorTilesPage()
    {
        InitializeComponent();
        Map.Center = new Geopoint(new BasicGeoposition
        {
            Longitude = -122.33,
            Latitude = 47.61,
        });
        Map.ZoomLevel = 10;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started)
        {
            return;
        }
        _started = true;

        try
        {
            _layer = await ArcGISTileLayer.CreateAsync(
                ArcGISTileLayer.DefaultServiceUrl);
            Map.Layers.Add(_layer);
            StylePicker.ItemsSource = new[]
            {
                new StyleItem("Default", _layer.StyleUrl!),
                new StyleItem("Night", ArcGISTileLayer.NightStyleUrl),
                new StyleItem(
                    "Modern Antique",
                    ArcGISTileLayer.ModernAntiqueStyleUrl),
            };
            StylePicker.SelectedIndex = 0;
            StylePicker.IsEnabled = true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                HttpRequestException or
                InvalidDataException or
                JsonException)
        {
            LoadError.Message = exception.Message;
            LoadError.IsOpen = true;
        }
    }

    private void StylePicker_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_layer is not null &&
            StylePicker.SelectedItem is StyleItem style)
        {
            _layer.StyleUrl = style.Url;
        }
    }

    private sealed record StyleItem(string Name, string Url);
}
