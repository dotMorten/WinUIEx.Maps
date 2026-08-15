using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Devices.Geolocation;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests.UITests;

[TestClass]
[DoNotParallelize]
public sealed class VectorRenderingTests
{
    [TestMethod]
    public Task PointShieldRendersCenteredTextComponents() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            TileId tileId = new(4, 8, 8);
            byte[] tile = new MapboxVectorTileBuilder()
                .AddPoint(
                    "markers",
                    2048,
                    2048,
                    new Dictionary<string, object>
                    {
                        ["label"] = "7",
                    })
                .Build();
            TestVectorTileSource source = CreateShieldSource(
                tileId,
                tile,
                """
                {
                  "version": 8,
                  "layers": [{
                    "type": "symbol",
                    "source-layer": "markers",
                    "layout": {
                      "icon-image": "shield",
                      "icon-text-fit": "both",
                      "text-field": ["get", "label"],
                      "text-font": ["TestFont"],
                      "text-size": 12
                    },
                    "paint": {
                      "text-color": "#000000"
                    }
                  }]
                }
                """,
                '7');
            MapRenderFrame captured = await RenderAsync(map, source);
            ConnectedComponent[] redComponents =
                ConnectedComponentAnalyzer.Find(
                    captured,
                    ConnectedComponentAnalyzer.Near(
                        255,
                        0,
                        0,
                        tolerance: 8,
                        minimumAlpha: 240),
                    minimumPixelCount: 100);
            ConnectedComponent[] blackComponents =
                ConnectedComponentAnalyzer.Find(
                    captured,
                    ConnectedComponentAnalyzer.Near(
                        0,
                        0,
                        0,
                        tolerance: 24,
                        minimumAlpha: 240),
                    minimumPixelCount: 20);

            Assert.AreEqual(640, captured.Width);
            Assert.AreEqual(480, captured.Height);
            Assert.AreEqual(1, redComponents.Length);
            PixelBounds shield = redComponents[0].Bounds;
            ConnectedComponent text = blackComponents.First(component =>
                shield.Contains(component.Bounds));
            Assert.AreEqual(captured.Width / 2d, shield.CenterX, 3);
            Assert.AreEqual(captured.Height / 2d, shield.CenterY, 3);
            Assert.IsTrue(shield.Width >= 20);
            Assert.IsTrue(shield.Height >= 12);
            Assert.IsTrue(shield.Contains(text.Bounds));
        });

    [TestMethod]
    public Task LineShieldsRenderEveryNumericRouteLabel() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            TileId tileId = new(4, 8, 8);
            byte[] tile = new MapboxVectorTileBuilder()
                .AddLine(
                    "roads",
                    [new(1400, 1300), new(2696, 1300)],
                    new Dictionary<string, object>
                    {
                        ["route"] = 7,
                        ["shield-scale"] = 0.8,
                    })
                .AddLine(
                    "roads",
                    [new(1400, 2800), new(2696, 2800)],
                    new Dictionary<string, object>
                    {
                        ["route"] = 12,
                        ["shield-scale"] = 1d,
                    })
                .Build();
            TestVectorTileSource source = CreateShieldSource(
                tileId,
                tile,
                """
                {
                  "version": 8,
                  "layers": [{
                    "type": "symbol",
                    "source-layer": "roads",
                    "layout": {
                      "symbol-placement": "line",
                      "icon-image": "shield",
                      "icon-text-fit": "both",
                      "icon-rotation-alignment": "viewport",
                      "text-field": ["to-string", ["get", "route"]],
                      "text-font": ["TestFont"],
                      "text-size": [
                        "*",
                        10,
                        ["number", ["get", "shield-scale"], 0.8]
                      ],
                      "text-rotation-alignment": "viewport"
                    },
                    "paint": {
                      "text-color": "#000000"
                    }
                  }]
                }
                """,
                '1',
                '2',
                '7');

            MapRenderFrame frame = await RenderAsync(map, source);
            ConnectedComponent[] shields = FindColor(
                frame,
                255,
                0,
                0,
                minimumPixelCount: 100);
            ConnectedComponent[] glyphs = FindColor(
                frame,
                0,
                0,
                0,
                minimumPixelCount: 10,
                tolerance: 24);

            Assert.AreEqual(2, shields.Length);
            Assert.IsTrue(shields.All(shield =>
                glyphs.Any(glyph => shield.Bounds.Contains(glyph.Bounds))));
            Assert.IsTrue(shields.All(shield =>
                shield.Bounds.Width >= 20 &&
                shield.Bounds.Height >= 12));
        });

    [TestMethod]
    public Task ViewportAlignedLineShieldIsUprightAndCenteredOnRoad() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            TileId tileId = new(4, 8, 8);
            byte[] tile = new MapboxVectorTileBuilder()
                .AddLine(
                    "roads",
                    [new(1024, 3072), new(3072, 1024)],
                    new Dictionary<string, object>
                    {
                        ["route"] = 7,
                    })
                .Build();
            TestVectorTileSource source = CreateShieldSource(
                tileId,
                tile,
                """
                {
                  "version": 8,
                  "layers": [
                    {
                      "type": "line",
                      "source-layer": "roads",
                      "paint": {
                        "line-color": "#00ff00",
                        "line-width": 4
                      }
                    },
                    {
                      "type": "symbol",
                      "source-layer": "roads",
                      "layout": {
                        "symbol-placement": "line",
                        "icon-image": "shield",
                        "icon-text-fit": "both",
                        "icon-rotation-alignment": "viewport",
                        "text-field": ["to-string", ["get", "route"]],
                        "text-font": ["TestFont"],
                        "text-size": 10,
                        "text-rotation-alignment": "viewport"
                      },
                      "paint": {
                        "text-color": "#000000"
                      }
                    }
                  ]
                }
                """,
                '7');

            MapRenderFrame frame = await RenderAsync(map, source);
            ConnectedComponent shield = Assert.ContainsSingle(
                FindColor(frame, 255, 0, 0, minimumPixelCount: 100));
            ConnectedComponent[] roadParts = FindColor(
                frame,
                0,
                255,
                0,
                minimumPixelCount: 20,
                tolerance: 8);
            PixelBounds road = Union(roadParts);

            Assert.IsGreaterThan(shield.Bounds.Height, shield.Bounds.Width);
            Assert.AreEqual(road.CenterX, shield.Bounds.CenterX, 3);
            Assert.AreEqual(road.CenterY, shield.Bounds.CenterY, 3);
            Assert.AreEqual(frame.Width / 2d, shield.Bounds.CenterX, 3);
            Assert.AreEqual(frame.Height / 2d, shield.Bounds.CenterY, 3);
        });

    [TestMethod]
    public Task TextFitShieldWithoutTextIsSuppressed() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            TileId tileId = new(4, 8, 8);
            byte[] tile = new MapboxVectorTileBuilder()
                .AddPoint("markers", 2048, 2048)
                .Build();
            TestVectorTileSource source = CreateShieldSource(
                tileId,
                tile,
                """
                {
                  "version": 8,
                  "layers": [{
                    "type": "symbol",
                    "source-layer": "markers",
                    "layout": {
                      "icon-image": "shield",
                      "icon-text-fit": "both",
                      "text-field": ["get", "missing-label"],
                      "text-font": ["TestFont"],
                      "text-size": 12
                    }
                  }]
                }
                """,
                '7');

            MapRenderFrame frame = await RenderAsync(map, source);

            Assert.IsEmpty(FindColor(
                frame,
                255,
                0,
                0,
                minimumPixelCount: 20));
        });

    [TestMethod]
    public Task LabelGlyphsFadeTogetherAfterTexturesBecomeReady() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            using RenderingEventListener listener =
                new("VectorLabelFadeSummary");
            TileId tileId = new(4, 8, 8);
            byte[] tile = new MapboxVectorTileBuilder()
                .AddPoint(
                    "markers",
                    2048,
                    2048,
                    new Dictionary<string, object> { ["label"] = "7" })
                .Build();
            TestVectorTileSource source = CreateShieldSource(
                tileId,
                tile,
                """
                {
                  "version": 8,
                  "layers": [{
                    "type": "symbol",
                    "source-layer": "markers",
                    "layout": {
                      "text-field": ["get", "label"],
                      "text-font": ["TestFont"],
                      "text-size": 18
                    },
                    "paint": {
                      "text-color": "#ff00ff"
                    }
                  }]
                }
                """,
                '7');

            BasicGeoposition center = source.TileCenter;
            map.MapStyle = MapStyle.Blank;
            map.Center = new Geopoint(center);
            map.ZoomLevel = source.TileId.Zoom;
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(
                map,
                center,
                source.TileId.Zoom);
            map.Layers.Add(new TestVectorTileLayer(
                source,
                TimeSpan.FromSeconds(1)));
            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(5));
            MapRenderFrame frame =
                await map.CaptureRenderedFrameAsync(timeout.Token);

            Assert.ContainsSingle(
                FindColor(frame, 255, 0, 255, minimumPixelCount: 20));
            CapturedRenderingEvent[] fadeEvents =
                listener.Events("VectorLabelFadeSummary");
            Assert.IsTrue(fadeEvents.Any(captured =>
                Convert.ToInt32(captured.Payload[1]) > 0 &&
                Convert.ToInt32(captured.Payload[2]) > 0));
            Assert.IsTrue(fadeEvents.Any(captured =>
                Convert.ToInt32(captured.Payload[1]) == 0 &&
                Convert.ToInt32(captured.Payload[2]) == 0));
        });

    private static TestVectorTileSource CreateShieldSource(
        TileId tileId,
        byte[] tile,
        string styleJson,
        params char[] glyphs)
    {
        byte[] shieldPixels = Enumerable.Range(0, 24 * 16)
            .SelectMany(_ => new byte[] { 0, 0, 255, 255 })
            .ToArray();
        TestVectorTileSource source = TestVectorTileSource.Create(
            tileId,
            tile,
            styleJson,
            """
            {
              "shield": {
                "x": 0, "y": 0, "width": 24, "height": 16,
                "pixelRatio": 1, "visible": true
              }
            }
            """,
            shieldPixels,
            24,
            16);
        source.AddGlyphs(
            "TestFont",
            [.. glyphs.Distinct().Select(character =>
                TestGlyph.Solid(character))]);
        return source;
    }

    private static async Task<MapRenderFrame> RenderAsync(
        MapControl map,
        TestVectorTileSource source,
        TimeSpan? fadeDuration = null)
    {
        BasicGeoposition center = source.TileCenter;
        map.MapStyle = MapStyle.Blank;
        map.Center = new Geopoint(center);
        map.ZoomLevel = source.TileId.Zoom;
        map.Layers.Add(new TestVectorTileLayer(source, fadeDuration));

        await MapControlTestUtilities.WaitForDisplayedCameraAsync(
            map,
            center,
            source.TileId.Zoom);
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(5));
        return await map.CaptureRenderedFrameAsync(timeout.Token);
    }

    private static ConnectedComponent[] FindColor(
        MapRenderFrame frame,
        byte red,
        byte green,
        byte blue,
        int minimumPixelCount,
        byte tolerance = 8) =>
        ConnectedComponentAnalyzer.Find(
            frame,
            ConnectedComponentAnalyzer.Near(
                red,
                green,
                blue,
                tolerance,
                minimumAlpha: 240),
            minimumPixelCount);

    private static PixelBounds Union(
        IReadOnlyCollection<ConnectedComponent> components)
    {
        Assert.IsNotEmpty(components);
        int left = components.Min(component => component.Bounds.Left);
        int top = components.Min(component => component.Bounds.Top);
        int right = components.Max(component => component.Bounds.Right);
        int bottom = components.Max(component => component.Bounds.Bottom);
        return new PixelBounds(left, top, right - left, bottom - top);
    }
}
