using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class AzureVectorStyleTests
{
    [TestMethod]
    public async Task RealisticExpressionsResolveExactCroppedSpriteAndPlacement()
    {
        AzureVectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [{
                "type": "symbol",
                "source-layer": "poi",
                "filter": ["all",
                  ["==", ["get", "bkt"], 631],
                  ["case",
                    ["has", "gt"],
                    ["==", ["get", "gt"], "pt"],
                    ["in", ["geometry-type"], ["literal", ["Point", "MultiPoint"]]]
                  ]
                ],
                "layout": {
                  "icon-image": ["concat", "bkt-", ["get", "bkt"]],
                  "icon-size": ["let", "st_tag", ["get", "st-tag"],
                    ["interpolate", ["linear"], ["zoom"],
                      10, ["match", ["var", "st_tag"], ["hub", "station"], 2, 1],
                      11, ["match", ["var", "st_tag"], ["hub", "station"], 4, 2]
                    ]
                  ],
                  "icon-offset": [2, -3],
                  "icon-anchor": "bottom-left"
                }
              }]
            }
            """,
            """
            {
              "bkt-631": {
                "x": 1, "y": 0, "width": 2, "height": 2,
                "pixelRatio": 2, "visible": true
              },
              "unused": {
                "x": 3, "y": 0, "width": 1, "height": 1,
                "pixelRatio": 1, "visible": true
              }
            }
            """,
            CreateAtlasPixels(),
            4,
            2);
        VectorTileFeatureCollection features = CreateFeatures(
            new VectorTileProperty("bkt", VectorTileValue.FromUInt(631)),
            new VectorTileProperty("st-tag", VectorTileValue.FromString("hub")));

        VectorSpriteTextureData texture = Assert.ContainsSingle(
            await assets.PrepareTexturesAsync(
                features,
                10,
                CancellationToken.None));
        VectorTileSymbol symbol = Assert.ContainsSingle(
            assets.ResolveSymbols(features, 10.5).Symbols);

        Assert.AreEqual(
            AzureSpriteAtlas.CreateTextureId("road", "bkt-631"),
            texture.TextureId);
        Assert.AreEqual(2u, texture.Width);
        Assert.AreEqual(2u, texture.Height);
        Assert.AreSequenceEqual(
            PixelBytes(1, 2, 5, 6),
            texture.Pixels);
        Assert.AreEqual(0, symbol.StyleLayerOrder);
        Assert.AreEqual(0.25, symbol.X);
        Assert.AreEqual(0.5, symbol.Y);
        Assert.AreEqual(3, symbol.Width, 0.000001);
        Assert.AreEqual(3, symbol.Height, 0.000001);
        Assert.AreEqual(7.5, symbol.OffsetX, 0.000001);
        Assert.AreEqual(-10.5, symbol.OffsetY, 0.000001);
    }

    [TestMethod]
    public async Task StepAndCoalesceSuppressThenResolveSprite()
    {
        AzureVectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [{
                "type": "symbol",
                "source-layer": "poi",
                "layout": {
                  "icon-image": ["step", ["zoom"], "", 10,
                    ["coalesce", ["get", "icon"], "fallback"]]
                }
              }]
            }
            """,
            """
            {
              "fallback": {
                "x": 0, "y": 0, "width": 1, "height": 1,
                "pixelRatio": 1, "visible": true
              }
            }
            """,
            PixelBytes(9),
            1,
            1);
        VectorTileFeatureCollection features = CreateFeatures();

        Assert.IsEmpty(assets.ResolveSymbols(features, 9.5).Symbols);
        Assert.ContainsSingle(
            await assets.PrepareTexturesAsync(
                features,
                10,
                CancellationToken.None));
        Assert.ContainsSingle(assets.ResolveSymbols(features, 10).Symbols);
    }

    [TestMethod]
    public async Task PreparationIncludesBothSidesOfFractionalZoomStops()
    {
        AzureVectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [{
                "type": "symbol",
                "source-layer": "poi",
                "layout": {
                  "icon-image": ["step", ["zoom"], "low", 10.5, "high"]
                }
              }]
            }
            """,
            """
            {
              "low": {
                "x": 0, "y": 0, "width": 1, "height": 1,
                "pixelRatio": 1, "visible": true
              },
              "high": {
                "x": 1, "y": 0, "width": 1, "height": 1,
                "pixelRatio": 1, "visible": true
              }
            }
            """,
            PixelBytes(1, 2),
            2,
            1);

        VectorSpriteTextureData[] textures =
            await assets.PrepareTexturesAsync(
                CreateFeatures(),
                10,
                CancellationToken.None);

        Assert.HasCount(2, textures);
        Assert.AreNotEqual(textures[0].TextureId, textures[1].TextureId);
    }

    [TestMethod]
    public void UnsupportedPointStyleFormsAreTypedAndLinePlacementIsParsed()
    {
        AzureSymbolStyle style = AzureSymbolStyle.Parse(Encoding.UTF8.GetBytes(
            """
            {
              "version": 8,
              "sources": {
                "base": {
                  "type": "vector",
                  "url": "https://example.test/tiles?tilesetId=microsoft.base"
                },
                "other": {
                  "type": "vector",
                  "url": "https://example.test/tiles?tilesetId=microsoft.traffic.relative"
                }
              },
              "layers": [
                {
                  "type": "symbol", "source": "other", "source-layer": "poi",
                  "layout": { "icon-image": "other-source" }
                },
                {
                  "type": "symbol", "source": "base", "source-layer": "road",
                  "layout": {
                    "symbol-placement": "line",
                    "icon-image": "line"
                  }
                },
                {
                  "type": "symbol", "source": "base", "source-layer": "poi",
                  "layout": {
                    "icon-image": "fit",
                    "icon-text-fit": "both"
                  }
                },
                {
                  "type": "symbol", "source": "base", "source-layer": "poi",
                  "layout": {
                    "icon-image": "rotate",
                    "icon-rotate": 15
                  }
                },
                {
                  "type": "symbol", "source": "base", "source-layer": "poi",
                  "layout": {
                    "icon-image": ["to-string", ["get", "bkt"]]
                  }
                },
                {
                  "type": "symbol", "source": "base",
                  "layout": { "icon-image": "no-source" }
                },
                {
                  "type": "symbol", "source": "base", "source-layer": "poi",
                  "layout": { "icon-image": "supported" }
                }
              ]
            }
            """));

        Assert.AreEqual(2, style.LayerCount);
        Assert.AreEqual(5, style.UnsupportedLayerCount);
        Assert.AreEqual(
            1,
            style.GetUnsupportedLayerCount(
                AzureStyleLayerParseResult.UnsupportedVectorSource));
        Assert.AreEqual(
            0,
            style.GetUnsupportedLayerCount(
                AzureStyleLayerParseResult.UnsupportedSymbolPlacement));
        Assert.AreEqual(
            1,
            style.GetUnsupportedLayerCount(
                AzureStyleLayerParseResult.UnsupportedTextFit));
        Assert.AreEqual(
            1,
            style.GetUnsupportedLayerCount(
                AzureStyleLayerParseResult.UnsupportedIconRotation));
        Assert.AreEqual(
            1,
            style.GetUnsupportedLayerCount(
                AzureStyleLayerParseResult.UnsupportedExpression));
        Assert.AreEqual(
            1,
            style.GetUnsupportedLayerCount(
                AzureStyleLayerParseResult.UnsupportedSourceLayer));
    }

    [TestMethod]
    public void EvaluationFailuresAreExplicitlyTyped()
    {
        AzureSymbolStyle style = AzureSymbolStyle.Parse(Encoding.UTF8.GetBytes(
            """
            {
              "version": 8,
              "layers": [
                {
                  "type": "symbol", "source-layer": "poi",
                  "filter": "not-a-boolean",
                  "layout": { "icon-image": "icon" }
                },
                {
                  "type": "symbol", "source-layer": "poi",
                  "layout": {
                    "icon-image": "icon",
                    "icon-size": "not-a-number"
                  }
                }
              ]
            }
            """));
        VectorTileFeature feature = CreateFeatures().Features[0];
        AzureStyleEvaluationContext context = new(feature, 10);

        Assert.AreEqual(
            AzureStyleFilterResult.EvaluationFailure,
            style.Layers[0].EvaluateFilter(context));
        Assert.AreEqual(
            AzureStyleIconResult.EvaluationFailure,
            style.Layers[1].EvaluateIcon(
                context,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _));
    }

    [TestMethod]
    public async Task LinePlacementResolvesIconsAndTextAgainstLineGeometry()
    {
        AzureVectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [
                {
                  "type": "symbol",
                  "source-layer": "road",
                  "layout": {
                    "symbol-placement": "line",
                    "symbol-spacing": 120,
                    "icon-image": "shield"
                  }
                },
                {
                  "type": "symbol",
                  "source-layer": "road",
                  "layout": {
                    "symbol-placement": "line",
                    "symbol-spacing": 180,
                    "text-field": ["get", "name"],
                    "text-font": ["Roboto-Regular"],
                    "text-size": 24
                  }
                }
              ]
            }
            """,
            """
            {
              "shield": {
                "x": 0, "y": 0, "width": 1, "height": 1,
                "pixelRatio": 1, "visible": true
              }
            }
            """,
            PixelBytes(9),
            1,
            1);
        assets.GlyphAtlas.AddRangeForTest(new AzureGlyphRange(
            "Roboto-Regular",
            0,
            new Dictionary<int, AzureGlyph>
            {
                ['R'] = new('R', GlyphBitmap(8, 128), 2, 2, 0, 2, 3),
            }));
        VectorTileFeatureCollection features = CreateLineFeatures(
            new VectorTileProperty(
                "name",
                VectorTileValue.FromString("R")));

        VectorSpriteTextureData[] textures =
            await assets.PrepareTexturesAsync(
                features,
                10,
                CancellationToken.None);
        VectorSymbolResolution resolution = assets.ResolveSymbols(features, 10);
        VectorTileSymbol icon = Assert.ContainsSingle(
            resolution.Symbols.Where(
                symbol => symbol.Kind == VectorSymbolKind.Icon));
        VectorTileSymbol glyph = Assert.ContainsSingle(
            resolution.Symbols.Where(
                symbol => symbol.Kind == VectorSymbolKind.Text));

        Assert.HasCount(2, textures);
        Assert.IsNotNull(icon.LinePoints);
        Assert.IsNotNull(glyph.LinePoints);
        Assert.AreSame(icon.LinePoints, glyph.LinePoints);
        Assert.AreEqual(120, icon.LineSpacing, 0.000001);
        Assert.AreEqual(180, glyph.LineSpacing, 0.000001);
        Assert.AreEqual(1, resolution.ResolvedGlyphCount);
    }

    [TestMethod]
    public void LineLayersResolvePaintWidthCapsAndJoins()
    {
        AzureVectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [{
                "type": "line",
                "source-layer": "road",
                "filter": ["==", ["get", "class"], "primary"],
                "layout": {
                  "line-cap": "round",
                  "line-join": "bevel"
                },
                "paint": {
                  "line-color": "#33669980",
                  "line-opacity": 0.5,
                  "line-width": ["interpolate", ["linear"], ["zoom"],
                    10, 2,
                    12, 6
                  ]
                }
              }]
            }
            """,
            "{}",
            PixelBytes(0),
            1,
            1);
        VectorTileFeatureCollection features = new(
        [
            new VectorTileFeature(
                "road",
                VectorTileGeometryType.LineString,
                [],
                [new VectorTileProperty(
                    "class",
                    VectorTileValue.FromString("primary"))],
                [new VectorTileLine(
                    [new VectorTilePoint(0.1, 0.2),
                     new VectorTilePoint(0.8, 0.9)])],
                []),
        ]);

        VectorTileStyledLine line = Assert.ContainsSingle(
            assets.ResolveLines(features, 11).Lines);

        Assert.AreEqual(0, line.StyleLayerOrder);
        Assert.AreEqual(4, line.Style.Width, 0.000001);
        Assert.AreEqual(VectorLineCap.Round, line.Style.Cap);
        Assert.AreEqual(VectorLineJoin.Bevel, line.Style.Join);
        Assert.AreEqual(0.050196, line.Style.Color.X, 0.00001);
        Assert.AreEqual(0.100392, line.Style.Color.Y, 0.00001);
        Assert.AreEqual(0.150588, line.Style.Color.Z, 0.00001);
        Assert.AreEqual(0.25098, line.Style.Color.W, 0.00001);
    }

    [TestMethod]
    public void PatternedAndDashedLineLayersAreExplicitlySkipped()
    {
        AzureSymbolStyle style = AzureSymbolStyle.Parse(Encoding.UTF8.GetBytes(
            """
            {
              "version": 8,
              "layers": [
                {
                  "type": "line",
                  "source-layer": "road",
                  "paint": { "line-pattern": "road-pattern" }
                },
                {
                  "type": "line",
                  "source-layer": "boundary",
                  "paint": { "line-dasharray": [2, 1] }
                },
                {
                  "type": "line",
                  "source-layer": "road",
                  "paint": { "line-color": "#ffffff" }
                }
              ]
            }
            """));

        Assert.AreEqual(1, style.LayerCount);
        Assert.AreEqual(
            1,
            style.GetUnsupportedLayerCount(
                AzureStyleLayerParseResult.UnsupportedLinePattern));
        Assert.AreEqual(
            1,
            style.GetUnsupportedLayerCount(
                AzureStyleLayerParseResult.UnsupportedLineDashArray));
    }

    [TestMethod]
    public void FillLayersResolveColorOpacityFiltersAndZoom()
    {
        AzureVectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [{
                "type": "fill",
                "source-layer": "land",
                "minzoom": 5,
                "maxzoom": 15,
                "filter": ["==", ["get", "class"], "park"],
                "paint": {
                  "fill-color": "#33669980",
                  "fill-opacity": 0.5
                }
              }]
            }
            """,
            "{}",
            PixelBytes(0),
            1,
            1);
        VectorTileFeatureCollection features = CreatePolygonFeatures("park");

        Assert.IsEmpty(assets.ResolvePolygons(features, 4).Polygons);
        VectorTileStyledPolygon polygon = Assert.ContainsSingle(
            assets.ResolvePolygons(features, 10).Polygons);
        Assert.IsEmpty(assets.ResolvePolygons(
            CreatePolygonFeatures("water"),
            10).Polygons);

        Assert.AreEqual(0, polygon.StyleLayerOrder);
        Assert.HasCount(3, polygon.FillTriangles);
        Assert.AreEqual(0.050196, polygon.Style.Color.X, 0.00001);
        Assert.AreEqual(0.100392, polygon.Style.Color.Y, 0.00001);
        Assert.AreEqual(0.150588, polygon.Style.Color.Z, 0.00001);
        Assert.AreEqual(0.25098, polygon.Style.Color.W, 0.00001);
    }

    [TestMethod]
    public void PatternedFillLayersAreExplicitlySkipped()
    {
        AzureSymbolStyle style = AzureSymbolStyle.Parse(Encoding.UTF8.GetBytes(
            """
            {
              "version": 8,
              "layers": [
                {
                  "type": "fill",
                  "source-layer": "land",
                  "paint": { "fill-pattern": "land-pattern" }
                },
                {
                  "type": "fill",
                  "source-layer": "land",
                  "paint": { "fill-color": "#ffffff" }
                }
              ]
            }
            """));

        Assert.AreEqual(1, style.LayerCount);
        Assert.AreEqual(
            1,
            style.GetUnsupportedLayerCount(
                AzureStyleLayerParseResult.UnsupportedFillPattern));
    }

    [TestMethod]
    public void ResolutionCountsEvaluationAndUnavailableSpriteFailures()
    {
        AzureVectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [
                {
                  "type": "symbol", "source-layer": "poi",
                  "filter": "not-a-boolean",
                  "layout": { "icon-image": "ignored" }
                },
                {
                  "type": "symbol", "source-layer": "poi",
                  "layout": { "icon-image": "missing" }
                }
              ]
            }
            """,
            """
            {
              "unused": {
                "x": 0, "y": 0, "width": 1, "height": 1,
                "pixelRatio": 1, "visible": true
              }
            }
            """,
            PixelBytes(1),
            1,
            1);

        VectorSymbolResolution resolution = assets.ResolveSymbols(
            CreateFeatures(),
            10);

        Assert.IsEmpty(resolution.Symbols);
        Assert.AreEqual(1, resolution.EvaluationFailureCount);
        Assert.AreEqual(1, resolution.UnavailableSpriteCount);
    }

    [TestMethod]
    public async Task BlankAccessibleNeverPreparesOrResolvesVisibleSprites()
    {
        AzureVectorStyleAssets assets = AzureVectorStyleAssets.CreateForTest(
            MapStyle.BlankAccessible,
            Encoding.UTF8.GetBytes(
                """
                {
                  "version": 8,
                  "layers": [{
                    "type": "symbol",
                    "source-layer": "poi",
                    "layout": { "icon-image": "icon" }
                  }]
                }
                """),
            Encoding.UTF8.GetBytes(
                """
                {
                  "icon": {
                    "x": 0, "y": 0, "width": 1, "height": 1,
                    "pixelRatio": 1, "visible": true
                  }
                }
                """),
            PixelBytes(1),
            1,
            1);
        VectorTileFeatureCollection features = CreateFeatures();

        Assert.IsEmpty(
            await assets.PrepareTexturesAsync(
                features,
                10,
                CancellationToken.None));
        Assert.IsEmpty(assets.ResolveSymbols(features, 10).Symbols);
    }

    [TestMethod]
    public void SpriteOwnershipReleasesOnlyAfterLastSource()
    {
        VectorSpriteOwnershipTracker tracker = new();

        Assert.IsTrue(tracker.Add(10, -1));
        Assert.IsFalse(tracker.Add(10, -1));
        Assert.IsFalse(tracker.Add(20, -1));
        Assert.IsEmpty(tracker.RemoveSource(10));
        Assert.AreSequenceEqual([-1L], tracker.RemoveSource(20));
    }

    [TestMethod]
    public void SpriteIndexRejectsMalformedEntriesInsteadOfSilentlyDroppingThem()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            AzureSpriteAtlas.ParseIndex(Encoding.UTF8.GetBytes(
                """
                {
                  "invalid": {
                    "x": 0, "y": 0, "width": 0, "height": 1,
                    "pixelRatio": 1
                  }
                }
                """)));
    }

    [TestMethod]
    public async Task PointTextResolvesSharedGlyphTexturesAndStylePlacement()
    {
        AzureVectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [{
                "type": "symbol",
                "source-layer": "poi",
                "layout": {
                  "text-field": ["format",
                    ["get", "name"], {},
                    "\n", {},
                    ["get", "suffix"], {"font-scale": 0.8}],
                  "text-font": ["Roboto-Regular"],
                  "text-size": 24,
                  "text-offset": [1, 2],
                  "text-variable-anchor": ["top", "bottom"]
                },
                "paint": {
                  "text-color": "#204080",
                  "text-halo-color": "#FFFFFF80",
                  "text-halo-width": 2
                }
              }]
            }
            """,
            "{}",
            PixelBytes(0),
            1,
            1);
        assets.GlyphAtlas.AddRangeForTest(new AzureGlyphRange(
            "Roboto-Regular",
            0,
            new Dictionary<int, AzureGlyph>
            {
                ['A'] = new('A', GlyphBitmap(8, 32), 2, 2, 0, 2, 3),
                ['1'] = new('1', GlyphBitmap(8, 224), 2, 2, 0, 2, 3),
            }));
        VectorTileFeatureCollection features = CreateFeatures(
            new VectorTileProperty("name", VectorTileValue.FromString("A")),
            new VectorTileProperty("suffix", VectorTileValue.FromString("1")));

        VectorSpriteTextureData[] textures =
            await assets.PrepareTexturesAsync(
                features,
                10,
                CancellationToken.None);
        VectorSymbolResolution resolution = assets.ResolveSymbols(features, 10);
        VectorTileSymbol[] glyphs =
            resolution.Symbols.Where(symbol => symbol.Kind == VectorSymbolKind.Text)
                .ToArray();

        Assert.HasCount(2, textures);
        Assert.HasCount(2, glyphs);
        Assert.AreEqual(2, resolution.ResolvedGlyphCount);
        Assert.AreEqual(0, resolution.UnavailableGlyphCount);
        Assert.AreEqual(glyphs[0].LabelId, glyphs[1].LabelId);
        Assert.IsGreaterThanOrEqualTo(0, glyphs[0].LabelId);
        Assert.AreEqual(VectorSymbolKind.Text, glyphs[0].Kind);
        Assert.AreEqual(8, glyphs[0].Width, 0.000001);
        Assert.AreEqual(8, glyphs[0].Height, 0.000001);
        Assert.AreEqual(2d / 8d, glyphs[0].Paint.HaloOffset, 0.000001);
        Assert.AreEqual(0x20 / 255f, glyphs[0].Paint.Color.X, 0.000001);
        Assert.AreEqual(0.5f, glyphs[0].Paint.HaloColor.W, 0.01);
    }

    [TestMethod]
    public async Task SupplementaryCharactersDoNotFailTheTile()
    {
        AzureVectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [{
                "type": "symbol",
                "source-layer": "poi",
                "layout": {
                  "text-field": ["get", "name"],
                  "text-font": ["Roboto-Regular"],
                  "text-size": 24
                }
              }]
            }
            """,
            "{}",
            PixelBytes(0),
            1,
            1);
        assets.GlyphAtlas.AddRangeForTest(new AzureGlyphRange(
            "Roboto-Regular",
            0,
            new Dictionary<int, AzureGlyph>
            {
                ['A'] = new('A', GlyphBitmap(8, 128), 2, 2, 0, -2, 3),
            }));
        VectorTileFeatureCollection features = CreateFeatures(
            new VectorTileProperty(
                "name",
                VectorTileValue.FromString("A\U0001F600")));

        await assets.PrepareTexturesAsync(features, 10, CancellationToken.None);
        VectorSymbolResolution resolution = assets.ResolveSymbols(features, 10);

        Assert.AreEqual(1, resolution.ResolvedGlyphCount);
        Assert.AreEqual(1, resolution.UnavailableGlyphCount);
    }

    [TestMethod]
    public async Task GlyphTopBearingsUseMapboxQuadPlacement()
    {
        AzureVectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [{
                "type": "symbol",
                "source-layer": "poi",
                "layout": {
                  "text-field": "Aa",
                  "text-font": ["Roboto-Regular"],
                  "text-size": 24
                }
              }]
            }
            """,
            "{}",
            PixelBytes(0),
            1,
            1);
        assets.GlyphAtlas.AddRangeForTest(new AzureGlyphRange(
            "Roboto-Regular",
            0,
            new Dictionary<int, AzureGlyph>
            {
                ['A'] = new(
                    'A',
                    GlyphBitmap(21, 128),
                    15,
                    15,
                    0,
                    -5,
                    15),
                ['a'] = new(
                    'a',
                    GlyphBitmap(17, 128),
                    11,
                    11,
                    2,
                    -9,
                    13),
            }));

        await assets.PrepareTexturesAsync(
            CreateFeatures(),
            10,
            CancellationToken.None);
        VectorTileSymbol[] glyphs = assets.ResolveSymbols(
                CreateFeatures(),
                10)
            .Symbols;

        Assert.HasCount(2, glyphs);
        double firstTop = glyphs[0].OffsetY - (glyphs[0].Height / 2);
        double secondTop = glyphs[1].OffsetY - (glyphs[1].Height / 2);
        Assert.AreEqual(4, secondTop - firstTop, 0.000001);
    }

    private static AzureVectorStyleAssets CreateAssets(
        string style,
        string sprites,
        byte[] pixels,
        uint width,
        uint height) =>
        AzureVectorStyleAssets.CreateForTest(
            MapStyle.Road,
            Encoding.UTF8.GetBytes(style),
            Encoding.UTF8.GetBytes(sprites),
            pixels,
            width,
            height);

    private static VectorTileFeatureCollection CreateFeatures(
        params VectorTileProperty[] properties) =>
        new(
        [
            new VectorTileFeature(
                "poi",
                VectorTileGeometryType.Point,
                [new VectorTilePoint(0.25, 0.5)],
                properties,
                [],
                []),
        ]);

    private static VectorTileFeatureCollection CreatePolygonFeatures(
        string featureClass) =>
        new(
        [
            new VectorTileFeature(
                "land",
                VectorTileGeometryType.Polygon,
                [],
                [new VectorTileProperty(
                    "class",
                    VectorTileValue.FromString(featureClass))],
                [],
                [new VectorTilePolygon(
                    [new VectorTileRing(
                        [new VectorTilePoint(0, 0),
                         new VectorTilePoint(1, 0),
                         new VectorTilePoint(0, 1)])],
                    [new VectorTilePoint(0, 0),
                     new VectorTilePoint(1, 0),
                     new VectorTilePoint(0, 1)])]),
        ]);

    private static VectorTileFeatureCollection CreateLineFeatures(
        params VectorTileProperty[] properties)
    {
        VectorTilePoint[] points =
        [
            new VectorTilePoint(0.1, 0.5),
            new VectorTilePoint(0.9, 0.5),
        ];
        return new VectorTileFeatureCollection(
        [
            new VectorTileFeature(
                "road",
                VectorTileGeometryType.LineString,
                [],
                properties,
                [new VectorTileLine(points)],
                []),
        ]);
    }

    private static byte[] CreateAtlasPixels() =>
        PixelBytes(0, 1, 2, 3, 4, 5, 6, 7);

    private static byte[] PixelBytes(params byte[] values) =>
        values.SelectMany(value => new byte[] { value, value, value, 255 })
            .ToArray();

    private static byte[] GlyphBitmap(int size, byte value) =>
        Enumerable.Repeat(value, size * size).ToArray();
}
