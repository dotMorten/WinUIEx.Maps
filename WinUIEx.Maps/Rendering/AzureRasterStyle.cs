using System.Text.Json;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// A supported Azure raster within a vector style. It is an overlay,
/// not a replacement canvas; absent opacity means Style Spec's default of one.
/// Request URLs from the document are never executed.
/// </summary>
internal sealed record AzureRasterStyle(
    int Order, double MinZoom, double MaxZoom, TimeSpan FadeDuration,
    VectorBackgroundStyleLayer Paint)
{
    internal double GetOpacity(double zoom) =>
        zoom >= MinZoom && zoom < MaxZoom &&
        Paint.Evaluate(zoom, out VectorFillStyle style) == VectorStyleFillResult.Resolved
            ? style.Color.W : 0;

    internal static AzureRasterStyle? Parse(
        JsonElement root, string tileset = "microsoft.terra.main")
    {
        if (!root.TryGetProperty("sources", out JsonElement sources) ||
            !root.TryGetProperty("layers", out JsonElement layers))
            return null;
        int order = 0;
        foreach (JsonElement layer in layers.EnumerateArray())
        {
            int currentOrder = order++;
            if (!layer.TryGetProperty("type", out var type) || type.GetString() != "raster" ||
                !layer.TryGetProperty("source", out var sourceName) ||
                !sources.TryGetProperty(sourceName.GetString()!, out var source) ||
                !source.TryGetProperty("url", out var url) ||
                url.GetString() is not string sourceUrl ||
                !sourceUrl.Contains(tileset, StringComparison.Ordinal))
                continue;

            VectorStyleExpression visibility = VectorStyleExpression.Literal(VectorStyleValue.FromString("visible"));
            VectorStyleExpression opacity = VectorStyleExpression.Literal(VectorStyleValue.FromNumber(1));
            double fade = 300;
            if (layer.TryGetProperty("layout", out var layout) &&
                layout.TryGetProperty("visibility", out var visible) &&
                !VectorStyleExpression.TryParse(visible, out visibility))
                throw new InvalidDataException("Unsupported Azure raster visibility.");
            if (layer.TryGetProperty("paint", out var paint))
            {
                foreach (var property in paint.EnumerateObject())
                    if (property.Name is not ("raster-opacity" or "raster-fade-duration"))
                        throw new InvalidDataException("Unsupported Azure raster paint.");
                if (paint.TryGetProperty("raster-opacity", out var alpha) &&
                    !VectorStyleExpression.TryParse(alpha, out opacity))
                    throw new InvalidDataException("Unsupported Azure raster opacity.");
                if (paint.TryGetProperty("raster-fade-duration", out var duration))
                    fade = duration.GetDouble();
            }
            if (!double.IsFinite(fade) || fade < 0)
                throw new InvalidDataException("Invalid Azure raster fade duration.");
            return new(currentOrder,
                layer.TryGetProperty("minzoom", out var min) ? min.GetDouble() : 0,
                layer.TryGetProperty("maxzoom", out var max) ? max.GetDouble() : 24,
                TimeSpan.FromMilliseconds(fade),
                new(currentOrder, visibility, VectorStyleExpression.Literal(VectorStyleValue.FromString("#ffffff")), opacity));
        }
        return null;
    }
}
