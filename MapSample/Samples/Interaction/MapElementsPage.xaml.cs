using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;
using WinUIEx.Maps;
using MapElement = WinUIEx.Maps.MapElement;
using MapIcon = WinUIEx.Maps.MapIcon;
using MapElementsLayer = WinUIEx.Maps.MapElementsLayer;
using MapPolygon = WinUIEx.Maps.MapPolygon;
using MapPolyline = WinUIEx.Maps.MapPolyline;

namespace MapSample.Samples.Interaction;

public sealed partial class MapElementsPage : Page
{
    private readonly MapElementsLayer _elementsLayer = new();
    private readonly FontIcon _pinIcon =
        CreateIcon("\uE707", 28, Microsoft.UI.Colors.Red);
    private readonly FontIcon _selectedPinIcon =
        CreateIcon("\uE707", 40, Microsoft.UI.Colors.Cyan);
    private readonly FontIcon _locationIcon =
        CreateIcon("\uE81D", 20, Microsoft.UI.Colors.Red);
    private readonly FontIcon _selectedLocationIcon =
        CreateIcon("\uE81D", 28, Microsoft.UI.Colors.Cyan);
    private readonly Dictionary<IconElement, IconElement> _selectedIcons =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IconElement, IconElement> _normalIcons =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MapElement, Windows.UI.Color> _normalStrokeColors =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MapElement, int> _normalZIndexes =
        new(ReferenceEqualityComparer.Instance);
    private uint? _elementPressPointerId;

    public MapElementsPage()
    {
        InitializeComponent();
        AddIconPair(_pinIcon, _selectedPinIcon);
        AddIconPair(_locationIcon, _selectedLocationIcon);
        _elementsLayer.PointerEntered += ElementsLayer_PointerEntered;
        _elementsLayer.PointerExited += ElementsLayer_PointerExited;
        _elementsLayer.PointerPressed += (_, e) =>
            _elementPressPointerId = e.Pointer.PointerId;
        _elementsLayer.RightTapped += ElementsLayer_RightTapped;
        Map.Tapped += Map_Tapped;
        Map.Center = CreateLocation(-122.33, 47.61);
        Map.ZoomLevel = 11;
        Map.Layers.Add(new TileLayer(
            new TileLayerOptions
            {
                TileUrl = "https://tile.openstreetmap.org/[level]/[column]/[row].png",
                TileSize = 256,
                MaxSourceZoom = 19,
            },
            "openstreetmap-icons-sample")
        {
            Attribution = "© OpenStreetMap contributors",
            AttributionLink = new Uri("https://www.openstreetmap.org/copyright"),
        });

        Map.Layers.Add(_elementsLayer);
        _elementsLayer.MapElements.Add(new MapIcon(
            _pinIcon,
            CreateLocation(-122.3352, 47.6080)));
        _elementsLayer.MapElements.Add(new MapIcon(
            _pinIcon,
            CreateLocation(-122.3210, 47.6150)));
        _elementsLayer.MapElements.Add(new MapPolyline
        {
            Path = CreatePath(
                (-122.3480, 47.6200),
                (-122.3370, 47.6150),
                (-122.3260, 47.6180),
                (-122.3140, 47.6110)),
            StrokeColor = Microsoft.UI.Colors.OrangeRed,
            StrokeThickness = 5,
        });

        MapPolygon polygon = new()
        {
            FillColor = Windows.UI.Color.FromArgb(72, 138, 43, 226),
            StrokeColor = Microsoft.UI.Colors.MediumPurple,
            StrokeThickness = 4,
        };
        polygon.Paths.Add(CreatePath(
            (-122.3490, 47.6040),
            (-122.3390, 47.5980),
            (-122.3250, 47.6000),
            (-122.3220, 47.6090),
            (-122.3380, 47.6120)));
        polygon.Paths.Add(CreatePath(
            (-122.3380, 47.6030),
            (-122.3320, 47.6020),
            (-122.3300, 47.6060),
            (-122.3360, 47.6070)));
        _elementsLayer.MapElements.Add(polygon);
    }

    private static FontIcon CreateIcon(
        string glyph,
        double fontSize,
        Windows.UI.Color color) =>
        new()
        {
            Glyph = glyph,
            FontSize = fontSize,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(color),
        };

    private void AddIconPair(IconElement normal, IconElement selected)
    {
        _selectedIcons.Add(normal, selected);
        _normalIcons.Add(selected, normal);
    }

    private void ElementsLayer_PointerEntered(
        object? sender,
        MapElementPointerEventArgs e)
    {
        _normalZIndexes.TryAdd(e.MapElement, e.MapElement.ZIndex);
        e.MapElement.ZIndex = int.MaxValue;

        if (e.MapElement is MapIcon icon &&
            _selectedIcons.TryGetValue(icon.IconElement, out IconElement? selected))
        {
            icon.IconElement = selected;
        }
        else if (TryGetStrokeColor(e.MapElement, out Windows.UI.Color color))
        {
            _normalStrokeColors.TryAdd(e.MapElement, color);
            SetStrokeColor(e.MapElement, Microsoft.UI.Colors.Cyan);
        }
    }

    private void ElementsLayer_PointerExited(
        object? sender,
        MapElementPointerEventArgs e)
    {
        if (_normalZIndexes.Remove(e.MapElement, out int zIndex))
        {
            e.MapElement.ZIndex = zIndex;
        }

        if (e.MapElement is MapIcon icon &&
            _normalIcons.TryGetValue(icon.IconElement, out IconElement? normal))
        {
            icon.IconElement = normal;
        }
        else if (_normalStrokeColors.Remove(
            e.MapElement,
            out Windows.UI.Color color))
        {
            SetStrokeColor(e.MapElement, color);
        }
    }

    private void Map_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var point = e.GetPosition(Map);
        if (Map.TryGetLocationFromOffset(point, out Geopoint location))
        {
            _elementsLayer.MapElements.Add(new MapIcon(_locationIcon, location));
            e.Handled = true;
        }
    }

    private void ElementsLayer_RightTapped(
        object? sender,
        MapElementRightTappedEventArgs e)
    {
        if (e.MapElement is not MapIcon icon)
        {
            return;
        }

        var deleteItem = new MenuFlyoutItem { Text = "Delete" };
        AutomationProperties.SetAutomationId(deleteItem, "DeleteMapIconMenuItem");
        deleteItem.Click += async (_, _) => await ConfirmDeleteAsync(icon);
        var menu = new MenuFlyout();
        menu.Items.Add(deleteItem);
        menu.ShowAt(
            Map,
            new FlyoutShowOptions { Position = e.GetPosition(Map) });
        e.Handled = true;
    }

    private async Task ConfirmDeleteAsync(MapIcon icon)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete map icon?",
            Content = "The selected icon will be removed from the map.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _elementsLayer.MapElements.Remove(icon);
        }
    }

    private static Geopoint CreateLocation(double longitude, double latitude) =>
        new(new BasicGeoposition
        {
            Longitude = longitude,
            Latitude = latitude,
        });

    private static Geopath CreatePath(
        params (double Longitude, double Latitude)[] positions) =>
        new(positions.Select(position => new BasicGeoposition
        {
            Longitude = position.Longitude,
            Latitude = position.Latitude,
        }));

    private static bool TryGetStrokeColor(
        MapElement element,
        out Windows.UI.Color color)
    {
        switch (element)
        {
            case MapPolygon polygon:
                color = polygon.StrokeColor;
                return true;
            case MapPolyline polyline:
                color = polyline.StrokeColor;
                return true;
            default:
                color = default;
                return false;
        }
    }

    private static void SetStrokeColor(
        MapElement element,
        Windows.UI.Color color)
    {
        switch (element)
        {
            case MapPolygon polygon:
                polygon.StrokeColor = color;
                break;
            case MapPolyline polyline:
                polyline.StrokeColor = color;
                break;
        }
    }
}
