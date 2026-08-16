namespace WinUIEx.Maps;

/// <summary>
/// Specifies the animation to use when you change the view of the map.
/// </summary>
/// <remarks>
/// When the Windows animation preference is disabled, the map applies every requested
/// view immediately, including requests that specify an animated value.
/// </remarks>
public enum MapAnimationKind
{
    /// <summary>
    /// The default animation.
    /// </summary>
    Default = 0,

    /// <summary>
    /// No animation.
    /// </summary>
    None = 1,

    /// <summary>
    /// A linear animation.
    /// </summary>
    Linear = 2,

    /// <summary>
    /// A parabolic animation.
    /// </summary>
    Bow = 3,
}
