using System.ComponentModel;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;

namespace MauiMapSample;

public partial class MainPage : ContentPage
{
    private readonly Location _seattle = new(47.6062, -122.3321);
    private Pin? _firstPin;
    private int _nextPin;

    public MainPage()
    {
        InitializeComponent();
        MapTypePicker.SelectedIndex = 0;
        AddPin(new Location(47.6205, -122.3493), "Space Needle");
        AddPin(new Location(47.6097, -122.3425), "Pike Place Market");
        AddPin(new Location(47.6100, -122.3420), "Near the market");
        AddPin(new Location(47.6103, -122.3422), "Another nearby pin");
        AddShapes();
        SampleMap.MoveToRegion(MapSpan.FromCenterAndRadius(_seattle, Distance.FromKilometers(3)), false);
    }

    private void AddPin(Location location, string label)
    {
        var pin = new Pin
        {
            Location = location, Label = label, Address = "Seattle, WA",
            ImageSource = new FontImageSource
            {
                Glyph = "\u2605", FontFamily = "OpenSansRegular", Color = Colors.Crimson, Size = 32,
            },
        };
        pin.MarkerClicked += (_, args) => StatusLabel.Text = $"Marker clicked: {pin.Label}";
        pin.InfoWindowClicked += (_, args) =>
        {
            StatusLabel.Text = $"Info window clicked: {pin.Label}";
            args.HideInfoWindow = true;
        };
        SampleMap.Pins.Add(pin);
        _firstPin ??= pin;
    }

    private void AddShapes()
    {
        var line = new Polyline { StrokeColor = Colors.RoyalBlue, StrokeWidth = 5, ZIndex = 2 };
        line.Geopath.Add(new Location(47.598, -122.350));
        line.Geopath.Add(new Location(47.610, -122.330));
        line.Geopath.Add(new Location(47.622, -122.310));
        line.PolylineClicked += (_, _) => StatusLabel.Text = "Polyline clicked";
        SampleMap.MapElements.Add(line);

        var polygon = new Polygon
        {
            FillColor = Colors.Orange.WithAlpha(0.3f), StrokeColor = Colors.DarkOrange, StrokeWidth = 3,
        };
        polygon.Geopath.Add(new Location(47.595, -122.340));
        polygon.Geopath.Add(new Location(47.600, -122.325));
        polygon.Geopath.Add(new Location(47.590, -122.320));
        polygon.PolygonClicked += (_, _) => StatusLabel.Text = "Polygon clicked";
        SampleMap.MapElements.Add(polygon);

        var circle = new Circle
        {
            Center = new Location(47.620, -122.330), Radius = Distance.FromMeters(400),
            FillColor = Colors.Purple.WithAlpha(0.25f), StrokeColor = Colors.Purple, StrokeWidth = 2, ZIndex = 1,
        };
        circle.CircleClicked += (_, _) => StatusLabel.Text = "Circle clicked";
        SampleMap.MapElements.Add(circle);
    }

    private void OnMapTypeChanged(object? sender, EventArgs args)
    {
        if (SampleMap is not null && MapTypePicker.SelectedIndex >= 0)
            SampleMap.MapType = (MapType)MapTypePicker.SelectedIndex;
    }

    private void OnPanToggled(object? sender, ToggledEventArgs args)
    {
        if (SampleMap is not null) SampleMap.IsScrollEnabled = args.Value;
    }

    private void OnZoomToggled(object? sender, ToggledEventArgs args)
    {
        if (SampleMap is not null) SampleMap.IsZoomEnabled = args.Value;
    }

    private void OnTrafficToggled(object? sender, ToggledEventArgs args) => SampleMap.IsTrafficEnabled = args.Value;
    private void OnClustersToggled(object? sender, ToggledEventArgs args) => SampleMap.IsClusteringEnabled = args.Value;
    private void OnClusterImagesToggled(object? sender, ToggledEventArgs args) =>
        SampleMap.ClusterImageProvider = args.Value ? cluster => new FontImageSource
        {
            Glyph = cluster.Count.ToString(System.Globalization.CultureInfo.CurrentCulture),
            FontFamily = "OpenSansSemibold", Size = 24, Color = Colors.DarkOrange,
        } : null;
    private void OnLocationToggled(object? sender, ToggledEventArgs args) => SampleMap.IsShowingUser = args.Value;
    private void OnResetRegion(object? sender, EventArgs args) =>
        SampleMap.MoveToRegion(MapSpan.FromCenterAndRadius(_seattle, Distance.FromKilometers(3)));
    private void OnAddPin(object? sender, EventArgs args)
    {
        _nextPin++;
        AddPin(new Location(47.6062 + _nextPin * 0.0005, -122.3321), $"Added pin {_nextPin}");
    }

    private void OnMovePin(object? sender, EventArgs args)
    {
        if (_firstPin is not null)
            _firstPin.Location = new Location(_firstPin.Location.Latitude + 0.001, _firstPin.Location.Longitude);
    }

    private void OnShowCallout(object? sender, EventArgs args) => _firstPin?.ShowInfoWindow();
    private void OnHideCallout(object? sender, EventArgs args) => _firstPin?.HideInfoWindow();
    private void OnToggleShapes(object? sender, EventArgs args)
    {
        foreach (var shape in SampleMap.MapElements) shape.IsVisible = !shape.IsVisible;
    }

    private void OnMapClicked(object? sender, MapClickedEventArgs args) =>
        StatusLabel.Text = $"Map clicked: {args.Location.Latitude:F5}, {args.Location.Longitude:F5}";
    private void OnMapLongClicked(object? sender, MapClickedEventArgs args) =>
        StatusLabel.Text = $"Long press: {args.Location.Latitude:F5}, {args.Location.Longitude:F5}";
    private void OnClusterClicked(object? sender, ClusterClickedEventArgs args) =>
        StatusLabel.Text = $"Cluster clicked: {args.Pins.Count} pins";
    private void OnUserLocationChanged(object? sender, UserLocationChangedEventArgs args) =>
        StatusLabel.Text = $"User location updated: {args.Location.Latitude:F5}, {args.Location.Longitude:F5}";
    private void OnMapPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SampleMap.VisibleRegion) && SampleMap.VisibleRegion is { } region)
            RegionLabel.Text = $"Visible region: {region.Center.Latitude:F4}, {region.Center.Longitude:F4}; " +
                $"{region.LatitudeDegrees:F4} x {region.LongitudeDegrees:F4} degrees";
    }
}
