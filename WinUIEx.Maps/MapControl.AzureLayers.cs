using Microsoft.UI.Dispatching;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Rendering.Diagnostics;

namespace WinUIEx.Maps;

public sealed partial class MapControl
{
    private DispatcherQueueTimer? _azureRefreshTimer;
    private volatile bool _hasAzureZoomLimits;
    private double _azureAttributionZoom;
    private readonly Dictionary<long, AzureSourceAttribution> _azureSourceAttributions = [];

    private sealed class AzureSourceAttribution(object sourceKey, object? refreshIdentity)
    {
        internal object SourceKey { get; } = sourceKey;
        internal object? RefreshIdentity { get; } = refreshIdentity;
        internal long Generation { get; set; }
        internal string Text { get; set; } = string.Empty;
        internal double MinZoom { get; set; }
        internal double MaxZoom { get; set; }
        internal bool IncludesZoom(double zoom) => zoom >= MinZoom && zoom < MaxZoom;
    }

    private void SynchronizeAzureSources(TileLayerSnapshot[] snapshots)
    {
        _azureAttributionZoom = TryGetDisplayedCamera(out _, out double zoom, out _, out _)
            ? zoom : ZoomLevel;
        _hasAzureZoomLimits = false;
        HashSet<long> active = [];
        foreach (TileLayerSnapshot snapshot in snapshots)
        {
            if (snapshot.Acquisition.SourceKind != RasterSourceKind.Azure ||
                !snapshot.IsVisible || snapshot.Opacity <= 0)
                continue;
            active.Add(snapshot.RuntimeId);
            _hasAzureZoomLimits |= snapshot.MinZoom > 0 || snapshot.MaxZoom < 24;
            if (!_azureSourceAttributions.TryGetValue(snapshot.RuntimeId, out var current) ||
                !Equals(current.SourceKey, snapshot.SourceKey))
            {
                bool liveRefresh = snapshot.Acquisition.RefreshIdentity is { } identity &&
                    Equals(identity, current?.RefreshIdentity);
                _azureSourceAttributions[snapshot.RuntimeId] = new(
                    snapshot.SourceKey, snapshot.Acquisition.RefreshIdentity)
                {
                    Text = liveRefresh ? current!.Text : string.Empty,
                };
            }
            var state = _azureSourceAttributions[snapshot.RuntimeId];
            state.MinZoom = snapshot.MinZoom;
            state.MaxZoom = snapshot.MaxZoom;
        }
        foreach (long id in _azureSourceAttributions.Keys.Where(id => !active.Contains(id)).ToArray())
            _azureSourceAttributions.Remove(id);
        UpdateAttribution();
    }

    private void OnAzureDisplayZoomChanged(double zoom)
    {
        bool changed = _azureSourceAttributions.Values.Any(
            source => source.IncludesZoom(_azureAttributionZoom) != source.IncludesZoom(zoom));
        _azureAttributionZoom = zoom;
        if (changed)
            UpdateAttribution();
    }

    private string GetAzureAttribution(AzureTileLayer layer)
    {
        List<string> values = [];
        if (!string.IsNullOrWhiteSpace(layer.Attribution))
            values.Add(layer.Attribution.Trim());
        foreach (long id in layer.AttributionSourceIds)
        {
            if (_azureSourceAttributions.TryGetValue(id, out var source) &&
                source.IncludesZoom(_azureAttributionZoom) &&
                !string.IsNullOrWhiteSpace(source.Text))
                values.Add(source.Text);
        }
        return string.Join(" ", values.Distinct(StringComparer.Ordinal));
    }

    private void ScheduleAzureRefresh()
    {
        _azureRefreshTimer?.Stop();
        if (!IsLoaded || _runtimeResourcesReleased ||
            MapStyle == MapStyle.Blank || string.IsNullOrWhiteSpace(MapServiceToken))
            return;
        TimeSpan? delay = GetAzureRefreshDelay(_layers, DateTimeOffset.UtcNow);
        if (delay is null)
            return;
        if (_azureRefreshTimer is null)
        {
            _azureRefreshTimer = DispatcherQueue.CreateTimer();
            _azureRefreshTimer.IsRepeating = false;
            _azureRefreshTimer.Tick += OnAzureRefreshTick;
        }
        _azureRefreshTimer.Interval = delay.Value;
        _azureRefreshTimer.Start();
    }

    private void OnAzureRefreshTick(DispatcherQueueTimer sender, object args)
    {
        int traffic = 0, radar = 0, infrared = 0;
        foreach (MapLayer layer in _layers)
        {
            if (!layer.IsVisible || layer.Opacity <= 0)
                continue;
            if (layer is AzureTrafficLayer)
                traffic++;
            else if (layer is AzureWeatherLayer { Timestamp: null } weather)
            {
                if (weather.Kind == WeatherLayerKind.Radar) radar++;
                else infrared++;
            }
        }
        MapControlEventSource.Log.AzureOverlayRefresh(traffic, radar, infrared);
        PublishLayerSnapshots();
    }

    internal static TimeSpan? GetAzureRefreshDelay(
        IEnumerable<MapLayer> layers, DateTimeOffset now)
    {
        long minimum = long.MaxValue;
        foreach (MapLayer layer in layers)
        {
            if (layer is AzureTileLayer { IsVisible: true, Opacity: > 0 } azure &&
                azure.RefreshCadence is TimeSpan cadence)
            {
                long remaining = cadence.Ticks - now.UtcTicks % cadence.Ticks;
                minimum = Math.Min(minimum, remaining);
            }
        }
        return minimum == long.MaxValue ? null :
            TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond, minimum));
    }
}
