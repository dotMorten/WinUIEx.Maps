using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BuildingOutlineClippingTests
{
    [TestMethod]
    [DataRow(1, 1d)]
    [DataRow(4, 1d)]
    [DataRow(1, 0.5)]
    [DataRow(4, 0.5)]
    public async Task AdjacentBufferedFootprintsKeepRealOutlinesWithoutClippingGrid(int samples, double opacity)
    {
        using RenderingEventListener listener = new("VectorPolygonDecorationSummary");
        using MapRenderer renderer = new();
        renderer.InitializeOffscreenForBenchmark(512, 256, samples);
        TileId leftId = new(4, 8, 8), rightId = new(4, 9, 8);
        byte[] left = new MapboxVectorTileBuilder().AddPolygon("footprint", [
            [new(1024, 1024), new(4696, 1024), new(4696, 3072), new(1024, 3072)],
            [new(3072, 1536), new(3072, 2560), new(4696, 2560), new(4696, 1536)]])
            .Build();
        byte[] right = new MapboxVectorTileBuilder().AddPolygon("footprint", [
            [new(-600, 1024), new(3072, 1024), new(3072, 3072), new(-600, 3072)],
            [new(-600, 1536), new(-600, 2560), new(1024, 2560), new(1024, 1536)]])
            .Build();
        MapScreenPoint[] retainedOutline = MapRenderer.ExpandVectorPolygonOutlineTriangles(
            VectorTileDecoder.Decode(left).Features.Single().Polygons.Single().Rings,
            new VisibleTile(leftId, leftId.X, 0, 0, 256), 512, 256, 0, 0);
        Assert.IsNotEmpty(retainedOutline);
        Assert.AreEqual(63.5, retainedOutline.Min(point => point.X), 0.001);
        TestVectorTileSource source = TestVectorTileSource.Create(leftId, left,
            """
            {"version":8,"layers":[
              {"type":"background","paint":{"background-color":"#ffffff"}},
              {"type":"fill","source-layer":"footprint","paint":{
                "fill-color":"rgba(255, 0, 0, 0.5)","fill-outline-color":"#000000"}}
            ]}
            """, "{}", [0, 0, 0, 0], 1, 1);
        double longitude = source.TileCenter.Longitude + 180d / (1 << leftId.Zoom);
        renderer.SetLayerRenderPlan([new(LayerRenderKind.VectorPoints, 0, 1, true, opacity,
            TimeSpan.Zero, 0, 24, 0, 256)]);
        renderer.SetCameraTargetImmediately(longitude, source.TileCenter.Latitude, 4, 512, 256);
        renderer.ActivateRasterTileSet(1, 1, 1,
            MapCamera.CreateScene(longitude, source.TileCenter.Latitude, 4, 4, 512, 256, 0, 0),
            id => id == leftId || id == rightId, RasterSourceKind.Custom, LayerRenderKind.VectorPoints, false);
        renderer.AddVectorTileForBenchmark(new(1, leftId), VectorTileDecoder.Decode(left), source.StyleAssets);
        renderer.AddVectorTileForBenchmark(new(1, rightId), VectorTileDecoder.Decode(right), source.StyleAssets);
        foreach (double offset in new[] { 0, 0.13, 0.47 })
        {
            renderer.SetCameraTargetImmediately(longitude, source.TileCenter.Latitude, 4 + offset, 512, 256);
            MapRenderFrame frame = renderer.CaptureOffscreenFrameForBenchmark();
            string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "artifacts", "azure-buildings"));
            Directory.CreateDirectory(directory);
            await frame.SavePngAsync(Path.Combine(directory,
                FormattableString.Invariant($"seam-fixture-{samples}-{opacity}-{offset}.png")));
            double scale = Math.Pow(2, offset);
            int roofY = (int)Math.Round(128 - 48 * scale);
            int referenceX = (int)Math.Round(256 - 100 * scale);
            int referenceGreen = Channel(frame, referenceX, roofY, 1);
            int referenceRed = Channel(frame, referenceX, roofY, 2);
            int backgroundGreen = Channel(frame, 10, 10, 1);
            foreach (double x in new[] { 256 - 37.5 * scale, 256d, 256 + 37.5 * scale })
            {
                for (int column = (int)Math.Floor(x) - 2; column <= (int)Math.Ceiling(x) + 2; column++)
                {
                    Assert.AreEqual(referenceRed, Channel(frame, column, roofY, 2), 3,
                        "Neither a buffered closure nor a tile join may stroke the roof interior.");
                    Assert.AreEqual(referenceGreen, Channel(frame, column, roofY, 1), 3,
                        "Adjacent translucent fills must not overlap or leave a resolve seam.");
                    Assert.AreEqual(backgroundGreen, Channel(frame, column, 128, 1), 3,
                        "The courtyard must remain transparent across the tile boundary.");
                }
            }
            int edgeX = (int)Math.Round(256 - 192 * scale);
            Assert.IsTrue(Enumerable.Range(edgeX - 2, 5)
                .Any(x => Channel(frame, x, roofY, 2) < 230), "Keep the real outer building outline.");
            int holeEdgeX = (int)Math.Round(256 - 64 * scale);
            Assert.IsTrue(Enumerable.Range(holeEdgeX - 2, 5)
                .Any(x => Channel(frame, x, 128, 2) < 230), "Keep the real courtyard outline.");
        }
        Assert.IsTrue(listener.Events("VectorPolygonDecorationSummary")
            .All(e => Convert.ToInt32(e.Payload[3]) > 0));
    }

    private static int Channel(MapRenderFrame frame, int x, int y, int channel) =>
        frame.Pixels.Span[(y * frame.Width + x) * 4 + channel];
}
