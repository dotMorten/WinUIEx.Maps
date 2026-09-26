namespace WinUIEx.Maps.Rendering;

/// <summary>Bounded tile-local BVH, allocated only when an incident tile is hit-tested.</summary>
internal sealed class IncidentSpatialIndex
{
    private readonly Entry[] _entries;
    private readonly Node? _root;

    internal IncidentSpatialIndex(VectorTileFeatureCollection collection)
    {
        _entries = collection.Features
            .Where(f => f.Points.Length != 0 || f.Lines.Length != 0)
            .Select(f => new Entry(f, Bounds(f))).ToArray();
        _root = Build(0, _entries.Length);
    }

    internal long ByteSize => _entries.Length * 160L;

    internal IEnumerable<VectorTileFeature> Query(double left, double top, double right, double bottom)
    {
        if (_root is null)
            yield break;
        foreach (VectorTileFeature feature in Query(_root, new(left, top, right, bottom)))
            yield return feature;
    }

    private IEnumerable<VectorTileFeature> Query(Node node, Box box)
    {
        if (!node.Bounds.Intersects(box))
            yield break;
        if (node.Left is not null && node.Right is not null)
        {
            foreach (var feature in Query(node.Left, box))
                yield return feature;
            foreach (var feature in Query(node.Right, box))
                yield return feature;
        }
        else
        {
            for (int i = node.Start; i < node.End; i++)
                if (_entries[i].Bounds.Intersects(box))
                    yield return _entries[i].Feature;
        }
    }

    private Node? Build(int start, int end)
    {
        if (start == end)
            return null;
        Box bounds = _entries[start].Bounds;
        for (int i = start + 1; i < end; i++)
            bounds = bounds.Union(_entries[i].Bounds);
        if (end - start <= 8)
            return new(bounds, start, end, null, null);
        bool horizontal = bounds.Right - bounds.Left >= bounds.Bottom - bounds.Top;
        Array.Sort(_entries, start, end - start, horizontal ? HorizontalComparer : VerticalComparer);
        int middle = start + (end - start) / 2;
        return new(bounds, start, end, Build(start, middle), Build(middle, end));
    }

    private static readonly IComparer<Entry> HorizontalComparer =
        Comparer<Entry>.Create((a, b) => (a.Bounds.Left + a.Bounds.Right)
            .CompareTo(b.Bounds.Left + b.Bounds.Right));
    private static readonly IComparer<Entry> VerticalComparer =
        Comparer<Entry>.Create((a, b) => (a.Bounds.Top + a.Bounds.Bottom)
            .CompareTo(b.Bounds.Top + b.Bounds.Bottom));

    private static Box Bounds(VectorTileFeature feature)
    {
        Box bounds = new(double.PositiveInfinity, double.PositiveInfinity,
            double.NegativeInfinity, double.NegativeInfinity);
        foreach (var point in feature.Points)
            bounds = bounds.Union(new(point.X, point.Y, point.X, point.Y));
        foreach (var line in feature.Lines)
            foreach (var point in line.Points)
                bounds = bounds.Union(new(point.X, point.Y, point.X, point.Y));
        return bounds;
    }

    private readonly record struct Box(double Left, double Top, double Right, double Bottom)
    {
        internal bool Intersects(Box other) =>
            Left <= other.Right && Right >= other.Left && Top <= other.Bottom && Bottom >= other.Top;
        internal Box Union(Box other) => new(Math.Min(Left, other.Left), Math.Min(Top, other.Top),
            Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    private sealed record Entry(VectorTileFeature Feature, Box Bounds);
    private sealed record Node(Box Bounds, int Start, int End, Node? Left, Node? Right);
}
