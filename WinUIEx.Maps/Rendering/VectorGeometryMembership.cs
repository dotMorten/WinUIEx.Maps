namespace WinUIEx.Maps.Rendering;

// Tile-owned filter decisions, keyed by the immutable style-layer identity.
// Paint, visibility, zoom bounds, and pattern availability are still evaluated at
// the requested zoom. Zoom-dependent filters never enter this cache.
internal sealed class VectorGeometryMembership
{
    private readonly Dictionary<object, VectorStyleFilterResult[]> _filters = [];
    internal long ByteSize { get; private set; }

    internal VectorStyleFilterResult[]? GetFilters(
        object layer, bool dependsOnZoom, IReadOnlyList<VectorTileFeature> features,
        double zoom, bool polygons, Func<VectorStyleEvaluationContext, VectorStyleFilterResult> evaluate)
    {
        if (dependsOnZoom)
            return null;
        if (_filters.TryGetValue(layer, out var filters))
            return filters;
        filters = new VectorStyleFilterResult[features.Count];
        for (int i = 0; i < features.Count; i++)
        {
            VectorTileFeature feature = features[i];
            if (polygons ? feature.Polygons.Length != 0 : feature.HasLinePaths)
                filters[i] = evaluate(new(feature, zoom));
        }
        _filters.Add(layer, filters);
        ByteSize += 64 + (long)filters.Length * sizeof(int);
        return filters;
    }
}
