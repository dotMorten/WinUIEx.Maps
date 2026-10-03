using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Win32.Graphics.Direct3D11;
using static WinUIEx.Maps.Rendering.DirectXInterop;

namespace WinUIEx.Maps.Rendering;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct VectorExtrusionVertex(
    Vector3 Position, Vector4 Normal, Vector4 Color, Vector2 Texture);

internal static class VectorExtrusionGeometry
{
    internal const double EarthCircumference = 40075016.68557849;

    internal static double MetersToTileUnits(TileId tile, double y)
    {
        double scale = Math.Pow(2, tile.Zoom);
        double latitude = MapCamera.WorldYToLatitude((tile.Y + y) / scale) * Math.PI / 180;
        return scale / (EarthCircumference * Math.Cos(latitude));
    }

    internal static Vector4 Project(Vector3 point, double heading, double pitch, double width, double height)
    {
        double h = heading * Math.PI / 180, p = pitch * Math.PI / 180;
        double x = point.X * Math.Cos(h) + point.Y * Math.Sin(h);
        double y = -point.X * Math.Sin(h) + point.Y * Math.Cos(h);
        double distance = MapCamera.GetPerspectiveDistance(height);
        double w = distance - y * Math.Sin(p) - point.Z * Math.Cos(p);
        return new((float)(x * distance * 2 / width),
            (float)(-(y * Math.Cos(p) - point.Z * Math.Sin(p)) * distance * 2 / height),
            (float)(w - 1), (float)w);
    }

    internal static void Append(TileId tile, VectorStyledExtrusion extrusion,
        ExtrusionMeshBuffer buffer, CancellationToken cancellationToken, double sourcePixels = 512)
    {
        VectorExtrusionPaint paint = extrusion.Paint;
        double meterScale = MetersToTileUnits(tile, 0.5);
        double height = paint.Height * meterScale, @base = paint.Base * meterScale;
        if (!double.IsFinite(height) || height > float.MaxValue ||
            !double.IsFinite(@base) || @base > float.MaxValue)
            throw new InvalidDataException("Extrusion coordinates exceed the supported numeric range.");
        double patternWidth = paint.PatternWidth > 0 ? paint.PatternWidth : 1;
        double patternHeight = paint.PatternHeight > 0 ? paint.PatternHeight : 1;
        Span<VectorTilePoint> first = stackalloc VectorTilePoint[8];
        Span<VectorTilePoint> second = stackalloc VectorTilePoint[8];
        var triangles = extrusion.Polygon.FillTriangles;
        for (int i = 0; i < triangles.Length; i += 3)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = MapRenderer.ClipVectorTileTriangle(triangles[i], triangles[i + 1], triangles[i + 2], first, second);
            for (int j = 1; j < count - 1; j++)
            {
                Roof(first[0]);
                Roof(first[j]);
                Roof(first[j + 1]);
            }
        }
        if (height == @base)
            return;
        foreach (var ring in extrusion.Polygon.Rings)
        {
            double distance = 0;
            for (int i = 0; i < ring.Points.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var a = ring.Points[i];
                var b = ring.Points[(i + 1) % ring.Points.Length];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length == 0)
                    continue;
                double start = distance;
                distance += length;
                // Closure edges introduced by a tile clip are not physical building walls.
                if ((a.X == b.X && (a.X <= 0 || a.X >= 1)) ||
                    (a.Y == b.Y && (a.Y <= 0 || a.Y >= 1)) ||
                    !MapRenderer.TryClipVectorPolygonOutlineSegment(a, b, out var ca, out var cb))
                    continue;
                Vector4 normal = new((float)(dy / length), (float)(-dx / length), 0, 0);
                double s = start + Math.Sqrt((ca.X - a.X) * (ca.X - a.X) + (ca.Y - a.Y) * (ca.Y - a.Y));
                double e = start + Math.Sqrt((cb.X - a.X) * (cb.X - a.X) + (cb.Y - a.Y) * (cb.Y - a.Y));
                Wall(ca, @base, s, 0, normal);
                Wall(cb, @base, e, 0, normal);
                Wall(cb, height, e, 1, normal);
                Wall(ca, @base, s, 0, normal);
                Wall(cb, height, e, 1, normal);
                Wall(ca, height, s, 1, normal);
            }
        }

        void Roof(VectorTilePoint point) => buffer.Add(new(
            new((float)point.X, (float)point.Y, (float)height),
            new(0, 0, 1, 1), paint.Color,
            new((float)(point.X * sourcePixels / patternWidth),
                (float)(point.Y * sourcePixels / patternHeight))));

        void Wall(VectorTilePoint point, double z, double along, float level, Vector4 normal) =>
            buffer.Add(new(new((float)point.X, (float)point.Y, (float)z),
                normal with { W = level }, paint.Color,
                new((float)(along * sourcePixels / patternWidth), (float)(-z * sourcePixels / patternHeight))));
    }
}

internal sealed unsafe class ExtrusionMeshBuffer : IDisposable
{
    private VectorExtrusionVertex* _vertices;
    private int _capacity;
    private IntPtr _buffer;
    internal int Count { get; private set; }
    internal IntPtr Pointer => _buffer;
    internal long ByteSize => (long)Count * sizeof(VectorExtrusionVertex);

    internal void Add(VectorExtrusionVertex vertex)
    {
        if (Count == _capacity)
        {
            if (Count >= 32 * 1024 * 1024 / sizeof(VectorExtrusionVertex))
                throw new InvalidDataException("Extrusion geometry exceeds the bounded mesh capacity.");
            int capacity = Math.Min(32 * 1024 * 1024 / sizeof(VectorExtrusionVertex),
                _capacity == 0 ? 256 : checked(_capacity * 2));
            var grown = (VectorExtrusionVertex*)NativeMemory.Realloc(
                _vertices, (nuint)(capacity * sizeof(VectorExtrusionVertex)));
            if (grown is null)
                throw new OutOfMemoryException();
            Interlocked.Add(ref MapRenderer.NativeGeometryBuffer.AllocatedBytes, (long)capacity * sizeof(VectorExtrusionVertex));
            Interlocked.Add(ref MapRenderer.NativeGeometryBuffer.ReleasedBytes, (long)_capacity * sizeof(VectorExtrusionVertex));
            _vertices = grown;
            _capacity = capacity;
        }
        _vertices[Count++] = vertex;
    }

    internal void Upload(IntPtr device, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Count == 0)
            return;
        D3D11_BUFFER_DESC description = new()
        {
            ByteWidth = checked((uint)ByteSize),
            Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_VERTEX_BUFFER,
        };
        D3D11_SUBRESOURCE_DATA data = new() { pSysMem = _vertices };
        _buffer = CreateBuffer(device, &description, &data, "Failed to upload extrusion geometry.");
        GC.AddMemoryPressure(ByteSize);
        NativeMemory.Free(_vertices);
        Interlocked.Add(ref MapRenderer.NativeGeometryBuffer.ReleasedBytes, (long)_capacity * sizeof(VectorExtrusionVertex));
        _vertices = null;
        _capacity = 0;
    }

    public void Dispose()
    {
        if (_buffer != IntPtr.Zero)
        {
            ReleasePointer(ref _buffer);
            GC.RemoveMemoryPressure(ByteSize);
        }
        NativeMemory.Free(_vertices);
        Interlocked.Add(ref MapRenderer.NativeGeometryBuffer.ReleasedBytes, (long)_capacity * sizeof(VectorExtrusionVertex));
        _vertices = null;
        _capacity = Count = 0;
    }
}
