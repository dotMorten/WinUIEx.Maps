using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui;
using Microsoft.Maui.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using MauiLocation = Microsoft.Maui.Devices.Sensors.Location;

namespace WinUIEx.Maps.Maui;

public partial class WinUIExMapHandler
{
    private readonly Dictionary<MapElement, PinMarker> _markers = [];
    private readonly HashSet<INotifyPropertyChanged> _observedPins = new(ReferenceEqualityComparer.Instance);
    private readonly SemaphoreSlim _imageSlots = new(4);
    private double _clusterZoom = double.NaN;
    private int? _clusterImageVersion;
    private Flyout? _infoWindow;
    private IMapPin? _infoPin;

    private sealed class PinMarker(MapIcon icon, IReadOnlyList<IMapPin> pins)
    {
        internal MapIcon Icon { get; } = icon;
        internal IReadOnlyList<IMapPin> Pins { get; } = pins;
        internal IImageSource? ImageSource { get; set; }
        internal CancellationTokenSource? ImageRequest { get; set; }
        internal IDisposable? Image { get; set; }
    }

    private void ClearPins()
    {
        foreach (var pin in _observedPins) pin.PropertyChanged -= OnPinChanged;
        _observedPins.Clear();
        foreach (var marker in _markers.Values) ReleaseMarker(marker);
        _markers.Clear();
        _pins.MapElements.Clear();
        _clusterZoom = double.NaN;
        _clusterImageVersion = null;
    }

    private static void ReleaseMarker(PinMarker marker)
    {
        marker.ImageRequest?.Cancel();
        marker.ImageRequest?.Dispose();
        marker.ImageRequest = null;
        marker.Image?.Dispose();
        marker.Image = null;
        foreach (var pin in marker.Pins)
            if (ReferenceEquals(pin.MarkerId, marker.Icon)) pin.MarkerId = null;
    }

    private void UpdatePins(IMapPin? changedPin = null, bool refreshImage = false)
    {
        if (!_connected) return;
        HideInfoWindow();
        int? imageVersion = (VirtualView as IMapClusterImageProvider)?.ClusterImageVersion;
        bool clusterImageChanged = imageVersion != _clusterImageVersion;
        var pins = VirtualView.Pins.ToList();
        var observed = new HashSet<INotifyPropertyChanged>(
            pins.OfType<INotifyPropertyChanged>(), ReferenceEqualityComparer.Instance);
        foreach (var pin in _observedPins.Where(pin => !observed.Contains(pin)).ToList())
        {
            pin.PropertyChanged -= OnPinChanged;
            _observedPins.Remove(pin);
        }
        foreach (var pin in observed)
            if (_observedPins.Add(pin)) pin.PropertyChanged += OnPinChanged;
        var groups = VirtualView.IsClusteringEnabled
            ? MapMath.Cluster(pins, p => (p.Location.Latitude, p.Location.Longitude, p.ClusteringIdentifier),
                PlatformView.ZoomLevel)
            : pins.Select(p => new List<IMapPin> { p }).ToList();
        var markers = MapMath.ReconcileClusters<IMapPin, PinMarker>(
            _markers.Values.Select(marker => (marker.Pins, marker)), groups, CreateMarker);
        var retained = markers.ToHashSet();
        var removed = _markers.Values.Where(marker => !retained.Contains(marker)).ToList();
        foreach (var marker in removed)
        {
            _markers.Remove(marker.Icon);
            ReleaseMarker(marker);
        }
        if (removed.Count != 0)
            for (int end = _pins.MapElements.Count - 1; end >= 0; end--)
            {
                if (_markers.ContainsKey(_pins.MapElements[end])) continue;
                int start = end;
                while (start > 0 && !_markers.ContainsKey(_pins.MapElements[start - 1])) start--;
                _pins.MapElements.RemoveRange(start, end - start + 1);
                end = start;
            }
        for (int i = 0; i < markers.Count; i++)
        {
            var marker = markers[i];
            var group = marker.Pins;
            bool added = _markers.TryAdd(marker.Icon, marker);
            var center = MapMath.Centroid(group.Select(p => (p.Location.Latitude, p.Location.Longitude)));
            var location = new MauiLocation(center.Latitude, center.Longitude);
            var position = marker.Icon.Location.Position;
            if (position.Latitude != center.Latitude || position.Longitude != center.Longitude)
                marker.Icon.Location = ToGeopoint(location);
            foreach (var pin in group)
                if (!ReferenceEquals(pin.MarkerId, marker.Icon)) pin.MarkerId = marker.Icon;
            if (i >= _pins.MapElements.Count || !ReferenceEquals(_pins.MapElements[i], marker.Icon))
            {
                int previousIndex = _pins.MapElements.IndexOf(marker.Icon);
                if (previousIndex < 0) _pins.MapElements.Insert(i, marker.Icon);
                else _pins.MapElements.Move(previousIndex, i);
            }
            bool changed = changedPin is not null && group.Any(pin => ReferenceEquals(pin, changedPin));
            if (group.Count == 1)
                UpdateMarkerImage(marker, group[0].ImageSource, changed && refreshImage);
            else if (added || changed || refreshImage || clusterImageChanged)
                UpdateMarkerImage(marker,
                    (VirtualView as IMapClusterImageProvider)?.GetClusterImage(group, group.Count, location),
                    changed || refreshImage || clusterImageChanged);
        }
        _clusterZoom = PlatformView.ZoomLevel;
        _clusterImageVersion = imageVersion;
    }

    private static PinMarker CreateMarker(IReadOnlyList<IMapPin> pins)
    {
        var center = MapMath.Centroid(pins.Select(pin => (pin.Location.Latitude, pin.Location.Longitude)));
        var icon = new MapIcon(CreateMarkerVisual(pins.Count),
            ToGeopoint(new MauiLocation(center.Latitude, center.Longitude)))
        {
            NormalizedAnchorPoint = new Point(0.5, pins.Count > 1 ? 0.5 : 1),
        };
        return new PinMarker(icon, pins);
    }

    private static IconElement CreateMarkerVisual(int count)
    {
        if (count > 1) return CreateClusterIcon(count);
        IconElement visual = new SymbolIcon(Symbol.MapPin) { Width = 32, Height = 32 };
        visual.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Crimson);
        return visual;
    }

    private void UpdateMarkerImage(PinMarker marker, IImageSource? source, bool refresh)
    {
        if (ReferenceEquals(marker.ImageSource, source) && (!refresh || source is null)) return;
        marker.ImageRequest?.Cancel();
        marker.ImageRequest?.Dispose();
        marker.ImageRequest = null;
        marker.ImageSource = source;
        if (source is null)
        {
            marker.Icon.IconElement = CreateMarkerVisual(marker.Pins.Count);
            marker.Image?.Dispose();
            marker.Image = null;
        }
        else
        {
            marker.ImageRequest = new CancellationTokenSource();
            LoadMarkerImage(marker, source, marker.ImageRequest.Token);
        }
    }

    private void OnPinChanged(object? sender, PropertyChangedEventArgs args)
    {
        // MarkerId is written by this handler and must not trigger another rebuild.
        if (args.PropertyName is nameof(IMapPin.Location) or nameof(IMapPin.Label) or
            nameof(IMapPin.Address) or nameof(IMapPin.ImageSource) or nameof(IMapPin.ClusteringIdentifier))
            UpdatePins(sender as IMapPin, args.PropertyName == nameof(IMapPin.ImageSource));
    }

    private async void LoadMarkerImage(PinMarker marker, IImageSource source, CancellationToken token)
    {
        bool acquired = false;
        try
        {
            await _imageSlots.WaitAsync(token);
            acquired = true;
            var services = MauiContext!.Services.GetRequiredService<IImageSourceServiceProvider>();
            var service = services.GetRequiredImageSourceService(source);
            var result = await service.GetImageSourceAsync(source,
                (float)(PlatformView.XamlRoot?.RasterizationScale ?? 1), token);
            if (result is null)
            {
                ReportFailure("MarkerImage.Empty", null);
                return;
            }
            if (token.IsCancellationRequested || !_connected)
            {
                result.Dispose();
                return;
            }
            var previous = marker.Image;
            marker.Image = result;
            marker.Icon.IconElement = new ImageIcon { Source = result.Value, Width = 32, Height = 32 };
            previous?.Dispose();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ReportFailure("MarkerImage.Load", exception);
        }
        finally
        {
            if (acquired) _imageSlots.Release();
        }
    }

    private void OnPinTapped(object? sender, MapElementTappedEventArgs args)
    {
        if (!_markers.TryGetValue(args.MapElement, out var marker)) return;
        var pins = marker.Pins;
        args.Handled = true;
        if (pins.Count > 1)
        {
            var center = MapMath.Centroid(pins.Select(p => (p.Location.Latitude, p.Location.Longitude)));
            var location = new MauiLocation(center.Latitude, center.Longitude);
            if (!VirtualView.ClusterClicked(pins, location))
                MoveToRegion(new MoveToRegionRequest(
                    MapSpan.FromLocations(pins.Select(p => p.Location)).WithZoom(0.8), true));
        }
        else if (!pins[0].SendMarkerClick())
        {
            ShowInfoWindow(pins[0], args.GetPosition(PlatformView));
        }
    }

    private void ShowInfoWindow(IMapPin pin, Point? position = null)
    {
        if (!VirtualView.Pins.Any(p => ReferenceEquals(p, pin)) || !PlatformView.IsLoaded) return;
        HideInfoWindow();
        _infoPin = pin;
        var content = new StackPanel { Spacing = 6, MaxWidth = 320 };
        content.Children.Add(new TextBlock
        {
            Text = pin.Label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock { Text = pin.Address, TextWrapping = TextWrapping.Wrap });
        var button = new Button { Content = content };
        button.Click += (_, _) =>
        {
            if (pin.SendInfoWindowClick()) HideInfoWindow();
        };
        _infoWindow = new Flyout { Content = button };
        _infoWindow.ShowAt(PlatformView, new FlyoutShowOptions
        {
            Position = position ?? GetPinOffset(pin),
            ShowMode = FlyoutShowMode.Transient,
        });
    }

    private Point GetPinOffset(IMapPin pin)
    {
        if (PlatformView.TryGetOffsetFromLocation(ToGeopoint(pin.Location), out var offset)) return offset;
        throw new InvalidOperationException("The pin cannot be projected into the current map view.");
    }

    private void HideInfoWindow()
    {
        _infoWindow?.Hide();
        _infoWindow = null;
        _infoPin = null;
    }

    private void ReportFailure(string category, Exception? exception) =>
        MauiContext?.Services.GetService<ILogger<WinUIExMapHandler>>()?.LogWarning(
            "MAUI map adapter failure: {Category}; exception type: {ExceptionType}; HRESULT: {HResult}",
            category, exception?.GetType().Name ?? "None", exception?.HResult ?? 0);
}
