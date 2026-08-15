using System.Text;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests.UITestHelpers;

internal sealed class MapboxVectorTileBuilder
{
    private const uint Extent = 4096;
    private readonly Dictionary<string, LayerBuilder> _layers =
        new(StringComparer.Ordinal);

    internal MapboxVectorTileBuilder AddPoint(
        string sourceLayer,
        int x,
        int y,
        IReadOnlyDictionary<string, object>? properties = null)
    {
        GetLayer(sourceLayer).Features.Add(new FeatureBuilder(
            VectorTileGeometryType.Point,
            [new TestTilePoint(x, y)],
            null,
            properties));
        return this;
    }

    internal MapboxVectorTileBuilder AddLine(
        string sourceLayer,
        IReadOnlyList<TestTilePoint> points,
        IReadOnlyDictionary<string, object>? properties = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
        {
            throw new ArgumentException(
                "A vector-tile line requires at least two points.",
                nameof(points));
        }
        GetLayer(sourceLayer).Features.Add(new FeatureBuilder(
            VectorTileGeometryType.LineString,
            points.ToArray(),
            null,
            properties));
        return this;
    }

    internal MapboxVectorTileBuilder AddPolygon(
        string sourceLayer,
        IReadOnlyList<IReadOnlyList<TestTilePoint>> rings,
        IReadOnlyDictionary<string, object>? properties = null)
    {
        ArgumentNullException.ThrowIfNull(rings);
        if (rings.Count == 0 || rings.Any(ring => ring.Count < 3))
        {
            throw new ArgumentException(
                "A vector-tile polygon requires rings of at least three points.",
                nameof(rings));
        }
        GetLayer(sourceLayer).Features.Add(new FeatureBuilder(
            VectorTileGeometryType.Polygon,
            null,
            rings.Select(ring => ring.ToArray()).ToArray(),
            properties));
        return this;
    }

    internal byte[] Build()
    {
        List<byte> tile = [];
        foreach (LayerBuilder layer in _layers.Values)
        {
            WriteMessage(tile, 3, layer.Build());
        }
        return tile.ToArray();
    }

    private LayerBuilder GetLayer(string sourceLayer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLayer);
        if (!_layers.TryGetValue(sourceLayer, out LayerBuilder? layer))
        {
            layer = new LayerBuilder(sourceLayer);
            _layers.Add(sourceLayer, layer);
        }
        return layer;
    }

    private static byte[] EncodeGeometry(FeatureBuilder feature)
    {
        List<ulong> geometry = [];
        int cursorX = 0;
        int cursorY = 0;
        if (feature.GeometryType == VectorTileGeometryType.Point)
        {
            TestTilePoint[] points = feature.Points!;
            geometry.Add(Command(1, points.Length));
            foreach (TestTilePoint point in points)
            {
                geometry.Add(ZigZag(point.X - cursorX));
                geometry.Add(ZigZag(point.Y - cursorY));
                cursorX = point.X;
                cursorY = point.Y;
            }
        }
        else if (feature.GeometryType == VectorTileGeometryType.LineString)
        {
            WritePath(feature.Points!, close: false);
        }
        else
        {
            foreach (TestTilePoint[] ring in feature.Rings!)
            {
                WritePath(ring, close: true);
            }
        }

        List<byte> encoded = [];
        foreach (ulong value in geometry)
        {
            WriteVarint(encoded, value);
        }
        return encoded.ToArray();

        void WritePath(TestTilePoint[] points, bool close)
        {
            geometry.Add(Command(1, 1));
            geometry.Add(ZigZag(points[0].X - cursorX));
            geometry.Add(ZigZag(points[0].Y - cursorY));
            cursorX = points[0].X;
            cursorY = points[0].Y;
            geometry.Add(Command(2, points.Length - 1));
            for (int index = 1; index < points.Length; index++)
            {
                geometry.Add(ZigZag(points[index].X - cursorX));
                geometry.Add(ZigZag(points[index].Y - cursorY));
                cursorX = points[index].X;
                cursorY = points[index].Y;
            }
            if (close)
            {
                geometry.Add(Command(7, 1));
            }
        }
    }

    private static byte[] EncodeValue(object value)
    {
        List<byte> encoded = [];
        switch (value)
        {
            case string text:
                WriteString(encoded, 1, text);
                break;
            case bool boolean:
                WriteVarintField(encoded, 7, boolean ? 1UL : 0UL);
                break;
            case sbyte signed:
                WriteVarintField(encoded, 4, unchecked((ulong)(long)signed));
                break;
            case short signed:
                WriteVarintField(encoded, 4, unchecked((ulong)(long)signed));
                break;
            case int signed:
                WriteVarintField(encoded, 4, unchecked((ulong)(long)signed));
                break;
            case long signed:
                WriteVarintField(encoded, 4, unchecked((ulong)signed));
                break;
            case byte unsigned:
                WriteVarintField(encoded, 5, unsigned);
                break;
            case ushort unsigned:
                WriteVarintField(encoded, 5, unsigned);
                break;
            case uint unsigned:
                WriteVarintField(encoded, 5, unsigned);
                break;
            case ulong unsigned:
                WriteVarintField(encoded, 5, unsigned);
                break;
            case float number:
                WriteFixed32Field(
                    encoded,
                    2,
                    unchecked((uint)BitConverter.SingleToInt32Bits(number)));
                break;
            case double number:
                WriteFixed64Field(
                    encoded,
                    3,
                    unchecked((ulong)BitConverter.DoubleToInt64Bits(number)));
                break;
            default:
                throw new ArgumentException(
                    $"Property type {value.GetType().Name} is not supported.",
                    nameof(value));
        }
        return encoded.ToArray();
    }

    private static ulong Command(int id, int count) =>
        checked((ulong)((count << 3) | id));

    private static ulong ZigZag(int value) =>
        unchecked((ulong)((value << 1) ^ (value >> 31)));

    private static void WriteMessage(List<byte> destination, int field, byte[] value)
    {
        WriteVarint(destination, (ulong)((field << 3) | 2));
        WriteVarint(destination, (ulong)value.Length);
        destination.AddRange(value);
    }

    private static void WriteString(List<byte> destination, int field, string value) =>
        WriteMessage(destination, field, Encoding.UTF8.GetBytes(value));

    private static void WriteVarintField(
        List<byte> destination,
        int field,
        ulong value)
    {
        WriteVarint(destination, (ulong)(field << 3));
        WriteVarint(destination, value);
    }

    private static void WriteFixed32Field(
        List<byte> destination,
        int field,
        uint value)
    {
        WriteVarint(destination, (ulong)((field << 3) | 5));
        destination.AddRange(BitConverter.GetBytes(value));
    }

    private static void WriteFixed64Field(
        List<byte> destination,
        int field,
        ulong value)
    {
        WriteVarint(destination, (ulong)((field << 3) | 1));
        destination.AddRange(BitConverter.GetBytes(value));
    }

    private static void WriteVarint(List<byte> destination, ulong value)
    {
        while (value >= 0x80)
        {
            destination.Add((byte)(value | 0x80));
            value >>= 7;
        }
        destination.Add((byte)value);
    }

    private sealed class LayerBuilder(string name)
    {
        internal List<FeatureBuilder> Features { get; } = [];

        internal byte[] Build()
        {
            List<string> keys = [];
            List<object> values = [];
            Dictionary<string, int> keyIndices = new(StringComparer.Ordinal);
            Dictionary<PropertyValueKey, int> valueIndices = [];
            List<byte[]> encodedFeatures = [];
            foreach (FeatureBuilder feature in Features)
            {
                List<byte> encoded = [];
                List<byte> tags = [];
                foreach ((string key, object value) in feature.Properties)
                {
                    if (!keyIndices.TryGetValue(key, out int keyIndex))
                    {
                        keyIndex = keys.Count;
                        keys.Add(key);
                        keyIndices.Add(key, keyIndex);
                    }
                    var valueKey = new PropertyValueKey(
                        value.GetType(),
                        value);
                    if (!valueIndices.TryGetValue(valueKey, out int valueIndex))
                    {
                        valueIndex = values.Count;
                        values.Add(value);
                        valueIndices.Add(valueKey, valueIndex);
                    }
                    WriteVarint(tags, (ulong)keyIndex);
                    WriteVarint(tags, (ulong)valueIndex);
                }
                if (tags.Count != 0)
                {
                    WriteMessage(encoded, 2, tags.ToArray());
                }
                WriteVarintField(
                    encoded,
                    3,
                    feature.GeometryType switch
                    {
                        VectorTileGeometryType.Point => 1,
                        VectorTileGeometryType.LineString => 2,
                        VectorTileGeometryType.Polygon => 3,
                        _ => throw new InvalidOperationException(
                            "The test builder contains an unsupported geometry type."),
                    });
                WriteMessage(encoded, 4, EncodeGeometry(feature));
                encodedFeatures.Add(encoded.ToArray());
            }

            List<byte> layer = [];
            WriteString(layer, 1, name);
            foreach (byte[] feature in encodedFeatures)
            {
                WriteMessage(layer, 2, feature);
            }
            foreach (string key in keys)
            {
                WriteString(layer, 3, key);
            }
            foreach (object value in values)
            {
                WriteMessage(layer, 4, EncodeValue(value));
            }
            WriteVarintField(layer, 5, Extent);
            WriteVarintField(layer, 15, 2);
            return layer.ToArray();
        }
    }

    private sealed class FeatureBuilder(
        VectorTileGeometryType geometryType,
        TestTilePoint[]? points,
        TestTilePoint[][]? rings,
        IReadOnlyDictionary<string, object>? properties)
    {
        internal VectorTileGeometryType GeometryType { get; } = geometryType;

        internal TestTilePoint[]? Points { get; } = points;

        internal TestTilePoint[][]? Rings { get; } = rings;

        internal IReadOnlyDictionary<string, object> Properties { get; } =
            properties ?? new Dictionary<string, object>();
    }

    private readonly record struct PropertyValueKey(Type Type, object Value);
}

internal readonly record struct TestTilePoint(int X, int Y);
