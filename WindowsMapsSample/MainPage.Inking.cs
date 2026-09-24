using Microsoft.UI.Xaml;
using Windows.Devices.Geolocation;
using Windows.UI.Core;
using WinUIEx.Maps;

namespace WindowsMapsSample;

public sealed partial class MainPage
{
    private readonly MapElementsLayer _inkLayer = new();

    private void InitializeInking()
    {
        Map.Layers.Add(_inkLayer);
        MapInk.InkPresenter.InputDeviceTypes = CoreInputDeviceTypes.Pen | CoreInputDeviceTypes.Mouse | CoreInputDeviceTypes.Touch;
        StylePicker.RegisterPropertyChangedCallback(Controls.MapStylePicker.IsInkEnabledProperty, (_, _) => UpdateInking());
    }

    private void UpdateInking()
    {
        bool enabled = StylePicker.IsInkEnabled;
        if (!enabled) CommitInk();
        MapInk.Visibility = InkTools.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        MapInk.InkPresenter.IsInputEnabled = enabled;
        if (enabled)
            MapInk.InkPresenter.InputDeviceTypes = CoreInputDeviceTypes.Pen | CoreInputDeviceTypes.Mouse | CoreInputDeviceTypes.Touch;
        Map.IsHitTestVisible = Navigation.IsEnabled = !enabled;
        if (enabled)
        {
            Pane.Visibility = Visibility.Collapsed;
            _selectionRequest.Cancel();
        }
    }

    private void CommitInk()
    {
        bool outsideMap = false;
        foreach (var stroke in MapInk.InkPresenter.StrokeContainer.GetStrokes())
        {
            var positions = new List<BasicGeoposition>();
            foreach (var point in stroke.GetInkPoints())
            {
                if (Map.TryGetLocationFromOffset(point.Position, out var location))
                    positions.Add(location.Position);
                else
                    outsideMap = true;
            }
            if (positions.Count < 2) continue;
            var color = stroke.DrawingAttributes.Color;
            if (stroke.DrawingAttributes.DrawAsHighlighter) color.A = 100;
            _inkLayer.MapElements.Add(new MapPolyline
            {
                Path = new Geopath(positions),
                StrokeColor = color,
                StrokeThickness = stroke.DrawingAttributes.Size.Width,
            });
        }
        MapInk.InkPresenter.StrokeContainer.Clear();
        if (outsideMap) ShowStatus("Ink outside the map could not be kept. Draw within the visible map.");
    }

    private void InkNavigation_Click(object sender, RoutedEventArgs e) =>
        StylePicker.IsInkEnabled = !StylePicker.IsInkEnabled;

    private void FinishInk_Click(object sender, RoutedEventArgs e) => StylePicker.IsInkEnabled = false;

    private void ClearInk_Click(object sender, RoutedEventArgs e)
    {
        MapInk.InkPresenter.StrokeContainer.Clear();
        _inkLayer.MapElements.Clear();
    }
}
