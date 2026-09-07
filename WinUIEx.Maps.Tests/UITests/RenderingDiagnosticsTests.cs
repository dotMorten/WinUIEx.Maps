using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.Input;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests.UITests;

[TestClass]
[DoNotParallelize]
public sealed class RenderingDiagnosticsTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task ContinuousPanAndZoomProduceCorrelatedFrameTimings()
    {
        TileId tileId = new(14, 4823, 6160);
        MapboxVectorTileBuilder builder = new();
        for (int index = 0; index < 128; index++)
        {
            builder.AddPoint("labels", 256 + index % 16 * 224, 256 + index / 16 * 448);
        }
        TestVectorTileSource source = TestVectorTileSource.Create(
            tileId,
            builder.Build(),
            """
            {
              "version": 8,
              "layers": [{
                "type": "symbol",
                "source-layer": "labels",
                "layout": {
                  "text-field": "7",
                  "text-font": ["TestFont"],
                  "text-size": 16
                },
                "paint": { "text-color": "#000000" }
              }]
            }
            """,
            "{}", [0, 0, 0, 0], 1, 1);
        source.AddGlyphs("TestFont", TestGlyph.Solid('7'));
        return MapControlTestHost.LoadMapControlAsync(
            source.TileCenter, tileId.Zoom, async map =>
            {
                using RenderingEventListener listener = new(
                    "RenderFrameTiming", "MapFrameStageTiming");
                map.Layers.Add(new TestVectorTileLayer(source));
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
                _ = await map.CaptureRenderedFrameAsync(timeout.Token);
                UiInputInjector input = UiInputInjector.ForElement(MapControlTestHost.Window, map);
                int completedManipulations = 0;
                map.AddHandler(
                    UIElement.ManipulationCompletedEvent,
                    new ManipulationCompletedEventHandler((_, _) => completedManipulations++),
                    handledEventsToo: true);
                Assert.IsNotNull(map.Center);
                double longitude = map.Center.Position.Longitude;
                await input.Touch.SwipeAsync(
                    input.PointAt(0.6, 0.5), input.PointAt(0.4, 0.5), 1500);
                await MapControlTestUtilities.WaitForAsync(() => completedManipulations >= 1);
                Assert.AreNotEqual(longitude, map.Center.Position.Longitude);
                int panFrames = listener.Events("RenderFrameTiming").Length;
                double zoom = map.ZoomLevel;
                await input.Touch.StretchAsync(distance: 60, durationMilliseconds: 1500);
                await MapControlTestUtilities.WaitForAsync(() => completedManipulations >= 2);
                Assert.IsTrue(map.ZoomLevel > zoom);
                _ = await map.CaptureRenderedFrameAsync(timeout.Token);
                while (listener.Events("RenderFrameTiming").Length <= panFrames)
                {
                    await Task.Delay(10, timeout.Token);
                }

                CapturedRenderingEvent[] frames = listener.Events("RenderFrameTiming");
                CapturedRenderingEvent[] stages = listener.Events("MapFrameStageTiming");
                Assert.IsTrue(panFrames > 1);
                Assert.IsTrue(frames.Length > panFrames);
                foreach (CapturedRenderingEvent frame in frames)
                {
                    Assert.IsTrue(stages.Any(stage =>
                        Equals(frame.Payload[0], stage.Payload[0]) &&
                        Equals(frame.Payload[1], stage.Payload[1])));
                    Assert.IsTrue(frame.Payload.Skip(2).All(value =>
                        double.IsFinite(Convert.ToDouble(value)) && Convert.ToDouble(value) >= 0));
                    double sum = frame.Payload.Skip(2).Take(5).Sum(Convert.ToDouble);
                    Assert.AreEqual(sum, Convert.ToDouble(frame.Payload[7]), 0.001);
                }
                double[] renderTimes = frames.Select(frame =>
                    Convert.ToDouble(frame.Payload[3])).Order().ToArray();
                TestContext.WriteLine(
                    $"Rendered frames: {frames.Length}; CPU render p50: " +
                    $"{renderTimes[renderTimes.Length / 2]:F3} ms; p95: " +
                    $"{renderTimes[(int)Math.Ceiling(renderTimes.Length * 0.95) - 1]:F3} ms. " +
                    "Includes tracing; not a displayed-FPS measurement.");
            });
    }
}
