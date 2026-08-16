using System.Text;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Benchmarks;

internal sealed record VectorSymbolBenchmarkFixture(
    VectorTileFeatureCollection Features,
    VectorStyleAssets StyleAssets,
    VectorSpriteTextureData[] Textures)
{
    private const string FontStack = "BenchmarkFont";

    internal static VectorSymbolBenchmarkFixture Create(int symbolCount)
    {
        if (symbolCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(symbolCount));
        }

        byte[] spritePixels = CreateSpritePixels(20, 20);
        VectorStyleAssets styleAssets = VectorStyleAssets.CreateForTest(
            MapStyle.Road,
            CreateStyle(),
            """
            {
              "marker": {
                "x": 0,
                "y": 0,
                "width": 20,
                "height": 20,
                "pixelRatio": 1,
                "visible": true
              }
            }
            """u8.ToArray(),
            spritePixels,
            20,
            20);
        styleAssets.GlyphAtlas.AddRangeForTest(new VectorGlyphRange(
            FontStack,
            0,
            CreateGlyphs()));

        VectorTileFeatureCollection features = CreateFeatures(symbolCount);
        VectorSpriteTextureData[] textures = styleAssets
            .PrepareTexturesAsync(features, 14, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        return new(features, styleAssets, textures);
    }

    private static VectorTileFeatureCollection CreateFeatures(int symbolCount)
    {
        int columns = (int)Math.Ceiling(Math.Sqrt(symbolCount));
        int rows = (int)Math.Ceiling((double)symbolCount / columns);
        VectorTileFeature[] features = new VectorTileFeature[symbolCount];
        for (int index = 0; index < symbolCount; index++)
        {
            int column = index % columns;
            int row = index / columns;
            double x = (column + 0.5) / columns;
            double y = (row + 0.5) / rows;
            string name = $"MAP {index % 100:00}";
            features[index] = new VectorTileFeature(
                "poi",
                VectorTileGeometryType.Point,
                [new VectorTilePoint(x, y)],
                [
                    new VectorTileProperty(
                        "name",
                        VectorTileValue.FromString(name)),
                    new VectorTileProperty(
                        "rank",
                        VectorTileValue.FromInt(index)),
                ],
                [],
                []);
        }
        return new VectorTileFeatureCollection(features);
    }

    private static byte[] CreateStyle() => Encoding.UTF8.GetBytes(
        $$"""
        {
          "version": 8,
          "sources": {
            "microsoft.base": {
              "type": "vector",
              "url": "https://atlas.microsoft.com/map/tile?tilesetId=microsoft.base"
            }
          },
          "layers": [{
            "id": "poi-labels",
            "type": "symbol",
            "source": "microsoft.base",
            "source-layer": "poi",
            "layout": {
              "icon-image": "marker",
              "icon-size": 1,
              "text-field": "{name}",
              "text-font": ["{{FontStack}}"],
              "text-size": 16,
              "text-offset": [0, 1.25],
              "symbol-sort-key": ["get", "rank"]
            },
            "paint": {
              "text-color": "#18324A",
              "text-halo-color": "#FFFFFFD0",
              "text-halo-width": 1.5,
              "text-halo-blur": 0.5,
              "icon-color": "#2D7DD2"
            }
          }]
        }
        """);

    private static Dictionary<int, VectorGlyph> CreateGlyphs()
    {
        const string characters = "MAP 0123456789";
        return characters
            .Distinct()
            .ToDictionary(
                character => (int)character,
                character => CreateGlyph(character));
    }

    private static VectorGlyph CreateGlyph(char character)
    {
        const uint width = 8;
        const uint height = 10;
        int textureWidth = checked((int)(width + (VectorGlyph.SdfBuffer * 2)));
        int textureHeight = checked((int)(height + (VectorGlyph.SdfBuffer * 2)));
        byte[] bitmap = new byte[checked(textureWidth * textureHeight)];
        int seed = character;
        for (int y = 0; y < textureHeight; y++)
        {
            for (int x = 0; x < textureWidth; x++)
            {
                int edgeDistance = Math.Min(
                    Math.Min(x, textureWidth - 1 - x),
                    Math.Min(y, textureHeight - 1 - y));
                int pattern = ((x * 17) + (y * 31) + seed) & 31;
                bitmap[(y * textureWidth) + x] = checked((byte)Math.Clamp(
                    80 + (edgeDistance * 32) + pattern,
                    0,
                    255));
            }
        }
        return new VectorGlyph(
            character,
            bitmap,
            width,
            height,
            0,
            checked((int)height),
            width + 1);
    }

    private static byte[] CreateSpritePixels(int width, int height)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 4;
                bool border = x < 2 || y < 2 || x >= width - 2 || y >= height - 2;
                pixels[offset] = border ? (byte)80 : (byte)210;
                pixels[offset + 1] = border ? (byte)45 : (byte)125;
                pixels[offset + 2] = border ? (byte)20 : (byte)45;
                pixels[offset + 3] = 255;
            }
        }
        return pixels;
    }
}
