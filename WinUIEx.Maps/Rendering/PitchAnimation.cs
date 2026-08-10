using System.Diagnostics;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Maintains render-thread cubic ease-out interpolation between camera pitch values.
/// </summary>
internal sealed class PitchAnimation
{
    private const double DurationMilliseconds = 300;
    private double _startPitch;
    private long _startTimestamp;

    internal double TargetPitch { get; private set; }

    internal bool IsActive { get; private set; }

    internal void Reset(double pitch)
    {
        _startPitch = MapCamera.NormalizePitch(pitch);
        TargetPitch = _startPitch;
        _startTimestamp = 0;
        IsActive = false;
    }

    internal void SetTarget(double currentPitch, double targetPitch, long timestamp)
    {
        _startPitch = MapCamera.NormalizePitch(currentPitch);
        TargetPitch = MapCamera.NormalizePitch(targetPitch);
        _startTimestamp = timestamp;
        IsActive = TargetPitch != _startPitch;
    }

    internal double GetPitch(long timestamp)
    {
        if (!IsActive)
        {
            return TargetPitch;
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
            return TargetPitch;
        }

        return _startPitch + ((TargetPitch - _startPitch) * easedProgress);
    }
}
