using System.Numerics;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Identifies the drawing and hit-test rules for an immutable geometry snapshot.
/// </summary>
internal enum MapGeometryKind
{
    Polygon,
    Polyline,
}

/// <summary>
/// Stores one color as immutable straight-alpha bytes without retaining a WinRT object.
/// </summary>
internal readonly record struct MapColorSnapshot(byte A, byte R, byte G, byte B)
{
    internal static MapColorSnapshot FromColor(Windows.UI.Color color) =>
        new(color.A, color.R, color.G, color.B);

    internal Vector4 ToVector(double layerOpacity) =>
        new(
            R / 255f,
            G / 255f,
            B / 255f,
            (float)((A / 255d) * Math.Clamp(layerOpacity, 0, 1)));
}

/// <summary>
/// Captures one polygon or polyline with immutable Web Mercator geometry, style, and visual
/// order for render-thread use.
/// </summary>
internal readonly record struct MapGeometrySnapshot(
    MapGeometryKind Kind,
    MapGeometryData Geometry,
    MapColorSnapshot FillColor,
    MapColorSnapshot StrokeColor,
    bool StrokeDashed,
    double StrokeThickness,
    int LayerIndex,
    int ElementIndex,
    int OrderIndex,
    bool IsEnabled = true)
{
    internal bool IsPolygon => Kind == MapGeometryKind.Polygon;
}

/// <summary>
/// Captures the displayed camera values needed by pure geometry generation and hit testing.
/// </summary>
internal readonly record struct MapGeometryCamera(
    double Longitude,
    double Latitude,
    double Zoom,
    double Heading,
    double ViewportWidth,
    double ViewportHeight,
    double Pitch = 0);

/// <summary>
/// Represents one screen-space point generated from immutable map geometry.
/// </summary>
internal readonly record struct MapScreenPoint(double X, double Y);

/// <summary>
/// Represents one solid or visible dashed screen-space stroke segment.
/// </summary>
internal readonly record struct MapScreenSegment(
    MapScreenPoint Start,
    MapScreenPoint End,
    int PathIndex = 0);

internal enum MapStrokeJoinPolicy
{
    SegmentsOnly,
    Round,
}

/// <summary>
/// Performs bounded screen-space fill expansion, dash generation, and geometry hit testing.
/// </summary>
internal static class MapGeometryOperations
{
    internal const MapStrokeJoinPolicy AutomaticStrokeJoinPolicy =
        MapStrokeJoinPolicy.Round;
    internal const int MaximumGeneratedVertexCount =
        MapGeometryData.MaximumPointCount * 6;
    private const int MaximumGeneratedSegmentCount =
        MapGeometryData.MaximumPointCount;

    internal static MapScreenPoint[] BuildFillTriangles(
        MapGeometryData geometry,
        MapGeometryCamera camera)
    {
        if (geometry.FillIndices.Length < 3 ||
            camera.ViewportWidth <= 0 ||
            camera.ViewportHeight <= 0)
        {
            return [];
        }

        int capacity = Math.Min(
            geometry.FillIndices.Length,
            MaximumGeneratedVertexCount);
        List<MapScreenPoint> triangles = new(capacity);
        for (int index = 0;
            index + 2 < geometry.FillIndices.Length &&
            triangles.Count <= MaximumGeneratedVertexCount - 3;
            index += 3)
        {
            int firstIndex = geometry.FillIndices[index];
            int secondIndex = geometry.FillIndices[index + 1];
            int thirdIndex = geometry.FillIndices[index + 2];
            if ((uint)firstIndex >= (uint)geometry.FillVertices.Length ||
                (uint)secondIndex >= (uint)geometry.FillVertices.Length ||
                (uint)thirdIndex >= (uint)geometry.FillVertices.Length)
            {
                continue;
            }

            MapScreenPoint first = Project(
                geometry.FillVertices[firstIndex],
                geometry.AnchorWorldX,
                camera);
            MapScreenPoint second = Project(
                geometry.FillVertices[secondIndex],
                geometry.AnchorWorldX,
                camera);
            MapScreenPoint third = Project(
                geometry.FillVertices[thirdIndex],
                geometry.AnchorWorldX,
                camera);
            if (!TriangleIntersectsViewport(first, second, third, camera))
            {
                continue;
            }

            triangles.Add(first);
            triangles.Add(second);
            triangles.Add(third);
        }
        return triangles.ToArray();
    }

    internal static MapScreenSegment[] BuildStrokeSegments(
        MapGeometryData geometry,
        bool closed,
        double thickness,
        bool dashed,
        MapGeometryCamera camera)
    {
        if (thickness <= 0 ||
            camera.ViewportWidth <= 0 ||
            camera.ViewportHeight <= 0)
        {
            return [];
        }

        List<MapScreenSegment> segments = [];
        for (int pathIndex = 0;
            pathIndex < geometry.Contours.Length;
            pathIndex++)
        {
            MapGeometryContour contour = geometry.Contours[pathIndex];
            double pathDistance = 0;
            MapWorldPoint[] points = contour.Points;
            int segmentCount = closed && points.Length > 2
                ? points.Length
                : Math.Max(0, points.Length - 1);
            for (int index = 0; index < segmentCount; index++)
            {
                MapScreenPoint start = Project(
                    points[index],
                    geometry.AnchorWorldX,
                    camera);
                MapScreenPoint end = Project(
                    points[(index + 1) % points.Length],
                    geometry.AnchorWorldX,
                    camera);
                double deltaX = end.X - start.X;
                double deltaY = end.Y - start.Y;
                double length = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
                if (!double.IsFinite(length) || length <= 1e-7)
                {
                    continue;
                }

                if (TryClipSegment(
                    start,
                    end,
                    thickness / 2,
                    camera.ViewportWidth,
                    camera.ViewportHeight,
                    out double clippedStart,
                    out double clippedEnd))
                {
                    if (dashed)
                    {
                        AddDashedSegments(
                            segments,
                            start,
                            end,
                            pathDistance,
                            length,
                            clippedStart,
                            clippedEnd,
                            thickness,
                            pathIndex);
                    }
                    else if (segments.Count < MaximumGeneratedSegmentCount)
                    {
                        segments.Add(new MapScreenSegment(
                            Interpolate(start, end, clippedStart),
                            Interpolate(start, end, clippedEnd),
                            pathIndex));
                    }
                }
                pathDistance += length;
                if (segments.Count == MaximumGeneratedSegmentCount)
                {
                    return segments.ToArray();
                }
            }
        }
        return segments.ToArray();
    }

    internal static MapScreenPoint[] ExpandStrokeTriangles(
        IReadOnlyList<MapScreenSegment> segments,
        double thickness) =>
        ExpandStrokeTriangles(
            segments,
            thickness,
            closed: false,
            MapStrokeJoinPolicy.SegmentsOnly);

    internal static MapScreenPoint[] BuildStrokeTriangles(
        MapGeometryData geometry,
        bool closed,
        double thickness,
        bool dashed,
        MapGeometryCamera camera) =>
        BuildStrokeTriangles(
            geometry,
            closed,
            thickness,
            dashed,
            camera,
            AutomaticStrokeJoinPolicy);

    internal static MapScreenPoint[] BuildStrokeTriangles(
        MapGeometryData geometry,
        bool closed,
        double thickness,
        bool dashed,
        MapGeometryCamera camera,
        MapStrokeJoinPolicy joinPolicy)
    {
        MapScreenSegment[] segments = BuildStrokeSegments(
            geometry,
            closed,
            thickness,
            dashed,
            camera);
        return ExpandStrokeTriangles(segments, thickness, closed, joinPolicy);
    }

    internal static MapScreenPoint[] ExpandStrokeTriangles(
        IReadOnlyList<MapScreenSegment> segments,
        double thickness,
        bool closed,
        MapStrokeJoinPolicy joinPolicy)
    {
        int segmentCount = Math.Min(segments.Count, MaximumGeneratedSegmentCount);
        int joinVertexCount = GetStrokeJoinVertexCount(
            segments,
            segmentCount,
            closed,
            joinPolicy);
        MapScreenPoint[] triangles = new MapScreenPoint[
            checked((segmentCount * 6) + joinVertexCount)];
        double halfThickness = thickness / 2;
        int vertex = 0;
        for (int index = 0; index < segmentCount; index++)
        {
            MapScreenSegment segment = segments[index];
            double deltaX = segment.End.X - segment.Start.X;
            double deltaY = segment.End.Y - segment.Start.Y;
            double length = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
            if (length <= 1e-7)
            {
                continue;
            }

            double perpendicularX = (-deltaY / length) * halfThickness;
            double perpendicularY = (deltaX / length) * halfThickness;
            MapScreenPoint first = new(
                segment.Start.X + perpendicularX,
                segment.Start.Y + perpendicularY);
            MapScreenPoint second = new(
                segment.End.X + perpendicularX,
                segment.End.Y + perpendicularY);
            MapScreenPoint third = new(
                segment.End.X - perpendicularX,
                segment.End.Y - perpendicularY);
            MapScreenPoint fourth = new(
                segment.Start.X - perpendicularX,
                segment.Start.Y - perpendicularY);
            AddTriangle(triangles, ref vertex, first, second, third);
            AddTriangle(triangles, ref vertex, first, third, fourth);
        }

        if (joinPolicy == MapStrokeJoinPolicy.SegmentsOnly)
        {
            return triangles;
        }

        for (int index = 1; index < segmentCount; index++)
        {
            MapScreenSegment previous = segments[index - 1];
            MapScreenSegment next = segments[index];
            if (GetStrokeJoinVertexCount(previous, next) > 0)
            {
                AddStrokeJoin(
                    triangles,
                    ref vertex,
                    previous,
                    next,
                    halfThickness);
            }
        }

        if (closed)
        {
            int first = 0;
            while (first < segmentCount)
            {
                int last = first;
                while (last + 1 < segmentCount &&
                    segments[last + 1].PathIndex == segments[first].PathIndex)
                {
                    last++;
                }
                if (last > first &&
                    GetStrokeJoinVertexCount(
                        segments[last],
                        segments[first]) > 0)
                {
                    AddStrokeJoin(
                        triangles,
                        ref vertex,
                        segments[last],
                        segments[first],
                        halfThickness);
                }
                first = last + 1;
            }
        }
        return triangles;
    }

    private static int GetStrokeJoinVertexCount(
        IReadOnlyList<MapScreenSegment> segments,
        int segmentCount,
        bool closed,
        MapStrokeJoinPolicy joinPolicy)
    {
        if (joinPolicy == MapStrokeJoinPolicy.SegmentsOnly)
        {
            return 0;
        }

        int count = 0;
        for (int index = 1; index < segmentCount; index++)
        {
            count += GetStrokeJoinVertexCount(
                segments[index - 1],
                segments[index]);
        }
        if (!closed)
        {
            return count;
        }

        int first = 0;
        while (first < segmentCount)
        {
            int last = first;
            while (last + 1 < segmentCount &&
                segments[last + 1].PathIndex == segments[first].PathIndex)
            {
                last++;
            }
            if (last > first)
            {
                count += GetStrokeJoinVertexCount(
                    segments[last],
                    segments[first]);
            }
            first = last + 1;
        }
        return count;
    }

    private static int GetStrokeJoinVertexCount(
        MapScreenSegment previous,
        MapScreenSegment next)
    {
        if (previous.PathIndex != next.PathIndex ||
            Math.Abs(previous.End.X - next.Start.X) > 1e-7 ||
            Math.Abs(previous.End.Y - next.Start.Y) > 1e-7 ||
            !TryGetUnitDirection(previous.Start, previous.End, out MapScreenPoint incoming) ||
            !TryGetUnitDirection(next.Start, next.End, out MapScreenPoint outgoing))
        {
            return 0;
        }

        double cross =
            (incoming.X * outgoing.Y) - (incoming.Y * outgoing.X);
        double dot =
            (incoming.X * outgoing.X) + (incoming.Y * outgoing.Y);
        if (Math.Abs(cross) <= 1e-7 && dot >= 0)
        {
            return 0;
        }
        if (Math.Abs(cross) <= 1e-7)
        {
            return 24;
        }

        return GetRoundJoinSegmentCount(dot) * 3;
    }

    private static void AddStrokeJoin(
        MapScreenPoint[] triangles,
        ref int vertex,
        MapScreenSegment previous,
        MapScreenSegment next,
        double radius)
    {
        AddRoundJoin(
            triangles,
            ref vertex,
            previous.Start,
            previous.End,
            next.End,
            radius);
    }

    private static void AddRoundJoin(
        MapScreenPoint[] triangles,
        ref int vertex,
        MapScreenPoint previous,
        MapScreenPoint join,
        MapScreenPoint next,
        double radius)
    {
        if (!TryGetUnitDirection(previous, join, out MapScreenPoint incoming) ||
            !TryGetUnitDirection(join, next, out MapScreenPoint outgoing))
        {
            return;
        }

        double cross =
            (incoming.X * outgoing.Y) - (incoming.Y * outgoing.X);
        double dot =
            (incoming.X * outgoing.X) + (incoming.Y * outgoing.Y);
        if (Math.Abs(cross) <= 1e-7)
        {
            AddCircle(triangles, ref vertex, join, radius);
            return;
        }

        MapScreenPoint incomingNormal = new(-incoming.Y, incoming.X);
        MapScreenPoint startOffset;
        if (cross > 0)
        {
            startOffset = new MapScreenPoint(
                -incomingNormal.X,
                -incomingNormal.Y);
        }
        else
        {
            startOffset = incomingNormal;
        }

        int segmentCount = GetRoundJoinSegmentCount(dot);
        double sweep = Math.CopySign(
            Math.Atan2(Math.Abs(cross), Math.Clamp(dot, -1, 1)),
            cross);
        (double stepSin, double stepCos) =
            Math.SinCos(sweep / segmentCount);
        MapScreenPoint offset = startOffset;
        MapScreenPoint previousArc = new(
            join.X + (offset.X * radius),
            join.Y + (offset.Y * radius));
        for (int index = 1; index <= segmentCount; index++)
        {
            offset = new MapScreenPoint(
                (offset.X * stepCos) - (offset.Y * stepSin),
                (offset.X * stepSin) + (offset.Y * stepCos));
            MapScreenPoint current = new(
                join.X + (offset.X * radius),
                join.Y + (offset.Y * radius));
            AddTriangle(triangles, ref vertex, join, previousArc, current);
            previousArc = current;
        }
    }

    private static int GetRoundJoinSegmentCount(double dot)
    {
        const double cosine45Degrees = 0.7071067811865476;
        if (dot >= cosine45Degrees)
        {
            return 1;
        }
        if (dot >= 0)
        {
            return 2;
        }
        return dot >= -cosine45Degrees ? 3 : 4;
    }

    private static void AddCircle(
        MapScreenPoint[] triangles,
        ref int vertex,
        MapScreenPoint center,
        double radius)
    {
        const int segmentCount = 8;
        MapScreenPoint previous = new(center.X + radius, center.Y);
        for (int index = 1; index <= segmentCount; index++)
        {
            double angle = index * Math.Tau / segmentCount;
            MapScreenPoint current = new(
                center.X + (Math.Cos(angle) * radius),
                center.Y + (Math.Sin(angle) * radius));
            AddTriangle(triangles, ref vertex, center, previous, current);
            previous = current;
        }
    }

    private static bool TryGetUnitDirection(
        MapScreenPoint start,
        MapScreenPoint end,
        out MapScreenPoint direction)
    {
        double deltaX = end.X - start.X;
        double deltaY = end.Y - start.Y;
        double length = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        if (!double.IsFinite(length) || length <= 1e-7)
        {
            direction = default;
            return false;
        }
        direction = new MapScreenPoint(deltaX / length, deltaY / length);
        return true;
    }

    private static void AddTriangle(
        MapScreenPoint[] triangles,
        ref int vertex,
        MapScreenPoint first,
        MapScreenPoint second,
        MapScreenPoint third)
    {
        triangles[vertex++] = first;
        triangles[vertex++] = second;
        triangles[vertex++] = third;
    }

    internal static bool Contains(
        MapGeometrySnapshot snapshot,
        MapGeometryCamera camera,
        double viewportX,
        double viewportY)
    {
        if (snapshot.IsPolygon &&
            snapshot.FillColor.A != 0 &&
            ContainsFill(snapshot.Geometry, camera, viewportX, viewportY))
        {
            return true;
        }
        if (snapshot.StrokeColor.A == 0 || snapshot.StrokeThickness <= 0)
        {
            return false;
        }

        MapScreenPoint point = new(viewportX, viewportY);
        MapScreenSegment[] segments = BuildStrokeSegments(
            snapshot.Geometry,
            snapshot.IsPolygon,
            snapshot.StrokeThickness,
            snapshot.StrokeDashed,
            camera);
        foreach (MapScreenSegment segment in segments)
        {
            if (StrokeContainsPoint(point, segment, snapshot.StrokeThickness / 2))
            {
               return true;
            }
        }
        return StrokeJoinContainsPoint(
            point,
            segments,
            snapshot.IsPolygon,
            snapshot.StrokeThickness / 2);
    }

    internal static bool TryHitTestAbove(
        IReadOnlyList<MapGeometrySnapshot> geometries,
        bool[] visibleLayers,
        MapGeometryCamera camera,
        double viewportX,
        double viewportY,
        int minimumOrder,
        out int elementIndex,
        out int orderIndex)
    {
        for (int index = geometries.Count - 1; index >= 0; index--)
        {
            MapGeometrySnapshot geometry = geometries[index];
            if (geometry.OrderIndex <= minimumOrder)
            {
                break;
            }
            if (!geometry.IsEnabled ||
                (uint)geometry.LayerIndex >= (uint)visibleLayers.Length ||
                !visibleLayers[geometry.LayerIndex])
            {
                continue;
            }
            if (Contains(geometry, camera, viewportX, viewportY))
            {
                elementIndex = geometry.ElementIndex;
                orderIndex = geometry.OrderIndex;
                return true;
            }
        }

        elementIndex = -1;
        orderIndex = -1;
        return false;
    }

    private static bool ContainsFill(
        MapGeometryData geometry,
        MapGeometryCamera camera,
        double viewportX,
        double viewportY)
    {
        if (!MapCamera.TryGetLocationFromOffset(
            camera.Longitude,
            camera.Latitude,
            camera.Zoom,
            camera.ViewportWidth,
            camera.ViewportHeight,
            viewportX,
            viewportY,
            camera.Heading,
            camera.Pitch,
            out MapCenter location))
        {
            return false;
        }

        double pointX = MapCamera.LongitudeToWorldX(location.Longitude);
        pointX += Math.Round(geometry.AnchorWorldX - pointX);
        double pointY = MapCamera.LatitudeToWorldY(location.Latitude);
        bool inside = false;
        foreach (MapGeometryContour contour in geometry.Contours)
        {
            MapWorldPoint[] points = contour.Points;
            if (points.Length < 3)
            {
                continue;
            }

            int previous = points.Length - 1;
            for (int current = 0; current < points.Length; current++)
            {
                MapWorldPoint first = points[previous];
                MapWorldPoint second = points[current];
                if (PointOnSegment(pointX, pointY, first, second))
                {
                    return true;
                }

                bool crosses = (first.Y > pointY) != (second.Y > pointY);
                if (crosses &&
                    pointX < ((second.X - first.X) * (pointY - first.Y) /
                        (second.Y - first.Y)) + first.X)
                {
                    inside = !inside;
                }
                previous = current;
            }
        }
        return inside;
    }

    private static MapScreenPoint Project(
        MapWorldPoint point,
        double anchorWorldX,
        MapGeometryCamera camera)
    {
        double worldSize = 256 * Math.Pow(
            2,
            Math.Clamp(camera.Zoom, 0, MapCamera.MaximumTileZoom));
        double cameraWorldX = MapCamera.LongitudeToWorldX(camera.Longitude);
        double worldShift = Math.Round(cameraWorldX - anchorWorldX);
        double x = (point.X + worldShift - cameraWorldX) * worldSize;
        double cameraY = MapCamera.LatitudeToWorldY(camera.Latitude) * worldSize;
        double y = (point.Y * worldSize) -
            MapCamera.GetEffectiveCameraY(cameraY, worldSize);
        MapCamera.TransformViewportOffset(
            x,
            y,
            camera.Heading,
            camera.Pitch,
            camera.ViewportHeight,
            out double rotatedX,
            out double rotatedY);
        return new MapScreenPoint(
            rotatedX + (camera.ViewportWidth / 2),
            rotatedY + (camera.ViewportHeight / 2));
    }

    private static bool TriangleIntersectsViewport(
        MapScreenPoint first,
        MapScreenPoint second,
        MapScreenPoint third,
        MapGeometryCamera camera)
    {
        double minX = Math.Min(first.X, Math.Min(second.X, third.X));
        double maxX = Math.Max(first.X, Math.Max(second.X, third.X));
        double minY = Math.Min(first.Y, Math.Min(second.Y, third.Y));
        double maxY = Math.Max(first.Y, Math.Max(second.Y, third.Y));
        return minX < camera.ViewportWidth &&
            minY < camera.ViewportHeight &&
            maxX > 0 &&
            maxY > 0;
    }

    private static void AddDashedSegments(
        List<MapScreenSegment> segments,
        MapScreenPoint start,
        MapScreenPoint end,
        double pathDistance,
        double length,
        double clippedStart,
        double clippedEnd,
        double thickness,
        int pathIndex)
    {
        double dashLength = Math.Max(4, thickness * 3);
        double gapLength = Math.Max(2, thickness * 2);
        double period = dashLength + gapLength;
        double visibleStart = pathDistance + (length * clippedStart);
        double visibleEnd = pathDistance + (length * clippedEnd);
        double cycle = Math.Floor(visibleStart / period);
        double dashStart = cycle * period;
        if (dashStart + dashLength <= visibleStart)
        {
            dashStart += period;
        }

        while (dashStart < visibleEnd &&
            segments.Count < MaximumGeneratedSegmentCount)
        {
            double segmentStart = Math.Max(visibleStart, dashStart);
            double segmentEnd = Math.Min(visibleEnd, dashStart + dashLength);
            if (segmentEnd > segmentStart)
            {
                segments.Add(new MapScreenSegment(
                    Interpolate(start, end, (segmentStart - pathDistance) / length),
                    Interpolate(start, end, (segmentEnd - pathDistance) / length),
                    pathIndex));
            }
            dashStart += period;
        }
    }

    private static bool TryClipSegment(
        MapScreenPoint start,
        MapScreenPoint end,
        double padding,
        double viewportWidth,
        double viewportHeight,
        out double clippedStart,
        out double clippedEnd)
    {
        clippedStart = 0;
        clippedEnd = 1;
        double deltaX = end.X - start.X;
        double deltaY = end.Y - start.Y;
        return Clip(-deltaX, start.X + padding, ref clippedStart, ref clippedEnd) &&
            Clip(deltaX, viewportWidth + padding - start.X, ref clippedStart, ref clippedEnd) &&
            Clip(-deltaY, start.Y + padding, ref clippedStart, ref clippedEnd) &&
            Clip(deltaY, viewportHeight + padding - start.Y, ref clippedStart, ref clippedEnd);
    }

    private static bool Clip(
        double denominator,
        double numerator,
        ref double start,
        ref double end)
    {
        if (Math.Abs(denominator) <= 1e-12)
        {
            return numerator >= 0;
        }
        double value = numerator / denominator;
        if (denominator < 0)
        {
            if (value > end)
            {
                return false;
            }
            start = Math.Max(start, value);
        }
        else
        {
            if (value < start)
            {
                return false;
            }
            end = Math.Min(end, value);
        }
        return true;
    }

    private static MapScreenPoint Interpolate(
        MapScreenPoint start,
        MapScreenPoint end,
        double amount) =>
        new(
            start.X + ((end.X - start.X) * amount),
            start.Y + ((end.Y - start.Y) * amount));

    private static bool StrokeContainsPoint(
        MapScreenPoint point,
        MapScreenSegment segment,
        double halfThickness)
    {
        double deltaX = segment.End.X - segment.Start.X;
        double deltaY = segment.End.Y - segment.Start.Y;
        double lengthSquared = (deltaX * deltaX) + (deltaY * deltaY);
        if (lengthSquared <= 1e-12)
        {
            return false;
        }

        double amount =
            (((point.X - segment.Start.X) * deltaX) +
                ((point.Y - segment.Start.Y) * deltaY)) / lengthSquared;
        if (amount < 0 || amount > 1)
        {
            return false;
        }

        double cross =
            ((point.X - segment.Start.X) * deltaY) -
            ((point.Y - segment.Start.Y) * deltaX);
        return Math.Abs(cross) <= halfThickness * Math.Sqrt(lengthSquared);
    }

    private static bool StrokeJoinContainsPoint(
        MapScreenPoint point,
        IReadOnlyList<MapScreenSegment> segments,
        bool closed,
        double radius)
    {
        int segmentCount = Math.Min(segments.Count, MaximumGeneratedSegmentCount);
        for (int index = 1; index < segmentCount; index++)
        {
            MapScreenSegment previous = segments[index - 1];
            MapScreenSegment next = segments[index];
            if (GetStrokeJoinVertexCount(
                    previous,
                    next) > 0 &&
                PointInCircle(point, previous.End, radius))
            {
                return true;
            }
        }
        if (!closed)
        {
            return false;
        }

        int first = 0;
        while (first < segmentCount)
        {
            int last = first;
            while (last + 1 < segmentCount &&
                segments[last + 1].PathIndex == segments[first].PathIndex)
            {
                last++;
            }
            if (last > first &&
                GetStrokeJoinVertexCount(
                    segments[last],
                    segments[first]) > 0 &&
                PointInCircle(point, segments[last].End, radius))
            {
                return true;
            }
            first = last + 1;
        }
        return false;
    }

    private static bool PointInCircle(
        MapScreenPoint point,
        MapScreenPoint center,
        double radius)
    {
        double deltaX = point.X - center.X;
        double deltaY = point.Y - center.Y;
        return (deltaX * deltaX) + (deltaY * deltaY) <= radius * radius;
    }

    private static bool PointOnSegment(
        double pointX,
        double pointY,
        MapWorldPoint start,
        MapWorldPoint end)
    {
        double cross = ((pointY - start.Y) * (end.X - start.X)) -
            ((pointX - start.X) * (end.Y - start.Y));
        if (Math.Abs(cross) > 1e-12)
        {
            return false;
        }
        return pointX >= Math.Min(start.X, end.X) - 1e-12 &&
            pointX <= Math.Max(start.X, end.X) + 1e-12 &&
            pointY >= Math.Min(start.Y, end.Y) - 1e-12 &&
            pointY <= Math.Max(start.Y, end.Y) + 1e-12;
    }
}
