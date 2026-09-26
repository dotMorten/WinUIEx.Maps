using Microsoft.UI.Xaml;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps;

/// <summary>
/// Base class for built-in Azure tile layers. Uses the owning map's
/// <see cref="MapControl.MapServiceToken"/> and language, and supplies automatic attribution.
/// </summary>
/// <remarks>
/// Create and access layers on the UI thread. Azure layers are suppressed by
/// <see cref="MapStyle.Blank"/>. This class is not a custom acquisition extension point.
/// </remarks>
public abstract class AzureTileLayer : MapLayer
{
    private static long _nextRuntimeId;
    private bool _restoringProperty;

    internal AzureTileLayer()
    {
        // Custom TileLayer identities are positive; Azure identities occupy a separate range.
        RuntimeId = Interlocked.Decrement(ref _nextRuntimeId);
    }

    internal long RuntimeId { get; }

    internal abstract IEnumerable<TileLayerSnapshot> CreateSnapshots(
        string token, string? language, DateTimeOffset now);

    internal virtual TimeSpan? RefreshCadence => null;

    internal long GetRefreshVersion(DateTimeOffset now) =>
        RefreshCadence is TimeSpan cadence ? now.UtcTicks / cadence.Ticks : 0;

    internal TileLayerSnapshot Snapshot(
        RasterTileAcquisitionSession session, long? runtimeId = null, double minZoom = 0) =>
        new(runtimeId ?? RuntimeId, Revision, session, minZoom, 24, IsVisible, Opacity,
            TimeSpan.FromMilliseconds(250));

    internal TileLayerSnapshot CreateOverlaySnapshot(
        string tileset, string token, string? language, DateTimeOffset now,
        DateTimeOffset? timestamp = null, TrafficFlowStyle? flowStyle = null,
        bool incidents = false, long? runtimeId = null, double minZoom = 0) =>
        Snapshot(new AzureOverlayAcquisitionSession(
            tileset, token, language, timestamp, GetRefreshVersion(now), incidents, flowStyle),
            runtimeId, minZoom);

    internal virtual IEnumerable<long> AttributionSourceIds => [RuntimeId];

    internal static DependencyProperty RegisterAzureProperty(
        string name, Type type, Type owner, object? defaultValue, Func<object?, bool>? isValid = null) =>
        DependencyProperty.Register(name, type, owner,
            new PropertyMetadata(defaultValue, (sender, args) =>
                ((AzureTileLayer)sender).OnAzurePropertyChanged(args, isValid)));

    private void OnAzurePropertyChanged(
        DependencyPropertyChangedEventArgs args, Func<object?, bool>? isValid)
    {
        if (_restoringProperty)
            return;
        if (isValid is not null && !isValid(args.NewValue))
        {
            // Match MapLayer/TileLayer: invalid bindings restore the previously accepted value.
            _restoringProperty = true;
            try { SetValue(args.Property, args.OldValue); }
            finally { _restoringProperty = false; }
            return;
        }
        NotifyChanged(args.Property);
    }

    internal static long AllocateRuntimeId() => Interlocked.Decrement(ref _nextRuntimeId);
}
