using System.Numerics;
using System.Text.Json;

namespace WinUIEx.Maps.Rendering;

internal sealed partial class VectorStyle
{
    internal VectorExtrusionStyleLayer[] ExtrusionLayers { get; private init; } = [];
    internal VectorExtrusionLight? ExtrusionLight { get; private init; }

    private static VectorStyleLayerParseResult TryParseExtrusionLayer(
        JsonElement layer, IReadOnlySet<string>? sources, int order,
        out VectorExtrusionStyleLayer? parsed)
    {
        parsed = null;
        if (sources is not null &&
            (!layer.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String ||
             !sources.Contains(source.GetString()!)))
            return VectorStyleLayerParseResult.UnsupportedVectorSource;
        if (!layer.TryGetProperty("source-layer", out var sourceLayer) ||
            sourceLayer.ValueKind != JsonValueKind.String)
            return VectorStyleLayerParseResult.UnsupportedSourceLayer;
        string name = sourceLayer.GetString()!;
        if (name.Length is 0 or > MaximumSourceLayerLength)
            return VectorStyleLayerParseResult.InvalidDefinition;
        layer.TryGetProperty("layout", out var layout);
        layer.TryGetProperty("paint", out var paint);
        if (layout.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object) ||
            paint.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
            return VectorStyleLayerParseResult.InvalidDefinition;
        double min = 0, max = 24;
        if (layer.TryGetProperty("minzoom", out var minValue) &&
                (!minValue.TryGetDouble(out min) || !double.IsFinite(min)) ||
            layer.TryGetProperty("maxzoom", out var maxValue) &&
                (!maxValue.TryGetDouble(out max) || !double.IsFinite(max)) ||
            min < 0 || max > 24 || min >= max)
            return VectorStyleLayerParseResult.InvalidDefinition;
        VectorStyleExpression filter = VectorStyleExpression.Literal(VectorStyleValue.FromBoolean(true));
        if (layer.TryGetProperty("filter", out var filterValue) &&
            !VectorStyleExpression.TryParseFilter(filterValue, out filter))
            return VectorStyleLayerParseResult.UnsupportedExpression;
        if (!TryParseOptionalExpression(layout, "visibility", VectorStyleValue.FromString("visible"), out var visibility) ||
            !TryParseOptionalExpression(paint, "fill-extrusion-height", VectorStyleValue.FromNumber(0), out var height) ||
            !TryParseOptionalExpression(paint, "fill-extrusion-base", VectorStyleValue.FromNumber(0), out var @base) ||
            !TryParseOptionalExpression(paint, "fill-extrusion-color", VectorStyleValue.FromString("#000000"), out var color) ||
            !TryParseOptionalExpression(paint, "fill-extrusion-pattern", VectorStyleValue.Null, out var pattern) ||
            !TryParseOptionalExpression(paint, "fill-extrusion-opacity", VectorStyleValue.FromNumber(1), out var opacity) ||
            !TryParseOptionalExpression(paint, "fill-extrusion-vertical-gradient", VectorStyleValue.FromBoolean(true), out var gradient) ||
            !TryParseOptionalExpression(paint, "fill-extrusion-translate", VectorStyleValue.FromArray(
                [VectorStyleValue.FromNumber(0), VectorStyleValue.FromNumber(0)]), out var translate) ||
            !TryParseOptionalExpression(paint, "fill-extrusion-translate-anchor", VectorStyleValue.FromString("map"), out var anchor) ||
            visibility.DependsOnFeature || opacity.DependsOnFeature || gradient.DependsOnFeature ||
            translate.DependsOnFeature || anchor.DependsOnFeature)
            return VectorStyleLayerParseResult.UnsupportedExpression;
        parsed = new(order, name, min, max, visibility, filter, height, @base,
            color.WithOpaqueColorInterpolation(), pattern, opacity, gradient, translate, anchor);
        return VectorStyleLayerParseResult.Parsed;
    }

    private static VectorExtrusionLight ParseExtrusionLight(JsonElement root)
    {
        root.TryGetProperty("light", out var light);
        if (light.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object) ||
            !TryParseOptionalExpression(light, "anchor", VectorStyleValue.FromString("viewport"), out var anchor) ||
            !TryParseOptionalExpression(light, "position", VectorStyleValue.FromArray(
                [VectorStyleValue.FromNumber(1.15), VectorStyleValue.FromNumber(210), VectorStyleValue.FromNumber(30)]), out var position) ||
            !TryParseOptionalExpression(light, "color", VectorStyleValue.FromString("#ffffff"), out var color) ||
            !TryParseOptionalExpression(light, "intensity", VectorStyleValue.FromNumber(0.5), out var intensity) ||
            anchor.DependsOnFeature || position.DependsOnFeature || color.DependsOnFeature || intensity.DependsOnFeature)
            throw new InvalidDataException("The vector style contains unsupported extrusion lighting.");
        return new(anchor, position, color.WithOpaqueColorInterpolation(), intensity);
    }
}

internal sealed class VectorExtrusionStyleLayer(
    int order, string sourceLayer, double minZoom, double maxZoom,
    VectorStyleExpression visibility, VectorStyleExpression filter,
    VectorStyleExpression height, VectorStyleExpression @base,
    VectorStyleExpression color, VectorStyleExpression pattern,
    VectorStyleExpression opacity, VectorStyleExpression gradient,
    VectorStyleExpression translate, VectorStyleExpression anchor)
{
    internal int Order { get; } = order;
    internal string SourceLayer { get; } = sourceLayer;
    private readonly VectorStyleExpression[] _zoomExpressions = VectorStyleExpression.GetZoomDependencies(
        visibility, height, @base, color);

    internal void CollectZoomStops(List<double> stops)
    {
        stops.Add(minZoom);
        stops.Add(maxZoom);
        visibility.CollectZoomStops(stops);
        filter.CollectZoomStops(stops);
        height.CollectZoomStops(stops);
        @base.CollectZoomStops(stops);
        pattern.CollectZoomStops(stops);
    }

    internal bool CanReuseZoom(double previous, double zoom) =>
        double.IsFinite(previous) &&
        VectorStyleExpression.CanReuseLayerZoom(previous, zoom, minZoom, maxZoom, _zoomExpressions) &&
        filter.CanReuseZoom(Math.Floor(previous), Math.Floor(zoom)) &&
        pattern.CanReuseZoom(Math.Floor(previous), Math.Floor(zoom));

    internal bool CanReuseMembership(double previous, double zoom) =>
        double.IsFinite(previous) &&
        (previous >= minZoom && previous < maxZoom) == (zoom >= minZoom && zoom < maxZoom) &&
        visibility.CanReuseZoom(previous, zoom) &&
        filter.CanReuseZoom(Math.Floor(previous), Math.Floor(zoom)) &&
        pattern.CanReuseZoom(Math.Floor(previous), Math.Floor(zoom));

    internal VectorStyleVisibilityResult EvaluateVisibility(double zoom)
    {
        if (zoom < minZoom || zoom >= maxZoom)
            return VectorStyleVisibilityResult.Hidden;
        if (!visibility.TryEvaluate(new(null, zoom), out var value))
            return VectorStyleVisibilityResult.EvaluationFailure;
        return value.StringValue switch
        {
            "visible" => VectorStyleVisibilityResult.Visible,
            "none" => VectorStyleVisibilityResult.Hidden,
            _ => VectorStyleVisibilityResult.EvaluationFailure,
        };
    }

    internal VectorStyleFilterResult EvaluateFilter(VectorTileFeature feature, double zoom)
    {
        if (!filter.TryEvaluate(new(feature, Math.Floor(zoom)), out var value) ||
            value.Kind != VectorStyleValueKind.Boolean)
            return VectorStyleFilterResult.EvaluationFailure;
        return value.BooleanValue ? VectorStyleFilterResult.Match : VectorStyleFilterResult.NoMatch;
    }

    internal bool TryEvaluateLayer(double zoom, out VectorExtrusionLayerPaint paint)
    {
        paint = default;
        VectorStyleEvaluationContext context = new(null, zoom);
        if (!TryNumber(opacity, context, out double alpha) || alpha is < 0 or > 1 ||
            !gradient.TryEvaluate(context, out var gradientValue) || gradientValue.Kind != VectorStyleValueKind.Boolean ||
            !translate.TryEvaluate(context, out var offset) || !VectorTextStyleLayer.TryGetPair(offset, out double x, out double y) ||
            !double.IsFinite(x) || !double.IsFinite(y) ||
            !anchor.TryEvaluate(context, out var anchorValue) || anchorValue.StringValue is not ("map" or "viewport"))
            return false;
        paint = new(alpha, gradientValue.BooleanValue, x, y, anchorValue.StringValue == "viewport");
        return true;
    }

    internal bool TryEvaluateFeature(VectorTileFeature feature, double zoom,
        out VectorExtrusionPaint paint, out string? patternName)
    {
        paint = default;
        patternName = null;
        VectorStyleEvaluationContext context = new(feature, zoom);
        if (!TryNumber(height, context, out double h) || !TryNumber(@base, context, out double b) ||
            b < 0 || h < b ||
            !pattern.TryEvaluate(new(feature, Math.Floor(zoom)), out var patternValue))
            return false;
        Vector4 rgb = Vector4.One;
        if (patternValue.Kind != VectorStyleValueKind.Null)
        {
            if (patternValue.Kind != VectorStyleValueKind.String || string.IsNullOrEmpty(patternValue.StringValue))
                return false;
            patternName = patternValue.StringValue;
        }
        else if (!color.TryEvaluate(context, out var colorValue) ||
            !VectorTextStyleLayer.TryParseColor(colorValue, out rgb, ignoreAlpha: true))
            return false;
        paint = new(h, b, rgb);
        return true;
    }

    internal static bool TryNumber(VectorStyleExpression expression, VectorStyleEvaluationContext context, out double number)
    {
        number = 0;
        return expression.TryEvaluate(context, out var value) && value.TryGetNumber(out number) && double.IsFinite(number);
    }
}

internal readonly record struct VectorExtrusionPaint(
    double Height, double Base, Vector4 Color, long TextureId = 0,
    double PatternWidth = 0, double PatternHeight = 0);

internal readonly record struct VectorExtrusionLayerPaint(
    double Opacity, bool VerticalGradient, double TranslateX, double TranslateY, bool ViewportTranslation);

internal readonly record struct VectorStyledExtrusion(int Order, VectorTilePolygon Polygon, VectorExtrusionPaint Paint);
internal sealed record VectorExtrusionResolution(VectorStyledExtrusion[] Extrusions, int EvaluationFailures);

internal sealed class VectorExtrusionLight(
    VectorStyleExpression anchor, VectorStyleExpression position,
    VectorStyleExpression color, VectorStyleExpression intensity)
{
    internal bool TryEvaluate(double zoom, double heading, out Vector4 direction, out Vector4 tint)
    {
        direction = tint = default;
        VectorStyleEvaluationContext context = new(null, zoom);
        if (!anchor.TryEvaluate(context, out var a) || a.StringValue is not ("map" or "viewport") ||
            !position.TryEvaluate(context, out var p) || p.ArrayValue is not { Length: 3 } ||
            !p.ArrayValue[0].TryGetNumber(out double radius) || !double.IsFinite(radius) || radius < 0 ||
            !p.ArrayValue[1].TryGetNumber(out double azimuth) || !double.IsFinite(azimuth) ||
            !p.ArrayValue[2].TryGetNumber(out double polar) || !double.IsFinite(polar) ||
            !color.TryEvaluate(context, out var c) || !VectorTextStyleLayer.TryParseColor(c, out tint, ignoreAlpha: true) ||
            !VectorExtrusionStyleLayer.TryNumber(intensity, context, out double strength) || strength is < 0 or > 1)
            return false;
        azimuth = (azimuth + (a.StringValue == "viewport" ? heading : 0)) * Math.PI / 180;
        polar *= Math.PI / 180;
        direction = new((float)(Math.Sin(azimuth) * Math.Sin(polar)),
            (float)(-Math.Cos(azimuth) * Math.Sin(polar)), (float)Math.Cos(polar), (float)strength);
        return true;
    }
}

internal sealed partial class VectorStyleAssets
{
    internal bool HasExtrusions => _mapStyle != MapStyle.BlankAccessible && _style.ExtrusionLayers.Length != 0;
    internal VectorExtrusionStyleLayer[] ExtrusionLayers => _style.ExtrusionLayers;
    internal VectorExtrusionLight? ExtrusionLight => _style.ExtrusionLight;

    internal bool HasVisibleExtrusions(double displayZoom)
    {
        double zoom = GetStyleZoom(displayZoom);
        foreach (var layer in ExtrusionLayers)
            if (layer.EvaluateVisibility(zoom) == VectorStyleVisibilityResult.Visible &&
                layer.TryEvaluateLayer(zoom, out var paint) && paint.Opacity > 0)
                return true;
        return false;
    }

    internal bool CanReuseExtrusions(double previous, double zoom)
    {
        if (!double.IsFinite(previous))
            return false;
        foreach (var layer in ExtrusionLayers)
            if (!layer.CanReuseZoom(GetStyleZoom(previous), GetStyleZoom(zoom)))
                return false;
        return true;
    }

    internal bool CanReuseExtrusionMembership(double previous, double zoom)
    {
        foreach (var layer in ExtrusionLayers)
            if (!layer.CanReuseMembership(GetStyleZoom(previous), GetStyleZoom(zoom)))
                return false;
        return true;
    }

    internal VectorExtrusionResolution ResolveExtrusions(VectorTileFeatureCollection features, double zoom)
    {
        zoom = GetStyleZoom(zoom);
        List<VectorStyledExtrusion> results = [];
        int failures = 0;
        foreach (var layer in ExtrusionLayers)
        {
            var visibility = layer.EvaluateVisibility(zoom);
            if (visibility != VectorStyleVisibilityResult.Visible)
            {
                failures += visibility == VectorStyleVisibilityResult.EvaluationFailure ? 1 : 0;
                continue;
            }
            if (!layer.TryEvaluateLayer(zoom, out var layerPaint))
            {
                failures++;
                continue;
            }
            if (layerPaint.Opacity == 0)
                continue;
            foreach (var feature in features.GetSourceLayer(layer.SourceLayer))
            {
                if (feature.Polygons.Length == 0)
                    continue;
                var filter = layer.EvaluateFilter(feature, zoom);
                if (filter != VectorStyleFilterResult.Match)
                {
                    failures += filter == VectorStyleFilterResult.EvaluationFailure ? 1 : 0;
                    continue;
                }
                if (!layer.TryEvaluateFeature(feature, zoom, out var paint, out string? pattern))
                {
                    failures++;
                    continue;
                }
                if (pattern is not null)
                {
                    if (_spriteAtlas.TryGetTexture(pattern, out var texture, out var entry) != VectorSpriteLookupResult.Found ||
                        texture is null)
                    {
                        failures++;
                        continue;
                    }
                    paint = paint with { TextureId = texture.TextureId,
                        PatternWidth = entry.Width / entry.PixelRatio, PatternHeight = entry.Height / entry.PixelRatio };
                }
                foreach (var polygon in feature.Polygons)
                    results.Add(new(layer.Order, polygon, paint));
            }
        }
        return new(results.ToArray(), failures);
    }

    private void PrepareExtrusionPatternTextures(VectorTileFeatureCollection features, double zoom,
        Dictionary<long, VectorSpriteTextureData> textures, CancellationToken cancellationToken)
    {
        foreach (var layer in ExtrusionLayers)
        {
            if (layer.EvaluateVisibility(zoom) != VectorStyleVisibilityResult.Visible)
                continue;
            foreach (var feature in features.GetSourceLayer(layer.SourceLayer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (feature.Polygons.Length == 0 || layer.EvaluateFilter(feature, zoom) != VectorStyleFilterResult.Match ||
                    !layer.TryEvaluateFeature(feature, zoom, out _, out string? pattern) || pattern is null)
                    continue;
                if (_spriteAtlas.TryGetOrCreateTexture(pattern, cancellationToken, out var texture, out _) ==
                    VectorSpriteLookupResult.Found && texture is not null)
                    textures.TryAdd(texture.TextureId, texture);
            }
        }
    }
}
