using LibTessDotNet.Double;
using Windows.Devices.Geolocation;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps;

/// <summary>
/// Immutable projected geometry retained by lightweight map elements and published to the
/// renderer without exposing a mutable <see cref="Geopath"/>.
/// </summary>
internal sealed class MapGeometryData
{
    internal const int MaximumContourCount = 256;
    internal const int MaximumPointCount = 65_536;
    internal const int MaximumTessellatedVertexCount = 262_144;
    internal static readonly MapGeometryData Empty = new([], [], [], 0);

    private MapGeometryData(
        MapGeometryContour[] contours,
        MapWorldPoint[] fillVertices,
        int[] fillIndices,
        double anchorWorldX)
    {
        Contours = contours;
        FillVertices = fillVertices;
        FillIndices = fillIndices;
        AnchorWorldX = anchorWorldX;
    }

    internal MapGeometryContour[] Contours { get; }

    internal MapWorldPoint[] FillVertices { get; }

    internal int[] FillIndices { get; }

    internal double AnchorWorldX { get; }

    internal static MapGeometryData CreatePolyline(Geopath? path) =>
        path is null
            ? Empty
            : Create([CreateContour(path)], tessellate: false);

    internal static MapGeometryData CreatePolygon(IEnumerable<Geopath> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        List<MapGeometryContour> contours = [];
        foreach (Geopath? path in paths)
        {
            ArgumentNullException.ThrowIfNull(path);
            if (contours.Count == MaximumContourCount)
            {
                throw new ArgumentException(
                    $"A polygon supports at most {MaximumContourCount} paths.",
                    nameof(paths));
            }
            contours.Add(CreateContour(path));
        }
        return Create(contours, tessellate: true);
    }

    private static MapGeometryContour CreateContour(Geopath path)
    {
        IReadOnlyList<BasicGeoposition> positions = path.Positions;
        if (positions.Count > MaximumPointCount)
        {
            throw new ArgumentException(
                $"A path supports at most {MaximumPointCount} positions.",
                nameof(path));
        }

        List<MapWorldPoint> points = new(positions.Count);
        double previousWorldX = 0;
        for (int index = 0; index < positions.Count; index++)
        {
            BasicGeoposition position = positions[index];
            if (!double.IsFinite(position.Longitude) ||
                !double.IsFinite(position.Latitude))
            {
                throw new ArgumentException(
                    "Path positions must contain finite longitude and latitude values.",
                    nameof(path));
            }

            double wrappedWorldX = MapCamera.LongitudeToWorldX(position.Longitude);
            double worldX = index == 0
                ? wrappedWorldX
                : wrappedWorldX + Math.Round(previousWorldX - wrappedWorldX);
            MapWorldPoint point = new(
                worldX,
                MapCamera.LatitudeToWorldY(position.Latitude));
            previousWorldX = worldX;
            if (points.Count == 0 || !points[^1].Equals(point))
            {
                points.Add(point);
            }
        }

        if (points.Count > 1 && points[0].Equals(points[^1]))
        {
            points.RemoveAt(points.Count - 1);
        }
        return new MapGeometryContour(points.ToArray());
    }

    private static MapGeometryData Create(
        IReadOnlyList<MapGeometryContour> sourceContours,
        bool tessellate)
    {
        if (sourceContours.Count == 0)
        {
            return Empty;
        }

        int pointCount = sourceContours.Sum(contour => contour.Points.Length);
        if (pointCount > MaximumPointCount)
        {
            throw new ArgumentException(
                $"Geometry supports at most {MaximumPointCount} positions in total.");
        }

        MapGeometryContour? referenceContour =
            sourceContours.FirstOrDefault(contour => contour.Points.Length != 0);
        if (referenceContour is null)
        {
            return Empty;
        }

        double referenceWorldX = referenceContour.Points[0].X;
        MapGeometryContour[] contours = new MapGeometryContour[sourceContours.Count];
        double anchorSum = 0;
        int anchorCount = 0;
        for (int index = 0; index < sourceContours.Count; index++)
        {
            MapWorldPoint[] source = sourceContours[index].Points;
            if (source.Length == 0)
            {
                contours[index] = sourceContours[index];
                continue;
            }

            double shift = Math.Round(referenceWorldX - source[0].X);
            MapWorldPoint[] aligned = new MapWorldPoint[source.Length];
            for (int pointIndex = 0; pointIndex < source.Length; pointIndex++)
            {
                aligned[pointIndex] = source[pointIndex] with
                {
                    X = source[pointIndex].X + shift,
                };
                anchorSum += aligned[pointIndex].X;
                anchorCount++;
            }
            contours[index] = new MapGeometryContour(aligned);
        }

        double anchorWorldX = anchorCount == 0 ? referenceWorldX : anchorSum / anchorCount;
        if (!tessellate)
        {
            return new MapGeometryData(contours, [], [], anchorWorldX);
        }

        Tess tessellator = new();
        foreach (MapGeometryContour contour in contours)
        {
            if (contour.Points.Length < 3)
            {
                continue;
            }

            ContourVertex[] vertices = new ContourVertex[contour.Points.Length];
            for (int index = 0; index < vertices.Length; index++)
            {
                MapWorldPoint point = contour.Points[index];
                vertices[index] = new ContourVertex(new Vec3(point.X, point.Y, 0), null);
            }
            tessellator.AddContour(vertices, ContourOrientation.Original);
        }

        tessellator.Tessellate(
            WindingRule.EvenOdd,
            ElementType.Polygons,
            3);
        if (tessellator.VertexCount > MaximumTessellatedVertexCount ||
            tessellator.Elements.Length > MaximumTessellatedVertexCount * 3)
        {
            throw new ArgumentException(
                "The tessellated polygon exceeds the supported geometry complexity.");
        }

        MapWorldPoint[] fillVertices = new MapWorldPoint[tessellator.VertexCount];
        for (int index = 0; index < fillVertices.Length; index++)
        {
            Vec3 position = tessellator.Vertices[index].Position;
            fillVertices[index] = new MapWorldPoint(position.X, position.Y);
        }

        List<int> fillIndices = new(tessellator.Elements.Length);
        foreach (int element in tessellator.Elements)
        {
            if (element != Tess.Undef)
            {
                fillIndices.Add(element);
            }
        }
        return new MapGeometryData(
            contours,
            fillVertices,
            fillIndices.ToArray(),
            anchorWorldX);
    }
}

/// <summary>
/// Immutable unwrapped Web Mercator contour used for stroke generation and hit testing.
/// </summary>
internal sealed record MapGeometryContour(MapWorldPoint[] Points);

/// <summary>
/// Represents one immutable unwrapped normalized Web Mercator coordinate.
/// </summary>
internal readonly record struct MapWorldPoint(double X, double Y);
