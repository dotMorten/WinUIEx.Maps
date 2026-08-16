using MapSample.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Geolocation;
using WinUIEx.Maps;

namespace MapSample.Samples.Maps;

public sealed partial class BasemapPage : Page
{
    public BasemapPage()
    {
        InitializeComponent();
        StylePicker.ItemsSource = new[]
        {
            MapStyle.Road,
            MapStyle.GrayscaleDark,
            MapStyle.RoadShadedRelief,
            MapStyle.BlankAccessible,
            MapStyle.GrayscaleLight,
            MapStyle.Night,
            MapStyle.HighContrastDark,
            MapStyle.HighContrastLight,
            MapStyle.SatelliteWithRoads,
            MapStyle.RoadRaster,
            MapStyle.GrayscaleDarkRaster,
            MapStyle.Satellite,
            MapStyle.RoadShadedReliefRaster,
            MapStyle.Blank,
        };
        LanguagePicker.ItemsSource = new[]
        {
            new LanguagePickerItem("Azure default", null),
            new LanguagePickerItem("English (United States)", "en-US"),
            new LanguagePickerItem("French", "fr"),
            new LanguagePickerItem("German", "de-DE"),
            new LanguagePickerItem("Spanish", "es"),
            new LanguagePickerItem("Japanese", "ja"),
            new LanguagePickerItem("Arabic", "ar-SA"),
            new LanguagePickerItem("Chinese (Simplified)", "zh-Hans"),
        };
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
        LanguagePicker.SelectedIndex = 0;
    }

    private void StylePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StylePicker.SelectedItem is not MapStyle style)
        {
            return;
        }
        Map.MapStyle = style;
    }

    private void LanguagePicker_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (LanguagePicker.SelectedItem is not LanguagePickerItem selection)
        {
            return;
        }

        if (selection.Language is null)
        {
            Map.ClearValue(FrameworkElement.LanguageProperty);
        }
        else
        {
            Map.Language = selection.Language;
        }
    }

    private void GoToHome_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (Microsoft.UI.Xaml.Application.Current is App app)
        {
            app.MainWindow?.NavigateHome();
        }
    }

    private sealed record LanguagePickerItem(
        string Name,
        string? Language);
}
