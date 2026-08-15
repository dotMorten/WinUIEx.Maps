namespace WinUIEx.Maps.Rendering;

internal static class CameraAnimation
{
    internal static double Ease(double progress, MapAnimationKind animation) =>
        animation switch
        {
            MapAnimationKind.None => 1,
            MapAnimationKind.Linear => progress,
            MapAnimationKind.Bow => 1 - Math.Pow(1 - progress, 2),
            _ => 1 - Math.Pow(1 - progress, 3),
        };
}
