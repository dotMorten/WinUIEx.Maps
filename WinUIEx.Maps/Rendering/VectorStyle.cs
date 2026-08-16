using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Graphics.Imaging;
using WinUIEx.Maps.Rendering.Diagnostics;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Loads and retains the private Azure Style Spec and sprite assets for one hidden Azure
/// layer without retaining its dependency object.
/// </summary>
internal sealed class AzureVectorStyleProvider
{
    private const int MaximumStyleBytes = 4 * 1024 * 1024;
    private const int MaximumSpriteIndexBytes = 4 * 1024 * 1024;
    private const int MaximumEncodedSpriteBytes = 16 * 1024 * 1024;
    private const uint MaximumSpriteDimension = 4096;
    private const long MaximumDecodedSpriteBytes = 64 * 1024 * 1024;
    private readonly object _sync = new();
    private readonly MapStyle _style;
    private readonly string _styleSlug;
    private readonly string _token;
    private readonly AzureGlyphProvider _glyphProvider;
    private Task<VectorStyleAssets>? _assetsTask;

    internal AzureVectorStyleProvider(MapStyle style, string token)
    {
        _style = style;
        _styleSlug = AzureTileAcquisitionSession.GetAzureStyleName(style);
        _token = token;
        _glyphProvider = new AzureGlyphProvider(style, token);
    }

    internal async Task<VectorStyleAssets> GetAssetsAsync(
        CancellationToken cancellationToken)
    {
        Task<VectorStyleAssets> task;
        lock (_sync)
        {
            if (_assetsTask is { IsCompleted: true, IsCompletedSuccessfully: false })
            {
                _assetsTask = null;
            }
            task = _assetsTask ??= LoadAsync(cancellationToken);
        }

        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (task.IsCompleted && !task.IsCompletedSuccessfully)
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_assetsTask, task))
                    {
                        _assetsTask = null;
                    }
                }
            }
        }
    }

    internal static AzureVectorStyleAssetPaths GetAssetPaths(MapStyle style)
    {
        string slug = AzureTileAcquisitionSession.GetAzureStyleName(style);
        const string query = "styleVersion=2023-01-01&api-version=2.0";
        return new AzureVectorStyleAssetPaths(
            $"styling/styles/{slug}?{query}",
            $"styling/sprites/{slug}/sprite.json?{query}",
            $"styling/sprites/{slug}/sprite.png?{query}");
    }

    private async Task<VectorStyleAssets> LoadAsync(
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        AzureVectorStyleAssetPaths paths = GetAssetPaths(_style);
        byte[] styleJson = await AzureTileAcquisitionSession
            .GetStyleAssetAsync(
                paths.Style,
                _token,
                "application/json",
                MaximumStyleBytes,
                cancellationToken)
            .ConfigureAwait(false);
        byte[] spriteJson = await AzureTileAcquisitionSession
            .GetStyleAssetAsync(
                paths.SpriteIndex,
                _token,
                "application/json",
                MaximumSpriteIndexBytes,
                cancellationToken)
            .ConfigureAwait(false);
        byte[] spritePng = await AzureTileAcquisitionSession
            .GetStyleAssetAsync(
                paths.SpriteImage,
                _token,
                "image/png",
                MaximumEncodedSpriteBytes,
                cancellationToken)
            .ConfigureAwait(false);

        VectorStyle style = VectorStyle.Parse(styleJson);
        Dictionary<string, VectorSpriteEntry> spriteEntries =
            VectorSpriteAtlas.ParseIndex(spriteJson);
        DecodedSpriteImage decoded = await DecodeSpriteAsync(
            spritePng,
            cancellationToken).ConfigureAwait(false);
        VectorSpriteAtlas spriteAtlas = new(
            _styleSlug,
            spriteEntries,
            decoded.Pixels,
            decoded.Width,
            decoded.Height);
        VectorStyleAssets assets = new(
            _style,
            style,
            spriteAtlas,
            new VectorGlyphAtlas(_styleSlug, _glyphProvider));
        MapControlEventSource.Log.VectorStyleAssetsLoaded(
            (int)_style,
            style.LayerCount,
            style.UnsupportedLayerCount,
            spriteAtlas.EntryCount,
            checked((int)decoded.Width),
            checked((int)decoded.Height),
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        VectorStyleCompatibility.Report((int)_style, styleJson);
        return assets;
    }

    internal static async Task<DecodedSpriteImage> DecodeSpriteAsync(
        byte[] encoded,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using MemoryStream stream = new(encoded, writable: false);
        using Windows.Storage.Streams.IRandomAccessStream randomAccessStream =
            stream.AsRandomAccessStream();
        BitmapDecoder? decoder = null;
        BitmapTransform? transform = null;
        PixelDataProvider? pixelData = null;
        try
        {
            decoder = await BitmapDecoder
                .CreateAsync(randomAccessStream)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            uint width = decoder.PixelWidth;
            uint height = decoder.PixelHeight;
            if (width == 0 ||
                height == 0 ||
                width > MaximumSpriteDimension ||
                height > MaximumSpriteDimension ||
                (long)width * height * 4 > MaximumDecodedSpriteBytes)
            {
                throw new InvalidDataException(
                    "The Azure vector sprite atlas dimensions exceed the supported limit.");
            }

            transform = new BitmapTransform();
            pixelData = await decoder
                .GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            byte[] pixels = pixelData.DetachPixelData();
            if (!MapRenderer.IsValidPixelBuffer(pixels, width, height))
            {
                throw new InvalidDataException(
                    "The decoded Azure vector sprite atlas has an invalid pixel buffer.");
            }
            return new DecodedSpriteImage(pixels, width, height);
        }
        finally
        {
            ReleaseWinRtObject(pixelData);
            ReleaseWinRtObject(transform);
            ReleaseWinRtObject(decoder);
        }
    }

    private static void ReleaseWinRtObject(object? value)
    {
        if (value is WinRT.IWinRTObject winRtObject)
        {
            winRtObject.NativeObject.Dispose();
        }
    }

    internal readonly record struct DecodedSpriteImage(
        byte[] Pixels,
        uint Width,
        uint Height);
}

internal readonly record struct AzureVectorStyleAssetPaths(
    string Style,
    string SpriteIndex,
    string SpriteImage);

/// <summary>
/// Combines parsed symbol layers with one premultiplied sprite atlas.
/// </summary>
internal sealed class VectorStyleAssets
{
    private readonly MapStyle _mapStyle;
    private readonly string _styleIdentity;
    private readonly VectorStyle _style;
    private readonly VectorSpriteAtlas _spriteAtlas;
    private readonly VectorGlyphAtlas _glyphAtlas;

    internal VectorStyleAssets(
        MapStyle mapStyle,
        VectorStyle style,
        VectorSpriteAtlas spriteAtlas,
        VectorGlyphAtlas glyphAtlas)
    {
        _mapStyle = mapStyle;
        _styleIdentity = AzureTileAcquisitionSession.GetAzureStyleName(mapStyle);
        _style = style;
        _spriteAtlas = spriteAtlas;
        _glyphAtlas = glyphAtlas;
    }

    internal VectorStyleAssets(
        string styleIdentity,
        VectorStyle style,
        VectorSpriteAtlas spriteAtlas,
        VectorGlyphAtlas glyphAtlas)
    {
        _mapStyle = MapStyle.Road;
        _styleIdentity = styleIdentity;
        _style = style;
        _spriteAtlas = spriteAtlas;
        _glyphAtlas = glyphAtlas;
    }

    internal static VectorStyleAssets CreateForTest(
        MapStyle mapStyle,
        ReadOnlyMemory<byte> styleJson,
        ReadOnlyMemory<byte> spriteJson,
        byte[] spritePixels,
        uint spriteWidth,
        uint spriteHeight) =>
        new(
            mapStyle,
            VectorStyle.Parse(styleJson),
            new VectorSpriteAtlas(
                AzureTileAcquisitionSession.GetAzureStyleName(mapStyle),
                VectorSpriteAtlas.ParseIndex(spriteJson),
                spritePixels,
                spriteWidth,
                spriteHeight),
            new VectorGlyphAtlas(
                AzureTileAcquisitionSession.GetAzureStyleName(mapStyle),
                provider: null));

    internal VectorGlyphAtlas GlyphAtlas => _glyphAtlas;

    internal async Task<VectorSpriteTextureData[]> PrepareTexturesAsync(
        VectorTileFeatureCollection features,
        int tileZoom,
        CancellationToken cancellationToken)
    {
        if (_mapStyle == MapStyle.BlankAccessible)
        {
            return [];
        }

        Dictionary<long, VectorSpriteTextureData> textures = [];
        foreach (double zoom in _style.GetPreparationZooms(tileZoom))
        {
            Resolve(
                features,
                zoom,
                createTextures: true,
                textures,
                symbols: null,
                cancellationToken);
            ResolveLinePatterns(
                features,
                zoom,
                createTextures: true,
                textures,
                symbols: null,
                cancellationToken);
            PrepareFillPatternTextures(
                features,
                zoom,
                textures,
                cancellationToken);
        }
        HashSet<VectorGlyphKey> glyphKeys = [];
        foreach (double zoom in _style.GetPreparationZooms(tileZoom))
        {
            CollectTextGlyphKeys(
                features,
                zoom,
                glyphKeys,
                cancellationToken);
        }
        await _glyphAtlas.PrepareAsync(glyphKeys, cancellationToken)
            .ConfigureAwait(false);
        foreach (VectorGlyphKey key in glyphKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_glyphAtlas.TryGetOrCreateTexture(
                    key,
                    out _,
                    out VectorSpriteTextureData? texture) &&
                texture is not null)
            {
                textures.TryAdd(texture.TextureId, texture);
            }
        }
        return textures.Values.ToArray();
    }

    internal VectorSymbolResolution ResolveSymbols(
        VectorTileFeatureCollection features,
        double zoom,
        double textScaleFactor = 1)
    {
        if (_mapStyle == MapStyle.BlankAccessible)
        {
            return new VectorSymbolResolution([], 0, 0);
        }

        List<VectorTileSymbol> symbols = [];
        VectorStyleResolutionCounts counts = Resolve(
            features,
            zoom,
            createTextures: false,
            textures: null,
            symbols,
            CancellationToken.None);
        ResolveLinePatterns(
            features,
            zoom,
            createTextures: false,
            textures: null,
            symbols,
            CancellationToken.None,
            ref counts);
        ResolveText(
            features,
            zoom,
            NormalizeTextScaleFactor(textScaleFactor),
            symbols,
            ref counts,
            CancellationToken.None);
        ApplyTextFit(symbols);
        return new VectorSymbolResolution(
            symbols.ToArray(),
            counts.EvaluationFailureCount,
            counts.UnavailableSpriteCount,
            counts.ResolvedGlyphCount,
            counts.UnavailableGlyphCount);
    }

    internal VectorTileAccessibilityFeature[] ResolveAccessibilityFeatures(
        VectorTileFeatureCollection features,
        double zoom,
        CancellationToken cancellationToken = default)
    {
        const int maximumFeatureCount = 256;
        List<VectorTileAccessibilityFeature> resolved = [];
        foreach (VectorTextStyleLayer layer in _style.TextLayers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (layer.EvaluateVisibility(zoom) != VectorStyleVisibilityResult.Visible)
            {
                continue;
            }

            foreach (VectorTileFeature feature in
                features.GetSourceLayer(layer.SourceLayer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                VectorStyleEvaluationContext context = new(feature, zoom);
                if (layer.EvaluateFilter(context) != VectorStyleFilterResult.Match ||
                    layer.EvaluateAccessibilityText(
                        context,
                        out string name,
                        out double prominence) != VectorStyleTextResult.Resolved ||
                    !TryGetAccessibilityPosition(
                        feature,
                        layer.Placement,
                        out VectorTilePoint position))
                {
                    continue;
                }

                resolved.Add(new VectorTileAccessibilityFeature(
                    name,
                    ClassifyAccessibilityFeature(layer.SourceLayer),
                    position.X,
                    position.Y,
                    layer.Order,
                    prominence));
                if (resolved.Count == maximumFeatureCount)
                {
                    return resolved.ToArray();
                }
            }
        }
        return resolved.ToArray();
    }

    private static bool TryGetAccessibilityPosition(
        VectorTileFeature feature,
        VectorSymbolPlacementKind placement,
        out VectorTilePoint position)
    {
        if (placement == VectorSymbolPlacementKind.Line)
        {
            foreach (VectorTileLine line in feature.Lines)
            {
                if (line.Points.Length != 0)
                {
                    position = line.Points[line.Points.Length / 2];
                    return true;
                }
            }
        }
        else if (feature.Points.Length != 0)
        {
            position = feature.Points[0];
            return true;
        }

        position = default;
        return false;
    }

    private static MapAccessibilityFeatureKind ClassifyAccessibilityFeature(
        string sourceLayer)
    {
        if (sourceLayer.Contains("road", StringComparison.OrdinalIgnoreCase) ||
            sourceLayer.Contains("transportation", StringComparison.OrdinalIgnoreCase))
        {
            return MapAccessibilityFeatureKind.Road;
        }
        if (sourceLayer.Contains("transit", StringComparison.OrdinalIgnoreCase))
        {
            return MapAccessibilityFeatureKind.Transit;
        }
        if (sourceLayer.Contains("water", StringComparison.OrdinalIgnoreCase) ||
            sourceLayer.Contains("marine", StringComparison.OrdinalIgnoreCase))
        {
            return MapAccessibilityFeatureKind.Water;
        }
        if (sourceLayer.Contains("natural", StringComparison.OrdinalIgnoreCase) ||
            sourceLayer.Contains("landcover", StringComparison.OrdinalIgnoreCase))
        {
            return MapAccessibilityFeatureKind.NaturalFeature;
        }
        if (sourceLayer.Contains("poi", StringComparison.OrdinalIgnoreCase) ||
            sourceLayer.Contains("building", StringComparison.OrdinalIgnoreCase))
        {
            return MapAccessibilityFeatureKind.Landmark;
        }
        if (sourceLayer.Contains("boundary", StringComparison.OrdinalIgnoreCase) ||
            sourceLayer.Contains("admin", StringComparison.OrdinalIgnoreCase))
        {
            return MapAccessibilityFeatureKind.AdministrativeArea;
        }
        if (sourceLayer.Contains("place", StringComparison.OrdinalIgnoreCase) ||
            sourceLayer.Contains("label", StringComparison.OrdinalIgnoreCase))
        {
            return MapAccessibilityFeatureKind.Place;
        }
        return MapAccessibilityFeatureKind.Other;
    }

    private static void ApplyTextFit(List<VectorTileSymbol> symbols)
    {
        Dictionary<long, (double Left, double Top, double Right, double Bottom)>
            textBounds = [];
        foreach (VectorTileSymbol symbol in symbols)
        {
            if (symbol.Kind != VectorSymbolKind.Text ||
                symbol.SymbolGroupId < 0)
            {
                continue;
            }
            double left = symbol.OffsetX - (symbol.Width / 2);
            double top = symbol.OffsetY - (symbol.Height / 2);
            double right = left + symbol.Width;
            double bottom = top + symbol.Height;
            if (textBounds.TryGetValue(symbol.SymbolGroupId, out var bounds))
            {
                textBounds[symbol.SymbolGroupId] = (
                    Math.Min(bounds.Left, left),
                    Math.Min(bounds.Top, top),
                    Math.Max(bounds.Right, right),
                    Math.Max(bounds.Bottom, bottom));
            }
            else
            {
                textBounds[symbol.SymbolGroupId] = (left, top, right, bottom);
            }
        }

        for (int index = symbols.Count - 1; index >= 0; index--)
        {
            VectorTileSymbol icon = symbols[index];
            if (icon.Kind != VectorSymbolKind.Icon ||
                icon.TextFit == VectorIconTextFit.None)
            {
                continue;
            }
            if (!textBounds.TryGetValue(icon.SymbolGroupId, out var bounds))
            {
                symbols.RemoveAt(index);
                continue;
            }
            Vector4 padding = icon.TextFitPadding;
            double fittedLeft = bounds.Left - padding.W;
            double fittedTop = bounds.Top - padding.X;
            double fittedRight = bounds.Right + padding.Y;
            double fittedBottom = bounds.Bottom + padding.Z;
            bool fitWidth = icon.TextFit is
                VectorIconTextFit.Width or VectorIconTextFit.Both;
            bool fitHeight = icon.TextFit is
                VectorIconTextFit.Height or VectorIconTextFit.Both;
            double fittedWidth = fittedRight - fittedLeft;
            double fittedHeight = fittedBottom - fittedTop;
            symbols[index] = icon with
            {
                Width = fitWidth
                    ? Math.Max(icon.Width, fittedWidth)
                    : icon.Width,
                Height = fitHeight
                    ? Math.Max(icon.Height, fittedHeight)
                    : icon.Height,
                OffsetX = fitWidth
                    ? (fittedLeft + fittedRight) / 2
                    : icon.OffsetX,
                OffsetY = fitHeight
                    ? (fittedTop + fittedBottom) / 2
                    : icon.OffsetY,
            };
        }
    }

    internal VectorLineResolution ResolveLines(
        VectorTileFeatureCollection features,
        double zoom)
    {
        if (_mapStyle == MapStyle.BlankAccessible)
        {
            return new VectorLineResolution([], 0);
        }

        List<VectorTileStyledLine> lines = [];
        int evaluationFailureCount = 0;
        foreach (VectorLineStyleLayer layer in _style.LineLayers)
        {
            VectorStyleVisibilityResult visibility = layer.EvaluateVisibility(zoom);
            if (visibility != VectorStyleVisibilityResult.Visible)
            {
                if (visibility == VectorStyleVisibilityResult.EvaluationFailure)
                {
                    evaluationFailureCount++;
                }
                continue;
            }
            foreach (VectorTileFeature feature in features.GetSourceLayer(layer.SourceLayer))
            {
                if (feature.Lines.Length == 0)
                {
                    continue;
                }
                VectorStyleEvaluationContext context = new(feature, zoom);
                VectorStyleFilterResult filter = layer.EvaluateFilter(context);
                if (filter != VectorStyleFilterResult.Match)
                {
                    if (filter == VectorStyleFilterResult.EvaluationFailure)
                    {
                        evaluationFailureCount++;
                    }
                    continue;
                }
                if (!layer.TryEvaluatePattern(
                        context,
                        out string? patternName,
                        out _) ||
                    patternName is not null)
                {
                    if (patternName is null)
                    {
                        evaluationFailureCount++;
                    }
                    continue;
                }
                VectorStyleLineResult lineResult =
                    layer.EvaluateLine(context, out VectorLineStyle style);
                if (lineResult != VectorStyleLineResult.Resolved)
                {
                    if (lineResult == VectorStyleLineResult.EvaluationFailure)
                    {
                        evaluationFailureCount++;
                    }
                    continue;
                }
                foreach (VectorTileLine line in feature.Lines)
                {
                    lines.Add(new VectorTileStyledLine(
                        layer.Order,
                        line.Points,
                        style));
                }
            }
        }
        return new VectorLineResolution(lines.ToArray(), evaluationFailureCount);
    }

    private void PrepareFillPatternTextures(
        VectorTileFeatureCollection features,
        double zoom,
        Dictionary<long, VectorSpriteTextureData> textures,
        CancellationToken cancellationToken)
    {
        foreach (VectorFillStyleLayer layer in _style.FillLayers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (layer.EvaluateVisibility(zoom) !=
                VectorStyleVisibilityResult.Visible)
            {
                continue;
            }
            foreach (VectorTileFeature feature in
                features.GetSourceLayer(layer.SourceLayer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (feature.Polygons.Length == 0)
                {
                    continue;
                }
                VectorStyleEvaluationContext context = new(feature, zoom);
                if (layer.EvaluateFilter(context) !=
                        VectorStyleFilterResult.Match ||
                    layer.EvaluateFill(
                        context,
                        out _,
                        out string? patternName) !=
                        VectorStyleFillResult.Resolved ||
                    patternName is null)
                {
                    continue;
                }
                if (_spriteAtlas.TryGetOrCreateTexture(
                        patternName,
                        cancellationToken,
                        out VectorSpriteTextureData? texture,
                        out _) == VectorSpriteLookupResult.Found &&
                    texture is not null)
                {
                    textures.TryAdd(texture.TextureId, texture);
                }
            }
        }
    }

    private void ResolveLinePatterns(
        VectorTileFeatureCollection features,
        double zoom,
        bool createTextures,
        Dictionary<long, VectorSpriteTextureData>? textures,
        List<VectorTileSymbol>? symbols,
        CancellationToken cancellationToken)
    {
        VectorStyleResolutionCounts ignoredCounts = default;
        ResolveLinePatterns(
            features,
            zoom,
            createTextures,
            textures,
            symbols,
            cancellationToken,
            ref ignoredCounts,
            collectCounts: false);
    }

    private void ResolveLinePatterns(
        VectorTileFeatureCollection features,
        double zoom,
        bool createTextures,
        Dictionary<long, VectorSpriteTextureData>? textures,
        List<VectorTileSymbol>? symbols,
        CancellationToken cancellationToken,
        ref VectorStyleResolutionCounts counts,
        bool collectCounts = true)
    {
        foreach (VectorLineStyleLayer layer in _style.LineLayers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VectorStyleVisibilityResult visibility = layer.EvaluateVisibility(zoom);
            if (visibility != VectorStyleVisibilityResult.Visible)
            {
                if (collectCounts &&
                    visibility == VectorStyleVisibilityResult.EvaluationFailure)
                {
                    counts.EvaluationFailureCount++;
                }
                continue;
            }
            foreach (VectorTileFeature feature in features.GetSourceLayer(layer.SourceLayer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (feature.Lines.Length == 0)
                {
                    continue;
                }
                VectorStyleEvaluationContext context = new(feature, zoom);
                VectorStyleFilterResult filter = layer.EvaluateFilter(context);
                if (filter != VectorStyleFilterResult.Match)
                {
                    if (collectCounts &&
                        filter == VectorStyleFilterResult.EvaluationFailure)
                    {
                        counts.EvaluationFailureCount++;
                    }
                    continue;
                }
                if (!layer.TryEvaluatePattern(
                        context,
                        out string? patternName,
                        out double opacity))
                {
                    if (collectCounts)
                    {
                        counts.EvaluationFailureCount++;
                    }
                    continue;
                }
                if (patternName is null || opacity <= 0)
                {
                    continue;
                }
                VectorSpriteLookupResult spriteResult = createTextures
                    ? _spriteAtlas.TryGetOrCreateTexture(
                        patternName,
                        cancellationToken,
                        out VectorSpriteTextureData? texture,
                        out VectorSpriteEntry entry)
                    : _spriteAtlas.TryGetTexture(
                        patternName,
                        out texture,
                        out entry);
                if (spriteResult != VectorSpriteLookupResult.Found ||
                    texture is null)
                {
                    if (collectCounts)
                    {
                        counts.UnavailableSpriteCount += feature.Lines.Length;
                    }
                    continue;
                }
                textures?.TryAdd(texture.TextureId, texture);
                if (symbols is null)
                {
                    continue;
                }
                double width = entry.Width / entry.PixelRatio;
                double height = entry.Height / entry.PixelRatio;
                if (!double.IsFinite(width) ||
                    !double.IsFinite(height) ||
                    width <= 0 ||
                    height <= 0 ||
                    width > 4096 ||
                    height > 4096)
                {
                    if (collectCounts)
                    {
                        counts.EvaluationFailureCount++;
                    }
                    continue;
                }
                foreach (VectorTileLine line in feature.Lines)
                {
                    symbols.Add(new VectorTileSymbol(
                        layer.Order,
                        0,
                        0,
                        texture.TextureId,
                        width,
                        height,
                        0,
                        0,
                        LinePoints: line.Points,
                        LineSpacing: width,
                        Opacity: opacity,
                        ContinuousLinePlacement: true));
                }
            }
        }
    }

    internal VectorPolygonResolution ResolvePolygons(
        VectorTileFeatureCollection features,
        double zoom)
    {
        if (_mapStyle == MapStyle.BlankAccessible)
        {
            return new VectorPolygonResolution([], 0);
        }

        List<VectorTileStyledPolygon> polygons = [];
        int evaluationFailureCount = 0;
        foreach (VectorBackgroundStyleLayer layer in
            _style.BackgroundLayers)
        {
            VectorStyleFillResult backgroundResult =
                layer.Evaluate(zoom, out VectorFillStyle style);
            if (backgroundResult == VectorStyleFillResult.Resolved)
            {
                polygons.Add(new VectorTileStyledPolygon(
                    layer.Order,
                    [
                        new VectorTileRing(
                        [
                            new VectorTilePoint(0, 0),
                            new VectorTilePoint(1, 0),
                            new VectorTilePoint(1, 1),
                            new VectorTilePoint(0, 1),
                        ]),
                    ],
                    [
                        new VectorTilePoint(0, 0),
                        new VectorTilePoint(1, 0),
                        new VectorTilePoint(1, 1),
                        new VectorTilePoint(0, 0),
                        new VectorTilePoint(1, 1),
                        new VectorTilePoint(0, 1),
                    ],
                    style));
            }
            else if (backgroundResult ==
                VectorStyleFillResult.EvaluationFailure)
            {
                evaluationFailureCount++;
            }
        }
        foreach (VectorFillStyleLayer layer in _style.FillLayers)
        {
            VectorStyleVisibilityResult visibility = layer.EvaluateVisibility(zoom);
            if (visibility != VectorStyleVisibilityResult.Visible)
            {
                if (visibility == VectorStyleVisibilityResult.EvaluationFailure)
                {
                    evaluationFailureCount++;
                }
                continue;
            }
            foreach (VectorTileFeature feature in features.GetSourceLayer(layer.SourceLayer))
            {
                if (feature.Polygons.Length == 0)
                {
                    continue;
                }
                VectorStyleEvaluationContext context = new(feature, zoom);
                VectorStyleFilterResult filter = layer.EvaluateFilter(context);
                if (filter != VectorStyleFilterResult.Match)
                {
                    if (filter == VectorStyleFilterResult.EvaluationFailure)
                    {
                        evaluationFailureCount++;
                    }
                    continue;
                }
                VectorStyleFillResult fillResult =
                    layer.EvaluateFill(
                        context,
                        out VectorFillStyle style,
                        out string? patternName);
                if (fillResult != VectorStyleFillResult.Resolved)
                {
                    if (fillResult == VectorStyleFillResult.EvaluationFailure)
                    {
                        evaluationFailureCount++;
                    }
                    continue;
                }
                if (patternName is not null)
                {
                    VectorSpriteLookupResult spriteResult =
                        _spriteAtlas.TryGetTexture(
                            patternName,
                            out VectorSpriteTextureData? texture,
                            out VectorSpriteEntry entry);
                    if (spriteResult is
                        VectorSpriteLookupResult.Missing or
                        VectorSpriteLookupResult.Hidden)
                    {
                        evaluationFailureCount++;
                        continue;
                    }
                    double patternWidth = entry.Width / entry.PixelRatio;
                    double patternHeight = entry.Height / entry.PixelRatio;
                    if (!double.IsFinite(patternWidth) ||
                        !double.IsFinite(patternHeight) ||
                        patternWidth <= 0 ||
                        patternHeight <= 0)
                    {
                        evaluationFailureCount++;
                        continue;
                    }
                    style = style with
                    {
                        PatternTextureId = texture?.TextureId ??
                            VectorSpriteAtlas.CreateTextureId(
                                _styleIdentity,
                                patternName),
                        PatternWidth = patternWidth,
                        PatternHeight = patternHeight,
                    };
                }
                foreach (VectorTilePolygon polygon in feature.Polygons)
                {
                    polygons.Add(new VectorTileStyledPolygon(
                        layer.Order,
                        polygon.Rings,
                        polygon.FillTriangles,
                        style));
                }
            }
        }
        return new VectorPolygonResolution(
            polygons.ToArray(),
            evaluationFailureCount);
    }

    private void CollectTextGlyphKeys(
        VectorTileFeatureCollection features,
        double zoom,
        HashSet<VectorGlyphKey> glyphKeys,
        CancellationToken cancellationToken)
    {
        foreach (VectorTextStyleLayer layer in _style.TextLayers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (layer.EvaluateVisibility(zoom) != VectorStyleVisibilityResult.Visible)
            {
                continue;
            }
            foreach (VectorTileFeature feature in features.GetSourceLayer(layer.SourceLayer))
            {
                if (layer.Placement == VectorSymbolPlacementKind.Line
                    ? feature.Lines.Length == 0
                    : feature.Points.Length == 0)
                {
                    continue;
                }
                VectorStyleEvaluationContext context = new(feature, zoom);
                if (layer.EvaluateFilter(context) != VectorStyleFilterResult.Match ||
                    layer.EvaluateText(context, out VectorTextStyle text) !=
                        VectorStyleTextResult.Resolved)
                {
                    continue;
                }
                foreach (Rune rune in text.Text.EnumerateRunes())
                {
                    if (rune.Value is not ('\r' or '\n') &&
                        rune.Value <= char.MaxValue)
                    {
                        glyphKeys.Add(new VectorGlyphKey(text.FontStack, rune.Value));
                    }
                }
            }
        }
    }

    private void ResolveText(
        VectorTileFeatureCollection features,
        double zoom,
        double textScaleFactor,
        List<VectorTileSymbol> symbols,
        ref VectorStyleResolutionCounts counts,
        CancellationToken cancellationToken)
    {
        int nextLabelId = 0;
        foreach (VectorTextStyleLayer layer in _style.TextLayers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VectorStyleVisibilityResult visibility = layer.EvaluateVisibility(zoom);
            if (visibility != VectorStyleVisibilityResult.Visible)
            {
                if (visibility == VectorStyleVisibilityResult.EvaluationFailure)
                {
                    counts.EvaluationFailureCount++;
                }
                continue;
            }
            foreach (VectorTileFeature feature in features.GetSourceLayer(layer.SourceLayer))
            {
                if (layer.Placement == VectorSymbolPlacementKind.Line
                    ? feature.Lines.Length == 0
                    : feature.Points.Length == 0)
                {
                    continue;
                }
                VectorStyleEvaluationContext context = new(feature, zoom);
                VectorStyleFilterResult filter = layer.EvaluateFilter(context);
                if (filter != VectorStyleFilterResult.Match)
                {
                    if (filter == VectorStyleFilterResult.EvaluationFailure)
                    {
                        counts.EvaluationFailureCount++;
                    }
                    continue;
                }
                if (layer.EvaluateText(context, out VectorTextStyle text) !=
                    VectorStyleTextResult.Resolved)
                {
                    counts.EvaluationFailureCount++;
                    continue;
                }
                text = text with { Size = text.Size * textScaleFactor };
                if (layer.Placement == VectorSymbolPlacementKind.Line)
                {
                    foreach (VectorTileLine line in feature.Lines)
                    {
                        AddTextSymbols(
                            layer.Order,
                            default,
                            line.Points,
                            text,
                            nextLabelId++,
                            CreateSymbolGroupId(layer.Order, line.Points),
                            symbols,
                            ref counts);
                    }
                }
                else
                {
                    for (int pointIndex = 0;
                        pointIndex < feature.Points.Length;
                        pointIndex++)
                    {
                        VectorTilePoint point = feature.Points[pointIndex];
                        AddTextSymbols(
                            layer.Order,
                            point,
                            null,
                            text,
                            nextLabelId++,
                            CreateSymbolGroupId(layer.Order, feature, pointIndex),
                            symbols,
                            ref counts);
                    }
                }
            }
        }
    }

    private void AddTextSymbols(
        int order,
        VectorTilePoint point,
        VectorTilePoint[]? linePoints,
        VectorTextStyle text,
        int labelId,
        long symbolGroupId,
        List<VectorTileSymbol> symbols,
        ref VectorStyleResolutionCounts counts)
    {
        const double glyphEmSize = 24;
        double scale = text.Size / glyphEmSize;
        VectorTextPaint paint = text.Paint with
        {
            HaloOffset = Math.Clamp(
                text.Paint.HaloOffset / (8 * scale),
                0,
                0.5),
        };
        string[] lines = text.Text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        List<(VectorGlyph Glyph, VectorSpriteTextureData Texture, double X)>[] shaped =
            new List<(VectorGlyph, VectorSpriteTextureData, double)>[lines.Length];
        double[] widths = new double[lines.Length];
        double[] baselineOffsets = new double[lines.Length];
        double maximumWidth = 0;
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            shaped[lineIndex] = [];
            double pen = 0;
            double minimumTop = double.PositiveInfinity;
            double maximumBottom = double.NegativeInfinity;
            foreach (Rune rune in lines[lineIndex].EnumerateRunes())
            {
                if (rune.Value > char.MaxValue)
                {
                    counts.UnavailableGlyphCount++;
                    continue;
                }
                if (!_glyphAtlas.TryGetOrCreateTexture(
                        new VectorGlyphKey(text.FontStack, rune.Value),
                        out VectorGlyph glyph,
                        out VectorSpriteTextureData? texture))
                {
                    counts.UnavailableGlyphCount++;
                    continue;
                }
                if (texture is not null)
                {
                    shaped[lineIndex].Add((glyph, texture, pen));
                    double top = -glyph.Top * scale;
                    minimumTop = Math.Min(minimumTop, top);
                    maximumBottom = Math.Max(
                        maximumBottom,
                        top + (glyph.Height * scale));
                }
                pen += (glyph.Advance * scale) +
                    (text.LetterSpacing * text.Size);
            }
            widths[lineIndex] = pen;
            maximumWidth = Math.Max(maximumWidth, pen);
            if (double.IsFinite(minimumTop) &&
                double.IsFinite(maximumBottom))
            {
                baselineOffsets[lineIndex] =
                    -(minimumTop + maximumBottom) / 2;
            }
        }

        double lineHeight = text.Size * 1.2;
        double totalHeight = Math.Max(lineHeight, lines.Length * lineHeight);
        GetTextAnchorShift(
            text.Anchor,
            maximumWidth,
            totalHeight,
            text.RadialOffset * text.Size,
            out double anchorX,
            out double anchorY);
        double baseX = anchorX + (text.OffsetX * text.Size);
        double baseY = anchorY + (text.OffsetY * text.Size);
        for (int lineIndex = 0; lineIndex < shaped.Length; lineIndex++)
        {
            double lineX = baseX + ((maximumWidth - widths[lineIndex]) / 2);
            double baseline = baseY +
                (lineIndex * lineHeight) +
                (lineHeight / 2) +
                baselineOffsets[lineIndex];
            foreach ((VectorGlyph glyph, VectorSpriteTextureData texture, double x)
                in shaped[lineIndex])
            {
                double left = lineX + x +
                    ((glyph.Left - VectorGlyph.SdfBuffer) * scale);
                double top = baseline +
                    ((-glyph.Top - VectorGlyph.SdfBuffer) * scale);
                symbols.Add(new VectorTileSymbol(
                    order,
                    point.X,
                    point.Y,
                    texture.TextureId,
                    texture.Width * scale,
                    texture.Height * scale,
                    left + ((texture.Width * scale) / 2),
                    top + ((texture.Height * scale) / 2),
                    VectorSymbolKind.Text,
                    paint,
                    labelId,
                    linePoints,
                    text.LineSpacing,
                    ViewportAligned: text.ViewportAligned,
                    SymbolGroupId: symbolGroupId,
                    SortKey: text.SortKey,
                    AllowOverlap: text.AllowOverlap,
                    IgnorePlacement: text.IgnorePlacement,
                    Optional: text.Optional));
                counts.ResolvedGlyphCount++;
            }
        }
    }

    private static void GetTextAnchorShift(
        string anchor,
        double width,
        double height,
        double radialOffset,
        out double x,
        out double y)
    {
        (x, y) = anchor switch
        {
            "left" => (radialOffset, -height / 2),
            "right" => (-width - radialOffset, -height / 2),
            "top" => (-width / 2, radialOffset),
            "bottom" => (-width / 2, -height - radialOffset),
            "top-left" => (radialOffset, radialOffset),
            "top-right" => (-width - radialOffset, radialOffset),
            "bottom-left" => (radialOffset, -height - radialOffset),
            "bottom-right" => (-width - radialOffset, -height - radialOffset),
            _ => (-width / 2, -height / 2),
        };
    }

    private static double NormalizeTextScaleFactor(double textScaleFactor) =>
        double.IsFinite(textScaleFactor) && textScaleFactor > 0
            ? textScaleFactor
            : 1;

    private VectorStyleResolutionCounts Resolve(
        VectorTileFeatureCollection features,
        double zoom,
        bool createTextures,
        Dictionary<long, VectorSpriteTextureData>? textures,
        List<VectorTileSymbol>? symbols,
        CancellationToken cancellationToken)
    {
        VectorStyleResolutionCounts counts = default;
        foreach (VectorIconStyleLayer layer in _style.IconLayers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VectorStyleVisibilityResult visibility =
                layer.EvaluateVisibility(zoom);
            if (visibility != VectorStyleVisibilityResult.Visible)
            {
                if (visibility == VectorStyleVisibilityResult.EvaluationFailure)
                {
                    counts.EvaluationFailureCount++;
                }
                continue;
            }

            IReadOnlyList<VectorTileFeature> candidates =
                features.GetSourceLayer(layer.SourceLayer);
            foreach (VectorTileFeature feature in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (layer.Placement == VectorSymbolPlacementKind.Line
                    ? feature.Lines.Length == 0
                    : feature.Points.Length == 0)
                {
                    continue;
                }
                VectorStyleEvaluationContext context = new(feature, zoom);
                VectorStyleFilterResult filter = layer.EvaluateFilter(context);
                if (filter != VectorStyleFilterResult.Match)
                {
                    if (filter == VectorStyleFilterResult.EvaluationFailure)
                    {
                        counts.EvaluationFailureCount++;
                    }
                    continue;
                }
                VectorStyleIconResult icon = layer.EvaluateIcon(
                        context,
                        out string spriteName,
                        out double iconSize,
                        out double offsetX,
                        out double offsetY,
                        out double anchorX,
                        out double anchorY,
                        out double spacing,
                        out double rotation,
                        out bool viewportAligned,
                        out VectorIconPaint iconPaint,
                        out VectorIconTextFit textFit,
                        out Vector4 textFitPadding,
                        out double sortKey,
                        out bool allowOverlap,
                        out bool ignorePlacement,
                        out bool optional);
                if (icon != VectorStyleIconResult.Resolved)
                {
                    if (icon == VectorStyleIconResult.EvaluationFailure)
                    {
                        counts.EvaluationFailureCount++;
                    }
                    continue;
                }

                VectorSpriteLookupResult spriteResult = createTextures
                    ? _spriteAtlas.TryGetOrCreateTexture(
                        spriteName,
                        cancellationToken,
                        out VectorSpriteTextureData? texture,
                        out VectorSpriteEntry entry)
                    : _spriteAtlas.TryGetTexture(
                        spriteName,
                        out texture,
                        out entry);
                if (spriteResult != VectorSpriteLookupResult.Found ||
                    texture is null)
                {
                    counts.UnavailableSpriteCount += layer.Placement ==
                        VectorSymbolPlacementKind.Line
                        ? feature.Lines.Length
                        : feature.Points.Length;
                    continue;
                }

                textures?.TryAdd(texture.TextureId, texture);
                if (symbols is null)
                {
                    continue;
                }

                double width = (entry.Width / entry.PixelRatio) * iconSize;
                double height = (entry.Height / entry.PixelRatio) * iconSize;
                double displayOffsetX =
                    (offsetX * iconSize) + (width * anchorX);
                double displayOffsetY =
                    (offsetY * iconSize) + (height * anchorY);
                if (!double.IsFinite(width) ||
                    !double.IsFinite(height) ||
                    width <= 0 ||
                    height <= 0 ||
                    width > 4096 ||
                    height > 4096 ||
                    !double.IsFinite(displayOffsetX) ||
                    !double.IsFinite(displayOffsetY))
                {
                    continue;
                }

                if (layer.Placement == VectorSymbolPlacementKind.Line)
                {
                    foreach (VectorTileLine line in feature.Lines)
                    {
                        symbols.Add(new VectorTileSymbol(
                            layer.Order,
                            0,
                            0,
                            texture.TextureId,
                            width,
                            height,
                            displayOffsetX,
                            displayOffsetY,
                            LinePoints: line.Points,
                            LineSpacing: spacing,
                            Rotation: rotation,
                            ViewportAligned: viewportAligned,
                            IconPaint: iconPaint,
                            SymbolGroupId: CreateSymbolGroupId(
                                layer.Order,
                                line.Points),
                            SortKey: sortKey,
                            AllowOverlap: allowOverlap,
                            IgnorePlacement: ignorePlacement,
                            Optional: optional,
                            TextFit: textFit,
                            TextFitPadding: textFitPadding));
                    }
                }
                else
                {
                    for (int pointIndex = 0;
                        pointIndex < feature.Points.Length;
                        pointIndex++)
                    {
                        VectorTilePoint point = feature.Points[pointIndex];
                        symbols.Add(new VectorTileSymbol(
                            layer.Order,
                            point.X,
                            point.Y,
                            texture.TextureId,
                            width,
                            height,
                            displayOffsetX,
                            displayOffsetY,
                            Rotation: rotation,
                            ViewportAligned: viewportAligned,
                            IconPaint: iconPaint,
                            SymbolGroupId: CreateSymbolGroupId(
                                layer.Order,
                                feature,
                                pointIndex),
                            SortKey: sortKey,
                            AllowOverlap: allowOverlap,
                            IgnorePlacement: ignorePlacement,
                            Optional: optional,
                            TextFit: textFit,
                            TextFitPadding: textFitPadding));
                    }
                }
            }
        }
        return counts;
    }

    private static long CreateSymbolGroupId(
        int order,
        object identity,
        int geometryIndex = 0) =>
        ((long)(uint)RuntimeHelpers.GetHashCode(identity) << 32) |
        ((long)(order & 0xFFFF) << 16) |
        (uint)(geometryIndex & 0xFFFF);

    private struct VectorStyleResolutionCounts
    {
        internal int EvaluationFailureCount;
        internal int UnavailableSpriteCount;
        internal int UnavailableGlyphCount;
        internal int ResolvedGlyphCount;
    }
}

/// <summary>
/// Parses supported Style Spec v8 line and symbol layers.
/// </summary>
internal sealed class VectorStyle
{
    private const int MaximumStyleLayers = 4096;
    private const int MaximumSymbolLayers = 2048;
    private const int MaximumSourceLayerLength = 1024;
    private const int MaximumSourceNameLength = 1024;
    private readonly double[] _zoomStops;
    private readonly int[] _unsupportedLayerCounts;

    private VectorStyle(
        VectorIconStyleLayer[] layers,
        VectorTextStyleLayer[] textLayers,
        VectorLineStyleLayer[] lineLayers,
        VectorFillStyleLayer[] fillLayers,
        VectorBackgroundStyleLayer[] backgroundLayers,
        int[] unsupportedLayerCounts)
    {
        IconLayers = layers;
        TextLayers = textLayers;
        LineLayers = lineLayers;
        FillLayers = fillLayers;
        BackgroundLayers = backgroundLayers;
        _unsupportedLayerCounts = unsupportedLayerCounts;
        List<double> stops = [];
        foreach (VectorIconStyleLayer layer in layers)
        {
            layer.CollectZoomStops(stops);
        }
        foreach (VectorTextStyleLayer layer in textLayers)
        {
            layer.CollectZoomStops(stops);
        }
        foreach (VectorLineStyleLayer layer in lineLayers)
        {
            layer.CollectZoomStops(stops);
        }
        foreach (VectorFillStyleLayer layer in fillLayers)
        {
            layer.CollectZoomStops(stops);
        }
        foreach (VectorBackgroundStyleLayer layer in backgroundLayers)
        {
            layer.CollectZoomStops(stops);
        }
        _zoomStops =
        [
            .. stops
                .Where(double.IsFinite)
                .Distinct()
                .Order(),
        ];
    }

    internal VectorIconStyleLayer[] IconLayers { get; }

    internal VectorTextStyleLayer[] TextLayers { get; }

    internal VectorLineStyleLayer[] LineLayers { get; }

    internal VectorFillStyleLayer[] FillLayers { get; }

    internal VectorBackgroundStyleLayer[] BackgroundLayers { get; }

    internal int LayerCount =>
        IconLayers.Length + TextLayers.Length + LineLayers.Length +
        FillLayers.Length + BackgroundLayers.Length;

    internal int UnsupportedLayerCount =>
        _unsupportedLayerCounts.Sum();

    internal int GetUnsupportedLayerCount(
        VectorStyleLayerParseResult result) =>
        result == VectorStyleLayerParseResult.Parsed
            ? 0
            : _unsupportedLayerCounts[(int)result];

    internal static VectorStyle Parse(ReadOnlyMemory<byte> json)
    {
        return Parse(json, azureBaseSourceOnly: true);
    }

    internal static VectorStyle ParseCustom(ReadOnlyMemory<byte> json)
    {
        return Parse(json, azureBaseSourceOnly: false);
    }

    private static VectorStyle Parse(
        ReadOnlyMemory<byte> json,
        bool azureBaseSourceOnly)
    {
        using JsonDocument document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                MaxDepth = 64,
            });
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("version", out JsonElement version) ||
            !version.TryGetInt32(out int versionNumber) ||
            versionNumber != 8 ||
            !root.TryGetProperty("layers", out JsonElement layers) ||
            layers.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "The vector style is not a supported Style Spec v8 document.");
        }

        List<VectorIconStyleLayer> parsed = [];
        List<VectorTextStyleLayer> parsedText = [];
        List<VectorLineStyleLayer> parsedLines = [];
        List<VectorFillStyleLayer> parsedFills = [];
        List<VectorBackgroundStyleLayer> parsedBackgrounds = [];
        HashSet<string>? baseVectorSources = GetVectorSources(
            root,
            azureBaseSourceOnly);
        int[] unsupportedLayerCounts =
            new int[Enum.GetValues<VectorStyleLayerParseResult>().Length];
        int layerCount = 0;
        foreach (JsonElement layer in layers.EnumerateArray())
        {
            if (++layerCount > MaximumStyleLayers)
            {
                throw new InvalidDataException(
                    "The vector style contains too many layers.");
            }
            if (layer.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "The vector style contains a non-object layer.");
            }
            if (!layer.TryGetProperty("type", out JsonElement type) ||
                type.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    "The vector style contains a layer without a valid type.");
            }
            string? layerType = type.GetString();
            if (string.Equals(layerType, "background", StringComparison.Ordinal))
            {
                VectorStyleLayerParseResult result = TryParseBackgroundLayer(
                    layer,
                    layerCount - 1,
                    out VectorBackgroundStyleLayer? backgroundLayer);
                if (result == VectorStyleLayerParseResult.InvalidDefinition)
                {
                    throw new InvalidDataException(
                        "The vector style contains an invalid background layer.");
                }
                if (result != VectorStyleLayerParseResult.Parsed)
                {
                    unsupportedLayerCounts[(int)result]++;
                }
                else
                {
                    parsedBackgrounds.Add(backgroundLayer!);
                }
                continue;
            }
            if (string.Equals(layerType, "fill", StringComparison.Ordinal))
            {
                VectorStyleLayerParseResult result = TryParseFillLayer(
                    layer,
                    baseVectorSources,
                    layerCount - 1,
                    out VectorFillStyleLayer? fillLayer);
                if (result == VectorStyleLayerParseResult.InvalidDefinition)
                {
                    throw new InvalidDataException(
                        "The vector style contains an invalid fill layer.");
                }
                if (result != VectorStyleLayerParseResult.Parsed)
                {
                    unsupportedLayerCounts[(int)result]++;
                }
                else
                {
                    if (parsedFills.Count >= MaximumSymbolLayers)
                    {
                        throw new InvalidDataException(
                            "The vector style contains too many fill layers.");
                    }
                    parsedFills.Add(fillLayer!);
                }
                continue;
            }
            if (string.Equals(layerType, "line", StringComparison.Ordinal))
            {
                VectorStyleLayerParseResult result = TryParseLineLayer(
                    layer,
                    baseVectorSources,
                    layerCount - 1,
                    out VectorLineStyleLayer? lineLayer);
                if (result == VectorStyleLayerParseResult.InvalidDefinition)
                {
                    throw new InvalidDataException(
                        "The vector style contains an invalid line layer.");
                }
                if (result != VectorStyleLayerParseResult.Parsed)
                {
                    unsupportedLayerCounts[(int)result]++;
                }
                else
                {
                    if (parsedLines.Count >= MaximumSymbolLayers)
                    {
                        throw new InvalidDataException(
                            "The vector style contains too many line layers.");
                    }
                    parsedLines.Add(lineLayer!);
                }
                continue;
            }
            if (!string.Equals(layerType, "symbol", StringComparison.Ordinal) ||
                !layer.TryGetProperty("layout", out JsonElement layout) ||
                layout.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (layout.TryGetProperty("icon-image", out _))
            {
                VectorStyleLayerParseResult result = TryParseLayer(
                    layer,
                    layout,
                    baseVectorSources,
                    layerCount - 1,
                    out VectorIconStyleLayer? symbolLayer);
                if (result != VectorStyleLayerParseResult.Parsed)
                {
                    if (result == VectorStyleLayerParseResult.InvalidDefinition)
                    {
                        throw new InvalidDataException(
                            "The vector style contains an invalid point-symbol layer.");
                    }
                    unsupportedLayerCounts[(int)result]++;
                }
                else
                {
                    if (parsed.Count >= MaximumSymbolLayers)
                    {
                        throw new InvalidDataException(
                            "The vector style contains too many symbol layers.");
                    }
                    parsed.Add(symbolLayer!);
                }
            }

            if (layout.TryGetProperty("text-field", out _))
            {
                VectorStyleLayerParseResult result = TryParseTextLayer(
                    layer,
                    layout,
                    baseVectorSources,
                    layerCount - 1,
                    out VectorTextStyleLayer? textLayer);
                if (result != VectorStyleLayerParseResult.Parsed)
                {
                    if (result == VectorStyleLayerParseResult.InvalidDefinition)
                    {
                        throw new InvalidDataException(
                            "The vector style contains an invalid point-label layer.");
                    }
                    unsupportedLayerCounts[(int)result]++;
                }
                else
                {
                    if (parsedText.Count >= MaximumSymbolLayers)
                    {
                        throw new InvalidDataException(
                            "The vector style contains too many text layers.");
                    }
                    parsedText.Add(textLayer!);
                }
            }
        }
        return new VectorStyle(
            parsed.ToArray(),
            parsedText.ToArray(),
            parsedLines.ToArray(),
            parsedFills.ToArray(),
            parsedBackgrounds.ToArray(),
            unsupportedLayerCounts);
    }

    internal double[] GetPreparationZooms(int tileZoom)
    {
        double minimum = Math.Clamp(tileZoom, 0, MapCamera.MaximumTileZoom);
        double maximum = Math.Min(minimum + 0.999999, MapCamera.MaximumTileZoom);
        List<double> values = [minimum];
        if (maximum != minimum)
        {
            values.Add(maximum);
        }
        foreach (double stop in _zoomStops)
        {
            if (stop >= minimum && stop <= maximum)
            {
                values.Add(Math.Max(minimum, stop - 0.000001));
                values.Add(stop);
                values.Add(Math.Min(maximum, stop + 0.000001));
            }
        }
        return
        [
            .. values
                .Where(double.IsFinite)
                .Distinct()
                .Order()
                .Take(64),
        ];
    }

    private static VectorStyleLayerParseResult TryParseLayer(
        JsonElement layer,
        JsonElement layout,
        IReadOnlySet<string>? baseVectorSources,
        int order,
        out VectorIconStyleLayer? parsed)
    {
        parsed = null;
        if (baseVectorSources is not null &&
            (!layer.TryGetProperty("source", out JsonElement sourceElement) ||
             sourceElement.ValueKind != JsonValueKind.String ||
             sourceElement.GetString() is not string source ||
             !baseVectorSources.Contains(source)))
        {
            return VectorStyleLayerParseResult.UnsupportedVectorSource;
        }
        if (!layer.TryGetProperty("source-layer", out JsonElement sourceLayerElement) ||
            sourceLayerElement.ValueKind != JsonValueKind.String)
        {
            return VectorStyleLayerParseResult.UnsupportedSourceLayer;
        }
        string? sourceLayer = sourceLayerElement.GetString();
        if (string.IsNullOrEmpty(sourceLayer) ||
            sourceLayer.Length > MaximumSourceLayerLength)
        {
            return VectorStyleLayerParseResult.InvalidDefinition;
        }

        if (!layout.TryGetProperty("icon-image", out JsonElement iconImage) ||
            !VectorStyleExpression.TryParseTokenized(
                iconImage,
                out VectorStyleExpression? iconImageExpression))
        {
            return VectorStyleLayerParseResult.UnsupportedExpression;
        }
        if (!TryParseSymbolPlacement(
                layout,
                out VectorSymbolPlacementKind placement))
        {
            return VectorStyleLayerParseResult.UnsupportedSymbolPlacement;
        }

        if (!TryParseOptionalExpression(
                layout,
                "visibility",
                VectorStyleValue.FromString("visible"),
                out VectorStyleExpression visibility) ||
            !TryParseOptionalExpression(
                layout,
                "icon-size",
                VectorStyleValue.FromNumber(1),
                out VectorStyleExpression iconSize) ||
            !TryParseOptionalExpression(
                layout,
                "icon-offset",
                VectorStyleValue.FromArray(
                    [VectorStyleValue.FromNumber(0), VectorStyleValue.FromNumber(0)]),
                out VectorStyleExpression iconOffset) ||
            !TryParseOptionalExpression(
                layout,
                "icon-anchor",
                VectorStyleValue.FromString("center"),
                out VectorStyleExpression iconAnchor) ||
            !TryParseOptionalExpression(
                layout,
                "symbol-spacing",
                VectorStyleValue.FromNumber(250),
                out VectorStyleExpression symbolSpacing) ||
            !TryParseOptionalExpression(
                layout,
                "icon-rotate",
                VectorStyleValue.FromNumber(0),
                out VectorStyleExpression iconRotate) ||
            !TryParseOptionalExpression(
                layout,
                "icon-rotation-alignment",
                VectorStyleValue.FromString("auto"),
                out VectorStyleExpression iconRotationAlignment) ||
            !TryParseOptionalExpression(
                layout,
                "icon-text-fit",
                VectorStyleValue.FromString("none"),
                out VectorStyleExpression iconTextFit) ||
            !TryParseOptionalExpression(
                layout,
                "icon-text-fit-padding",
                VectorStyleValue.FromArray(
                [
                    VectorStyleValue.FromNumber(0),
                    VectorStyleValue.FromNumber(0),
                    VectorStyleValue.FromNumber(0),
                    VectorStyleValue.FromNumber(0),
                ]),
                out VectorStyleExpression iconTextFitPadding) ||
            !TryParseOptionalExpression(
                layout,
                "symbol-sort-key",
                VectorStyleValue.FromNumber(0),
                out VectorStyleExpression symbolSortKey) ||
            !TryParseOptionalExpression(
                layout,
                "icon-allow-overlap",
                VectorStyleValue.FromBoolean(false),
                out VectorStyleExpression iconAllowOverlap) ||
            !TryParseOptionalExpression(
                layout,
                "icon-ignore-placement",
                VectorStyleValue.FromBoolean(false),
                out VectorStyleExpression iconIgnorePlacement) ||
            !TryParseOptionalExpression(
                layout,
                "icon-optional",
                VectorStyleValue.FromBoolean(false),
                out VectorStyleExpression iconOptional))
        {
            return VectorStyleLayerParseResult.UnsupportedExpression;
        }
        JsonElement paint = default;
        bool hasPaint = layer.TryGetProperty("paint", out paint);
        if (hasPaint && paint.ValueKind != JsonValueKind.Object)
        {
            return VectorStyleLayerParseResult.InvalidDefinition;
        }
        if (!TryParseOptionalExpression(
                hasPaint ? paint : default,
                "icon-opacity",
                VectorStyleValue.FromNumber(1),
                out VectorStyleExpression iconOpacity) ||
            !TryParseOptionalExpression(
                hasPaint ? paint : default,
                "icon-color",
                VectorStyleValue.Null,
                out VectorStyleExpression iconColor))
        {
            return VectorStyleLayerParseResult.UnsupportedExpression;
        }

        VectorStyleExpression filter = VectorStyleExpression.Literal(
            VectorStyleValue.FromBoolean(true));
        if (layer.TryGetProperty("filter", out JsonElement filterElement) &&
            !VectorStyleExpression.TryParseFilter(filterElement, out filter))
        {
            return VectorStyleLayerParseResult.UnsupportedExpression;
        }

        double minimumZoom = 0;
        double maximumZoom = MapCamera.MaximumTileZoom + 1;
        if (layer.TryGetProperty("minzoom", out JsonElement minimumElement) &&
            (!minimumElement.TryGetDouble(out minimumZoom) ||
             !double.IsFinite(minimumZoom)))
        {
            return VectorStyleLayerParseResult.InvalidDefinition;
        }
        if (layer.TryGetProperty("maxzoom", out JsonElement maximumElement) &&
            (!maximumElement.TryGetDouble(out maximumZoom) ||
             !double.IsFinite(maximumZoom)))
        {
            return VectorStyleLayerParseResult.InvalidDefinition;
        }
        if (maximumZoom <= minimumZoom)
        {
            return VectorStyleLayerParseResult.InvalidDefinition;
        }

        parsed = new VectorIconStyleLayer(
            order,
            sourceLayer,
            minimumZoom,
            maximumZoom,
            visibility,
            filter,
            placement,
            symbolSpacing,
            iconImageExpression,
            iconSize,
            iconOffset,
            iconAnchor,
            iconRotate,
            iconRotationAlignment,
            iconTextFit,
            iconTextFitPadding,
            symbolSortKey,
            iconAllowOverlap,
            iconIgnorePlacement,
            iconOptional,
            iconOpacity,
            iconColor);
        return VectorStyleLayerParseResult.Parsed;
    }

    private static VectorStyleLayerParseResult TryParseTextLayer(
        JsonElement layer,
        JsonElement layout,
        IReadOnlySet<string>? baseVectorSources,
        int order,
        out VectorTextStyleLayer? parsed)
    {
        parsed = null;
        if (baseVectorSources is not null &&
            (!layer.TryGetProperty("source", out JsonElement sourceElement) ||
             sourceElement.ValueKind != JsonValueKind.String ||
             sourceElement.GetString() is not string source ||
             !baseVectorSources.Contains(source)))
        {
            return VectorStyleLayerParseResult.UnsupportedVectorSource;
        }
        if (!layer.TryGetProperty("source-layer", out JsonElement sourceLayerElement) ||
            sourceLayerElement.ValueKind != JsonValueKind.String)
        {
            return VectorStyleLayerParseResult.UnsupportedSourceLayer;
        }
        string? sourceLayer = sourceLayerElement.GetString();
        if (string.IsNullOrEmpty(sourceLayer) ||
            sourceLayer.Length > MaximumSourceLayerLength)
        {
            return VectorStyleLayerParseResult.InvalidDefinition;
        }
        if (!TryParseSymbolPlacement(
                layout,
                out VectorSymbolPlacementKind placement))
        {
            return VectorStyleLayerParseResult.UnsupportedSymbolPlacement;
        }
        if (!layout.TryGetProperty("text-field", out JsonElement textField) ||
            !VectorStyleExpression.TryParseTokenized(
                textField,
                out VectorStyleExpression textFieldExpression) ||
            !TryParseOptionalExpression(
                layout,
                "visibility",
                VectorStyleValue.FromString("visible"),
                out VectorStyleExpression visibility) ||
            !TryParseOptionalLiteralExpression(
                layout,
                "text-font",
                VectorStyleValue.FromArray(
                    [VectorStyleValue.FromString("Roboto-Regular")]),
                out VectorStyleExpression textFont) ||
            !TryParseOptionalExpression(
                layout,
                "text-size",
                VectorStyleValue.FromNumber(16),
                out VectorStyleExpression textSize) ||
            !TryParseOptionalExpression(
                layout,
                "text-offset",
                VectorStyleValue.FromArray(
                    [VectorStyleValue.FromNumber(0), VectorStyleValue.FromNumber(0)]),
                out VectorStyleExpression textOffset) ||
            !TryParseOptionalExpression(
                layout,
                "text-anchor",
                VectorStyleValue.FromString("center"),
                out VectorStyleExpression textAnchor) ||
            !TryParseOptionalExpression(
                layout,
                "text-radial-offset",
                VectorStyleValue.FromNumber(0),
                out VectorStyleExpression textRadialOffset) ||
            !TryParseOptionalExpression(
                layout,
                "text-letter-spacing",
                VectorStyleValue.FromNumber(0),
                out VectorStyleExpression textLetterSpacing) ||
            !TryParseOptionalExpression(
                layout,
                "text-transform",
                VectorStyleValue.FromString("none"),
                out VectorStyleExpression textTransform) ||
            !TryParseOptionalExpression(
                layout,
                "text-rotation-alignment",
                VectorStyleValue.FromString("auto"),
                out VectorStyleExpression textRotationAlignment) ||
            !TryParseOptionalExpression(
                layout,
                "symbol-spacing",
                VectorStyleValue.FromNumber(250),
                out VectorStyleExpression symbolSpacing) ||
            !TryParseOptionalExpression(
                layout,
                "symbol-sort-key",
                VectorStyleValue.FromNumber(0),
                out VectorStyleExpression symbolSortKey) ||
            !TryParseOptionalExpression(
                layout,
                "text-allow-overlap",
                VectorStyleValue.FromBoolean(false),
                out VectorStyleExpression textAllowOverlap) ||
            !TryParseOptionalExpression(
                layout,
                "text-ignore-placement",
                VectorStyleValue.FromBoolean(false),
                out VectorStyleExpression textIgnorePlacement) ||
            !TryParseOptionalExpression(
                layout,
                "text-optional",
                VectorStyleValue.FromBoolean(false),
                out VectorStyleExpression textOptional))
        {
            return VectorStyleLayerParseResult.UnsupportedExpression;
        }

        VectorStyleExpression textVariableAnchor =
            VectorStyleExpression.Literal(VectorStyleValue.Null);
        if (layout.TryGetProperty(
                "text-variable-anchor",
                out JsonElement variableAnchor) &&
            !VectorStyleExpression.TryParseLiteralExpression(
                variableAnchor,
                out textVariableAnchor))
        {
            return VectorStyleLayerParseResult.UnsupportedExpression;
        }

        JsonElement paint = default;
        bool hasPaint = layer.TryGetProperty("paint", out paint);
        if (hasPaint && paint.ValueKind != JsonValueKind.Object)
        {
            return VectorStyleLayerParseResult.InvalidDefinition;
        }
        if (!TryParseOptionalExpression(
                hasPaint ? paint : default,
                "text-color",
                VectorStyleValue.FromString("#000000"),
                out VectorStyleExpression textColor) ||
            !TryParseOptionalExpression(
                hasPaint ? paint : default,
                "text-halo-color",
                VectorStyleValue.FromString("#00000000"),
                out VectorStyleExpression textHaloColor) ||
            !TryParseOptionalExpression(
                hasPaint ? paint : default,
                "text-halo-width",
                VectorStyleValue.FromNumber(0),
                out VectorStyleExpression textHaloWidth))
        {
            return VectorStyleLayerParseResult.UnsupportedExpression;
        }

        VectorStyleExpression filter = VectorStyleExpression.Literal(
            VectorStyleValue.FromBoolean(true));
        if (layer.TryGetProperty("filter", out JsonElement filterElement) &&
            !VectorStyleExpression.TryParseFilter(filterElement, out filter))
        {
            return VectorStyleLayerParseResult.UnsupportedExpression;
        }
        double minimumZoom = 0;
        double maximumZoom = MapCamera.MaximumTileZoom + 1;
        if (layer.TryGetProperty("minzoom", out JsonElement minimumElement) &&
            (!minimumElement.TryGetDouble(out minimumZoom) ||
             !double.IsFinite(minimumZoom)) ||
            layer.TryGetProperty("maxzoom", out JsonElement maximumElement) &&
            (!maximumElement.TryGetDouble(out maximumZoom) ||
             !double.IsFinite(maximumZoom)) ||
            maximumZoom <= minimumZoom)
        {
            return VectorStyleLayerParseResult.InvalidDefinition;
        }

        parsed = new VectorTextStyleLayer(
            order,
            sourceLayer,
            minimumZoom,
            maximumZoom,
            visibility,
            filter,
            placement,
            symbolSpacing,
            textFieldExpression,
            textFont,
            textSize,
            textOffset,
            textAnchor,
            textVariableAnchor,
            textRadialOffset,
            textLetterSpacing,
            textTransform,
            textRotationAlignment,
            textColor,
            textHaloColor,
            textHaloWidth,
            symbolSortKey,
            textAllowOverlap,
            textIgnorePlacement,
            textOptional);
        return VectorStyleLayerParseResult.Parsed;
    }

    private static bool TryParseSymbolPlacement(
        JsonElement layout,
        out VectorSymbolPlacementKind placement)
    {
        placement = VectorSymbolPlacementKind.Point;
        if (!layout.TryGetProperty(
                "symbol-placement",
                out JsonElement symbolPlacement))
        {
            return true;
        }
        if (symbolPlacement.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        placement = symbolPlacement.GetString() switch
        {
            "point" => VectorSymbolPlacementKind.Point,
            "line" => VectorSymbolPlacementKind.Line,
            _ => (VectorSymbolPlacementKind)(-1),
        };
        return (int)placement >= 0;
    }

    private static VectorStyleLayerParseResult TryParseFillLayer(
        JsonElement layer,
        IReadOnlySet<string>? baseVectorSources,
        int order,
        out VectorFillStyleLayer? parsed)
    {
        parsed = null;
        if (baseVectorSources is not null &&
            (!layer.TryGetProperty("source", out JsonElement sourceElement) ||
             sourceElement.ValueKind != JsonValueKind.String ||
             sourceElement.GetString() is not string source ||
             !baseVectorSources.Contains(source)))
        {
            return VectorStyleLayerParseResult.UnsupportedVectorSource;
        }
        if (!layer.TryGetProperty("source-layer", out JsonElement sourceLayerElement) ||
            sourceLayerElement.ValueKind != JsonValueKind.String)
        {
            return VectorStyleLayerParseResult.UnsupportedSourceLayer;
        }
        string? sourceLayer = sourceLayerElement.GetString();
        if (string.IsNullOrEmpty(sourceLayer) ||
            sourceLayer.Length > MaximumSourceLayerLength)
        {
            return VectorStyleLayerParseResult.InvalidDefinition;
        }

        JsonElement layout = default;
        if (layer.TryGetProperty("layout", out layout) &&
            layout.ValueKind != JsonValueKind.Object)
        {
            return VectorStyleLayerParseResult.InvalidDefinition;
        }
        JsonElement paint = default;
        if (layer.TryGetProperty("paint", out paint) &&
            paint.ValueKind != JsonValueKind.Object)
        {
            return VectorStyleLayerParseResult.InvalidDefinition;
        }
        if (!TryParseOptionalExpression(
                layout,
                "visibility",
                VectorStyleValue.FromString("visible"),
                out VectorStyleExpression visibility) ||
            !TryParseOptionalExpression(
                paint,
                "fill-color",
                VectorStyleValue.FromString("#000000"),
                out VectorStyleExpression fillColor) ||
            !TryParseOptionalExpression(
                paint,
                "fill-opacity",
                VectorStyleValue.FromNumber(1),
                out VectorStyleExpression fillOpacity) ||
            !TryParseOptionalExpression(
                paint,
                "fill-outline-color",
                VectorStyleValue.Null,
                out VectorStyleExpression fillOutlineColor) ||
            !TryParseOptionalExpression(
                paint,
                "fill-pattern",
                VectorStyleValue.Null,
                out VectorStyleExpression fillPattern))
        {
            return VectorStyleLayerParseResult.UnsupportedExpression;
        }

        VectorStyleExpression filter = VectorStyleExpression.Literal(
            VectorStyleValue.FromBoolean(true));
        if (layer.TryGetProperty("filter", out JsonElement filterElement) &&
            !VectorStyleExpression.TryParseFilter(filterElement, out filter))
        {
            return VectorStyleLayerParseResult.UnsupportedExpression;
        }
        double minimumZoom = 0;
        double maximumZoom = MapCamera.MaximumTileZoom + 1;
        if (layer.TryGetProperty("minzoom", out JsonElement minimumElement) &&
            (!minimumElement.TryGetDouble(out minimumZoom) ||
             !double.IsFinite(minimumZoom)) ||
            layer.TryGetProperty("maxzoom", out JsonElement maximumElement) &&
            (!maximumElement.TryGetDouble(out maximumZoom) ||
             !double.IsFinite(maximumZoom)) ||
            maximumZoom <= minimumZoom)
        {
            return VectorStyleLayerParseResult.InvalidDefinition;
        }

        parsed = new VectorFillStyleLayer(
            order,
            sourceLayer,
            minimumZoom,
            maximumZoom,
            visibility,
            filter,
            fillColor,
            fillOpacity,
            fillOutlineColor,
            fillPattern);
        return VectorStyleLayerParseResult.Parsed;
    }

    private static VectorStyleLayerParseResult TryParseBackgroundLayer(
        JsonElement layer,
        int order,
        out VectorBackgroundStyleLayer? parsed)
    {
        parsed = null;
        if (order != 0)
        {
            return VectorStyleLayerParseResult.UnsupportedExpression;
        }
        JsonElement layout = default;
        bool hasLayout = layer.TryGetProperty("layout", out layout);
        if (hasLayout && layout.ValueKind != JsonValueKind.Object)
        {
            return VectorStyleLayerParseResult.InvalidDefinition;
        }
        JsonElement paint = default;
        bool hasPaint = layer.TryGetProperty("paint", out paint);
        if (hasPaint && paint.ValueKind != JsonValueKind.Object)
        {
            return VectorStyleLayerParseResult.InvalidDefinition;
        }
        if (hasPaint &&
            paint.TryGetProperty("background-pattern", out JsonElement pattern) &&
            pattern.ValueKind != JsonValueKind.Null)
        {
            return VectorStyleLayerParseResult.UnsupportedExpression;
        }
        if (!TryParseOptionalExpression(
                hasLayout ? layout : default,
                "visibility",
                VectorStyleValue.FromString("visible"),
                out VectorStyleExpression visibility) ||
            !TryParseOptionalExpression(
                hasPaint ? paint : default,
                "background-color",
                VectorStyleValue.FromString("#000000"),
                out VectorStyleExpression color) ||
            !TryParseOptionalExpression(
                hasPaint ? paint : default,
                "background-opacity",
                VectorStyleValue.FromNumber(1),
                out VectorStyleExpression opacity))
        {
            return VectorStyleLayerParseResult.UnsupportedExpression;
        }

        parsed = new VectorBackgroundStyleLayer(
            order,
            visibility,
            color,
            opacity);
        return VectorStyleLayerParseResult.Parsed;
    }

    private static VectorStyleLayerParseResult TryParseLineLayer(
        JsonElement layer,
        IReadOnlySet<string>? baseVectorSources,
        int order,
        out VectorLineStyleLayer? parsed)
    {
        parsed = null;
        if (baseVectorSources is not null &&
            (!layer.TryGetProperty("source", out JsonElement sourceElement) ||
             sourceElement.ValueKind != JsonValueKind.String ||
             sourceElement.GetString() is not string source ||
             !baseVectorSources.Contains(source)))
        {
            return VectorStyleLayerParseResult.UnsupportedVectorSource;
        }
        if (!layer.TryGetProperty("source-layer", out JsonElement sourceLayerElement) ||
            sourceLayerElement.ValueKind != JsonValueKind.String)
        {
            return VectorStyleLayerParseResult.UnsupportedSourceLayer;
        }
        string? sourceLayer = sourceLayerElement.GetString();
        if (string.IsNullOrEmpty(sourceLayer) ||
            sourceLayer.Length > MaximumSourceLayerLength)
        {
            return VectorStyleLayerParseResult.InvalidDefinition;
        }

        JsonElement layout = default;
        if (layer.TryGetProperty("layout", out layout) &&
            layout.ValueKind != JsonValueKind.Object)
        {
            return VectorStyleLayerParseResult.InvalidDefinition;
        }
        JsonElement paint = default;
        if (layer.TryGetProperty("paint", out paint) &&
            paint.ValueKind != JsonValueKind.Object)
        {
            return VectorStyleLayerParseResult.InvalidDefinition;
        }
        if (!TryParseOptionalExpression(
                layout,
                "visibility",
                VectorStyleValue.FromString("visible"),
                out VectorStyleExpression visibility) ||
            !TryParseOptionalExpression(
                layout,
                "line-cap",
                VectorStyleValue.FromString("butt"),
                out VectorStyleExpression lineCap) ||
            !TryParseOptionalExpression(
                layout,
                "line-join",
                VectorStyleValue.FromString("miter"),
                out VectorStyleExpression lineJoin) ||
            !TryParseOptionalExpression(
                paint,
                "line-color",
                VectorStyleValue.FromString("#000000"),
                out VectorStyleExpression lineColor) ||
            !TryParseOptionalExpression(
                paint,
                "line-opacity",
                VectorStyleValue.FromNumber(1),
                out VectorStyleExpression lineOpacity) ||
            !TryParseOptionalExpression(
                paint,
                "line-width",
                VectorStyleValue.FromNumber(1),
                out VectorStyleExpression lineWidth) ||
            !TryParseOptionalExpression(
                paint,
                "line-offset",
                VectorStyleValue.FromNumber(0),
                out VectorStyleExpression lineOffset) ||
            !TryParseOptionalExpression(
                paint,
                "line-gap-width",
                VectorStyleValue.FromNumber(0),
                out VectorStyleExpression lineGapWidth) ||
            !TryParseOptionalExpression(
                paint,
                "line-blur",
                VectorStyleValue.FromNumber(0),
                out VectorStyleExpression lineBlur) ||
            !TryParseOptionalExpression(
                layout,
                "line-miter-limit",
                VectorStyleValue.FromNumber(2),
                out VectorStyleExpression lineMiterLimit) ||
            !TryParseOptionalExpression(
                paint,
                "line-dasharray",
                VectorStyleValue.FromArray([]),
                out VectorStyleExpression lineDashArray) ||
            !TryParseOptionalExpression(
                paint,
                "line-pattern",
                VectorStyleValue.Null,
                out VectorStyleExpression linePattern) ||
            !TryParseLineGradient(
                paint,
                out ImmutableArray<VectorLineGradientStop> lineGradient))
        {
            return VectorStyleLayerParseResult.UnsupportedExpression;
        }

        VectorStyleExpression filter = VectorStyleExpression.Literal(
            VectorStyleValue.FromBoolean(true));
        if (layer.TryGetProperty("filter", out JsonElement filterElement) &&
            !VectorStyleExpression.TryParseFilter(filterElement, out filter))
        {
            return VectorStyleLayerParseResult.UnsupportedExpression;
        }
        double minimumZoom = 0;
        double maximumZoom = MapCamera.MaximumTileZoom + 1;
        if (layer.TryGetProperty("minzoom", out JsonElement minimumElement) &&
            (!minimumElement.TryGetDouble(out minimumZoom) ||
             !double.IsFinite(minimumZoom)) ||
            layer.TryGetProperty("maxzoom", out JsonElement maximumElement) &&
            (!maximumElement.TryGetDouble(out maximumZoom) ||
             !double.IsFinite(maximumZoom)) ||
            maximumZoom <= minimumZoom)
        {
            return VectorStyleLayerParseResult.InvalidDefinition;
        }

        parsed = new VectorLineStyleLayer(
            order,
            sourceLayer,
            minimumZoom,
            maximumZoom,
            visibility,
            filter,
            lineColor,
            lineOpacity,
            lineWidth,
            lineCap,
            lineJoin,
            lineDashArray,
            linePattern,
            lineOffset,
            lineGapWidth,
            lineBlur,
            lineMiterLimit,
            lineGradient);
        return VectorStyleLayerParseResult.Parsed;
    }

    private static HashSet<string>? GetVectorSources(
        JsonElement root,
        bool azureBaseSourceOnly)
    {
        if (!root.TryGetProperty("sources", out JsonElement sources))
        {
            return null;
        }
        if (sources.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The vector style contains an invalid sources object.");
        }

        HashSet<string> result = new(StringComparer.Ordinal);
        foreach (JsonProperty source in sources.EnumerateObject())
        {
            if (source.Name.Length > MaximumSourceNameLength ||
                source.Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "The vector style contains an invalid source.");
            }
            if (source.Value.TryGetProperty("type", out JsonElement type) &&
                type.ValueKind == JsonValueKind.String &&
                string.Equals(type.GetString(), "vector", StringComparison.Ordinal) &&
                (!azureBaseSourceOnly ||
                 (source.Value.TryGetProperty("url", out JsonElement url) &&
                  url.ValueKind == JsonValueKind.String &&
                  url.GetString() is string sourceUrl &&
                  sourceUrl.Contains(
                      "tilesetId=microsoft.base",
                      StringComparison.Ordinal))))
            {
                result.Add(source.Name);
            }
        }
        if (result.Count == 0)
        {
            throw new InvalidDataException(
                azureBaseSourceOnly
                    ? "The vector style does not define the Azure base vector source."
                    : "The custom vector style does not define a vector source.");
        }
        if (!azureBaseSourceOnly && result.Count > 1)
        {
            throw new InvalidDataException(
                "The custom vector style defines more than one vector source.");
        }
        return result;
    }

    private static bool TryParseOptionalExpression(
        JsonElement owner,
        string propertyName,
        VectorStyleValue defaultValue,
        out VectorStyleExpression expression)
    {
        if (owner.ValueKind != JsonValueKind.Object ||
            !owner.TryGetProperty(propertyName, out JsonElement value))
        {
            expression = VectorStyleExpression.Literal(defaultValue);
            return true;
        }
        return VectorStyleExpression.TryParseStyleValue(
            value,
            defaultValue,
            out expression);
    }

    private static bool TryParseLineGradient(
        JsonElement paint,
        out ImmutableArray<VectorLineGradientStop> gradient)
    {
        gradient = [];
        if (paint.ValueKind != JsonValueKind.Object ||
            !paint.TryGetProperty("line-gradient", out JsonElement value))
        {
            return true;
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        JsonElement[] items = value.EnumerateArray().ToArray();
        if (items.Length < 7 ||
            (items.Length & 1) == 0 ||
            items[0].ValueKind != JsonValueKind.String ||
            !string.Equals(
                items[0].GetString(),
                "interpolate",
                StringComparison.Ordinal) ||
            items[1].ValueKind != JsonValueKind.Array ||
            items[1].GetArrayLength() != 1 ||
            items[1][0].ValueKind != JsonValueKind.String ||
            !string.Equals(
                items[1][0].GetString(),
                "linear",
                StringComparison.Ordinal) ||
            items[2].ValueKind != JsonValueKind.Array ||
            items[2].GetArrayLength() != 1 ||
            items[2][0].ValueKind != JsonValueKind.String ||
            !string.Equals(
                items[2][0].GetString(),
                "line-progress",
                StringComparison.Ordinal))
        {
            return false;
        }

        ImmutableArray<VectorLineGradientStop>.Builder stops =
            ImmutableArray.CreateBuilder<VectorLineGradientStop>(
                (items.Length - 3) / 2);
        double previousOffset = double.NegativeInfinity;
        for (int index = 3; index + 1 < items.Length; index += 2)
        {
            if (!items[index].TryGetDouble(out double offset) ||
                !double.IsFinite(offset) ||
                offset < 0 ||
                offset > 1 ||
                offset <= previousOffset ||
                items[index + 1].ValueKind != JsonValueKind.String ||
                !VectorTextStyleLayer.TryParseColor(
                    VectorStyleValue.FromString(
                        items[index + 1].GetString()!),
                    out Vector4 color))
            {
                return false;
            }
            stops.Add(new VectorLineGradientStop(offset, color));
            previousOffset = offset;
        }
        if (stops.Count < 2)
        {
            return false;
        }
        gradient = stops.MoveToImmutable();
        return true;
    }

    private static bool TryParseOptionalLiteralExpression(
        JsonElement owner,
        string propertyName,
        VectorStyleValue defaultValue,
        out VectorStyleExpression expression)
    {
        if (owner.ValueKind != JsonValueKind.Object ||
            !owner.TryGetProperty(propertyName, out JsonElement value))
        {
            expression = VectorStyleExpression.Literal(defaultValue);
            return true;
        }
        return VectorStyleExpression.TryParseLiteralExpression(
            value,
            out expression);
    }
}

internal sealed class VectorIconStyleLayer(
    int order,
    string sourceLayer,
    double minimumZoom,
    double maximumZoom,
    VectorStyleExpression visibility,
    VectorStyleExpression filter,
    VectorSymbolPlacementKind placement,
    VectorStyleExpression symbolSpacing,
    VectorStyleExpression iconImage,
    VectorStyleExpression iconSize,
    VectorStyleExpression iconOffset,
    VectorStyleExpression iconAnchor,
    VectorStyleExpression iconRotate,
    VectorStyleExpression iconRotationAlignment,
    VectorStyleExpression iconTextFit,
    VectorStyleExpression iconTextFitPadding,
    VectorStyleExpression symbolSortKey,
    VectorStyleExpression iconAllowOverlap,
    VectorStyleExpression iconIgnorePlacement,
    VectorStyleExpression iconOptional,
    VectorStyleExpression iconOpacity,
    VectorStyleExpression iconColor)
{
    internal int Order { get; } = order;

    internal string SourceLayer { get; } = sourceLayer;

    internal VectorSymbolPlacementKind Placement { get; } = placement;

    internal VectorStyleVisibilityResult EvaluateVisibility(double zoom)
    {
        if (zoom < minimumZoom || zoom >= maximumZoom)
        {
            return VectorStyleVisibilityResult.Hidden;
        }
        VectorStyleEvaluationContext context = new(null, zoom);
        if (!visibility.TryEvaluate(context, out VectorStyleValue value) ||
            value.Kind != VectorStyleValueKind.String)
        {
            return VectorStyleVisibilityResult.EvaluationFailure;
        }
        return string.Equals(value.StringValue, "none", StringComparison.Ordinal)
            ? VectorStyleVisibilityResult.Hidden
            : VectorStyleVisibilityResult.Visible;
    }

    internal VectorStyleFilterResult EvaluateFilter(
        VectorStyleEvaluationContext context)
    {
        if (!filter.TryEvaluate(context, out VectorStyleValue result) ||
            result.Kind != VectorStyleValueKind.Boolean)
        {
            return VectorStyleFilterResult.EvaluationFailure;
        }
        return result.BooleanValue
            ? VectorStyleFilterResult.Match
            : VectorStyleFilterResult.NoMatch;
    }

    internal VectorStyleIconResult EvaluateIcon(
        VectorStyleEvaluationContext context,
        out string spriteName,
        out double size,
        out double offsetX,
        out double offsetY,
        out double anchorX,
        out double anchorY,
        out double spacing,
        out double rotation,
        out bool viewportAligned,
        out VectorIconPaint paint,
        out VectorIconTextFit textFit,
        out Vector4 textFitPadding,
        out double sortKey,
        out bool allowOverlap,
        out bool ignorePlacement,
        out bool optional)
    {
        spriteName = string.Empty;
        size = 0;
        offsetX = 0;
        offsetY = 0;
        anchorX = 0;
        anchorY = 0;
        spacing = 0;
        rotation = 0;
        viewportAligned = false;
        paint = VectorIconPaint.Default;
        textFit = VectorIconTextFit.None;
        textFitPadding = default;
        sortKey = 0;
        allowOverlap = false;
        ignorePlacement = false;
        optional = false;
        if (!iconImage.TryEvaluate(context, out VectorStyleValue image) ||
            image.Kind != VectorStyleValueKind.String)
        {
            return VectorStyleIconResult.EvaluationFailure;
        }
        if (string.IsNullOrEmpty(image.StringValue))
        {
            return VectorStyleIconResult.NoIcon;
        }
        if (!iconSize.TryEvaluate(context, out VectorStyleValue sizeValue) ||
            !sizeValue.TryGetNumber(out size) ||
            !double.IsFinite(size))
        {
            return VectorStyleIconResult.EvaluationFailure;
        }
        if (size <= 0)
        {
            return VectorStyleIconResult.NoIcon;
        }
        if (!iconOffset.TryEvaluate(context, out VectorStyleValue offset) ||
            offset.Kind != VectorStyleValueKind.Array ||
            offset.ArrayValue is null ||
            offset.ArrayValue.Length != 2 ||
            !offset.ArrayValue[0].TryGetNumber(out offsetX) ||
            !offset.ArrayValue[1].TryGetNumber(out offsetY) ||
            !double.IsFinite(offsetX) ||
            !double.IsFinite(offsetY) ||
            !iconAnchor.TryEvaluate(context, out VectorStyleValue anchor) ||
            anchor.Kind != VectorStyleValueKind.String ||
            !TryGetAnchorOffsets(anchor.StringValue, out anchorX, out anchorY) ||
            !symbolSpacing.TryEvaluate(context, out VectorStyleValue spacingValue) ||
            !spacingValue.TryGetNumber(out spacing) ||
            !double.IsFinite(spacing) ||
            spacing <= 0 ||
            !iconRotate.TryEvaluate(context, out VectorStyleValue rotationValue) ||
            !rotationValue.TryGetNumber(out rotation) ||
            !double.IsFinite(rotation) ||
            !iconRotationAlignment.TryEvaluate(
                context,
                out VectorStyleValue alignmentValue) ||
            !TryGetViewportAlignment(
                alignmentValue,
                Placement,
                out viewportAligned) ||
            !iconTextFit.TryEvaluate(context, out VectorStyleValue fitValue) ||
            fitValue.Kind != VectorStyleValueKind.String ||
            !TryGetTextFit(fitValue.StringValue, out textFit) ||
            !iconTextFitPadding.TryEvaluate(
                context,
                out VectorStyleValue paddingValue) ||
            !TryGetTextFitPadding(paddingValue, out textFitPadding) ||
            !symbolSortKey.TryEvaluate(context, out VectorStyleValue sortValue) ||
            !sortValue.TryGetNumber(out sortKey) ||
            !double.IsFinite(sortKey) ||
            !TryEvaluateBoolean(
                iconAllowOverlap,
                context,
                out allowOverlap) ||
            !TryEvaluateBoolean(
                iconIgnorePlacement,
                context,
                out ignorePlacement) ||
            !TryEvaluateBoolean(iconOptional, context, out optional) ||
            !iconOpacity.TryEvaluate(context, out VectorStyleValue opacityValue) ||
            !opacityValue.TryGetNumber(out double opacity) ||
            !double.IsFinite(opacity) ||
            !iconColor.TryEvaluate(context, out VectorStyleValue colorValue) ||
            !TryGetIconPaint(colorValue, out paint))
        {
            return VectorStyleIconResult.EvaluationFailure;
        }
        spriteName = image.StringValue;
        rotation = rotation * Math.PI / 180;
        opacity = Math.Clamp(opacity, 0, 1);
        paint = paint with
        {
            Color = paint.Color * (float)opacity,
        };
        if (paint.Color.W <= 0)
        {
            return VectorStyleIconResult.NoIcon;
        }
        return VectorStyleIconResult.Resolved;
    }

    private static bool TryGetViewportAlignment(
        VectorStyleValue value,
        VectorSymbolPlacementKind placement,
        out bool viewportAligned)
    {
        viewportAligned = value.StringValue switch
        {
            "viewport" => true,
            "map" => false,
            "auto" => placement == VectorSymbolPlacementKind.Point,
            _ => false,
        };
        return value.Kind == VectorStyleValueKind.String &&
            value.StringValue is "viewport" or "map" or "auto";
    }

    private static bool TryEvaluateBoolean(
        VectorStyleExpression expression,
        VectorStyleEvaluationContext context,
        out bool value)
    {
        value = false;
        if (!expression.TryEvaluate(context, out VectorStyleValue result) ||
            result.Kind != VectorStyleValueKind.Boolean)
        {
            return false;
        }
        value = result.BooleanValue;
        return true;
    }

    private static bool TryGetTextFit(
        string? value,
        out VectorIconTextFit fit)
    {
        fit = value switch
        {
            "none" => VectorIconTextFit.None,
            "width" => VectorIconTextFit.Width,
            "height" => VectorIconTextFit.Height,
            "both" => VectorIconTextFit.Both,
            _ => (VectorIconTextFit)(-1),
        };
        return (int)fit >= 0;
    }

    private static bool TryGetTextFitPadding(
        VectorStyleValue value,
        out Vector4 padding)
    {
        padding = default;
        if (value is not
            {
                Kind: VectorStyleValueKind.Array,
                ArrayValue.Length: 4
            } ||
            value.ArrayValue is not { } values ||
            !values[0].TryGetNumber(out double top) ||
            !values[1].TryGetNumber(out double right) ||
            !values[2].TryGetNumber(out double bottom) ||
            !values[3].TryGetNumber(out double left) ||
            !double.IsFinite(top) ||
            !double.IsFinite(right) ||
            !double.IsFinite(bottom) ||
            !double.IsFinite(left) ||
            top < 0 ||
            right < 0 ||
            bottom < 0 ||
            left < 0)
        {
            return false;
        }
        padding = new Vector4(
            (float)top,
            (float)right,
            (float)bottom,
            (float)left);
        return true;
    }

    private static bool TryGetIconPaint(
        VectorStyleValue value,
        out VectorIconPaint paint)
    {
        if (value.Kind == VectorStyleValueKind.Null)
        {
            paint = VectorIconPaint.Default;
            return true;
        }
        if (!VectorTextStyleLayer.TryParseColor(value, out Vector4 color))
        {
            paint = default;
            return false;
        }
        paint = new VectorIconPaint(color, true);
        return true;
    }

    private static bool TryGetAnchorOffsets(
        string? anchor,
        out double x,
        out double y)
    {
        (x, y) = anchor switch
        {
            "center" => (0, 0),
            "left" => (0.5, 0),
            "right" => (-0.5, 0),
            "top" => (0, 0.5),
            "bottom" => (0, -0.5),
            "top-left" => (0.5, 0.5),
            "top-right" => (-0.5, 0.5),
            "bottom-left" => (0.5, -0.5),
            "bottom-right" => (-0.5, -0.5),
            _ => (0, 0),
        };
        return anchor is "center" or "left" or "right" or "top" or
            "bottom" or "top-left" or "top-right" or "bottom-left" or
            "bottom-right";
    }

    internal void CollectZoomStops(List<double> stops)
    {
        stops.Add(minimumZoom);
        stops.Add(maximumZoom);
        visibility.CollectZoomStops(stops);
        filter.CollectZoomStops(stops);
        symbolSpacing.CollectZoomStops(stops);
        iconImage.CollectZoomStops(stops);
        iconSize.CollectZoomStops(stops);
        iconOffset.CollectZoomStops(stops);
        iconAnchor.CollectZoomStops(stops);
        iconRotate.CollectZoomStops(stops);
        iconTextFit.CollectZoomStops(stops);
        iconTextFitPadding.CollectZoomStops(stops);
        symbolSortKey.CollectZoomStops(stops);
        iconAllowOverlap.CollectZoomStops(stops);
        iconIgnorePlacement.CollectZoomStops(stops);
        iconOptional.CollectZoomStops(stops);
        iconOpacity.CollectZoomStops(stops);
        iconColor.CollectZoomStops(stops);
    }
}

internal sealed class VectorBackgroundStyleLayer(
    int order,
    VectorStyleExpression visibility,
    VectorStyleExpression backgroundColor,
    VectorStyleExpression backgroundOpacity)
{
    internal int Order { get; } = order;

    internal VectorStyleFillResult Evaluate(
        double zoom,
        out VectorFillStyle style)
    {
        style = default;
        VectorStyleEvaluationContext context = new(null, zoom);
        if (!visibility.TryEvaluate(context, out VectorStyleValue visibilityValue) ||
            visibilityValue.Kind != VectorStyleValueKind.String)
        {
            return VectorStyleFillResult.EvaluationFailure;
        }
        if (visibilityValue.StringValue == "none")
        {
            return VectorStyleFillResult.Hidden;
        }
        if (visibilityValue.StringValue != "visible" ||
            !backgroundColor.TryEvaluate(context, out VectorStyleValue colorValue) ||
            !VectorTextStyleLayer.TryParseColor(colorValue, out Vector4 color) ||
            !backgroundOpacity.TryEvaluate(
                context,
                out VectorStyleValue opacityValue) ||
            !opacityValue.TryGetNumber(out double opacity) ||
            !double.IsFinite(opacity) ||
            opacity is < 0 or > 1)
        {
            return VectorStyleFillResult.EvaluationFailure;
        }

        style = new VectorFillStyle(
            color * (float)opacity,
            null,
            0,
            0,
            0);
        return VectorStyleFillResult.Resolved;
    }

    internal void CollectZoomStops(List<double> stops)
    {
        visibility.CollectZoomStops(stops);
        backgroundColor.CollectZoomStops(stops);
        backgroundOpacity.CollectZoomStops(stops);
    }
}

internal sealed class VectorFillStyleLayer(
    int order,
    string sourceLayer,
    double minimumZoom,
    double maximumZoom,
    VectorStyleExpression visibility,
    VectorStyleExpression filter,
    VectorStyleExpression fillColor,
    VectorStyleExpression fillOpacity,
    VectorStyleExpression fillOutlineColor,
    VectorStyleExpression fillPattern)
{
    internal int Order { get; } = order;

    internal string SourceLayer { get; } = sourceLayer;

    internal VectorStyleVisibilityResult EvaluateVisibility(double zoom)
    {
        if (zoom < minimumZoom || zoom >= maximumZoom)
        {
            return VectorStyleVisibilityResult.Hidden;
        }
        VectorStyleEvaluationContext context = new(null, zoom);
        if (!visibility.TryEvaluate(context, out VectorStyleValue value) ||
            value.Kind != VectorStyleValueKind.String)
        {
            return VectorStyleVisibilityResult.EvaluationFailure;
        }
        return string.Equals(value.StringValue, "none", StringComparison.Ordinal)
            ? VectorStyleVisibilityResult.Hidden
            : VectorStyleVisibilityResult.Visible;
    }

    internal VectorStyleFilterResult EvaluateFilter(
        VectorStyleEvaluationContext context)
    {
        if (!filter.TryEvaluate(context, out VectorStyleValue value) ||
            value.Kind != VectorStyleValueKind.Boolean)
        {
            return VectorStyleFilterResult.EvaluationFailure;
        }
        return value.BooleanValue
            ? VectorStyleFilterResult.Match
            : VectorStyleFilterResult.NoMatch;
    }

    internal VectorStyleFillResult EvaluateFill(
        VectorStyleEvaluationContext context,
        out VectorFillStyle result,
        out string? patternName)
    {
        result = default;
        patternName = null;
        if (!fillColor.TryEvaluate(context, out VectorStyleValue colorValue) ||
            !VectorTextStyleLayer.TryParseColor(colorValue, out Vector4 color) ||
            !fillOpacity.TryEvaluate(context, out VectorStyleValue opacityValue) ||
            !opacityValue.TryGetNumber(out double opacity) ||
            !double.IsFinite(opacity) ||
            !fillOutlineColor.TryEvaluate(
                context,
                out VectorStyleValue outlineValue) ||
            !TryResolveOptionalColor(
                outlineValue,
                out Vector4? outlineColor) ||
            !fillPattern.TryEvaluate(context, out VectorStyleValue patternValue) ||
            !TryResolvePattern(patternValue, out patternName))
        {
            return VectorStyleFillResult.EvaluationFailure;
        }
        opacity = Math.Clamp(opacity, 0, 1);
        if (opacity <= 0 ||
            patternName is null && color.W <= 0 &&
            (outlineColor is not Vector4 visibleOutline ||
             visibleOutline.W <= 0))
        {
            return VectorStyleFillResult.Hidden;
        }
        color *= (float)opacity;
        if (outlineColor is Vector4 outline)
        {
            outlineColor = outline * (float)opacity;
        }
        result = new VectorFillStyle(
            patternName is null ? color : Vector4.Zero,
            outlineColor,
            Opacity: opacity);
        return VectorStyleFillResult.Resolved;
    }

    internal void CollectZoomStops(List<double> stops)
    {
        stops.Add(minimumZoom);
        stops.Add(maximumZoom);
        visibility.CollectZoomStops(stops);
        filter.CollectZoomStops(stops);
        fillColor.CollectZoomStops(stops);
        fillOpacity.CollectZoomStops(stops);
        fillOutlineColor.CollectZoomStops(stops);
        fillPattern.CollectZoomStops(stops);
    }

    private static bool TryResolveOptionalColor(
        VectorStyleValue value,
        out Vector4? color)
    {
        color = null;
        if (value.Kind == VectorStyleValueKind.Null)
        {
            return true;
        }
        if (!VectorTextStyleLayer.TryParseColor(value, out Vector4 resolved))
        {
            return false;
        }
        color = resolved;
        return true;
    }

    private static bool TryResolvePattern(
        VectorStyleValue value,
        out string? patternName)
    {
        patternName = null;
        if (value.Kind == VectorStyleValueKind.Null)
        {
            return true;
        }
        if (value.Kind != VectorStyleValueKind.String ||
            string.IsNullOrWhiteSpace(value.StringValue))
        {
            return false;
        }
        patternName = value.StringValue;
        return true;
    }
}

internal sealed class VectorLineStyleLayer(
    int order,
    string sourceLayer,
    double minimumZoom,
    double maximumZoom,
    VectorStyleExpression visibility,
    VectorStyleExpression filter,
    VectorStyleExpression lineColor,
    VectorStyleExpression lineOpacity,
    VectorStyleExpression lineWidth,
    VectorStyleExpression lineCap,
    VectorStyleExpression lineJoin,
    VectorStyleExpression lineDashArray,
    VectorStyleExpression linePattern,
    VectorStyleExpression lineOffset,
    VectorStyleExpression lineGapWidth,
    VectorStyleExpression lineBlur,
    VectorStyleExpression lineMiterLimit,
    ImmutableArray<VectorLineGradientStop> lineGradient)
{
    internal int Order { get; } = order;

    internal string SourceLayer { get; } = sourceLayer;

    internal VectorStyleVisibilityResult EvaluateVisibility(double zoom)
    {
        if (zoom < minimumZoom || zoom >= maximumZoom)
        {
            return VectorStyleVisibilityResult.Hidden;
        }
        VectorStyleEvaluationContext context = new(null, zoom);
        if (!visibility.TryEvaluate(context, out VectorStyleValue value) ||
            value.Kind != VectorStyleValueKind.String)
        {
            return VectorStyleVisibilityResult.EvaluationFailure;
        }
        return string.Equals(value.StringValue, "none", StringComparison.Ordinal)
            ? VectorStyleVisibilityResult.Hidden
            : VectorStyleVisibilityResult.Visible;
    }

    internal VectorStyleFilterResult EvaluateFilter(
        VectorStyleEvaluationContext context)
    {
        if (!filter.TryEvaluate(context, out VectorStyleValue value) ||
            value.Kind != VectorStyleValueKind.Boolean)
        {
            return VectorStyleFilterResult.EvaluationFailure;
        }
        return value.BooleanValue
            ? VectorStyleFilterResult.Match
            : VectorStyleFilterResult.NoMatch;
    }

    internal VectorStyleLineResult EvaluateLine(
        VectorStyleEvaluationContext context,
        out VectorLineStyle result)
    {
        result = default;
        if (!lineColor.TryEvaluate(context, out VectorStyleValue colorValue) ||
            !VectorTextStyleLayer.TryParseColor(colorValue, out Vector4 color) ||
            !lineOpacity.TryEvaluate(context, out VectorStyleValue opacityValue) ||
            !opacityValue.TryGetNumber(out double opacity) ||
            !double.IsFinite(opacity) ||
            !lineWidth.TryEvaluate(context, out VectorStyleValue widthValue) ||
            !widthValue.TryGetNumber(out double width) ||
            !double.IsFinite(width) ||
            !lineOffset.TryEvaluate(context, out VectorStyleValue offsetValue) ||
            !offsetValue.TryGetNumber(out double offset) ||
            !double.IsFinite(offset) ||
            !lineGapWidth.TryEvaluate(context, out VectorStyleValue gapValue) ||
            !gapValue.TryGetNumber(out double gapWidth) ||
            !double.IsFinite(gapWidth) ||
            !lineBlur.TryEvaluate(context, out VectorStyleValue blurValue) ||
            !blurValue.TryGetNumber(out double blur) ||
            !double.IsFinite(blur) ||
            !lineMiterLimit.TryEvaluate(
                context,
                out VectorStyleValue miterLimitValue) ||
            !miterLimitValue.TryGetNumber(out double miterLimit) ||
            !double.IsFinite(miterLimit) ||
            !lineCap.TryEvaluate(context, out VectorStyleValue capValue) ||
            capValue.Kind != VectorStyleValueKind.String ||
            !TryGetCap(capValue.StringValue, out VectorLineCap cap) ||
            !lineJoin.TryEvaluate(context, out VectorStyleValue joinValue) ||
            joinValue.Kind != VectorStyleValueKind.String ||
            !TryGetJoin(joinValue.StringValue, out VectorLineJoin join))
        {
            return VectorStyleLineResult.EvaluationFailure;
        }
        if (opacity <= 0 ||
            width <= 0 ||
            lineGradient.IsDefaultOrEmpty && color.W <= 0)
        {
            return VectorStyleLineResult.Hidden;
        }
        if (width > 256 ||
            Math.Abs(offset) > 4096 ||
            gapWidth < 0 ||
            gapWidth > 512 ||
            blur < 0 ||
            blur > 128 ||
            miterLimit < 1 ||
            miterLimit > 16)
        {
            return VectorStyleLineResult.EvaluationFailure;
        }
        if (!lineDashArray.TryEvaluate(context, out VectorStyleValue dashValue) ||
            !TryResolveDashArray(
                dashValue,
                width,
                out ImmutableArray<double> dashArray))
        {
            return VectorStyleLineResult.EvaluationFailure;
        }
        color *= (float)Math.Clamp(opacity, 0, 1);
        ImmutableArray<VectorLineGradientStop> gradient =
            lineGradient.IsDefaultOrEmpty
                ? []
                : [.. lineGradient.Select(stop => stop with
                {
                    Color = stop.Color * (float)Math.Clamp(opacity, 0, 1),
                })];
        result = new VectorLineStyle(
            color,
            width,
            cap,
            join,
            dashArray,
            offset,
            gapWidth,
            blur,
            miterLimit,
            gradient);
        return VectorStyleLineResult.Resolved;
    }

    internal bool TryEvaluatePattern(
        VectorStyleEvaluationContext context,
        out string? patternName,
        out double opacity)
    {
        patternName = null;
        opacity = 0;
        if (!linePattern.TryEvaluate(context, out VectorStyleValue patternValue))
        {
            return false;
        }
        if (patternValue.Kind == VectorStyleValueKind.Null)
        {
            return true;
        }
        if (patternValue.Kind != VectorStyleValueKind.String ||
            string.IsNullOrWhiteSpace(patternValue.StringValue) ||
            !lineOpacity.TryEvaluate(context, out VectorStyleValue opacityValue) ||
            !opacityValue.TryGetNumber(out opacity) ||
            !double.IsFinite(opacity))
        {
            return false;
        }
        patternName = patternValue.StringValue;
        opacity = Math.Clamp(opacity, 0, 1);
        return true;
    }

    internal void CollectZoomStops(List<double> stops)
    {
        stops.Add(minimumZoom);
        stops.Add(maximumZoom);
        visibility.CollectZoomStops(stops);
        filter.CollectZoomStops(stops);
        lineColor.CollectZoomStops(stops);
        lineOpacity.CollectZoomStops(stops);
        lineWidth.CollectZoomStops(stops);
        lineCap.CollectZoomStops(stops);
        lineJoin.CollectZoomStops(stops);
        lineDashArray.CollectZoomStops(stops);
        linePattern.CollectZoomStops(stops);
        lineOffset.CollectZoomStops(stops);
        lineGapWidth.CollectZoomStops(stops);
        lineBlur.CollectZoomStops(stops);
        lineMiterLimit.CollectZoomStops(stops);
    }

    private static bool TryResolveDashArray(
        VectorStyleValue value,
        double lineWidth,
        out ImmutableArray<double> dashArray)
    {
        dashArray = [];
        if (value.Kind == VectorStyleValueKind.Null ||
            value is { Kind: VectorStyleValueKind.Array, ArrayValue.Length: 0 })
        {
            return true;
        }
        if (value is not
            {
                Kind: VectorStyleValueKind.Array,
                ArrayValue.Length: >= 2
            } ||
            value.ArrayValue is not { } values)
        {
            return false;
        }
        int normalizedCount = (values.Length & 1) == 0
            ? values.Length
            : values.Length * 2;
        double[] resolved = new double[normalizedCount];
        bool hasPositiveLength = false;
        for (int index = 0; index < normalizedCount; index++)
        {
            if (!values[index % values.Length].TryGetNumber(out double length) ||
                !double.IsFinite(length) ||
                length < 0)
            {
                return false;
            }
            resolved[index] = length * lineWidth;
            hasPositiveLength |= resolved[index] > 0;
        }
        if (!hasPositiveLength)
        {
            return false;
        }
        dashArray = [.. resolved];
        return true;
    }

    private static bool TryGetCap(string? value, out VectorLineCap cap)
    {
        cap = value switch
        {
            "butt" => VectorLineCap.Butt,
            "round" => VectorLineCap.Round,
            "square" => VectorLineCap.Square,
            _ => (VectorLineCap)(-1),
        };
        return (int)cap >= 0;
    }

    private static bool TryGetJoin(string? value, out VectorLineJoin join)
    {
        join = value switch
        {
            "bevel" => VectorLineJoin.Bevel,
            "round" => VectorLineJoin.Round,
            "miter" => VectorLineJoin.Miter,
            _ => (VectorLineJoin)(-1),
        };
        return (int)join >= 0;
    }
}

internal sealed class VectorTextStyleLayer(
    int order,
    string sourceLayer,
    double minimumZoom,
    double maximumZoom,
    VectorStyleExpression visibility,
    VectorStyleExpression filter,
    VectorSymbolPlacementKind placement,
    VectorStyleExpression symbolSpacing,
    VectorStyleExpression textField,
    VectorStyleExpression textFont,
    VectorStyleExpression textSize,
    VectorStyleExpression textOffset,
    VectorStyleExpression textAnchor,
    VectorStyleExpression textVariableAnchor,
    VectorStyleExpression textRadialOffset,
    VectorStyleExpression textLetterSpacing,
    VectorStyleExpression textTransform,
    VectorStyleExpression textRotationAlignment,
    VectorStyleExpression textColor,
    VectorStyleExpression textHaloColor,
    VectorStyleExpression textHaloWidth,
    VectorStyleExpression symbolSortKey,
    VectorStyleExpression textAllowOverlap,
    VectorStyleExpression textIgnorePlacement,
    VectorStyleExpression textOptional)
{
    internal int Order { get; } = order;

    internal string SourceLayer { get; } = sourceLayer;

    internal VectorSymbolPlacementKind Placement { get; } = placement;

    internal VectorStyleVisibilityResult EvaluateVisibility(double zoom)
    {
        if (zoom < minimumZoom || zoom >= maximumZoom)
        {
            return VectorStyleVisibilityResult.Hidden;
        }
        VectorStyleEvaluationContext context = new(null, zoom);
        if (!visibility.TryEvaluate(context, out VectorStyleValue value) ||
            value.Kind != VectorStyleValueKind.String)
        {
            return VectorStyleVisibilityResult.EvaluationFailure;
        }
        return string.Equals(value.StringValue, "none", StringComparison.Ordinal)
            ? VectorStyleVisibilityResult.Hidden
            : VectorStyleVisibilityResult.Visible;
    }

    internal VectorStyleFilterResult EvaluateFilter(
        VectorStyleEvaluationContext context)
    {
        if (!filter.TryEvaluate(context, out VectorStyleValue value) ||
            value.Kind != VectorStyleValueKind.Boolean)
        {
            return VectorStyleFilterResult.EvaluationFailure;
        }
        return value.BooleanValue
            ? VectorStyleFilterResult.Match
            : VectorStyleFilterResult.NoMatch;
    }

    internal VectorStyleTextResult EvaluateText(
        VectorStyleEvaluationContext context,
        out VectorTextStyle result)
    {
        result = default;
        if (!textField.TryEvaluate(context, out VectorStyleValue field) ||
            field.Kind != VectorStyleValueKind.String)
        {
            return VectorStyleTextResult.EvaluationFailure;
        }
        string text = field.StringValue ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return VectorStyleTextResult.NoText;
        }
        if (!textFont.TryEvaluate(context, out VectorStyleValue fontValue) ||
            !TryGetFontStack(fontValue, out string fontStack) ||
            !textSize.TryEvaluate(context, out VectorStyleValue sizeValue) ||
            !sizeValue.TryGetNumber(out double size) ||
            !double.IsFinite(size) ||
            !textOffset.TryEvaluate(context, out VectorStyleValue offsetValue) ||
            !TryGetPair(offsetValue, out double offsetX, out double offsetY) ||
            !textAnchor.TryEvaluate(context, out VectorStyleValue anchorValue) ||
            anchorValue.Kind != VectorStyleValueKind.String ||
            !TryGetAnchor(anchorValue.StringValue, out string anchor) ||
            !textRadialOffset.TryEvaluate(
                context,
                out VectorStyleValue radialOffsetValue) ||
            !radialOffsetValue.TryGetNumber(out double radialOffset) ||
            !double.IsFinite(radialOffset) ||
            !textLetterSpacing.TryEvaluate(
                context,
                out VectorStyleValue letterSpacingValue) ||
            !letterSpacingValue.TryGetNumber(out double letterSpacing) ||
            !double.IsFinite(letterSpacing) ||
            !textTransform.TryEvaluate(context, out VectorStyleValue transformValue) ||
            transformValue.Kind != VectorStyleValueKind.String ||
            !textRotationAlignment.TryEvaluate(
                context,
                out VectorStyleValue alignmentValue) ||
            !TryGetViewportAlignment(
                alignmentValue,
                Placement,
                out bool viewportAligned) ||
            !textColor.TryEvaluate(context, out VectorStyleValue colorValue) ||
            !TryParseColor(colorValue, out Vector4 color) ||
            !textHaloColor.TryEvaluate(context, out VectorStyleValue haloColorValue) ||
            !TryParseColor(haloColorValue, out Vector4 haloColor) ||
            !textHaloWidth.TryEvaluate(context, out VectorStyleValue haloWidthValue) ||
            !haloWidthValue.TryGetNumber(out double haloWidth) ||
            !double.IsFinite(haloWidth) ||
            !symbolSpacing.TryEvaluate(context, out VectorStyleValue spacingValue) ||
            !spacingValue.TryGetNumber(out double spacing) ||
            !double.IsFinite(spacing) ||
            spacing <= 0 ||
            !symbolSortKey.TryEvaluate(context, out VectorStyleValue sortValue) ||
            !sortValue.TryGetNumber(out double sortKey) ||
            !double.IsFinite(sortKey) ||
            !TryEvaluateBoolean(
                textAllowOverlap,
                context,
                out bool allowOverlap) ||
            !TryEvaluateBoolean(
                textIgnorePlacement,
                context,
                out bool ignorePlacement) ||
            !TryEvaluateBoolean(textOptional, context, out bool optional))
        {
            return VectorStyleTextResult.EvaluationFailure;
        }
        if (size <= 0 || size > 256 || haloWidth < 0 || haloWidth > 32)
        {
            return VectorStyleTextResult.NoText;
        }
        if (textVariableAnchor.TryEvaluate(
                context,
                out VectorStyleValue variableAnchorValue) &&
            variableAnchorValue.Kind != VectorStyleValueKind.Null &&
            TryGetFirstAnchor(variableAnchorValue, out string variableAnchor))
        {
            anchor = variableAnchor;
        }
        text = transformValue.StringValue switch
        {
            "uppercase" => text.ToUpperInvariant(),
            "lowercase" => text.ToLowerInvariant(),
            "none" => text,
            _ => string.Empty,
        };
        if (text.Length == 0)
        {
            return VectorStyleTextResult.EvaluationFailure;
        }
        result = new VectorTextStyle(
            text,
            fontStack,
            size,
            offsetX,
            offsetY,
            anchor,
            radialOffset,
            letterSpacing,
            new VectorTextPaint(color, haloColor, haloWidth),
            spacing,
            sortKey,
            allowOverlap,
            ignorePlacement,
            optional,
            viewportAligned);
        return VectorStyleTextResult.Resolved;
    }

    internal VectorStyleTextResult EvaluateAccessibilityText(
        VectorStyleEvaluationContext context,
        out string text,
        out double prominence)
    {
        text = string.Empty;
        prominence = 0;
        if (!textField.TryEvaluate(context, out VectorStyleValue field) ||
            field.Kind != VectorStyleValueKind.String ||
            !textSize.TryEvaluate(context, out VectorStyleValue sizeValue) ||
            !sizeValue.TryGetNumber(out double size) ||
            !double.IsFinite(size) ||
            !textTransform.TryEvaluate(context, out VectorStyleValue transformValue) ||
            transformValue.Kind != VectorStyleValueKind.String ||
            !symbolSortKey.TryEvaluate(context, out VectorStyleValue sortValue) ||
            !sortValue.TryGetNumber(out double sortKey) ||
            !double.IsFinite(sortKey))
        {
            return VectorStyleTextResult.EvaluationFailure;
        }

        text = field.StringValue ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) || size <= 0 || size > 256)
        {
            text = string.Empty;
            return VectorStyleTextResult.NoText;
        }

        text = transformValue.StringValue switch
        {
            "uppercase" => text.ToUpperInvariant(),
            "lowercase" => text.ToLowerInvariant(),
            "none" => text,
            _ => string.Empty,
        };
        text = text.Trim();
        if (text.Length == 0)
        {
            return VectorStyleTextResult.EvaluationFailure;
        }
        if (text.Length > 256)
        {
            text = text[..256];
        }

        prominence = size - sortKey;
        return VectorStyleTextResult.Resolved;
    }

    internal void CollectZoomStops(List<double> stops)
    {
        stops.Add(minimumZoom);
        stops.Add(maximumZoom);
        visibility.CollectZoomStops(stops);
        filter.CollectZoomStops(stops);
        symbolSpacing.CollectZoomStops(stops);
        textField.CollectZoomStops(stops);
        textFont.CollectZoomStops(stops);
        textSize.CollectZoomStops(stops);
        textOffset.CollectZoomStops(stops);
        textAnchor.CollectZoomStops(stops);
        textVariableAnchor.CollectZoomStops(stops);
        textRadialOffset.CollectZoomStops(stops);
        textLetterSpacing.CollectZoomStops(stops);
        textTransform.CollectZoomStops(stops);
        textRotationAlignment.CollectZoomStops(stops);
        textColor.CollectZoomStops(stops);
        textHaloColor.CollectZoomStops(stops);
        textHaloWidth.CollectZoomStops(stops);
        symbolSortKey.CollectZoomStops(stops);
        textAllowOverlap.CollectZoomStops(stops);
        textIgnorePlacement.CollectZoomStops(stops);
        textOptional.CollectZoomStops(stops);
    }

    private static bool TryEvaluateBoolean(
        VectorStyleExpression expression,
        VectorStyleEvaluationContext context,
        out bool value)
    {
        value = false;
        if (!expression.TryEvaluate(context, out VectorStyleValue result) ||
            result.Kind != VectorStyleValueKind.Boolean)
        {
            return false;
        }
        value = result.BooleanValue;
        return true;
    }

    private static bool TryGetViewportAlignment(
        VectorStyleValue value,
        VectorSymbolPlacementKind placement,
        out bool viewportAligned)
    {
        viewportAligned = value.StringValue switch
        {
            "viewport" => true,
            "map" => false,
            "auto" => placement == VectorSymbolPlacementKind.Point,
            _ => false,
        };
        return value.Kind == VectorStyleValueKind.String &&
            value.StringValue is "viewport" or "map" or "auto";
    }

    private static bool TryGetFontStack(
        VectorStyleValue value,
        out string fontStack)
    {
        if (value.Kind == VectorStyleValueKind.String &&
            !string.IsNullOrWhiteSpace(value.StringValue))
        {
            fontStack = value.StringValue;
            return true;
        }
        if (value.Kind == VectorStyleValueKind.Array &&
            value.ArrayValue is { Length: > 0 } fonts &&
            fonts[0].Kind == VectorStyleValueKind.String &&
            !string.IsNullOrWhiteSpace(fonts[0].StringValue))
        {
            fontStack = fonts[0].StringValue!;
            return true;
        }
        fontStack = string.Empty;
        return false;
    }

    private static bool TryGetPair(
        VectorStyleValue value,
        out double x,
        out double y)
    {
        if (value.Kind == VectorStyleValueKind.Array &&
            value.ArrayValue is { Length: 2 } values &&
            values[0].TryGetNumber(out x) &&
            values[1].TryGetNumber(out y) &&
            double.IsFinite(x) &&
            double.IsFinite(y))
        {
            return true;
        }
        x = 0;
        y = 0;
        return false;
    }

    private static bool TryGetFirstAnchor(
        VectorStyleValue value,
        out string anchor)
    {
        if (value.Kind == VectorStyleValueKind.String)
        {
            return TryGetAnchor(value.StringValue, out anchor);
        }
        if (value.Kind == VectorStyleValueKind.Array &&
            value.ArrayValue is { Length: > 0 } anchors &&
            anchors[0].Kind == VectorStyleValueKind.String)
        {
            return TryGetAnchor(anchors[0].StringValue, out anchor);
        }
        anchor = string.Empty;
        return false;
    }

    private static bool TryGetAnchor(string? value, out string anchor)
    {
        if (value is "center" or "left" or "right" or "top" or "bottom" or
            "top-left" or "top-right" or "bottom-left" or "bottom-right")
        {
            anchor = value;
            return true;
        }
        anchor = string.Empty;
        return false;
    }

    internal static bool TryParseColor(
        VectorStyleValue value,
        out Vector4 color)
    {
        color = default;
        if (value.Kind != VectorStyleValueKind.String ||
            value.StringValue is not string text)
        {
            return false;
        }
        if (TryParseFunctionalColor(text, out color))
        {
            return true;
        }
        if (text.Length is not (7 or 9) ||
            text[0] != '#' ||
            !byte.TryParse(
                text.AsSpan(1, 2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out byte red) ||
            !byte.TryParse(
                text.AsSpan(3, 2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out byte green) ||
            !byte.TryParse(
                text.AsSpan(5, 2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out byte blue))
        {
            return false;
        }
        byte alpha = byte.MaxValue;
        if (text.Length == 9 &&
            !byte.TryParse(
                text.AsSpan(7, 2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out alpha))
        {
            return false;
        }
        float normalizedAlpha = alpha / 255f;
        color = new Vector4(
            (red / 255f) * normalizedAlpha,
            (green / 255f) * normalizedAlpha,
            (blue / 255f) * normalizedAlpha,
            normalizedAlpha);
        return true;
    }

    private static bool TryParseFunctionalColor(
        string text,
        out Vector4 color)
    {
        color = default;
        bool hasAlpha;
        int prefixLength;
        if (text.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase) &&
            text.EndsWith(')'))
        {
            hasAlpha = true;
            prefixLength = 5;
        }
        else if (text.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) &&
            text.EndsWith(')'))
        {
            hasAlpha = false;
            prefixLength = 4;
        }
        else
        {
            return false;
        }

        string[] components = text[prefixLength..^1].Split(
            ',',
            StringSplitOptions.TrimEntries);
        if (components.Length != (hasAlpha ? 4 : 3) ||
            !double.TryParse(
                components[0],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double red) ||
            !double.TryParse(
                components[1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double green) ||
            !double.TryParse(
                components[2],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double blue) ||
            red is < 0 or > 255 ||
            green is < 0 or > 255 ||
            blue is < 0 or > 255)
        {
            return false;
        }

        double alpha = 1;
        if (hasAlpha &&
            (!double.TryParse(
                components[3],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out alpha) ||
             alpha is < 0 or > 1))
        {
            return false;
        }

        float normalizedAlpha = (float)alpha;
        color = new Vector4(
            (float)(red / 255) * normalizedAlpha,
            (float)(green / 255) * normalizedAlpha,
            (float)(blue / 255) * normalizedAlpha,
            normalizedAlpha);
        return true;
    }
}

internal readonly record struct VectorTextStyle(
    string Text,
    string FontStack,
    double Size,
    double OffsetX,
    double OffsetY,
    string Anchor,
    double RadialOffset,
    double LetterSpacing,
    VectorTextPaint Paint,
    double LineSpacing,
    double SortKey,
    bool AllowOverlap,
    bool IgnorePlacement,
    bool Optional,
    bool ViewportAligned);

internal enum VectorSymbolPlacementKind
{
    Point,
    Line,
}

internal enum VectorStyleTextResult
{
    Resolved,
    NoText,
    EvaluationFailure,
}

internal enum VectorStyleLineResult
{
    Resolved,
    Hidden,
    EvaluationFailure,
}

internal enum VectorStyleFillResult
{
    Resolved,
    Hidden,
    EvaluationFailure,
}

internal enum VectorStyleLayerParseResult
{
    Parsed,
    UnsupportedVectorSource,
    UnsupportedSourceLayer,
    UnsupportedSymbolPlacement,
    UnsupportedTextFit,
    UnsupportedIconRotation,
    UnsupportedExpression,
    InvalidDefinition,
}

internal enum VectorStyleVisibilityResult
{
    Visible,
    Hidden,
    EvaluationFailure,
}

internal enum VectorStyleFilterResult
{
    Match,
    NoMatch,
    EvaluationFailure,
}

internal enum VectorStyleIconResult
{
    Resolved,
    NoIcon,
    EvaluationFailure,
}

internal readonly record struct VectorStyleEvaluationContext(
    VectorTileFeature? Feature,
    double Zoom);

internal enum VectorStyleExpressionOperator
{
    Literal,
    TokenString,
    Get,
    Has,
    GeometryType,
    Zoom,
    Equal,
    Not,
    All,
    Any,
    In,
    Case,
    Coalesce,
    Concat,
    Format,
    Match,
    Step,
    InterpolateLinear,
    InterpolateExponential,
    Let,
    Var,
    ToString,
    Multiply,
    Number,
}

/// <summary>
/// Small fail-closed evaluator for supported Style Spec expression forms.
/// </summary>
internal sealed class VectorStyleExpression
{
    private const int MaximumDepth = 32;
    private const int MaximumNodes = 4096;
    private const int MaximumArguments = 1024;
    private const int MaximumStringLength = 16 * 1024;
    private readonly VectorStyleExpressionOperator _operator;
    private readonly VectorStyleValue _literal;
    private readonly string? _name;
    private readonly VectorStyleExpression[] _arguments;
    private readonly bool _containsZoom;

    private VectorStyleExpression(
        VectorStyleExpressionOperator expressionOperator,
        VectorStyleValue literal,
        string? name,
        VectorStyleExpression[] arguments)
    {
        _operator = expressionOperator;
        _literal = literal;
        _name = name;
        _arguments = arguments;
        _containsZoom =
            expressionOperator == VectorStyleExpressionOperator.Zoom ||
            arguments.Any(argument => argument._containsZoom);
    }

    internal static VectorStyleExpression Literal(VectorStyleValue value) =>
        new(VectorStyleExpressionOperator.Literal, value, null, []);

    internal static bool TryParse(
        JsonElement element,
        out VectorStyleExpression expression)
    {
        int nodeCount = 0;
        return TryParse(element, 0, ref nodeCount, out expression);
    }

    internal static bool TryParseFilter(
        JsonElement element,
        out VectorStyleExpression expression)
    {
        int nodeCount = 0;
        return TryParseLegacyFilter(
                element,
                0,
                ref nodeCount,
                out expression) ||
            TryParse(element, out expression);
    }

    internal static bool TryParseStyleValue(
        JsonElement element,
        out VectorStyleExpression expression)
    {
        return TryParseStyleValue(
            element,
            VectorStyleValue.Null,
            out expression);
    }

    internal static bool TryParseStyleValue(
        JsonElement element,
        VectorStyleValue defaultValue,
        out VectorStyleExpression expression)
    {
        if (TryParse(element, out expression))
        {
            return true;
        }
        int nodeCount = 0;
        return TryParseLegacyFunction(
            element,
            0,
            ref nodeCount,
            defaultValue,
            out expression);
    }

    internal static bool TryParseTokenized(
        JsonElement element,
        out VectorStyleExpression expression)
    {
        if (element.ValueKind == JsonValueKind.String &&
            element.GetString() is string text &&
            text.Contains('{') &&
            text.Contains('}'))
        {
            expression = new VectorStyleExpression(
                VectorStyleExpressionOperator.TokenString,
                VectorStyleValue.FromString(text),
                null,
                []);
            return true;
        }
        return TryParseStyleValue(element, out expression);
    }

    internal static bool TryParseLiteralExpression(
        JsonElement element,
        out VectorStyleExpression expression) =>
        TryParseLiteral(element, out expression);

    internal bool TryEvaluate(
        VectorStyleEvaluationContext context,
        out VectorStyleValue value) =>
        TryEvaluate(context, variables: null, out value);

    internal void CollectZoomStops(List<double> values)
    {
        if (!_containsZoom)
        {
            return;
        }
        switch (_operator)
        {
            case VectorStyleExpressionOperator.Step:
                _arguments[0].CollectZoomStops(values);
                if (_arguments[0]._containsZoom)
                {
                    for (int index = 2; index < _arguments.Length; index += 2)
                    {
                        _arguments[index].CollectLiteralNumbers(values);
                    }
                }
                for (int index = 1; index < _arguments.Length; index += 2)
                {
                    _arguments[index].CollectZoomStops(values);
                }
                break;
            case VectorStyleExpressionOperator.InterpolateLinear:
            case VectorStyleExpressionOperator.InterpolateExponential:
                _arguments[0].CollectZoomStops(values);
                if (_arguments[0]._containsZoom)
                {
                    for (int index = 1; index < _arguments.Length; index += 2)
                    {
                        _arguments[index].CollectLiteralNumbers(values);
                    }
                }
                for (int index = 2; index < _arguments.Length; index += 2)
                {
                    _arguments[index].CollectZoomStops(values);
                }
                break;
            case VectorStyleExpressionOperator.Equal:
                foreach (VectorStyleExpression argument in _arguments)
                {
                    argument.CollectZoomStops(values);
                }
                if (_arguments[0]._containsZoom)
                {
                    _arguments[1].CollectLiteralNumbers(values);
                }
                if (_arguments[1]._containsZoom)
                {
                    _arguments[0].CollectLiteralNumbers(values);
                }
                break;
            case VectorStyleExpressionOperator.Match:
                _arguments[0].CollectZoomStops(values);
                if (_arguments[0]._containsZoom)
                {
                    for (int index = 1; index < _arguments.Length - 1; index += 2)
                    {
                        _arguments[index].CollectLiteralNumbers(values);
                    }
                }
                for (int index = 2; index < _arguments.Length; index += 2)
                {
                    _arguments[index].CollectZoomStops(values);
                }
                _arguments[^1].CollectZoomStops(values);
                break;
            default:
                foreach (VectorStyleExpression argument in _arguments)
                {
                    argument.CollectZoomStops(values);
                }
                break;
        }
    }

    private void CollectLiteralNumbers(List<double> values)
    {
        if (_operator != VectorStyleExpressionOperator.Literal)
        {
            return;
        }
        if (_literal.TryGetNumber(out double number))
        {
            values.Add(number);
            return;
        }
        if (_literal.ArrayValue is not null)
        {
            foreach (VectorStyleValue item in _literal.ArrayValue)
            {
                if (item.TryGetNumber(out number))
                {
                    values.Add(number);
                }
            }
        }
    }

    private static bool TryParse(
        JsonElement element,
        int depth,
        ref int nodeCount,
        out VectorStyleExpression expression)
    {
        expression = null!;
        if (depth > MaximumDepth || ++nodeCount > MaximumNodes)
        {
            return false;
        }
        if (element.ValueKind != JsonValueKind.Array)
        {
            return TryParseLiteral(element, out expression);
        }

        JsonElement.ArrayEnumerator enumerator = element.EnumerateArray();
        if (!enumerator.MoveNext() ||
            enumerator.Current.ValueKind != JsonValueKind.String)
        {
            return TryParseLiteral(element, out expression);
        }
        string? operation = enumerator.Current.GetString();
        if (string.IsNullOrEmpty(operation))
        {
            return false;
        }

        JsonElement[] rawArguments = element.EnumerateArray().Skip(1).ToArray();
        if (rawArguments.Length > MaximumArguments)
        {
            return false;
        }

        switch (operation)
        {
            case "literal":
                if (rawArguments.Length != 1 ||
                    !TryParseLiteralValue(rawArguments[0], out VectorStyleValue literal))
                {
                    return false;
                }
                expression = Literal(literal);
                return true;
            case "get":
            case "has":
            case "var":
                if (rawArguments.Length != 1 ||
                    rawArguments[0].ValueKind != JsonValueKind.String)
                {
                    return false;
                }
                string? name = rawArguments[0].GetString();
                if (string.IsNullOrEmpty(name) || name.Length > MaximumStringLength)
                {
                    return false;
                }
                expression = new VectorStyleExpression(
                    operation switch
                    {
                        "get" => VectorStyleExpressionOperator.Get,
                        "has" => VectorStyleExpressionOperator.Has,
                        _ => VectorStyleExpressionOperator.Var,
                    },
                    default,
                    name,
                    []);
                return true;
            case "geometry-type":
            case "zoom":
                if (rawArguments.Length != 0)
                {
                    return false;
                }
                expression = new VectorStyleExpression(
                    operation == "zoom"
                        ? VectorStyleExpressionOperator.Zoom
                        : VectorStyleExpressionOperator.GeometryType,
                    default,
                    null,
                    []);
                return true;
            case "interpolate":
                return TryParseInterpolate(
                    rawArguments,
                    depth,
                    ref nodeCount,
                    out expression);
            case "match":
                return TryParseMatch(
                    rawArguments,
                    depth,
                    ref nodeCount,
                    out expression);
            case "let":
                return TryParseLet(
                    rawArguments,
                    depth,
                    ref nodeCount,
                    out expression);
            case "format":
                return TryParseFormat(
                    rawArguments,
                    depth,
                    ref nodeCount,
                    out expression);
            default:
                if (!TryGetOperator(operation, out VectorStyleExpressionOperator expressionOperator) ||
                    !HasValidArgumentCount(expressionOperator, rawArguments.Length) ||
                    !TryParseArguments(
                        rawArguments,
                        depth,
                        ref nodeCount,
                        out VectorStyleExpression[] arguments))
                {
                    return false;
                }
                expression = new VectorStyleExpression(
                    expressionOperator,
                    default,
                    null,
                    arguments);
                return true;
        }
    }

    private static bool TryParseLegacyFilter(
        JsonElement element,
        int depth,
        ref int nodeCount,
        out VectorStyleExpression expression)
    {
        expression = null!;
        if (element.ValueKind != JsonValueKind.Array ||
            depth > MaximumDepth ||
            ++nodeCount > MaximumNodes)
        {
            return false;
        }

        JsonElement[] items = element.EnumerateArray().ToArray();
        if (items.Length == 0 ||
            items.Length > MaximumArguments + 1 ||
            items[0].ValueKind != JsonValueKind.String ||
            items[0].GetString() is not string operation)
        {
            return false;
        }

        if (operation is "all" or "any" or "none")
        {
            if (items.Length < 2)
            {
                return false;
            }
            VectorStyleExpression[] arguments =
                new VectorStyleExpression[items.Length - 1];
            for (int index = 1; index < items.Length; index++)
            {
                if (!TryParseLegacyFilter(
                        items[index],
                        depth + 1,
                        ref nodeCount,
                        out arguments[index - 1]) &&
                    !TryParse(
                        items[index],
                        depth + 1,
                        ref nodeCount,
                        out arguments[index - 1]))
                {
                    return false;
                }
            }
            VectorStyleExpression logical = new(
                operation == "all"
                    ? VectorStyleExpressionOperator.All
                    : VectorStyleExpressionOperator.Any,
                default,
                null,
                arguments);
            expression = operation == "none"
                ? new VectorStyleExpression(
                    VectorStyleExpressionOperator.Not,
                    default,
                    null,
                    [logical])
                : logical;
            return true;
        }

        if (operation is "has" or "!has")
        {
            if (items.Length != 2 ||
                !TryCreateLegacyFilterAccessor(
                    items[1],
                    VectorStyleExpressionOperator.Has,
                    out VectorStyleExpression has))
            {
                return false;
            }
            expression = operation == "!has"
                ? new VectorStyleExpression(
                    VectorStyleExpressionOperator.Not,
                    default,
                    null,
                    [has])
                : has;
            return true;
        }

        if (operation is "==" or "!=")
        {
            if (items.Length != 3 ||
                !TryCreateLegacyFilterAccessor(
                    items[1],
                    VectorStyleExpressionOperator.Get,
                    out VectorStyleExpression accessor) ||
                !TryParseLiteralValue(items[2], out VectorStyleValue expected))
            {
                return false;
            }
            VectorStyleExpression equal = new(
                VectorStyleExpressionOperator.Equal,
                default,
                null,
                [accessor, Literal(expected)]);
            expression = operation == "!="
                ? new VectorStyleExpression(
                    VectorStyleExpressionOperator.Not,
                    default,
                    null,
                    [equal])
                : equal;
            return true;
        }

        if (operation is "in" or "!in")
        {
            if (items.Length < 3 ||
                !TryCreateLegacyFilterAccessor(
                    items[1],
                    VectorStyleExpressionOperator.Get,
                    out VectorStyleExpression accessor))
            {
                return false;
            }
            VectorStyleExpression[] arguments =
                new VectorStyleExpression[items.Length - 1];
            arguments[0] = accessor;
            for (int index = 2; index < items.Length; index++)
            {
                if (!TryParseLiteralValue(
                        items[index],
                        out VectorStyleValue candidate))
                {
                    return false;
                }
                arguments[index - 1] = Literal(candidate);
            }
            VectorStyleExpression contains = new(
                VectorStyleExpressionOperator.In,
                default,
                null,
                arguments);
            expression = operation == "!in"
                ? new VectorStyleExpression(
                    VectorStyleExpressionOperator.Not,
                    default,
                    null,
                    [contains])
                : contains;
            return true;
        }

        return false;
    }

    private static bool TryCreateLegacyFilterAccessor(
        JsonElement element,
        VectorStyleExpressionOperator propertyOperator,
        out VectorStyleExpression expression)
    {
        expression = null!;
        if (element.ValueKind != JsonValueKind.String ||
            element.GetString() is not string name ||
            string.IsNullOrEmpty(name) ||
            name.Length > MaximumStringLength)
        {
            return false;
        }
        if (name == "$type")
        {
            if (propertyOperator != VectorStyleExpressionOperator.Get)
            {
                return false;
            }
            expression = new VectorStyleExpression(
                VectorStyleExpressionOperator.GeometryType,
                default,
                null,
                []);
            return true;
        }
        if (name == "$id")
        {
            return false;
        }
        expression = new VectorStyleExpression(
            propertyOperator,
            default,
            name,
            []);
        return true;
    }

    private static bool TryParseLegacyFunction(
        JsonElement element,
        int depth,
        ref int nodeCount,
        VectorStyleValue propertyDefaultValue,
        out VectorStyleExpression expression)
    {
        expression = null!;
        if (element.ValueKind != JsonValueKind.Object ||
            depth > MaximumDepth ||
            ++nodeCount > MaximumNodes ||
            !element.TryGetProperty("stops", out JsonElement stopsElement) ||
            stopsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        JsonElement[] stops = stopsElement.EnumerateArray().ToArray();
        if (stops.Length == 0 || stops.Length > MaximumArguments / 2)
        {
            return false;
        }

        string? property = null;
        if (element.TryGetProperty("property", out JsonElement propertyElement))
        {
            if (propertyElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(propertyElement.GetString()) ||
                propertyElement.GetString()!.Length > MaximumStringLength)
            {
                return false;
            }
            property = propertyElement.GetString();
        }

        string functionType = "exponential";
        if (element.TryGetProperty("type", out JsonElement typeElement))
        {
            if (typeElement.ValueKind != JsonValueKind.String ||
                typeElement.GetString() is not string parsedType ||
                parsedType is not
                    ("categorical" or "interval" or "exponential"))
            {
                return false;
            }
            functionType = parsedType;
        }

        double interpolationBase = 1;
        if (element.TryGetProperty("base", out JsonElement baseElement) &&
            (!baseElement.TryGetDouble(out interpolationBase) ||
             !double.IsFinite(interpolationBase) ||
             interpolationBase <= 0))
        {
            return false;
        }

        VectorStyleExpression input = property is null
            ? new VectorStyleExpression(
                VectorStyleExpressionOperator.Zoom,
                default,
                null,
                [])
            : new VectorStyleExpression(
                VectorStyleExpressionOperator.Get,
                default,
                property,
                []);
        VectorStyleExpression[] stopInputs =
            new VectorStyleExpression[stops.Length];
        VectorStyleExpression[] stopOutputs =
            new VectorStyleExpression[stops.Length];
        for (int index = 0; index < stops.Length; index++)
        {
            if (stops[index].ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            JsonElement[] pair = stops[index].EnumerateArray().ToArray();
            if (pair.Length != 2 ||
                !TryParseLiteral(pair[0], out stopInputs[index]) ||
                !TryParseLiteral(pair[1], out stopOutputs[index]))
            {
                return false;
            }
        }
        if (stopInputs.Any(stop =>
                stop._literal.Kind == VectorStyleValueKind.Array))
        {
            return false;
        }

        VectorStyleExpression fallback;
        if (element.TryGetProperty(
                "default",
                out JsonElement defaultElement))
        {
            if (!TryParseLiteral(defaultElement, out fallback))
            {
                return false;
            }
        }
        else
        {
            fallback = Literal(propertyDefaultValue);
        }

        bool categorical = functionType == "categorical" ||
            stopInputs.Any(stop =>
                stop._literal.Kind != VectorStyleValueKind.Number);
        if (categorical)
        {
            VectorStyleExpression[] arguments =
                new VectorStyleExpression[(stops.Length * 2) + 2];
            arguments[0] = input;
            for (int index = 0; index < stops.Length; index++)
            {
                arguments[(index * 2) + 1] = stopInputs[index];
                arguments[(index * 2) + 2] = stopOutputs[index];
            }
            arguments[^1] = fallback;
            expression = new VectorStyleExpression(
                VectorStyleExpressionOperator.Match,
                default,
                null,
                arguments);
            return true;
        }

        VectorStyleExpression resolved;
        if (functionType == "interval" ||
            !CanInterpolateLegacyOutputs(stopOutputs))
        {
            VectorStyleExpression[] arguments =
                new VectorStyleExpression[stops.Length * 2];
            arguments[0] = input;
            arguments[1] = stopOutputs[0];
            for (int index = 1; index < stops.Length; index++)
            {
                arguments[index * 2] = stopInputs[index];
                arguments[(index * 2) + 1] = stopOutputs[index];
            }
            resolved = new VectorStyleExpression(
                VectorStyleExpressionOperator.Step,
                default,
                null,
                arguments);
        }
        else
        {
            VectorStyleExpression[] interpolateArguments =
                new VectorStyleExpression[(stops.Length * 2) + 1];
            interpolateArguments[0] = input;
            for (int index = 0; index < stops.Length; index++)
            {
                interpolateArguments[(index * 2) + 1] = stopInputs[index];
                interpolateArguments[(index * 2) + 2] = stopOutputs[index];
            }
            resolved = new VectorStyleExpression(
                interpolationBase == 1
                    ? VectorStyleExpressionOperator.InterpolateLinear
                    : VectorStyleExpressionOperator.InterpolateExponential,
                VectorStyleValue.FromNumber(interpolationBase),
                null,
                interpolateArguments);
        }

        expression = property is null
            ? resolved
            : new VectorStyleExpression(
                VectorStyleExpressionOperator.Coalesce,
                default,
                null,
                [resolved, fallback]);
        return true;
    }

    private static bool CanInterpolateLegacyOutputs(
        IReadOnlyList<VectorStyleExpression> outputs)
    {
        foreach (VectorStyleExpression output in outputs)
        {
            VectorStyleValue value = output._literal;
            if (value.Kind == VectorStyleValueKind.Number ||
                value.Kind == VectorStyleValueKind.Array)
            {
                continue;
            }
            if (value.Kind != VectorStyleValueKind.String ||
                !VectorTextStyleLayer.TryParseColor(
                    value,
                    out _))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryParseInterpolate(
        JsonElement[] rawArguments,
        int depth,
        ref int nodeCount,
        out VectorStyleExpression expression)
    {
        expression = null!;
        if (rawArguments.Length < 6 ||
            (rawArguments.Length & 1) != 0 ||
            rawArguments[0].ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        JsonElement[] interpolation = rawArguments[0].EnumerateArray().ToArray();
        if (interpolation.Length is < 1 or > 2 ||
            interpolation[0].ValueKind != JsonValueKind.String)
        {
            return false;
        }
        string? interpolationKind = interpolation[0].GetString();
        double interpolationBase = 1;
        if (interpolationKind == "exponential")
        {
            if (interpolation.Length != 2 ||
                !interpolation[1].TryGetDouble(out interpolationBase) ||
                !double.IsFinite(interpolationBase) ||
                interpolationBase <= 0)
            {
                return false;
            }
        }
        else if (interpolationKind != "linear" ||
            interpolation.Length != 1)
        {
            return false;
        }
        JsonElement[] expressionArguments = rawArguments.Skip(1).ToArray();
        for (int index = 1; index < expressionArguments.Length; index += 2)
        {
            if (expressionArguments[index].ValueKind != JsonValueKind.Number)
            {
                return false;
            }
        }
        if (!TryParseArguments(
                expressionArguments,
                depth,
                ref nodeCount,
                out VectorStyleExpression[] arguments))
        {
            return false;
        }
        expression = new VectorStyleExpression(
            interpolationKind == "linear"
                ? VectorStyleExpressionOperator.InterpolateLinear
                : VectorStyleExpressionOperator.InterpolateExponential,
            VectorStyleValue.FromNumber(interpolationBase),
            null,
            arguments);
        return true;
    }

    private static bool TryParseMatch(
        JsonElement[] rawArguments,
        int depth,
        ref int nodeCount,
        out VectorStyleExpression expression)
    {
        expression = null!;
        if (rawArguments.Length < 4 || (rawArguments.Length & 1) != 0)
        {
            return false;
        }

        VectorStyleExpression[] arguments =
            new VectorStyleExpression[rawArguments.Length];
        if (!TryParse(
                rawArguments[0],
                depth + 1,
                ref nodeCount,
                out arguments[0]))
        {
            return false;
        }
        for (int index = 1; index < rawArguments.Length - 1; index += 2)
        {
            if (++nodeCount > MaximumNodes ||
                !TryParseLiteral(rawArguments[index], out arguments[index]) ||
                !TryParse(
                    rawArguments[index + 1],
                    depth + 1,
                    ref nodeCount,
                    out arguments[index + 1]))
            {
                return false;
            }
        }
        if (!TryParse(
                rawArguments[^1],
                depth + 1,
                ref nodeCount,
                out arguments[^1]))
        {
            return false;
        }
        expression = new VectorStyleExpression(
            VectorStyleExpressionOperator.Match,
            default,
            null,
            arguments);
        return true;
    }

    private static bool TryParseLet(
        JsonElement[] rawArguments,
        int depth,
        ref int nodeCount,
        out VectorStyleExpression expression)
    {
        expression = null!;
        if (rawArguments.Length < 3 || (rawArguments.Length & 1) == 0)
        {
            return false;
        }

        List<VectorStyleExpression> arguments = [];
        for (int index = 0; index < rawArguments.Length - 1; index += 2)
        {
            if (rawArguments[index].ValueKind != JsonValueKind.String)
            {
                return false;
            }
            string? name = rawArguments[index].GetString();
            if (string.IsNullOrEmpty(name) || name.Length > MaximumStringLength)
            {
                return false;
            }
            arguments.Add(Literal(VectorStyleValue.FromString(name)));
            if (!TryParse(
                    rawArguments[index + 1],
                    depth + 1,
                    ref nodeCount,
                    out VectorStyleExpression value))
            {
                return false;
            }
            arguments.Add(value);
        }
        if (!TryParse(
                rawArguments[^1],
                depth + 1,
                ref nodeCount,
                out VectorStyleExpression result))
        {
            return false;
        }
        arguments.Add(result);
        expression = new VectorStyleExpression(
            VectorStyleExpressionOperator.Let,
            default,
            null,
            arguments.ToArray());
        return true;
    }

    private static bool TryParseFormat(
        JsonElement[] rawArguments,
        int depth,
        ref int nodeCount,
        out VectorStyleExpression expression)
    {
        expression = null!;
        if (rawArguments.Length < 2 || (rawArguments.Length & 1) != 0)
        {
            return false;
        }
        VectorStyleExpression[] arguments =
            new VectorStyleExpression[rawArguments.Length / 2];
        for (int source = 0, destination = 0;
            source < rawArguments.Length;
            source += 2, destination++)
        {
            if (rawArguments[source + 1].ValueKind != JsonValueKind.Object ||
                !TryParse(
                    rawArguments[source],
                    depth + 1,
                    ref nodeCount,
                    out arguments[destination]))
            {
                return false;
            }
        }
        expression = new VectorStyleExpression(
            VectorStyleExpressionOperator.Format,
            default,
            null,
            arguments);
        return true;
    }

    private static bool TryParseArguments(
        JsonElement[] rawArguments,
        int depth,
        ref int nodeCount,
        out VectorStyleExpression[] arguments)
    {
        arguments = new VectorStyleExpression[rawArguments.Length];
        for (int index = 0; index < rawArguments.Length; index++)
        {
            if (!TryParse(
                    rawArguments[index],
                    depth + 1,
                    ref nodeCount,
                    out arguments[index]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryParseLiteral(
        JsonElement element,
        out VectorStyleExpression expression)
    {
        if (TryParseLiteralValue(element, out VectorStyleValue value))
        {
            expression = Literal(value);
            return true;
        }
        expression = null!;
        return false;
    }

    private static bool TryParseLiteralValue(
        JsonElement element,
        out VectorStyleValue value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                value = VectorStyleValue.Null;
                return true;
            case JsonValueKind.True:
            case JsonValueKind.False:
                value = VectorStyleValue.FromBoolean(element.GetBoolean());
                return true;
            case JsonValueKind.Number:
                if (element.TryGetDouble(out double number) && double.IsFinite(number))
                {
                    value = VectorStyleValue.FromNumber(number);
                    return true;
                }
                break;
            case JsonValueKind.String:
                string? text = element.GetString();
                if (text is not null && text.Length <= MaximumStringLength)
                {
                    value = VectorStyleValue.FromString(text);
                    return true;
                }
                break;
            case JsonValueKind.Array:
                JsonElement[] elements = element.EnumerateArray().ToArray();
                if (elements.Length > MaximumArguments)
                {
                    break;
                }
                VectorStyleValue[] items = new VectorStyleValue[elements.Length];
                for (int index = 0; index < elements.Length; index++)
                {
                    if (!TryParseLiteralValue(elements[index], out items[index]))
                    {
                        value = default;
                        return false;
                    }
                }
                value = VectorStyleValue.FromArray(items);
                return true;
        }
        value = default;
        return false;
    }

    private static bool TryGetOperator(
        string operation,
        out VectorStyleExpressionOperator expressionOperator)
    {
        expressionOperator = operation switch
        {
            "==" => VectorStyleExpressionOperator.Equal,
            "!" => VectorStyleExpressionOperator.Not,
            "all" => VectorStyleExpressionOperator.All,
            "any" => VectorStyleExpressionOperator.Any,
            "in" => VectorStyleExpressionOperator.In,
            "case" => VectorStyleExpressionOperator.Case,
            "coalesce" => VectorStyleExpressionOperator.Coalesce,
            "concat" => VectorStyleExpressionOperator.Concat,
            "match" => VectorStyleExpressionOperator.Match,
            "step" => VectorStyleExpressionOperator.Step,
            "to-string" => VectorStyleExpressionOperator.ToString,
            "*" => VectorStyleExpressionOperator.Multiply,
            "number" => VectorStyleExpressionOperator.Number,
            _ => default,
        };
        return operation is "==" or "!" or "all" or "any" or "in" or
            "case" or "coalesce" or "concat" or "match" or "step" or
            "to-string" or "*" or "number";
    }

    private static bool HasValidArgumentCount(
        VectorStyleExpressionOperator expressionOperator,
        int count) =>
        expressionOperator switch
        {
            VectorStyleExpressionOperator.Equal => count == 2,
            VectorStyleExpressionOperator.Not => count == 1,
            VectorStyleExpressionOperator.All or VectorStyleExpressionOperator.Any =>
                count >= 1,
            VectorStyleExpressionOperator.In => count >= 2,
            VectorStyleExpressionOperator.Case => count >= 3 && (count & 1) == 1,
            VectorStyleExpressionOperator.Coalesce or VectorStyleExpressionOperator.Concat =>
                count >= 1,
            VectorStyleExpressionOperator.Match => count >= 4 && (count & 1) == 0,
            VectorStyleExpressionOperator.Step => count >= 4 && (count & 1) == 0,
            VectorStyleExpressionOperator.ToString => count == 1,
            VectorStyleExpressionOperator.Multiply => count >= 2,
            VectorStyleExpressionOperator.Number => count >= 1,
            _ => false,
        };

    private bool TryEvaluate(
        VectorStyleEvaluationContext context,
        Dictionary<string, VectorStyleValue>? variables,
        out VectorStyleValue value)
    {
        switch (_operator)
        {
            case VectorStyleExpressionOperator.Literal:
                value = _literal;
                return true;
            case VectorStyleExpressionOperator.TokenString:
                return TryEvaluateTokenString(context, out value);
            case VectorStyleExpressionOperator.Get:
                if (context.Feature is not null &&
                    context.Feature.TryGetProperty(_name!, out VectorTileValue property))
                {
                    value = VectorStyleValue.FromVectorTileValue(property);
                    return true;
                }
                value = VectorStyleValue.Null;
                return true;
            case VectorStyleExpressionOperator.Has:
                value = VectorStyleValue.FromBoolean(
                    context.Feature is not null &&
                    context.Feature.TryGetProperty(_name!, out _));
                return true;
            case VectorStyleExpressionOperator.GeometryType:
                value = VectorStyleValue.FromString(
                    context.Feature?.GeometryType switch
                    {
                        VectorTileGeometryType.Point => "Point",
                        VectorTileGeometryType.MultiPoint => "MultiPoint",
                        VectorTileGeometryType.LineString => "LineString",
                        VectorTileGeometryType.MultiLineString => "MultiLineString",
                        VectorTileGeometryType.Polygon => "Polygon",
                        VectorTileGeometryType.MultiPolygon => "MultiPolygon",
                        _ => string.Empty,
                    });
                return context.Feature is not null;
            case VectorStyleExpressionOperator.Zoom:
                value = VectorStyleValue.FromNumber(context.Zoom);
                return true;
            case VectorStyleExpressionOperator.Var:
                if (variables is not null &&
                    variables.TryGetValue(_name!, out value))
                {
                    return true;
                }
                value = VectorStyleValue.Null;
                return false;
            case VectorStyleExpressionOperator.Equal:
                return TryEvaluateEqual(context, variables, out value);
            case VectorStyleExpressionOperator.Not:
                return TryEvaluateNot(context, variables, out value);
            case VectorStyleExpressionOperator.All:
            case VectorStyleExpressionOperator.Any:
                return TryEvaluateLogical(context, variables, out value);
            case VectorStyleExpressionOperator.In:
                return TryEvaluateIn(context, variables, out value);
            case VectorStyleExpressionOperator.Case:
                return TryEvaluateCase(context, variables, out value);
            case VectorStyleExpressionOperator.Coalesce:
                return TryEvaluateCoalesce(context, variables, out value);
            case VectorStyleExpressionOperator.Concat:
                return TryEvaluateConcat(context, variables, out value);
            case VectorStyleExpressionOperator.Format:
                return TryEvaluateConcat(context, variables, out value);
            case VectorStyleExpressionOperator.Match:
                return TryEvaluateMatch(context, variables, out value);
            case VectorStyleExpressionOperator.Step:
                return TryEvaluateStep(context, variables, out value);
            case VectorStyleExpressionOperator.InterpolateLinear:
            case VectorStyleExpressionOperator.InterpolateExponential:
                return TryEvaluateInterpolate(context, variables, out value);
            case VectorStyleExpressionOperator.Let:
                return TryEvaluateLet(context, variables, out value);
            case VectorStyleExpressionOperator.ToString:
                return TryEvaluateToString(context, variables, out value);
            case VectorStyleExpressionOperator.Multiply:
                return TryEvaluateMultiply(context, variables, out value);
            case VectorStyleExpressionOperator.Number:
                return TryEvaluateNumber(context, variables, out value);
            default:
                value = default;
                return false;
        }
    }

    private bool TryEvaluateEqual(
        VectorStyleEvaluationContext context,
        Dictionary<string, VectorStyleValue>? variables,
        out VectorStyleValue value)
    {
        if (!TryEvaluateArgument(0, context, variables, out VectorStyleValue left) ||
            !TryEvaluateArgument(1, context, variables, out VectorStyleValue right))
        {
            value = default;
            return false;
        }

        value = VectorStyleValue.FromBoolean(left.EqualsValue(right));
        return true;
    }

    private bool TryEvaluateTokenString(
        VectorStyleEvaluationContext context,
        out VectorStyleValue value)
    {
        string template = _literal.StringValue!;
        StringBuilder builder = new(template.Length);
        int copiedThrough = 0;
        while (copiedThrough < template.Length)
        {
            int open = template.IndexOf('{', copiedThrough);
            if (open < 0)
            {
                builder.Append(
                    template,
                    copiedThrough,
                    template.Length - copiedThrough);
                break;
            }
            int close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                builder.Append(
                    template,
                    copiedThrough,
                    template.Length - copiedThrough);
                break;
            }

            builder.Append(template, copiedThrough, open - copiedThrough);
            string propertyName = template[(open + 1)..close];
            if (propertyName.Length > 0 &&
                context.Feature is not null &&
                context.Feature.TryGetProperty(
                    propertyName,
                    out VectorTileValue property))
            {
                builder.Append(
                    VectorStyleValue.FromVectorTileValue(property)
                        .ToInvariantString());
            }
            copiedThrough = close + 1;
            if (builder.Length > MaximumStringLength)
            {
                value = default;
                return false;
            }
        }

        value = VectorStyleValue.FromString(builder.ToString());
        return true;
    }

    private bool TryEvaluateNot(
        VectorStyleEvaluationContext context,
        Dictionary<string, VectorStyleValue>? variables,
        out VectorStyleValue value)
    {
        if (!TryEvaluateArgument(0, context, variables, out VectorStyleValue operand) ||
            operand.Kind != VectorStyleValueKind.Boolean)
        {
            value = default;
            return false;
        }
        value = VectorStyleValue.FromBoolean(!operand.BooleanValue);
        return true;
    }

    private bool TryEvaluateLogical(
        VectorStyleEvaluationContext context,
        Dictionary<string, VectorStyleValue>? variables,
        out VectorStyleValue value)
    {
        bool all = _operator == VectorStyleExpressionOperator.All;
        foreach (VectorStyleExpression argument in _arguments)
        {
            if (!argument.TryEvaluate(context, variables, out VectorStyleValue result) ||
                result.Kind != VectorStyleValueKind.Boolean)
            {
                value = default;
                return false;
            }
            if (all && !result.BooleanValue)
            {
                value = VectorStyleValue.FromBoolean(false);
                return true;
            }
            if (!all && result.BooleanValue)
            {
                value = VectorStyleValue.FromBoolean(true);
                return true;
            }
        }
        value = VectorStyleValue.FromBoolean(all);
        return true;
    }

    private bool TryEvaluateIn(
        VectorStyleEvaluationContext context,
        Dictionary<string, VectorStyleValue>? variables,
        out VectorStyleValue value)
    {
        if (!TryEvaluateArgument(0, context, variables, out VectorStyleValue needle))
        {
            value = default;
            return false;
        }
        bool found = false;
        for (int index = 1; index < _arguments.Length; index++)
        {
            if (!TryEvaluateArgument(
                    index,
                    context,
                    variables,
                    out VectorStyleValue candidate))
            {
                value = default;
                return false;
            }
            if (candidate.Kind == VectorStyleValueKind.Array &&
                candidate.ArrayValue is not null)
            {
                found |= candidate.ArrayValue.Any(needle.EqualsValue);
            }
            else
            {
                found |= needle.EqualsValue(candidate);
            }
        }
        value = VectorStyleValue.FromBoolean(found);
        return true;
    }

    private bool TryEvaluateCase(
        VectorStyleEvaluationContext context,
        Dictionary<string, VectorStyleValue>? variables,
        out VectorStyleValue value)
    {
        for (int index = 0; index < _arguments.Length - 1; index += 2)
        {
            if (!TryEvaluateArgument(
                    index,
                    context,
                    variables,
                    out VectorStyleValue condition) ||
                condition.Kind != VectorStyleValueKind.Boolean)
            {
                value = default;
                return false;
            }
            if (condition.BooleanValue)
            {
                return TryEvaluateArgument(index + 1, context, variables, out value);
            }
        }
        return TryEvaluateArgument(_arguments.Length - 1, context, variables, out value);
    }

    private bool TryEvaluateCoalesce(
        VectorStyleEvaluationContext context,
        Dictionary<string, VectorStyleValue>? variables,
        out VectorStyleValue value)
    {
        foreach (VectorStyleExpression argument in _arguments)
        {
            if (!argument.TryEvaluate(context, variables, out VectorStyleValue candidate))
            {
                continue;
            }
            if (candidate.Kind != VectorStyleValueKind.Null)
            {
                value = candidate;
                return true;
            }
        }
        value = VectorStyleValue.Null;
        return true;
    }

    private bool TryEvaluateConcat(
        VectorStyleEvaluationContext context,
        Dictionary<string, VectorStyleValue>? variables,
        out VectorStyleValue value)
    {
        StringBuilder builder = new();
        foreach (VectorStyleExpression argument in _arguments)
        {
            if (!argument.TryEvaluate(context, variables, out VectorStyleValue candidate))
            {
                value = default;
                return false;
            }
            builder.Append(candidate.ToInvariantString());
            if (builder.Length > MaximumStringLength)
            {
                value = default;
                return false;
            }
        }
        value = VectorStyleValue.FromString(builder.ToString());
        return true;
    }

    private bool TryEvaluateToString(
        VectorStyleEvaluationContext context,
        Dictionary<string, VectorStyleValue>? variables,
        out VectorStyleValue value)
    {
        if (!TryEvaluateArgument(0, context, variables, out VectorStyleValue input))
        {
            value = default;
            return false;
        }
        string? text = input.Kind switch
        {
            VectorStyleValueKind.Null => string.Empty,
            VectorStyleValueKind.Boolean =>
                input.BooleanValue ? "true" : "false",
            VectorStyleValueKind.Number =>
                input.NumberValue.ToString(
                    "G",
                    CultureInfo.InvariantCulture),
            VectorStyleValueKind.String => input.StringValue,
            _ => null,
        };
        if (text is null || text.Length > MaximumStringLength)
        {
            value = default;
            return false;
        }
        value = VectorStyleValue.FromString(text);
        return true;
    }

    private bool TryEvaluateMultiply(
        VectorStyleEvaluationContext context,
        Dictionary<string, VectorStyleValue>? variables,
        out VectorStyleValue value)
    {
        double product = 1;
        foreach (VectorStyleExpression argument in _arguments)
        {
            if (!argument.TryEvaluate(
                    context,
                    variables,
                    out VectorStyleValue factor) ||
                !factor.TryGetNumber(out double number))
            {
                value = default;
                return false;
            }
            product *= number;
            if (!double.IsFinite(product))
            {
                value = default;
                return false;
            }
        }
        value = VectorStyleValue.FromNumber(product);
        return true;
    }

    private bool TryEvaluateNumber(
        VectorStyleEvaluationContext context,
        Dictionary<string, VectorStyleValue>? variables,
        out VectorStyleValue value)
    {
        foreach (VectorStyleExpression argument in _arguments)
        {
            if (argument.TryEvaluate(
                    context,
                    variables,
                    out VectorStyleValue candidate) &&
                candidate.Kind == VectorStyleValueKind.Number)
            {
                value = candidate;
                return true;
            }
        }
        value = default;
        return false;
    }

    private bool TryEvaluateMatch(
        VectorStyleEvaluationContext context,
        Dictionary<string, VectorStyleValue>? variables,
        out VectorStyleValue value)
    {
        if (!TryEvaluateArgument(0, context, variables, out VectorStyleValue input))
        {
            value = default;
            return false;
        }
        for (int index = 1; index < _arguments.Length - 1; index += 2)
        {
            if (!TryEvaluateArgument(
                    index,
                    context,
                    variables,
                    out VectorStyleValue label))
            {
                value = default;
                return false;
            }
            bool matches = label.Kind == VectorStyleValueKind.Array &&
                label.ArrayValue is not null
                ? label.ArrayValue.Any(input.EqualsValue)
                : input.EqualsValue(label);
            if (matches)
            {
                return TryEvaluateArgument(index + 1, context, variables, out value);
            }
        }
        return TryEvaluateArgument(_arguments.Length - 1, context, variables, out value);
    }

    private bool TryEvaluateStep(
        VectorStyleEvaluationContext context,
        Dictionary<string, VectorStyleValue>? variables,
        out VectorStyleValue value)
    {
        if (!TryEvaluateArgument(
                0,
                context,
                variables,
                out VectorStyleValue inputValue) ||
            !inputValue.TryGetNumber(out double input) ||
            !TryEvaluateArgument(1, context, variables, out value))
        {
            value = default;
            return false;
        }
        for (int index = 2; index < _arguments.Length; index += 2)
        {
            if (!TryEvaluateArgument(
                    index,
                    context,
                    variables,
                    out VectorStyleValue stopValue) ||
                !stopValue.TryGetNumber(out double stop))
            {
                value = default;
                return false;
            }
            if (input < stop)
            {
                return true;
            }
            if (!TryEvaluateArgument(index + 1, context, variables, out value))
            {
                return false;
            }
        }
        return true;
    }

    private bool TryEvaluateInterpolate(
        VectorStyleEvaluationContext context,
        Dictionary<string, VectorStyleValue>? variables,
        out VectorStyleValue value)
    {
        if (!TryEvaluateArgument(
                0,
                context,
                variables,
                out VectorStyleValue inputValue) ||
            !inputValue.TryGetNumber(out double input))
        {
            value = default;
            return false;
        }

        if (!TryEvaluateArgument(
                1,
                context,
                variables,
                out VectorStyleValue firstStopValue) ||
            !firstStopValue.TryGetNumber(out double firstStop) ||
            !TryEvaluateArgument(2, context, variables, out VectorStyleValue firstOutput))
        {
            value = default;
            return false;
        }
        if (input <= firstStop)
        {
            value = firstOutput;
            return true;
        }

        double previousStop = firstStop;
        VectorStyleValue previousOutput = firstOutput;
        for (int index = 3; index < _arguments.Length; index += 2)
        {
            if (!TryEvaluateArgument(
                    index,
                    context,
                    variables,
                    out VectorStyleValue stopValue) ||
                !stopValue.TryGetNumber(out double stop) ||
                !TryEvaluateArgument(
                    index + 1,
                    context,
                    variables,
                    out VectorStyleValue output))
            {
                value = default;
                return false;
            }
            if (stop <= previousStop)
            {
                value = default;
                return false;
            }
            if (input <= stop)
            {
                double amount = (input - previousStop) / (stop - previousStop);
                if (_operator ==
                    VectorStyleExpressionOperator.InterpolateExponential &&
                    _literal.TryGetNumber(out double interpolationBase) &&
                    interpolationBase != 1)
                {
                    double denominator =
                        Math.Pow(interpolationBase, stop - previousStop) - 1;
                    amount = denominator == 0
                        ? 0
                        : (Math.Pow(
                            interpolationBase,
                            input - previousStop) - 1) / denominator;
                }
                return VectorStyleValue.TryInterpolate(
                    previousOutput,
                    output,
                    amount,
                    out value);
            }
            previousStop = stop;
            previousOutput = output;
        }
        value = previousOutput;
        return true;
    }

    private bool TryEvaluateLet(
        VectorStyleEvaluationContext context,
        Dictionary<string, VectorStyleValue>? variables,
        out VectorStyleValue value)
    {
        Dictionary<string, VectorStyleValue> local = variables is null
            ? new(StringComparer.Ordinal)
            : new(variables, StringComparer.Ordinal);
        for (int index = 0; index < _arguments.Length - 1; index += 2)
        {
            string name = _arguments[index]._literal.StringValue!;
            if (!_arguments[index + 1].TryEvaluate(context, local, out VectorStyleValue item))
            {
                value = default;
                return false;
            }
            local[name] = item;
        }
        return _arguments[^1].TryEvaluate(context, local, out value);
    }

    private bool TryEvaluateArgument(
        int index,
        VectorStyleEvaluationContext context,
        Dictionary<string, VectorStyleValue>? variables,
        out VectorStyleValue value) =>
        _arguments[index].TryEvaluate(context, variables, out value);
}

internal enum VectorStyleValueKind
{
    Null,
    Boolean,
    Number,
    String,
    Array,
}

internal readonly record struct VectorStyleValue
{
    private VectorStyleValue(
        VectorStyleValueKind kind,
        bool booleanValue,
        double numberValue,
        string? stringValue,
        VectorStyleValue[]? arrayValue)
    {
        Kind = kind;
        BooleanValue = booleanValue;
        NumberValue = numberValue;
        StringValue = stringValue;
        ArrayValue = arrayValue;
    }

    internal static VectorStyleValue Null { get; } =
        new(VectorStyleValueKind.Null, false, 0, null, null);

    internal VectorStyleValueKind Kind { get; }

    internal bool BooleanValue { get; }

    internal double NumberValue { get; }

    internal string? StringValue { get; }

    internal VectorStyleValue[]? ArrayValue { get; }

    internal static VectorStyleValue FromBoolean(bool value) =>
        new(VectorStyleValueKind.Boolean, value, 0, null, null);

    internal static VectorStyleValue FromNumber(double value) =>
        new(VectorStyleValueKind.Number, false, value, null, null);

    internal static VectorStyleValue FromString(string value) =>
        new(VectorStyleValueKind.String, false, 0, value, null);

    internal static VectorStyleValue FromArray(VectorStyleValue[] value) =>
        new(VectorStyleValueKind.Array, false, 0, null, value);

    internal static VectorStyleValue FromVectorTileValue(VectorTileValue value)
    {
        if (value.TryGetNumber(out double number))
        {
            return FromNumber(number);
        }
        return value.Kind switch
        {
            VectorTileValueKind.String => FromString(value.StringValue ?? string.Empty),
            VectorTileValueKind.Bool => FromBoolean(value.BoolValue),
            _ => Null,
        };
    }

    internal bool TryGetNumber(out double value)
    {
        value = NumberValue;
        return Kind == VectorStyleValueKind.Number;
    }

    internal bool EqualsValue(VectorStyleValue other)
    {
        if (Kind == VectorStyleValueKind.Number &&
            other.Kind == VectorStyleValueKind.Number)
        {
            return NumberValue.Equals(other.NumberValue);
        }
        if (Kind != other.Kind)
        {
            return false;
        }
        return Kind switch
        {
            VectorStyleValueKind.Null => true,
            VectorStyleValueKind.Boolean => BooleanValue == other.BooleanValue,
            VectorStyleValueKind.String =>
                string.Equals(StringValue, other.StringValue, StringComparison.Ordinal),
            VectorStyleValueKind.Array =>
                ArrayValue is not null &&
                other.ArrayValue is not null &&
                ArrayValue.Length == other.ArrayValue.Length &&
                ArrayValue.Zip(other.ArrayValue).All(pair =>
                    pair.First.EqualsValue(pair.Second)),
            _ => false,
        };
    }

    internal string ToInvariantString() => Kind switch
    {
        VectorStyleValueKind.Null => string.Empty,
        VectorStyleValueKind.Boolean => BooleanValue ? "true" : "false",
        VectorStyleValueKind.Number =>
            NumberValue.ToString("G17", CultureInfo.InvariantCulture),
        VectorStyleValueKind.String => StringValue ?? string.Empty,
        _ => string.Empty,
    };

    internal static bool TryInterpolate(
        VectorStyleValue from,
        VectorStyleValue to,
        double amount,
        out VectorStyleValue value)
    {
        amount = Math.Clamp(amount, 0, 1);
        if (from.TryGetNumber(out double fromNumber) &&
            to.TryGetNumber(out double toNumber))
        {
            value = FromNumber(fromNumber + ((toNumber - fromNumber) * amount));
            return true;
        }
        if (from.Kind == VectorStyleValueKind.String &&
            to.Kind == VectorStyleValueKind.String &&
            VectorTextStyleLayer.TryParseColor(from, out Vector4 fromColor) &&
            VectorTextStyleLayer.TryParseColor(to, out Vector4 toColor))
        {
            value = FromString(ToColorString(Vector4.Lerp(
                fromColor,
                toColor,
                (float)amount)));
            return true;
        }
        if (from.Kind == VectorStyleValueKind.Array &&
            to.Kind == VectorStyleValueKind.Array &&
            from.ArrayValue is not null &&
            to.ArrayValue is not null &&
            from.ArrayValue.Length == to.ArrayValue.Length)
        {
            VectorStyleValue[] items = new VectorStyleValue[from.ArrayValue.Length];
            for (int index = 0; index < items.Length; index++)
            {
                if (!TryInterpolate(
                        from.ArrayValue[index],
                        to.ArrayValue[index],
                        amount,
                        out items[index]))
                {
                    value = default;
                    return false;
                }
            }
            value = FromArray(items);
            return true;
        }
        value = default;
        return false;
    }

    private static string ToColorString(Vector4 premultiplied)
    {
        float alpha = Math.Clamp(premultiplied.W, 0, 1);
        float inverseAlpha = alpha > 0 ? 1 / alpha : 0;
        byte red = (byte)Math.Round(
            Math.Clamp(premultiplied.X * inverseAlpha, 0, 1) * 255);
        byte green = (byte)Math.Round(
            Math.Clamp(premultiplied.Y * inverseAlpha, 0, 1) * 255);
        byte blue = (byte)Math.Round(
            Math.Clamp(premultiplied.Z * inverseAlpha, 0, 1) * 255);
        byte opacity = (byte)Math.Round(alpha * 255);
        return FormattableString.Invariant(
            $"#{red:X2}{green:X2}{blue:X2}{opacity:X2}");
    }
}

/// <summary>
/// Owns one decoded premultiplied sprite atlas and lazily cropped, bounded icon buffers.
/// </summary>
internal sealed class VectorSpriteAtlas
{
    private const int MaximumEntries = 16_384;
    private const long MaximumCroppedBytes = 32 * 1024 * 1024;
    private const int MaximumCroppedTextures = 4096;
    private const int MaximumSpriteNameLength = 1024;
    private readonly object _sync = new();
    private readonly string _styleSlug;
    private readonly Dictionary<string, VectorSpriteEntry> _entries;
    private readonly byte[] _pixels;
    private readonly uint _width;
    private readonly uint _height;
    private readonly Dictionary<string, VectorSpriteTextureData> _textures =
        new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _textureNames = [];
    private long _croppedBytes;

    internal VectorSpriteAtlas(
        string styleSlug,
        Dictionary<string, VectorSpriteEntry> entries,
        byte[] pixels,
        uint width,
        uint height)
    {
        if (!MapRenderer.IsValidPixelBuffer(pixels, width, height))
        {
            throw new InvalidDataException(
                "The sprite atlas pixel buffer does not match its dimensions.");
        }
        foreach (VectorSpriteEntry entry in entries.Values)
        {
            if (entry.X > width ||
                entry.Y > height ||
                entry.Width > width - entry.X ||
                entry.Height > height - entry.Y)
            {
                throw new InvalidDataException(
                    "The sprite index contains an out-of-range rectangle.");
            }
        }
        _styleSlug = styleSlug;
        _entries = entries;
        _pixels = pixels;
        _width = width;
        _height = height;
    }

    internal int EntryCount => _entries.Count;

    internal static Dictionary<string, VectorSpriteEntry> ParseIndex(
        ReadOnlyMemory<byte> json)
    {
        using JsonDocument document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                MaxDepth = 16,
            });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The sprite index is not a JSON object.");
        }

        Dictionary<string, VectorSpriteEntry> entries =
            new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (entries.Count >= MaximumEntries)
            {
                throw new InvalidDataException(
                    "The sprite index contains too many entries.");
            }
            if (string.IsNullOrEmpty(property.Name) ||
                property.Name.Length > MaximumSpriteNameLength ||
                property.Value.ValueKind != JsonValueKind.Object ||
                !TryGetUInt32(property.Value, "x", out uint x) ||
                !TryGetUInt32(property.Value, "y", out uint y) ||
                !TryGetUInt32(property.Value, "width", out uint width) ||
                !TryGetUInt32(property.Value, "height", out uint height) ||
                width == 0 ||
                height == 0 ||
                !property.Value.TryGetProperty(
                    "pixelRatio",
                    out JsonElement pixelRatioElement) ||
                !pixelRatioElement.TryGetDouble(out double pixelRatio) ||
                !double.IsFinite(pixelRatio) ||
                pixelRatio <= 0 ||
                pixelRatio > 16)
            {
                throw new InvalidDataException(
                    "The sprite index contains an invalid entry.");
            }
            bool visible = true;
            if (property.Value.TryGetProperty(
                    "visible",
                    out JsonElement visibleElement))
            {
                if (visibleElement.ValueKind is not
                    (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new InvalidDataException(
                        "The sprite index contains an invalid visibility value.");
                }
                visible = visibleElement.GetBoolean();
            }
            if (!entries.TryAdd(
                    property.Name,
                    new VectorSpriteEntry(
                        x,
                        y,
                        width,
                        height,
                        pixelRatio,
                        visible)))
            {
                throw new InvalidDataException(
                    "The sprite index contains a duplicate entry.");
            }
        }
        return entries;
    }

    internal static long CreateTextureId(string styleSlug, string spriteName)
    {
        byte[] identity = Encoding.UTF8.GetBytes($"{styleSlug}\0{spriteName}");
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(identity, hash);
        return BinaryPrimitives.ReadInt64LittleEndian(hash) | long.MinValue;
    }

    internal VectorSpriteLookupResult TryGetTexture(
        string spriteName,
        out VectorSpriteTextureData? texture,
        out VectorSpriteEntry entry)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(spriteName, out entry))
            {
                texture = null;
                return VectorSpriteLookupResult.Missing;
            }
            if (!entry.Visible)
            {
                texture = null;
                return VectorSpriteLookupResult.Hidden;
            }
            if (!_textures.TryGetValue(spriteName, out texture))
            {
                return VectorSpriteLookupResult.NotPrepared;
            }
            return VectorSpriteLookupResult.Found;
        }
    }

    internal VectorSpriteLookupResult TryGetOrCreateTexture(
        string spriteName,
        CancellationToken cancellationToken,
        out VectorSpriteTextureData? texture,
        out VectorSpriteEntry entry)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(spriteName, out entry))
            {
                texture = null;
                return VectorSpriteLookupResult.Missing;
            }
            if (!entry.Visible)
            {
                texture = null;
                return VectorSpriteLookupResult.Hidden;
            }
            if (_textures.TryGetValue(spriteName, out texture))
            {
                return VectorSpriteLookupResult.Found;
            }

            long byteCount = checked((long)entry.Width * entry.Height * 4);
            if (_textures.Count >= MaximumCroppedTextures ||
                byteCount > MaximumCroppedBytes - _croppedBytes)
            {
                throw new InvalidDataException(
                    "The sprite crop cache exceeds its supported limit.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            byte[] cropped = new byte[checked((int)byteCount)];
            int sourceStride = checked((int)_width * 4);
            int destinationStride = checked((int)entry.Width * 4);
            int sourceX = checked((int)entry.X * 4);
            for (uint row = 0; row < entry.Height; row++)
            {
                if ((row & 31) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                Buffer.BlockCopy(
                    _pixels,
                    checked(((int)(entry.Y + row) * sourceStride) + sourceX),
                    cropped,
                    checked((int)row * destinationStride),
                    destinationStride);
            }

            long textureId = CreateTextureId(_styleSlug, spriteName);
            if (_textureNames.TryGetValue(textureId, out string? existingName) &&
                !string.Equals(existingName, spriteName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The sprite texture identity is not unique.");
            }
            texture = new VectorSpriteTextureData(
                textureId,
                cropped,
                entry.Width,
                entry.Height);
            _textures.Add(spriteName, texture);
            _textureNames[textureId] = spriteName;
            _croppedBytes += byteCount;
            return VectorSpriteLookupResult.Found;
        }
    }

    private static bool TryGetUInt32(
        JsonElement owner,
        string name,
        out uint value)
    {
        if (owner.TryGetProperty(name, out JsonElement element) &&
            element.TryGetInt64(out long signed) &&
            signed >= 0 &&
            signed <= uint.MaxValue)
        {
            value = (uint)signed;
            return true;
        }
        value = 0;
        return false;
    }
}

internal readonly record struct VectorSpriteEntry(
    uint X,
    uint Y,
    uint Width,
    uint Height,
    double PixelRatio,
    bool Visible);

internal enum VectorSpriteLookupResult
{
    Found,
    Missing,
    Hidden,
    NotPrepared,
}

/// <summary>
/// Tracks source ownership for negative internal sprite texture identifiers.
/// </summary>
internal sealed class VectorSpriteOwnershipTracker
{
    private readonly Dictionary<long, HashSet<long>> _sourceTextures = [];
    private readonly Dictionary<long, int> _referenceCounts = [];

    internal bool Add(long sourceId, long textureId)
    {
        if (!_sourceTextures.TryGetValue(sourceId, out HashSet<long>? textures))
        {
            textures = [];
            _sourceTextures.Add(sourceId, textures);
        }
        if (!textures.Add(textureId))
        {
            return false;
        }
        _referenceCounts.TryGetValue(textureId, out int count);
        _referenceCounts[textureId] = count + 1;
        return count == 0;
    }

    internal long[] RemoveSource(long sourceId)
    {
        if (!_sourceTextures.Remove(sourceId, out HashSet<long>? textures))
        {
            return [];
        }
        List<long> released = [];
        foreach (long textureId in textures)
        {
            int count = _referenceCounts[textureId] - 1;
            if (count == 0)
            {
                _referenceCounts.Remove(textureId);
                released.Add(textureId);
            }
            else
            {
                _referenceCounts[textureId] = count;
            }
        }
        return released.ToArray();
    }

    internal void Clear()
    {
        _sourceTextures.Clear();
        _referenceCounts.Clear();
    }
}
