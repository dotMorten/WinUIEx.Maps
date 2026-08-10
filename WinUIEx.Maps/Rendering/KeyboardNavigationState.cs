namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Immutable held-key state consumed by the renderer for continuous map navigation.
/// </summary>
internal readonly record struct KeyboardNavigationState(
    int HorizontalDirection,
    int VerticalDirection,
    int ZoomDirection,
    long StartTimestamp)
{
    internal static readonly TimeSpan HoldThreshold = TimeSpan.FromMilliseconds(250);
    internal const double PanViewportFractionsPerSecond = 0.5;
    internal const double ZoomLevelsPerSecond = 1;

    internal bool HasInput =>
        HorizontalDirection != 0 || VerticalDirection != 0 || ZoomDirection != 0;
}
