using System.Numerics;

namespace WinUIEx.Maps.Rendering;

internal sealed partial class MapRenderer
{
    private static bool TryGetConvexTileBoundary(
        IReadOnlyList<VectorTilePoint> triangles,
        Span<VectorTilePoint> boundary,
        out int count)
    {
        count = 0;
        if (triangles.Count is < 3 or > 192 || triangles.Count % 3 != 0)
            return false;
        Span<VectorTilePoint> sorted = stackalloc VectorTilePoint[triangles.Count];
        double triangleArea = 0;
        for (int i = 0; i < triangles.Count; i++)
        {
            sorted[i] = triangles[i];
            if (!double.IsFinite(sorted[i].X) || !double.IsFinite(sorted[i].Y))
                return false;
            if (i % 3 == 2)
                triangleArea += Math.Abs(Cross(sorted[i - 2], sorted[i - 1], sorted[i]));
        }
        sorted.Sort(static (a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        Span<VectorTilePoint> hull = stackalloc VectorTilePoint[384];
        int size = 0;
        foreach (VectorTilePoint point in sorted)
        {
            while (size >= 2 && Cross(hull[size - 2], hull[size - 1], point) <= 0)
                size--;
            hull[size++] = point;
        }
        int lower = size;
        for (int i = sorted.Length - 2; i >= 0; i--)
        {
            while (size > lower && Cross(hull[size - 2], hull[size - 1], sorted[i]) <= 0)
                size--;
            hull[size++] = sorted[i];
        }
        size--;
        if (size is < 3 or > 124)
            return false;
        double hullArea = 0;
        for (int i = 1; i + 1 < size; i++)
            hullArea += Cross(hull[0], hull[i], hull[i + 1]);
        // Decoder triangles are non-overlapping. Area equality excludes concavities,
        // disconnected polygons and holes rather than antialiasing their convex hull.
        if (hullArea <= 0 || Math.Abs(hullArea - triangleArea) > hullArea * 1e-9)
            return false;
        Span<VectorTilePoint> scratch = stackalloc VectorTilePoint[128];
        count = ClipVectorTilePolygon(hull[..size], scratch, VectorTileClipEdge.Left);
        count = ClipVectorTilePolygon(scratch[..count], boundary, VectorTileClipEdge.Right);
        count = ClipVectorTilePolygon(boundary[..count], scratch, VectorTileClipEdge.Top);
        count = ClipVectorTilePolygon(scratch[..count], boundary, VectorTileClipEdge.Bottom);
        return count >= 3;
    }

    private static double Cross(VectorTilePoint a, VectorTilePoint b, VectorTilePoint c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    // This bounded path never infers a boundary from individual tessellation triangles.
    // Unsupported topology keeps the existing mesh, including its joins and patterns.
    private static bool TryAppendConvexCoverage(
        ReadOnlySpan<MapScreenPoint> boundary,
        ReadOnlySpan<bool> hardEdges,
        PooledGeometryBuffer output)
    {
        int count = boundary.Length;
        if (count is < 3 or > 128)
            return false;
        Span<MapScreenPoint> inner = stackalloc MapScreenPoint[count];
        Span<MapScreenPoint> outer = stackalloc MapScreenPoint[count];
        Span<Vector2> normals = stackalloc Vector2[count];
        for (int i = 0; i < count; i++)
        {
            MapScreenPoint a = boundary[i], b = boundary[(i + 1) % count];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (!double.IsFinite(length) || length < 1e-5)
                return false;
            normals[i] = new((float)(-dy / length), (float)(dx / length));
        }
        for (int i = 0; i < count; i++)
        {
            int previous = (i + count - 1) % count;
            Vector2 a = normals[previous], b = normals[i];
            double determinant = a.X * b.Y - a.Y * b.X;
            if (determinant <= 1e-5)
                return false;
            double da = !hardEdges.IsEmpty && hardEdges[previous] ? 0 : 1;
            double db = !hardEdges.IsEmpty && hardEdges[i] ? 0 : 1;
            double dx = (da * b.Y - a.Y * db) / determinant;
            double dy = (a.X * db - da * b.X) / determinant;
            // Bound acute-corner extrusion and reject collapsed inset rings.
            if (dx * dx + dy * dy > 64)
                return false;
            inner[i] = new(boundary[i].X + dx, boundary[i].Y + dy);
            outer[i] = new(boundary[i].X - dx, boundary[i].Y - dy);
        }
        for (int i = 0; i < count; i++)
        {
            Vector2 normal = normals[i];
            MapScreenPoint origin = boundary[i];
            for (int j = 0; j < count; j++)
            {
                double distance = (inner[j].X - origin.X) * normal.X +
                    (inner[j].Y - origin.Y) * normal.Y;
                double required = !hardEdges.IsEmpty && hardEdges[i] ? 0 : 1;
                if (distance < required - 1e-4)
                    return false;
            }
        }
        for (int i = 1; i + 1 < count; i++)
        {
            output.Add(inner[0]);
            output.Add(inner[i]);
            output.Add(inner[i + 1]);
        }
        for (int i = 0; i < count; i++)
        {
            if (!hardEdges.IsEmpty && hardEdges[i])
                continue;
            int next = (i + 1) % count;
            AddQuad(output,
                inner[i] with { Coverage = new(1, 1) },
                outer[i] with { Coverage = new(-1, 1) },
                outer[next] with { Coverage = new(-1, 1) },
                inner[next] with { Coverage = new(1, 1) });
        }
        return true;
    }

    private static bool TryAppendStraightLineCoverage(
        ReadOnlySpan<MapScreenPoint> points,
        VectorLineStyle style,
        PooledGeometryBuffer output)
    {
        if (points.Length != 2 || style.Width <= 2)
            return false;
        MapScreenPoint start = points[0], end = points[1];
        double dx = end.X - start.X, dy = end.Y - start.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(length) || length <= 2)
            return false;
        double ux = dx / length, uy = dy / length, radius = style.Width / 2;
        Span<MapScreenPoint> boundary = stackalloc MapScreenPoint[34];
        int count = 0;
        if (style.Cap == VectorLineCap.Round)
        {
            for (int cap = 0; cap < 2; cap++)
            {
                MapScreenPoint center = cap == 0 ? end : start;
                for (int step = 0; step <= 16; step++)
                {
                    double angle = -Math.PI / 2 + step * Math.PI / 16 + cap * Math.PI;
                    double along = Math.Cos(angle) * radius;
                    double across = Math.Sin(angle) * radius;
                    boundary[count++] = new(center.X + ux * along - uy * across,
                        center.Y + uy * along + ux * across);
                }
            }
        }
        else
        {
            double extension = style.Cap == VectorLineCap.Square ? radius : 0;
            boundary[count++] = new(start.X - ux * extension + uy * radius, start.Y - uy * extension - ux * radius);
            boundary[count++] = new(end.X + ux * extension + uy * radius, end.Y + uy * extension - ux * radius);
            boundary[count++] = new(end.X + ux * extension - uy * radius, end.Y + uy * extension + ux * radius);
            boundary[count++] = new(start.X - ux * extension - uy * radius, start.Y - uy * extension + ux * radius);
        }
        return TryAppendConvexCoverage(boundary[..count], [], output);
    }
}
