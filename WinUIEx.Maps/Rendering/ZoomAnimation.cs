using System.Diagnostics;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Maintains render-thread cubic ease-out zoom interpolation with duration proportional to
/// the number of zoom levels crossed.
/// </summary>
/// <remarks>
/// The render thread owns instances. Inputs are normalized to supported camera zoom, and
/// duration is bounded so retargeted UI publications remain responsive while large
/// transitions do not complete abruptly.
/// </remarks>
internal sealed class ZoomAnimation
{
    private const double MillisecondsPerLevel = 500;
    private const double MinimumDurationMilliseconds = 220;
    private const double MaximumDurationMilliseconds = 1000;

    private double _startZoom;
    private long _startTimestamp;
    private double _durationMilliseconds;

    internal double TargetZoom { get; private set; }

    internal bool IsActive { get; private set; }

    /// <summary>
    /// Resets the animation to a normalized, stationary zoom level.
    /// </summary>
    internal void Reset(double zoom)
    {
        _startZoom = Normalize(zoom);
        TargetZoom = _startZoom;
        IsActive = false;
    }

    /// <summary>
    /// Starts a cubic ease-out transition whose bounded duration scales with zoom distance.
    /// </summary>
    internal void SetTarget(double currentZoom, double targetZoom, long timestamp)
    {
        double normalizedCurrent = Normalize(currentZoom);
        TargetZoom = Normalize(targetZoom);
        _startZoom = normalizedCurrent;
        _startTimestamp = timestamp;
        _durationMilliseconds = Math.Clamp(
            Math.Abs(TargetZoom - _startZoom) * MillisecondsPerLevel,
            MinimumDurationMilliseconds,
            MaximumDurationMilliseconds);
        IsActive = TargetZoom != _startZoom;
    }

    /// <summary>
    /// Evaluates the zoom transition at a timestamp and deactivates it upon reaching the
    /// target.
    /// </summary>
    internal double GetZoom(long timestamp)
    {
        if (!IsActive)
        {
            return TargetZoom;
        }

        double elapsedMilliseconds = Stopwatch.GetElapsedTime(_startTimestamp, timestamp).TotalMilliseconds;
        double progress = Math.Clamp(elapsedMilliseconds / _durationMilliseconds, 0, 1);
        double easedProgress = 1 - Math.Pow(1 - progress, 3);
        double zoom = _startZoom + ((TargetZoom - _startZoom) * easedProgress);
        if (progress >= 1)
        {
            IsActive = false;
            return TargetZoom;
        }

        return zoom;
    }

    /// <summary>
    /// Converts non-finite input to zero and clamps finite zoom to the renderer's supported
    /// range.
    /// </summary>
    private static double Normalize(double zoom)
    {
        return double.IsFinite(zoom) ? Math.Clamp(zoom, 0, MapCamera.MaximumTileZoom) : 0;
    }
}
