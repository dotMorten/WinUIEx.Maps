using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Devices.Sensors;

namespace WorkingWithMaps.Views;

public partial class GeocoderPage : ContentPage
{
    public GeocoderPage()
    {
        InitializeComponent();
    }

    async void OnGeocodeButtonClicked(object? sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(geocodeEntry.Text))
        {
            string address = geocodeEntry.Text;
            try
            {
                IEnumerable<Location> approximateLocations = await Geocoding.Default.GetLocationsAsync(address);
                Location? location = approximateLocations.FirstOrDefault();
                geocodedOutputLabel.Text = location is null ? "No matching location found."
                    : $"{location.Latitude}, {location.Longitude}";
            }
            catch (NotSupportedException)
            {
                geocodedOutputLabel.Text = "MAUI Geocoding is not supported on this platform. It is separate from the WinUIEx.Maps handler.";
            }
            catch (ArgumentNullException exception) when (exception.ParamName == "MapServiceToken")
            {
                geocodedOutputLabel.Text = "MAUI's Windows geocoder requires a legacy Bing Maps token. The Azure Maps key configures map rendering only.";
            }
            catch (HttpRequestException)
            {
                geocodedOutputLabel.Text = "The geocoding service could not be reached.";
            }
        }
        else geocodedOutputLabel.Text = "Enter an address to geocode.";
    }

    async void OnReverseGeocodeButtonClicked(object? sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(reverseGeocodeEntry.Text))
        {
            string[] coordinates = reverseGeocodeEntry.Text.Split(',');
            if (coordinates.Length != 2 ||
                !double.TryParse(coordinates[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double latitude) ||
                !double.TryParse(coordinates[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double longitude) ||
                !double.IsFinite(latitude) || !double.IsFinite(longitude) ||
                latitude is < -90 or > 90 || longitude is < -180 or > 180)
            {
                reverseGeocodedOutputLabel.Text = "Enter latitude (-90 to 90), longitude (-180 to 180), using decimal points.";
                return;
            }
            try
            {
                Location location = new Location(latitude, longitude);
                IEnumerable<Placemark> possibleAddresses = await Geocoding.Default.GetPlacemarksAsync(location);
                Placemark? placemark = possibleAddresses.FirstOrDefault();
                if (placemark != null)
                    reverseGeocodedOutputLabel.Text = $"Thoroughfare: {placemark.Thoroughfare}, Locality: {placemark.Locality}, ZipCode: {placemark.PostalCode}";
                else
                    reverseGeocodedOutputLabel.Text = "No matching address found.";
            }
            catch (NotSupportedException)
            {
                reverseGeocodedOutputLabel.Text = "MAUI Geocoding is not supported on this platform. It is separate from the WinUIEx.Maps handler.";
            }
            catch (ArgumentNullException exception) when (exception.ParamName == "MapServiceToken")
            {
                reverseGeocodedOutputLabel.Text = "MAUI's Windows geocoder requires a legacy Bing Maps token. The Azure Maps key configures map rendering only.";
            }
            catch (HttpRequestException)
            {
                reverseGeocodedOutputLabel.Text = "The geocoding service could not be reached.";
            }
        }
        else reverseGeocodedOutputLabel.Text = "Enter latitude, longitude to reverse geocode.";
    }
}
