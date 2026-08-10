using WinUIEx.Maps.Tests.Input;
using System.Diagnostics.Tracing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests.UITests;

[TestClass]
[DoNotParallelize]
public sealed class MapIconTests
{
    private const double CoordinateTolerance = 0.000000001;
    private const double TileSize = 256;
    private const double ZoomTolerance = 0.001;
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public Task MapIcon_RendersAtCenter() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            using var renderListener = new IconRenderListener();
            BasicGeoposition center = new()
            {
                Latitude = 10,
                Longitude = 20,
            };
            map.MapStyle = WinUIEx.Maps.MapStyle.Blank;
            map.ZoomLevel = 5;
            map.Center = new Geopoint(center);

            var pin = new PathIcon
            {
                Width = 16,
                Height = 16,
                Data = new EllipseGeometry
                {
                    Center = new Point(8, 8),
                    RadiusX = 8,
                    RadiusY = 8,
                },
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red),
            };
            var layer = new MapElementsLayer();
            layer.MapElements.Add(new MapIcon(pin, new Geopoint(center)));
            map.Layers.Add(layer);

            await WaitForAsync(() => IsDisplayedAt(map, center, map.ZoomLevel));
            await WaitForAsync(() => renderListener.HasDrawableIcon);
            UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            await WaitForAsync(() => ContainsRedPixel(input.Screenshot.Capture()));
        });

    [TestMethod]
    public Task StandardIconSlotKeepsFontGlyphInsideTexture() =>
        MapControlTestHost.LoadUIAsync(
            () => new FontIcon
                {
                    Glyph = "\uE81D",
                    FontSize = 20,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red),
                },
            async element =>
            {
                var icon = (FontIcon)element;
                icon.Measure(new Size(32, 32));
                Size desiredSize = icon.DesiredSize;
                icon.Arrange(new Rect(0, 0, 32, 32));

                RasterCapture capture = await CaptureAsync(icon);
                Assert.IsTrue(capture.Pixels.Where((_, index) => index % 4 == 3)
                    .Any(alpha => alpha != 0));
                Assert.IsFalse(
                    HasRedBoundaryPixel(capture.Pixels, capture.Width, capture.Height),
                    $"Desired size {desiredSize.Width}x{desiredSize.Height} in a 32x32 slot clips the glyph.");
            });

    [TestMethod]
    public Task IconElementPropertyChangeAutomaticallyInvalidatesRaster() =>
        MapControlTestHost.LoadMapControlAsync(map =>
        {
            map.MapStyle = WinUIEx.Maps.MapStyle.Blank;
            var iconElement = new FontIcon
            {
                Glyph = "\uE81D",
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red),
            };
            var layer = new MapElementsLayer();
            layer.MapElements.Add(
                new MapIcon(iconElement, new Geopoint(new BasicGeoposition())));
            map.Layers.Add(layer);
            long version = map.GetIconTextureVersion(iconElement);

            iconElement.Foreground =
                new SolidColorBrush(Microsoft.UI.Colors.Blue);

            Assert.IsGreaterThan(version, map.GetIconTextureVersion(iconElement));
            return Task.CompletedTask;
        });

    [TestMethod]
    public Task SharedIconReferencesFollowOffThreadIconElementChanges() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            using var renderListener = new IconRenderListener();
            map.MapStyle = WinUIEx.Maps.MapStyle.Blank;
            var sharedElement = new FontIcon { Glyph = "\uE81D" };
            var replacementElement = new FontIcon { Glyph = "\uE946" };
            var first = new MapIcon(
                sharedElement,
                new Geopoint(new BasicGeoposition()));
            var second = new MapIcon(
                sharedElement,
                new Geopoint(new BasicGeoposition()));
            var layer = new MapElementsLayer
            {
                MapElements = [first, second],
            };
            map.Layers.Add(layer);

            Assert.AreEqual(1, map.TrackedIconTextureCount);
            Assert.AreEqual(2, map.GetIconTextureReferenceCount(sharedElement));
            await WaitForAsync(() => renderListener.HasDrawableIcon);
            renderListener.ResetIncrementalUpdates();

            await Task.Run(() => first.IconElement = replacementElement);
            await WaitForAsync(() =>
                map.TrackedIconTextureCount == 2 &&
                map.GetIconTextureReferenceCount(sharedElement) == 1 &&
                map.GetIconTextureReferenceCount(replacementElement) == 1 &&
                renderListener.HasIncrementalUpdate);

            layer.MapElements.Remove(second);

            Assert.AreEqual(1, map.TrackedIconTextureCount);
            Assert.AreEqual(0, map.GetIconTextureReferenceCount(sharedElement));
            Assert.AreEqual(1, map.GetIconTextureReferenceCount(replacementElement));
        });

    [TestMethod]
    public Task LargeFontIconUsesItsDesiredSize() =>
        MapControlTestHost.LoadUIAsync(
            () => new FontIcon
            {
                Glyph = "\uE81D",
                FontSize = 60,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red),
            },
            element =>
            {
                var icon = (FontIcon)element;
                icon.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                Assert.IsGreaterThanOrEqualTo(60, icon.DesiredSize.Width);
                Assert.IsGreaterThanOrEqualTo(60, icon.DesiredSize.Height);
            });

    [TestMethod]
    public Task MapElementInputTargetsOnlyTopmostIcon() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            using var renderListener = new IconRenderListener();
            BasicGeoposition center = new()
            {
                Latitude = 10,
                Longitude = 20,
            };
            map.MapStyle = WinUIEx.Maps.MapStyle.Blank;
            map.ZoomLevel = 5;
            map.Center = new Geopoint(center);
            MapIcon lowerIcon = CreateInputIcon(center, Microsoft.UI.Colors.Red);
            MapIcon upperIcon = CreateInputIcon(center, Microsoft.UI.Colors.Blue);
            lowerIcon.ZIndex = int.MaxValue;
            var lowerLayer = new MapElementsLayer();
            var upperLayer = new MapElementsLayer();
            lowerLayer.MapElements.Add(lowerIcon);
            upperLayer.MapElements.Add(upperIcon);
            map.Layers.Add(lowerLayer);
            map.Layers.Add(upperLayer);
            int lowerEntered = 0;
            int upperEntered = 0;
            int upperExited = 0;
            int upperMoved = 0;
            int upperPressed = 0;
            int upperReleased = 0;
            int upperTapped = 0;
            int upperRightTapped = 0;
            Geopoint? tappedLocation = null;
            Geopoint? rightTappedLocation = null;
            lowerLayer.PointerEntered += (_, _) => lowerEntered++;
            upperLayer.PointerEntered += (_, e) =>
            {
                Assert.AreSame(upperIcon, e.MapElement);
                upperEntered++;
            };
            upperLayer.PointerExited += (_, e) =>
            {
                Assert.AreSame(upperIcon, e.MapElement);
                upperExited++;
            };
            upperLayer.PointerMoved += (_, _) => upperMoved++;
            upperLayer.PointerPressed += (_, _) => upperPressed++;
            upperLayer.PointerReleased += (_, _) => upperReleased++;
            upperLayer.Tapped += (_, e) =>
            {
                Assert.AreSame(upperIcon, e.MapElement);
                AssertLocationMatchesOffset(
                    map,
                    e.GetPosition(map),
                    e.Location);
                tappedLocation = e.Location;
                upperTapped++;
            };
            upperLayer.RightTapped += (_, e) =>
            {
                Assert.AreSame(upperIcon, e.MapElement);
                AssertLocationMatchesOffset(
                    map,
                    e.GetPosition(map),
                    e.Location);
                rightTappedLocation = e.Location;
                upperRightTapped++;
            };
            Assert.AreEqual(
                MapElementInputEventKind.PointerHover |
                    MapElementInputEventKind.PointerPressed |
                    MapElementInputEventKind.PointerReleased |
                    MapElementInputEventKind.Tapped |
                    MapElementInputEventKind.RightTapped,
                map.ElementInputHandlers);
            await WaitForAsync(() => IsDisplayedAt(map, center, map.ZoomLevel));
            await WaitForAsync(() => renderListener.HasDrawableIcon);
            Assert.IsTrue(map.TryHitTestMapElement(
                new Point(map.ActualWidth / 2, map.ActualHeight / 2),
                out MapElement? hitElement));
            Assert.AreSame(upperIcon, hitElement);
            UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
            Point? rawPoint = null;
            map.AddHandler(
                UIElement.PointerMovedEvent,
                new PointerEventHandler((_, e) =>
                {
                    rawPoint = e.GetCurrentPoint(map).Position;
                }),
                handledEventsToo: true);

            input.Mouse.MoveTo(input.PointAt(0.1, 0.1));
            await Task.Delay(100);
            input.Mouse.MoveTo(input.Mouse.Center);
            await WaitForAsync(() =>
                rawPoint is Point point &&
                map.TryHitTestMapElement(point, out MapElement? element) &&
                ReferenceEquals(element, upperIcon));
            Assert.IsTrue(map.TryHitTestMapElement(
                rawPoint.GetValueOrDefault(),
                out hitElement));
            Assert.AreSame(upperIcon, hitElement);
            await WaitForAsync(() => upperEntered == 1 && upperMoved > 0);
            Assert.AreEqual(0, lowerEntered);

            input.Mouse.Click(input.Mouse.Center);
            await WaitForAsync(
                () => upperPressed > 0 && upperReleased > 0 && upperTapped > 0);
            Assert.IsNotNull(tappedLocation);

            input.Mouse.RightClick(input.Mouse.Center);
            await WaitForAsync(() => upperRightTapped > 0);
            Assert.IsNotNull(rightTappedLocation);

            input.Mouse.MoveTo(input.PointAt(0.1, 0.1));
            await WaitForAsync(() => upperExited == 1);
        });

    [TestMethod]
    public Task MapElementStateControlsVisibilityInputAndLayerLocalOrder() =>
        MapControlTestHost.LoadMapControlAsync(async map =>
        {
            using var renderListener = new IconRenderListener();
            BasicGeoposition center = new()
            {
                Latitude = 10,
                Longitude = 20,
            };
            map.MapStyle = WinUIEx.Maps.MapStyle.Blank;
            map.ZoomLevel = 5;
            map.Center = new Geopoint(center);
            MapIcon first = CreateInputIcon(center, Microsoft.UI.Colors.Red);
            MapIcon second = CreateInputIcon(center, Microsoft.UI.Colors.Blue);
            var layer = new MapElementsLayer
            {
                MapElements = [first, second],
            };
            map.Layers.Add(layer);

            await WaitForAsync(() => renderListener.HasDrawableIcon);
            Point centerPoint = new(map.ActualWidth / 2, map.ActualHeight / 2);
            await WaitForAsync(() =>
                map.TryHitTestMapElement(centerPoint, out MapElement? element) &&
                ReferenceEquals(element, second));

            first.ZIndex = 1;

            await WaitForAsync(() =>
                map.TryHitTestMapElement(centerPoint, out MapElement? element) &&
                ReferenceEquals(element, first));

            first.IsEnabled = false;

            await WaitForAsync(() =>
                map.TryHitTestMapElement(centerPoint, out MapElement? element) &&
                ReferenceEquals(element, second));

            second.IsVisible = false;
            first.IsEnabled = true;

            await WaitForAsync(() =>
                map.TryHitTestMapElement(centerPoint, out MapElement? element) &&
                ReferenceEquals(element, first));
        });

    private static void AssertLocationMatchesOffset(
        MapControl map,
        Point offset,
        Geopoint actual)
    {
        Assert.IsTrue(map.TryGetLocationFromOffset(offset, out Geopoint expected));
        Assert.AreEqual(
            expected.Position.Latitude,
            actual.Position.Latitude,
            CoordinateTolerance);
        Assert.AreEqual(
            expected.Position.Longitude,
            actual.Position.Longitude,
            CoordinateTolerance);
    }

    private static MapIcon CreateInputIcon(
        BasicGeoposition location,
        Windows.UI.Color color) =>
        new(
            new FontIcon
            {
                Glyph = "\uE81D",
                FontSize = 20,
                Foreground = new SolidColorBrush(color),
            },
            new Geopoint(location));

    private static async Task<RasterCapture> CaptureAsync(UIElement element)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        IBuffer buffer = await bitmap.GetPixelsAsync();
        byte[] pixels = new byte[buffer.Length];
        using (DataReader reader = DataReader.FromBuffer(buffer))
        {
            reader.ReadBytes(pixels);
        }

        return new RasterCapture(pixels, bitmap.PixelWidth, bitmap.PixelHeight);
    }

    private static bool IsDisplayedAt(
        MapControl map,
        BasicGeoposition expectedCenter,
        double expectedZoom)
    {
        var viewportCenter = new Point(map.ActualWidth / 2, map.ActualHeight / 2);
        const double sampleDistance = 64;
        var sampleOffset = new Point(viewportCenter.X + sampleDistance, viewportCenter.Y);
        if (!map.TryGetLocationFromOffset(viewportCenter, out Geopoint centerLocation) ||
            !map.TryGetLocationFromOffset(sampleOffset, out Geopoint sampleLocation))
        {
            return false;
        }

        double longitudeDelta =
            sampleLocation.Position.Longitude - centerLocation.Position.Longitude;
        if (longitudeDelta < 0)
        {
            longitudeDelta += 360;
        }

        double displayedZoom = Math.Log2(
            (360 * sampleDistance) /
            (TileSize * longitudeDelta));
        return Math.Abs(centerLocation.Position.Longitude - expectedCenter.Longitude) <=
                CoordinateTolerance &&
            Math.Abs(centerLocation.Position.Latitude - expectedCenter.Latitude) <=
                CoordinateTolerance &&
            Math.Abs(displayedZoom - expectedZoom) <= ZoomTolerance;
    }

    private static bool ContainsRedPixel(ScreenshotFrame screenshot)
    {
        ReadOnlySpan<byte> pixels = screenshot.Pixels;
        for (int index = 0; index <= pixels.Length - 4; index += 4)
        {
            byte blue = pixels[index];
            byte green = pixels[index + 1];
            byte red = pixels[index + 2];
            if (red >= 192 && green <= 96 && blue <= 96)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasRedBoundaryPixel(
        ReadOnlySpan<byte> pixels,
        int width,
        int height)
    {
        for (int x = 0; x < width; x++)
        {
            if (IsRedPixel(pixels, x * 4) ||
                IsRedPixel(pixels, ((height - 1) * width + x) * 4))
            {
                return true;
            }
        }

        for (int y = 1; y < height - 1; y++)
        {
            if (IsRedPixel(pixels, y * width * 4) ||
                IsRedPixel(pixels, (y * width + width - 1) * 4))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRedPixel(ReadOnlySpan<byte> pixels, int offset) =>
        pixels[offset + 2] > 32 &&
        pixels[offset + 2] > pixels[offset] * 2 &&
        pixels[offset + 2] > pixels[offset + 1] * 2;

    private readonly record struct RasterCapture(byte[] Pixels, int Width, int Height);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + RenderTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("The map icon was not rendered before the timeout.");
    }

    private sealed class IconRenderListener : EventListener
    {
        private int _hasDrawableIcon;
        private int _incrementalUpdateCount;

        internal bool HasDrawableIcon => Volatile.Read(ref _hasDrawableIcon) != 0;

        internal bool HasIncrementalUpdate =>
            Volatile.Read(ref _incrementalUpdateCount) != 0;

        internal void ResetIncrementalUpdates() =>
            Interlocked.Exchange(ref _incrementalUpdateCount, 0);

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "WinUIEx-Maps-Rendering")
            {
                EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x20);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId == 22)
            {
                Interlocked.Increment(ref _incrementalUpdateCount);
            }
            if (eventData.EventId == 26 &&
                eventData.Payload is { Count: >= 2 } &&
                eventData.Payload[1] is int drawableInstanceCount &&
                drawableInstanceCount > 0)
            {
                Interlocked.Exchange(ref _hasDrawableIcon, 1);
            }
        }
    }
}
