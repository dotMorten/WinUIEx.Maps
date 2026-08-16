namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Implements normalized Web Mercator camera math, viewport projection, wrapping, and
/// visible-tile scene construction without UI or device dependencies.
/// </summary>
/// <remarks>
/// <para>
/// The render thread uses these pure helpers to normalize camera targets and enumerate
/// wrapped visible tiles at a selected source zoom. Manager workers reuse the same scene
/// construction for acquisition, and the UI thread uses projection helpers for interaction
/// and anchored zoom calculations.
/// </para>
/// <para>
/// Longitude wraps horizontally across repeated worlds; latitude and vertical camera
/// placement clamp to the Web Mercator limit. Invalid or non-finite inputs are normalized or
/// rejected before enumeration so camera state cannot produce unbounded tile or wrap loops.
/// </para>
/// </remarks>
internal static class MapCamera
{
    internal const double MaximumLatitude = 85.05112878;
    internal const double MaximumPitch = 60;
    internal const int MaximumTileZoom = 22;
    internal const double TileSize = 256;

    /// <summary>
    /// Creates a camera scene using the integral floor of the display zoom for tile
    /// enumeration.
    /// </summary>
    internal static MapScene CreateScene(
        double longitude,
        double latitude,
        double zoom,
        double viewportWidth,
        double viewportHeight,
        double heading = 0,
        double pitch = 0)
    {
        double normalizedZoom = NormalizeZoom(zoom);
        return CreateScene(
            longitude,
            latitude,
            normalizedZoom,
            Math.Min((int)Math.Floor(normalizedZoom), MaximumTileZoom),
            viewportWidth,
            viewportHeight,
            heading,
            pitch);
    }

    /// <summary>
    /// Normalizes camera input, clamps vertical coverage, and enumerates visible wrapped
    /// Web Mercator tiles for an explicitly selected source zoom.
    /// </summary>
    internal static MapScene CreateScene(
        double longitude,
        double latitude,
        double zoom,
        int tileZoom,
        double viewportWidth,
        double viewportHeight,
        double heading = 0,
        double pitch = 0)
    {
        double normalizedZoom = NormalizeZoom(zoom);
        double normalizedLatitude = double.IsFinite(latitude)
            ? Math.Clamp(latitude, -MaximumLatitude, MaximumLatitude)
            : 0;
        tileZoom = Math.Clamp(tileZoom, 0, MaximumTileZoom);
        int tileCount = 1 << tileZoom;
        double tileDisplaySize = TileSize * Math.Pow(2, normalizedZoom - tileZoom);
        double worldDisplaySize = tileCount * tileDisplaySize;
        double centerX = LongitudeToWorldX(longitude) * worldDisplaySize;
        double centerY = LatitudeToWorldY(normalizedLatitude) * worldDisplaySize;

        centerY = GetEffectiveCameraY(centerY, worldDisplaySize);

        double normalizedHeading = NormalizeHeading(heading);
        double normalizedPitch = NormalizePitch(pitch);
        GetMapPlaneViewportBounds(
            viewportWidth,
            viewportHeight,
            normalizedHeading,
            normalizedPitch,
            out double minimumX,
            out double minimumY,
            out double maximumX,
            out double maximumY);
        double coverageLeft = centerX + minimumX;
        double coverageTop = centerY + minimumY;
        double coverageWidth = maximumX - minimumX;
        double coverageHeight = maximumY - minimumY;
        double viewportLeft = centerX - (viewportWidth / 2);
        double viewportTop = centerY - (viewportHeight / 2);
        int firstWorldX = (int)Math.Floor(coverageLeft / tileDisplaySize);
        int lastWorldX = (int)Math.Floor(
            (coverageLeft + Math.Max(0, coverageWidth) - 1e-7) / tileDisplaySize);
        int firstY = Math.Max(0, (int)Math.Floor(coverageTop / tileDisplaySize));
        int lastY = Math.Min(
            tileCount - 1,
            (int)Math.Floor(
                (coverageTop + Math.Max(0, coverageHeight) - 1e-7) /
                tileDisplaySize));

        List<VisibleTile> visibleTiles = [];
        if (viewportWidth > 0 && viewportHeight > 0 && firstY <= lastY)
        {
            for (int y = firstY; y <= lastY; y++)
            {
                for (int worldX = firstWorldX; worldX <= lastWorldX; worldX++)
                {
                    int x = Mod(worldX, tileCount);
                    visibleTiles.Add(new VisibleTile(
                        new TileId(tileZoom, x, y),
                        worldX,
                        (worldX * tileDisplaySize) - viewportLeft,
                        (y * tileDisplaySize) - viewportTop,
                        tileDisplaySize));
                }
            }
        }

        return new MapScene(
            normalizedZoom,
            tileZoom,
            NormalizeLongitude(longitude),
            normalizedLatitude,
            viewportWidth,
            viewportHeight,
            normalizedHeading,
            normalizedPitch,
            visibleTiles);
    }

    /// <summary>
    /// Converts longitude to a wrapped, normalized Web Mercator horizontal coordinate.
    /// </summary>
    internal static double LongitudeToWorldX(double longitude)
    {
        return (NormalizeLongitude(longitude) + 180) / 360;
    }

    /// <summary>
    /// Converts latitude to a normalized Web Mercator vertical coordinate after clamping the
    /// poles to the projection limit.
    /// </summary>
    internal static double LatitudeToWorldY(double latitude)
    {
        double radians = Math.Clamp(latitude, -MaximumLatitude, MaximumLatitude) * Math.PI / 180;
        return (1 - (Math.Log(Math.Tan(radians) + (1 / Math.Cos(radians))) / Math.PI)) / 2;
    }

    /// <summary>
    /// Translates a camera center by a screen-space drag measured in pixels at the current
    /// zoom.
    /// </summary>
    internal static MapCenter PanByPixels(
        double longitude,
        double latitude,
        double zoom,
        double horizontalDelta,
        double verticalDelta,
        double heading = 0,
        double pitch = 0,
        double viewportHeight = 0)
    {
        UntransformViewportOffset(
            horizontalDelta,
            verticalDelta,
            heading,
            pitch,
            viewportHeight,
            out horizontalDelta,
            out verticalDelta);
        double worldSize = TileSize * Math.Pow(2, NormalizeZoom(zoom));
        double worldX = LongitudeToWorldX(longitude) - (horizontalDelta / worldSize);
        double worldY = LatitudeToWorldY(latitude) - (verticalDelta / worldSize);
        return new MapCenter(WorldXToLongitude(worldX), WorldYToLatitude(worldY));
    }

    /// <summary>
    /// Converts a relative pinch scale into an additive map zoom delta.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when <paramref name="scale"/> is finite and positive;
    /// otherwise, <paramref name="zoomDelta"/> is zero.
    /// </returns>
    internal static bool TryGetZoomDeltaFromScale(double scale, out double zoomDelta)
    {
        zoomDelta = 0;
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return false;
        }

        zoomDelta = Math.Log2(scale);
        return double.IsFinite(zoomDelta);
    }

    /// <summary>
    /// Resolves the geographic location at a pixel offset from a camera center.
    /// </summary>
    internal static MapCenter LocationAtOffset(
        double longitude,
        double latitude,
        double zoom,
        double horizontalOffset,
        double verticalOffset,
        double heading = 0,
        double pitch = 0,
        double viewportHeight = 0)
    {
        UntransformViewportOffset(
            horizontalOffset,
            verticalOffset,
            heading,
            pitch,
            viewportHeight,
            out horizontalOffset,
            out verticalOffset);
        double worldSize = TileSize * Math.Pow(2, NormalizeZoom(zoom));
        double worldX = LongitudeToWorldX(longitude) + (horizontalOffset / worldSize);
        double worldY = LatitudeToWorldY(latitude) + (verticalOffset / worldSize);
        return new MapCenter(WorldXToLongitude(worldX), WorldYToLatitude(worldY));
    }

    /// <summary>
    /// Resolves a viewport point to a geographic location when the viewport and point are
    /// finite and in bounds.
    /// </summary>
    internal static bool TryGetLocationFromOffset(
        double longitude,
        double latitude,
        double zoom,
        double viewportWidth,
        double viewportHeight,
        double offsetX,
        double offsetY,
        out MapCenter location)
    {
        return TryGetLocationFromOffset(
            longitude,
            latitude,
            zoom,
            viewportWidth,
            viewportHeight,
            offsetX,
            offsetY,
            0,
            0,
            out location);
    }

    internal static bool TryGetLocationFromOffset(
        double longitude,
        double latitude,
        double zoom,
        double viewportWidth,
        double viewportHeight,
        double offsetX,
        double offsetY,
        double heading,
        out MapCenter location)
    {
        return TryGetLocationFromOffset(
            longitude,
            latitude,
            zoom,
            viewportWidth,
            viewportHeight,
            offsetX,
            offsetY,
            heading,
            0,
            out location);
    }

    internal static bool TryGetLocationFromOffset(
        double longitude,
        double latitude,
        double zoom,
        double viewportWidth,
        double viewportHeight,
        double offsetX,
        double offsetY,
        double heading,
        double pitch,
        out MapCenter location)
    {
        location = default;
        if (!double.IsFinite(offsetX) ||
            !double.IsFinite(offsetY) ||
            !double.IsFinite(viewportWidth) ||
            !double.IsFinite(viewportHeight) ||
            viewportWidth <= 0 ||
            viewportHeight <= 0 ||
            offsetX < 0 ||
            offsetX > viewportWidth ||
            offsetY < 0 ||
            offsetY > viewportHeight)
        {
            return false;
        }

        location = LocationAtOffset(
            longitude,
            latitude,
            zoom,
            offsetX - (viewportWidth / 2),
            offsetY - (viewportHeight / 2),
            heading,
            pitch,
            viewportHeight);
        return true;
    }

    /// <summary>
    /// Computes the camera center that keeps a geographic anchor at a specified pixel offset.
    /// </summary>
    internal static MapCenter CenterForLocationAtOffset(
        MapCenter location,
        double zoom,
        double horizontalOffset,
        double verticalOffset,
        double heading = 0,
        double pitch = 0,
        double viewportHeight = 0)
    {
        UntransformViewportOffset(
            horizontalOffset,
            verticalOffset,
            heading,
            pitch,
            viewportHeight,
            out horizontalOffset,
            out verticalOffset);
        double worldSize = TileSize * Math.Pow(2, NormalizeZoom(zoom));
        double worldX = LongitudeToWorldX(location.Longitude) - (horizontalOffset / worldSize);
        double worldY = LatitudeToWorldY(location.Latitude) - (verticalOffset / worldSize);
        return new MapCenter(WorldXToLongitude(worldX), WorldYToLatitude(worldY));
    }

    /// <summary>
    /// Projects a geographic location into viewport coordinates using wrapped horizontal
    /// distance and vertically clamped camera coverage.
    /// </summary>
    internal static bool TryProjectLocation(
        double locationLongitude,
        double locationLatitude,
        double cameraLongitude,
        double cameraLatitude,
        double zoom,
        double viewportWidth,
        double viewportHeight,
        out MapViewportPoint point)
    {
        return TryProjectLocation(
            locationLongitude,
            locationLatitude,
            cameraLongitude,
            cameraLatitude,
            zoom,
            viewportWidth,
            viewportHeight,
            0,
            0,
            out point);
    }

    internal static bool TryProjectLocation(
        double locationLongitude,
        double locationLatitude,
        double cameraLongitude,
        double cameraLatitude,
        double zoom,
        double viewportWidth,
        double viewportHeight,
        double heading,
        out MapViewportPoint point)
    {
        return TryProjectLocation(
            locationLongitude,
            locationLatitude,
            cameraLongitude,
            cameraLatitude,
            zoom,
            viewportWidth,
            viewportHeight,
            heading,
            0,
            out point);
    }

    internal static bool TryProjectLocation(
        double locationLongitude,
        double locationLatitude,
        double cameraLongitude,
        double cameraLatitude,
        double zoom,
        double viewportWidth,
        double viewportHeight,
        double heading,
        double pitch,
        out MapViewportPoint point)
    {
        point = default;
        if (!double.IsFinite(locationLongitude) ||
            !double.IsFinite(locationLatitude) ||
            !double.IsFinite(cameraLongitude) ||
            !double.IsFinite(cameraLatitude) ||
            !double.IsFinite(viewportWidth) ||
            !double.IsFinite(viewportHeight) ||
            viewportWidth <= 0 ||
            viewportHeight <= 0)
        {
            return false;
        }

        double worldSize = TileSize * Math.Pow(2, NormalizeZoom(zoom));
        double deltaX = LongitudeToWorldX(locationLongitude) - LongitudeToWorldX(cameraLongitude);
        deltaX -= Math.Round(deltaX);
        double x = deltaX * worldSize;
        double cameraY = LatitudeToWorldY(cameraLatitude) * worldSize;
        double effectiveCameraY = GetEffectiveCameraY(cameraY, worldSize);
        double y = (LatitudeToWorldY(locationLatitude) * worldSize) -
            effectiveCameraY;
        TransformViewportOffset(
            x,
            y,
            heading,
            pitch,
            viewportHeight,
            out x,
            out y);
        x += viewportWidth / 2;
        y += viewportHeight / 2;
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            return false;
        }

        point = new MapViewportPoint(x, y);
        return true;
    }

    internal static double NormalizeHeading(double heading)
    {
        if (!double.IsFinite(heading))
        {
            return 0;
        }

        double normalized = ((heading % 360) + 360) % 360;
        return normalized == 360 ? 0 : normalized;
    }

    internal static double NormalizePitch(double pitch)
    {
        return double.IsFinite(pitch)
            ? Math.Clamp(pitch, 0, MaximumPitch)
            : 0;
    }

    internal static double ShortestHeadingDelta(double from, double to)
    {
        double delta = NormalizeHeading(to) - NormalizeHeading(from);
        return ((delta + 540) % 360) - 180;
    }

    internal static void GetUnrotatedViewportSize(
        double width,
        double height,
        double heading,
        out double unrotatedWidth,
        out double unrotatedHeight)
    {
        double radians = NormalizeHeading(heading) * Math.PI / 180;
        double cosine = Math.Abs(Math.Cos(radians));
        double sine = Math.Abs(Math.Sin(radians));
        unrotatedWidth = (width * cosine) + (height * sine);
        unrotatedHeight = (width * sine) + (height * cosine);
    }

    internal static void TransformViewportOffset(
        double x,
        double y,
        double heading,
        double pitch,
        double viewportHeight,
        out double transformedX,
        out double transformedY)
    {
        RotateViewportOffset(x, y, heading, out double rotatedX, out double rotatedY);
        double radians = NormalizePitch(pitch) * Math.PI / 180;
        if (radians == 0)
        {
            transformedX = rotatedX;
            transformedY = rotatedY;
            return;
        }

        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        double distance = GetPerspectiveDistance(viewportHeight);
        double scale = distance / (distance - (rotatedY * sine));
        transformedX = rotatedX * scale;
        transformedY = rotatedY * cosine * scale;
    }

    internal static void UntransformViewportOffset(
        double x,
        double y,
        double heading,
        double pitch,
        double viewportHeight,
        out double untransformedX,
        out double untransformedY)
    {
        double radians = NormalizePitch(pitch) * Math.PI / 180;
        double unpitchedX = x;
        double unpitchedY = y;
        if (radians != 0)
        {
            double cosine = Math.Cos(radians);
            double sine = Math.Sin(radians);
            double distance = GetPerspectiveDistance(viewportHeight);
            double denominator = (cosine * distance) + (y * sine);
            unpitchedY = denominator == 0 ? 0 : (y * distance) / denominator;
            unpitchedX = x * (distance - (unpitchedY * sine)) / distance;
        }

        UnrotateViewportOffset(
            unpitchedX,
            unpitchedY,
            heading,
            out untransformedX,
            out untransformedY);
    }

    internal static void GetMapPlaneViewportBounds(
        double width,
        double height,
        double heading,
        double pitch,
        out double minimumX,
        out double minimumY,
        out double maximumX,
        out double maximumY)
    {
        minimumX = double.PositiveInfinity;
        minimumY = double.PositiveInfinity;
        maximumX = double.NegativeInfinity;
        maximumY = double.NegativeInfinity;
        double halfWidth = width / 2;
        double halfHeight = height / 2;
        ReadOnlySpan<(double X, double Y)> corners =
        [
            (-halfWidth, -halfHeight),
            (halfWidth, -halfHeight),
            (-halfWidth, halfHeight),
            (halfWidth, halfHeight),
        ];
        foreach ((double X, double Y) corner in corners)
        {
            UntransformViewportOffset(
                corner.X,
                corner.Y,
                heading,
                pitch,
                height,
                out double x,
                out double y);
            minimumX = Math.Min(minimumX, x);
            minimumY = Math.Min(minimumY, y);
            maximumX = Math.Max(maximumX, x);
            maximumY = Math.Max(maximumY, y);
        }
    }

    private static void RotateViewportOffset(
        double x,
        double y,
        double heading,
        out double rotatedX,
        out double rotatedY)
    {
        double radians = NormalizeHeading(heading) * Math.PI / 180;
        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        rotatedX = (x * cosine) + (y * sine);
        rotatedY = (-x * sine) + (y * cosine);
    }

    private static void UnrotateViewportOffset(
        double x,
        double y,
        double heading,
        out double unrotatedX,
        out double unrotatedY)
    {
        RotateViewportOffset(x, y, -heading, out unrotatedX, out unrotatedY);
    }

    internal static double GetPerspectiveDistance(double viewportHeight) =>
        Math.Max(1, viewportHeight * 2);

    internal static double GetEffectiveCameraY(
        double cameraY,
        double worldSize)
    {
        return Math.Clamp(cameraY, 0, worldSize);
    }

    /// <summary>
    /// Determines whether a finite, positive rectangle intersects a valid viewport.
    /// </summary>
    internal static bool IsRectangleVisible(
        double left,
        double top,
        double width,
        double height,
        double viewportWidth,
        double viewportHeight)
    {
        return double.IsFinite(left) &&
            double.IsFinite(top) &&
            double.IsFinite(width) &&
            double.IsFinite(height) &&
            double.IsFinite(viewportWidth) &&
            double.IsFinite(viewportHeight) &&
            width > 0 &&
            height > 0 &&
            viewportWidth > 0 &&
            viewportHeight > 0 &&
            left < viewportWidth &&
            top < viewportHeight &&
            left + width > 0 &&
            top + height > 0;
    }

    /// <summary>
    /// Converts a normalized world coordinate to longitude while wrapping repeated worlds.
    /// </summary>
    internal static double WorldXToLongitude(double worldX)
    {
        double wrapped = ((worldX % 1) + 1) % 1;
        return (wrapped * 360) - 180;
    }

    /// <summary>
    /// Converts a normalized Web Mercator vertical coordinate to a projection-safe latitude.
    /// </summary>
    internal static double WorldYToLatitude(double worldY)
    {
        double clamped = Math.Clamp(worldY, 0, 1);
        double radians = Math.Atan(Math.Sinh(Math.PI * (1 - (2 * clamped))));
        return Math.Clamp(radians * 180 / Math.PI, -MaximumLatitude, MaximumLatitude);
    }

    /// <summary>
    /// Wraps finite longitude into the canonical range while preserving positive 180 degrees.
    /// </summary>
    private static double NormalizeLongitude(double longitude)
    {
        if (!double.IsFinite(longitude))
        {
            return 0;
        }

        double wrapped = ((longitude + 180) % 360 + 360) % 360 - 180;
        return wrapped == -180 && longitude > 0 ? 180 : wrapped;
    }

    /// <summary>
    /// Converts non-finite zoom to zero and clamps finite input to supported levels.
    /// </summary>
    private static double NormalizeZoom(double zoom)
    {
        return double.IsFinite(zoom) ? Math.Clamp(zoom, 0, MaximumTileZoom) : 0;
    }

    /// <summary>
    /// Computes a nonnegative modulus used to wrap horizontal tile indexes.
    /// </summary>
    private static int Mod(int value, int divisor)
    {
        int remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }
}

internal readonly record struct MapViewportProjectiveTransform(
    System.Numerics.Vector4 X,
    System.Numerics.Vector4 Y)
{
    internal bool IsFinite =>
        IsFiniteVector(X) &&
        IsFiniteVector(Y);

    internal static MapViewportProjectiveTransform CreateTranslation(
        double offsetX,
        double offsetY) =>
        new(
            new System.Numerics.Vector4(1, 0, (float)offsetX, 0),
            new System.Numerics.Vector4(0, 1, (float)offsetY, 0));

    internal static MapViewportProjectiveTransform CreatePan(
        double offsetX,
        double offsetY,
        double pitch,
        double viewportHeight)
    {
        double radians = MapCamera.NormalizePitch(pitch) * Math.PI / 180;
        if (radians == 0)
        {
            return CreateTranslation(offsetX, offsetY);
        }

        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        double distance = MapCamera.GetPerspectiveDistance(viewportHeight);
        double denominator = distance - (offsetY * sine);
        if (Math.Abs(cosine) < 1e-7 ||
            Math.Abs(denominator) < 1e-7)
        {
            return default;
        }

        return new MapViewportProjectiveTransform(
            new System.Numerics.Vector4(
                (float)(distance / denominator),
                (float)(offsetX * sine / (cosine * denominator)),
                (float)(offsetX * distance / denominator),
                0),
            new System.Numerics.Vector4(
                0,
                (float)((distance + (offsetY * sine)) / denominator),
                (float)(offsetY * cosine * distance / denominator),
                (float)(-offsetY * sine * sine /
                    (cosine * distance * denominator))));
    }

    internal MapScreenPoint Transform(MapScreenPoint point)
    {
        double denominator =
            (X.W * point.X) +
            (Y.W * point.Y) +
            1;
        return new MapScreenPoint(
            ((X.X * point.X) + (X.Y * point.Y) + X.Z) / denominator,
            ((Y.X * point.X) + (Y.Y * point.Y) + Y.Z) / denominator);
    }

    private static bool IsFiniteVector(System.Numerics.Vector4 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);
}

/// <summary>
/// Represents a geographic camera or anchor location in longitude and latitude degrees.
/// </summary>
internal readonly record struct MapCenter(double Longitude, double Latitude);

/// <summary>
/// Represents a projected point in viewport pixel coordinates.
/// </summary>
internal readonly record struct MapViewportPoint(double X, double Y);

/// <summary>
/// Captures one normalized camera frame, selected tile zoom, viewport, and ordered visible
/// tile instances.
/// </summary>
/// <remarks>
/// Display scenes are created on the render thread and passed as immutable scheduling input
/// to the raster manager; manager workers derive source-zoom scenes with the same camera
/// math. <see cref="RequiredTiles"/> deduplicates horizontal wraps and prioritizes unique
/// tiles nearest the viewport center, defining request batch order without changing
/// draw-instance ordering.
/// </remarks>
internal sealed record MapScene(
    double Zoom,
    int TileZoom,
    double Longitude,
    double Latitude,
    double ViewportWidth,
    double ViewportHeight,
    double Heading,
    double Pitch,
    IReadOnlyList<VisibleTile> VisibleTiles)
{
    internal IReadOnlyList<TileId> RequiredTiles
    {
        get
        {
            double viewportCenterX = ViewportWidth / 2;
            double viewportCenterY = ViewportHeight / 2;
            return VisibleTiles
                .Select((tile, index) => new
                {
                    tile.Id,
                    Index = index,
                    DistanceSquared =
                        Math.Pow((tile.Left + (tile.Size / 2)) - viewportCenterX, 2) +
                        Math.Pow((tile.Top + (tile.Size / 2)) - viewportCenterY, 2),
                })
                .GroupBy(tile => tile.Id)
                .Select(group => group
                    .OrderBy(tile => tile.DistanceSquared)
                    .ThenBy(tile => tile.Index)
                    .First())
                .OrderBy(tile => tile.DistanceSquared)
                .ThenBy(tile => tile.Index)
                .Select(tile => tile.Id)
                .ToArray();
        }
    }
}
