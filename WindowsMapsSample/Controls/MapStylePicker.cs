using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using WinUIEx.Maps;

namespace WindowsMapsSample.Controls;

[TemplatePart(Name = "PART_Road", Type = typeof(RadioButton))]
[TemplatePart(Name = "PART_Aerial", Type = typeof(RadioButton))]
[TemplatePart(Name = "PART_Terrain", Type = typeof(RadioButton))]
[TemplatePart(Name = "PART_SelectedPreview", Type = typeof(Image))]
[TemplatePart(Name = "PART_SelectedLabel", Type = typeof(TextBlock))]
[TemplatePart(Name = "PART_Ink", Type = typeof(ToggleSwitch))]
public sealed partial class MapStylePicker : Control
{
    private RadioButton? _road, _aerial, _terrain;
    private Image? _preview;
    private TextBlock? _label;
    private ToggleSwitch? _ink;
    private bool _updating;

    public MapStylePicker()
    {
        DefaultStyleKey = typeof(MapStylePicker);
        ActualThemeChanged += (_, _) => UpdateSelection();
    }

    public static readonly DependencyProperty SelectedStyleProperty = DependencyProperty.Register(
        nameof(SelectedStyle), typeof(MapStyle), typeof(MapStylePicker),
        new PropertyMetadata(MapStyle.Road, (sender, _) => ((MapStylePicker)sender).UpdateSelection()));

    public MapStyle SelectedStyle
    {
        get => (MapStyle)GetValue(SelectedStyleProperty);
        set
        {
            // Boxed enum DPs can notify even when equal; terminate two-way binding feedback.
            if (SelectedStyle != value) SetValue(SelectedStyleProperty, value);
        }
    }

    public static readonly DependencyProperty IsInkEnabledProperty = DependencyProperty.Register(
        nameof(IsInkEnabled), typeof(bool), typeof(MapStylePicker),
        new PropertyMetadata(false, (sender, _) => ((MapStylePicker)sender).UpdateSelection()));

    public bool IsInkEnabled
    {
        get => (bool)GetValue(IsInkEnabledProperty);
        set => SetValue(IsInkEnabledProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        if (_road is not null) _road.Checked -= Road_Checked;
        if (_aerial is not null) _aerial.Checked -= Aerial_Checked;
        if (_terrain is not null) _terrain.Checked -= Terrain_Checked;
        if (_ink is not null) _ink.Toggled -= Ink_Toggled;
        base.OnApplyTemplate();
        _road = GetTemplateChild("PART_Road") as RadioButton;
        _aerial = GetTemplateChild("PART_Aerial") as RadioButton;
        _terrain = GetTemplateChild("PART_Terrain") as RadioButton;
        _preview = GetTemplateChild("PART_SelectedPreview") as Image;
        _label = GetTemplateChild("PART_SelectedLabel") as TextBlock;
        _ink = GetTemplateChild("PART_Ink") as ToggleSwitch;
        UpdateSelection();
        if (_road is not null) _road.Checked += Road_Checked;
        if (_aerial is not null) _aerial.Checked += Aerial_Checked;
        if (_terrain is not null) _terrain.Checked += Terrain_Checked;
        if (_ink is not null) _ink.Toggled += Ink_Toggled;
    }

    private void UpdateSelection()
    {
        _updating = true;
        if (_road is not null) _road.IsChecked = SelectedStyle is MapStyle.Road or MapStyle.Night;
        if (_aerial is not null) _aerial.IsChecked = SelectedStyle is MapStyle.Satellite or MapStyle.SatelliteWithRoads;
        if (_terrain is not null) _terrain.IsChecked = SelectedStyle == MapStyle.RoadShadedRelief;
        if (_ink is not null) _ink.IsOn = IsInkEnabled;
        if (_label is not null) _label.Text = SelectedStyle switch
        {
            MapStyle.Satellite or MapStyle.SatelliteWithRoads => "Aerial",
            MapStyle.RoadShadedRelief => "Terrain",
            _ => "Road",
        };
        if (_preview is not null)
        {
            string asset = SelectedStyle switch
            {
                MapStyle.Satellite or MapStyle.SatelliteWithRoads => "Aerial",
                MapStyle.RoadShadedRelief => "Terrain",
                _ => ActualTheme == ElementTheme.Dark ? "RoadDark" : "RoadLight",
            };
            _preview.Source = new SvgImageSource(new Uri($"ms-appx:///Assets/MapViews/{asset}.svg"));
        }
        _updating = false;
    }

    private void Road_Checked(object sender, RoutedEventArgs e) { if (!_updating) SelectedStyle = MapStyle.Road; }
    private void Aerial_Checked(object sender, RoutedEventArgs e) { if (!_updating) SelectedStyle = MapStyle.SatelliteWithRoads; }
    private void Terrain_Checked(object sender, RoutedEventArgs e) { if (!_updating) SelectedStyle = MapStyle.RoadShadedRelief; }
    private void Ink_Toggled(object sender, RoutedEventArgs e) { if (!_updating) IsInkEnabled = _ink!.IsOn; }
}
