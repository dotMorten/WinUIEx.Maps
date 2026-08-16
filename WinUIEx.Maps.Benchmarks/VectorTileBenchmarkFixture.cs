using System.Text;
using System.Text.Json;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Benchmarks;

internal sealed record VectorTileBenchmarkFixture(
    TileId Id,
    byte[] Encoded,
    VectorTileFeatureCollection Features,
    VectorStyleAssets StyleAssets)
{
    internal static byte[] LoadEncoded(VectorTileFixture fixture)
    {
        string fileName = fixture switch
        {
            VectorTileFixture.NewYorkZ10 => "new-york-z10.pbf",
            VectorTileFixture.SeattleZ12 => "seattle-z12.pbf",
            VectorTileFixture.NewYorkZ14 => "new-york-z14.pbf",
            VectorTileFixture.TokyoZ16 => "tokyo-z16.pbf",
            _ => throw new ArgumentOutOfRangeException(nameof(fixture)),
        };
        string resourceName =
            $"{typeof(VectorTileBenchmarkFixture).Namespace}.Fixtures.{fileName}";
        using Stream stream = typeof(VectorTileBenchmarkFixture).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"The embedded vector-tile fixture '{resourceName}' was not found.");
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    internal static VectorTileBenchmarkFixture Load(VectorTileFixture fixture)
    {
        TileId id = fixture switch
        {
            VectorTileFixture.NewYorkZ10 => new(10, 301, 385),
            VectorTileFixture.SeattleZ12 => new(12, 656, 1430),
            VectorTileFixture.NewYorkZ14 => new(14, 4823, 6160),
            VectorTileFixture.TokyoZ16 => new(16, 58198, 25804),
            _ => throw new ArgumentOutOfRangeException(nameof(fixture)),
        };
        byte[] encoded = LoadEncoded(fixture);
        VectorTileFeatureCollection features = VectorTileDecoder.Decode(encoded);
        byte[] styleJson = CreateStyle(features);
        VectorStyleAssets styleAssets = VectorStyleAssets.CreateForTest(
            MapStyle.Road,
            styleJson,
            "{}"u8.ToArray(),
            [0, 0, 0, 0],
            1,
            1);
        return new VectorTileBenchmarkFixture(
            id,
            encoded,
            features,
            styleAssets);
    }

    private static byte[] CreateStyle(VectorTileFeatureCollection features)
    {
        StringBuilder layers = new();
        int order = 0;
        foreach (IGrouping<string, VectorTileFeature> sourceLayer in
            features.Features.GroupBy(
                feature => feature.SourceLayer,
                StringComparer.Ordinal))
        {
            string encodedName = JsonSerializer.Serialize(sourceLayer.Key);
            if (sourceLayer.Any(feature =>
                feature.GeometryType is
                    VectorTileGeometryType.Polygon or
                    VectorTileGeometryType.MultiPolygon))
            {
                AppendSeparator(layers);
                layers.Append(
                    $"{{\"id\":\"fill-{order++}\",\"type\":\"fill\"," +
                    $"\"source\":\"microsoft.base\",\"source-layer\":{encodedName}," +
                    "\"paint\":{\"fill-color\":\"#739b68\",\"fill-opacity\":0.65}}");
            }
            if (sourceLayer.Any(feature =>
                feature.GeometryType is
                    VectorTileGeometryType.LineString or
                    VectorTileGeometryType.MultiLineString))
            {
                AppendSeparator(layers);
                layers.Append(
                    $"{{\"id\":\"line-{order++}\",\"type\":\"line\"," +
                    $"\"source\":\"microsoft.base\",\"source-layer\":{encodedName}," +
                    "\"paint\":{\"line-color\":\"#375a7f\",\"line-width\":2}}");
            }
        }

        return Encoding.UTF8.GetBytes(
            $$"""
            {
              "version": 8,
              "sources": {
                "microsoft.base": {
                  "type": "vector",
                  "url": "https://atlas.microsoft.com/map/tile?tilesetId=microsoft.base"
                }
              },
              "layers": [
                {
                  "id": "background",
                  "type": "background",
                  "paint": { "background-color": "#eef1f4" }
                }{{(layers.Length == 0 ? string.Empty : ",")}}
                {{layers}}
              ]
            }
            """);
    }

    private static void AppendSeparator(StringBuilder builder)
    {
        if (builder.Length != 0)
        {
            builder.Append(',');
        }
    }
}
