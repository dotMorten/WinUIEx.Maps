using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests.UITests;

[TestClass]
[DoNotParallelize]
public sealed class SymbolInstanceStreamingTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(8192)]
    public Task InstanceStreamPreservesMixedLayersAcrossOffsetsAndWraps(int iconsPerTexture)
    {
        TileId tileId = new(4, 8, 8);
        TestVectorTileSource source = TestVectorTileSource.Create(
            tileId,
            new MapboxVectorTileBuilder().AddPoint("labels", 2048, 2048).Build(),
            """
            {
              "version": 8,
              "layers": [{
                "type": "symbol",
                "source-layer": "labels",
                "layout": {
                  "text-field": "7",
                  "text-font": ["TestFont"],
                  "text-size": 24
                },
                "paint": {
                  "text-color": "#00ff00",
                  "text-halo-color": "#ffffff",
                  "text-halo-width": 3
                }
              }]
            }
            """,
            "{}", [0, 0, 0, 0], 1, 1);
        source.AddGlyphs("TestFont", TestGlyph.RectangleSdf('7'));
        return MapControlTestHost.LoadMapControlAsync(
            source.TileCenter, tileId.Zoom, async map =>
            {
                using RenderingEventListener listener = new(
                    "SymbolInstanceUploadTiming", "IconRenderBatch");
                TestRasterTileSource raster = new(
                    tileId.Zoom,
                    new Dictionary<TileId, TestRasterTile>
                    {
                        [tileId] = TestRasterTileSource.Solid(256, 64, 64, 64),
                    });
                map.Layers.Add(new TestHybridRasterTileLayer(raster));
                map.Layers.Add(new TestVectorTileLayer(source));

                MapElementsLayer icons = new();
                foreach ((double offset, Windows.UI.Color color) in new[]
                {
                    (-3d, Colors.Red), (3d, Colors.Blue),
                })
                {
                    PathIcon visual = new()
                    {
                        Width = 4,
                        Height = 4,
                        Data = new EllipseGeometry { Center = new Point(2, 2), RadiusX = 2, RadiusY = 2 },
                        Foreground = new SolidColorBrush(color),
                    };
                    Geopoint location = new(new BasicGeoposition
                    {
                        Longitude = source.TileCenter.Longitude + offset,
                        Latitude = source.TileCenter.Latitude,
                    });
                    icons.MapElements.AddRange(Enumerable.Range(0, iconsPerTexture)
                        .Select(_ => new MapIcon(visual, location)));
                }
                map.Layers.Add(icons);
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
                await MapControlTestUtilities.WaitForAsync(() =>
                    listener.Events("IconRenderBatch").Any(e =>
                        Convert.ToInt32(e.Payload[1]) == iconsPerTexture * 2));
                for (int iteration = 0; iteration < 3; iteration++)
                {
                    Assert.IsTrue(await map.TrySetViewAsync(
                        new Geopoint(source.TileCenter), tileId.Zoom + iteration * 0.25,
                        null, null, MapAnimationKind.None));
                    MapRenderFrame frame = await map.CaptureRenderedFrameAsync(timeout.Token);
                    foreach ((byte red, byte green, byte blue) in new[]
                    {
                        ((byte)255, (byte)0, (byte)0),
                        ((byte)0, (byte)0, (byte)255),
                        ((byte)0, (byte)255, (byte)0),
                        ((byte)255, (byte)255, (byte)255),
                        ((byte)64, (byte)64, (byte)64),
                    })
                    {
                        Assert.IsNotEmpty(ConnectedComponentAnalyzer.Find(
                            frame, ConnectedComponentAnalyzer.Near(red, green, blue, tolerance: 8),
                            minimumPixelCount: 4),
                            $"Missing color {red},{green},{blue} after instance-stream wrap.");
                    }
                }

                CapturedRenderingEvent[] uploads = listener.Events("SymbolInstanceUploadTiming");
                if (iconsPerTexture == 8192)
                {
                    Assert.IsTrue(uploads.Any(e =>
                        Convert.ToInt64(e.Payload[5]) > Convert.ToInt64(e.Payload[6]) &&
                        Convert.ToInt32(e.Payload[3]) > 0 &&
                        Convert.ToInt32(e.Payload[4]) > 0),
                        "A frame must append and wrap more than one full instance buffer.");
                }
                else
                {
                    Assert.IsTrue(uploads.Any(e =>
                        Convert.ToInt32(e.Payload[2]) > 0 && Convert.ToInt32(e.Payload[3]) == 0),
                        "Small batches must reuse the stream across frames without discarding.");
                }
                Assert.IsTrue(uploads.Any(e => Convert.ToInt32(e.Payload[3]) > 0));
                Assert.IsTrue(uploads.Any(e => Convert.ToInt32(e.Payload[4]) > 0),
                    "At least one draw must use an appended, nonzero instance offset.");
                foreach (CapturedRenderingEvent upload in uploads)
                {
                    Assert.AreEqual(Convert.ToInt32(upload.Payload[2]),
                        Convert.ToInt32(upload.Payload[3]) + Convert.ToInt32(upload.Payload[4]));
                }
            });
    }
}
