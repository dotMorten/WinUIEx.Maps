using System.Collections;
using Windows.Devices.Geolocation;
using Windows.UI;

namespace WinUIEx.Maps;

/// <summary>
/// Represents a lightweight filled and stroked polygon on a map.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Path"/> defines one contour. <see cref="Paths"/> defines multiple contours
/// using even-odd filling, so nested contours form holes. Setting a non-null
/// <see cref="Path"/> clears <see cref="Paths"/>; adding, inserting, or replacing an item in
/// <see cref="Paths"/> clears <see cref="Path"/>. The two geometry sources are therefore
/// never active simultaneously.
/// </para>
/// <para>
/// Properties and the <see cref="Paths"/> collection publish immutable geometry snapshots
/// and may be changed from a worker thread. A <see cref="Geopath"/> is copied when assigned
/// or added; later changes to external position storage do not affect the polygon.
/// </para>
/// </remarks>
public sealed class MapPolygon : MapElement
{
    private const double MaximumStrokeThickness = 4096;
    private readonly object _sync = new();
    private readonly List<Geopath> _pathItems = [];
    private readonly PolygonPathCollection _paths;
    private MapPolygonState _state = new(
        null,
        MapGeometryData.Empty,
        Microsoft.UI.Colors.Transparent,
        Microsoft.UI.Colors.Black,
        false,
        1);

    /// <summary>
    /// Initializes an empty polygon.
    /// </summary>
    public MapPolygon()
    {
        _paths = new PolygonPathCollection(this);
    }

    /// <summary>
    /// Gets or sets the single polygon contour, or <see langword="null"/> when
    /// <see cref="Paths"/> supplies the contours.
    /// </summary>
    /// <remarks>Setting a non-null value clears <see cref="Paths"/>.</remarks>
    /// <exception cref="ArgumentException">
    /// A path contains a non-finite coordinate or exceeds the supported geometry limits.
    /// </exception>
    public Geopath? Path
    {
        get => Volatile.Read(ref _state).Path;
        set
        {
            MapGeometryData geometry = value is null
                ? MapGeometryData.Empty
                : MapGeometryData.CreatePolygon([value]);
            lock (_sync)
            {
                MapPolygonState current = _state;
                if (ReferenceEquals(current.Path, value) &&
                    (value is null || _pathItems.Count == 0))
                {
                    return;
                }

                if (value is not null)
                {
                    _pathItems.Clear();
                }
                Volatile.Write(ref _state, current with
                {
                    Path = value,
                    Geometry = geometry,
                });
            }
            OnChanged();
        }
    }

    /// <summary>
    /// Gets the mutable list of polygon contours.
    /// </summary>
    /// <remarks>
    /// Contours use the even-odd fill rule. Adding, inserting, or replacing an item clears
    /// <see cref="Path"/> before publishing the new immutable geometry. A mutation is
    /// rejected atomically when a path contains a non-finite coordinate or the combined
    /// geometry exceeds the supported limits.
    /// </remarks>
    public IList<Geopath> Paths => _paths;

    /// <summary>
    /// Gets or sets the polygon fill color. The default is transparent.
    /// </summary>
    public Color FillColor
    {
        get => Volatile.Read(ref _state).FillColor;
        set => UpdateState(
            state => state.FillColor == value ? state : state with { FillColor = value });
    }

    /// <summary>
    /// Gets or sets the polygon stroke color. The default is black.
    /// </summary>
    public Color StrokeColor
    {
        get => Volatile.Read(ref _state).StrokeColor;
        set => UpdateState(
            state => state.StrokeColor == value ? state : state with { StrokeColor = value });
    }

    /// <summary>
    /// Gets or sets whether the polygon stroke uses a deterministic screen-space dash
    /// pattern. The default is <see langword="false"/>.
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
            ValidateStrokeThickness(value);
            UpdateState(
                state => state.StrokeThickness.Equals(value)
                    ? state
                    : state with { StrokeThickness = value });
        }
    }

    internal MapPolygonState GetState() => Volatile.Read(ref _state);

    private void UpdateState(Func<MapPolygonState, MapPolygonState> update)
    {
        bool changed;
        lock (_sync)
        {
            MapPolygonState current = _state;
            MapPolygonState replacement = update(current);
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

    private void MutatePaths(Action<List<Geopath>> mutation)
    {
        bool changed;
        lock (_sync)
        {
            List<Geopath> replacement = [.. _pathItems];
            mutation(replacement);
            if (replacement.Any(path => path is null))
            {
                throw new ArgumentNullException(nameof(Paths), "Paths cannot contain null.");
            }

            MapPolygonState current = _state;
            changed = !ReferenceListsEqual(_pathItems, replacement);
            if (!changed)
            {
                return;
            }

            MapGeometryData geometry = MapGeometryData.CreatePolygon(replacement);
            _pathItems.Clear();
            _pathItems.AddRange(replacement);
            Volatile.Write(ref _state, current with
            {
                Path = null,
                Geometry = geometry,
            });
        }
        OnChanged();
    }

    private static bool ReferenceListsEqual(
        IReadOnlyList<Geopath> left,
        IReadOnlyList<Geopath> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        for (int index = 0; index < left.Count; index++)
        {
            if (!ReferenceEquals(left[index], right[index]))
            {
                return false;
            }
        }
        return true;
    }

    private static void ValidateStrokeThickness(double value)
    {
        if (!double.IsFinite(value) || value < 0 || value > MaximumStrokeThickness)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private sealed class PolygonPathCollection(MapPolygon owner) : IList<Geopath>
    {
        public Geopath this[int index]
        {
            get
            {
                lock (owner._sync)
                {
                    return owner._pathItems[index];
                }
            }
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                owner.MutatePaths(paths => paths[index] = value);
            }
        }

        public int Count
        {
            get
            {
                lock (owner._sync)
                {
                    return owner._pathItems.Count;
                }
            }
        }

        public bool IsReadOnly => false;

        public void Add(Geopath item)
        {
            ArgumentNullException.ThrowIfNull(item);
            owner.MutatePaths(paths => paths.Add(item));
        }

        public void Clear()
        {
            owner.MutatePaths(paths => paths.Clear());
        }

        public bool Contains(Geopath item)
        {
            lock (owner._sync)
            {
                return owner._pathItems.Contains(item);
            }
        }

        public void CopyTo(Geopath[] array, int arrayIndex)
        {
            lock (owner._sync)
            {
                owner._pathItems.CopyTo(array, arrayIndex);
            }
        }

        public IEnumerator<Geopath> GetEnumerator()
        {
            lock (owner._sync)
            {
                return owner._pathItems.ToArray().AsEnumerable().GetEnumerator();
            }
        }

        public int IndexOf(Geopath item)
        {
            lock (owner._sync)
            {
                return owner._pathItems.IndexOf(item);
            }
        }

        public void Insert(int index, Geopath item)
        {
            ArgumentNullException.ThrowIfNull(item);
            owner.MutatePaths(paths => paths.Insert(index, item));
        }

        public bool Remove(Geopath item)
        {
            bool removed = false;
            owner.MutatePaths(paths => removed = paths.Remove(item));
            return removed;
        }

        public void RemoveAt(int index)
        {
            owner.MutatePaths(paths => paths.RemoveAt(index));
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

internal sealed record MapPolygonState(
    Geopath? Path,
    MapGeometryData Geometry,
    Color FillColor,
    Color StrokeColor,
    bool StrokeDashed,
    double StrokeThickness);
