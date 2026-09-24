using System.Diagnostics;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Owns a programmatic camera transition on the render thread.
/// </summary>
/// <remarks>
/// Unlike the independent input animations, every component of this animation is sampled
/// from one normalized timeline. This keeps a <c>TrySetViewAsync</c>
/// camera transition coherent even when its center, zoom, heading, and pitch all change.
/// </remarks>
internal sealed class CameraAnimation
{
    private const double PanDurationMilliseconds = 500;
    private const double HeadingAndPitchDurationMilliseconds = 300;
    private const double ZoomMillisecondsPerLevel = 500;
    private const double MinimumZoomDurationMilliseconds = 220;
    private const double MaximumZoomDurationMilliseconds = 1000;
    // These tune Bow's elevated Bezier control points in ground-height space.
    private const double BowHorizontalControlFraction = 0.25;
    private const double BowTopControlHeightFraction = 0.95;
    private const double BowZoomOutLevelsPerViewportDoubling = 0.8;
    private double _startWorldX;
    private double _startWorldY;
    private double _startHeight;
    private double _firstControlWorldX;
    private double _firstControlWorldY;
    private double _firstControlHeight;
    private double _secondControlWorldX;
    private double _secondControlWorldY;
    private double _secondControlHeight;
    private double _targetWorldX;
    private double _targetWorldY;
    private double _targetHeight;
    private double _startZoom;
    private double _targetZoom;
    private double _bowZoomOutLevels;
    private double _startHeading;
    private double _headingDelta;
    private double _startPitch;
    private double _targetPitch;
    private long _startTimestamp;
    private double _durationMilliseconds;
    private MapAnimationKind _animationKind;

    internal bool IsActive { get; private set; }

    internal MapCenter TargetCenter { get; private set; }

    internal double TargetZoom { get; private set; }

    internal double TargetHeading { get; private set; }

    internal double TargetPitch { get; private set; }

    internal void Reset(
        double longitude,
        double latitude,
        double zoom,
        double heading,
        double pitch)
    {
        _startWorldX = MapCamera.LongitudeToWorldX(longitude);
        _startWorldY = MapCamera.LatitudeToWorldY(latitude);
        _targetWorldX = _startWorldX;
        _targetWorldY = _startWorldY;
        _startHeight = Math.Pow(2, -zoom);
        _firstControlWorldX = _startWorldX;
        _firstControlWorldY = _startWorldY;
        _firstControlHeight = _startHeight;
        _secondControlWorldX = _startWorldX;
        _secondControlWorldY = _startWorldY;
        _secondControlHeight = _startHeight;
        _targetHeight = _startHeight;
        _startZoom = zoom;
        _targetZoom = zoom;
        _bowZoomOutLevels = 0;
        _startHeading = MapCamera.NormalizeHeading(heading);
        _headingDelta = 0;
        _startPitch = MapCamera.NormalizePitch(pitch);
        _targetPitch = _startPitch;
        _startTimestamp = 0;
        TargetCenter = new MapCenter(longitude, latitude);
        TargetZoom = zoom;
        TargetHeading = _startHeading;
        TargetPitch = _startPitch;
        IsActive = false;
    }

    internal bool HasTarget(
        MapCenter center,
        double zoom,
        double heading,
        double pitch) =>
        TargetCenter == center &&
        TargetZoom == zoom &&
        TargetHeading == MapCamera.NormalizeHeading(heading) &&
        TargetPitch == MapCamera.NormalizePitch(pitch);

    internal void SetTarget(
        double currentLongitude,
        double currentLatitude,
        double currentZoom,
        double currentHeading,
        double currentPitch,
        double targetLongitude,
        double targetLatitude,
        double targetZoom,
        double targetHeading,
        double targetPitch,
        double viewportWidth,
        double viewportHeight,
        long timestamp,
        MapAnimationKind animationKind,
        double? durationMilliseconds = null)
    {
        _startWorldX = MapCamera.LongitudeToWorldX(currentLongitude);
        _startWorldY = MapCamera.LatitudeToWorldY(currentLatitude);
        double targetWorldX = MapCamera.LongitudeToWorldX(targetLongitude);
        double horizontalDelta = targetWorldX - _startWorldX;
        if (horizontalDelta > 0.5)
        {
            horizontalDelta -= 1;
        }
        else if (horizontalDelta < -0.5)
        {
            horizontalDelta += 1;
        }

        _targetWorldX = _startWorldX + horizontalDelta;
        _targetWorldY = MapCamera.LatitudeToWorldY(targetLatitude);
        _startZoom = NormalizeZoom(currentZoom);
        _targetZoom = NormalizeZoom(targetZoom);
        _startHeight = Math.Pow(2, -_startZoom);
        _targetHeight = Math.Pow(2, -_targetZoom);
        _startHeading = MapCamera.NormalizeHeading(currentHeading);
        TargetHeading = MapCamera.NormalizeHeading(targetHeading);
        _headingDelta = MapCamera.ShortestHeadingDelta(_startHeading, TargetHeading);
        _startPitch = MapCamera.NormalizePitch(currentPitch);
        _targetPitch = MapCamera.NormalizePitch(targetPitch);
        _animationKind = animationKind;
        _startTimestamp = timestamp;
        TargetCenter = new MapCenter(targetLongitude, targetLatitude);
        TargetZoom = _targetZoom;
        TargetPitch = _targetPitch;

        bool centerChanged = Math.Abs(horizontalDelta) > double.Epsilon ||
            Math.Abs(_targetWorldY - _startWorldY) > double.Epsilon;
        bool zoomChanged = _targetZoom != _startZoom;
        bool headingChanged = Math.Abs(_headingDelta) > double.Epsilon;
        bool pitchChanged = _targetPitch != _startPitch;
        _durationMilliseconds = durationMilliseconds ?? GetDurationMilliseconds(
            centerChanged,
            zoomChanged,
            _startZoom,
            _targetZoom,
            headingChanged,
            pitchChanged,
            GetWorldDistance(
                currentLongitude,
                currentLatitude,
                targetLongitude,
                targetLatitude));

        bool zoomingOutWithinTarget = animationKind == MapAnimationKind.Bow &&
            _targetZoom < _startZoom &&
            MapCamera.TryProjectLocation(
                currentLongitude, currentLatitude,
                targetLongitude, targetLatitude, _targetZoom,
                viewportWidth, viewportHeight, TargetHeading, _targetPitch,
                out MapViewportPoint sourceInTarget) &&
            sourceInTarget.X >= 0 && sourceInTarget.X <= viewportWidth &&
            sourceInTarget.Y >= 0 && sourceInTarget.Y <= viewportHeight;
        double zoomOutLevels = animationKind == MapAnimationKind.Bow && centerChanged && !zoomingOutWithinTarget
            ? GetBowZoomOutLevels(
                currentLongitude,
                currentLatitude,
                targetLongitude,
                targetLatitude,
                _startZoom,
                viewportWidth,
                viewportHeight)
            : 0;
        _bowZoomOutLevels = Math.Min(
            zoomOutLevels,
            Math.Min(_startZoom, _targetZoom));
        ConfigureFlightPath();
        IsActive = centerChanged || zoomChanged || headingChanged || pitchChanged ||
            _bowZoomOutLevels > 0;
    }

    internal void GetCamera(
        long timestamp,
        out MapCenter center,
        out double zoom,
        out double heading,
        out double pitch)
    {
        if (!IsActive)
        {
            center = TargetCenter;
            zoom = TargetZoom;
            heading = TargetHeading;
            pitch = TargetPitch;
            return;
        }

        double progress = Math.Clamp(
            Stopwatch.GetElapsedTime(_startTimestamp, timestamp).TotalMilliseconds /
                _durationMilliseconds,
            0,
            1);
        double easedProgress = Ease(progress, _animationKind);
        double centerProgress = easedProgress;
        double worldX = EvaluateBezier(
            _startWorldX,
            _firstControlWorldX,
            _secondControlWorldX,
            _targetWorldX,
            centerProgress);
        double worldY = EvaluateBezier(
            _startWorldY,
            _firstControlWorldY,
            _secondControlWorldY,
            _targetWorldY,
            centerProgress);
        double height = EvaluateBezier(
            _startHeight,
            _firstControlHeight,
            _secondControlHeight,
            _targetHeight,
            centerProgress);
        center = new MapCenter(
            MapCamera.WorldXToLongitude(worldX),
            MapCamera.WorldYToLatitude(worldY));
        zoom = Math.Clamp(-Math.Log2(height), 0, MapCamera.MaximumTileZoom);
        heading = MapCamera.NormalizeHeading(_startHeading + (_headingDelta * easedProgress));
        pitch = _startPitch + ((_targetPitch - _startPitch) * easedProgress);
        if (progress >= 1)
        {
            center = TargetCenter;
            zoom = TargetZoom;
            heading = TargetHeading;
            pitch = TargetPitch;
            IsActive = false;
        }
    }

    internal static double GetBowZoomOutLevels(
        double currentLongitude,
        double currentLatitude,
        double targetLongitude,
        double targetLatitude,
        double currentZoom,
        double viewportWidth,
        double viewportHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return 0;
        }

        double worldPixels = 256 * Math.Pow(2, currentZoom);
        double horizontalDistance = Math.Abs(
            MapCamera.LongitudeToWorldX(targetLongitude) -
            MapCamera.LongitudeToWorldX(currentLongitude));
        horizontalDistance = Math.Min(horizontalDistance, 1 - horizontalDistance);
        double verticalDistance = Math.Abs(
            MapCamera.LatitudeToWorldY(targetLatitude) -
            MapCamera.LatitudeToWorldY(currentLatitude));
        double viewportDistance = Math.Max(
            (horizontalDistance * worldPixels) / (viewportWidth / 2),
            (verticalDistance * worldPixels) / (viewportHeight / 2));
        if (viewportDistance <= 1)
        {
            return 0;
        }

        return Math.Clamp(
            0.2 + (BowZoomOutLevelsPerViewportDoubling * Math.Log2(viewportDistance)),
            0.2,
            5);
    }

    internal static double Ease(double progress, MapAnimationKind animation) =>
        animation switch
        {
            MapAnimationKind.None => 1,
            MapAnimationKind.Linear => progress,
            MapAnimationKind.Bow => 1 - Math.Pow(1 - progress, 3),
            _ => 1 - Math.Pow(1 - progress, 3),
        };

    private void ConfigureFlightPath()
    {
        _firstControlWorldX = _startWorldX +
            ((_targetWorldX - _startWorldX) / 3);
        _firstControlWorldY = _startWorldY +
            ((_targetWorldY - _startWorldY) / 3);
        _secondControlWorldX = _startWorldX +
            (2 * (_targetWorldX - _startWorldX) / 3);
        _secondControlWorldY = _startWorldY +
            (2 * (_targetWorldY - _startWorldY) / 3);
        _firstControlHeight = _startHeight +
            ((_targetHeight - _startHeight) / 3);
        _secondControlHeight = _startHeight +
            (2 * (_targetHeight - _startHeight) / 3);
        if (_animationKind != MapAnimationKind.Bow || _bowZoomOutLevels <= 0)
        {
            return;
        }

        double apexHeight = Math.Max(_startHeight, _targetHeight) *
            Math.Pow(2, _bowZoomOutLevels);
        double midpointHeight = (_startHeight + _targetHeight) / 2;
        double controlHeight = midpointHeight +
            ((apexHeight - midpointHeight) * BowTopControlHeightFraction);
        _firstControlWorldX = _startWorldX +
            ((_targetWorldX - _startWorldX) * BowHorizontalControlFraction);
        _firstControlWorldY = _startWorldY +
            ((_targetWorldY - _startWorldY) * BowHorizontalControlFraction);
        _secondControlWorldX = _targetWorldX -
            ((_targetWorldX - _startWorldX) * BowHorizontalControlFraction);
        _secondControlWorldY = _targetWorldY -
            ((_targetWorldY - _startWorldY) * BowHorizontalControlFraction);
        _firstControlHeight = controlHeight;
        _secondControlHeight = controlHeight;
    }

    private static double EvaluateBezier(
        double start,
        double firstControl,
        double secondControl,
        double target,
        double progress)
    {
        double inverseProgress = 1 - progress;
        return (inverseProgress * inverseProgress * inverseProgress * start) +
            (3 * inverseProgress * inverseProgress * progress * firstControl) +
            (3 * inverseProgress * progress * progress * secondControl) +
            (progress * progress * progress * target);
    }

    private static double GetDurationMilliseconds(
        bool centerChanged,
        bool zoomChanged,
        double currentZoom,
        double targetZoom,
        bool headingChanged,
        bool pitchChanged,
        double worldDistance)
    {
        double duration = centerChanged
            ? Math.Clamp(
                500 + (Math.Log2(Math.Max(
                    1,
                    worldDistance * Math.Pow(2, Math.Max(currentZoom, targetZoom)))) * 150),
                PanDurationMilliseconds,
                2000)
            : 0;
        if (zoomChanged)
        {
            duration = Math.Max(
                duration,
                Math.Clamp(
                    Math.Abs(targetZoom - currentZoom) * ZoomMillisecondsPerLevel,
                    MinimumZoomDurationMilliseconds,
                    MaximumZoomDurationMilliseconds));
        }
        if (headingChanged || pitchChanged)
        {
            duration = Math.Max(duration, HeadingAndPitchDurationMilliseconds);
        }

        return duration;
    }

    private static double GetWorldDistance(
        double currentLongitude,
        double currentLatitude,
        double targetLongitude,
        double targetLatitude)
    {
        double horizontalDistance = Math.Abs(
            MapCamera.LongitudeToWorldX(targetLongitude) -
            MapCamera.LongitudeToWorldX(currentLongitude));
        horizontalDistance = Math.Min(horizontalDistance, 1 - horizontalDistance);
        double verticalDistance = Math.Abs(
            MapCamera.LatitudeToWorldY(targetLatitude) -
            MapCamera.LatitudeToWorldY(currentLatitude));
        return Math.Sqrt(
            (horizontalDistance * horizontalDistance) +
            (verticalDistance * verticalDistance));
    }

    private static double NormalizeZoom(double zoom) =>
        double.IsFinite(zoom) ? Math.Clamp(zoom, 0, MapCamera.MaximumTileZoom) : 0;
}
