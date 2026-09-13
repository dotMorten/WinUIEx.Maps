using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Microsoft.UI;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests.UITests;

[TestClass]
[DoNotParallelize]
public sealed class RenderSurfaceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CaptureUsesPhysicalPixelsAndPreservesLogicalIconPositionAfterResize(bool disableAntialiasing)
    {
        const string switchName = "WinUIEx.Maps.DisableMultisampleAntialiasing";
        AppContext.TryGetSwitch(switchName, out bool previous);
        AppContext.SetSwitch(switchName, disableAntialiasing);
        try
        {
        Assert.AreEqual(!disableAntialiasing, DirectXRenderer.IsPresentationAntialiasingEnabled);
        await MapControlTestHost.LoadMapControlAsync(
            MapControlTestUtilities.InitialCenter,
            MapControlTestUtilities.InitialZoomLevel,
            async map =>
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
                using RenderingEventListener events = new("RenderSurfaceChanged", "RendererFailure");
                var layer = new MapElementsLayer();
                layer.MapElements.Add(new MapIcon(new PathIcon
                {
                    Width = 24,
                    Height = 24,
                    Data = new RectangleGeometry { Rect = new Rect(0, 0, 24, 24) },
                    Foreground = new SolidColorBrush(Colors.Red),
                }, map.Center!));
                map.Layers.Add(layer);

                foreach (int width in new[] { 641, 700, 640 })
                {
                    map.Width = width;
                    map.Height = 480;
                    map.UpdateLayout();
                    await MapControlTestUtilities.WaitForAsync(() => map.ActualWidth == width);
                    MapRenderFrame frame;
                    ConnectedComponent[] icons;
                    do
                    {
                        await Task.Delay(20, timeout.Token);
                        frame = await map.CaptureRenderedFrameAsync(timeout.Token);
                        icons = ConnectedComponentAnalyzer.Find(
                            frame, ConnectedComponentAnalyzer.Near(255, 0, 0, tolerance: 8),
                            minimumPixelCount: 20).ToArray();
                    } while (icons.Length == 0);
                    double scale = map.XamlRoot.RasterizationScale;
                    Assert.AreEqual((int)Math.Ceiling(width * scale), frame.Width);
                    Assert.AreEqual((int)Math.Ceiling(480 * scale), frame.Height);
                    CapturedRenderingEvent surface = events.Events("RenderSurfaceChanged").Last();
                    Assert.AreEqual(frame.Width, Convert.ToInt32(surface.Payload[5]));
                    Assert.AreEqual(frame.Height, Convert.ToInt32(surface.Payload[6]));
                    int samples = Convert.ToInt32(surface.Payload[8]);
                    Assert.IsTrue(samples is 1 or 4);
                    Assert.AreEqual(samples > 1, ReadRasterizerMultisampleEnable(map),
                        "Production rasterizer must be created after selecting the surface sample count.");
                    if (disableAntialiasing)
                        Assert.AreEqual(1, samples);
                    Assert.AreEqual((long)frame.Width * frame.Height * 4 * (2 + (samples == 4 ? 4 : 0)),
                        Convert.ToInt64(surface.Payload[7]));
                    Assert.AreEqual(0, events.Events("RendererFailure").Length);
                    ConnectedComponent icon = Assert.ContainsSingle(icons);
                    Assert.AreEqual(frame.Width / 2d, icon.Bounds.CenterX, 2 * scale);
                    Assert.IsTrue(map.TryGetLocationFromOffset(
                        new Point(width / 2d, 240), out var center));
                    Assert.AreEqual(map.Center!.Position.Longitude, center.Position.Longitude, 0.00001);
                    Assert.AreEqual(map.Center.Position.Latitude, center.Position.Latitude, 0.00001);
                    await frame.SavePngAsync(Path.Combine(
                        AppContext.BaseDirectory, "TestResults", $"surface-{width}.png"));
                }

                foreach (double compositionScale in new[] { 1.25, 1.5, 1.75, 2, 1 })
                {
                    map.RenderTransform = new ScaleTransform
                    {
                        ScaleX = compositionScale,
                        ScaleY = compositionScale,
                    };
                    double scale = compositionScale * map.XamlRoot.RasterizationScale;
                    await MapControlTestUtilities.WaitForAsync(() =>
                        events.Events("RenderSurfaceChanged").LastOrDefault().Payload is { } payload &&
                        Math.Abs(Convert.ToDouble(payload[3]) - scale) < 0.001);
                    MapRenderFrame frame = await map.CaptureRenderedFrameAsync(timeout.Token);
                    Assert.AreEqual((int)Math.Ceiling(640 * scale), frame.Width);
                    Assert.AreEqual((int)Math.Ceiling(480 * scale), frame.Height);
                    ConnectedComponent icon = Assert.ContainsSingle(ConnectedComponentAnalyzer.Find(
                        frame, ConnectedComponentAnalyzer.Near(255, 0, 0, tolerance: 8),
                        minimumPixelCount: 20));
                    Assert.AreEqual(frame.Width / 2d, icon.Bounds.CenterX, 2 * scale);
                    await frame.SavePngAsync(Path.Combine(
                        AppContext.BaseDirectory, "TestResults", $"surface-scale-{compositionScale}.png"));
                }
            });
        }
        finally
        {
            AppContext.SetSwitch(switchName, previous);
        }

    }

    private static unsafe bool ReadRasterizerMultisampleEnable(MapControl map)
    {
    const System.Reflection.BindingFlags flags =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
    var renderer = (MapRenderer)typeof(MapControl).GetField("_renderer", flags)!.GetValue(map)!;
    object sync = typeof(DirectXRenderer).GetProperty("RenderLock", flags)!.GetValue(renderer)!;
    lock (sync)
    {
        IntPtr pointer = (IntPtr)typeof(MapRenderer).GetField("_rasterizerPointer", flags)!.GetValue(renderer)!;
        Assert.AreNotEqual(IntPtr.Zero, pointer);
        Windows.Win32.Graphics.Direct3D11.D3D11_RASTERIZER_DESC description;
        void** methods = *(void***)pointer;
        ((delegate* unmanaged[Stdcall]<IntPtr, Windows.Win32.Graphics.Direct3D11.D3D11_RASTERIZER_DESC*, void>)methods[7])(
            pointer, &description);
        return description.MultisampleEnable;
    }
    }
}
