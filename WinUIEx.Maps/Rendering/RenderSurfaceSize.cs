namespace WinUIEx.Maps.Rendering;

/// <summary>Immutable UI-thread snapshot of the logical-to-physical surface boundary.</summary>
internal readonly record struct RenderSurfaceSize(
    double LogicalWidth,
    double LogicalHeight,
    float ScaleX,
    float ScaleY)
{
    internal uint PixelWidth => ToPixels(LogicalWidth, ScaleX);
    internal uint PixelHeight => ToPixels(LogicalHeight, ScaleY);

    private static uint ToPixels(double extent, float scale) =>
        checked((uint)Math.Max(1, Math.Ceiling(extent * scale)));
}
