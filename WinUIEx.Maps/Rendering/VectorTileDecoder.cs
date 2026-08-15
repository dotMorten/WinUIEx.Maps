using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using LibTessDotNet.Double;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Decodes bounded point, line, and polygon features, source-layer names, and properties from a
/// Mapbox Vector Tile payload.
/// </summary>
internal static class VectorTileDecoder
{
    private const int MaximumLayers = 256;
    private const int MaximumFeatures = 200_000;
    private const int MaximumKeys = 65_536;
    private const int MaximumValues = 262_144;
    private const int MaximumProperties = 2_000_000;
    private const int MaximumPropertiesPerFeature = 1_024;
    private const int MaximumPoints = 500_000;
    private const int MaximumPolygonTrianglePoints = 1_500_000;
    private const int MaximumLayerNameBytes = 1_024;
    private const int MaximumKeyBytes = 1_024;
    private const int MaximumStringValueBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static VectorTileFeatureCollection Decode(
        ReadOnlySpan<byte> tile,
        CancellationToken cancellationToken = default)
    {
        List<VectorTileFeature> features = [];
        ProtobufReader reader = new(tile);
        int layerCount = 0;
        int featureCount = 0;
        int keyCount = 0;
        int valueCount = 0;
        int propertyCount = 0;
        int pointCount = 0;
        while (reader.TryReadField(out int fieldNumber, out int wireType))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fieldNumber == 3 && wireType == 2)
            {
                if (++layerCount > MaximumLayers)
                {
                    throw new InvalidDataException("The vector tile contains too many layers.");
                }
                DecodeLayer(
                    reader.ReadLengthDelimited(),
                    features,
                    ref featureCount,
                    ref keyCount,
                    ref valueCount,
                    ref propertyCount,
                    ref pointCount,
                    cancellationToken);
            }
            else
            {
                reader.SkipField(wireType);
            }
        }
        return new VectorTileFeatureCollection(features.ToArray());
    }

    private static void DecodeLayer(
        ReadOnlySpan<byte> layer,
        List<VectorTileFeature> features,
        ref int featureCount,
        ref int keyCount,
        ref int valueCount,
        ref int propertyCount,
        ref int pointCount,
        CancellationToken cancellationToken)
    {
        string name = string.Empty;
        uint extent = 4096;
        List<string> keys = [];
        List<VectorTileValue> values = [];
        ProtobufReader metadataReader = new(layer);
        while (metadataReader.TryReadField(out int fieldNumber, out int wireType))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (fieldNumber, wireType)
            {
                case (1, 2):
                    name = ReadBoundedString(
                        metadataReader.ReadLengthDelimited(),
                        MaximumLayerNameBytes,
                        "source-layer name");
                    break;
                case (3, 2):
                    if (++keyCount > MaximumKeys)
                    {
                        throw new InvalidDataException(
                            "The vector tile contains too many property keys.");
                    }
                    keys.Add(ReadBoundedString(
                        metadataReader.ReadLengthDelimited(),
                        MaximumKeyBytes,
                        "property key"));
                    break;
                case (4, 2):
                    if (++valueCount > MaximumValues)
                    {
                        throw new InvalidDataException(
                            "The vector tile contains too many property values.");
                    }
                    values.Add(DecodeValue(metadataReader.ReadLengthDelimited()));
                    break;
                case (5, 0):
                    extent = checked((uint)metadataReader.ReadVarint());
                    break;
                default:
                    metadataReader.SkipField(wireType);
                    break;
            }
        }

        if (extent == 0)
        {
            throw new InvalidDataException("The vector tile layer has a zero extent.");
        }
        if (string.IsNullOrEmpty(name))
        {
            throw new InvalidDataException(
                "The vector tile layer has no source-layer name.");
        }

        ProtobufReader featureReader = new(layer);
        while (featureReader.TryReadField(out int fieldNumber, out int wireType))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fieldNumber == 2 && wireType == 2)
            {
                if (++featureCount > MaximumFeatures)
                {
                    throw new InvalidDataException(
                        "The vector tile contains too many features.");
                }
                VectorTileFeature? feature = DecodeFeature(
                    featureReader.ReadLengthDelimited(),
                    name,
                    extent,
                    keys,
                    values,
                    ref propertyCount,
                    ref pointCount,
                    cancellationToken);
                if (feature is not null)
                {
                    features.Add(feature);
                }
            }
            else
            {
                featureReader.SkipField(wireType);
            }
        }
    }

    private static VectorTileFeature? DecodeFeature(
        ReadOnlySpan<byte> feature,
        string sourceLayer,
        uint extent,
        IReadOnlyList<string> keys,
        IReadOnlyList<VectorTileValue> values,
        ref int propertyCount,
        ref int pointCount,
        CancellationToken cancellationToken)
    {
        ProtobufReader typeReader = new(feature);
        uint geometryType = 0;
        while (typeReader.TryReadField(out int fieldNumber, out int wireType))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fieldNumber == 3 && wireType == 0)
            {
                geometryType = checked((uint)typeReader.ReadVarint());
            }
            else
            {
                typeReader.SkipField(wireType);
            }
        }
        if (geometryType is not (1 or 2 or 3))
        {
            return null;
        }

        VectorTilePoint[] points = [];
        VectorTileLine[] lines = [];
        VectorTilePolygon[] polygons = [];
        VectorTileGeometryType decodedType;
        if (geometryType == 1)
        {
            points = DecodePointGeometry(
                feature,
                extent,
                ref pointCount,
                cancellationToken);
            if (points.Length == 0)
            {
                throw new InvalidDataException(
                    "The vector tile contains a point feature without point geometry.");
            }
            decodedType = points.Length > 1
                ? VectorTileGeometryType.MultiPoint
                : VectorTileGeometryType.Point;
        }
        else if (geometryType == 2)
        {
            lines = DecodeLineGeometry(
                feature,
                extent,
                ref pointCount,
                cancellationToken);
            if (lines.Length == 0)
            {
                throw new InvalidDataException(
                    "The vector tile contains a line feature without line geometry.");
            }
            decodedType = lines.Length > 1
                ? VectorTileGeometryType.MultiLineString
                : VectorTileGeometryType.LineString;
        }
        else
        {
            polygons = DecodePolygonGeometry(
                feature,
                extent,
                ref pointCount,
                cancellationToken);
            if (polygons.Length == 0)
            {
                throw new InvalidDataException(
                    "The vector tile contains a polygon feature without polygon geometry.");
            }
            decodedType = polygons.Length > 1
                ? VectorTileGeometryType.MultiPolygon
                : VectorTileGeometryType.Polygon;
        }
        VectorTileProperty[] properties = DecodeProperties(
            feature,
            keys,
            values,
            ref propertyCount,
            cancellationToken);
        return new VectorTileFeature(
            sourceLayer,
            decodedType,
            points,
            properties,
            lines,
            polygons);
    }

    private static VectorTilePoint[] DecodePointGeometry(
        ReadOnlySpan<byte> feature,
        uint extent,
        ref int pointCount,
        CancellationToken cancellationToken)
    {
        List<VectorTilePoint> points = [];
        int x = 0;
        int y = 0;
        ProtobufReader geometryReader = new(feature);
        while (geometryReader.TryReadField(out int fieldNumber, out int wireType))
        {
            if (fieldNumber != 4 || wireType != 2)
            {
                geometryReader.SkipField(wireType);
                continue;
            }

            ProtobufReader commands = new(geometryReader.ReadLengthDelimited());
            while (!commands.End)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong commandInteger = commands.ReadVarint();
                int command = (int)(commandInteger & 7);
                ulong count = commandInteger >> 3;
                if (count == 0 || command != 1)
                {
                    throw new InvalidDataException(
                        "The vector tile contains invalid point geometry.");
                }

                for (ulong index = 0; index < count; index++)
                {
                    x = checked(x + DecodeZigZag32(commands.ReadVarint()));
                    y = checked(y + DecodeZigZag32(commands.ReadVarint()));
                    if (++pointCount > MaximumPoints)
                    {
                        throw new InvalidDataException(
                            "The vector tile contains too many points.");
                    }
                    points.Add(new VectorTilePoint(
                        x / (double)extent,
                        y / (double)extent));
                }
            }
        }
        return points.ToArray();
    }

    private static VectorTileLine[] DecodeLineGeometry(
        ReadOnlySpan<byte> feature,
        uint extent,
        ref int pointCount,
        CancellationToken cancellationToken)
    {
        List<VectorTileLine> lines = [];
        List<VectorTilePoint>? currentLine = null;
        int x = 0;
        int y = 0;
        ProtobufReader geometryReader = new(feature);
        while (geometryReader.TryReadField(out int fieldNumber, out int wireType))
        {
            if (fieldNumber != 4 || wireType != 2)
            {
                geometryReader.SkipField(wireType);
                continue;
            }

            ProtobufReader commands = new(geometryReader.ReadLengthDelimited());
            while (!commands.End)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong commandInteger = commands.ReadVarint();
                int command = (int)(commandInteger & 7);
                ulong count = commandInteger >> 3;
                if (count == 0)
                {
                    throw new InvalidDataException(
                        "The vector tile contains invalid line geometry.");
                }
                if (command == 1)
                {
                    if (count != 1)
                    {
                        throw new InvalidDataException(
                            "The vector tile contains invalid line geometry.");
                    }
                    AddCompletedLine(lines, currentLine);
                    currentLine = [];
                }
                else if (command != 2 || currentLine is null)
                {
                    throw new InvalidDataException(
                        "The vector tile contains invalid line geometry.");
                }

                for (ulong index = 0; index < count; index++)
                {
                    x = checked(x + DecodeZigZag32(commands.ReadVarint()));
                    y = checked(y + DecodeZigZag32(commands.ReadVarint()));
                    if (++pointCount > MaximumPoints)
                    {
                        throw new InvalidDataException(
                            "The vector tile contains too many points.");
                    }
                    currentLine.Add(new VectorTilePoint(
                        x / (double)extent,
                        y / (double)extent));
                }
            }
        }
        AddCompletedLine(lines, currentLine);
        return lines.ToArray();
    }

    private static void AddCompletedLine(
        List<VectorTileLine> lines,
        List<VectorTilePoint>? points)
    {
        if (points is null)
        {
            return;
        }
        if (points.Count < 2)
        {
            throw new InvalidDataException(
                "The vector tile contains a line with fewer than two points.");
        }
        lines.Add(new VectorTileLine(points.ToArray()));
    }

    private static VectorTilePolygon[] DecodePolygonGeometry(
        ReadOnlySpan<byte> feature,
        uint extent,
        ref int pointCount,
        CancellationToken cancellationToken)
    {
        List<VectorTilePolygon> polygons = [];
        List<VectorTileRing> currentRings = [];
        List<VectorTilePoint>? currentRing = null;
        int trianglePointCount = 0;
        int x = 0;
        int y = 0;
        ProtobufReader geometryReader = new(feature);
        while (geometryReader.TryReadField(out int fieldNumber, out int wireType))
        {
            if (fieldNumber != 4 || wireType != 2)
            {
                geometryReader.SkipField(wireType);
                continue;
            }

            ProtobufReader commands = new(geometryReader.ReadLengthDelimited());
            while (!commands.End)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong commandInteger = commands.ReadVarint();
                int command = (int)(commandInteger & 7);
                ulong count = commandInteger >> 3;
                if (count == 0)
                {
                    throw new InvalidDataException(
                        "The vector tile contains invalid polygon geometry.");
                }
                if (command == 1)
                {
                    if (count != 1 || currentRing is not null)
                    {
                        throw new InvalidDataException(
                            "The vector tile contains invalid polygon geometry.");
                    }
                    currentRing = [];
                }
                else if (command == 2)
                {
                    if (currentRing is null)
                    {
                        throw new InvalidDataException(
                            "The vector tile contains invalid polygon geometry.");
                    }
                }
                else if (command == 7)
                {
                    if (count != 1 || currentRing is null)
                    {
                        throw new InvalidDataException(
                            "The vector tile contains invalid polygon geometry.");
                    }
                    AddCompletedRing(
                        polygons,
                        currentRings,
                        currentRing,
                        ref trianglePointCount);
                    currentRing = null;
                    continue;
                }
                else
                {
                    throw new InvalidDataException(
                        "The vector tile contains invalid polygon geometry.");
                }

                for (ulong index = 0; index < count; index++)
                {
                    x = checked(x + DecodeZigZag32(commands.ReadVarint()));
                    y = checked(y + DecodeZigZag32(commands.ReadVarint()));
                    if (++pointCount > MaximumPoints)
                    {
                        throw new InvalidDataException(
                            "The vector tile contains too many points.");
                    }
                    currentRing.Add(new VectorTilePoint(
                        x / (double)extent,
                        y / (double)extent));
                }
            }
        }
        if (currentRing is not null)
        {
            throw new InvalidDataException(
                "The vector tile contains an unclosed polygon ring.");
        }
        AddCompletedPolygon(
            polygons,
            currentRings,
            ref trianglePointCount);
        return polygons.ToArray();
    }

    private static void AddCompletedRing(
        List<VectorTilePolygon> polygons,
        List<VectorTileRing> currentRings,
        List<VectorTilePoint> points,
        ref int trianglePointCount)
    {
        if (points.Count < 3)
        {
            throw new InvalidDataException(
                "The vector tile contains a polygon ring with fewer than three points.");
        }
        double signedArea = GetSignedArea(points);
        if (!double.IsFinite(signedArea) || Math.Abs(signedArea) <= 1e-15)
        {
            throw new InvalidDataException(
                "The vector tile contains a degenerate polygon ring.");
        }
        if (signedArea > 0)
        {
            AddCompletedPolygon(
                polygons,
                currentRings,
                ref trianglePointCount);
        }
        else if (currentRings.Count == 0)
        {
            throw new InvalidDataException(
                "The vector tile polygon starts with an interior ring.");
        }
        currentRings.Add(new VectorTileRing(points.ToArray()));
    }

    private static void AddCompletedPolygon(
        List<VectorTilePolygon> polygons,
        List<VectorTileRing> rings,
        ref int trianglePointCount)
    {
        if (rings.Count == 0)
        {
            return;
        }
        Tess tessellator = new();
        foreach (VectorTileRing ring in rings)
        {
            ContourVertex[] vertices = new ContourVertex[ring.Points.Length];
            for (int index = 0; index < vertices.Length; index++)
            {
                VectorTilePoint point = ring.Points[index];
                vertices[index] = new ContourVertex(
                    new Vec3(point.X, point.Y, 0),
                    null);
            }
            tessellator.AddContour(vertices, ContourOrientation.Original);
        }
        tessellator.Tessellate(
            WindingRule.EvenOdd,
            ElementType.Polygons,
            3);

        List<VectorTilePoint> triangles = new(tessellator.Elements.Length);
        foreach (int element in tessellator.Elements)
        {
            if (element == Tess.Undef)
            {
                continue;
            }
            Vec3 point = tessellator.Vertices[element].Position;
            triangles.Add(new VectorTilePoint(point.X, point.Y));
        }
        if (trianglePointCount >
            MaximumPolygonTrianglePoints - triangles.Count)
        {
            throw new InvalidDataException(
                "The vector tile contains too much tessellated polygon geometry.");
        }
        trianglePointCount += triangles.Count;
        polygons.Add(new VectorTilePolygon(
            rings.ToArray(),
            triangles.ToArray()));
        rings.Clear();
    }

    private static double GetSignedArea(IReadOnlyList<VectorTilePoint> points)
    {
        double area = 0;
        VectorTilePoint previous = points[^1];
        foreach (VectorTilePoint current in points)
        {
            area += (previous.X * current.Y) - (current.X * previous.Y);
            previous = current;
        }
        return area / 2;
    }

    private static VectorTileProperty[] DecodeProperties(
        ReadOnlySpan<byte> feature,
        IReadOnlyList<string> keys,
        IReadOnlyList<VectorTileValue> values,
        ref int propertyCount,
        CancellationToken cancellationToken)
    {
        List<uint> tags = [];
        ProtobufReader tagReader = new(feature);
        while (tagReader.TryReadField(out int fieldNumber, out int wireType))
        {
            if (fieldNumber != 2)
            {
                tagReader.SkipField(wireType);
                continue;
            }

            if (wireType == 2)
            {
                ProtobufReader packed = new(tagReader.ReadLengthDelimited());
                while (!packed.End)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    tags.Add(checked((uint)packed.ReadVarint()));
                }
            }
            else if (wireType == 0)
            {
                tags.Add(checked((uint)tagReader.ReadVarint()));
            }
            else
            {
                throw new InvalidDataException(
                    "The vector tile contains an invalid property tag field.");
            }
        }

        if ((tags.Count & 1) != 0)
        {
            throw new InvalidDataException(
                "The vector tile contains an incomplete property tag pair.");
        }
        int featurePropertyCount = tags.Count / 2;
        if (featurePropertyCount > MaximumPropertiesPerFeature ||
            propertyCount > MaximumProperties - featurePropertyCount)
        {
            throw new InvalidDataException(
                "The vector tile contains too many feature properties.");
        }
        propertyCount += featurePropertyCount;

        VectorTileProperty[] properties = new VectorTileProperty[featurePropertyCount];
        for (int index = 0; index < properties.Length; index++)
        {
            uint keyIndex = tags[index * 2];
            uint valueIndex = tags[(index * 2) + 1];
            if (keyIndex >= keys.Count || valueIndex >= values.Count)
            {
                throw new InvalidDataException(
                    "The vector tile contains an out-of-range property tag.");
            }
            properties[index] = new VectorTileProperty(
                keys[(int)keyIndex],
                values[(int)valueIndex]);
        }
        return properties;
    }

    private static VectorTileValue DecodeValue(ReadOnlySpan<byte> encoded)
    {
        VectorTileValue value = default;
        bool found = false;
        ProtobufReader reader = new(encoded);
        while (reader.TryReadField(out int fieldNumber, out int wireType))
        {
            switch (fieldNumber, wireType)
            {
                case (1, 2):
                    EnsureSingleValue(found);
                    value = VectorTileValue.FromString(ReadBoundedString(
                        reader.ReadLengthDelimited(),
                        MaximumStringValueBytes,
                        "string property value"));
                    found = true;
                    break;
                case (2, 5):
                    EnsureSingleValue(found);
                    value = VectorTileValue.FromFloat(
                        BitConverter.Int32BitsToSingle(
                            unchecked((int)reader.ReadFixed32())));
                    found = true;
                    break;
                case (3, 1):
                    EnsureSingleValue(found);
                    value = VectorTileValue.FromDouble(
                        BitConverter.Int64BitsToDouble(
                            unchecked((long)reader.ReadFixed64())));
                    found = true;
                    break;
                case (4, 0):
                    EnsureSingleValue(found);
                    value = VectorTileValue.FromInt(
                        unchecked((long)reader.ReadVarint()));
                    found = true;
                    break;
                case (5, 0):
                    EnsureSingleValue(found);
                    value = VectorTileValue.FromUInt(reader.ReadVarint());
                    found = true;
                    break;
                case (6, 0):
                    EnsureSingleValue(found);
                    value = VectorTileValue.FromSInt(
                        DecodeZigZag64(reader.ReadVarint()));
                    found = true;
                    break;
                case (7, 0):
                    EnsureSingleValue(found);
                    value = VectorTileValue.FromBool(reader.ReadVarint() != 0);
                    found = true;
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }
        if (!found)
        {
            throw new InvalidDataException(
                "The vector tile contains a property value with no supported type.");
        }
        return value;
    }

    private static void EnsureSingleValue(bool found)
    {
        if (found)
        {
            throw new InvalidDataException(
                "The vector tile property value contains multiple value types.");
        }
    }

    private static string ReadBoundedString(
        ReadOnlySpan<byte> encoded,
        int maximumBytes,
        string description)
    {
        if (encoded.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"The vector tile {description} exceeds its size limit.");
        }
        try
        {
            return StrictUtf8.GetString(encoded);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"The vector tile {description} is not valid UTF-8.",
                exception);
        }
    }

    private static int DecodeZigZag32(ulong value)
    {
        long decoded = DecodeZigZag64(value);
        return checked((int)decoded);
    }

    private static long DecodeZigZag64(ulong value) =>
        unchecked((long)(value >> 1)) ^ -unchecked((long)(value & 1));

    private ref struct ProtobufReader(ReadOnlySpan<byte> buffer)
    {
        private readonly ReadOnlySpan<byte> _buffer = buffer;
        private int _offset;

        internal readonly bool End => _offset == _buffer.Length;

        internal bool TryReadField(out int fieldNumber, out int wireType)
        {
            if (End)
            {
                fieldNumber = 0;
                wireType = 0;
                return false;
            }

            ulong tag = ReadVarint();
            fieldNumber = checked((int)(tag >> 3));
            wireType = (int)(tag & 7);
            if (fieldNumber == 0)
            {
                throw new InvalidDataException(
                    "The vector tile contains an invalid protobuf field.");
            }
            return true;
        }

        internal ulong ReadVarint()
        {
            ulong value = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (_offset >= _buffer.Length)
                {
                    throw new InvalidDataException(
                        "The vector tile ended inside a varint.");
                }
                byte current = _buffer[_offset++];
                if (shift == 63 && current > 1)
                {
                    throw new InvalidDataException(
                        "The vector tile contains an oversized varint.");
                }
                value |= (ulong)(current & 0x7f) << shift;
                if ((current & 0x80) == 0)
                {
                    return value;
                }
            }
            throw new InvalidDataException(
                "The vector tile contains an oversized varint.");
        }

        internal uint ReadFixed32()
        {
            ReadOnlySpan<byte> value = ReadFixed(4);
            return BinaryPrimitives.ReadUInt32LittleEndian(value);
        }

        internal ulong ReadFixed64()
        {
            ReadOnlySpan<byte> value = ReadFixed(8);
            return BinaryPrimitives.ReadUInt64LittleEndian(value);
        }

        internal ReadOnlySpan<byte> ReadLengthDelimited()
        {
            ulong length = ReadVarint();
            if (length > int.MaxValue || length > (ulong)(_buffer.Length - _offset))
            {
                throw new InvalidDataException(
                    "The vector tile contains an invalid field length.");
            }
            ReadOnlySpan<byte> value = _buffer.Slice(_offset, (int)length);
            _offset += (int)length;
            return value;
        }

        internal void SkipField(int wireType)
        {
            switch (wireType)
            {
                case 0:
                    ReadVarint();
                    break;
                case 1:
                    _ = ReadFixed64();
                    break;
                case 2:
                    _ = ReadLengthDelimited();
                    break;
                case 5:
                    _ = ReadFixed32();
                    break;
                default:
                    throw new InvalidDataException(
                        "The vector tile uses an unsupported protobuf wire type.");
            }
        }

        private ReadOnlySpan<byte> ReadFixed(int count)
        {
            if (count > _buffer.Length - _offset)
            {
                throw new InvalidDataException(
                    "The vector tile ended inside a fixed-width field.");
            }
            ReadOnlySpan<byte> value = _buffer.Slice(_offset, count);
            _offset += count;
            return value;
        }
    }
}

internal sealed class VectorTileFeatureCollection
{
    private readonly Dictionary<string, VectorTileFeature[]> _featuresBySourceLayer;

    internal VectorTileFeatureCollection(VectorTileFeature[] features)
    {
        Features = features;
        _featuresBySourceLayer = features
            .GroupBy(feature => feature.SourceLayer, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);
        PointCount = features.Sum(feature => feature.Points.Length);
        LineCount = features.Sum(feature => feature.Lines.Length);
        LinePointCount = features.Sum(feature =>
            feature.Lines.Sum(line => line.Points.Length));
        PolygonCount = features.Sum(feature => feature.Polygons.Length);
        PolygonTriangleCount = features.Sum(feature =>
            feature.Polygons.Sum(polygon => polygon.FillTriangles.Length / 3));
        ByteSize = features.Sum(feature => feature.ByteSize);
    }

    internal VectorTileFeature[] Features { get; }

    internal int PointCount { get; }

    internal int LineCount { get; }

    internal int LinePointCount { get; }

    internal int PolygonCount { get; }

    internal int PolygonTriangleCount { get; }

    internal long ByteSize { get; }

    internal IReadOnlyList<VectorTileFeature> GetSourceLayer(string sourceLayer) =>
        _featuresBySourceLayer.TryGetValue(sourceLayer, out VectorTileFeature[]? features)
            ? features
            : [];
}

internal sealed record VectorTileFeature(
    string SourceLayer,
    VectorTileGeometryType GeometryType,
    VectorTilePoint[] Points,
    VectorTileProperty[] Properties,
    VectorTileLine[] Lines,
    VectorTilePolygon[] Polygons)
{
    internal long ByteSize =>
        ((long)Points.Length * 16) +
        Lines.Sum(line => (long)line.Points.Length * 16) +
        Polygons.Sum(polygon =>
            polygon.Rings.Sum(ring => (long)ring.Points.Length * 16) +
            ((long)polygon.FillTriangles.Length * 16)) +
        ((long)Properties.Length * 32) +
        (SourceLayer.Length * 2L);

    internal bool TryGetProperty(string name, out VectorTileValue value)
    {
        for (int index = Properties.Length - 1; index >= 0; index--)
        {
            if (string.Equals(Properties[index].Name, name, StringComparison.Ordinal))
            {
                value = Properties[index].Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}

internal enum VectorTileGeometryType
{
    Point,
    MultiPoint,
    LineString,
    MultiLineString,
    Polygon,
    MultiPolygon,
}

internal sealed record VectorTileLine(VectorTilePoint[] Points);

internal sealed record VectorTileRing(VectorTilePoint[] Points);

internal sealed record VectorTilePolygon(
    VectorTileRing[] Rings,
    VectorTilePoint[] FillTriangles);

internal readonly record struct VectorTileProperty(string Name, VectorTileValue Value);

internal enum VectorTileValueKind
{
    String,
    Float,
    Double,
    Int,
    UInt,
    SInt,
    Bool,
}

internal readonly record struct VectorTileValue
{
    private VectorTileValue(
        VectorTileValueKind kind,
        string? stringValue,
        double floatingValue,
        long signedValue,
        ulong unsignedValue,
        bool boolValue)
    {
        Kind = kind;
        StringValue = stringValue;
        FloatingValue = floatingValue;
        SignedValue = signedValue;
        UnsignedValue = unsignedValue;
        BoolValue = boolValue;
    }

    internal VectorTileValueKind Kind { get; }

    internal string? StringValue { get; }

    internal double FloatingValue { get; }

    internal long SignedValue { get; }

    internal ulong UnsignedValue { get; }

    internal bool BoolValue { get; }

    internal static VectorTileValue FromString(string value) =>
        new(VectorTileValueKind.String, value, 0, 0, 0, false);

    internal static VectorTileValue FromFloat(float value) =>
        new(VectorTileValueKind.Float, null, value, 0, 0, false);

    internal static VectorTileValue FromDouble(double value) =>
        new(VectorTileValueKind.Double, null, value, 0, 0, false);

    internal static VectorTileValue FromInt(long value) =>
        new(VectorTileValueKind.Int, null, 0, value, 0, false);

    internal static VectorTileValue FromUInt(ulong value) =>
        new(VectorTileValueKind.UInt, null, 0, 0, value, false);

    internal static VectorTileValue FromSInt(long value) =>
        new(VectorTileValueKind.SInt, null, 0, value, 0, false);

    internal static VectorTileValue FromBool(bool value) =>
        new(VectorTileValueKind.Bool, null, 0, 0, 0, value);

    internal bool TryGetNumber(out double value)
    {
        switch (Kind)
        {
            case VectorTileValueKind.Float:
            case VectorTileValueKind.Double:
                value = FloatingValue;
                return true;
            case VectorTileValueKind.Int:
            case VectorTileValueKind.SInt:
                value = SignedValue;
                return true;
            case VectorTileValueKind.UInt:
                value = UnsignedValue;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    internal string ToInvariantString() => Kind switch
    {
        VectorTileValueKind.String => StringValue ?? string.Empty,
        VectorTileValueKind.Float or VectorTileValueKind.Double =>
            FloatingValue.ToString("G17", CultureInfo.InvariantCulture),
        VectorTileValueKind.Int or VectorTileValueKind.SInt =>
            SignedValue.ToString(CultureInfo.InvariantCulture),
        VectorTileValueKind.UInt =>
            UnsignedValue.ToString(CultureInfo.InvariantCulture),
        VectorTileValueKind.Bool => BoolValue ? "true" : "false",
        _ => string.Empty,
    };
}

internal readonly record struct VectorTilePoint(double X, double Y);
