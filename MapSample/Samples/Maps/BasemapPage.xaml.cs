using MapSample.Services;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Linq;
using Windows.Devices.Geolocation;
using WinUIEx.Maps;

namespace MapSample.Samples.Maps;

public sealed partial class BasemapPage : Page
{
    public BasemapPage()
    {
        InitializeComponent();
        StylePicker.ItemsSource = Enum.GetValues<MapStyle>()
            .Where(static style => style != MapStyle.Blank);
        Map.Center = new Geopoint(new BasicGeoposition
        {
            Longitude = -122.33,
            Latitude = 47.61,
        });
        Map.ZoomLevel = 10;
        string token = MapServiceTokenStore.Current;
        Map.MapServiceToken = token;
        GoToHomeButton.Visibility = string.IsNullOrWhiteSpace(token)
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
        StylePicker.SelectedItem = MapStyle.Road;
    }

    private void StylePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StylePicker.SelectedItem is not MapStyle style)
        {
            return;
        }
        Map.MapStyle = style;
    }

    private void GoToHome_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (Microsoft.UI.Xaml.Application.Current is App app)
        {
            app.MainWindow?.NavigateHome();
        }
    }
}
