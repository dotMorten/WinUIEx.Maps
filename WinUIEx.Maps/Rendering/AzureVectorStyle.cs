using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
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
    private Task<AzureVectorStyleAssets>? _assetsTask;

    internal AzureVectorStyleProvider(MapStyle style, string token)
    {
        _style = style;
        _styleSlug = AzureTileAcquisitionSession.GetAzureStyleName(style);
        _token = token;
        _glyphProvider = new AzureGlyphProvider(style, token);
    }

    internal async Task<AzureVectorStyleAssets> GetAssetsAsync(
        CancellationToken cancellationToken)
    {
        Task<AzureVectorStyleAssets> task;
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

    internal static AzureStyleAssetPaths GetAssetPaths(MapStyle style)
    {
        string slug = AzureTileAcquisitionSession.GetAzureStyleName(style);
        const string query = "styleVersion=2023-01-01&api-version=2.0";
        return new AzureStyleAssetPaths(
            $"styling/styles/{slug}?{query}",
            $"styling/sprites/{slug}/sprite.json?{query}",
            $"styling/sprites/{slug}/sprite.png?{query}");
    }

    private async Task<AzureVectorStyleAssets> LoadAsync(
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        AzureStyleAssetPaths paths = GetAssetPaths(_style);
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

        AzureSymbolStyle symbolStyle = AzureSymbolStyle.Parse(styleJson);
        Dictionary<string, AzureSpriteEntry> spriteEntries =
            AzureSpriteAtlas.ParseIndex(spriteJson);
        DecodedSpriteImage decoded = await DecodeSpriteAsync(
            spritePng,
            cancellationToken).ConfigureAwait(false);
        AzureSpriteAtlas spriteAtlas = new(
            _styleSlug,
            spriteEntries,
            decoded.Pixels,
            decoded.Width,
            decoded.Height);
        AzureVectorStyleAssets assets = new(
            _style,
            symbolStyle,
            spriteAtlas,
            new AzureGlyphAtlas(_styleSlug, _glyphProvider));
        MapControlEventSource.Log.VectorStyleAssetsLoaded(
            (int)_style,
            symbolStyle.LayerCount,
            symbolStyle.UnsupportedLayerCount,
            spriteAtlas.EntryCount,
            checked((int)decoded.Width),
            checked((int)decoded.Height),
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return assets;
    }

    private static async Task<DecodedSpriteImage> DecodeSpriteAsync(
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

    private readonly record struct DecodedSpriteImage(
        byte[] Pixels,
        uint Width,
        uint Height);
}

internal readonly record struct AzureStyleAssetPaths(
    string Style,
    string SpriteIndex,
    string SpriteImage);

/// <summary>
/// Combines parsed symbol layers with one premultiplied sprite atlas.
/// </summary>
internal sealed class AzureVectorStyleAssets
{
    private readonly MapStyle _mapStyle;
    private readonly AzureSymbolStyle _symbolStyle;
    private readonly AzureSpriteAtlas _spriteAtlas;
    private readonly AzureGlyphAtlas _glyphAtlas;

    internal AzureVectorStyleAssets(
        MapStyle mapStyle,
        AzureSymbolStyle symbolStyle,
        AzureSpriteAtlas spriteAtlas,
        AzureGlyphAtlas glyphAtlas)
    {
        _mapStyle = mapStyle;
        _symbolStyle = symbolStyle;
        _spriteAtlas = spriteAtlas;
        _glyphAtlas = glyphAtlas;
    }

    internal static AzureVectorStyleAssets CreateForTest(
        MapStyle mapStyle,
        ReadOnlyMemory<byte> styleJson,
        ReadOnlyMemory<byte> spriteJson,
        byte[] spritePixels,
        uint spriteWidth,
        uint spriteHeight) =>
        new(
            mapStyle,
            AzureSymbolStyle.Parse(styleJson),
            new AzureSpriteAtlas(
                AzureTileAcquisitionSession.GetAzureStyleName(mapStyle),
                AzureSpriteAtlas.ParseIndex(spriteJson),
                spritePixels,
                spriteWidth,
                spriteHeight),
            new AzureGlyphAtlas(
                AzureTileAcquisitionSession.GetAzureStyleName(mapStyle),
                provider: null));

    internal AzureGlyphAtlas GlyphAtlas => _glyphAtlas;

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
        foreach (double zoom in _symbolStyle.GetPreparationZooms(tileZoom))
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
        HashSet<AzureGlyphKey> glyphKeys = [];
        foreach (double zoom in _symbolStyle.GetPreparationZooms(tileZoom))
        {
            CollectTextGlyphKeys(
                features,
                zoom,
                glyphKeys,
                cancellationToken);
        }
        await _glyphAtlas.PrepareAsync(glyphKeys, cancellationToken)
            .ConfigureAwait(false);
        foreach (AzureGlyphKey key in glyphKeys)
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
        double zoom)
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
            symbols,
            ref counts,
            CancellationToken.None);
        return new VectorSymbolResolution(
            symbols.ToArray(),
            counts.EvaluationFailureCount,
            counts.UnavailableSpriteCount,
            counts.ResolvedGlyphCount,
            counts.UnavailableGlyphCount);
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
        foreach (AzureLineStyleLayer layer in _symbolStyle.LineLayers)
        {
            AzureStyleVisibilityResult visibility = layer.EvaluateVisibility(zoom);
            if (visibility != AzureStyleVisibilityResult.Visible)
            {
                if (visibility == AzureStyleVisibilityResult.EvaluationFailure)
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
                AzureStyleEvaluationContext context = new(feature, zoom);
                AzureStyleFilterResult filter = layer.EvaluateFilter(context);
                if (filter != AzureStyleFilterResult.Match)
                {
                    if (filter == AzureStyleFilterResult.EvaluationFailure)
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
                AzureStyleLineResult lineResult =
                    layer.EvaluateLine(context, out VectorLineStyle style);
                if (lineResult != AzureStyleLineResult.Resolved)
                {
                    if (lineResult == AzureStyleLineResult.EvaluationFailure)
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
        foreach (AzureFillStyleLayer layer in _symbolStyle.FillLayers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (layer.EvaluateVisibility(zoom) !=
                AzureStyleVisibilityResult.Visible)
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
                AzureStyleEvaluationContext context = new(feature, zoom);
                if (layer.EvaluateFilter(context) !=
                        AzureStyleFilterResult.Match ||
                    layer.EvaluateFill(
                        context,
                        out _,
                        out string? patternName) !=
                        AzureStyleFillResult.Resolved ||
                    patternName is null)
                {
                    continue;
                }
                if (_spriteAtlas.TryGetOrCreateTexture(
                        patternName,
                        cancellationToken,
                        out VectorSpriteTextureData? texture,
                        out _) == AzureSpriteLookupResult.Found &&
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
        foreach (AzureLineStyleLayer layer in _symbolStyle.LineLayers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AzureStyleVisibilityResult visibility = layer.EvaluateVisibility(zoom);
            if (visibility != AzureStyleVisibilityResult.Visible)
            {
                if (collectCounts &&
                    visibility == AzureStyleVisibilityResult.EvaluationFailure)
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
                AzureStyleEvaluationContext context = new(feature, zoom);
                AzureStyleFilterResult filter = layer.EvaluateFilter(context);
                if (filter != AzureStyleFilterResult.Match)
                {
                    if (collectCounts &&
                        filter == AzureStyleFilterResult.EvaluationFailure)
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
                AzureSpriteLookupResult spriteResult = createTextures
                    ? _spriteAtlas.TryGetOrCreateTexture(
                        patternName,
                        cancellationToken,
                        out VectorSpriteTextureData? texture,
                        out AzureSpriteEntry entry)
                    : _spriteAtlas.TryGetTexture(
                        patternName,
                        out texture,
                        out entry);
                if (spriteResult != AzureSpriteLookupResult.Found ||
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
        foreach (AzureFillStyleLayer layer in _symbolStyle.FillLayers)
        {
            AzureStyleVisibilityResult visibility = layer.EvaluateVisibility(zoom);
            if (visibility != AzureStyleVisibilityResult.Visible)
            {
                if (visibility == AzureStyleVisibilityResult.EvaluationFailure)
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
                AzureStyleEvaluationContext context = new(feature, zoom);
                AzureStyleFilterResult filter = layer.EvaluateFilter(context);
                if (filter != AzureStyleFilterResult.Match)
                {
                    if (filter == AzureStyleFilterResult.EvaluationFailure)
                    {
                        evaluationFailureCount++;
                    }
                    continue;
                }
                AzureStyleFillResult fillResult =
                    layer.EvaluateFill(
                        context,
                        out VectorFillStyle style,
                        out string? patternName);
                if (fillResult != AzureStyleFillResult.Resolved)
                {
                    if (fillResult == AzureStyleFillResult.EvaluationFailure)
                    {
                        evaluationFailureCount++;
                    }
                    continue;
                }
                if (patternName is not null)
                {
                    AzureSpriteLookupResult spriteResult =
                        _spriteAtlas.TryGetTexture(
                            patternName,
                            out VectorSpriteTextureData? texture,
                            out AzureSpriteEntry entry);
                    if (spriteResult is
                        AzureSpriteLookupResult.Missing or
                        AzureSpriteLookupResult.Hidden)
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
                            AzureSpriteAtlas.CreateTextureId(
                                AzureTileAcquisitionSession
                                    .GetAzureStyleName(_mapStyle),
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
        HashSet<AzureGlyphKey> glyphKeys,
        CancellationToken cancellationToken)
    {
        foreach (AzureTextStyleLayer layer in _symbolStyle.TextLayers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (layer.EvaluateVisibility(zoom) != AzureStyleVisibilityResult.Visible)
            {
                continue;
            }
            foreach (VectorTileFeature feature in features.GetSourceLayer(layer.SourceLayer))
            {
                if (layer.Placement == AzureSymbolPlacement.Line
                    ? feature.Lines.Length == 0
                    : feature.Points.Length == 0)
                {
                    continue;
                }
                AzureStyleEvaluationContext context = new(feature, zoom);
                if (layer.EvaluateFilter(context) != AzureStyleFilterResult.Match ||
                    layer.EvaluateText(context, out AzureTextStyle text) !=
                        AzureStyleTextResult.Resolved)
                {
                    continue;
                }
                foreach (Rune rune in text.Text.EnumerateRunes())
                {
                    if (rune.Value is not ('\r' or '\n') &&
                        rune.Value <= char.MaxValue)
                    {
                        glyphKeys.Add(new AzureGlyphKey(text.FontStack, rune.Value));
                    }
                }
            }
        }
    }

    private void ResolveText(
        VectorTileFeatureCollection features,
        double zoom,
        List<VectorTileSymbol> symbols,
        ref VectorStyleResolutionCounts counts,
        CancellationToken cancellationToken)
    {
        int nextLabelId = 0;
        foreach (AzureTextStyleLayer layer in _symbolStyle.TextLayers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AzureStyleVisibilityResult visibility = layer.EvaluateVisibility(zoom);
            if (visibility != AzureStyleVisibilityResult.Visible)
            {
                if (visibility == AzureStyleVisibilityResult.EvaluationFailure)
                {
                    counts.EvaluationFailureCount++;
                }
                continue;
            }
            foreach (VectorTileFeature feature in features.GetSourceLayer(layer.SourceLayer))
            {
                if (layer.Placement == AzureSymbolPlacement.Line
                    ? feature.Lines.Length == 0
                    : feature.Points.Length == 0)
                {
                    continue;
                }
                AzureStyleEvaluationContext context = new(feature, zoom);
                AzureStyleFilterResult filter = layer.EvaluateFilter(context);
                if (filter != AzureStyleFilterResult.Match)
                {
                    if (filter == AzureStyleFilterResult.EvaluationFailure)
                    {
                        counts.EvaluationFailureCount++;
                    }
                    continue;
                }
                if (layer.EvaluateText(context, out AzureTextStyle text) !=
                    AzureStyleTextResult.Resolved)
                {
                    counts.EvaluationFailureCount++;
                    continue;
                }
                if (layer.Placement == AzureSymbolPlacement.Line)
                {
                    foreach (VectorTileLine line in feature.Lines)
                    {
                        AddTextSymbols(
                            layer.Order,
                            default,
                            line.Points,
                            text,
                            nextLabelId++,
                            symbols,
                            ref counts);
                    }
                }
                else
                {
                    foreach (VectorTilePoint point in feature.Points)
                    {
                        AddTextSymbols(
                            layer.Order,
                            point,
                            null,
                            text,
                            nextLabelId++,
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
        AzureTextStyle text,
        int labelId,
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
        List<(AzureGlyph Glyph, VectorSpriteTextureData Texture, double X)>[] shaped =
            new List<(AzureGlyph, VectorSpriteTextureData, double)>[lines.Length];
        double[] widths = new double[lines.Length];
        double maximumWidth = 0;
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            shaped[lineIndex] = [];
            double pen = 0;
            foreach (Rune rune in lines[lineIndex].EnumerateRunes())
            {
                if (rune.Value > char.MaxValue)
                {
                    counts.UnavailableGlyphCount++;
                    continue;
                }
                if (!_glyphAtlas.TryGetOrCreateTexture(
                        new AzureGlyphKey(text.FontStack, rune.Value),
                        out AzureGlyph glyph,
                        out VectorSpriteTextureData? texture))
                {
                    counts.UnavailableGlyphCount++;
                    continue;
                }
                if (texture is not null)
                {
                    shaped[lineIndex].Add((glyph, texture, pen));
                }
                pen += (glyph.Advance * scale) +
                    (text.LetterSpacing * text.Size);
            }
            widths[lineIndex] = pen;
            maximumWidth = Math.Max(maximumWidth, pen);
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
            double baseline = baseY + (lineIndex * lineHeight) + text.Size;
            foreach ((AzureGlyph glyph, VectorSpriteTextureData texture, double x)
                in shaped[lineIndex])
            {
                double left = lineX + x +
                    ((glyph.Left - AzureGlyph.SdfBuffer) * scale);
                double top = baseline +
                    ((-glyph.Top - AzureGlyph.SdfBuffer) * scale);
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
                    text.LineSpacing));
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

    private VectorStyleResolutionCounts Resolve(
        VectorTileFeatureCollection features,
        double zoom,
        bool createTextures,
        Dictionary<long, VectorSpriteTextureData>? textures,
        List<VectorTileSymbol>? symbols,
        CancellationToken cancellationToken)
    {
        VectorStyleResolutionCounts counts = default;
        foreach (AzureSymbolStyleLayer layer in _symbolStyle.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AzureStyleVisibilityResult visibility =
                layer.EvaluateVisibility(zoom);
            if (visibility != AzureStyleVisibilityResult.Visible)
            {
                if (visibility == AzureStyleVisibilityResult.EvaluationFailure)
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
                if (layer.Placement == AzureSymbolPlacement.Line
                    ? feature.Lines.Length == 0
                    : feature.Points.Length == 0)
                {
                    continue;
                }
                AzureStyleEvaluationContext context = new(feature, zoom);
                AzureStyleFilterResult filter = layer.EvaluateFilter(context);
                if (filter != AzureStyleFilterResult.Match)
                {
                    if (filter == AzureStyleFilterResult.EvaluationFailure)
                    {
                        counts.EvaluationFailureCount++;
                    }
                    continue;
                }
                AzureStyleIconResult icon = layer.EvaluateIcon(
                        context,
                        out string spriteName,
                        out double iconSize,
                        out double offsetX,
                        out double offsetY,
                        out double anchorX,
                        out double anchorY,
                        out double spacing);
                if (icon != AzureStyleIconResult.Resolved)
                {
                    if (icon == AzureStyleIconResult.EvaluationFailure)
                    {
                        counts.EvaluationFailureCount++;
                    }
                    continue;
                }

                AzureSpriteLookupResult spriteResult = createTextures
                    ? _spriteAtlas.TryGetOrCreateTexture(
                        spriteName,
                        cancellationToken,
                        out VectorSpriteTextureData? texture,
                        out AzureSpriteEntry entry)
                    : _spriteAtlas.TryGetTexture(
                        spriteName,
                        out texture,
                        out entry);
                if (spriteResult != AzureSpriteLookupResult.Found ||
                    texture is null)
                {
                    counts.UnavailableSpriteCount += layer.Placement ==
                        AzureSymbolPlacement.Line
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

                if (layer.Placement == AzureSymbolPlacement.Line)
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
                            LineSpacing: spacing));
                    }
                }
                else
                {
                    foreach (VectorTilePoint point in feature.Points)
                    {
                        symbols.Add(new VectorTileSymbol(
                            layer.Order,
                            point.X,
                            point.Y,
                            texture.TextureId,
                            width,
                            height,
                            displayOffsetX,
                            displayOffsetY));
                    }
                }
            }
        }
        return counts;
    }

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
internal sealed class AzureSymbolStyle
{
    private const int MaximumStyleLayers = 4096;
    private const int MaximumSymbolLayers = 2048;
    private const int MaximumSourceLayerLength = 1024;
    private const int MaximumSourceNameLength = 1024;
    private readonly double[] _zoomStops;
    private readonly int[] _unsupportedLayerCounts;

    private AzureSymbolStyle(
        AzureSymbolStyleLayer[] layers,
        AzureTextStyleLayer[] textLayers,
        AzureLineStyleLayer[] lineLayers,
        AzureFillStyleLayer[] fillLayers,
        int[] unsupportedLayerCounts)
    {
        Layers = layers;
        TextLayers = textLayers;
        LineLayers = lineLayers;
        FillLayers = fillLayers;
        _unsupportedLayerCounts = unsupportedLayerCounts;
        List<double> stops = [];
        foreach (AzureSymbolStyleLayer layer in layers)
        {
            layer.CollectZoomStops(stops);
        }
        foreach (AzureTextStyleLayer layer in textLayers)
        {
            layer.CollectZoomStops(stops);
        }
        foreach (AzureLineStyleLayer layer in lineLayers)
        {
            layer.CollectZoomStops(stops);
        }
        foreach (AzureFillStyleLayer layer in fillLayers)
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

    internal AzureSymbolStyleLayer[] Layers { get; }

    internal AzureTextStyleLayer[] TextLayers { get; }

    internal AzureLineStyleLayer[] LineLayers { get; }

    internal AzureFillStyleLayer[] FillLayers { get; }

    internal int LayerCount =>
        Layers.Length + TextLayers.Length + LineLayers.Length + FillLayers.Length;

    internal int UnsupportedLayerCount =>
        _unsupportedLayerCounts.Sum();

    internal int GetUnsupportedLayerCount(
        AzureStyleLayerParseResult result) =>
        result == AzureStyleLayerParseResult.Parsed
            ? 0
            : _unsupportedLayerCounts[(int)result];

    internal static AzureSymbolStyle Parse(ReadOnlyMemory<byte> json)
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
                "The Azure vector style is not a supported Style Spec v8 document.");
        }

        List<AzureSymbolStyleLayer> parsed = [];
        List<AzureTextStyleLayer> parsedText = [];
        List<AzureLineStyleLayer> parsedLines = [];
        List<AzureFillStyleLayer> parsedFills = [];
        HashSet<string>? baseVectorSources = GetBaseVectorSources(root);
        int[] unsupportedLayerCounts =
            new int[Enum.GetValues<AzureStyleLayerParseResult>().Length];
        int layerCount = 0;
        foreach (JsonElement layer in layers.EnumerateArray())
        {
            if (++layerCount > MaximumStyleLayers)
            {
                throw new InvalidDataException(
                    "The Azure vector style contains too many layers.");
            }
            if (layer.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "The Azure vector style contains a non-object layer.");
            }
            if (!layer.TryGetProperty("type", out JsonElement type) ||
                type.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    "The Azure vector style contains a layer without a valid type.");
            }
            string? layerType = type.GetString();
            if (string.Equals(layerType, "fill", StringComparison.Ordinal))
            {
                AzureStyleLayerParseResult result = TryParseFillLayer(
                    layer,
                    baseVectorSources,
                    layerCount - 1,
                    out AzureFillStyleLayer? fillLayer);
                if (result == AzureStyleLayerParseResult.InvalidDefinition)
                {
                    throw new InvalidDataException(
                        "The Azure vector style contains an invalid fill layer.");
                }
                if (result != AzureStyleLayerParseResult.Parsed)
                {
                    unsupportedLayerCounts[(int)result]++;
                }
                else
                {
                    if (parsedFills.Count >= MaximumSymbolLayers)
                    {
                        throw new InvalidDataException(
                            "The Azure vector style contains too many fill layers.");
                    }
                    parsedFills.Add(fillLayer!);
                }
                continue;
            }
            if (string.Equals(layerType, "line", StringComparison.Ordinal))
            {
                AzureStyleLayerParseResult result = TryParseLineLayer(
                    layer,
                    baseVectorSources,
                    layerCount - 1,
                    out AzureLineStyleLayer? lineLayer);
                if (result == AzureStyleLayerParseResult.InvalidDefinition)
                {
                    throw new InvalidDataException(
                        "The Azure vector style contains an invalid line layer.");
                }
                if (result != AzureStyleLayerParseResult.Parsed)
                {
                    unsupportedLayerCounts[(int)result]++;
                }
                else
                {
                    if (parsedLines.Count >= MaximumSymbolLayers)
                    {
                        throw new InvalidDataException(
                            "The Azure vector style contains too many line layers.");
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
                AzureStyleLayerParseResult result = TryParseLayer(
                    layer,
                    layout,
                    baseVectorSources,
                    layerCount - 1,
                    out AzureSymbolStyleLayer? symbolLayer);
                if (result != AzureStyleLayerParseResult.Parsed)
                {
                    if (result == AzureStyleLayerParseResult.InvalidDefinition)
                    {
                        throw new InvalidDataException(
                            "The Azure vector style contains an invalid point-symbol layer.");
                    }
                    unsupportedLayerCounts[(int)result]++;
                }
                else
                {
                    if (parsed.Count >= MaximumSymbolLayers)
                    {
                        throw new InvalidDataException(
                            "The Azure vector style contains too many symbol layers.");
                    }
                    parsed.Add(symbolLayer!);
                }
            }

            if (layout.TryGetProperty("text-field", out _))
            {
                AzureStyleLayerParseResult result = TryParseTextLayer(
                    layer,
                    layout,
                    baseVectorSources,
                    layerCount - 1,
                    out AzureTextStyleLayer? textLayer);
                if (result != AzureStyleLayerParseResult.Parsed)
                {
                    if (result == AzureStyleLayerParseResult.InvalidDefinition)
                    {
                        throw new InvalidDataException(
                            "The Azure vector style contains an invalid point-label layer.");
                    }
                    unsupportedLayerCounts[(int)result]++;
                }
                else
                {
                    if (parsedText.Count >= MaximumSymbolLayers)
                    {
                        throw new InvalidDataException(
                            "The Azure vector style contains too many text layers.");
                    }
                    parsedText.Add(textLayer!);
                }
            }
        }
        return new AzureSymbolStyle(
            parsed.ToArray(),
            parsedText.ToArray(),
            parsedLines.ToArray(),
            parsedFills.ToArray(),
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

    private static AzureStyleLayerParseResult TryParseLayer(
        JsonElement layer,
        JsonElement layout,
        IReadOnlySet<string>? baseVectorSources,
        int order,
        out AzureSymbolStyleLayer? parsed)
    {
        parsed = null;
        if (baseVectorSources is not null &&
            (!layer.TryGetProperty("source", out JsonElement sourceElement) ||
             sourceElement.ValueKind != JsonValueKind.String ||
             sourceElement.GetString() is not string source ||
             !baseVectorSources.Contains(source)))
        {
            return AzureStyleLayerParseResult.UnsupportedVectorSource;
        }
        if (!layer.TryGetProperty("source-layer", out JsonElement sourceLayerElement) ||
            sourceLayerElement.ValueKind != JsonValueKind.String)
        {
            return AzureStyleLayerParseResult.UnsupportedSourceLayer;
        }
        string? sourceLayer = sourceLayerElement.GetString();
        if (string.IsNullOrEmpty(sourceLayer) ||
            sourceLayer.Length > MaximumSourceLayerLength)
        {
            return AzureStyleLayerParseResult.InvalidDefinition;
        }

        if (layout.TryGetProperty("icon-text-fit", out JsonElement iconTextFit) &&
            (iconTextFit.ValueKind != JsonValueKind.String ||
             !string.Equals(
                 iconTextFit.GetString(),
                 "none",
                 StringComparison.Ordinal)))
        {
            return AzureStyleLayerParseResult.UnsupportedTextFit;
        }
        if (layout.TryGetProperty("icon-rotate", out JsonElement iconRotate) &&
            (!iconRotate.TryGetDouble(out double rotation) ||
             !double.IsFinite(rotation) ||
             rotation != 0))
        {
            return AzureStyleLayerParseResult.UnsupportedIconRotation;
        }

        if (!layout.TryGetProperty("icon-image", out JsonElement iconImage) ||
            !AzureStyleExpression.TryParse(iconImage, out AzureStyleExpression? iconImageExpression))
        {
            return AzureStyleLayerParseResult.UnsupportedExpression;
        }
        if (!TryParseSymbolPlacement(
                layout,
                out AzureSymbolPlacement placement))
        {
            return AzureStyleLayerParseResult.UnsupportedSymbolPlacement;
        }

        if (!TryParseOptionalExpression(
                layout,
                "visibility",
                AzureStyleValue.FromString("visible"),
                out AzureStyleExpression visibility) ||
            !TryParseOptionalExpression(
                layout,
                "icon-size",
                AzureStyleValue.FromNumber(1),
                out AzureStyleExpression iconSize) ||
            !TryParseOptionalExpression(
                layout,
                "icon-offset",
                AzureStyleValue.FromArray(
                    [AzureStyleValue.FromNumber(0), AzureStyleValue.FromNumber(0)]),
                out AzureStyleExpression iconOffset) ||
            !TryParseOptionalExpression(
                layout,
                "icon-anchor",
                AzureStyleValue.FromString("center"),
                out AzureStyleExpression iconAnchor) ||
            !TryParseOptionalExpression(
                layout,
                "symbol-spacing",
                AzureStyleValue.FromNumber(250),
                out AzureStyleExpression symbolSpacing))
        {
            return AzureStyleLayerParseResult.UnsupportedExpression;
        }

        AzureStyleExpression filter = AzureStyleExpression.Literal(
            AzureStyleValue.FromBoolean(true));
        if (layer.TryGetProperty("filter", out JsonElement filterElement) &&
            !AzureStyleExpression.TryParse(filterElement, out filter))
        {
            return AzureStyleLayerParseResult.UnsupportedExpression;
        }

        double minimumZoom = 0;
        double maximumZoom = MapCamera.MaximumTileZoom + 1;
        if (layer.TryGetProperty("minzoom", out JsonElement minimumElement) &&
            (!minimumElement.TryGetDouble(out minimumZoom) ||
             !double.IsFinite(minimumZoom)))
        {
            return AzureStyleLayerParseResult.InvalidDefinition;
        }
        if (layer.TryGetProperty("maxzoom", out JsonElement maximumElement) &&
            (!maximumElement.TryGetDouble(out maximumZoom) ||
             !double.IsFinite(maximumZoom)))
        {
            return AzureStyleLayerParseResult.InvalidDefinition;
        }
        if (maximumZoom <= minimumZoom)
        {
            return AzureStyleLayerParseResult.InvalidDefinition;
        }

        parsed = new AzureSymbolStyleLayer(
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
            iconAnchor);
        return AzureStyleLayerParseResult.Parsed;
    }

    private static AzureStyleLayerParseResult TryParseTextLayer(
        JsonElement layer,
        JsonElement layout,
        IReadOnlySet<string>? baseVectorSources,
        int order,
        out AzureTextStyleLayer? parsed)
    {
        parsed = null;
        if (baseVectorSources is not null &&
            (!layer.TryGetProperty("source", out JsonElement sourceElement) ||
             sourceElement.ValueKind != JsonValueKind.String ||
             sourceElement.GetString() is not string source ||
             !baseVectorSources.Contains(source)))
        {
            return AzureStyleLayerParseResult.UnsupportedVectorSource;
        }
        if (!layer.TryGetProperty("source-layer", out JsonElement sourceLayerElement) ||
            sourceLayerElement.ValueKind != JsonValueKind.String)
        {
            return AzureStyleLayerParseResult.UnsupportedSourceLayer;
        }
        string? sourceLayer = sourceLayerElement.GetString();
        if (string.IsNullOrEmpty(sourceLayer) ||
            sourceLayer.Length > MaximumSourceLayerLength)
        {
            return AzureStyleLayerParseResult.InvalidDefinition;
        }
        if (!TryParseSymbolPlacement(
                layout,
                out AzureSymbolPlacement placement))
        {
            return AzureStyleLayerParseResult.UnsupportedSymbolPlacement;
        }
        if (!layout.TryGetProperty("text-field", out JsonElement textField) ||
            !AzureStyleExpression.TryParse(
                textField,
                out AzureStyleExpression textFieldExpression) ||
            !TryParseOptionalExpression(
                layout,
                "visibility",
                AzureStyleValue.FromString("visible"),
                out AzureStyleExpression visibility) ||
            !TryParseOptionalLiteralExpression(
                layout,
                "text-font",
                AzureStyleValue.FromArray(
                    [AzureStyleValue.FromString("Roboto-Regular")]),
                out AzureStyleExpression textFont) ||
            !TryParseOptionalExpression(
                layout,
                "text-size",
                AzureStyleValue.FromNumber(16),
                out AzureStyleExpression textSize) ||
            !TryParseOptionalExpression(
                layout,
                "text-offset",
                AzureStyleValue.FromArray(
                    [AzureStyleValue.FromNumber(0), AzureStyleValue.FromNumber(0)]),
                out AzureStyleExpression textOffset) ||
            !TryParseOptionalExpression(
                layout,
                "text-anchor",
                AzureStyleValue.FromString("center"),
                out AzureStyleExpression textAnchor) ||
            !TryParseOptionalExpression(
                layout,
                "text-radial-offset",
                AzureStyleValue.FromNumber(0),
                out AzureStyleExpression textRadialOffset) ||
            !TryParseOptionalExpression(
                layout,
                "text-letter-spacing",
                AzureStyleValue.FromNumber(0),
                out AzureStyleExpression textLetterSpacing) ||
            !TryParseOptionalExpression(
                layout,
                "text-transform",
                AzureStyleValue.FromString("none"),
                out AzureStyleExpression textTransform) ||
            !TryParseOptionalExpression(
                layout,
                "symbol-spacing",
                AzureStyleValue.FromNumber(250),
                out AzureStyleExpression symbolSpacing))
        {
            return AzureStyleLayerParseResult.UnsupportedExpression;
        }

        AzureStyleExpression textVariableAnchor =
            AzureStyleExpression.Literal(AzureStyleValue.Null);
        if (layout.TryGetProperty(
                "text-variable-anchor",
                out JsonElement variableAnchor) &&
            !AzureStyleExpression.TryParseLiteralExpression(
                variableAnchor,
                out textVariableAnchor))
        {
            return AzureStyleLayerParseResult.UnsupportedExpression;
        }

        JsonElement paint = default;
        bool hasPaint = layer.TryGetProperty("paint", out paint);
        if (hasPaint && paint.ValueKind != JsonValueKind.Object)
        {
            return AzureStyleLayerParseResult.InvalidDefinition;
        }
        if (!TryParseOptionalExpression(
                hasPaint ? paint : default,
                "text-color",
                AzureStyleValue.FromString("#000000"),
                out AzureStyleExpression textColor) ||
            !TryParseOptionalExpression(
                hasPaint ? paint : default,
                "text-halo-color",
                AzureStyleValue.FromString("#00000000"),
                out AzureStyleExpression textHaloColor) ||
            !TryParseOptionalExpression(
                hasPaint ? paint : default,
                "text-halo-width",
                AzureStyleValue.FromNumber(0),
                out AzureStyleExpression textHaloWidth))
        {
            return AzureStyleLayerParseResult.UnsupportedExpression;
        }

        AzureStyleExpression filter = AzureStyleExpression.Literal(
            AzureStyleValue.FromBoolean(true));
        if (layer.TryGetProperty("filter", out JsonElement filterElement) &&
            !AzureStyleExpression.TryParse(filterElement, out filter))
        {
            return AzureStyleLayerParseResult.UnsupportedExpression;
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
            return AzureStyleLayerParseResult.InvalidDefinition;
        }

        parsed = new AzureTextStyleLayer(
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
            textColor,
            textHaloColor,
            textHaloWidth);
        return AzureStyleLayerParseResult.Parsed;
    }

    private static bool TryParseSymbolPlacement(
        JsonElement layout,
        out AzureSymbolPlacement placement)
    {
        placement = AzureSymbolPlacement.Point;
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
            "point" => AzureSymbolPlacement.Point,
            "line" => AzureSymbolPlacement.Line,
            _ => (AzureSymbolPlacement)(-1),
        };
        return (int)placement >= 0;
    }

    private static AzureStyleLayerParseResult TryParseFillLayer(
        JsonElement layer,
        IReadOnlySet<string>? baseVectorSources,
        int order,
        out AzureFillStyleLayer? parsed)
    {
        parsed = null;
        if (baseVectorSources is not null &&
            (!layer.TryGetProperty("source", out JsonElement sourceElement) ||
             sourceElement.ValueKind != JsonValueKind.String ||
             sourceElement.GetString() is not string source ||
             !baseVectorSources.Contains(source)))
        {
            return AzureStyleLayerParseResult.UnsupportedVectorSource;
        }
        if (!layer.TryGetProperty("source-layer", out JsonElement sourceLayerElement) ||
            sourceLayerElement.ValueKind != JsonValueKind.String)
        {
            return AzureStyleLayerParseResult.UnsupportedSourceLayer;
        }
        string? sourceLayer = sourceLayerElement.GetString();
        if (string.IsNullOrEmpty(sourceLayer) ||
            sourceLayer.Length > MaximumSourceLayerLength)
        {
            return AzureStyleLayerParseResult.InvalidDefinition;
        }

        JsonElement layout = default;
        if (layer.TryGetProperty("layout", out layout) &&
            layout.ValueKind != JsonValueKind.Object)
        {
            return AzureStyleLayerParseResult.InvalidDefinition;
        }
        JsonElement paint = default;
        if (layer.TryGetProperty("paint", out paint) &&
            paint.ValueKind != JsonValueKind.Object)
        {
            return AzureStyleLayerParseResult.InvalidDefinition;
        }
        if (!TryParseOptionalExpression(
                layout,
                "visibility",
                AzureStyleValue.FromString("visible"),
                out AzureStyleExpression visibility) ||
            !TryParseOptionalExpression(
                paint,
                "fill-color",
                AzureStyleValue.FromString("#000000"),
                out AzureStyleExpression fillColor) ||
            !TryParseOptionalExpression(
                paint,
                "fill-opacity",
                AzureStyleValue.FromNumber(1),
                out AzureStyleExpression fillOpacity) ||
            !TryParseOptionalExpression(
                paint,
                "fill-outline-color",
                AzureStyleValue.Null,
                out AzureStyleExpression fillOutlineColor) ||
            !TryParseOptionalExpression(
                paint,
                "fill-pattern",
                AzureStyleValue.Null,
                out AzureStyleExpression fillPattern))
        {
            return AzureStyleLayerParseResult.UnsupportedExpression;
        }

        AzureStyleExpression filter = AzureStyleExpression.Literal(
            AzureStyleValue.FromBoolean(true));
        if (layer.TryGetProperty("filter", out JsonElement filterElement) &&
            !AzureStyleExpression.TryParse(filterElement, out filter))
        {
            return AzureStyleLayerParseResult.UnsupportedExpression;
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
            return AzureStyleLayerParseResult.InvalidDefinition;
        }

        parsed = new AzureFillStyleLayer(
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
        return AzureStyleLayerParseResult.Parsed;
    }

    private static AzureStyleLayerParseResult TryParseLineLayer(
        JsonElement layer,
        IReadOnlySet<string>? baseVectorSources,
        int order,
        out AzureLineStyleLayer? parsed)
    {
        parsed = null;
        if (baseVectorSources is not null &&
            (!layer.TryGetProperty("source", out JsonElement sourceElement) ||
             sourceElement.ValueKind != JsonValueKind.String ||
             sourceElement.GetString() is not string source ||
             !baseVectorSources.Contains(source)))
        {
            return AzureStyleLayerParseResult.UnsupportedVectorSource;
        }
        if (!layer.TryGetProperty("source-layer", out JsonElement sourceLayerElement) ||
            sourceLayerElement.ValueKind != JsonValueKind.String)
        {
            return AzureStyleLayerParseResult.UnsupportedSourceLayer;
        }
        string? sourceLayer = sourceLayerElement.GetString();
        if (string.IsNullOrEmpty(sourceLayer) ||
            sourceLayer.Length > MaximumSourceLayerLength)
        {
            return AzureStyleLayerParseResult.InvalidDefinition;
        }

        JsonElement layout = default;
        if (layer.TryGetProperty("layout", out layout) &&
            layout.ValueKind != JsonValueKind.Object)
        {
            return AzureStyleLayerParseResult.InvalidDefinition;
        }
        JsonElement paint = default;
        if (layer.TryGetProperty("paint", out paint) &&
            paint.ValueKind != JsonValueKind.Object)
        {
            return AzureStyleLayerParseResult.InvalidDefinition;
        }
        if (!TryParseOptionalExpression(
                layout,
                "visibility",
                AzureStyleValue.FromString("visible"),
                out AzureStyleExpression visibility) ||
            !TryParseOptionalExpression(
                layout,
                "line-cap",
                AzureStyleValue.FromString("butt"),
                out AzureStyleExpression lineCap) ||
            !TryParseOptionalExpression(
                layout,
                "line-join",
                AzureStyleValue.FromString("miter"),
                out AzureStyleExpression lineJoin) ||
            !TryParseOptionalExpression(
                paint,
                "line-color",
                AzureStyleValue.FromString("#000000"),
                out AzureStyleExpression lineColor) ||
            !TryParseOptionalExpression(
                paint,
                "line-opacity",
                AzureStyleValue.FromNumber(1),
                out AzureStyleExpression lineOpacity) ||
            !TryParseOptionalExpression(
                paint,
                "line-width",
                AzureStyleValue.FromNumber(1),
                out AzureStyleExpression lineWidth) ||
            !TryParseOptionalExpression(
                paint,
                "line-dasharray",
                AzureStyleValue.FromArray([]),
                out AzureStyleExpression lineDashArray) ||
            !TryParseOptionalExpression(
                paint,
                "line-pattern",
                AzureStyleValue.Null,
                out AzureStyleExpression linePattern))
        {
            return AzureStyleLayerParseResult.UnsupportedExpression;
        }

        AzureStyleExpression filter = AzureStyleExpression.Literal(
            AzureStyleValue.FromBoolean(true));
        if (layer.TryGetProperty("filter", out JsonElement filterElement) &&
            !AzureStyleExpression.TryParse(filterElement, out filter))
        {
            return AzureStyleLayerParseResult.UnsupportedExpression;
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
            return AzureStyleLayerParseResult.InvalidDefinition;
        }

        parsed = new AzureLineStyleLayer(
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
            linePattern);
        return AzureStyleLayerParseResult.Parsed;
    }

    private static HashSet<string>? GetBaseVectorSources(JsonElement root)
    {
        if (!root.TryGetProperty("sources", out JsonElement sources))
        {
            return null;
        }
        if (sources.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The Azure vector style contains an invalid sources object.");
        }

        HashSet<string> result = new(StringComparer.Ordinal);
        foreach (JsonProperty source in sources.EnumerateObject())
        {
            if (source.Name.Length > MaximumSourceNameLength ||
                source.Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "The Azure vector style contains an invalid source.");
            }
            if (source.Value.TryGetProperty("type", out JsonElement type) &&
                type.ValueKind == JsonValueKind.String &&
                string.Equals(type.GetString(), "vector", StringComparison.Ordinal) &&
                source.Value.TryGetProperty("url", out JsonElement url) &&
                url.ValueKind == JsonValueKind.String &&
                url.GetString() is string sourceUrl &&
                sourceUrl.Contains(
                    "tilesetId=microsoft.base",
                    StringComparison.Ordinal))
            {
                result.Add(source.Name);
            }
        }
        if (result.Count == 0)
        {
            throw new InvalidDataException(
                "The Azure vector style does not define the Azure base vector source.");
        }
        return result;
    }

    private static bool TryParseOptionalExpression(
        JsonElement owner,
        string propertyName,
        AzureStyleValue defaultValue,
        out AzureStyleExpression expression)
    {
        if (owner.ValueKind != JsonValueKind.Object ||
            !owner.TryGetProperty(propertyName, out JsonElement value))
        {
            expression = AzureStyleExpression.Literal(defaultValue);
            return true;
        }
        return AzureStyleExpression.TryParse(value, out expression);
    }

    private static bool TryParseOptionalLiteralExpression(
        JsonElement owner,
        string propertyName,
        AzureStyleValue defaultValue,
        out AzureStyleExpression expression)
    {
        if (owner.ValueKind != JsonValueKind.Object ||
            !owner.TryGetProperty(propertyName, out JsonElement value))
        {
            expression = AzureStyleExpression.Literal(defaultValue);
            return true;
        }
        return AzureStyleExpression.TryParseLiteralExpression(
            value,
            out expression);
    }
}

internal sealed class AzureSymbolStyleLayer(
    int order,
    string sourceLayer,
    double minimumZoom,
    double maximumZoom,
    AzureStyleExpression visibility,
    AzureStyleExpression filter,
    AzureSymbolPlacement placement,
    AzureStyleExpression symbolSpacing,
    AzureStyleExpression iconImage,
    AzureStyleExpression iconSize,
    AzureStyleExpression iconOffset,
    AzureStyleExpression iconAnchor)
{
    internal int Order { get; } = order;

    internal string SourceLayer { get; } = sourceLayer;

    internal AzureSymbolPlacement Placement { get; } = placement;

    internal AzureStyleVisibilityResult EvaluateVisibility(double zoom)
    {
        if (zoom < minimumZoom || zoom >= maximumZoom)
        {
            return AzureStyleVisibilityResult.Hidden;
        }
        AzureStyleEvaluationContext context = new(null, zoom);
        if (!visibility.TryEvaluate(context, out AzureStyleValue value) ||
            value.Kind != AzureStyleValueKind.String)
        {
            return AzureStyleVisibilityResult.EvaluationFailure;
        }
        return string.Equals(value.StringValue, "none", StringComparison.Ordinal)
            ? AzureStyleVisibilityResult.Hidden
            : AzureStyleVisibilityResult.Visible;
    }

    internal AzureStyleFilterResult EvaluateFilter(
        AzureStyleEvaluationContext context)
    {
        if (!filter.TryEvaluate(context, out AzureStyleValue result) ||
            result.Kind != AzureStyleValueKind.Boolean)
        {
            return AzureStyleFilterResult.EvaluationFailure;
        }
        return result.BooleanValue
            ? AzureStyleFilterResult.Match
            : AzureStyleFilterResult.NoMatch;
    }

    internal AzureStyleIconResult EvaluateIcon(
        AzureStyleEvaluationContext context,
        out string spriteName,
        out double size,
        out double offsetX,
        out double offsetY,
        out double anchorX,
        out double anchorY,
        out double spacing)
    {
        spriteName = string.Empty;
        size = 0;
        offsetX = 0;
        offsetY = 0;
        anchorX = 0;
        anchorY = 0;
        spacing = 0;
        if (!iconImage.TryEvaluate(context, out AzureStyleValue image) ||
            image.Kind != AzureStyleValueKind.String)
        {
            return AzureStyleIconResult.EvaluationFailure;
        }
        if (string.IsNullOrEmpty(image.StringValue))
        {
            return AzureStyleIconResult.NoIcon;
        }
        if (!iconSize.TryEvaluate(context, out AzureStyleValue sizeValue) ||
            !sizeValue.TryGetNumber(out size) ||
            !double.IsFinite(size))
        {
            return AzureStyleIconResult.EvaluationFailure;
        }
        if (size <= 0)
        {
            return AzureStyleIconResult.NoIcon;
        }
        if (!iconOffset.TryEvaluate(context, out AzureStyleValue offset) ||
            offset.Kind != AzureStyleValueKind.Array ||
            offset.ArrayValue is null ||
            offset.ArrayValue.Length != 2 ||
            !offset.ArrayValue[0].TryGetNumber(out offsetX) ||
            !offset.ArrayValue[1].TryGetNumber(out offsetY) ||
            !double.IsFinite(offsetX) ||
            !double.IsFinite(offsetY) ||
            !iconAnchor.TryEvaluate(context, out AzureStyleValue anchor) ||
            anchor.Kind != AzureStyleValueKind.String ||
            !TryGetAnchorOffsets(anchor.StringValue, out anchorX, out anchorY) ||
            !symbolSpacing.TryEvaluate(context, out AzureStyleValue spacingValue) ||
            !spacingValue.TryGetNumber(out spacing) ||
            !double.IsFinite(spacing) ||
            spacing <= 0)
        {
            return AzureStyleIconResult.EvaluationFailure;
        }
        spriteName = image.StringValue;
        return AzureStyleIconResult.Resolved;
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
    }
}

internal sealed class AzureFillStyleLayer(
    int order,
    string sourceLayer,
    double minimumZoom,
    double maximumZoom,
    AzureStyleExpression visibility,
    AzureStyleExpression filter,
    AzureStyleExpression fillColor,
    AzureStyleExpression fillOpacity,
    AzureStyleExpression fillOutlineColor,
    AzureStyleExpression fillPattern)
{
    internal int Order { get; } = order;

    internal string SourceLayer { get; } = sourceLayer;

    internal AzureStyleVisibilityResult EvaluateVisibility(double zoom)
    {
        if (zoom < minimumZoom || zoom >= maximumZoom)
        {
            return AzureStyleVisibilityResult.Hidden;
        }
        AzureStyleEvaluationContext context = new(null, zoom);
        if (!visibility.TryEvaluate(context, out AzureStyleValue value) ||
            value.Kind != AzureStyleValueKind.String)
        {
            return AzureStyleVisibilityResult.EvaluationFailure;
        }
        return string.Equals(value.StringValue, "none", StringComparison.Ordinal)
            ? AzureStyleVisibilityResult.Hidden
            : AzureStyleVisibilityResult.Visible;
    }

    internal AzureStyleFilterResult EvaluateFilter(
        AzureStyleEvaluationContext context)
    {
        if (!filter.TryEvaluate(context, out AzureStyleValue value) ||
            value.Kind != AzureStyleValueKind.Boolean)
        {
            return AzureStyleFilterResult.EvaluationFailure;
        }
        return value.BooleanValue
            ? AzureStyleFilterResult.Match
            : AzureStyleFilterResult.NoMatch;
    }

    internal AzureStyleFillResult EvaluateFill(
        AzureStyleEvaluationContext context,
        out VectorFillStyle result,
        out string? patternName)
    {
        result = default;
        patternName = null;
        if (!fillColor.TryEvaluate(context, out AzureStyleValue colorValue) ||
            !AzureTextStyleLayer.TryParseColor(colorValue, out Vector4 color) ||
            !fillOpacity.TryEvaluate(context, out AzureStyleValue opacityValue) ||
            !opacityValue.TryGetNumber(out double opacity) ||
            !double.IsFinite(opacity) ||
            !fillOutlineColor.TryEvaluate(
                context,
                out AzureStyleValue outlineValue) ||
            !TryResolveOptionalColor(
                outlineValue,
                out Vector4? outlineColor) ||
            !fillPattern.TryEvaluate(context, out AzureStyleValue patternValue) ||
            !TryResolvePattern(patternValue, out patternName))
        {
            return AzureStyleFillResult.EvaluationFailure;
        }
        opacity = Math.Clamp(opacity, 0, 1);
        if (opacity <= 0 ||
            patternName is null && color.W <= 0 &&
            (outlineColor is not Vector4 visibleOutline ||
             visibleOutline.W <= 0))
        {
            return AzureStyleFillResult.Hidden;
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
        return AzureStyleFillResult.Resolved;
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
        AzureStyleValue value,
        out Vector4? color)
    {
        color = null;
        if (value.Kind == AzureStyleValueKind.Null)
        {
            return true;
        }
        if (!AzureTextStyleLayer.TryParseColor(value, out Vector4 resolved))
        {
            return false;
        }
        color = resolved;
        return true;
    }

    private static bool TryResolvePattern(
        AzureStyleValue value,
        out string? patternName)
    {
        patternName = null;
        if (value.Kind == AzureStyleValueKind.Null)
        {
            return true;
        }
        if (value.Kind != AzureStyleValueKind.String ||
            string.IsNullOrWhiteSpace(value.StringValue))
        {
            return false;
        }
        patternName = value.StringValue;
        return true;
    }
}

internal sealed class AzureLineStyleLayer(
    int order,
    string sourceLayer,
    double minimumZoom,
    double maximumZoom,
    AzureStyleExpression visibility,
    AzureStyleExpression filter,
    AzureStyleExpression lineColor,
    AzureStyleExpression lineOpacity,
    AzureStyleExpression lineWidth,
    AzureStyleExpression lineCap,
    AzureStyleExpression lineJoin,
    AzureStyleExpression lineDashArray,
    AzureStyleExpression linePattern)
{
    internal int Order { get; } = order;

    internal string SourceLayer { get; } = sourceLayer;

    internal AzureStyleVisibilityResult EvaluateVisibility(double zoom)
    {
        if (zoom < minimumZoom || zoom >= maximumZoom)
        {
            return AzureStyleVisibilityResult.Hidden;
        }
        AzureStyleEvaluationContext context = new(null, zoom);
        if (!visibility.TryEvaluate(context, out AzureStyleValue value) ||
            value.Kind != AzureStyleValueKind.String)
        {
            return AzureStyleVisibilityResult.EvaluationFailure;
        }
        return string.Equals(value.StringValue, "none", StringComparison.Ordinal)
            ? AzureStyleVisibilityResult.Hidden
            : AzureStyleVisibilityResult.Visible;
    }

    internal AzureStyleFilterResult EvaluateFilter(
        AzureStyleEvaluationContext context)
    {
        if (!filter.TryEvaluate(context, out AzureStyleValue value) ||
            value.Kind != AzureStyleValueKind.Boolean)
        {
            return AzureStyleFilterResult.EvaluationFailure;
        }
        return value.BooleanValue
            ? AzureStyleFilterResult.Match
            : AzureStyleFilterResult.NoMatch;
    }

    internal AzureStyleLineResult EvaluateLine(
        AzureStyleEvaluationContext context,
        out VectorLineStyle result)
    {
        result = default;
        if (!lineColor.TryEvaluate(context, out AzureStyleValue colorValue) ||
            !AzureTextStyleLayer.TryParseColor(colorValue, out Vector4 color) ||
            !lineOpacity.TryEvaluate(context, out AzureStyleValue opacityValue) ||
            !opacityValue.TryGetNumber(out double opacity) ||
            !double.IsFinite(opacity) ||
            !lineWidth.TryEvaluate(context, out AzureStyleValue widthValue) ||
            !widthValue.TryGetNumber(out double width) ||
            !double.IsFinite(width) ||
            !lineCap.TryEvaluate(context, out AzureStyleValue capValue) ||
            capValue.Kind != AzureStyleValueKind.String ||
            !TryGetCap(capValue.StringValue, out VectorLineCap cap) ||
            !lineJoin.TryEvaluate(context, out AzureStyleValue joinValue) ||
            joinValue.Kind != AzureStyleValueKind.String ||
            !TryGetJoin(joinValue.StringValue, out VectorLineJoin join))
        {
            return AzureStyleLineResult.EvaluationFailure;
        }
        if (opacity <= 0 || width <= 0 || color.W <= 0)
        {
            return AzureStyleLineResult.Hidden;
        }
        if (width > 256)
        {
            return AzureStyleLineResult.EvaluationFailure;
        }
        if (!lineDashArray.TryEvaluate(context, out AzureStyleValue dashValue) ||
            !TryResolveDashArray(
                dashValue,
                width,
                out ImmutableArray<double> dashArray))
        {
            return AzureStyleLineResult.EvaluationFailure;
        }
        color *= (float)Math.Clamp(opacity, 0, 1);
        result = new VectorLineStyle(color, width, cap, join, dashArray);
        return AzureStyleLineResult.Resolved;
    }

    internal bool TryEvaluatePattern(
        AzureStyleEvaluationContext context,
        out string? patternName,
        out double opacity)
    {
        patternName = null;
        opacity = 0;
        if (!linePattern.TryEvaluate(context, out AzureStyleValue patternValue))
        {
            return false;
        }
        if (patternValue.Kind == AzureStyleValueKind.Null)
        {
            return true;
        }
        if (patternValue.Kind != AzureStyleValueKind.String ||
            string.IsNullOrWhiteSpace(patternValue.StringValue) ||
            !lineOpacity.TryEvaluate(context, out AzureStyleValue opacityValue) ||
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
    }

    private static bool TryResolveDashArray(
        AzureStyleValue value,
        double lineWidth,
        out ImmutableArray<double> dashArray)
    {
        dashArray = [];
        if (value.Kind == AzureStyleValueKind.Null ||
            value is { Kind: AzureStyleValueKind.Array, ArrayValue.Length: 0 })
        {
            return true;
        }
        if (value is not
            {
                Kind: AzureStyleValueKind.Array,
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

internal sealed class AzureTextStyleLayer(
    int order,
    string sourceLayer,
    double minimumZoom,
    double maximumZoom,
    AzureStyleExpression visibility,
    AzureStyleExpression filter,
    AzureSymbolPlacement placement,
    AzureStyleExpression symbolSpacing,
    AzureStyleExpression textField,
    AzureStyleExpression textFont,
    AzureStyleExpression textSize,
    AzureStyleExpression textOffset,
    AzureStyleExpression textAnchor,
    AzureStyleExpression textVariableAnchor,
    AzureStyleExpression textRadialOffset,
    AzureStyleExpression textLetterSpacing,
    AzureStyleExpression textTransform,
    AzureStyleExpression textColor,
    AzureStyleExpression textHaloColor,
    AzureStyleExpression textHaloWidth)
{
    internal int Order { get; } = order;

    internal string SourceLayer { get; } = sourceLayer;

    internal AzureSymbolPlacement Placement { get; } = placement;

    internal AzureStyleVisibilityResult EvaluateVisibility(double zoom)
    {
        if (zoom < minimumZoom || zoom >= maximumZoom)
        {
            return AzureStyleVisibilityResult.Hidden;
        }
        AzureStyleEvaluationContext context = new(null, zoom);
        if (!visibility.TryEvaluate(context, out AzureStyleValue value) ||
            value.Kind != AzureStyleValueKind.String)
        {
            return AzureStyleVisibilityResult.EvaluationFailure;
        }
        return string.Equals(value.StringValue, "none", StringComparison.Ordinal)
            ? AzureStyleVisibilityResult.Hidden
            : AzureStyleVisibilityResult.Visible;
    }

    internal AzureStyleFilterResult EvaluateFilter(
        AzureStyleEvaluationContext context)
    {
        if (!filter.TryEvaluate(context, out AzureStyleValue value) ||
            value.Kind != AzureStyleValueKind.Boolean)
        {
            return AzureStyleFilterResult.EvaluationFailure;
        }
        return value.BooleanValue
            ? AzureStyleFilterResult.Match
            : AzureStyleFilterResult.NoMatch;
    }

    internal AzureStyleTextResult EvaluateText(
        AzureStyleEvaluationContext context,
        out AzureTextStyle result)
    {
        result = default;
        if (!textField.TryEvaluate(context, out AzureStyleValue field) ||
            field.Kind != AzureStyleValueKind.String)
        {
            return AzureStyleTextResult.EvaluationFailure;
        }
        string text = field.StringValue ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return AzureStyleTextResult.NoText;
        }
        if (!textFont.TryEvaluate(context, out AzureStyleValue fontValue) ||
            !TryGetFontStack(fontValue, out string fontStack) ||
            !textSize.TryEvaluate(context, out AzureStyleValue sizeValue) ||
            !sizeValue.TryGetNumber(out double size) ||
            !double.IsFinite(size) ||
            !textOffset.TryEvaluate(context, out AzureStyleValue offsetValue) ||
            !TryGetPair(offsetValue, out double offsetX, out double offsetY) ||
            !textAnchor.TryEvaluate(context, out AzureStyleValue anchorValue) ||
            anchorValue.Kind != AzureStyleValueKind.String ||
            !TryGetAnchor(anchorValue.StringValue, out string anchor) ||
            !textRadialOffset.TryEvaluate(
                context,
                out AzureStyleValue radialOffsetValue) ||
            !radialOffsetValue.TryGetNumber(out double radialOffset) ||
            !double.IsFinite(radialOffset) ||
            !textLetterSpacing.TryEvaluate(
                context,
                out AzureStyleValue letterSpacingValue) ||
            !letterSpacingValue.TryGetNumber(out double letterSpacing) ||
            !double.IsFinite(letterSpacing) ||
            !textTransform.TryEvaluate(context, out AzureStyleValue transformValue) ||
            transformValue.Kind != AzureStyleValueKind.String ||
            !textColor.TryEvaluate(context, out AzureStyleValue colorValue) ||
            !TryParseColor(colorValue, out Vector4 color) ||
            !textHaloColor.TryEvaluate(context, out AzureStyleValue haloColorValue) ||
            !TryParseColor(haloColorValue, out Vector4 haloColor) ||
            !textHaloWidth.TryEvaluate(context, out AzureStyleValue haloWidthValue) ||
            !haloWidthValue.TryGetNumber(out double haloWidth) ||
            !double.IsFinite(haloWidth) ||
            !symbolSpacing.TryEvaluate(context, out AzureStyleValue spacingValue) ||
            !spacingValue.TryGetNumber(out double spacing) ||
            !double.IsFinite(spacing) ||
            spacing <= 0)
        {
            return AzureStyleTextResult.EvaluationFailure;
        }
        if (size <= 0 || size > 256 || haloWidth < 0 || haloWidth > 32)
        {
            return AzureStyleTextResult.NoText;
        }
        if (textVariableAnchor.TryEvaluate(
                context,
                out AzureStyleValue variableAnchorValue) &&
            variableAnchorValue.Kind != AzureStyleValueKind.Null &&
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
            return AzureStyleTextResult.EvaluationFailure;
        }
        result = new AzureTextStyle(
            text,
            fontStack,
            size,
            offsetX,
            offsetY,
            anchor,
            radialOffset,
            letterSpacing,
            new VectorTextPaint(color, haloColor, haloWidth),
            spacing);
        return AzureStyleTextResult.Resolved;
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
        textColor.CollectZoomStops(stops);
        textHaloColor.CollectZoomStops(stops);
        textHaloWidth.CollectZoomStops(stops);
    }

    private static bool TryGetFontStack(
        AzureStyleValue value,
        out string fontStack)
    {
        if (value.Kind == AzureStyleValueKind.String &&
            !string.IsNullOrWhiteSpace(value.StringValue))
        {
            fontStack = value.StringValue;
            return true;
        }
        if (value.Kind == AzureStyleValueKind.Array &&
            value.ArrayValue is { Length: > 0 } fonts &&
            fonts[0].Kind == AzureStyleValueKind.String &&
            !string.IsNullOrWhiteSpace(fonts[0].StringValue))
        {
            fontStack = fonts[0].StringValue!;
            return true;
        }
        fontStack = string.Empty;
        return false;
    }

    private static bool TryGetPair(
        AzureStyleValue value,
        out double x,
        out double y)
    {
        if (value.Kind == AzureStyleValueKind.Array &&
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
        AzureStyleValue value,
        out string anchor)
    {
        if (value.Kind == AzureStyleValueKind.String)
        {
            return TryGetAnchor(value.StringValue, out anchor);
        }
        if (value.Kind == AzureStyleValueKind.Array &&
            value.ArrayValue is { Length: > 0 } anchors &&
            anchors[0].Kind == AzureStyleValueKind.String)
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
        AzureStyleValue value,
        out Vector4 color)
    {
        color = default;
        if (value.Kind != AzureStyleValueKind.String ||
            value.StringValue is not string text ||
            text.Length is not (7 or 9) ||
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
}

internal readonly record struct AzureTextStyle(
    string Text,
    string FontStack,
    double Size,
    double OffsetX,
    double OffsetY,
    string Anchor,
    double RadialOffset,
    double LetterSpacing,
    VectorTextPaint Paint,
    double LineSpacing);

internal enum AzureSymbolPlacement
{
    Point,
    Line,
}

internal enum AzureStyleTextResult
{
    Resolved,
    NoText,
    EvaluationFailure,
}

internal enum AzureStyleLineResult
{
    Resolved,
    Hidden,
    EvaluationFailure,
}

internal enum AzureStyleFillResult
{
    Resolved,
    Hidden,
    EvaluationFailure,
}

internal enum AzureStyleLayerParseResult
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

internal enum AzureStyleVisibilityResult
{
    Visible,
    Hidden,
    EvaluationFailure,
}

internal enum AzureStyleFilterResult
{
    Match,
    NoMatch,
    EvaluationFailure,
}

internal enum AzureStyleIconResult
{
    Resolved,
    NoIcon,
    EvaluationFailure,
}

internal readonly record struct AzureStyleEvaluationContext(
    VectorTileFeature? Feature,
    double Zoom);

internal enum AzureStyleExpressionOperator
{
    Literal,
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
    Let,
    Var,
}

/// <summary>
/// Small fail-closed evaluator for the expression forms used by Azure point-symbol layers.
/// </summary>
internal sealed class AzureStyleExpression
{
    private const int MaximumDepth = 32;
    private const int MaximumNodes = 4096;
    private const int MaximumArguments = 1024;
    private const int MaximumStringLength = 16 * 1024;
    private readonly AzureStyleExpressionOperator _operator;
    private readonly AzureStyleValue _literal;
    private readonly string? _name;
    private readonly AzureStyleExpression[] _arguments;
    private readonly bool _containsZoom;

    private AzureStyleExpression(
        AzureStyleExpressionOperator expressionOperator,
        AzureStyleValue literal,
        string? name,
        AzureStyleExpression[] arguments)
    {
        _operator = expressionOperator;
        _literal = literal;
        _name = name;
        _arguments = arguments;
        _containsZoom =
            expressionOperator == AzureStyleExpressionOperator.Zoom ||
            arguments.Any(argument => argument._containsZoom);
    }

    internal static AzureStyleExpression Literal(AzureStyleValue value) =>
        new(AzureStyleExpressionOperator.Literal, value, null, []);

    internal static bool TryParse(
        JsonElement element,
        out AzureStyleExpression expression)
    {
        int nodeCount = 0;
        return TryParse(element, 0, ref nodeCount, out expression);
    }

    internal static bool TryParseLiteralExpression(
        JsonElement element,
        out AzureStyleExpression expression) =>
        TryParseLiteral(element, out expression);

    internal bool TryEvaluate(
        AzureStyleEvaluationContext context,
        out AzureStyleValue value) =>
        TryEvaluate(context, variables: null, out value);

    internal void CollectZoomStops(List<double> values)
    {
        if (!_containsZoom)
        {
            return;
        }
        switch (_operator)
        {
            case AzureStyleExpressionOperator.Step:
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
            case AzureStyleExpressionOperator.InterpolateLinear:
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
            case AzureStyleExpressionOperator.Equal:
                foreach (AzureStyleExpression argument in _arguments)
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
            case AzureStyleExpressionOperator.Match:
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
                foreach (AzureStyleExpression argument in _arguments)
                {
                    argument.CollectZoomStops(values);
                }
                break;
        }
    }

    private void CollectLiteralNumbers(List<double> values)
    {
        if (_operator != AzureStyleExpressionOperator.Literal)
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
            foreach (AzureStyleValue item in _literal.ArrayValue)
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
        out AzureStyleExpression expression)
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
                    !TryParseLiteralValue(rawArguments[0], out AzureStyleValue literal))
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
                expression = new AzureStyleExpression(
                    operation switch
                    {
                        "get" => AzureStyleExpressionOperator.Get,
                        "has" => AzureStyleExpressionOperator.Has,
                        _ => AzureStyleExpressionOperator.Var,
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
                expression = new AzureStyleExpression(
                    operation == "zoom"
                        ? AzureStyleExpressionOperator.Zoom
                        : AzureStyleExpressionOperator.GeometryType,
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
                if (!TryGetOperator(operation, out AzureStyleExpressionOperator expressionOperator) ||
                    !HasValidArgumentCount(expressionOperator, rawArguments.Length) ||
                    !TryParseArguments(
                        rawArguments,
                        depth,
                        ref nodeCount,
                        out AzureStyleExpression[] arguments))
                {
                    return false;
                }
                expression = new AzureStyleExpression(
                    expressionOperator,
                    default,
                    null,
                    arguments);
                return true;
        }
    }

    private static bool TryParseInterpolate(
        JsonElement[] rawArguments,
        int depth,
        ref int nodeCount,
        out AzureStyleExpression expression)
    {
        expression = null!;
        if (rawArguments.Length < 6 ||
            (rawArguments.Length & 1) != 0 ||
            rawArguments[0].ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        JsonElement[] interpolation = rawArguments[0].EnumerateArray().ToArray();
        if (interpolation.Length != 1 ||
            interpolation[0].ValueKind != JsonValueKind.String ||
            !string.Equals(interpolation[0].GetString(), "linear", StringComparison.Ordinal))
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
                out AzureStyleExpression[] arguments))
        {
            return false;
        }
        expression = new AzureStyleExpression(
            AzureStyleExpressionOperator.InterpolateLinear,
            default,
            null,
            arguments);
        return true;
    }

    private static bool TryParseMatch(
        JsonElement[] rawArguments,
        int depth,
        ref int nodeCount,
        out AzureStyleExpression expression)
    {
        expression = null!;
        if (rawArguments.Length < 4 || (rawArguments.Length & 1) != 0)
        {
            return false;
        }

        AzureStyleExpression[] arguments =
            new AzureStyleExpression[rawArguments.Length];
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
        expression = new AzureStyleExpression(
            AzureStyleExpressionOperator.Match,
            default,
            null,
            arguments);
        return true;
    }

    private static bool TryParseLet(
        JsonElement[] rawArguments,
        int depth,
        ref int nodeCount,
        out AzureStyleExpression expression)
    {
        expression = null!;
        if (rawArguments.Length < 3 || (rawArguments.Length & 1) == 0)
        {
            return false;
        }

        List<AzureStyleExpression> arguments = [];
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
            arguments.Add(Literal(AzureStyleValue.FromString(name)));
            if (!TryParse(
                    rawArguments[index + 1],
                    depth + 1,
                    ref nodeCount,
                    out AzureStyleExpression value))
            {
                return false;
            }
            arguments.Add(value);
        }
        if (!TryParse(
                rawArguments[^1],
                depth + 1,
                ref nodeCount,
                out AzureStyleExpression result))
        {
            return false;
        }
        arguments.Add(result);
        expression = new AzureStyleExpression(
            AzureStyleExpressionOperator.Let,
            default,
            null,
            arguments.ToArray());
        return true;
    }

    private static bool TryParseFormat(
        JsonElement[] rawArguments,
        int depth,
        ref int nodeCount,
        out AzureStyleExpression expression)
    {
        expression = null!;
        if (rawArguments.Length < 2 || (rawArguments.Length & 1) != 0)
        {
            return false;
        }
        AzureStyleExpression[] arguments =
            new AzureStyleExpression[rawArguments.Length / 2];
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
        expression = new AzureStyleExpression(
            AzureStyleExpressionOperator.Format,
            default,
            null,
            arguments);
        return true;
    }

    private static bool TryParseArguments(
        JsonElement[] rawArguments,
        int depth,
        ref int nodeCount,
        out AzureStyleExpression[] arguments)
    {
        arguments = new AzureStyleExpression[rawArguments.Length];
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
        out AzureStyleExpression expression)
    {
        if (TryParseLiteralValue(element, out AzureStyleValue value))
        {
            expression = Literal(value);
            return true;
        }
        expression = null!;
        return false;
    }

    private static bool TryParseLiteralValue(
        JsonElement element,
        out AzureStyleValue value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                value = AzureStyleValue.Null;
                return true;
            case JsonValueKind.True:
            case JsonValueKind.False:
                value = AzureStyleValue.FromBoolean(element.GetBoolean());
                return true;
            case JsonValueKind.Number:
                if (element.TryGetDouble(out double number) && double.IsFinite(number))
                {
                    value = AzureStyleValue.FromNumber(number);
                    return true;
                }
                break;
            case JsonValueKind.String:
                string? text = element.GetString();
                if (text is not null && text.Length <= MaximumStringLength)
                {
                    value = AzureStyleValue.FromString(text);
                    return true;
                }
                break;
            case JsonValueKind.Array:
                JsonElement[] elements = element.EnumerateArray().ToArray();
                if (elements.Length > MaximumArguments)
                {
                    break;
                }
                AzureStyleValue[] items = new AzureStyleValue[elements.Length];
                for (int index = 0; index < elements.Length; index++)
                {
                    if (!TryParseLiteralValue(elements[index], out items[index]))
                    {
                        value = default;
                        return false;
                    }
                }
                value = AzureStyleValue.FromArray(items);
                return true;
        }
        value = default;
        return false;
    }

    private static bool TryGetOperator(
        string operation,
        out AzureStyleExpressionOperator expressionOperator)
    {
        expressionOperator = operation switch
        {
            "==" => AzureStyleExpressionOperator.Equal,
            "!" => AzureStyleExpressionOperator.Not,
            "all" => AzureStyleExpressionOperator.All,
            "any" => AzureStyleExpressionOperator.Any,
            "in" => AzureStyleExpressionOperator.In,
            "case" => AzureStyleExpressionOperator.Case,
            "coalesce" => AzureStyleExpressionOperator.Coalesce,
            "concat" => AzureStyleExpressionOperator.Concat,
            "match" => AzureStyleExpressionOperator.Match,
            "step" => AzureStyleExpressionOperator.Step,
            _ => default,
        };
        return operation is "==" or "!" or "all" or "any" or "in" or
            "case" or "coalesce" or "concat" or "match" or "step";
    }

    private static bool HasValidArgumentCount(
        AzureStyleExpressionOperator expressionOperator,
        int count) =>
        expressionOperator switch
        {
            AzureStyleExpressionOperator.Equal => count == 2,
            AzureStyleExpressionOperator.Not => count == 1,
            AzureStyleExpressionOperator.All or AzureStyleExpressionOperator.Any =>
                count >= 1,
            AzureStyleExpressionOperator.In => count >= 2,
            AzureStyleExpressionOperator.Case => count >= 3 && (count & 1) == 1,
            AzureStyleExpressionOperator.Coalesce or AzureStyleExpressionOperator.Concat =>
                count >= 1,
            AzureStyleExpressionOperator.Match => count >= 4 && (count & 1) == 0,
            AzureStyleExpressionOperator.Step => count >= 4 && (count & 1) == 0,
            _ => false,
        };

    private bool TryEvaluate(
        AzureStyleEvaluationContext context,
        Dictionary<string, AzureStyleValue>? variables,
        out AzureStyleValue value)
    {
        switch (_operator)
        {
            case AzureStyleExpressionOperator.Literal:
                value = _literal;
                return true;
            case AzureStyleExpressionOperator.Get:
                if (context.Feature is not null &&
                    context.Feature.TryGetProperty(_name!, out VectorTileValue property))
                {
                    value = AzureStyleValue.FromVectorTileValue(property);
                    return true;
                }
                value = AzureStyleValue.Null;
                return true;
            case AzureStyleExpressionOperator.Has:
                value = AzureStyleValue.FromBoolean(
                    context.Feature is not null &&
                    context.Feature.TryGetProperty(_name!, out _));
                return true;
            case AzureStyleExpressionOperator.GeometryType:
                value = AzureStyleValue.FromString(
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
            case AzureStyleExpressionOperator.Zoom:
                value = AzureStyleValue.FromNumber(context.Zoom);
                return true;
            case AzureStyleExpressionOperator.Var:
                if (variables is not null &&
                    variables.TryGetValue(_name!, out value))
                {
                    return true;
                }
                value = AzureStyleValue.Null;
                return false;
            case AzureStyleExpressionOperator.Equal:
                return TryEvaluateEqual(context, variables, out value);
            case AzureStyleExpressionOperator.Not:
                return TryEvaluateNot(context, variables, out value);
            case AzureStyleExpressionOperator.All:
            case AzureStyleExpressionOperator.Any:
                return TryEvaluateLogical(context, variables, out value);
            case AzureStyleExpressionOperator.In:
                return TryEvaluateIn(context, variables, out value);
            case AzureStyleExpressionOperator.Case:
                return TryEvaluateCase(context, variables, out value);
            case AzureStyleExpressionOperator.Coalesce:
                return TryEvaluateCoalesce(context, variables, out value);
            case AzureStyleExpressionOperator.Concat:
                return TryEvaluateConcat(context, variables, out value);
            case AzureStyleExpressionOperator.Format:
                return TryEvaluateConcat(context, variables, out value);
            case AzureStyleExpressionOperator.Match:
                return TryEvaluateMatch(context, variables, out value);
            case AzureStyleExpressionOperator.Step:
                return TryEvaluateStep(context, variables, out value);
            case AzureStyleExpressionOperator.InterpolateLinear:
                return TryEvaluateInterpolate(context, variables, out value);
            case AzureStyleExpressionOperator.Let:
                return TryEvaluateLet(context, variables, out value);
            default:
                value = default;
                return false;
        }
    }

    private bool TryEvaluateEqual(
        AzureStyleEvaluationContext context,
        Dictionary<string, AzureStyleValue>? variables,
        out AzureStyleValue value)
    {
        if (!TryEvaluateArgument(0, context, variables, out AzureStyleValue left) ||
            !TryEvaluateArgument(1, context, variables, out AzureStyleValue right))
        {
            value = default;
            return false;
        }
        value = AzureStyleValue.FromBoolean(left.EqualsValue(right));
        return true;
    }

    private bool TryEvaluateNot(
        AzureStyleEvaluationContext context,
        Dictionary<string, AzureStyleValue>? variables,
        out AzureStyleValue value)
    {
        if (!TryEvaluateArgument(0, context, variables, out AzureStyleValue operand) ||
            operand.Kind != AzureStyleValueKind.Boolean)
        {
            value = default;
            return false;
        }
        value = AzureStyleValue.FromBoolean(!operand.BooleanValue);
        return true;
    }

    private bool TryEvaluateLogical(
        AzureStyleEvaluationContext context,
        Dictionary<string, AzureStyleValue>? variables,
        out AzureStyleValue value)
    {
        bool all = _operator == AzureStyleExpressionOperator.All;
        foreach (AzureStyleExpression argument in _arguments)
        {
            if (!argument.TryEvaluate(context, variables, out AzureStyleValue result) ||
                result.Kind != AzureStyleValueKind.Boolean)
            {
                value = default;
                return false;
            }
            if (all && !result.BooleanValue)
            {
                value = AzureStyleValue.FromBoolean(false);
                return true;
            }
            if (!all && result.BooleanValue)
            {
                value = AzureStyleValue.FromBoolean(true);
                return true;
            }
        }
        value = AzureStyleValue.FromBoolean(all);
        return true;
    }

    private bool TryEvaluateIn(
        AzureStyleEvaluationContext context,
        Dictionary<string, AzureStyleValue>? variables,
        out AzureStyleValue value)
    {
        if (!TryEvaluateArgument(0, context, variables, out AzureStyleValue needle))
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
                    out AzureStyleValue candidate))
            {
                value = default;
                return false;
            }
            if (candidate.Kind == AzureStyleValueKind.Array &&
                candidate.ArrayValue is not null)
            {
                found |= candidate.ArrayValue.Any(needle.EqualsValue);
            }
            else
            {
                found |= needle.EqualsValue(candidate);
            }
        }
        value = AzureStyleValue.FromBoolean(found);
        return true;
    }

    private bool TryEvaluateCase(
        AzureStyleEvaluationContext context,
        Dictionary<string, AzureStyleValue>? variables,
        out AzureStyleValue value)
    {
        for (int index = 0; index < _arguments.Length - 1; index += 2)
        {
            if (!TryEvaluateArgument(
                    index,
                    context,
                    variables,
                    out AzureStyleValue condition) ||
                condition.Kind != AzureStyleValueKind.Boolean)
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
        AzureStyleEvaluationContext context,
        Dictionary<string, AzureStyleValue>? variables,
        out AzureStyleValue value)
    {
        foreach (AzureStyleExpression argument in _arguments)
        {
            if (!argument.TryEvaluate(context, variables, out AzureStyleValue candidate))
            {
                continue;
            }
            if (candidate.Kind != AzureStyleValueKind.Null)
            {
                value = candidate;
                return true;
            }
        }
        value = AzureStyleValue.Null;
        return true;
    }

    private bool TryEvaluateConcat(
        AzureStyleEvaluationContext context,
        Dictionary<string, AzureStyleValue>? variables,
        out AzureStyleValue value)
    {
        StringBuilder builder = new();
        foreach (AzureStyleExpression argument in _arguments)
        {
            if (!argument.TryEvaluate(context, variables, out AzureStyleValue candidate))
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
        value = AzureStyleValue.FromString(builder.ToString());
        return true;
    }

    private bool TryEvaluateMatch(
        AzureStyleEvaluationContext context,
        Dictionary<string, AzureStyleValue>? variables,
        out AzureStyleValue value)
    {
        if (!TryEvaluateArgument(0, context, variables, out AzureStyleValue input))
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
                    out AzureStyleValue label))
            {
                value = default;
                return false;
            }
            bool matches = label.Kind == AzureStyleValueKind.Array &&
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
        AzureStyleEvaluationContext context,
        Dictionary<string, AzureStyleValue>? variables,
        out AzureStyleValue value)
    {
        if (!TryEvaluateArgument(
                0,
                context,
                variables,
                out AzureStyleValue inputValue) ||
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
                    out AzureStyleValue stopValue) ||
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
        AzureStyleEvaluationContext context,
        Dictionary<string, AzureStyleValue>? variables,
        out AzureStyleValue value)
    {
        if (!TryEvaluateArgument(
                0,
                context,
                variables,
                out AzureStyleValue inputValue) ||
            !inputValue.TryGetNumber(out double input))
        {
            value = default;
            return false;
        }

        if (!TryEvaluateArgument(
                1,
                context,
                variables,
                out AzureStyleValue firstStopValue) ||
            !firstStopValue.TryGetNumber(out double firstStop) ||
            !TryEvaluateArgument(2, context, variables, out AzureStyleValue firstOutput))
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
        AzureStyleValue previousOutput = firstOutput;
        for (int index = 3; index < _arguments.Length; index += 2)
        {
            if (!TryEvaluateArgument(
                    index,
                    context,
                    variables,
                    out AzureStyleValue stopValue) ||
                !stopValue.TryGetNumber(out double stop) ||
                !TryEvaluateArgument(
                    index + 1,
                    context,
                    variables,
                    out AzureStyleValue output))
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
                return AzureStyleValue.TryInterpolate(
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
        AzureStyleEvaluationContext context,
        Dictionary<string, AzureStyleValue>? variables,
        out AzureStyleValue value)
    {
        Dictionary<string, AzureStyleValue> local = variables is null
            ? new(StringComparer.Ordinal)
            : new(variables, StringComparer.Ordinal);
        for (int index = 0; index < _arguments.Length - 1; index += 2)
        {
            string name = _arguments[index]._literal.StringValue!;
            if (!_arguments[index + 1].TryEvaluate(context, local, out AzureStyleValue item))
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
        AzureStyleEvaluationContext context,
        Dictionary<string, AzureStyleValue>? variables,
        out AzureStyleValue value) =>
        _arguments[index].TryEvaluate(context, variables, out value);
}

internal enum AzureStyleValueKind
{
    Null,
    Boolean,
    Number,
    String,
    Array,
}

internal readonly record struct AzureStyleValue
{
    private AzureStyleValue(
        AzureStyleValueKind kind,
        bool booleanValue,
        double numberValue,
        string? stringValue,
        AzureStyleValue[]? arrayValue)
    {
        Kind = kind;
        BooleanValue = booleanValue;
        NumberValue = numberValue;
        StringValue = stringValue;
        ArrayValue = arrayValue;
    }

    internal static AzureStyleValue Null { get; } =
        new(AzureStyleValueKind.Null, false, 0, null, null);

    internal AzureStyleValueKind Kind { get; }

    internal bool BooleanValue { get; }

    internal double NumberValue { get; }

    internal string? StringValue { get; }

    internal AzureStyleValue[]? ArrayValue { get; }

    internal static AzureStyleValue FromBoolean(bool value) =>
        new(AzureStyleValueKind.Boolean, value, 0, null, null);

    internal static AzureStyleValue FromNumber(double value) =>
        new(AzureStyleValueKind.Number, false, value, null, null);

    internal static AzureStyleValue FromString(string value) =>
        new(AzureStyleValueKind.String, false, 0, value, null);

    internal static AzureStyleValue FromArray(AzureStyleValue[] value) =>
        new(AzureStyleValueKind.Array, false, 0, null, value);

    internal static AzureStyleValue FromVectorTileValue(VectorTileValue value)
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
        return Kind == AzureStyleValueKind.Number;
    }

    internal bool EqualsValue(AzureStyleValue other)
    {
        if (Kind == AzureStyleValueKind.Number &&
            other.Kind == AzureStyleValueKind.Number)
        {
            return NumberValue.Equals(other.NumberValue);
        }
        if (Kind != other.Kind)
        {
            return false;
        }
        return Kind switch
        {
            AzureStyleValueKind.Null => true,
            AzureStyleValueKind.Boolean => BooleanValue == other.BooleanValue,
            AzureStyleValueKind.String =>
                string.Equals(StringValue, other.StringValue, StringComparison.Ordinal),
            AzureStyleValueKind.Array =>
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
        AzureStyleValueKind.Null => string.Empty,
        AzureStyleValueKind.Boolean => BooleanValue ? "true" : "false",
        AzureStyleValueKind.Number =>
            NumberValue.ToString("G17", CultureInfo.InvariantCulture),
        AzureStyleValueKind.String => StringValue ?? string.Empty,
        _ => string.Empty,
    };

    internal static bool TryInterpolate(
        AzureStyleValue from,
        AzureStyleValue to,
        double amount,
        out AzureStyleValue value)
    {
        amount = Math.Clamp(amount, 0, 1);
        if (from.TryGetNumber(out double fromNumber) &&
            to.TryGetNumber(out double toNumber))
        {
            value = FromNumber(fromNumber + ((toNumber - fromNumber) * amount));
            return true;
        }
        if (from.Kind == AzureStyleValueKind.Array &&
            to.Kind == AzureStyleValueKind.Array &&
            from.ArrayValue is not null &&
            to.ArrayValue is not null &&
            from.ArrayValue.Length == to.ArrayValue.Length)
        {
            AzureStyleValue[] items = new AzureStyleValue[from.ArrayValue.Length];
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
}

/// <summary>
/// Owns one decoded premultiplied sprite atlas and lazily cropped, bounded icon buffers.
/// </summary>
internal sealed class AzureSpriteAtlas
{
    private const int MaximumEntries = 16_384;
    private const long MaximumCroppedBytes = 32 * 1024 * 1024;
    private const int MaximumCroppedTextures = 4096;
    private const int MaximumSpriteNameLength = 1024;
    private readonly object _sync = new();
    private readonly string _styleSlug;
    private readonly Dictionary<string, AzureSpriteEntry> _entries;
    private readonly byte[] _pixels;
    private readonly uint _width;
    private readonly uint _height;
    private readonly Dictionary<string, VectorSpriteTextureData> _textures =
        new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _textureNames = [];
    private long _croppedBytes;

    internal AzureSpriteAtlas(
        string styleSlug,
        Dictionary<string, AzureSpriteEntry> entries,
        byte[] pixels,
        uint width,
        uint height)
    {
        if (!MapRenderer.IsValidPixelBuffer(pixels, width, height))
        {
            throw new InvalidDataException(
                "The Azure sprite atlas pixel buffer does not match its dimensions.");
        }
        foreach (AzureSpriteEntry entry in entries.Values)
        {
            if (entry.X > width ||
                entry.Y > height ||
                entry.Width > width - entry.X ||
                entry.Height > height - entry.Y)
            {
                throw new InvalidDataException(
                    "The Azure sprite index contains an out-of-range rectangle.");
            }
        }
        _styleSlug = styleSlug;
        _entries = entries;
        _pixels = pixels;
        _width = width;
        _height = height;
    }

    internal int EntryCount => _entries.Count;

    internal static Dictionary<string, AzureSpriteEntry> ParseIndex(
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
                "The Azure sprite index is not a JSON object.");
        }

        Dictionary<string, AzureSpriteEntry> entries =
            new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (entries.Count >= MaximumEntries)
            {
                throw new InvalidDataException(
                    "The Azure sprite index contains too many entries.");
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
                    "The Azure sprite index contains an invalid entry.");
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
                        "The Azure sprite index contains an invalid visibility value.");
                }
                visible = visibleElement.GetBoolean();
            }
            if (!entries.TryAdd(
                    property.Name,
                    new AzureSpriteEntry(
                        x,
                        y,
                        width,
                        height,
                        pixelRatio,
                        visible)))
            {
                throw new InvalidDataException(
                    "The Azure sprite index contains a duplicate entry.");
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

    internal AzureSpriteLookupResult TryGetTexture(
        string spriteName,
        out VectorSpriteTextureData? texture,
        out AzureSpriteEntry entry)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(spriteName, out entry))
            {
                texture = null;
                return AzureSpriteLookupResult.Missing;
            }
            if (!entry.Visible)
            {
                texture = null;
                return AzureSpriteLookupResult.Hidden;
            }
            if (!_textures.TryGetValue(spriteName, out texture))
            {
                return AzureSpriteLookupResult.NotPrepared;
            }
            return AzureSpriteLookupResult.Found;
        }
    }

    internal AzureSpriteLookupResult TryGetOrCreateTexture(
        string spriteName,
        CancellationToken cancellationToken,
        out VectorSpriteTextureData? texture,
        out AzureSpriteEntry entry)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(spriteName, out entry))
            {
                texture = null;
                return AzureSpriteLookupResult.Missing;
            }
            if (!entry.Visible)
            {
                texture = null;
                return AzureSpriteLookupResult.Hidden;
            }
            if (_textures.TryGetValue(spriteName, out texture))
            {
                return AzureSpriteLookupResult.Found;
            }

            long byteCount = checked((long)entry.Width * entry.Height * 4);
            if (_textures.Count >= MaximumCroppedTextures ||
                byteCount > MaximumCroppedBytes - _croppedBytes)
            {
                throw new InvalidDataException(
                    "The Azure sprite crop cache exceeds its supported limit.");
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
                    "The Azure sprite texture identity is not unique.");
            }
            texture = new VectorSpriteTextureData(
                textureId,
                cropped,
                entry.Width,
                entry.Height);
            _textures.Add(spriteName, texture);
            _textureNames[textureId] = spriteName;
            _croppedBytes += byteCount;
            return AzureSpriteLookupResult.Found;
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

internal readonly record struct AzureSpriteEntry(
    uint X,
    uint Y,
    uint Width,
    uint Height,
    double PixelRatio,
    bool Visible);

internal enum AzureSpriteLookupResult
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
