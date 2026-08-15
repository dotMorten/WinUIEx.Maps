using System.Diagnostics;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Maintains render-thread pan interpolation in normalized world coordinates with
/// antimeridian-aware shortest-path movement.
/// </summary>
/// <remarks>
/// The render thread owns instances. Retargeting first samples an active animation, then
/// starts a cubic ease-out transition from that displayed position so rapid UI camera
/// publications remain continuous.
/// </remarks>
internal sealed class PanAnimation
{
    private const double DurationMilliseconds = 500;
    private double _currentWorldX;
    private double _currentWorldY;
    private double _startWorldX;
    private double _startWorldY;
    private double _targetWorldX;
    private double _targetWorldY;
    private long _startTimestamp;
    private MapAnimationKind _animationKind;

    internal bool IsActive { get; private set; }

    internal MapCenter Target { get; private set; }

    /// <summary>
    /// Resets the animation to a stationary geographic center and clears active timing state.
    /// </summary>
    internal void Reset(double longitude, double latitude)
    {
        _currentWorldX = MapCamera.LongitudeToWorldX(longitude);
        _currentWorldY = MapCamera.LatitudeToWorldY(latitude);
        _startWorldX = _currentWorldX;
        _startWorldY = _currentWorldY;
        _targetWorldX = _currentWorldX;
        _targetWorldY = _currentWorldY;
        _startTimestamp = 0;
        Target = new MapCenter(longitude, latitude);
        IsActive = false;
    }

    /// <summary>
    /// Starts or retargets a pan from the current interpolated position, choosing the shortest
    /// horizontal route across the antimeridian.
    /// </summary>
    internal void SetTarget(
        double currentLongitude,
        double currentLatitude,
        double targetLongitude,
        double targetLatitude,
        long timestamp,
        MapAnimationKind animationKind = MapAnimationKind.Default)
    {
        if (IsActive)
        {
            UpdateCurrent(timestamp);
        }
        else
        {
            _currentWorldX = MapCamera.LongitudeToWorldX(currentLongitude);
            _currentWorldY = MapCamera.LatitudeToWorldY(currentLatitude);
        }

        _startWorldX = _currentWorldX;
        _startWorldY = _currentWorldY;
        _startTimestamp = timestamp;
        _animationKind = animationKind;
        double targetWorldX = MapCamera.LongitudeToWorldX(targetLongitude);
        double horizontalDelta = targetWorldX - _currentWorldX;
        if (horizontalDelta > 0.5)
        {
            horizontalDelta -= 1;
        }
        else if (horizontalDelta < -0.5)
        {
            horizontalDelta += 1;
        }

        _targetWorldX = _currentWorldX + horizontalDelta;
        _targetWorldY = MapCamera.LatitudeToWorldY(targetLatitude);
        Target = new MapCenter(targetLongitude, targetLatitude);
        IsActive =
            Math.Abs(horizontalDelta) > double.Epsilon ||
            Math.Abs(_targetWorldY - _currentWorldY) > double.Epsilon;
    }

    /// <summary>
    /// Evaluates the cubic ease-out animation at a timestamp and returns the current center.
    /// </summary>
    internal MapCenter GetCenter(long timestamp)
    {
        if (!IsActive)
        {
            return Target;
        }

        UpdateCurrent(timestamp);

        return new MapCenter(
            MapCamera.WorldXToLongitude(_currentWorldX),
            MapCamera.WorldYToLatitude(_currentWorldY));
    }

    /// <summary>
    /// Advances world-coordinate interpolation and marks the animation complete at its
    /// target.
    /// </summary>
    private void UpdateCurrent(long timestamp)
    {
        double elapsedMilliseconds =
            Stopwatch.GetElapsedTime(_startTimestamp, timestamp).TotalMilliseconds;
        double progress = Math.Clamp(elapsedMilliseconds / DurationMilliseconds, 0, 1);
        double easedProgress = CameraAnimation.Ease(progress, _animationKind);
        _currentWorldX = _startWorldX + ((_targetWorldX - _startWorldX) * easedProgress);
        _currentWorldY = _startWorldY + ((_targetWorldY - _startWorldY) * easedProgress);
        if (progress >= 1)
        {
            _currentWorldX = _targetWorldX;
            _currentWorldY = _targetWorldY;
            IsActive = false;
        }
    }
}
