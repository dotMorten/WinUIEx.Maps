using Windows.Devices.Geolocation;
using Windows.UI;

namespace WinUIEx.Maps;

/// <summary>
/// Represents a lightweight stroked geographic path on a map.
/// </summary>
/// <remarks>
/// Properties publish immutable geometry snapshots and may be changed from a worker thread.
/// A <see cref="Geopath"/> is copied when assigned; no mutable path object is passed to the
/// renderer.
/// </remarks>
public sealed class MapPolyline : MapElement
{
    private const double MaximumStrokeThickness = 4096;
    private readonly object _sync = new();
    private MapPolylineState _state = new(
        null,
        MapGeometryData.Empty,
        Microsoft.UI.Colors.Black,
        false,
        1);

    /// <summary>
    /// Gets or sets the geographic path, or <see langword="null"/> for no geometry.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A path contains a non-finite coordinate or exceeds the supported geometry limits.
    /// </exception>
    public Geopath? Path
    {
        get => Volatile.Read(ref _state).Path;
        set
        {
            MapGeometryData geometry = MapGeometryData.CreatePolyline(value);
            UpdateState(
                state => ReferenceEquals(state.Path, value)
                    ? state
                    : state with { Path = value, Geometry = geometry });
        }
    }

    /// <summary>
    /// Gets or sets the polyline stroke color. The default is black.
    /// </summary>
    public Color StrokeColor
    {
        get => Volatile.Read(ref _state).StrokeColor;
        set => UpdateState(
            state => state.StrokeColor == value ? state : state with { StrokeColor = value });
    }

    /// <summary>
    /// Gets or sets whether the stroke uses a deterministic screen-space dash pattern. The
    /// default is <see langword="false"/>.
    /// </summary>
    public bool StrokeDashed
    {
        get => Volatile.Read(ref _state).StrokeDashed;
        set => UpdateState(
            state => state.StrokeDashed == value
                ? state
                : state with { StrokeDashed = value });
    }

    /// <summary>
    /// Gets or sets the stroke thickness in logical pixels. The default is 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The assigned value is negative, non-finite, or greater than 4096.
    /// </exception>
    public double StrokeThickness
    {
        get => Volatile.Read(ref _state).StrokeThickness;
        set
        {
            if (!double.IsFinite(value) || value < 0 || value > MaximumStrokeThickness)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            UpdateState(
                state => state.StrokeThickness.Equals(value)
                    ? state
                    : state with { StrokeThickness = value });
        }
    }

    internal MapPolylineState GetState() => Volatile.Read(ref _state);

    private void UpdateState(Func<MapPolylineState, MapPolylineState> update)
    {
        bool changed;
        lock (_sync)
        {
            MapPolylineState current = _state;
            MapPolylineState replacement = update(current);
            changed = !ReferenceEquals(current, replacement);
            if (changed)
            {
                Volatile.Write(ref _state, replacement);
            }
        }
        if (changed)
        {
            OnChanged();
        }
    }
}

internal sealed record MapPolylineState(
    Geopath? Path,
    MapGeometryData Geometry,
    Color StrokeColor,
    bool StrokeDashed,
    double StrokeThickness);
