namespace WinUIEx.Maps;

/// <summary>
/// Specifies which Azure base-map style a <see cref="MapControl"/> displays behind its
/// public layers.
/// </summary>
/// <remarks>
/// Every value except <see cref="Blank"/> requires a valid
/// <see cref="MapControl.MapServiceToken"/>. Changing the style does not modify or reorder
/// <see cref="MapControl.Layers"/>.
/// </remarks>
public enum MapStyle
{
    /// <summary>Displays the standard Azure road map with labels and transportation detail.</summary>
    Road,
    /// <summary>Displays the Azure road map using a dark grayscale presentation.</summary>
    GrayscaleDark,
    /// <summary>Displays Azure satellite imagery as the base map.</summary>
    Satellite,
    /// <summary>Displays Azure road data over terrain-oriented shaded relief.</summary>
    RoadShadedRelief,
    /// <summary>
    /// Displays no Azure base map, requires no Azure Maps token, and leaves only public
    /// layers visible.
    /// </summary>
    Blank,
}
