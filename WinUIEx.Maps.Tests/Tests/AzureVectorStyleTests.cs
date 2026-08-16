using System.Numerics;
using System.Text;
using System.Text.Json;
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
    public void AdvancedPointStylesAndLinePlacementAreParsed()
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
                    "icon-image": ["number-format", ["get", "bkt"], {}]
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

        Assert.AreEqual(4, style.LayerCount);
        Assert.AreEqual(3, style.UnsupportedLayerCount);
        Assert.AreEqual(
            1,
            style.GetUnsupportedLayerCount(
                AzureStyleLayerParseResult.UnsupportedVectorSource));
        Assert.AreEqual(
            0,
            style.GetUnsupportedLayerCount(
                AzureStyleLayerParseResult.UnsupportedSymbolPlacement));
        Assert.AreEqual(
            0,
            style.GetUnsupportedLayerCount(
                AzureStyleLayerParseResult.UnsupportedTextFit));
        Assert.AreEqual(
            0,
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
                out _,
                out _,
                out _,
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
    public async Task AdvancedSymbolStylesResolveAsOneFittedGroup()
    {
        AzureVectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [{
                "type": "symbol",
                "source-layer": "poi",
                "layout": {
                  "icon-image": "marker",
                  "icon-rotate": 30,
                  "icon-rotation-alignment": "viewport",
                  "icon-text-fit": "both",
                  "icon-text-fit-padding": [1, 2, 3, 4],
                  "icon-allow-overlap": true,
                  "icon-ignore-placement": false,
                  "icon-optional": true,
                  "text-field": ["to-string", ["get", "route"]],
                  "text-font": ["Roboto-Regular"],
                  "text-size": [
                    "*",
                    10,
                    ["number", ["get", "shield-scale"], 0.8]
                  ],
                  "text-rotation-alignment": "viewport",
                  "text-allow-overlap": false,
                  "text-ignore-placement": true,
                  "text-optional": false,
                  "symbol-sort-key": 7
                },
                "paint": {
                  "icon-color": "#804020",
                  "icon-opacity": 0.5
                }
              }]
            }
            """,
            """
            {
              "marker": {
                "x": 0, "y": 0, "width": 10, "height": 10,
                "pixelRatio": 1, "visible": true
              }
            }
            """,
            PixelBytes(Enumerable.Repeat((byte)9, 100).ToArray()),
            10,
            10);
        assets.GlyphAtlas.AddRangeForTest(new AzureGlyphRange(
            "Roboto-Regular",
            0,
            new Dictionary<int, AzureGlyph>
            {
                ['5'] = new('5', GlyphBitmap(8, 128), 2, 2, 0, 2, 3),
            }));
        VectorTileFeatureCollection features = CreateFeatures(
            new VectorTileProperty(
                "route",
                VectorTileValue.FromUInt(5)),
            new VectorTileProperty(
                "shield-scale",
                VectorTileValue.FromDouble(1.2)));

        await assets.PrepareTexturesAsync(features, 10, CancellationToken.None);
        VectorTileSymbol[] symbols = assets.ResolveSymbols(features, 10).Symbols;
        VectorTileSymbol icon = Assert.ContainsSingle(
            symbols.Where(symbol => symbol.Kind == VectorSymbolKind.Icon));
        VectorTileSymbol glyph = Assert.ContainsSingle(
            symbols.Where(symbol => symbol.Kind == VectorSymbolKind.Text));
        double glyphLeft = glyph.OffsetX - (glyph.Width / 2);
        double glyphTop = glyph.OffsetY - (glyph.Height / 2);

        Assert.AreEqual(glyph.SymbolGroupId, icon.SymbolGroupId);
        Assert.AreEqual(4, glyph.Width, 0.000001);
        Assert.IsTrue(icon.ViewportAligned);
        Assert.IsTrue(glyph.ViewportAligned);
        Assert.AreEqual(Math.PI / 6, icon.Rotation, 0.000001);
        Assert.AreEqual(
            Math.Max(10, glyph.Width + 6),
            icon.Width,
            0.000001);
        Assert.AreEqual(
            Math.Max(10, glyph.Height + 4),
            icon.Height,
            0.000001);
        Assert.AreEqual(glyphLeft + ((glyph.Width - 2) / 2), icon.OffsetX, 0.000001);
        Assert.AreEqual(glyphTop + ((glyph.Height + 2) / 2), icon.OffsetY, 0.000001);
        Assert.IsTrue(icon.IconPaint.IsTinted);
        Assert.AreEqual(0.25098, icon.IconPaint.Color.X, 0.00001);
        Assert.AreEqual(0.12549, icon.IconPaint.Color.Y, 0.00001);
        Assert.AreEqual(0.062745, icon.IconPaint.Color.Z, 0.00001);
        Assert.AreEqual(0.5, icon.IconPaint.Color.W, 0.00001);
        Assert.AreEqual(7, icon.SortKey);
        Assert.IsTrue(icon.AllowOverlap);
        Assert.IsFalse(icon.IgnorePlacement);
        Assert.IsTrue(icon.Optional);
        Assert.AreEqual(7, glyph.SortKey);
        Assert.IsFalse(glyph.AllowOverlap);
        Assert.IsTrue(glyph.IgnorePlacement);
        Assert.IsFalse(glyph.Optional);
    }

    [TestMethod]
    public async Task TextFitIconIsSuppressedWhenItsTextDoesNotResolve()
    {
        AzureVectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [{
                "type": "symbol",
                "source-layer": "poi",
                "layout": {
                  "icon-image": "marker",
                  "icon-text-fit": "both",
                  "text-field": ["get", "missing"],
                  "text-font": ["Roboto-Regular"]
                }
              }]
            }
            """,
            """
            {
              "marker": {
                "x": 0, "y": 0, "width": 10, "height": 10,
                "pixelRatio": 1, "visible": true
              }
            }
            """,
            PixelBytes(Enumerable.Repeat((byte)9, 100).ToArray()),
            10,
            10);

        await assets.PrepareTexturesAsync(
            CreateFeatures(),
            10,
            CancellationToken.None);

        Assert.IsEmpty(assets.ResolveSymbols(CreateFeatures(), 10).Symbols);
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
    public async Task PatternedAndDashedLineLayersResolve()
    {
        AzureVectorStyleAssets assets = CreateAssets(
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
                  "paint": {
                    "line-width": 4,
                    "line-dasharray": [2, 1]
                  }
                },
                {
                  "type": "line",
                  "source-layer": "road",
                  "paint": { "line-color": "#ffffff" }
                }
              ]
            }
            """,
            """
            {
              "road-pattern": {
                "x": 0, "y": 0, "width": 4, "height": 2,
                "pixelRatio": 2, "visible": true
              }
            }
            """,
            PixelBytes(1, 2, 3, 4, 5, 6, 7, 8),
            4,
            2);
        VectorTileFeatureCollection features = new(
        [
            CreateLineFeature("road"),
            CreateLineFeature("boundary"),
        ]);

        VectorSpriteTextureData texture = Assert.ContainsSingle(
            await assets.PrepareTexturesAsync(
                features,
                10,
                CancellationToken.None));
        VectorTileSymbol pattern = Assert.ContainsSingle(
            assets.ResolveSymbols(features, 10).Symbols);
        VectorLineResolution lines = assets.ResolveLines(features, 10);
        VectorTileStyledLine dashed = Assert.ContainsSingle(
            lines.Lines.Where(line => line.StyleLayerOrder == 1));

        Assert.AreEqual(
            AzureSpriteAtlas.CreateTextureId("road", "road-pattern"),
            texture.TextureId);
        Assert.AreEqual(texture.TextureId, pattern.TextureId);
        Assert.AreEqual(2, pattern.Width, 0.000001);
        Assert.AreEqual(1, pattern.Height, 0.000001);
        Assert.AreEqual(2, pattern.LineSpacing, 0.000001);
        Assert.IsTrue(pattern.ContinuousLinePlacement);
        Assert.AreSequenceEqual([8d, 4d], dashed.Style.DashArray!);
        Assert.HasCount(2, lines.Lines);
    }

    [TestMethod]
    public void AdvancedLineStylesResolve()
    {
        AzureVectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [{
                "type": "line",
                "source-layer": "road",
                "layout": {
                  "line-join": "miter",
                  "line-miter-limit": 3
                },
                "paint": {
                  "line-width": 6,
                  "line-offset": 3,
                  "line-gap-width": 4,
                  "line-blur": 2,
                  "line-gradient": [
                    "interpolate", ["linear"], ["line-progress"],
                    0, "#ff0000",
                    0.5, "#00ff00",
                    1, "#0000ff"
                  ]
                }
              }]
            }
            """,
            "{}",
            PixelBytes(0),
            1,
            1);

        VectorTileStyledLine line = Assert.ContainsSingle(
            assets.ResolveLines(CreateLineFeatures(), 10).Lines);

        Assert.AreEqual(6, line.Style.Width, 0.000001);
        Assert.AreEqual(3, line.Style.Offset, 0.000001);
        Assert.AreEqual(4, line.Style.GapWidth, 0.000001);
        Assert.AreEqual(2, line.Style.Blur, 0.000001);
        Assert.AreEqual(3, line.Style.MiterLimit, 0.000001);
        Assert.AreEqual(VectorLineJoin.Miter, line.Style.Join);
        Assert.HasCount(3, line.Style.Gradient);
        Assert.AreEqual(new Vector4(1, 0, 0, 1), line.Style.Gradient[0].Color);
        Assert.AreEqual(new Vector4(0, 0, 1, 1), line.Style.Gradient[2].Color);
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
    public async Task PatternedFillsAndExplicitOutlinesResolve()
    {
        AzureVectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [
                {
                  "type": "fill",
                  "source-layer": "land",
                  "paint": {
                    "fill-pattern": "land-pattern",
                    "fill-opacity": 0.5,
                    "fill-outline-color": "#33669980"
                  }
                }
              ]
            }
            """,
            """
            {
              "land-pattern": {
                "x": 0, "y": 0, "width": 4, "height": 2,
                "pixelRatio": 2, "visible": true
              }
            }
            """,
            PixelBytes(1, 2, 3, 4, 5, 6, 7, 8),
            4,
            2);
        VectorTileFeatureCollection features = CreatePolygonFeatures("park");

        VectorSpriteTextureData texture = Assert.ContainsSingle(
            await assets.PrepareTexturesAsync(
                features,
                10,
                CancellationToken.None));
        VectorTileStyledPolygon polygon = Assert.ContainsSingle(
            assets.ResolvePolygons(features, 10).Polygons);

        Assert.AreEqual(texture.TextureId, polygon.Style.PatternTextureId);
        Assert.AreEqual(2, polygon.Style.PatternWidth, 0.000001);
        Assert.AreEqual(1, polygon.Style.PatternHeight, 0.000001);
        Assert.AreEqual(0.5, polygon.Style.Opacity, 0.000001);
        Assert.AreEqual(Vector4.Zero, polygon.Style.Color);
        Vector4 outline = polygon.Style.OutlineColor!.Value;
        Assert.AreEqual(0.050196, outline.X, 0.00001);
        Assert.AreEqual(0.100392, outline.Y, 0.00001);
        Assert.AreEqual(0.150588, outline.Z, 0.00001);
        Assert.AreEqual(0.25098, outline.W, 0.00001);
        Assert.IsNotEmpty(polygon.Rings);
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

    [TestMethod]
    public async Task TextScaleFactorScalesGlyphGeometryAndSpacing()
    {
        AzureVectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [{
                "type": "symbol",
                "source-layer": "poi",
                "layout": {
                  "text-field": "AA",
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
            }));
        VectorTileFeatureCollection features = CreateFeatures();

        await assets.PrepareTexturesAsync(
            features,
            10,
            CancellationToken.None);
        VectorTileSymbol[] normal =
            assets.ResolveSymbols(features, 10, 1).Symbols;
        VectorTileSymbol[] scaled =
            assets.ResolveSymbols(features, 10, 2).Symbols;

        Assert.HasCount(2, normal);
        Assert.HasCount(2, scaled);
        for (int index = 0; index < normal.Length; index++)
        {
            Assert.AreEqual(normal[index].Width * 2, scaled[index].Width, 0.000001);
            Assert.AreEqual(normal[index].Height * 2, scaled[index].Height, 0.000001);
        }
        Assert.AreEqual(
            (normal[1].OffsetX - normal[0].OffsetX) * 2,
            scaled[1].OffsetX - scaled[0].OffsetX,
            0.000001);
    }

    [TestMethod]
    public void InvalidStyleDocumentsFailClosed()
    {
        string[] invalidStyles =
        [
            "{}",
            """{"version":7,"layers":[]}""",
            """{"version":8,"layers":{}}""",
            """{"version":8,"layers":[null]}""",
            """{"version":8,"layers":[{}]}""",
            """{"version":8,"layers":[{"type":5}]}""",
            """
            {
              "version": 8,
              "layers": [{
                "type": "fill",
                "source-layer": "",
                "paint": {}
              }]
            }
            """,
            """
            {
              "version": 8,
              "layers": [{
                "type": "line",
                "source-layer": "",
                "paint": {}
              }]
            }
            """,
            """
            {
              "version": 8,
              "layers": [{
                "type": "symbol",
                "source-layer": "",
                "layout": { "icon-image": "marker" }
              }]
            }
            """,
            """
            {
              "version": 8,
              "layers": [{
                "type": "symbol",
                "source-layer": "",
                "layout": { "text-field": "label" }
              }]
            }
            """,
            """
            {
              "version": 8,
              "layers": [{
                "type": "fill",
                "source-layer": "land",
                "paint": false
              }]
            }
            """,
            """
            {
              "version": 8,
              "layers": [{
                "type": "line",
                "source-layer": "road",
                "paint": false
              }]
            }
            """,
        ];

        foreach (string style in invalidStyles)
        {
            Assert.ThrowsExactly<InvalidDataException>(() =>
                AzureSymbolStyle.Parse(Encoding.UTF8.GetBytes(style)));
        }
    }

    [TestMethod]
    public async Task EveryIconAnchorResolvesExpectedSpriteOffset()
    {
        Dictionary<string, (double X, double Y)> anchors = new()
        {
            ["center"] = (0, 0),
            ["left"] = (5, 0),
            ["right"] = (-5, 0),
            ["top"] = (0, 10),
            ["bottom"] = (0, -10),
            ["top-left"] = (5, 10),
            ["top-right"] = (-5, 10),
            ["bottom-left"] = (5, -10),
            ["bottom-right"] = (-5, -10),
        };

        foreach ((string anchor, (double expectedX, double expectedY)) in anchors)
        {
            AzureVectorStyleAssets assets = CreateAssets(
                $$"""
                {
                  "version": 8,
                  "layers": [{
                    "type": "symbol",
                    "source-layer": "poi",
                    "layout": {
                      "icon-image": "marker",
                      "icon-anchor": "{{anchor}}"
                    }
                  }]
                }
                """,
                """
                {
                  "marker": {
                    "x": 0, "y": 0, "width": 10, "height": 20,
                    "pixelRatio": 1, "visible": true
                  }
                }
                """,
                PixelBytes(Enumerable.Repeat((byte)7, 200).ToArray()),
                10,
                20);

            await assets.PrepareTexturesAsync(
                CreateFeatures(),
                10,
                CancellationToken.None);
            VectorTileSymbol symbol = Assert.ContainsSingle(
                assets.ResolveSymbols(CreateFeatures(), 10).Symbols);

            Assert.AreEqual(expectedX, symbol.OffsetX, 0.000001, anchor);
            Assert.AreEqual(expectedY, symbol.OffsetY, 0.000001, anchor);
        }
    }

    [TestMethod]
    public async Task EveryTextAnchorAppliesRadialOffsetFromCenter()
    {
        string[] anchors =
        [
            "center",
            "left",
            "right",
            "top",
            "bottom",
            "top-left",
            "top-right",
            "bottom-left",
            "bottom-right",
        ];
        string layers = string.Join(
            ",",
            anchors.Select(anchor =>
                $$"""
                {
                  "type": "symbol",
                  "source-layer": "poi",
                  "layout": {
                    "text-field": "A",
                    "text-font": ["Roboto-Regular"],
                    "text-size": 24,
                    "text-anchor": "{{anchor}}",
                    "text-radial-offset": 1
                  }
                }
                """));
        AzureVectorStyleAssets assets = CreateAssets(
            $$"""{"version":8,"layers":[{{layers}}]}""",
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
                    GlyphBitmap(8, 128),
                    2,
                    2,
                    0,
                    2,
                    24),
            }));

        await assets.PrepareTexturesAsync(
            CreateFeatures(),
            10,
            CancellationToken.None);
        VectorTileSymbol[] symbols = assets.ResolveSymbols(
                CreateFeatures(),
                10)
            .Symbols;
        VectorTileSymbol center = symbols.Single(symbol =>
            symbol.StyleLayerOrder == 0);
        Dictionary<string, (double X, double Y)> expectedDeltas = new()
        {
            ["center"] = (0, 0),
            ["left"] = (36, 0),
            ["right"] = (-36, 0),
            ["top"] = (0, 38.4),
            ["bottom"] = (0, -38.4),
            ["top-left"] = (36, 38.4),
            ["top-right"] = (-36, 38.4),
            ["bottom-left"] = (36, -38.4),
            ["bottom-right"] = (-36, -38.4),
        };

        Assert.HasCount(anchors.Length, symbols);
        for (int index = 0; index < anchors.Length; index++)
        {
            VectorTileSymbol symbol = symbols.Single(candidate =>
                candidate.StyleLayerOrder == index);
            (double expectedX, double expectedY) =
                expectedDeltas[anchors[index]];
            Assert.AreEqual(
                expectedX,
                symbol.OffsetX - center.OffsetX,
                0.000001,
                anchors[index]);
            Assert.AreEqual(
                expectedY,
                symbol.OffsetY - center.OffsetY,
                0.000001,
                anchors[index]);
        }
    }

    [TestMethod]
    public void LineAndPolygonFailuresAreCountedWithoutPartialOutput()
    {
        AzureVectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [
                {
                  "type": "line", "source-layer": "road",
                  "layout": { "visibility": true }
                },
                {
                  "type": "line", "source-layer": "road",
                  "filter": "not-a-boolean"
                },
                {
                  "type": "line", "source-layer": "road",
                  "paint": { "line-pattern": 5 }
                },
                {
                  "type": "line", "source-layer": "road",
                  "paint": { "line-width": "wide" }
                },
                {
                  "type": "line", "source-layer": "road",
                  "paint": { "line-width": 0 }
                },
                {
                  "type": "line", "source-layer": "road",
                  "paint": { "line-pattern": "missing" }
                },
                {
                  "type": "fill", "source-layer": "land",
                  "layout": { "visibility": true }
                },
                {
                  "type": "fill", "source-layer": "land",
                  "filter": "not-a-boolean"
                },
                {
                  "type": "fill", "source-layer": "land",
                  "paint": { "fill-color": "red" }
                },
                {
                  "type": "fill", "source-layer": "land",
                  "paint": { "fill-opacity": "opaque" }
                },
                {
                  "type": "fill", "source-layer": "land",
                  "paint": { "fill-opacity": 0 }
                },
                {
                  "type": "fill", "source-layer": "land",
                  "paint": { "fill-pattern": "missing" }
                }
              ]
            }
            """,
            "{}",
            PixelBytes(0),
            1,
            1);
        VectorLineResolution lines = assets.ResolveLines(
            CreateLineFeatures(),
            10);
        VectorPolygonResolution polygons = assets.ResolvePolygons(
            CreatePolygonFeatures("park"),
            10);
        VectorSymbolResolution patterns = assets.ResolveSymbols(
            CreateLineFeatures(),
            10);

        Assert.IsEmpty(lines.Lines);
        Assert.AreEqual(4, lines.EvaluationFailureCount);
        Assert.IsEmpty(polygons.Polygons);
        Assert.AreEqual(5, polygons.EvaluationFailureCount);
        Assert.IsEmpty(patterns.Symbols);
        Assert.AreEqual(3, patterns.EvaluationFailureCount);
        Assert.AreEqual(1, patterns.UnavailableSpriteCount);
    }

    [TestMethod]
    public void SupportedExpressionsEvaluateValuesAndRejectTypeMismatches()
    {
        VectorTileFeature feature = CreateFeatures(
            new VectorTileProperty("number", VectorTileValue.FromDouble(2)),
            new VectorTileProperty("name", VectorTileValue.FromString("road")),
            new VectorTileProperty("enabled", VectorTileValue.FromBool(true)))
            .Features[0];
        AzureStyleEvaluationContext context = new(feature, 10.5);

        AssertExpressionNumber("""["get","number"]""", context, 2);
        AssertExpressionKind("""["get","missing"]""", context, AzureStyleValueKind.Null);
        AssertExpressionBoolean("""["has","enabled"]""", context, true);
        AssertExpressionBoolean("""["has","missing"]""", context, false);
        AssertExpressionString("""["geometry-type"]""", context, "Point");
        AssertExpressionNumber("""["zoom"]""", context, 10.5);
        AssertExpressionBoolean("""["==",["get","number"],2]""", context, true);
        AssertExpressionBoolean("""["!",false]""", context, true);
        AssertExpressionBoolean("""["all",true,true,false]""", context, false);
        AssertExpressionBoolean("""["any",false,false,true]""", context, true);
        AssertExpressionBoolean(
            """["in","road",["literal",["water","road"]]]""",
            context,
            true);
        AssertExpressionString(
            """["case",false,"first",true,"second","fallback"]""",
            context,
            "second");
        AssertExpressionString(
            """["coalesce",["get","missing"],"fallback"]""",
            context,
            "fallback");
        AssertExpressionString(
            """["concat","route-",["to-string",["get","number"]]]""",
            context,
            "route-2");
        AssertExpressionString(
            """["match",["get","name"],["road","street"],"yes","no"]""",
            context,
            "yes");
        AssertExpressionNumber(
            """["step",["zoom"],1,10,2,12,3]""",
            context,
            2);
        AssertExpressionNumber(
            """["interpolate",["linear"],["zoom"],10,0,12,20]""",
            context,
            5);
        AzureStyleValue array = EvaluateExpression(
            """["interpolate",["linear"],["zoom"],10,["literal",[0,10]],12,["literal",[20,30]]]""",
            context);
        Assert.AreEqual(AzureStyleValueKind.Array, array.Kind);
        Assert.AreEqual(5, array.ArrayValue![0].NumberValue, 0.000001);
        Assert.AreEqual(15, array.ArrayValue[1].NumberValue, 0.000001);
        AssertExpressionNumber(
            """["let","scale",3,"offset",4,["*",["var","scale"],["var","offset"]]]""",
            context,
            12);
        AssertExpressionString("""["to-string",true]""", context, "true");
        AssertExpressionString("""["to-string",null]""", context, string.Empty);
        AssertExpressionNumber("""["*",2,3,4]""", context, 24);
        AssertExpressionNumber("""["number","not-number",7]""", context, 7);

        AssertExpressionFails("""["var","missing"]""", context);
        AssertExpressionFails("""["!",1]""", context);
        AssertExpressionFails("""["all",true,1]""", context);
        AssertExpressionFails("""["to-string",["literal",[1]]]""", context);
        AssertExpressionFails("""["*","x",2]""", context);
        AssertExpressionFails("""["number","x",false]""", context);
        AssertExpressionFails(
            """["interpolate",["linear"],["zoom"],10,"low",12,"high"]""",
            context);
    }

    [TestMethod]
    public void AccessibilityFeaturesReuseLocalizedTextWithoutGlyphResolution()
    {
        AzureVectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [{
                "type": "symbol",
                "source-layer": "poi",
                "filter": ["==", ["get", "class"], "museum"],
                "layout": {
                  "text-field": ["coalesce", ["get", "name_de"], ["get", "name"]],
                  "text-size": 18,
                  "text-transform": "uppercase"
                }
              }]
            }
            """,
            "{}",
            PixelBytes(0),
            1,
            1);
        VectorTileFeatureCollection features = CreateFeatures(
            new VectorTileProperty(
                "class",
                VectorTileValue.FromString("museum")),
            new VectorTileProperty(
                "name",
                VectorTileValue.FromString("Museum")),
            new VectorTileProperty(
                "name_de",
                VectorTileValue.FromString("Kunsthalle")));

        VectorTileAccessibilityFeature feature = Assert.ContainsSingle(
            assets.ResolveAccessibilityFeatures(features, 12));

        Assert.AreEqual("KUNSTHALLE", feature.Name);
        Assert.AreEqual(MapAccessibilityFeatureKind.Landmark, feature.Kind);
        Assert.AreEqual(0.25, feature.X);
        Assert.AreEqual(0.5, feature.Y);
        Assert.AreEqual(18, feature.Prominence);
    }

    [TestMethod]
    public async Task BlankAccessibleResolvesSemanticsWithoutPreparingTextures()
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
                    "layout": {
                      "icon-image": "museum",
                      "text-field": ["get", "name"]
                    }
                  }]
                }
                """),
            Encoding.UTF8.GetBytes(
                """
                {
                  "museum": {
                    "x": 0, "y": 0, "width": 1, "height": 1,
                    "pixelRatio": 1, "visible": true
                  }
                }
                """),
            PixelBytes(1),
            1,
            1);
        VectorTileFeatureCollection features = CreateFeatures(
            new VectorTileProperty(
                "name",
                VectorTileValue.FromString("Museum")));

        Assert.IsEmpty(await assets.PrepareTexturesAsync(
            features,
            12,
            CancellationToken.None));
        Assert.IsEmpty(assets.ResolveSymbols(features, 12).Symbols);
        Assert.AreEqual(
            "Museum",
            Assert.ContainsSingle(
                assets.ResolveAccessibilityFeatures(features, 12)).Name);
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

    private static VectorTileFeature CreateLineFeature(string sourceLayer) =>
        new(
            sourceLayer,
            VectorTileGeometryType.LineString,
            [],
            [],
            [new VectorTileLine(
                [new VectorTilePoint(0.1, 0.5),
                 new VectorTilePoint(0.9, 0.5)])],
            []);

    private static byte[] CreateAtlasPixels() =>
        PixelBytes(0, 1, 2, 3, 4, 5, 6, 7);

    private static byte[] PixelBytes(params byte[] values) =>
        values.SelectMany(value => new byte[] { value, value, value, 255 })
            .ToArray();

    private static byte[] GlyphBitmap(int size, byte value) =>
        Enumerable.Repeat(value, size * size).ToArray();

    private static AzureStyleValue EvaluateExpression(
        string json,
        AzureStyleEvaluationContext context)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.IsTrue(AzureStyleExpression.TryParse(
            document.RootElement,
            out AzureStyleExpression expression));
        Assert.IsTrue(expression.TryEvaluate(context, out AzureStyleValue value));
        return value;
    }

    private static void AssertExpressionFails(
        string json,
        AzureStyleEvaluationContext context)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.IsTrue(AzureStyleExpression.TryParse(
            document.RootElement,
            out AzureStyleExpression expression));
        Assert.IsFalse(expression.TryEvaluate(context, out _));
    }

    private static void AssertExpressionKind(
        string json,
        AzureStyleEvaluationContext context,
        AzureStyleValueKind expected) =>
        Assert.AreEqual(expected, EvaluateExpression(json, context).Kind);

    private static void AssertExpressionBoolean(
        string json,
        AzureStyleEvaluationContext context,
        bool expected) =>
        Assert.AreEqual(expected, EvaluateExpression(json, context).BooleanValue);

    private static void AssertExpressionNumber(
        string json,
        AzureStyleEvaluationContext context,
        double expected) =>
        Assert.AreEqual(
            expected,
            EvaluateExpression(json, context).NumberValue,
            0.000001);

    private static void AssertExpressionString(
        string json,
        AzureStyleEvaluationContext context,
        string expected) =>
        Assert.AreEqual(expected, EvaluateExpression(json, context).StringValue);
}
