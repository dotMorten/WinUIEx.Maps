using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class VectorTileDecoderTests
{
    [TestMethod]
    public void DecoderPreservesSourceLayerTagsAndAllSupportedValueTypes()
    {
        string[] keys = ["name", "float", "double", "int", "uint", "sint", "bool"];
        byte[][] values =
        [
            StringValue("station"),
            FloatValue(1.25f),
            DoubleValue(2.5),
            IntValue(-5),
            UIntValue(6),
            SIntValue(-7),
            BoolValue(true),
        ];
        uint[] tags = Enumerable.Range(0, keys.Length)
            .SelectMany(index => new[] { (uint)index, (uint)index })
            .ToArray();
        byte[] tile = Tile(
            Layer(
                "transit",
                4096,
                keys,
                values,
                Feature(tags, 1, MoveTo((1024, 2048), (2048, 1024)))),
            Layer(
                "roads",
                4096,
                [],
                [],
                Feature(
                    [],
                    2,
                    LineGeometry((1024, 1024), (2048, 1024)))));

        VectorTileFeatureCollection decoded = VectorTileDecoder.Decode(tile);
        VectorTileFeature feature = Assert.ContainsSingle(
            decoded.Features.Where(item => item.Points.Length != 0));
        VectorTileFeature line = Assert.ContainsSingle(
            decoded.Features.Where(item => item.Lines.Length != 0));

        Assert.AreEqual("transit", feature.SourceLayer);
        Assert.AreEqual(VectorTileGeometryType.MultiPoint, feature.GeometryType);
        Assert.AreSequenceEqual(
            [new VectorTilePoint(0.25, 0.5), new VectorTilePoint(0.5, 0.25)],
            feature.Points);
        AssertProperty(feature, "name", VectorTileValueKind.String, "station");
        AssertProperty(feature, "float", VectorTileValueKind.Float, 1.25);
        AssertProperty(feature, "double", VectorTileValueKind.Double, 2.5);
        AssertProperty(feature, "int", VectorTileValueKind.Int, -5L);
        AssertProperty(feature, "uint", VectorTileValueKind.UInt, 6UL);
        AssertProperty(feature, "sint", VectorTileValueKind.SInt, -7L);
        AssertProperty(feature, "bool", VectorTileValueKind.Bool, true);
        Assert.AreEqual(VectorTileGeometryType.LineString, line.GeometryType);
        Assert.AreSequenceEqual(
            [
                new VectorTilePoint(0.25, 0.25),
                new VectorTilePoint(0.5, 0.25),
            ],
            Assert.ContainsSingle(line.Lines).Points);
        Assert.AreEqual(1, decoded.LineCount);
        Assert.AreEqual(2, decoded.LinePointCount);
    }

    [TestMethod]
    public void DecoderPreservesMultipleLineStrings()
    {
        byte[] tile = Tile(Layer(
            "roads",
            4096,
            [],
            [],
            Feature(
                [],
                2,
                MultiLineGeometry(
                    [(100, 200), (300, 400)],
                    [(500, 600), (700, 800), (900, 1000)]))));

        VectorTileFeature feature = Assert.ContainsSingle(
            VectorTileDecoder.Decode(tile).Features);

        Assert.AreEqual(VectorTileGeometryType.MultiLineString, feature.GeometryType);
        Assert.HasCount(2, feature.Lines);
        Assert.AreSequenceEqual(
            [new VectorTilePoint(100d / 4096, 200d / 4096),
             new VectorTilePoint(300d / 4096, 400d / 4096)],
            feature.Lines[0].Points);
        Assert.AreSequenceEqual(
            [new VectorTilePoint(500d / 4096, 600d / 4096),
             new VectorTilePoint(700d / 4096, 800d / 4096),
             new VectorTilePoint(900d / 4096, 1000d / 4096)],
            feature.Lines[1].Points);
    }

    [TestMethod]
    public void DecoderTessellatesPolygonWithHole()
    {
        byte[] tile = Tile(Layer(
            "land",
            100,
            [],
            [],
            Feature(
                [],
                3,
                PolygonGeometry(
                    [(0, 0), (100, 0), (100, 100), (0, 100)],
                    [(25, 25), (25, 75), (75, 75), (75, 25)]))));

        VectorTileFeatureCollection decoded = VectorTileDecoder.Decode(tile);
        VectorTileFeature feature = Assert.ContainsSingle(decoded.Features);
        VectorTilePolygon polygon = Assert.ContainsSingle(feature.Polygons);

        Assert.AreEqual(VectorTileGeometryType.Polygon, feature.GeometryType);
        Assert.HasCount(2, polygon.Rings);
        Assert.IsGreaterThan(0, polygon.FillTriangles.Length);
        Assert.AreEqual(0.75, TriangleArea(polygon.FillTriangles), 0.000001);
        Assert.AreEqual(1, decoded.PolygonCount);
        Assert.AreEqual(polygon.FillTriangles.Length / 3, decoded.PolygonTriangleCount);
    }

    [TestMethod]
    public void DecoderPreservesMultiplePolygons()
    {
        byte[] tile = Tile(Layer(
            "land",
            100,
            [],
            [],
            Feature(
                [],
                3,
                PolygonGeometry(
                    [(0, 0), (20, 0), (20, 20), (0, 20)],
                    [(50, 50), (80, 50), (80, 80), (50, 80)]))));

        VectorTileFeature feature = Assert.ContainsSingle(
            VectorTileDecoder.Decode(tile).Features);

        Assert.AreEqual(VectorTileGeometryType.MultiPolygon, feature.GeometryType);
        Assert.HasCount(2, feature.Polygons);
    }

    [TestMethod]
    public void DecoderRejectsUnclosedPolygonRing()
    {
        byte[] tile = Tile(Layer(
            "land",
            100,
            [],
            [],
            Feature(
                [],
                3,
                MultiLineGeometry(
                    [(0, 0), (100, 0), (100, 100), (0, 100)]))));

        Assert.ThrowsExactly<InvalidDataException>(
            () => VectorTileDecoder.Decode(tile));
    }

    [TestMethod]
    public void DecoderRejectsInteriorRingBeforeExterior()
    {
        byte[] tile = Tile(Layer(
            "land",
            100,
            [],
            [],
            Feature(
                [],
                3,
                PolygonGeometry(
                    [(25, 25), (25, 75), (75, 75), (75, 25)]))));

        Assert.ThrowsExactly<InvalidDataException>(
            () => VectorTileDecoder.Decode(tile));
    }

    [TestMethod]
    public void DecoderRejectsDegeneratePolygonRing()
    {
        byte[] tile = Tile(Layer(
            "land",
            100,
            [],
            [],
            Feature(
                [],
                3,
                PolygonGeometry(
                    [(0, 0), (25, 0), (50, 0)]))));

        Assert.ThrowsExactly<InvalidDataException>(
            () => VectorTileDecoder.Decode(tile));
    }

    [TestMethod]
    public void DecoderUsesLayerMetadataEvenWhenItFollowsFeatures()
    {
        byte[] feature = Feature([], 1, MoveTo((256, 768)));
        List<byte> layer = [];
        WriteMessage(layer, 2, feature);
        WriteString(layer, 1, "poi");
        WriteVarintField(layer, 5, 1024);

        VectorTileFeature decoded = Assert.ContainsSingle(
            VectorTileDecoder.Decode(Tile(layer.ToArray())).Features);

        Assert.AreEqual("poi", decoded.SourceLayer);
        Assert.AreEqual(new VectorTilePoint(0.25, 0.75), decoded.Points[0]);
    }

    [TestMethod]
    public void DecoderRejectsOutOfRangePropertyTags()
    {
        byte[] tile = Tile(Layer(
            "poi",
            4096,
            ["name"],
            [StringValue("value")],
            Feature([1, 0], 1, MoveTo((1, 1)))));

        Assert.ThrowsExactly<InvalidDataException>(
            () => VectorTileDecoder.Decode(tile));
    }

    [TestMethod]
    public void DecoderRejectsTruncatedPayload()
    {
        Assert.ThrowsExactly<InvalidDataException>(
            () => VectorTileDecoder.Decode([0x1a, 0x05, 0x0a]));
    }

    [TestMethod]
    public void DecoderObservesCancellation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(
            () => VectorTileDecoder.Decode(
                Tile(Layer(
                    "poi",
                    4096,
                    [],
                    [],
                    Feature([], 1, MoveTo((1, 1))))),
                cancellation.Token));
    }

    [TestMethod]
    public void SymbolsAreProjectedWithDisplayDimensionsAndOffset()
    {
        VisibleTile tile = new(
            new TileId(2, 1, 1),
            1,
            100,
            50,
            256);
        VectorTileSymbol symbol = new(
            3,
            0.5,
            0.25,
            -10,
            20,
            10,
            3,
            -2);

        VectorSymbolPlacement placement = Assert.ContainsSingle(
            MapRenderer.ProjectVectorSymbols(
                [symbol],
                tile,
                640,
                480,
                0,
                0));

        Assert.AreEqual(
            new VectorSymbolPlacement(3, -10, 221, 107, 20, 10),
            placement);
    }

    [TestMethod]
    public void ProjectedSymbolsCullOutsideViewport()
    {
        VisibleTile tile = new(
            new TileId(2, 1, 1),
            1,
            1000,
            1000,
            256);

        Assert.IsEmpty(MapRenderer.ProjectVectorSymbols(
            [new VectorTileSymbol(0, 0.5, 0.5, -1, 16, 16, 0, 0)],
            tile,
            640,
            480,
            0,
            0));
    }

    [TestMethod]
    public void LineSymbolsRepeatAlongPathAndRemainUpright()
    {
        VisibleTile tile = new(
            new TileId(2, 1, 1),
            1,
            100,
            50,
            256);
        VectorTilePoint[] forward =
        [
            new VectorTilePoint(0.1, 0.5),
            new VectorTilePoint(0.9, 0.5),
        ];
        VectorSymbolPlacement[] repeated = MapRenderer.ProjectVectorSymbols(
            [new VectorTileSymbol(
                3,
                0,
                0,
                -1,
                20,
                10,
                0,
                0,
                VectorSymbolKind.Text,
                default,
                7,
                forward,
                100)],
            tile,
            640,
            480,
            0,
            0);
        VectorSymbolPlacement reversed = Assert.ContainsSingle(
            MapRenderer.ProjectVectorSymbols(
                [new VectorTileSymbol(
                    3,
                    0,
                    0,
                    -1,
                    20,
                    10,
                    0,
                    0,
                    VectorSymbolKind.Text,
                    default,
                    8,
                    forward.Reverse().ToArray(),
                    500)],
                tile,
                640,
                480,
                0,
                0));

        Assert.HasCount(2, repeated);
        Assert.AreEqual(0, repeated[0].PlacementIndex);
        Assert.AreEqual(1, repeated[1].PlacementIndex);
        Assert.AreEqual(0, repeated[0].Rotation, 0.000001);
        Assert.AreEqual(0, reversed.Rotation, 0.000001);
    }

    [TestMethod]
    public void LineSymbolsFollowGentleCurvesButRejectDistortedBends()
    {
        VisibleTile tile = new(
            new TileId(2, 1, 1),
            1,
            100,
            50,
            256);
        VectorTileSymbol[] gentleSymbols = CreateLineGlyphs(
            [
                new VectorTilePoint(0.1, 0.55),
                new VectorTilePoint(0.3, 0.5),
                new VectorTilePoint(0.5, 0.48),
                new VectorTilePoint(0.7, 0.5),
                new VectorTilePoint(0.9, 0.55),
            ]);
        VectorTileSymbol[] sharpSymbols = CreateLineGlyphs(
            [
                new VectorTilePoint(0.1, 0.2),
                new VectorTilePoint(0.5, 0.2),
                new VectorTilePoint(0.5, 0.8),
            ]);

        VectorSymbolPlacement[] gentle = MapRenderer.ProjectVectorSymbols(
            gentleSymbols,
            tile,
            640,
            480,
            0,
            0);
        VectorSymbolPlacement[] sharp = MapRenderer.ProjectVectorSymbols(
            sharpSymbols,
            tile,
            640,
            480,
            0,
            0);

        Assert.HasCount(4, gentle);
        Assert.IsTrue(gentle.Any(
            placement => Math.Abs(placement.Rotation) > 0.01));
        Assert.IsEmpty(sharp);
    }

    [TestMethod]
    public void VectorLinesProjectAndExpandWithRoundCaps()
    {
        VisibleTile tile = new(
            new TileId(2, 1, 1),
            1,
            100,
            50,
            256);
        MapScreenPoint[] projected = MapRenderer.ProjectVectorLine(
            [new VectorTilePoint(0.25, 0.5),
             new VectorTilePoint(0.75, 0.5)],
            tile,
            640,
            480,
            0,
            0);

        Assert.AreSequenceEqual(
            [new MapScreenPoint(164, 178), new MapScreenPoint(292, 178)],
            projected);
        MapScreenPoint[] triangles = MapRenderer.ExpandVectorLineTriangles(
            projected,
            new VectorLineStyle(
                new(1, 0, 0, 1),
                4,
                VectorLineCap.Round,
                VectorLineJoin.Round),
            640,
            480);

        Assert.HasCount(54, triangles);
    }

    private static VectorTileSymbol[] CreateLineGlyphs(
        VectorTilePoint[] linePoints)
    {
        return new[] { -30d, -10d, 10d, 30d }
            .Select((offset, index) => new VectorTileSymbol(
                3,
                0,
                0,
                index,
                12,
                16,
                offset,
                0,
                VectorSymbolKind.Text,
                default,
                7,
                linePoints,
                500))
            .ToArray();
    }

    [TestMethod]
    public void SubpixelVectorLinesPreserveCoverageAtOnePixel()
    {
        VectorLineStyle style = new(
            new(0.4f, 0.2f, 0.1f, 0.5f),
            0.25,
            VectorLineCap.Butt,
            VectorLineJoin.Bevel);

        VectorLineStyle rasterStyle =
            MapRenderer.PrepareVectorLineForRasterization(style);

        Assert.AreEqual(1, rasterStyle.Width, 0.000001);
        Assert.AreEqual(0.1f, rasterStyle.Color.X, 0.000001);
        Assert.AreEqual(0.05f, rasterStyle.Color.Y, 0.000001);
        Assert.AreEqual(0.025f, rasterStyle.Color.Z, 0.000001);
        Assert.AreEqual(0.125f, rasterStyle.Color.W, 0.000001);
        Assert.AreEqual(
            style with { Width = 2 },
            MapRenderer.PrepareVectorLineForRasterization(
                style with { Width = 2 }));
    }

    [TestMethod]
    public void VectorGeometryFallbackCrossfadesByDistanceAndReplacement()
    {
        Assert.AreEqual(
            1,
            MapRenderer.ComputeVectorGeometryFallbackOpacity(13, 12, 0),
            0.000001);
        Assert.AreEqual(
            0.5,
            MapRenderer.ComputeVectorGeometryFallbackOpacity(11.5, 10, 0),
            0.000001);
        Assert.AreEqual(
            0.25,
            MapRenderer.ComputeVectorGeometryFallbackOpacity(11.5, 10, 0.5),
            0.000001);
        Assert.AreEqual(
            0,
            MapRenderer.ComputeVectorGeometryFallbackOpacity(13, 10, 0),
            0.000001);
        Assert.AreEqual(
            0,
            MapRenderer.ComputeVectorGeometryFallbackOpacity(10.86, 10, 1),
            0.000001);
        Assert.AreEqual(
            0,
            MapRenderer.ComputeVectorGeometryFallbackOpacity(
                double.NaN,
                10,
                0),
            0.000001);
    }

    [TestMethod]
    public void VectorPolygonTrianglesProjectAndCullOutsideViewport()
    {
        VisibleTile tile = new(
            new TileId(2, 1, 1),
            1,
            100,
            50,
            256);

        MapScreenPoint[] projected = MapRenderer.ProjectVectorPolygonTriangles(
            [new VectorTilePoint(0.25, 0.25),
             new VectorTilePoint(0.75, 0.25),
             new VectorTilePoint(0.5, 0.75)],
            tile,
            640,
            480,
            0,
            0);
        MapScreenPoint[] culled = MapRenderer.ProjectVectorPolygonTriangles(
            [new VectorTilePoint(0, 0),
             new VectorTilePoint(1, 0),
             new VectorTilePoint(0, 1)],
            tile with { Left = 1000, Top = 1000 },
            640,
            480,
            0,
            0);

        Assert.AreSequenceEqual(
            [new MapScreenPoint(164, 114),
             new MapScreenPoint(292, 114),
             new MapScreenPoint(228, 242)],
            projected);
        Assert.IsEmpty(culled);
    }

    [TestMethod]
    public void SymbolBatchesGroupTexturesInFirstSeenOrder()
    {
        VectorSymbolBatch[] batches = MapRenderer.BatchVectorSymbolsByTexture(
        [
            new(4, -2, 0, 0, 10, 10),
            new(4, -1, 10, 0, 10, 10),
            new(4, -2, 20, 0, 10, 10),
            new(5, -2, 30, 0, 10, 10),
        ]);

        Assert.AreEqual(3, batches.Length);
        Assert.AreEqual(4, batches[0].StyleLayerOrder);
        Assert.AreEqual(-2, batches[0].TextureId);
        Assert.AreEqual(2, batches[0].Placements.Length);
        Assert.AreEqual(-1, batches[1].TextureId);
        Assert.AreEqual(1, batches[1].Placements.Length);
        Assert.AreEqual(5, batches[2].StyleLayerOrder);
        Assert.AreEqual(-2, batches[2].TextureId);
        Assert.AreEqual(1, batches[2].Placements.Length);
    }

    [TestMethod]
    public void LabelCollisionsPreferHigherStyleLayersAndKeepWholeLabels()
    {
        MapRenderer.LabelCollisionResult result =
            MapRenderer.ResolveLabelCollisions(
            [
                new(
                    2, -1, 0, 0, 8, 10, VectorSymbolKind.Text, default, 0, 10),
                new(
                    2, -2, 8, 0, 8, 10, VectorSymbolKind.Text, default, 0, 10),
                new(
                    5, -3, 4, 0, 8, 10, VectorSymbolKind.Text, default, 0, 20),
                new(
                    1, -4, 30, 0, 8, 10, VectorSymbolKind.Text, default, 0, 30),
                new(0, -5, 4, 0, 8, 10),
            ]);

        Assert.AreEqual(3, result.CandidateLabelCount);
        Assert.AreEqual(1, result.SuppressedLabelCount);
        Assert.AreEqual(2, result.SuppressedGlyphCount);
        Assert.IsFalse(result.AcceptedGroups.Contains(10));
        Assert.IsTrue(result.AcceptedGroups.Contains(20));
        Assert.IsTrue(result.AcceptedGroups.Contains(30));
    }

    [TestMethod]
    public void DecoderRejectsPropertyValuesWithMultipleTypes()
    {
        List<byte> value = [];
        WriteString(value, 1, "text");
        WriteVarintField(value, 5, 4);
        byte[] tile = Tile(Layer(
            "poi",
            4096,
            ["value"],
            [value.ToArray()],
            Feature([0, 0], 1, MoveTo((1, 1)))));

        Assert.ThrowsExactly<InvalidDataException>(
            () => VectorTileDecoder.Decode(tile));
    }

    [TestMethod]
    public void DecoderRejectsInvalidUtf8()
    {
        List<byte> layer = [];
        WriteMessage(layer, 1, [0xc3, 0x28]);
        WriteMessage(layer, 2, Feature([], 1, MoveTo((1, 1))));
        WriteVarintField(layer, 5, 4096);

        Assert.ThrowsExactly<InvalidDataException>(
            () => VectorTileDecoder.Decode(Tile(layer.ToArray())));
    }

    [TestMethod]
    public void DecoderRejectsPointFeaturesWithoutGeometry()
    {
        byte[] tile = Tile(Layer(
            "poi",
            4096,
            [],
            [],
            Feature([], 1)));

        Assert.ThrowsExactly<InvalidDataException>(
            () => VectorTileDecoder.Decode(tile));
    }

    [TestMethod]
    public void DecoderRejectsLineWithFewerThanTwoPoints()
    {
        byte[] tile = Tile(Layer(
            "roads",
            4096,
            [],
            [],
            Feature([], 2, MoveTo((1, 1)))));

        Assert.ThrowsExactly<InvalidDataException>(
            () => VectorTileDecoder.Decode(tile));
    }

    private static void AssertProperty(
        VectorTileFeature feature,
        string name,
        VectorTileValueKind kind,
        object expected)
    {
        Assert.IsTrue(feature.TryGetProperty(name, out VectorTileValue value));
        Assert.AreEqual(kind, value.Kind);
        object actual = kind switch
        {
            VectorTileValueKind.String => value.StringValue!,
            VectorTileValueKind.Float or VectorTileValueKind.Double =>
                value.FloatingValue,
            VectorTileValueKind.Int or VectorTileValueKind.SInt =>
                value.SignedValue,
            VectorTileValueKind.UInt => value.UnsignedValue,
            VectorTileValueKind.Bool => value.BoolValue,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        Assert.AreEqual(expected, actual);
    }

    private static byte[] Tile(params byte[][] layers)
    {
        List<byte> tile = [];
        foreach (byte[] layer in layers)
        {
            WriteMessage(tile, 3, layer);
        }
        return tile.ToArray();
    }

    private static byte[] Layer(
        string name,
        uint extent,
        string[] keys,
        byte[][] values,
        params byte[][] features)
    {
        List<byte> layer = [];
        WriteString(layer, 1, name);
        foreach (byte[] feature in features)
        {
            WriteMessage(layer, 2, feature);
        }
        foreach (string key in keys)
        {
            WriteString(layer, 3, key);
        }
        foreach (byte[] value in values)
        {
            WriteMessage(layer, 4, value);
        }
        WriteVarintField(layer, 5, extent);
        WriteVarintField(layer, 15, 2);
        return layer.ToArray();
    }

    private static byte[] Feature(
        uint[] tags,
        uint geometryType,
        params uint[] geometry)
    {
        List<byte> feature = [];
        if (tags.Length != 0)
        {
            List<byte> packedTags = [];
            foreach (uint tag in tags)
            {
                WriteVarint(packedTags, tag);
            }
            WriteMessage(feature, 2, packedTags.ToArray());
        }
        WriteVarintField(feature, 3, geometryType);
        List<byte> packedGeometry = [];
        foreach (uint value in geometry)
        {
            WriteVarint(packedGeometry, value);
        }
        WriteMessage(feature, 4, packedGeometry.ToArray());
        return feature.ToArray();
    }

    private static byte[] StringValue(string value)
    {
        List<byte> output = [];
        WriteString(output, 1, value);
        return output.ToArray();
    }

    private static byte[] FloatValue(float value)
    {
        List<byte> output = [(byte)((2 << 3) | 5)];
        output.AddRange(BitConverter.GetBytes(value));
        return output.ToArray();
    }

    private static byte[] DoubleValue(double value)
    {
        List<byte> output = [(byte)((3 << 3) | 1)];
        output.AddRange(BitConverter.GetBytes(value));
        return output.ToArray();
    }

    private static byte[] IntValue(long value)
    {
        List<byte> output = [];
        WriteVarintField(output, 4, unchecked((ulong)value));
        return output.ToArray();
    }

    private static byte[] UIntValue(ulong value)
    {
        List<byte> output = [];
        WriteVarintField(output, 5, value);
        return output.ToArray();
    }

    private static byte[] SIntValue(long value)
    {
        List<byte> output = [];
        WriteVarintField(
            output,
            6,
            unchecked((ulong)((value << 1) ^ (value >> 63))));
        return output.ToArray();
    }

    private static byte[] BoolValue(bool value)
    {
        List<byte> output = [];
        WriteVarintField(output, 7, value ? 1UL : 0);
        return output.ToArray();
    }

    private static uint[] MoveTo(params (int X, int Y)[] points)
    {
        List<uint> geometry = [(uint)((points.Length << 3) | 1)];
        int previousX = 0;
        int previousY = 0;
        foreach ((int x, int y) in points)
        {
            geometry.Add(EncodeZigZag(x - previousX));
            geometry.Add(EncodeZigZag(y - previousY));
            previousX = x;
            previousY = y;
        }
        return geometry.ToArray();
    }

    private static uint[] LineGeometry(params (int X, int Y)[] points) =>
        MultiLineGeometry(points);

    private static uint[] MultiLineGeometry(
        params (int X, int Y)[][] lines)
    {
        List<uint> geometry = [];
        int previousX = 0;
        int previousY = 0;
        foreach ((int X, int Y)[] line in lines)
        {
            if (line.Length == 0)
            {
                continue;
            }
            geometry.Add((1u << 3) | 1u);
            geometry.Add(EncodeZigZag(line[0].X - previousX));
            geometry.Add(EncodeZigZag(line[0].Y - previousY));
            previousX = line[0].X;
            previousY = line[0].Y;
            if (line.Length == 1)
            {
                continue;
            }
            geometry.Add((uint)(((line.Length - 1) << 3) | 2));
            for (int index = 1; index < line.Length; index++)
            {
                geometry.Add(EncodeZigZag(line[index].X - previousX));
                geometry.Add(EncodeZigZag(line[index].Y - previousY));
                previousX = line[index].X;
                previousY = line[index].Y;
            }
        }
        return geometry.ToArray();
    }

    private static uint[] PolygonGeometry(
        params (int X, int Y)[][] rings)
    {
        List<uint> geometry = [];
        int previousX = 0;
        int previousY = 0;
        foreach ((int X, int Y)[] ring in rings)
        {
            geometry.Add((1u << 3) | 1u);
            geometry.Add(EncodeZigZag(ring[0].X - previousX));
            geometry.Add(EncodeZigZag(ring[0].Y - previousY));
            previousX = ring[0].X;
            previousY = ring[0].Y;
            geometry.Add((uint)(((ring.Length - 1) << 3) | 2));
            for (int index = 1; index < ring.Length; index++)
            {
                geometry.Add(EncodeZigZag(ring[index].X - previousX));
                geometry.Add(EncodeZigZag(ring[index].Y - previousY));
                previousX = ring[index].X;
                previousY = ring[index].Y;
            }
            geometry.Add((1u << 3) | 7u);
        }
        return geometry.ToArray();
    }

    private static double TriangleArea(IReadOnlyList<VectorTilePoint> points)
    {
        double area = 0;
        for (int index = 0; index + 2 < points.Count; index += 3)
        {
            VectorTilePoint first = points[index];
            VectorTilePoint second = points[index + 1];
            VectorTilePoint third = points[index + 2];
            area += Math.Abs(
                ((second.X - first.X) * (third.Y - first.Y)) -
                ((second.Y - first.Y) * (third.X - first.X))) / 2;
        }
        return area;
    }

    private static uint EncodeZigZag(int value) =>
        (uint)((value << 1) ^ (value >> 31));

    private static void WriteString(
        List<byte> output,
        int fieldNumber,
        string value) =>
        WriteMessage(output, fieldNumber, Encoding.UTF8.GetBytes(value));

    private static void WriteMessage(
        List<byte> output,
        int fieldNumber,
        byte[] value)
    {
        WriteVarint(output, (ulong)((fieldNumber << 3) | 2));
        WriteVarint(output, (ulong)value.Length);
        output.AddRange(value);
    }

    private static void WriteVarintField(
        List<byte> output,
        int fieldNumber,
        ulong value)
    {
        WriteVarint(output, (ulong)(fieldNumber << 3));
        WriteVarint(output, value);
    }

    private static void WriteVarint(List<byte> output, ulong value)
    {
        do
        {
            byte current = (byte)(value & 0x7f);
            value >>= 7;
            if (value != 0)
            {
                current |= 0x80;
            }
            output.Add(current);
        }
        while (value != 0);
    }
}
