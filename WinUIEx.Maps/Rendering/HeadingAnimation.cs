using System.Diagnostics;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Maintains render-thread heading interpolation along the shortest path around north.
/// </summary>
internal sealed class HeadingAnimation
{
    private const double DurationMilliseconds = 300;
    private double _startHeading;
    private double _headingDelta;
    private long _startTimestamp;

    internal double TargetHeading { get; private set; }

    internal bool IsActive { get; private set; }

    internal void Reset(double heading)
    {
        _startHeading = MapCamera.NormalizeHeading(heading);
        TargetHeading = _startHeading;
        _headingDelta = 0;
        _startTimestamp = 0;
        IsActive = false;
    }

    internal void SetTarget(double currentHeading, double targetHeading, long timestamp)
    {
        _startHeading = MapCamera.NormalizeHeading(currentHeading);
        TargetHeading = MapCamera.NormalizeHeading(targetHeading);
        _headingDelta = MapCamera.ShortestHeadingDelta(
            _startHeading,
            TargetHeading);
        _startTimestamp = timestamp;
        IsActive = Math.Abs(_headingDelta) > double.Epsilon;
    }

    internal double GetHeading(long timestamp)
    {
        if (!IsActive)
        {
            return TargetHeading;
        }

        double elapsedMilliseconds =
            Stopwatch.GetElapsedTime(_startTimestamp, timestamp).TotalMilliseconds;
        double progress = Math.Clamp(
            elapsedMilliseconds / DurationMilliseconds,
            0,
            1);
        double easedProgress = 1 - Math.Pow(1 - progress, 3);
        if (progress >= 1)
        {
            IsActive = false;
            return TargetHeading;
        }

        return MapCamera.NormalizeHeading(
            _startHeading + (_headingDelta * easedProgress));
    }
}
