using System.ComponentModel;
using Microsoft.Maui;
using Microsoft.Maui.Maps;
using Windows.Devices.Geolocation;

namespace WinUIEx.Maps.Maui;

public partial class WinUIExMapHandler
{
    private readonly Dictionary<MapElement, IMapElement> _elements = [];
    private readonly HashSet<INotifyPropertyChanged> _observedElements = new(ReferenceEqualityComparer.Instance);

    private void ClearElements()
    {
        foreach (var element in _observedElements) element.PropertyChanged -= OnElementChanged;
        _observedElements.Clear();
        foreach (var pair in _elements)
            if (ReferenceEquals(pair.Value.MapElementId, pair.Key)) pair.Value.MapElementId = null;
        _elements.Clear();
        _shapes.MapElements.Clear();
    }

    private void UpdateElements()
    {
        if (!_connected) return;
        ClearElements();
        foreach (var source in VirtualView.Elements)
        {
            MapElement target;
            if (source is ICircleMapElement circle)
            {
                var points = Enumerable.Range(0, 120).Select(i =>
                {
                    var point = MapMath.CirclePoint(circle.Center.Latitude, circle.Center.Longitude,
                        circle.Radius.Meters, i * 3);
                    return new BasicGeoposition { Latitude = point.Latitude, Longitude = point.Longitude };
                }).ToList();
                target = new MapPolygon { Path = new Geopath(points) };
            }
            else if (source is IGeoPathMapElement path)
            {
                var points = path.Select(p => new BasicGeoposition
                    { Latitude = p.Latitude, Longitude = p.Longitude }).ToList();
                target = source is IFilledMapElement
                    ? new MapPolygon { Path = points.Count >= 3 ? new Geopath(points) : null }
                    : new MapPolyline { Path = points.Count >= 2 ? new Geopath(points) : null };
            }
            else
            {
                throw new NotSupportedException($"Unsupported map element type: {source.GetType().Name}.");
            }
            var stroke = GetColor(source.Stroke);
            if (target is MapPolygon polygon)
            {
                polygon.FillColor = GetColor((source as IFilledMapElement)?.Fill);
                polygon.StrokeColor = stroke;
                polygon.StrokeThickness = source.StrokeThickness;
            }
            else if (target is MapPolyline line)
            {
                line.StrokeColor = stroke;
                line.StrokeThickness = source.StrokeThickness;
            }
            target.IsVisible = source.IsVisible;
            target.ZIndex = source.ZIndex;
            source.MapElementId = target;
            _elements.Add(target, source);
            _shapes.MapElements.Add(target);
            if (source is INotifyPropertyChanged observable && _observedElements.Add(observable))
                observable.PropertyChanged += OnElementChanged;
        }
    }

    private void OnElementChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(IMapElement.MapElementId)) UpdateElements();
    }

    private void OnShapeTapped(object? sender, MapElementTappedEventArgs args)
    {
        if (_elements.TryGetValue(args.MapElement, out var element))
        {
            args.Handled = true;
            element.Clicked();
        }
    }

    private static Windows.UI.Color GetColor(Microsoft.Maui.Graphics.Paint? paint) =>
        paint is Microsoft.Maui.Graphics.SolidPaint { Color: { } color }
            ? Windows.UI.Color.FromArgb(ToByte(color.Alpha), ToByte(color.Red), ToByte(color.Green), ToByte(color.Blue))
            : paint is null ? Microsoft.UI.Colors.Transparent
            : throw new NotSupportedException("Map element fills and strokes must be solid colors.");

    private static byte ToByte(float value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
}
