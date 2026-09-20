using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class VectorStyleTests
{
    [TestMethod]
    public async Task RealisticExpressionsResolveExactCroppedSpriteAndPlacement()
    {
        VectorStyleAssets assets = CreateAssets(
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
            VectorSpriteAtlas.CreateTextureId("road", "bkt-631"),
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
        VectorStyleAssets assets = CreateAssets(
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
    }

    [TestMethod]
    public async Task IconImageTokensResolveFeatureProperties()
    {
        VectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [{
                "type": "symbol",
                "source-layer": "poi",
                "layout": {
                  "icon-image": "marker-{kind}"
                }
              }]
            }
            """,
            """
            {
              "marker-park": {
                "x": 0,
                "y": 0,
                "width": 1,
                "height": 1,
                "pixelRatio": 1,
                "visible": true
              }
            }
            """,
            PixelBytes(9),
            1,
            1);
        VectorTileFeatureCollection features = CreateFeatures(
            new VectorTileProperty(
                "kind",
                VectorTileValue.FromString("park")));

        Assert.ContainsSingle(await assets.PrepareTexturesAsync(
            features,
            10,
            CancellationToken.None));
        Assert.ContainsSingle(assets.ResolveSymbols(features, 10).Symbols);
        Assert.ContainsSingle(assets.ResolveSymbols(features, 10).Symbols);
    }

    [TestMethod]
    public async Task PreparationIncludesBothSidesOfFractionalZoomStops()
    {
        VectorStyleAssets assets = CreateAssets(
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
        VectorStyle style = VectorStyle.Parse(Encoding.UTF8.GetBytes(
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
                VectorStyleLayerParseResult.UnsupportedVectorSource));
        Assert.AreEqual(
            0,
            style.GetUnsupportedLayerCount(
                VectorStyleLayerParseResult.UnsupportedSymbolPlacement));
        Assert.AreEqual(
            0,
            style.GetUnsupportedLayerCount(
                VectorStyleLayerParseResult.UnsupportedTextFit));
        Assert.AreEqual(
            0,
            style.GetUnsupportedLayerCount(
                VectorStyleLayerParseResult.UnsupportedIconRotation));
        Assert.AreEqual(
            1,
            style.GetUnsupportedLayerCount(
                VectorStyleLayerParseResult.UnsupportedExpression));
        Assert.AreEqual(
            1,
            style.GetUnsupportedLayerCount(
                VectorStyleLayerParseResult.UnsupportedSourceLayer));
    }

    [TestMethod]
    public void EvaluationFailuresAreExplicitlyTyped()
    {
        VectorStyle style = VectorStyle.Parse(Encoding.UTF8.GetBytes(
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
        VectorStyleEvaluationContext context = new(feature, 10);

        Assert.AreEqual(
            VectorStyleFilterResult.EvaluationFailure,
            style.IconLayers[0].EvaluateFilter(context));
        Assert.AreEqual(
            VectorStyleIconResult.EvaluationFailure,
            style.IconLayers[1].EvaluateIcon(
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
                out _,
                out _,
                out _));
    }

    [TestMethod]
    public async Task LinePlacementResolvesIconsAndTextAgainstLineGeometry()
    {
        VectorStyleAssets assets = CreateAssets(
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
        assets.GlyphAtlas.AddRangeForTest(new VectorGlyphRange(
            "Roboto-Regular",
            0,
            new Dictionary<int, VectorGlyph>
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
        VectorStyleAssets assets = CreateAssets(
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
        assets.GlyphAtlas.AddRangeForTest(new VectorGlyphRange(
            "Roboto-Regular",
            0,
            new Dictionary<int, VectorGlyph>
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
        VectorStyleAssets assets = CreateAssets(
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
        VectorStyleAssets assets = CreateAssets(
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
        VectorStyleAssets assets = CreateAssets(
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
            VectorSpriteAtlas.CreateTextureId("road", "road-pattern"),
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
        VectorStyleAssets assets = CreateAssets(
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
        VectorStyleAssets assets = CreateAssets(
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
    public void LegacyZoomStopsInterpolateRgbaFillColors()
    {
        VectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [{
                "type": "fill",
                "source-layer": "land",
                "paint": {
                  "fill-color": {
                    "base": 1,
                    "stops": [
                      [8, "rgba(0, 0, 255, 0.5)"],
                      [12, "rgba(255, 0, 0, 1)"]
                    ]
                  }
                }
              }]
            }
            """,
            "{}",
            PixelBytes(0),
            1,
            1);

        VectorTileStyledPolygon polygon = Assert.ContainsSingle(
            assets.ResolvePolygons(CreatePolygonFeatures("park"), 10).Polygons);

        Assert.AreEqual(0.5, polygon.Style.Color.X, 0.01);
        Assert.AreEqual(0, polygon.Style.Color.Y, 0.01);
        Assert.AreEqual(0.25, polygon.Style.Color.Z, 0.01);
        Assert.AreEqual(0.75, polygon.Style.Color.W, 0.01);
    }

    [TestMethod]
    public void BackgroundLayerResolvesIndependentlyOfTileFeatures()
    {
        VectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [{
                "type": "background",
                "paint": {
                  "background-color": "rgb(10, 20, 30)",
                  "background-opacity": 0.5
                }
              }]
            }
            """,
            "{}",
            PixelBytes(0),
            1,
            1);

        VectorResolvedBackground background = Assert.ContainsSingle(
            assets.ResolveBackgrounds(10).Backgrounds);

        Assert.AreEqual(10 / 255d * 0.5, background.Style.Color.X, 0.001);
        Assert.AreEqual(20 / 255d * 0.5, background.Style.Color.Y, 0.001);
        Assert.AreEqual(30 / 255d * 0.5, background.Style.Color.Z, 0.001);
        Assert.AreEqual(0.5, background.Style.Color.W, 0.001);
    }

    [TestMethod]
    public void TextFieldTokensResolveFeatureProperties()
    {
        VectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [{
                "type": "symbol",
                "source-layer": "poi",
                "layout": {
                  "text-field": "Place: {_name_global}"
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
                "_name_global",
                VectorTileValue.FromString("Seattle")));

        VectorTileAccessibilityFeature feature = Assert.ContainsSingle(
            assets.ResolveAccessibilityFeatures(features, 10));

        Assert.AreEqual("Place: Seattle", feature.Name);
    }

    [TestMethod]
    public void LegacyCategoricalStopsResolveFeatureValues()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "type": "categorical",
              "property": "direction",
              "default": 0,
              "stops": [["forward", 0], ["reverse", 180]]
            }
            """);
        Assert.IsTrue(VectorStyleExpression.TryParseStyleValue(
            document.RootElement,
            out VectorStyleExpression expression));
        VectorTileFeature feature = CreateFeatures(
            new VectorTileProperty(
                "direction",
                VectorTileValue.FromString("reverse"))).Features[0];

        Assert.IsTrue(expression.TryEvaluate(
            new VectorStyleEvaluationContext(feature, 10),
            out VectorStyleValue value));
        Assert.AreEqual(180, value.NumberValue);
    }

    [TestMethod]
    public void LegacyPropertyStopsUsePropertyDefaultWhenValueIsMissing()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "property": "importance",
              "stops": [[0, 2], [10, 12]]
            }
            """);
        Assert.IsTrue(VectorStyleExpression.TryParseStyleValue(
            document.RootElement,
            VectorStyleValue.FromNumber(7),
            out VectorStyleExpression expression));

        Assert.IsTrue(expression.TryEvaluate(
            new VectorStyleEvaluationContext(
                CreateFeatures().Features[0],
                10),
            out VectorStyleValue value));
        Assert.AreEqual(7, value.NumberValue);
    }

    [TestMethod]
    public void CompositeLegacyStopsAreRejectedInsteadOfMisapplied()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "property": "importance",
              "stops": [[[5, 0], 2], [[10, 1], 12]]
            }
            """);

        Assert.IsFalse(VectorStyleExpression.TryParseStyleValue(
            document.RootElement,
            out _));
    }

    [TestMethod]
    public void BackgroundLayerAfterFeatureLayersIsSkipped()
    {
        VectorStyle style = VectorStyle.Parse(
            Encoding.UTF8.GetBytes(
                """
                {
                  "version": 8,
                  "layers": [
                    {
                      "type": "fill",
                      "source-layer": "land"
                    },
                    {
                      "type": "background",
                      "paint": {
                        "background-color": "#102030"
                      }
                    }
                  ]
                }
                """));

        Assert.IsEmpty(style.BackgroundLayers);
        Assert.AreEqual(
            1,
            style.GetUnsupportedLayerCount(
                VectorStyleLayerParseResult.UnsupportedExpression));
    }

    [TestMethod]
    public async Task PatternedFillsAndExplicitOutlinesResolve()
    {
        VectorStyleAssets assets = CreateAssets(
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
        VectorStyleAssets assets = CreateAssets(
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
        VectorStyleAssets assets = VectorStyleAssets.CreateForTest(
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
            VectorSpriteAtlas.ParseIndex(Encoding.UTF8.GetBytes(
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
    [DataRow(7, true, false)]
    [DataRow(7, true, true)]
    [DataRow(10, false, false)]
    [DataRow(10, false, true)]
    public async Task AzurePlaceLabelsResolveWithPropertyDrivenMaximumWidth(
        int zoom, bool hasIcon, bool hasMaximumWidth)
    {
        const string textLayout = """
            "text-field": ["get", "name"],
            "text-font": ["Roboto-Regular"],
            "text-size": ["get", "name-f"],
            "text-max-width": ["+", ["number", ["get", "max-text-width"], 9], 1],
            "text-justify": "auto",
            "text-padding": 0,
            "symbol-sort-key": ["coalesce", ["get", "st-lblimp"], ["get", "label-importance"], 255]
            """;
        string styleJson = $$"""
            {
              "version": 8,
              "layers": [
                {
                  "type": "symbol",
                  "source-layer": "populated_place",
                  "minzoom": 3,
                  "maxzoom": 9,
                  "filter": ["has", "has-icon"],
                  "layout": {
                    "icon-image": "marker",
                    "text-variable-anchor": ["bottom-left", "bottom-right", "top-left", "top-right"],
                    "text-radial-offset": 0.29,
                    {{textLayout}}
                  }
                },
                {
                  "type": "symbol",
                  "source-layer": "populated_place",
                  "minzoom": 8,
                  "maxzoom": 16,
                  "filter": ["!", ["has", "has-icon"]],
                  "layout": {
                    {{textLayout}}
                  }
                }
              ]
            }
            """;
        VectorStyle style = VectorStyle.Parse(Encoding.UTF8.GetBytes(styleJson));
        Assert.AreEqual(0, style.UnsupportedLayerCount);
        Assert.HasCount(2, style.TextLayers);
        VectorStyleAssets assets = CreateAssets(
            styleJson,
            """{"marker":{"x":0,"y":0,"width":1,"height":1,"pixelRatio":1,"visible":true}}""",
            PixelBytes(0),
            1,
            1);
        assets.GlyphAtlas.AddRangeForTest(new VectorGlyphRange(
            "Roboto-Regular",
            0,
            new Dictionary<int, VectorGlyph>
            {
                ['L'] = new('L', GlyphBitmap(8, 128), 2, 2, 0, 2, 3),
                ['A'] = new('A', GlyphBitmap(8, 128), 2, 2, 0, 2, 3),
            }));
        List<VectorTileProperty> properties =
        [
            new("name", VectorTileValue.FromString("LA")),
            new("name-f", VectorTileValue.FromDouble(24)),
        ];
        if (hasIcon)
        {
            properties.Add(new("has-icon", VectorTileValue.FromBool(true)));
        }
        if (hasMaximumWidth)
        {
            properties.Add(new("max-text-width", VectorTileValue.FromDouble(4)));
        }
        VectorTileFeatureCollection features = new(
        [
            new VectorTileFeature(
                "populated_place",
                VectorTileGeometryType.Point,
                [new VectorTilePoint(0.5, 0.5)],
                properties.ToArray(),
                [],
                []),
        ]);

        await assets.PrepareTexturesAsync(features, zoom, CancellationToken.None);
        VectorSymbolResolution resolution = assets.ResolveSymbols(features, zoom);

        Assert.AreEqual(0, resolution.EvaluationFailureCount);
        Assert.AreEqual(0, resolution.UnavailableGlyphCount);
        Assert.AreEqual(2, resolution.ResolvedGlyphCount);
        Assert.HasCount(2, resolution.Symbols.Where(symbol => symbol.Kind == VectorSymbolKind.Text));
        Assert.HasCount(hasIcon ? 1 : 0,
            resolution.Symbols.Where(symbol => symbol.Kind == VectorSymbolKind.Icon));
    }

    [TestMethod]
    public async Task PointTextResolvesSharedGlyphTexturesAndStylePlacement()
    {
        VectorStyleAssets assets = CreateAssets(
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
        assets.GlyphAtlas.AddRangeForTest(new VectorGlyphRange(
            "Roboto-Regular",
            0,
            new Dictionary<int, VectorGlyph>
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
        VectorStyleAssets assets = CreateAssets(
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
        assets.GlyphAtlas.AddRangeForTest(new VectorGlyphRange(
            "Roboto-Regular",
            0,
            new Dictionary<int, VectorGlyph>
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
        VectorStyleAssets assets = CreateAssets(
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
        assets.GlyphAtlas.AddRangeForTest(new VectorGlyphRange(
            "Roboto-Regular",
            0,
            new Dictionary<int, VectorGlyph>
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
        VectorStyleAssets assets = CreateAssets(
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
        assets.GlyphAtlas.AddRangeForTest(new VectorGlyphRange(
            "Roboto-Regular",
            0,
            new Dictionary<int, VectorGlyph>
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
                VectorStyle.Parse(Encoding.UTF8.GetBytes(style)));
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
            VectorStyleAssets assets = CreateAssets(
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
        VectorStyleAssets assets = CreateAssets(
            $$"""{"version":8,"layers":[{{layers}}]}""",
            "{}",
            PixelBytes(0),
            1,
            1);
        assets.GlyphAtlas.AddRangeForTest(new VectorGlyphRange(
            "Roboto-Regular",
            0,
            new Dictionary<int, VectorGlyph>
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
        VectorStyleAssets assets = CreateAssets(
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
    [DataRow("[\"+\",1,2]", 3d)]
    [DataRow("[\"+\",1,2,3,4]", 10d)]
    [DataRow("[\"+\",-2.5,0.5]", -2d)]
    [DataRow("[\"+\",[\"*\",2,3],4]", 10d)]
    [DataRow("[\"+\",[\"zoom\"],0.5]", 11d)]
    [DataRow("[\"+\",[\"number\",[\"get\",\"missing\"],9],1]", 10d)]
    [DataRow("[\"let\",\"width\",4,[\"+\",[\"var\",\"width\"],1]]", 5d)]
    public void AdditionExpressionsEvaluateNumericOperands(string json, double expected)
    {
        AssertExpressionNumber(json, new VectorStyleEvaluationContext(null, 10.5), expected);
    }

    [TestMethod]
    [DataRow("[\"+\",1,\"2\"]")]
    [DataRow("[\"+\",1,null]")]
    [DataRow("[\"+\",1,true]")]
    [DataRow("[\"+\",1,[\"literal\",[2]]]")]
    [DataRow("[\"+\",1,[\"get\",\"missing\"]]")]
    [DataRow("[\"+\",1e308,1e308]")]
    public void AdditionRejectsInvalidOperandsAndOverflow(string json)
    {
        AssertExpressionFails(json, new VectorStyleEvaluationContext(null, 10));
    }

    [TestMethod]
    [DataRow("[\"+\"]")]
    [DataRow("[\"+\",1]")]
    public void AdditionRequiresAtLeastTwoOperands(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.IsFalse(VectorStyleExpression.TryParse(document.RootElement, out _));
    }

    [TestMethod]
    public void SupportedExpressionsEvaluateValuesAndRejectTypeMismatches()
    {
        VectorTileFeature feature = CreateFeatures(
            new VectorTileProperty("number", VectorTileValue.FromDouble(2)),
            new VectorTileProperty("name", VectorTileValue.FromString("road")),
            new VectorTileProperty("enabled", VectorTileValue.FromBool(true)))
            .Features[0];
        VectorStyleEvaluationContext context = new(feature, 10.5);

        AssertExpressionNumber("""["get","number"]""", context, 2);
        AssertExpressionKind("""["get","missing"]""", context, VectorStyleValueKind.Null);
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
        VectorStyleValue array = EvaluateExpression(
            """["interpolate",["linear"],["zoom"],10,["literal",[0,10]],12,["literal",[20,30]]]""",
            context);
        Assert.AreEqual(VectorStyleValueKind.Array, array.Kind);
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
    public void MembershipStillEvaluatesFailingOperandsAfterAnEarlierMatch()
    {
        VectorStyleEvaluationContext context = new(null, 10);
        AssertExpressionBoolean("""["in",2,["literal",[1,2,3]],4]""", context, true);
        AssertExpressionBoolean("""["in",5,["literal",[1,2,3]],4]""", context, false);
        AssertExpressionFails("""["in",2,["literal",[1,2,3]],["var","missing"]]""", context);
        AssertExpressionNumber(
            """["match",2,[1,2,3],7,["var","missing"]]""", context, 7);
    }

    [TestMethod]
    public void LegacyArcGisFiltersResolveFeatureProperties()
    {
        VectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [{
                "type": "line",
                "source-layer": "road",
                "filter": ["all",
                  ["==", "_symbol", 3],
                  ["!in", "Viz", 3]
                ],
                "paint": {
                  "line-color": "#e69973",
                  "line-width": 4
                }
              }]
            }
            """,
            "{}",
            PixelBytes(0),
            1,
            1);

        VectorLineResolution matching = assets.ResolveLines(
            CreateLineFeatures(
                new VectorTileProperty(
                    "_symbol",
                    VectorTileValue.FromUInt(3)),
                new VectorTileProperty(
                    "Viz",
                    VectorTileValue.FromUInt(2))),
            10);
        VectorLineResolution excluded = assets.ResolveLines(
            CreateLineFeatures(
                new VectorTileProperty(
                    "_symbol",
                    VectorTileValue.FromUInt(3)),
                new VectorTileProperty(
                    "Viz",
                    VectorTileValue.FromUInt(3))),
            10);

        Assert.ContainsSingle(matching.Lines);
        Assert.IsEmpty(excluded.Lines);
        Assert.AreEqual(0, matching.EvaluationFailureCount);
        Assert.AreEqual(0, excluded.EvaluationFailureCount);
    }

    [TestMethod]
    public void ArcGisLayoutAndPaintPropertiesResolve()
    {
        VectorStyle style = VectorStyle.ParseCustom(
            Encoding.UTF8.GetBytes(
                """
                {
                  "version": 8,
                  "layers": [
                    {
                      "type": "symbol",
                      "source-layer": "road",
                      "layout": {
                        "icon-image": "shield",
                        "icon-padding": 7,
                        "symbol-avoid-edges": true
                      }
                    },
                    {
                      "type": "symbol",
                      "source-layer": "road",
                      "layout": {
                        "text-field": "{name}",
                        "text-font": ["TestFont"],
                        "text-size": 10,
                        "text-max-width": 8,
                        "text-line-height": 1.4,
                        "text-justify": "right",
                        "text-padding": 6,
                        "text-keep-upright": false,
                        "text-max-angle": 30,
                        "symbol-avoid-edges": true
                      },
                      "paint": {
                        "text-color": "#ff0000",
                        "text-halo-color": "#0000ff",
                        "text-halo-width": 2,
                        "text-halo-blur": 3,
                        "text-opacity": 0.5
                      }
                    },
                    {
                      "type": "fill",
                      "source-layer": "building",
                      "paint": {
                        "fill-color": "#00ff00",
                        "fill-translate": {
                          "stops": [[0, "0 0"], [10, "6 4"]]
                        },
                        "fill-translate-anchor": "viewport",
                        "fill-antialias": false
                      }
                    }
                  ]
                }
                """));
        VectorTileFeature feature = CreateFeatures(
            new VectorTileProperty(
                "name",
                VectorTileValue.FromString("I-405"))).Features[0];
        VectorStyleEvaluationContext context = new(feature, 5);

        Assert.AreEqual(
            VectorStyleIconResult.Resolved,
            style.IconLayers[0].EvaluateIcon(
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
                out double iconPadding,
                out bool iconAvoidEdges,
                out _,
                out _,
                out _,
                out _));
        Assert.AreEqual(7, iconPadding);
        Assert.IsTrue(iconAvoidEdges);

        Assert.AreEqual(
            VectorStyleTextResult.Resolved,
            style.TextLayers[0].EvaluateText(
                context,
                out VectorTextStyle text));
        Assert.AreEqual(8, text.MaximumWidth);
        Assert.AreEqual(1.4, text.LineHeight);
        Assert.AreEqual("right", text.Justify);
        Assert.AreEqual(6, text.CollisionPadding);
        Assert.IsFalse(text.KeepUpright);
        Assert.AreEqual(Math.PI / 6, text.MaximumAngle, 0.000001);
        Assert.IsTrue(text.AvoidEdges);
        Assert.AreEqual(2, text.Paint.HaloOffset);
        Assert.AreEqual(3, text.Paint.HaloBlur);
        Assert.AreEqual(0.5, text.Paint.Color.W, 0.000001);
        Assert.AreEqual(0.5, text.Paint.HaloColor.W, 0.000001);

        Assert.AreEqual(
            VectorStyleFillResult.Resolved,
            style.FillLayers[0].EvaluateFill(
                context,
                out VectorFillStyle fill,
                out _));
        Assert.AreEqual(3, fill.TranslateX);
        Assert.AreEqual(2, fill.TranslateY);
        Assert.AreEqual(
            VectorTranslateAnchor.Viewport,
            fill.TranslateAnchor);
        Assert.IsFalse(fill.Antialias);
        Assert.IsNull(fill.OutlineColor);
    }

    [TestMethod]
    public void FillAntialiasDefaultsOutlineToFillColor()
    {
        VectorStyle style = VectorStyle.ParseCustom(
            Encoding.UTF8.GetBytes(
                """
                {
                  "version": 8,
                  "layers": [{
                    "type": "fill",
                    "source-layer": "building",
                    "paint": {
                      "fill-color": "#00ff00",
                      "fill-opacity": 0.5
                    }
                  }]
                }
                """));
        VectorTileFeature feature = CreateFeatures().Features[0];

        Assert.AreEqual(
            VectorStyleFillResult.Resolved,
            style.FillLayers[0].EvaluateFill(
                new VectorStyleEvaluationContext(feature, 10),
                out VectorFillStyle fill,
                out _));
        Assert.IsTrue(fill.Antialias);
        Assert.AreEqual(fill.Color, fill.OutlineColor);
    }

    [TestMethod]
    public void TextMaximumWidthLineHeightAndJustifyShapeMultipleLines()
    {
        VectorStyleAssets assets = CreateAssets(
            """
            {
              "version": 8,
              "layers": [{
                "type": "symbol",
                "source-layer": "poi",
                "layout": {
                  "text-field": "AA A",
                  "text-font": ["TestFont"],
                  "text-size": 10,
                  "text-max-width": 0.7,
                  "text-line-height": 2,
                  "text-justify": "right",
                  "text-padding": 9,
                  "text-keep-upright": false,
                  "text-max-angle": 30,
                  "symbol-avoid-edges": true
                }
              }]
            }
            """,
            "{}",
            PixelBytes(0),
            1,
            1);
        assets.GlyphAtlas.AddRangeForTest(new VectorGlyphRange(
            "TestFont",
            0,
            new Dictionary<int, VectorGlyph>
            {
                ['A'] = new(
                    'A',
                    GlyphBitmap(8, 128),
                    6,
                    8,
                    0,
                    8,
                    7),
            }));

        VectorTileSymbol[] glyphs = assets.ResolveSymbols(
            CreateFeatures(),
            10).Symbols.Where(symbol =>
                symbol.Kind == VectorSymbolKind.Text).ToArray();

        Assert.HasCount(3, glyphs);
        double[] rows = glyphs.Select(glyph => glyph.OffsetY)
            .Distinct()
            .Order()
            .ToArray();
        Assert.HasCount(2, rows);
        Assert.AreEqual(20, rows[1] - rows[0], 0.000001);
        double firstRight = glyphs.Where(glyph => glyph.OffsetY == rows[0])
            .Max(glyph => glyph.OffsetX + (glyph.Width / 2));
        double secondRight = glyphs.Where(glyph => glyph.OffsetY == rows[1])
            .Max(glyph => glyph.OffsetX + (glyph.Width / 2));
        Assert.AreEqual(firstRight, secondRight, 0.000001);
        Assert.IsTrue(glyphs.All(glyph =>
            glyph.CollisionPadding == 9 &&
            glyph.AvoidEdges &&
            !glyph.KeepUpright &&
            Math.Abs(glyph.MaximumAngle - (Math.PI / 6)) < 0.000001));
    }

    [TestMethod]
    public void CompatibilityReportCountsUnsupportedConstructsWithoutStyleData()
    {
        IReadOnlyList<VectorStyleCompatibilityIssue> issues =
            VectorStyleCompatibility.Analyze(
                Encoding.UTF8.GetBytes(
                    """
                    {
                      "version": 8,
                      "layers": [
                        {
                          "type": "fill",
                          "layout": {
                            "visibility": "visible",
                            "private-layout-property": true
                          },
                          "paint": {
                            "fill-color": "#ffffff",
                            "fill-antialias": true,
                            "fill-translate": [1, 2]
                          }
                        },
                        {
                          "type": "symbol",
                          "layout": {
                            "text-field": "{name}",
                            "text-max-width": 12,
                            "symbol-avoid-edges": true
                          },
                          "paint": {
                            "text-color": "#000000",
                            "text-halo-blur": 1
                          }
                        },
                        {
                          "type": "circle"
                        },
                        {
                          "type": "private-layer-type"
                        }
                      ]
                    }
                    """));

        Assert.AreSequenceEqual(
            new[]
            {
                new VectorStyleCompatibilityIssue(
                    VectorStyleCompatibilityIssueKind.UnsupportedLayerType,
                    "circle",
                    1),
                new VectorStyleCompatibilityIssue(
                    VectorStyleCompatibilityIssueKind.UnsupportedLayerType,
                    "other",
                    1),
                new VectorStyleCompatibilityIssue(
                    VectorStyleCompatibilityIssueKind.UnsupportedLayoutProperty,
                    "other",
                    1),
                new VectorStyleCompatibilityIssue(
                    VectorStyleCompatibilityIssueKind.IgnoredLayoutProperty,
                    "symbol-avoid-edges",
                    1),
            },
            issues);
    }

    [TestMethod]
    public void AccessibilityFeaturesReuseLocalizedTextWithoutGlyphResolution()
    {
        VectorStyleAssets assets = CreateAssets(
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
        VectorStyleAssets assets = VectorStyleAssets.CreateForTest(
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

    [TestMethod]
    [DataRow("line")]
    [DataRow("fill")]
    [DataRow("symbol")]
    public async Task StaticResolutionCacheReusesFractionalZoomUntilVisibilityChanges(
        string family)
    {
        (VectorStyleAssets assets, VectorTileFeatureCollection features) =
            CreateCacheTestStyle(family, """
                "minzoom": 14.25, "maxzoom": 14.28125,
                """);
        await assets.PrepareTexturesAsync(features, 14, CancellationToken.None);
        object cache = CreateResolutionCache(features, assets);

        object below = GetCachedResolution(cache, family, 14.24);
        object visible = GetCachedResolution(cache, family, 14.25);
        Assert.AreNotSame(below, visible);
        Assert.AreEqual(0, GetResolvedItemCount(below));
        Assert.AreEqual(1, GetResolvedItemCount(visible));
        Assert.AreSame(visible, GetCachedResolution(cache, family, 14.265625));
        object above = GetCachedResolution(cache, family, 14.28125);
        Assert.AreNotSame(visible, above);
        Assert.AreEqual(0, GetResolvedItemCount(above));
        Assert.AreSame(above, GetCachedResolution(cache, family, 14.3));
        object visibleAgain = GetCachedResolution(cache, family, 14.265625);
        Assert.AreNotSame(above, visibleAgain);
        Assert.AreEqual(1, GetResolvedItemCount(visibleAgain));
    }

    [TestMethod]
    [DataRow("line", "\"paint\":{\"line-width\":[\"interpolate\",[\"linear\"],[\"zoom\"],14,1,15,9]},")]
    [DataRow("fill", "\"paint\":{\"fill-opacity\":[\"interpolate\",[\"linear\"],[\"zoom\"],14,0.1,15,1]},")]
    [DataRow("symbol", "\"layout\":{\"icon-image\":\"marker\",\"icon-size\":[\"let\",\"z\",[\"zoom\"],[\"interpolate\",[\"linear\"],[\"var\",\"z\"],14,1,15,9]]},")]
    [DataRow("line", "\"paint\":{\"line-width\":{\"stops\":[[14,1],[15,9]]}},")]
    [DataRow("line", "\"filter\":[\"step\",[\"zoom\"],true,14.26,false],")]
    [DataRow("fill", "\"filter\":[\"step\",[\"zoom\"],true,14.26,false],")]
    [DataRow("symbol", "\"filter\":[\"step\",[\"zoom\"],true,14.26,false],")]
    [DataRow("line", "\"layout\":{\"visibility\":[\"step\",[\"zoom\"],\"visible\",14.26,\"none\"]},")]
    [DataRow("symbol", "\"layout\":{\"icon-image\":\"marker\",\"icon-rotation-alignment\":[\"step\",[\"zoom\"],\"map\",14.26,\"viewport\"]},")]
    public async Task ZoomDependentResolutionCacheRecomputesAtExactFractionalZoom(
        string family,
        string properties)
    {
        (VectorStyleAssets assets, VectorTileFeatureCollection features) =
            CreateCacheTestStyle(family, properties);
        await assets.PrepareTexturesAsync(features, 14, CancellationToken.None);
        object cache = CreateResolutionCache(features, assets);
        object first = GetCachedResolution(cache, family, 14.25);
        object next = GetCachedResolution(cache, family, 14.265625);

        Assert.AreEqual(1, GetResolvedItemCount(first));
        Assert.AreNotSame(first, next);
        Assert.AreSame(next, GetCachedResolution(cache, family, 14.265625));
        switch (first, next)
        {
            case (VectorLineResolution a, VectorLineResolution b)
                when b.Lines.Length != 0:
                Assert.AreNotEqual(a.Lines[0].Style.Width, b.Lines[0].Style.Width);
                break;
            case (VectorPolygonResolution a, VectorPolygonResolution b)
                when b.Polygons.Length != 0:
                Assert.AreNotEqual(a.Polygons[0].Style, b.Polygons[0].Style);
                break;
            case (VectorSymbolResolution a, VectorSymbolResolution b)
                when b.Symbols.Length != 0:
                Assert.AreNotEqual(a.Symbols[0], b.Symbols[0]);
                break;
            default:
                Assert.AreEqual(0, GetResolvedItemCount(next));
                break;
        }
    }

    [TestMethod]
    public async Task StaticSymbolResolutionCacheRetainsTextScaleInvalidation()
    {
        (VectorStyleAssets assets, VectorTileFeatureCollection features) =
            CreateCacheTestStyle("symbol", """
                "layout":{"text-field":"R"},
                """);
        assets.GlyphAtlas.AddRangeForTest(new VectorGlyphRange(
            "Roboto-Regular",
            0,
            new Dictionary<int, VectorGlyph>
            {
                ['R'] = new('R', GlyphBitmap(8, 128), 2, 2, 0, 2, 3),
            }));
        await assets.PrepareTexturesAsync(features, 14, CancellationToken.None);
        object cache = CreateResolutionCache(features, assets);
        var first = (VectorSymbolResolution)GetCachedResolution(
            cache, "symbol", 14.25);
        Assert.AreSame(first, GetCachedResolution(cache, "symbol", 14.265625));
        var scaled = (VectorSymbolResolution)GetCachedResolution(
            cache, "symbol", 14.265625, 2);
        Assert.AreNotSame(first, scaled);
        Assert.AreEqual(
            Assert.ContainsSingle(first.Symbols).Width * 2,
            Assert.ContainsSingle(scaled.Symbols).Width,
            0.000001);
        Assert.AreSame(scaled, GetCachedResolution(cache, "symbol", 14.27, 2));
    }

    [TestMethod]
    public void MissingGlyphsPreventCrossZoomSymbolReuse()
    {
        (VectorStyleAssets assets, VectorTileFeatureCollection features) =
            CreateCacheTestStyle("symbol", """
                "layout":{"text-field":"R"},
                """);
        object cache = CreateResolutionCache(features, assets);
        var missing = (VectorSymbolResolution)GetCachedResolution(
            cache, "symbol", 14.25);
        Assert.AreEqual(1, missing.UnavailableGlyphCount);
        assets.GlyphAtlas.AddRangeForTest(new VectorGlyphRange(
            "Roboto-Regular",
            0,
            new Dictionary<int, VectorGlyph>
            {
                ['R'] = new('R', GlyphBitmap(8, 128), 2, 2, 0, 2, 3),
            }));

        var available = (VectorSymbolResolution)GetCachedResolution(
            cache, "symbol", 14.265625);
        Assert.AreNotSame(missing, available);
        Assert.AreEqual(0, available.UnavailableGlyphCount);
        Assert.ContainsSingle(available.Symbols);
    }

    [TestMethod]
    public async Task MissingSpritesPreventCrossZoomSymbolReuse()
    {
        (VectorStyleAssets assets, VectorTileFeatureCollection features) =
            CreateCacheTestStyle("symbol", string.Empty);
        object cache = CreateResolutionCache(features, assets);
        var missing = (VectorSymbolResolution)GetCachedResolution(
            cache, "symbol", 14.25);
        Assert.AreEqual(1, missing.UnavailableSpriteCount);
        await assets.PrepareTexturesAsync(features, 14, CancellationToken.None);

        var available = (VectorSymbolResolution)GetCachedResolution(
            cache, "symbol", 14.265625);
        Assert.AreNotSame(missing, available);
        Assert.AreEqual(0, available.UnavailableSpriteCount);
        Assert.ContainsSingle(available.Symbols);
    }

    [TestMethod]
    public async Task PatternedLineSymbolsRetainFractionalVisibilityBoundaries()
    {
        (VectorStyleAssets assets, VectorTileFeatureCollection features) =
            CreateCacheTestStyle("line", """
                "minzoom":14.25, "maxzoom":14.28125,
                "paint":{"line-pattern":"marker"},
                """);
        await assets.PrepareTexturesAsync(features, 14, CancellationToken.None);
        object cache = CreateResolutionCache(features, assets);
        object below = GetCachedResolution(cache, "symbol", 14.24);
        object visible = GetCachedResolution(cache, "symbol", 14.25);
        Assert.AreEqual(0, GetResolvedItemCount(below));
        Assert.AreEqual(1, GetResolvedItemCount(visible));
        Assert.AreSame(visible, GetCachedResolution(cache, "symbol", 14.265625));
        object above = GetCachedResolution(cache, "symbol", 14.28125);
        Assert.AreNotSame(visible, above);
        Assert.AreEqual(0, GetResolvedItemCount(above));
    }

    [TestMethod]
    public void ZoomDependentTextAndLinePatternsInvalidateSymbolsOnlyAsNeeded()
    {
        (VectorStyleAssets text, _) = CreateCacheTestStyle("symbol", """
            "layout":{"text-field":"R"},
            "paint":{"text-opacity":["interpolate",["linear"],["zoom"],14,0,15,1]},
            """);
        Assert.IsFalse(text.CanReuseSymbols(14.25, 14.265625));
        Assert.IsTrue(text.CanReuseLines(14.25, 14.265625));
        Assert.IsTrue(text.CanReusePolygons(14.25, 14.265625));

        (VectorStyleAssets line, _) = CreateCacheTestStyle("line", """
            "paint":{"line-pattern":["step",["zoom"],"marker",14.26,"other"]},
            """);
        Assert.IsFalse(line.CanReuseSymbols(14.25, 14.265625));
        Assert.IsFalse(line.CanReuseLines(14.25, 14.265625));
        Assert.IsTrue(line.CanReusePolygons(14.25, 14.265625));
    }

    [TestMethod]
    [DataRow("[\"literal\",[\"zoom\"]]", false)]
    [DataRow("[\"get\",\"zoom\"]", false)]
    [DataRow("[\"let\",\"z\",[\"zoom\"],[\"var\",\"z\"]]", true)]
    [DataRow("{\"stops\":[[14,1],[15,2]]}", true)]
    [DataRow("{\"property\":\"width\",\"stops\":[[1,1],[2,2]]}", false)]
    [DataRow("[\"case\",false,[\"zoom\"],1]", true)]
    [DataRow("[\"+\",[\"zoom\"],1]", true)]
    [DataRow("[\"+\",[\"get\",\"width\"],1]", false)]
    public void ParsedExpressionsTrackTransitiveZoomDependency(
        string json,
        bool expected)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.IsTrue(VectorStyleExpression.TryParseStyleValue(
            document.RootElement,
            out VectorStyleExpression expression));
        Assert.AreEqual(expected, expression.DependsOnZoom);
    }

    private static (VectorStyleAssets Assets, VectorTileFeatureCollection Features)
        CreateCacheTestStyle(string family, string properties)
    {
        string sourceLayer = family switch
        {
            "line" => "road",
            "fill" => "land",
            _ => "poi",
        };
        string defaultLayout = family == "symbol" &&
            !properties.Contains("\"layout\"", StringComparison.Ordinal)
                ? "\"layout\":{\"icon-image\":\"marker\"},"
                : string.Empty;
        VectorStyleAssets assets = CreateAssets(
            $$"""
            {
              "version":8,
              "layers":[{
                {{properties}}
                {{defaultLayout}}
                "type":"{{family}}",
                "source-layer":"{{sourceLayer}}"
              }]
            }
            """,
            """
            {"marker":{"x":0,"y":0,"width":1,"height":1,"pixelRatio":1,"visible":true}}
            """,
            PixelBytes(9),
            1,
            1);
        return (assets, family switch
        {
            "line" => CreateLineFeatures(),
            "fill" => CreatePolygonFeatures("park"),
            _ => CreateFeatures(),
        });
    }

    private static object CreateResolutionCache(
        VectorTileFeatureCollection features,
        VectorStyleAssets assets)
    {
        Type type = typeof(MapRenderer).GetNestedType(
            "VectorTileCacheEntry", BindingFlags.NonPublic)!;
        return Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [features, assets, (int)MapStyle.Road],
            culture: null)!;
    }

    private static object GetCachedResolution(
        object cache,
        string family,
        double zoom,
        double textScaleFactor = 1)
    {
        string method = family switch
        {
            "line" => "GetLines",
            "fill" => "GetPolygons",
            _ => "GetSymbols",
        };
        return cache.GetType().GetMethod(
            method,
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(
                cache,
                family == "symbol" ? [zoom, textScaleFactor] : [zoom, false, double.PositiveInfinity])!;
    }

    private static int GetResolvedItemCount(object resolution) => resolution switch
    {
        VectorLineResolution lines => lines.Lines.Length,
        VectorPolygonResolution polygons => polygons.Polygons.Length,
        VectorSymbolResolution symbols => symbols.Symbols.Length,
        _ => throw new InvalidOperationException(),
    };

    private static VectorStyleAssets CreateAssets(
        string style,
        string sprites,
        byte[] pixels,
        uint width,
        uint height) =>
        VectorStyleAssets.CreateForTest(
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

    private static VectorStyleValue EvaluateExpression(
        string json,
        VectorStyleEvaluationContext context)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.IsTrue(VectorStyleExpression.TryParse(
            document.RootElement,
            out VectorStyleExpression expression));
        Assert.IsTrue(expression.TryEvaluate(context, out VectorStyleValue value));
        return value;
    }

    private static void AssertExpressionFails(
        string json,
        VectorStyleEvaluationContext context)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.IsTrue(VectorStyleExpression.TryParse(
            document.RootElement,
            out VectorStyleExpression expression));
        Assert.IsFalse(expression.TryEvaluate(context, out _));
    }

    private static void AssertExpressionKind(
        string json,
        VectorStyleEvaluationContext context,
        VectorStyleValueKind expected) =>
        Assert.AreEqual(expected, EvaluateExpression(json, context).Kind);

    private static void AssertExpressionBoolean(
        string json,
        VectorStyleEvaluationContext context,
        bool expected) =>
        Assert.AreEqual(expected, EvaluateExpression(json, context).BooleanValue);

    private static void AssertExpressionNumber(
        string json,
        VectorStyleEvaluationContext context,
        double expected) =>
        Assert.AreEqual(
            expected,
            EvaluateExpression(json, context).NumberValue,
            0.000001);

    private static void AssertExpressionString(
        string json,
        VectorStyleEvaluationContext context,
        string expected) =>
        Assert.AreEqual(expected, EvaluateExpression(json, context).StringValue);
}
