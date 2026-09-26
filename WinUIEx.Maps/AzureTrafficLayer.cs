using Microsoft.UI.Xaml;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps;

/// <summary>Specifies how Azure colors traffic flow.</summary>
public enum TrafficFlowStyle
{
    /// <summary>Colors by measured speed.</summary>
    Absolute,
    /// <summary>Colors by speed relative to uncongested, free-flow speed.</summary>
    Relative,
    /// <summary>Relative speed using a dark-map vector palette.</summary>
    RelativeDark,
    /// <summary>Highlights only departures from free-flow speed.</summary>
    Delay,
    /// <summary>Locally styles relative vectors with reduced sensitivity to minor slowdowns.</summary>
    Reduced,
}

/// <summary>Displays live Azure traffic flow and optional interactive incidents.</summary>
/// <remarks>
/// Add to <see cref="MapControl.Layers"/> to choose its visual position. Incidents render
/// above roads within this layer. Road lines are composited at 50% opacity without
/// fading incident icons. Inherited opacity additionally multiplies both roads and icons.
/// </remarks>
public sealed class AzureTrafficLayer : AzureTileLayer
{
    internal const double RoadLineOpacity = 0.5;
    internal long IncidentRuntimeId { get; } = AllocateRuntimeId();

    /// <summary>Initializes a relative-speed traffic overlay with incidents disabled.</summary>
    public AzureTrafficLayer() { }

    /// <summary>Gets or sets the traffic flow visualization. Defaults to Relative.</summary>
    public TrafficFlowStyle FlowStyle
    {
        get => (TrafficFlowStyle)GetValue(FlowStyleProperty);
        set
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            SetValue(FlowStyleProperty, value);
        }
    }

    /// <summary>Identifies the FlowStyle dependency property.</summary>
    public static readonly DependencyProperty FlowStyleProperty = RegisterAzureProperty(
        nameof(FlowStyle), typeof(TrafficFlowStyle), typeof(AzureTrafficLayer), TrafficFlowStyle.Relative,
        static value => value is TrafficFlowStyle style && Enum.IsDefined(style));

    /// <summary>Gets or sets whether incident markers and affected road segments are displayed.</summary>
    public bool ShowIncidents
    {
        get => (bool)GetValue(ShowIncidentsProperty);
        set => SetValue(ShowIncidentsProperty, value);
    }

    /// <summary>Identifies the ShowIncidents dependency property.</summary>
    public static readonly DependencyProperty ShowIncidentsProperty = RegisterAzureProperty(
        nameof(ShowIncidents), typeof(bool), typeof(AzureTrafficLayer), false);

    /// <summary>Gets or sets the inclusive minimum camera zoom for displaying and acquiring incidents.</summary>
    /// <value>A finite zoom from 0 through 24. The default is 12 (city/neighborhood scale).</value>
    /// <remarks>
    /// Applies to incident markers, affected road segments, and hit testing, not traffic flow.
    /// Set to 0 for all supported zooms. Invalid dependency-property values restore the previous
    /// valid value. Read and write this property on the UI thread.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is non-finite or outside [0, 24].</exception>
    public double MinIncidentZoom
    {
        get => (double)GetValue(MinIncidentZoomProperty);
        set
        {
            if (!IsValidIncidentZoom(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            SetValue(MinIncidentZoomProperty, value);
        }
    }

    /// <summary>Identifies the <see cref="MinIncidentZoom"/> dependency property.</summary>
    public static readonly DependencyProperty MinIncidentZoomProperty = RegisterAzureProperty(
        nameof(MinIncidentZoom), typeof(double), typeof(AzureTrafficLayer), 12d,
        static value => value is double zoom && IsValidIncidentZoom(zoom));

    /// <summary>Occurs when a displayed incident is tapped. Raised on the UI thread.</summary>
    public event EventHandler<AzureTrafficIncidentEventArgs>? IncidentTapped;

    internal bool HasIncidentTappedHandler => IncidentTapped is not null;

    internal void RaiseIncidentTapped(AzureTrafficIncidentEventArgs args) =>
        IncidentTapped?.Invoke(this, args);

    internal override TimeSpan? RefreshCadence => TimeSpan.FromMinutes(1);

    internal override IEnumerable<long> AttributionSourceIds =>
        ShowIncidents ? [RuntimeId, IncidentRuntimeId] : [RuntimeId];

    internal override IEnumerable<TileLayerSnapshot> CreateSnapshots(
        string token, string? language, DateTimeOffset now)
    {
        yield return CreateOverlaySnapshot(GetTileset(FlowStyle), token, language, now, flowStyle: FlowStyle);
        if (ShowIncidents)
            yield return CreateOverlaySnapshot("microsoft.traffic.incident", token, language, now,
                incidents: true, runtimeId: IncidentRuntimeId, minZoom: MinIncidentZoom);
    }

    internal static string GetTileset(TrafficFlowStyle style) => style switch
    {
        TrafficFlowStyle.Absolute => "microsoft.traffic.absolute",
        TrafficFlowStyle.Relative or TrafficFlowStyle.RelativeDark or TrafficFlowStyle.Reduced =>
            "microsoft.traffic.relative",
        TrafficFlowStyle.Delay => "microsoft.traffic.delay",
        _ => throw new ArgumentOutOfRangeException(nameof(style)),
    };

    private static bool IsValidIncidentZoom(double zoom) =>
        double.IsFinite(zoom) && zoom is >= 0 and <= 24;
}
