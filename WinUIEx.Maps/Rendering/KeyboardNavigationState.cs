namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Immutable held-key state consumed by the renderer for continuous map navigation.
/// </summary>
internal readonly record struct KeyboardNavigationState(
    int HorizontalDirection,
    int VerticalDirection,
    int ZoomDirection,
    int HeadingDirection,
    int PitchDirection,
    long StartTimestamp)
{
    internal static readonly TimeSpan HoldThreshold = TimeSpan.FromMilliseconds(250);
    internal const double PanViewportFractionsPerSecond = 0.5;
    internal const double ZoomLevelsPerSecond = 1;
    internal const double HeadingDegreesPerSecond = 30;
    internal const double PitchDegreesPerSecond = 20;

    internal bool HasInput =>
        HorizontalDirection != 0 ||
        VerticalDirection != 0 ||
        ZoomDirection != 0 ||
        HeadingDirection != 0 ||
        PitchDirection != 0;
}
