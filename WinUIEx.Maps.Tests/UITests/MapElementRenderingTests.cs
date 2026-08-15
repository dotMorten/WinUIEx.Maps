using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using Windows.UI;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests.UITests;

[TestClass]
[DoNotParallelize]
public sealed class MapElementRenderingTests
{
    private const byte ColorTolerance = 8;
    private const byte MinimumAlpha = 240;
    private const int MinimumComponentPixelCount = 8;

    public TestContext TestContext { get; set; } = null!;

    private static MapElementsLayer ConfigureBlankMap(
        MapControl map,
        BasicGeoposition center,
        double zoomLevel = 5)
    {
        map.Width = 640;
        map.Height = 480;
        map.MapStyle = MapStyle.Blank;
        map.Center = new Geopoint(center);
        map.ZoomLevel = zoomLevel;

        var layer = new MapElementsLayer
        {
            IsVisible = true,
            Opacity = 1,
        };
        map.Layers.Add(layer);
        return layer;
    }

    private async Task<MapRenderFrame> CaptureAndSaveAsync(
        MapControl map,
        CancellationToken cancellationToken,
        string phase)
    {
        MapRenderFrame frame =
            await map.CaptureRenderedFrameAsync(cancellationToken);
        await SaveFrameAsync(frame, phase);
        return frame;
    }

    private async Task<MapRenderFrame> CaptureUntilAndSaveAsync(
        MapControl map,
        CancellationToken cancellationToken,
        string phase,
        Func<MapRenderFrame, bool> expected)
    {
        ArgumentNullException.ThrowIfNull(expected);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MapRenderFrame frame =
                    await map.CaptureRenderedFrameAsync(cancellationToken);
                if (expected(frame))
                {
                    await SaveFrameAsync(frame, phase);
                    return frame;
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            Assert.Fail(
                $"The expected rendered state for phase '{phase}' was not reached before the timeout.");
            return null!;
        }
    }

    private async Task SaveFrameAsync(MapRenderFrame frame, string phase)
    {
        TestContext.Properties.TryGetValue(
            "TestRunResultsDirectory",
            out object? configuredResultsDirectory);
        string resultsDirectory =
            configuredResultsDirectory as string ??
            Path.Combine(AppContext.BaseDirectory, "TestResults");
        string path = Path.Combine(
            resultsDirectory,
            $"{TestContext.TestName}-{phase}.png");

        string savedPath = await frame.SavePngAsync(path);
        TestContext.AddResultFile(savedPath);

        Assert.IsTrue(File.Exists(savedPath), $"Render evidence was not saved to '{savedPath}'.");
        Assert.IsGreaterThan(0, new FileInfo(savedPath).Length);
    }

    private static ConnectedComponent[] FindColor(
        MapRenderFrame frame,
        Color color,
        int minimumPixelCount = MinimumComponentPixelCount,
        byte tolerance = ColorTolerance,
        byte minimumAlpha = MinimumAlpha) =>
        ConnectedComponentAnalyzer.Find(
            frame,
            ConnectedComponentAnalyzer.Near(
                color.R,
                color.G,
                color.B,
                tolerance,
                minimumAlpha),
            minimumPixelCount);

    private static ConnectedComponent AssertSingleColorComponent(
        MapRenderFrame frame,
        Color color,
        int minimumPixelCount = MinimumComponentPixelCount)
    {
        ConnectedComponent component =
            Assert.ContainsSingle(FindColor(frame, color, minimumPixelCount));
        Assert.IsGreaterThanOrEqualTo(minimumPixelCount, component.PixelCount);
        Assert.IsGreaterThan(0, component.Bounds.Width);
        Assert.IsGreaterThan(0, component.Bounds.Height);
        return component;
    }

    private static void AssertColorAbsent(
        MapRenderFrame frame,
        Color color,
        int minimumPixelCount = MinimumComponentPixelCount) =>
        Assert.IsEmpty(
            FindColor(frame, color, minimumPixelCount),
            $"Unexpected stale {color} component remained in the rendered frame.");

    private static void AssertColorRemoved(
        MapRenderFrame before,
        MapRenderFrame after,
        Color color,
        int minimumPixelCount = MinimumComponentPixelCount)
    {
        Assert.IsNotEmpty(
            FindColor(before, color, minimumPixelCount),
            "The before-frame must prove the color was rendered before absence is asserted.");
        AssertColorAbsent(after, color, minimumPixelCount);
    }

    private static PixelBounds GetUnionBounds(
        IReadOnlyCollection<ConnectedComponent> components)
    {
        Assert.IsNotEmpty(components);
        int left = components.Min(component => component.Bounds.Left);
        int top = components.Min(component => component.Bounds.Top);
        int right = components.Max(component => component.Bounds.Right);
        int bottom = components.Max(component => component.Bounds.Bottom);
        return new PixelBounds(left, top, right - left, bottom - top);
    }

    private static bool HasColorNear(
        MapRenderFrame frame,
        Color color,
        Point point,
        int radius = 3)
    {
        int left = Math.Max(0, (int)Math.Floor(point.X) - radius);
        int top = Math.Max(0, (int)Math.Floor(point.Y) - radius);
        int right = Math.Min(frame.Width - 1, (int)Math.Ceiling(point.X) + radius);
        int bottom = Math.Min(frame.Height - 1, (int)Math.Ceiling(point.Y) + radius);
        PixelColorFilter near = ConnectedComponentAnalyzer.Near(
            color.R,
            color.G,
            color.B,
            ColorTolerance,
            MinimumAlpha);
        ReadOnlySpan<byte> pixels = frame.Pixels.Span;

        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                int offset = ((y * frame.Width) + x) * 4;
                if (near(
                    pixels[offset + 2],
                    pixels[offset + 1],
                    pixels[offset],
                    pixels[offset + 3]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static Geopath CreatePath(
        params (double Longitude, double Latitude)[] points) =>
        new(points.Select(point => new BasicGeoposition
        {
            Longitude = point.Longitude,
            Latitude = point.Latitude,
        }));

    private static Geopath CreateRectanglePath(
        BasicGeoposition center,
        double longitudeRadius,
        double latitudeRadius) =>
        CreatePath(
            (center.Longitude - longitudeRadius, center.Latitude - latitudeRadius),
            (center.Longitude + longitudeRadius, center.Latitude - latitudeRadius),
            (center.Longitude + longitudeRadius, center.Latitude + latitudeRadius),
            (center.Longitude - longitudeRadius, center.Latitude + latitudeRadius),
            (center.Longitude - longitudeRadius, center.Latitude - latitudeRadius));

    private static PathIcon CreateAsymmetricPinIcon(Color color) =>
        new()
        {
            Width = 24,
            Height = 32,
            Foreground = new SolidColorBrush(color),
            Data = new GeometryGroup
            {
                Children =
                {
                    new EllipseGeometry
                    {
                        Center = new Point(8, 8),
                        RadiusX = 7,
                        RadiusY = 7,
                    },
                    new RectangleGeometry
                    {
                        Rect = new Rect(6, 14, 4, 17),
                    },
                },
            },
        };

    private static PathIcon CreateAsymmetricFlagIcon(Color color) =>
        new()
        {
            Width = 32,
            Height = 24,
            Foreground = new SolidColorBrush(color),
            Data = new GeometryGroup
            {
                Children =
                {
                    new RectangleGeometry
                    {
                        Rect = new Rect(2, 2, 4, 21),
                    },
                    new RectangleGeometry
                    {
                        Rect = new Rect(6, 3, 24, 11),
                    },
                },
            },
        };

    [TestMethod]
    public Task MapPolygon_PathRendersAndUpdatesProjectedFillBounds() =>
        MapControlTestHost.LoadMapControlAsync(new BasicGeoposition(), 5, async map =>
        {
            BasicGeoposition center = new() { Longitude = 0, Latitude = 0 };
            MapElementsLayer layer = ConfigureBlankMap(map, center);
            Color fill = Color.FromArgb(255, 32, 224, 160);
            var polygon = new MapPolygon
            {
                Path = CreateViewportRectanglePath(center, 5, 100, 140, 260, 260),
                FillColor = fill,
                StrokeColor = Microsoft.UI.Colors.Transparent,
            };
            layer.MapElements.Add(polygon);

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, center, 5);
            MapRenderFrame before = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "path-before",
                frame => HasColorNear(frame, fill, new Point(180, 200), 2));
            ConnectedComponent beforeFill =
                AssertSingleColorComponent(before, fill, 10_000);
            Assert.AreEqual(100, beforeFill.Bounds.Left, 4);
            Assert.AreEqual(140, beforeFill.Bounds.Top, 4);
            Assert.AreEqual(160, beforeFill.Bounds.Width, 5);
            Assert.AreEqual(120, beforeFill.Bounds.Height, 5);
            Assert.AreEqual(180, beforeFill.Bounds.CenterX, 4);
            Assert.AreEqual(200, beforeFill.Bounds.CenterY, 4);
            Assert.IsTrue(HasColorNear(before, fill, new Point(180, 200), 1));

            polygon.Path =
                CreateViewportRectanglePath(center, 5, 380, 100, 500, 340);

            MapRenderFrame after = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "path-after",
                frame =>
                    HasColorNear(frame, fill, new Point(440, 220), 2) &&
                    !HasColorNear(frame, fill, new Point(180, 200), 2));
            ConnectedComponent afterFill =
                AssertSingleColorComponent(after, fill, 20_000);
            Assert.AreEqual(380, afterFill.Bounds.Left, 4);
            Assert.AreEqual(100, afterFill.Bounds.Top, 4);
            Assert.AreEqual(120, afterFill.Bounds.Width, 5);
            Assert.AreEqual(240, afterFill.Bounds.Height, 5);
            Assert.IsGreaterThan(beforeFill.Bounds.CenterX + 200, afterFill.Bounds.CenterX);
            Assert.IsGreaterThan(afterFill.Bounds.Width, afterFill.Bounds.Height);
            Assert.IsFalse(HasColorNear(after, fill, new Point(180, 200), 2));
        });

    [TestMethod]
    public Task MapPolygon_PathsRendersHoleAndUpdatesContours() =>
        MapControlTestHost.LoadMapControlAsync(new BasicGeoposition(), 5, async map =>
        {
            BasicGeoposition center = new() { Longitude = 0, Latitude = 0 };
            MapElementsLayer layer = ConfigureBlankMap(map, center);
            Color fill = Color.FromArgb(255, 248, 120, 32);
            var polygon = new MapPolygon
            {
                FillColor = fill,
                StrokeColor = Microsoft.UI.Colors.Transparent,
            };
            polygon.Paths.Add(
                CreateViewportRectanglePath(center, 5, 160, 100, 480, 380));
            polygon.Paths.Add(
                CreateViewportRectanglePath(center, 5, 260, 180, 380, 300));
            layer.MapElements.Add(polygon);

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, center, 5);
            MapRenderFrame withHole = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "paths-with-hole",
                frame =>
                    HasColorNear(frame, fill, new Point(200, 140), 2) &&
                    !HasColorNear(frame, fill, new Point(320, 240), 3));
            ConnectedComponent ring =
                AssertSingleColorComponent(withHole, fill, 50_000);
            Assert.AreEqual(160, ring.Bounds.Left, 4);
            Assert.AreEqual(100, ring.Bounds.Top, 4);
            Assert.AreEqual(320, ring.Bounds.Width, 5);
            Assert.AreEqual(280, ring.Bounds.Height, 5);
            Assert.IsFalse(HasColorNear(withHole, fill, new Point(320, 240), 8));

            polygon.Paths.RemoveAt(1);

            MapRenderFrame updated = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "paths-updated",
                frame => HasColorNear(frame, fill, new Point(320, 240), 2));
            ConnectedComponent solid =
                AssertSingleColorComponent(updated, fill, 75_000);
            Assert.IsTrue(HasColorNear(updated, fill, new Point(320, 240), 2));
            Assert.IsGreaterThan(ring.PixelCount + 10_000, solid.PixelCount);
            Assert.AreEqual(ring.Bounds.Left, solid.Bounds.Left, 2);
            Assert.AreEqual(ring.Bounds.Top, solid.Bounds.Top, 2);
            Assert.AreEqual(ring.Bounds.Width, solid.Bounds.Width, 2);
            Assert.AreEqual(ring.Bounds.Height, solid.Bounds.Height, 2);
        });

    [TestMethod]
    public Task MapPolygon_FillColorUpdatesInteriorWithoutStalePixels() =>
        MapControlTestHost.LoadMapControlAsync(new BasicGeoposition(), 5, async map =>
        {
            BasicGeoposition center = new() { Longitude = 0, Latitude = 0 };
            MapElementsLayer layer = ConfigureBlankMap(map, center);
            Color initial = Color.FromArgb(255, 48, 104, 240);
            Color replacement = Color.FromArgb(255, 224, 48, 176);
            var polygon = new MapPolygon
            {
                Path = CreateViewportRectanglePath(center, 5, 180, 120, 460, 360),
                FillColor = initial,
                StrokeColor = Microsoft.UI.Colors.Transparent,
            };
            layer.MapElements.Add(polygon);

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, center, 5);
            MapRenderFrame before = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "fill-before",
                frame => HasColorNear(frame, initial, new Point(320, 240), 2));
            ConnectedComponent initialFill =
                AssertSingleColorComponent(before, initial, 60_000);
            Assert.IsTrue(HasColorNear(before, initial, new Point(320, 240), 1));

            polygon.FillColor = replacement;

            MapRenderFrame after = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "fill-after",
                frame =>
                    HasColorNear(frame, replacement, new Point(320, 240), 2) &&
                    FindColor(frame, initial).Length == 0);
            ConnectedComponent replacementFill =
                AssertSingleColorComponent(after, replacement, 60_000);
            AssertColorRemoved(before, after, initial);
            Assert.IsTrue(HasColorNear(after, replacement, new Point(320, 240), 1));
            Assert.AreEqual(initialFill.Bounds.Left, replacementFill.Bounds.Left, 1);
            Assert.AreEqual(initialFill.Bounds.Top, replacementFill.Bounds.Top, 1);
            Assert.AreEqual(initialFill.Bounds.Width, replacementFill.Bounds.Width, 1);
            Assert.AreEqual(initialFill.Bounds.Height, replacementFill.Bounds.Height, 1);
            Assert.AreEqual(initialFill.PixelCount, replacementFill.PixelCount, 32);
        });

    [TestMethod]
    public Task MapPolygon_StrokeColorOutlinesFillAndUpdatesWithoutStalePixels() =>
        MapControlTestHost.LoadMapControlAsync(new BasicGeoposition(), 5, async map =>
        {
            BasicGeoposition center = new() { Longitude = 0, Latitude = 0 };
            MapElementsLayer layer = ConfigureBlankMap(map, center);
            Color fill = Color.FromArgb(255, 48, 216, 216);
            Color initialStroke = Color.FromArgb(255, 240, 40, 72);
            Color replacementStroke = Color.FromArgb(255, 144, 64, 240);
            var polygon = new MapPolygon
            {
                Path = CreateViewportRectanglePath(center, 5, 180, 120, 460, 360),
                FillColor = fill,
                StrokeColor = initialStroke,
                StrokeThickness = 12,
            };
            layer.MapElements.Add(polygon);

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, center, 5);
            MapRenderFrame before = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "stroke-color-before",
                frame =>
                    HasColorNear(frame, fill, new Point(320, 240), 2) &&
                    HasColorNear(frame, initialStroke, new Point(180, 240), 3));
            ConnectedComponent initialBorder =
                AssertSingleColorComponent(before, initialStroke, 8_000);
            ConnectedComponent initialFill =
                AssertSingleColorComponent(before, fill, 50_000);
            Assert.IsTrue(initialBorder.Bounds.Contains(initialFill.Bounds));
            Assert.IsLessThan(initialFill.Bounds.Left, initialBorder.Bounds.Left);
            Assert.IsLessThan(initialFill.Bounds.Top, initialBorder.Bounds.Top);
            Assert.IsGreaterThan(initialFill.Bounds.Right, initialBorder.Bounds.Right);
            Assert.IsGreaterThan(initialFill.Bounds.Bottom, initialBorder.Bounds.Bottom);
            Assert.IsTrue(HasColorNear(before, fill, new Point(320, 240), 1));

            polygon.StrokeColor = replacementStroke;

            MapRenderFrame after = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "stroke-color-after",
                frame =>
                    HasColorNear(frame, replacementStroke, new Point(180, 240), 3) &&
                    FindColor(frame, initialStroke).Length == 0);
            ConnectedComponent replacementBorder =
                AssertSingleColorComponent(after, replacementStroke, 8_000);
            ConnectedComponent replacementFill =
                AssertSingleColorComponent(after, fill, 50_000);
            AssertColorRemoved(before, after, initialStroke);
            Assert.IsTrue(replacementBorder.Bounds.Contains(replacementFill.Bounds));
            Assert.AreEqual(initialBorder.Bounds.Left, replacementBorder.Bounds.Left, 1);
            Assert.AreEqual(initialBorder.Bounds.Top, replacementBorder.Bounds.Top, 1);
            Assert.AreEqual(initialBorder.Bounds.Width, replacementBorder.Bounds.Width, 1);
            Assert.AreEqual(initialBorder.Bounds.Height, replacementBorder.Bounds.Height, 1);
            Assert.IsTrue(HasColorNear(after, fill, new Point(320, 240), 1));
        });

    [TestMethod]
    public Task MapPolygon_StrokeDashedCreatesSeparatedBorderComponents() =>
        MapControlTestHost.LoadMapControlAsync(new BasicGeoposition(), 5, async map =>
        {
            BasicGeoposition center = new() { Longitude = 0, Latitude = 0 };
            MapElementsLayer layer = ConfigureBlankMap(map, center);
            Color stroke = Color.FromArgb(255, 248, 208, 24);
            var polygon = new MapPolygon
            {
                Path = CreateViewportRectanglePath(center, 5, 140, 100, 500, 380),
                FillColor = Microsoft.UI.Colors.Transparent,
                StrokeColor = stroke,
                StrokeThickness = 4,
            };
            layer.MapElements.Add(polygon);

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, center, 5);
            MapRenderFrame solidFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "stroke-solid",
                frame => FindColor(frame, stroke, 500).Length == 1);
            ConnectedComponent solid =
                AssertSingleColorComponent(solidFrame, stroke, 500);
            Assert.IsGreaterThan(1_000, solid.PixelCount);
            Assert.AreEqual(360, solid.Bounds.Width, 6);
            Assert.AreEqual(280, solid.Bounds.Height, 6);

            polygon.StrokeDashed = true;

            MapRenderFrame dashedFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "stroke-dashed",
                frame => FindColor(frame, stroke, 8).Length >= 20);
            ConnectedComponent[] dashes = FindColor(dashedFrame, stroke, 8);
            PixelBounds dashedBounds = GetUnionBounds(dashes);
            Assert.IsGreaterThanOrEqualTo(20, dashes.Length);
            Assert.IsTrue(dashes.All(component => component.PixelCount < solid.PixelCount / 4));
            Assert.AreEqual(solid.Bounds.Left, dashedBounds.Left, 12);
            Assert.AreEqual(solid.Bounds.Top, dashedBounds.Top, 12);
            Assert.AreEqual(solid.Bounds.Right, dashedBounds.Right, 12);
            Assert.AreEqual(solid.Bounds.Bottom, dashedBounds.Bottom, 12);
            Assert.IsFalse(HasColorNear(dashedFrame, stroke, new Point(155, 100), 0));
        });

    [TestMethod]
    public Task MapPolygon_StrokeThicknessIncreasesBorderPixelCount() =>
        MapControlTestHost.LoadMapControlAsync(new BasicGeoposition(), 5, async map =>
        {
            BasicGeoposition center = new() { Longitude = 0, Latitude = 0 };
            MapElementsLayer layer = ConfigureBlankMap(map, center);
            Color fill = Color.FromArgb(255, 80, 200, 96);
            Color stroke = Color.FromArgb(255, 232, 56, 48);
            var polygon = new MapPolygon
            {
                Path = CreateViewportRectanglePath(center, 5, 180, 120, 460, 360),
                FillColor = fill,
                StrokeColor = stroke,
                StrokeThickness = 2,
            };
            layer.MapElements.Add(polygon);

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, center, 5);
            MapRenderFrame thinFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "stroke-thin",
                frame =>
                    HasColorNear(frame, fill, new Point(320, 240), 2) &&
                    FindColor(frame, stroke, 100).Length == 1);
            ConnectedComponent thinStroke =
                AssertSingleColorComponent(thinFrame, stroke, 100);
            ConnectedComponent thinFill =
                AssertSingleColorComponent(thinFrame, fill, 50_000);

            polygon.StrokeThickness = 14;

            MapRenderFrame thickFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "stroke-thick",
                frame =>
                    HasColorNear(frame, fill, new Point(320, 240), 2) &&
                    FindColor(frame, stroke, 5_000).Length == 1);
            ConnectedComponent thickStroke =
                AssertSingleColorComponent(thickFrame, stroke, 5_000);
            ConnectedComponent thickFill =
                AssertSingleColorComponent(thickFrame, fill, 45_000);
            Assert.IsGreaterThan(thinStroke.PixelCount * 4, thickStroke.PixelCount);
            Assert.IsGreaterThanOrEqualTo(
                thinStroke.Bounds.Width + 10,
                thickStroke.Bounds.Width);
            Assert.IsGreaterThanOrEqualTo(
                thinStroke.Bounds.Height + 10,
                thickStroke.Bounds.Height);
            Assert.AreEqual(thinStroke.Bounds.CenterX, thickStroke.Bounds.CenterX, 2);
            Assert.AreEqual(thinStroke.Bounds.CenterY, thickStroke.Bounds.CenterY, 2);
            Assert.AreEqual(thinFill.Bounds.CenterX, thickFill.Bounds.CenterX, 2);
            Assert.AreEqual(thinFill.Bounds.CenterY, thickFill.Bounds.CenterY, 2);
            Assert.IsTrue(HasColorNear(thickFrame, fill, new Point(320, 240), 1));
        });

    private static Geopath CreateViewportRectanglePath(
        BasicGeoposition center,
        double zoom,
        double left,
        double top,
        double right,
        double bottom) =>
        CreatePath(
            ToCoordinate(MapControlTestUtilities.LocationAtOffset(
                center,
                zoom,
                640,
                480,
                new Point(left, top))),
            ToCoordinate(MapControlTestUtilities.LocationAtOffset(
                center,
                zoom,
                640,
                480,
                new Point(right, top))),
            ToCoordinate(MapControlTestUtilities.LocationAtOffset(
                center,
                zoom,
                640,
                480,
                new Point(right, bottom))),
            ToCoordinate(MapControlTestUtilities.LocationAtOffset(
                center,
                zoom,
                640,
                480,
                new Point(left, bottom))),
            ToCoordinate(MapControlTestUtilities.LocationAtOffset(
                center,
                zoom,
                640,
                480,
                new Point(left, top))));

    private static (double Longitude, double Latitude) ToCoordinate(
        BasicGeoposition position) =>
        (position.Longitude, position.Latitude);

    private static Geopath CreateViewportPath(
        BasicGeoposition center,
        double zoom,
        params Point[] points) =>
        CreatePath(
            [.. points.Select(point => ToCoordinate(
                MapControlTestUtilities.LocationAtOffset(
                    center,
                    zoom,
                    640,
                    480,
                    point)))]);

    [TestMethod]
    public Task MapPolyline_PathUpdatesOrientationBoundsAndProjectedPlacement() =>
        MapControlTestHost.LoadMapControlAsync(new BasicGeoposition(), 5, async map =>
        {
            BasicGeoposition center = new() { Longitude = 0, Latitude = 0 };
            MapElementsLayer layer = ConfigureBlankMap(map, center);
            Color stroke = Color.FromArgb(255, 24, 208, 232);
            var polyline = new MapPolyline
            {
                Path = CreateViewportPath(
                    center,
                    5,
                    new Point(120, 150),
                    new Point(420, 150)),
                StrokeColor = stroke,
                StrokeThickness = 10,
            };
            layer.MapElements.Add(polyline);

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, center, 5);
            MapRenderFrame horizontalFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "path-horizontal",
                frame =>
                    HasColorNear(frame, stroke, new Point(120, 150), 3) &&
                    HasColorNear(frame, stroke, new Point(420, 150), 3));
            ConnectedComponent horizontal =
                AssertSingleColorComponent(horizontalFrame, stroke, 2_500);
            Assert.AreEqual(120, horizontal.Bounds.Left, 4);
            Assert.AreEqual(150, horizontal.Bounds.CenterY, 3);
            Assert.AreEqual(300, horizontal.Bounds.Width, 6);
            Assert.AreEqual(10, horizontal.Bounds.Height, 3);
            Assert.IsGreaterThan(horizontal.Bounds.Height * 20, horizontal.Bounds.Width);
            Assert.IsTrue(HasColorNear(horizontalFrame, stroke, new Point(270, 150), 1));

            polyline.Path = CreateViewportPath(
                center,
                5,
                new Point(500, 100),
                new Point(500, 380));

            MapRenderFrame verticalFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "path-vertical",
                frame =>
                    HasColorNear(frame, stroke, new Point(500, 100), 3) &&
                    HasColorNear(frame, stroke, new Point(500, 380), 3) &&
                    !HasColorNear(frame, stroke, new Point(270, 150), 2));
            ConnectedComponent vertical =
                AssertSingleColorComponent(verticalFrame, stroke, 2_300);
            Assert.AreEqual(500, vertical.Bounds.CenterX, 3);
            Assert.AreEqual(100, vertical.Bounds.Top, 4);
            Assert.AreEqual(10, vertical.Bounds.Width, 3);
            Assert.AreEqual(280, vertical.Bounds.Height, 6);
            Assert.IsGreaterThan(vertical.Bounds.Width * 20, vertical.Bounds.Height);
            Assert.IsGreaterThan(horizontal.Bounds.CenterX + 200, vertical.Bounds.CenterX);
            Assert.AreEqual(horizontal.Bounds.Height, vertical.Bounds.Width, 2);
            Assert.AreEqual(horizontal.PixelCount, vertical.PixelCount, 300);
            Assert.IsFalse(HasColorNear(verticalFrame, stroke, new Point(270, 150), 2));
        });

    [TestMethod]
    public Task MapPolyline_StrokeColorUpdatesLineWithoutStalePixels() =>
        MapControlTestHost.LoadMapControlAsync(new BasicGeoposition(), 5, async map =>
        {
            BasicGeoposition center = new() { Longitude = 0, Latitude = 0 };
            MapElementsLayer layer = ConfigureBlankMap(map, center);
            Color initial = Color.FromArgb(255, 248, 72, 48);
            Color replacement = Color.FromArgb(255, 104, 80, 248);
            var polyline = new MapPolyline
            {
                Path = CreateViewportPath(
                    center,
                    5,
                    new Point(140, 220),
                    new Point(500, 220)),
                StrokeColor = initial,
                StrokeThickness = 8,
            };
            layer.MapElements.Add(polyline);

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, center, 5);
            MapRenderFrame before = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "stroke-color-before",
                frame =>
                    HasColorNear(frame, initial, new Point(140, 220), 3) &&
                    HasColorNear(frame, initial, new Point(500, 220), 3));
            ConnectedComponent initialLine =
                AssertSingleColorComponent(before, initial, 2_500);
            Assert.IsTrue(HasColorNear(before, initial, new Point(320, 220), 1));

            polyline.StrokeColor = replacement;

            MapRenderFrame after = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "stroke-color-after",
                frame =>
                    HasColorNear(frame, replacement, new Point(320, 220), 1) &&
                    FindColor(frame, initial).Length == 0);
            ConnectedComponent replacementLine =
                AssertSingleColorComponent(after, replacement, 2_500);
            AssertColorRemoved(before, after, initial);
            Assert.IsTrue(HasColorNear(after, replacement, new Point(140, 220), 3));
            Assert.IsTrue(HasColorNear(after, replacement, new Point(500, 220), 3));
            Assert.AreEqual(initialLine.Bounds.Left, replacementLine.Bounds.Left, 1);
            Assert.AreEqual(initialLine.Bounds.Top, replacementLine.Bounds.Top, 1);
            Assert.AreEqual(initialLine.Bounds.Width, replacementLine.Bounds.Width, 1);
            Assert.AreEqual(initialLine.Bounds.Height, replacementLine.Bounds.Height, 1);
            Assert.AreEqual(initialLine.PixelCount, replacementLine.PixelCount, 32);
        });

    [TestMethod]
    public Task MapPolyline_StrokeDashedCreatesSeparatedSegments() =>
        MapControlTestHost.LoadMapControlAsync(new BasicGeoposition(), 5, async map =>
        {
            BasicGeoposition center = new() { Longitude = 0, Latitude = 0 };
            MapElementsLayer layer = ConfigureBlankMap(map, center);
            Color stroke = Color.FromArgb(255, 248, 200, 32);
            var polyline = new MapPolyline
            {
                Path = CreateViewportPath(
                    center,
                    5,
                    new Point(100, 240),
                    new Point(540, 240)),
                StrokeColor = stroke,
                StrokeThickness = 4,
            };
            layer.MapElements.Add(polyline);

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, center, 5);
            MapRenderFrame solidFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "stroke-solid",
                frame => FindColor(frame, stroke, 1_500).Length == 1);
            ConnectedComponent solid =
                AssertSingleColorComponent(solidFrame, stroke, 1_500);
            Assert.AreEqual(440, solid.Bounds.Width, 6);
            Assert.AreEqual(4, solid.Bounds.Height, 3);
            Assert.IsTrue(HasColorNear(solidFrame, stroke, new Point(116, 240), 0));

            polyline.StrokeDashed = true;

            MapRenderFrame dashedFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "stroke-dashed",
                frame => FindColor(frame, stroke, 20).Length >= 20);
            ConnectedComponent[] dashes = FindColor(dashedFrame, stroke, 20);
            ConnectedComponent[] ordered =
                [.. dashes.OrderBy(component => component.Bounds.Left)];
            PixelBounds dashedBounds = GetUnionBounds(dashes);
            Assert.IsGreaterThanOrEqualTo(20, dashes.Length);
            Assert.IsTrue(
                ordered.Zip(
                    ordered.Skip(1),
                    (first, second) => first.Bounds.Right < second.Bounds.Left)
                    .All(separated => separated));
            Assert.IsTrue(dashes.All(component => component.Bounds.Width < 16));
            Assert.AreEqual(solid.Bounds.Left, dashedBounds.Left, 4);
            Assert.AreEqual(solid.Bounds.Right, dashedBounds.Right, 12);
            Assert.AreEqual(solid.Bounds.CenterY, dashedBounds.CenterY, 2);
            Assert.IsFalse(HasColorNear(dashedFrame, stroke, new Point(116, 240), 0));
        });

    [TestMethod]
    public Task MapPolyline_StrokeThicknessIncreasesPerpendicularSizeAndPixelCount() =>
        MapControlTestHost.LoadMapControlAsync(new BasicGeoposition(), 5, async map =>
        {
            BasicGeoposition center = new() { Longitude = 0, Latitude = 0 };
            MapElementsLayer layer = ConfigureBlankMap(map, center);
            Color stroke = Color.FromArgb(255, 40, 224, 112);
            var polyline = new MapPolyline
            {
                Path = CreateViewportPath(
                    center,
                    5,
                    new Point(120, 300),
                    new Point(520, 300)),
                StrokeColor = stroke,
                StrokeThickness = 2,
            };
            layer.MapElements.Add(polyline);

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, center, 5);
            MapRenderFrame thinFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "stroke-thin",
                frame => FindColor(frame, stroke, 600).Length == 1);
            ConnectedComponent thin =
                AssertSingleColorComponent(thinFrame, stroke, 600);
            Assert.IsTrue(HasColorNear(thinFrame, stroke, new Point(120, 300), 3));
            Assert.IsTrue(HasColorNear(thinFrame, stroke, new Point(520, 300), 3));

            polyline.StrokeThickness = 14;

            MapRenderFrame thickFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "stroke-thick",
                frame => FindColor(frame, stroke, 4_000).Length == 1);
            ConnectedComponent thick =
                AssertSingleColorComponent(thickFrame, stroke, 4_000);
            Assert.IsGreaterThan(thin.Bounds.Height + 10, thick.Bounds.Height);
            Assert.IsGreaterThan(thin.PixelCount * 5, thick.PixelCount);
            Assert.AreEqual(thin.Bounds.Width, thick.Bounds.Width, 2);
            Assert.AreEqual(thin.Bounds.CenterX, thick.Bounds.CenterX, 1);
            Assert.AreEqual(thin.Bounds.CenterY, thick.Bounds.CenterY, 1);
            Assert.IsTrue(HasColorNear(thickFrame, stroke, new Point(120, 300), 3));
            Assert.IsTrue(HasColorNear(thickFrame, stroke, new Point(520, 300), 3));
        });

    [TestMethod]
    public Task MapIcon_IconElementReplacementChangesColorAndBounds() =>
        MapControlTestHost.LoadMapControlAsync(new BasicGeoposition(), 5, async map =>
        {
            BasicGeoposition center = new() { Longitude = 0, Latitude = 0 };
            MapElementsLayer layer = ConfigureBlankMap(map, center);
            Color initialColor = Color.FromArgb(255, 168, 48, 232);
            Color replacementColor = Color.FromArgb(255, 32, 216, 120);
            var icon = new MapIcon(
                CreateAsymmetricFlagIcon(initialColor),
                new Geopoint(center));
            layer.MapElements.Add(icon);
            Point projectedLocation = new(320, 240);

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, center, 5);
            MapRenderFrame before = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "icon-element-before",
                frame => FindColor(frame, initialColor, 100).Length == 1);
            ConnectedComponent initial =
                AssertSingleColorComponent(before, initialColor, 100);
            Assert.IsGreaterThanOrEqualTo(
                initial.Bounds.Left,
                projectedLocation.X);
            Assert.IsLessThanOrEqualTo(
                initial.Bounds.Right,
                projectedLocation.X);
            Assert.IsGreaterThanOrEqualTo(
                initial.Bounds.Top,
                projectedLocation.Y);
            Assert.IsLessThanOrEqualTo(
                initial.Bounds.Bottom,
                projectedLocation.Y);
            Assert.AreEqual(projectedLocation.X, initial.Bounds.CenterX, 3);
            Assert.AreEqual(projectedLocation.Y, initial.Bounds.CenterY, 3);
            Assert.IsGreaterThan(initial.Bounds.Height, initial.Bounds.Width);

            icon.IconElement = CreateCenteredRectangleIcon(replacementColor);

            MapRenderFrame after = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "icon-element-after",
                frame =>
                    FindColor(frame, replacementColor, 200).Length == 1 &&
                    FindColor(frame, initialColor).Length == 0);
            ConnectedComponent replacement =
                AssertSingleColorComponent(after, replacementColor, 200);
            AssertColorRemoved(before, after, initialColor);
            Assert.IsGreaterThanOrEqualTo(
                replacement.Bounds.Left,
                projectedLocation.X);
            Assert.IsLessThanOrEqualTo(
                replacement.Bounds.Right,
                projectedLocation.X);
            Assert.IsGreaterThanOrEqualTo(
                replacement.Bounds.Top,
                projectedLocation.Y);
            Assert.IsLessThanOrEqualTo(
                replacement.Bounds.Bottom,
                projectedLocation.Y);
            Assert.AreEqual(projectedLocation.X, replacement.Bounds.CenterX, 3);
            Assert.AreEqual(projectedLocation.Y, replacement.Bounds.CenterY, 3);
            Assert.IsGreaterThan(replacement.Bounds.Width + 4, initial.Bounds.Width);
            Assert.IsGreaterThan(replacement.Bounds.Height + 2, initial.Bounds.Height);
            Assert.IsGreaterThan(initial.PixelCount + 30, replacement.PixelCount);
        });

    [TestMethod]
    public Task MapIcon_LocationMovesComponentToProjectedViewportPosition() =>
        MapControlTestHost.LoadMapControlAsync(new BasicGeoposition(), 5, async map =>
        {
            BasicGeoposition center = new() { Longitude = 0, Latitude = 0 };
            MapElementsLayer layer = ConfigureBlankMap(map, center);
            Color color = Color.FromArgb(255, 240, 88, 40);
            Point initialProjectedLocation = new(180, 160);
            Point movedProjectedLocation = new(470, 330);
            BasicGeoposition initialLocation =
                MapControlTestUtilities.LocationAtOffset(
                    center,
                    5,
                    640,
                    480,
                    initialProjectedLocation);
            BasicGeoposition movedLocation =
                MapControlTestUtilities.LocationAtOffset(
                    center,
                    5,
                    640,
                    480,
                    movedProjectedLocation);
            var icon = new MapIcon(
                CreateCenteredRectangleIcon(color),
                new Geopoint(initialLocation));
            layer.MapElements.Add(icon);

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, center, 5);
            MapRenderFrame before = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "location-before",
                frame => HasColorNear(frame, color, initialProjectedLocation, 2));
            ConnectedComponent initial =
                AssertSingleColorComponent(before, color, 300);
            Assert.AreEqual(
                initialProjectedLocation.X,
                initial.Bounds.CenterX,
                2);
            Assert.AreEqual(
                initialProjectedLocation.Y,
                initial.Bounds.CenterY,
                2);
            Assert.IsTrue(
                HasColorNear(before, color, initialProjectedLocation, 1));

            icon.Location = new Geopoint(movedLocation);

            MapRenderFrame after = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "location-after",
                frame =>
                    HasColorNear(frame, color, movedProjectedLocation, 2) &&
                    !HasColorNear(frame, color, initialProjectedLocation, 12));
            ConnectedComponent moved =
                AssertSingleColorComponent(after, color, 300);
            Assert.AreEqual(
                movedProjectedLocation.X,
                moved.Bounds.CenterX,
                2);
            Assert.AreEqual(
                movedProjectedLocation.Y,
                moved.Bounds.CenterY,
                2);
            Assert.AreEqual(
                movedProjectedLocation.X - initialProjectedLocation.X,
                moved.Bounds.CenterX - initial.Bounds.CenterX,
                3);
            Assert.AreEqual(
                movedProjectedLocation.Y - initialProjectedLocation.Y,
                moved.Bounds.CenterY - initial.Bounds.CenterY,
                3);
            Assert.IsGreaterThan(initial.Bounds.CenterX + 250, moved.Bounds.CenterX);
            Assert.IsGreaterThan(initial.Bounds.CenterY + 140, moved.Bounds.CenterY);
            Assert.AreEqual(initial.Bounds.Width, moved.Bounds.Width, 1);
            Assert.AreEqual(initial.Bounds.Height, moved.Bounds.Height, 1);
            Assert.AreEqual(initial.PixelCount, moved.PixelCount, 16);
            Assert.IsTrue(HasColorNear(before, color, initialProjectedLocation, 1));
            Assert.IsFalse(HasColorNear(after, color, initialProjectedLocation, 12));
        });

    [TestMethod]
    public Task MapIcon_NormalizedAnchorPointShiftsBoundsAroundProjectedLocation() =>
        MapControlTestHost.LoadMapControlAsync(new BasicGeoposition(), 5, async map =>
        {
            BasicGeoposition center = new() { Longitude = 0, Latitude = 0 };
            MapElementsLayer layer = ConfigureBlankMap(map, center);
            Color color = Color.FromArgb(255, 48, 144, 248);
            Point projectedLocation = new(350, 260);
            BasicGeoposition location = MapControlTestUtilities.LocationAtOffset(
                center,
                5,
                640,
                480,
                projectedLocation);
            var icon = new MapIcon(
                CreateAsymmetricFlagIcon(color),
                new Geopoint(location));
            layer.MapElements.Add(icon);
            Point oldOnlyPixel = new(
                projectedLocation.X - 12,
                projectedLocation.Y);

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, center, 5);
            MapRenderFrame centeredFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "anchor-center",
                frame =>
                    HasColorNear(frame, color, projectedLocation, 2) &&
                    HasColorNear(frame, color, oldOnlyPixel, 1));
            ConnectedComponent centered =
                AssertSingleColorComponent(centeredFrame, color, 200);
            Assert.IsGreaterThanOrEqualTo(centered.Bounds.Left, projectedLocation.X);
            Assert.IsLessThanOrEqualTo(centered.Bounds.Right, projectedLocation.X);
            Assert.IsGreaterThanOrEqualTo(centered.Bounds.Top, projectedLocation.Y);
            Assert.IsLessThanOrEqualTo(centered.Bounds.Bottom, projectedLocation.Y);
            Assert.AreEqual(projectedLocation.X, centered.Bounds.CenterX, 3);
            Assert.AreEqual(projectedLocation.Y, centered.Bounds.CenterY, 3);

            icon.NormalizedAnchorPoint = new Point(0, 1);

            MapRenderFrame cornerFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "anchor-corner",
                frame =>
                    FindColor(frame, color, 200).Length == 1 &&
                    !HasColorNear(frame, color, oldOnlyPixel, 1));
            ConnectedComponent corner =
                AssertSingleColorComponent(cornerFrame, color, 200);
            Assert.AreEqual(centered.Bounds.Left + 16, corner.Bounds.Left, 2);
            Assert.AreEqual(centered.Bounds.Top - 16, corner.Bounds.Top, 2);
            Assert.AreEqual(centered.Bounds.CenterX + 16, corner.Bounds.CenterX, 2);
            Assert.AreEqual(centered.Bounds.CenterY - 16, corner.Bounds.CenterY, 2);
            Assert.AreEqual(centered.Bounds.Width, corner.Bounds.Width, 1);
            Assert.AreEqual(centered.Bounds.Height, corner.Bounds.Height, 1);
            Assert.AreEqual(centered.PixelCount, corner.PixelCount, 16);
            Assert.IsGreaterThan(projectedLocation.X, corner.Bounds.Left);
            Assert.IsLessThan(projectedLocation.Y, corner.Bounds.Bottom);
            Assert.IsTrue(HasColorNear(centeredFrame, color, oldOnlyPixel, 1));
            Assert.IsFalse(HasColorNear(cornerFrame, color, oldOnlyPixel, 1));
        });

    private static PathIcon CreateCenteredRectangleIcon(Color color) =>
        new()
        {
            Width = 32,
            Height = 32,
            Foreground = new SolidColorBrush(color),
            Data = new RectangleGeometry
            {
                Rect = new Rect(5, 7, 22, 18),
            },
        };

    [TestMethod]
    public Task MapIcon_IsVisibleRemovesAndRestoresRenderedComponent() =>
        MapControlTestHost.LoadMapControlAsync(new BasicGeoposition(), 5, async map =>
        {
            BasicGeoposition center = new() { Longitude = 0, Latitude = 0 };
            MapElementsLayer layer = ConfigureBlankMap(map, center);
            Color color = Color.FromArgb(255, 224, 72, 168);
            Point projectedLocation = new(320, 240);
            var icon = new MapIcon(
                CreateCenteredRectangleIcon(color),
                new Geopoint(center));
            layer.MapElements.Add(icon);

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, center, 5);
            MapRenderFrame visibleFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "icon-visible",
                frame => FindColor(frame, color, 300).Length == 1);
            ConnectedComponent visible =
                AssertSingleColorComponent(visibleFrame, color, 300);
            Assert.IsTrue(HasColorNear(visibleFrame, color, projectedLocation, 1));
            Assert.AreEqual(projectedLocation.X, visible.Bounds.CenterX, 2);
            Assert.AreEqual(projectedLocation.Y, visible.Bounds.CenterY, 2);

            icon.IsVisible = false;

            MapRenderFrame hiddenFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "icon-hidden",
                frame =>
                    FindColor(frame, color).Length == 0 &&
                    !HasColorNear(frame, color, projectedLocation, 12));
            AssertColorRemoved(visibleFrame, hiddenFrame, color);
            Assert.IsFalse(HasColorNear(hiddenFrame, color, projectedLocation, 12));

            icon.IsVisible = true;

            MapRenderFrame restoredFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "icon-restored",
                frame => FindColor(frame, color, 300).Length == 1);
            ConnectedComponent restored =
                AssertSingleColorComponent(restoredFrame, color, 300);
            Assert.IsTrue(HasColorNear(restoredFrame, color, projectedLocation, 1));
            Assert.AreEqual(visible.Bounds, restored.Bounds);
            Assert.AreEqual(visible.PixelCount, restored.PixelCount, 16);
            Assert.AreEqual(visible.Bounds.CenterX, restored.Bounds.CenterX, 1);
            Assert.AreEqual(visible.Bounds.CenterY, restored.Bounds.CenterY, 1);
        });

    [TestMethod]
    public Task MapPolygon_IsVisibleRemovesAndRestoresRenderedComponent() =>
        MapControlTestHost.LoadMapControlAsync(new BasicGeoposition(), 5, async map =>
        {
            const double zoom = 5;
            BasicGeoposition center = new() { Longitude = 0, Latitude = 0 };
            MapElementsLayer layer = ConfigureBlankMap(map, center, zoom);
            Color color = Color.FromArgb(255, 40, 200, 112);
            Point projectedCenter = new(320, 240);
            var polygon = new MapPolygon
            {
                Path = CreateViewportRectanglePath(
                    center,
                    zoom,
                    220,
                    160,
                    420,
                    320),
                FillColor = color,
                StrokeColor = Microsoft.UI.Colors.Transparent,
            };
            layer.MapElements.Add(polygon);

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, center, zoom);
            MapRenderFrame visibleFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "polygon-visible",
                frame => FindColor(frame, color, 30_000).Length == 1);
            ConnectedComponent visible =
                AssertSingleColorComponent(visibleFrame, color, 30_000);
            Assert.AreEqual(200, visible.Bounds.Width, 4);
            Assert.AreEqual(160, visible.Bounds.Height, 4);
            Assert.IsTrue(HasColorNear(visibleFrame, color, projectedCenter, 1));

            polygon.IsVisible = false;

            MapRenderFrame hiddenFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "polygon-hidden",
                frame =>
                    FindColor(frame, color).Length == 0 &&
                    !HasColorNear(frame, color, projectedCenter, 20));
            AssertColorRemoved(visibleFrame, hiddenFrame, color);
            Assert.IsFalse(HasColorNear(hiddenFrame, color, projectedCenter, 20));

            polygon.IsVisible = true;

            MapRenderFrame restoredFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "polygon-restored",
                frame => FindColor(frame, color, 30_000).Length == 1);
            ConnectedComponent restored =
                AssertSingleColorComponent(restoredFrame, color, 30_000);
            Assert.IsTrue(HasColorNear(restoredFrame, color, projectedCenter, 1));
            Assert.AreEqual(visible.Bounds, restored.Bounds);
            Assert.AreEqual(visible.PixelCount, restored.PixelCount, 32);
            Assert.AreEqual(visible.Bounds.CenterX, restored.Bounds.CenterX, 1);
            Assert.AreEqual(visible.Bounds.CenterY, restored.Bounds.CenterY, 1);
        });

    [TestMethod]
    public Task MapPolyline_IsVisibleRemovesAndRestoresRenderedComponent() =>
        MapControlTestHost.LoadMapControlAsync(new BasicGeoposition(), 5, async map =>
        {
            const double zoom = 5;
            BasicGeoposition center = new() { Longitude = 0, Latitude = 0 };
            MapElementsLayer layer = ConfigureBlankMap(map, center, zoom);
            Color color = Color.FromArgb(255, 248, 152, 32);
            Point leftEndpoint = new(180, 240);
            Point rightEndpoint = new(460, 240);
            var polyline = new MapPolyline
            {
                Path = CreateViewportPath(
                    center,
                    zoom,
                    leftEndpoint,
                    rightEndpoint),
                StrokeColor = color,
                StrokeThickness = 12,
            };
            layer.MapElements.Add(polyline);

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, center, zoom);
            MapRenderFrame visibleFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "polyline-visible",
                frame =>
                    HasColorNear(frame, color, leftEndpoint, 3) &&
                    HasColorNear(frame, color, rightEndpoint, 3));
            ConnectedComponent visible =
                AssertSingleColorComponent(visibleFrame, color, 3_000);
            Assert.AreEqual(280, visible.Bounds.Width, 6);
            Assert.AreEqual(12, visible.Bounds.Height, 3);
            Assert.IsTrue(HasColorNear(visibleFrame, color, leftEndpoint, 3));
            Assert.IsTrue(HasColorNear(visibleFrame, color, rightEndpoint, 3));

            polyline.IsVisible = false;

            MapRenderFrame hiddenFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "polyline-hidden",
                frame =>
                    FindColor(frame, color).Length == 0 &&
                    !HasColorNear(frame, color, leftEndpoint, 8) &&
                    !HasColorNear(frame, color, rightEndpoint, 8));
            AssertColorRemoved(visibleFrame, hiddenFrame, color);
            Assert.IsFalse(HasColorNear(hiddenFrame, color, leftEndpoint, 8));
            Assert.IsFalse(HasColorNear(hiddenFrame, color, rightEndpoint, 8));

            polyline.IsVisible = true;

            MapRenderFrame restoredFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "polyline-restored",
                frame =>
                    HasColorNear(frame, color, leftEndpoint, 3) &&
                    HasColorNear(frame, color, rightEndpoint, 3));
            ConnectedComponent restored =
                AssertSingleColorComponent(restoredFrame, color, 3_000);
            Assert.IsTrue(HasColorNear(restoredFrame, color, leftEndpoint, 3));
            Assert.IsTrue(HasColorNear(restoredFrame, color, rightEndpoint, 3));
            Assert.AreEqual(visible.Bounds, restored.Bounds);
            Assert.AreEqual(visible.PixelCount, restored.PixelCount, 32);
            Assert.AreEqual(visible.Bounds.CenterX, restored.Bounds.CenterX, 1);
            Assert.AreEqual(visible.Bounds.CenterY, restored.Bounds.CenterY, 1);
        });

    [TestMethod]
    public Task MapElement_IsEnabledDoesNotChangeRenderingForAnySubtype() =>
        MapControlTestHost.LoadMapControlAsync(new BasicGeoposition(), 5, async map =>
        {
            const double zoom = 5;
            BasicGeoposition center = new() { Longitude = 0, Latitude = 0 };
            MapElementsLayer layer = ConfigureBlankMap(map, center, zoom);
            Color iconColor = Color.FromArgb(255, 176, 64, 224);
            Color polygonColor = Color.FromArgb(255, 48, 120, 232);
            Color polylineColor = Color.FromArgb(255, 240, 152, 32);
            Point iconLocation = new(140, 110);
            Point polygonCenter = new(320, 230);
            Point polylineLeft = new(180, 390);
            Point polylineRight = new(460, 390);
            var icon = new MapIcon(
                CreateCenteredRectangleIcon(iconColor),
                new Geopoint(MapControlTestUtilities.LocationAtOffset(
                    center,
                    zoom,
                    640,
                    480,
                    iconLocation)));
            var polygon = new MapPolygon
            {
                Path = CreateViewportRectanglePath(
                    center,
                    zoom,
                    240,
                    170,
                    400,
                    290),
                FillColor = polygonColor,
                StrokeColor = Microsoft.UI.Colors.Transparent,
            };
            var polyline = new MapPolyline
            {
                Path = CreateViewportPath(
                    center,
                    zoom,
                    polylineLeft,
                    polylineRight),
                StrokeColor = polylineColor,
                StrokeThickness = 10,
            };
            layer.MapElements.Add(icon);
            layer.MapElements.Add(polygon);
            layer.MapElements.Add(polyline);

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, center, zoom);
            MapRenderFrame enabledFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "enabled",
                frame =>
                    HasColorNear(frame, iconColor, iconLocation, 2) &&
                    HasColorNear(frame, polygonColor, polygonCenter, 2) &&
                    HasColorNear(frame, polylineColor, polylineLeft, 3) &&
                    HasColorNear(frame, polylineColor, polylineRight, 3));
            ConnectedComponent enabledIcon =
                AssertSingleColorComponent(enabledFrame, iconColor, 300);
            ConnectedComponent enabledPolygon =
                AssertSingleColorComponent(enabledFrame, polygonColor, 15_000);
            ConnectedComponent enabledPolyline =
                AssertSingleColorComponent(enabledFrame, polylineColor, 2_000);

            icon.IsEnabled = false;
            polygon.IsEnabled = false;
            polyline.IsEnabled = false;

            MapRenderFrame disabledFrame = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "disabled",
                frame =>
                    HasColorNear(frame, iconColor, iconLocation, 2) &&
                    HasColorNear(frame, polygonColor, polygonCenter, 2) &&
                    HasColorNear(frame, polylineColor, polylineLeft, 3) &&
                    HasColorNear(frame, polylineColor, polylineRight, 3));
            ConnectedComponent disabledIcon =
                AssertSingleColorComponent(disabledFrame, iconColor, 300);
            ConnectedComponent disabledPolygon =
                AssertSingleColorComponent(disabledFrame, polygonColor, 15_000);
            ConnectedComponent disabledPolyline =
                AssertSingleColorComponent(disabledFrame, polylineColor, 2_000);

            Assert.AreEqual(enabledIcon.Bounds, disabledIcon.Bounds);
            Assert.AreEqual(enabledIcon.PixelCount, disabledIcon.PixelCount, 16);
            Assert.AreEqual(enabledPolygon.Bounds, disabledPolygon.Bounds);
            Assert.AreEqual(enabledPolygon.PixelCount, disabledPolygon.PixelCount, 32);
            Assert.AreEqual(enabledPolyline.Bounds, disabledPolyline.Bounds);
            Assert.AreEqual(enabledPolyline.PixelCount, disabledPolyline.PixelCount, 32);
        });

    [TestMethod]
    public Task MapElement_ZIndexReordersMapIconMapPolygonAndMapPolyline() =>
        MapControlTestHost.LoadMapControlAsync(new BasicGeoposition(), 5, async map =>
        {
            const double zoom = 5;
            BasicGeoposition center = new() { Longitude = 0, Latitude = 0 };
            MapElementsLayer layer = ConfigureBlankMap(map, center, zoom);
            Color polygonColor = Color.FromArgb(255, 48, 120, 232);
            Color polylineColor = Color.FromArgb(255, 240, 72, 48);
            Color iconColor = Color.FromArgb(255, 176, 64, 224);
            Point overlap = new(260, 240);
            Point polygonOnly = new(340, 200);
            Point polylineOnly = new(200, 240);
            Point iconOnly = new(250, 232);
            var polygon = new MapPolygon
            {
                Path = CreateViewportRectanglePath(
                    center,
                    zoom,
                    255,
                    170,
                    400,
                    310),
                FillColor = polygonColor,
                StrokeColor = Microsoft.UI.Colors.Transparent,
                ZIndex = 3,
            };
            var polyline = new MapPolyline
            {
                Path = CreateViewportPath(
                    center,
                    zoom,
                    new Point(180, 240),
                    new Point(460, 240)),
                StrokeColor = polylineColor,
                StrokeThickness = 8,
                ZIndex = 2,
            };
            BasicGeoposition iconPosition =
                MapControlTestUtilities.LocationAtOffset(
                    center,
                    zoom,
                    640,
                    480,
                    overlap);
            var icon = new MapIcon(
                CreateAsymmetricFlagIcon(iconColor),
                new Geopoint(iconPosition))
            {
                ZIndex = 1,
            };
            layer.MapElements.Add(polygon);
            layer.MapElements.Add(polyline);
            layer.MapElements.Add(icon);

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));
            await MapControlTestUtilities.WaitForDisplayedCameraAsync(map, center, zoom);
            MapRenderFrame polygonTop = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "zindex-polygon-top",
                frame =>
                    HasColorNear(frame, polygonColor, overlap, 0) &&
                    HasColorNear(frame, polygonColor, polygonOnly, 1) &&
                    HasColorNear(frame, polylineColor, polylineOnly, 1) &&
                    HasColorNear(frame, iconColor, iconOnly, 1));
            ConnectedComponent polygonTopComponent =
                AssertSingleColorComponent(polygonTop, polygonColor, 18_000);
            Assert.IsTrue(HasColorNear(polygonTop, polygonColor, overlap, 0));
            Assert.IsTrue(HasColorNear(polygonTop, polylineColor, polylineOnly, 1));
            Assert.IsTrue(HasColorNear(polygonTop, iconColor, iconOnly, 1));

            polygon.ZIndex = 0;
            polyline.ZIndex = 3;

            MapRenderFrame polylineTop = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "zindex-polyline-top",
                frame =>
                    HasColorNear(frame, polylineColor, overlap, 0) &&
                    HasColorNear(frame, polygonColor, polygonOnly, 1) &&
                    HasColorNear(frame, polylineColor, polylineOnly, 1) &&
                    HasColorNear(frame, iconColor, iconOnly, 1));
            ConnectedComponent polylineTopComponent =
                AssertSingleColorComponent(polylineTop, polylineColor, 2_000);
            Assert.IsTrue(HasColorNear(polylineTop, polylineColor, overlap, 0));
            Assert.IsFalse(HasColorNear(polylineTop, polygonColor, overlap, 0));
            Assert.IsTrue(HasColorNear(polylineTop, polygonColor, polygonOnly, 1));
            Assert.IsTrue(HasColorNear(polylineTop, iconColor, iconOnly, 1));

            polyline.ZIndex = 1;
            icon.ZIndex = 3;

            MapRenderFrame iconTop = await CaptureUntilAndSaveAsync(
                map,
                timeout.Token,
                "zindex-icon-top",
                frame =>
                    HasColorNear(frame, iconColor, overlap, 0) &&
                    HasColorNear(frame, polygonColor, polygonOnly, 1) &&
                    HasColorNear(frame, polylineColor, polylineOnly, 1) &&
                    HasColorNear(frame, iconColor, iconOnly, 1));
            ConnectedComponent iconTopComponent =
                AssertSingleColorComponent(iconTop, iconColor, 200);
            Assert.IsTrue(HasColorNear(iconTop, iconColor, overlap, 0));
            Assert.IsFalse(HasColorNear(iconTop, polylineColor, overlap, 0));
            Assert.IsTrue(HasColorNear(iconTop, polygonColor, polygonOnly, 1));
            Assert.IsTrue(HasColorNear(iconTop, polylineColor, polylineOnly, 1));
            Assert.IsGreaterThan(100, iconTopComponent.PixelCount);
            Assert.IsGreaterThan(15_000, polygonTopComponent.PixelCount);
            Assert.IsGreaterThan(1_500, polylineTopComponent.PixelCount);
            Assert.IsTrue(layer.IsVisible);
            Assert.AreEqual(1d, layer.Opacity);
        });
}
