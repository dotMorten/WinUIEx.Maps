using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;
using Windows.Devices.Geolocation;

namespace WinUIEx.Maps.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AzureBuildingFootprintTests
{
    [TestMethod]
    [DataRow(18, false)]
    [DataRow(18, true)]
    [DataRow(22, false)]
    [DataRow(19, true)]
    public async Task CaptureSeattleBuildingFootprints(int zoom, bool overview)
    {
        string? token = new ConfigurationBuilder()
            .AddUserSecrets<AzureBuildingFootprintTests>(optional: true).Build()
            ["AzureMaps:MapServiceToken"];
        if (string.IsNullOrWhiteSpace(token))
            Assert.Inconclusive("Azure Maps test user secret is required.");
        using RenderingEventListener listener = new(
            "VectorStyleCompatibilityIssue", "VectorPolygonRenderBatch",
            "VectorTileCommitSummary", "TileRequestFailed", "RendererFailure");
        await MapControlTestHost.LoadMapControlAsync(
            new BasicGeoposition { Latitude = 47.6162, Longitude = -122.337 },
            zoom, async map =>
            {
                if (overview)
                {
                    map.Width = 1600;
                    map.Height = 1000;
                    map.Center = new Geopoint(new BasicGeoposition
                    {
                        Latitude = zoom == 19 ? 47.6092 : 47.6168,
                        Longitude = zoom == 19 ? -122.3350 : -122.3382,
                    });
                    map.UpdateLayout();
                }
                map.Language = "en-US";
                map.MapServiceToken = token;
                map.MapStyle = MapStyle.Road;
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
                MapRenderFrame frame = await map.CaptureRenderedFrameAsync(timeout.Token);
                string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                    "..", "..", "..", "..", "..", "artifacts", "azure-buildings"));
                Directory.CreateDirectory(directory);
                string suffix = zoom == 19 ? "rainier-current" :
                    overview ? "overview-current" : zoom == 18 ? "current" : "22-current";
                await frame.SavePngAsync(Path.Combine(directory, $"seattle-{suffix}.png"));
                string[] names = ["VectorStyleCompatibilityIssue", "VectorPolygonRenderBatch",
                    "VectorTileCommitSummary", "TileRequestFailed", "RendererFailure"];
                await File.WriteAllLinesAsync(Path.Combine(directory, $"events-{suffix}.txt"),
                    names.SelectMany(name => listener.Events(name).Select(e =>
                        name + ": " + string.Join(", ", e.Payload))));
                Assert.IsEmpty(listener.Events("TileRequestFailed"));
                Assert.IsEmpty(listener.Events("RendererFailure"));
                Assert.IsNotEmpty(listener.Events("VectorPolygonRenderBatch"));
                Assert.AreEqual(0, Convert.ToInt32(listener.Events("VectorPolygonRenderBatch").Last().Payload[4]));
            });
    }

    [TestMethod]
    public async Task InspectRainierTileClippingEdges()
    {
        string? token = new ConfigurationBuilder()
            .AddUserSecrets<AzureBuildingFootprintTests>(optional: true).Build()
            ["AzureMaps:MapServiceToken"];
        if (string.IsNullOrWhiteSpace(token))
            Assert.Inconclusive("Azure Maps test user secret is required.");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        List<string> edges = ["tileX,tileY,axis,coordinate,start,end"];
        for (int y = 183096; y <= 183098; y++)
        for (int x = 83979; x <= 83981; x++)
        {
            using PooledByteBuffer data = await AzureTileAcquisitionSession.GetStyleAssetAsync(
                FormattableString.Invariant($"map/tile?api-version=2024-04-01&tilesetId=microsoft.base&zoom=19&x={x}&y={y}&language=en-US"),
                token!, "application/vnd.mapbox-vector-tile", 4 * 1024 * 1024, timeout.Token);
            foreach (VectorTileFeature feature in VectorTileDecoder.Decode(data.Memory.Span).Features)
            {
                if (feature.SourceLayer != "footprint")
                    continue;
                foreach (VectorTilePolygon polygon in feature.Polygons)
                foreach (VectorTileRing ring in polygon.Rings)
                for (int index = 1; index < ring.Points.Length; index++)
                {
                    VectorTilePoint a = ring.Points[index - 1], b = ring.Points[index];
                    if (a.X == b.X && Math.Abs(a.Y - b.Y) > 0.05)
                        edges.Add(FormattableString.Invariant($"{x},{y},x,{a.X},{a.Y},{b.Y}"));
                    if (a.Y == b.Y && Math.Abs(a.X - b.X) > 0.05)
                        edges.Add(FormattableString.Invariant($"{x},{y},y,{a.Y},{a.X},{b.X}"));
                }
            }
        }
        string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "artifacts", "azure-buildings"));
        Directory.CreateDirectory(directory);
        await File.WriteAllLinesAsync(Path.Combine(directory, "rainier-clip-edges.csv"), edges, timeout.Token);
        Assert.IsGreaterThan(1, edges.Count);
    }

    [TestMethod]
    public async Task AzureRoadMvtFootprintsResolveAtTheirActualStyleThreshold()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddUserSecrets<AzureBuildingFootprintTests>(optional: true).Build();
        string? token = configuration["AzureMaps:MapServiceToken"];
        if (string.IsNullOrWhiteSpace(token))
            Assert.Inconclusive("Azure Maps test user secret is required.");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        using PooledByteBuffer json = await AzureTileAcquisitionSession.GetStyleAssetAsync(
            AzureVectorStyleProvider.GetAssetPaths(MapStyle.Road).Style,
            token!, "application/json", 4 * 1024 * 1024, timeout.Token);
        using JsonDocument document = JsonDocument.Parse(json.Memory);
        string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "artifacts", "azure-buildings"));
        Directory.CreateDirectory(directory);
        JsonElement footprintLayer = document.RootElement.GetProperty("layers").EnumerateArray()
            .Single(layer => layer.GetProperty("type").GetString() == "fill" &&
                layer.TryGetProperty("source-layer", out JsonElement sourceLayer) &&
                sourceLayer.GetString() == "footprint");
        Assert.AreEqual(15, footprintLayer.GetProperty("minzoom").GetDouble());
        Assert.IsFalse(footprintLayer.TryGetProperty("filter", out _));
        using PooledByteBuffer metadata = await AzureTileAcquisitionSession.GetStyleAssetAsync(
            "map/tileset?api-version=2024-04-01&tilesetId=microsoft.base",
            token!, "application/json", 4 * 1024 * 1024, timeout.Token);
        using JsonDocument sourceMetadata = JsonDocument.Parse(metadata.Memory);
        int maximumSourceZoom = sourceMetadata.RootElement.GetProperty("maxzoom").GetInt32();
        Assert.IsGreaterThanOrEqualTo(18, maximumSourceZoom);
        await File.WriteAllTextAsync(Path.Combine(directory, "source-zoom-bounds.txt"),
            $"Service maximum source zoom: {maximumSourceZoom}; native maximum: {AzureTileAcquisitionSession.GetMaximumTileZoom(MapStyle.Road)}",
            timeout.Token);
        List<string> sourceZoomResults = [];
        foreach (int sourceZoom in new[] { 14, 15, 16, 17, 21, 22 })
        {
            int sourceX = (int)Math.Floor(41988.5 * Math.Pow(2, sourceZoom - 18));
            int sourceY = (int)Math.Floor(91541.5 * Math.Pow(2, sourceZoom - 18));
            try
            {
                using PooledByteBuffer sourceTile = await AzureTileAcquisitionSession.GetStyleAssetAsync(
                    FormattableString.Invariant($"map/tile?api-version=2024-04-01&tilesetId=microsoft.base&zoom={sourceZoom}&x={sourceX}&y={sourceY}&language=en-US"),
                    token!, "application/vnd.mapbox-vector-tile", 4 * 1024 * 1024, timeout.Token);
                VectorTileFeatureCollection sourceFeatures = VectorTileDecoder.Decode(sourceTile.Memory.Span);
                int count = sourceFeatures.Features.Where(f => f.SourceLayer == "footprint")
                    .Sum(f => f.Polygons.Length);
                sourceZoomResults.Add($"{sourceZoom},200,{count}");
            }
            catch (AzureMapsRequestException exception) when (sourceZoom > maximumSourceZoom)
            {
                sourceZoomResults.Add($"{sourceZoom},{(int)exception.StatusCode},0");
            }
        }
        await File.WriteAllLinesAsync(Path.Combine(directory, "source-zoom-footprints.csv"),
            sourceZoomResults, timeout.Token);
        using PooledByteBuffer mvt = await AzureTileAcquisitionSession.GetStyleAssetAsync(
            "map/tile?api-version=2024-04-01&tilesetId=microsoft.base&zoom=18&x=41988&y=91541&language=en-US",
            token!, "application/vnd.mapbox-vector-tile", 4 * 1024 * 1024, timeout.Token);
        VectorTileFeatureCollection decoded = VectorTileDecoder.Decode(mvt.Memory.Span);
        await File.WriteAllLinesAsync(Path.Combine(directory, "mvt-polygon-counts-local.txt"),
            decoded.Features.Where(f => f.Polygons.Length > 0).GroupBy(f => f.SourceLayer)
                .Select(group => $"{group.Key}: {group.Count()} features, {group.Sum(f => f.Polygons.Length)} polygons"),
            timeout.Token);
        VectorTileFeatureCollection footprints = new(decoded.Features.Where(f =>
            f.SourceLayer == "footprint" && f.Polygons.Length > 0).ToArray());
        Assert.IsNotEmpty(footprints.Features);
        VectorStyleAssets assets = VectorStyleAssets.CreateForTest(MapStyle.Road,
            json.Memory, "{}"u8.ToArray(), [0, 0, 0, 0], 1, 1);
        List<string> results = [];
        foreach (double zoom in new[] { 14.99, 15, 15.01, 16, 18, 20, 22 })
        {
            VectorPolygonResolution resolution = assets.ResolvePolygons(footprints, zoom);
            Assert.AreEqual(0, resolution.EvaluationFailureCount);
            Assert.AreEqual(zoom >= 15, resolution.Polygons.Length > 0);
            results.Add(FormattableString.Invariant(
                $"{zoom},{footprints.PolygonCount},{resolution.Polygons.Length},{resolution.EvaluationFailureCount}"));
        }
        await File.WriteAllLinesAsync(Path.Combine(directory, "azure-footprint-resolution.csv"), results, timeout.Token);
    }
}
