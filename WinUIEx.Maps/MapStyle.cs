namespace WinUIEx.Maps;

/// <summary>
/// Specifies which Azure base-map style a <see cref="MapControl"/> displays behind its
/// public layers.
/// </summary>
/// <remarks>
/// Every value except <see cref="Blank"/> requires a valid
/// <see cref="MapControl.MapServiceToken"/>. Changing the style does not modify or reorder
/// <see cref="MapControl.Layers"/>. Vector-backed styles render sprite point symbols from
/// Azure's Style Spec and vector-tile properties. Point labels use Azure's SDF glyph
/// ranges and screen-space collision placement. Vector fill, line, icon, and point-label
/// layers render in style order; line-placement symbols follow projected paths and
/// participate in whole-label collision placement.
/// SatelliteWithRoads composes raster imagery beneath its vector overlays.
/// </remarks>
public enum MapStyle
{
    /// <summary>
    /// Selects the legacy raster road tileset.
    /// </summary>
    /// <remarks>
    /// Use <see cref="Road"/> for Azure's current vector road style.
    /// </remarks>
    RoadRaster = 0,
    /// <summary>Selects the legacy dark grayscale raster tileset.</summary>
    /// <remarks>
    /// Use <see cref="GrayscaleDark"/> for Azure's current vector dark grayscale style.
    /// </remarks>
    GrayscaleDarkRaster = 1,
    /// <summary>Displays a combination of satellite and aerial imagery.</summary>
    /// <remarks>
    /// This raster style contains no visible labels or road lines.
    /// </remarks>
    Satellite = 2,
    /// <summary>
    /// Selects the legacy raster road tileset composited with terrain shaded relief.
    /// </summary>
    /// <remarks>
    /// Use <see cref="RoadShadedRelief"/> for Azure's current vector shaded-relief style.
    /// </remarks>
    RoadShadedReliefRaster = 3,
    /// <summary>
    /// Displays no Azure base map, requires no Azure Maps token, and leaves only public
    /// layers visible.
    /// </summary>
    /// <remarks>
    /// This style loads no Azure vector data and therefore supplies no Azure Maps
    /// screen-reader descriptions.
    /// </remarks>
    Blank = 4,
    /// <summary>
    /// Displays a blank canvas while retaining transparent Azure vector data used for
    /// accessibility.
    /// </summary>
    /// <remarks>
    /// This control loads the style's vector data while suppressing visible symbols. It
    /// does not yet expose vector feature descriptions to accessibility clients.
    /// </remarks>
    BlankAccessible = 5,
    /// <summary>Selects the light grayscale Azure road style.</summary>
    /// <remarks>
    /// Designed primarily for business-intelligence scenarios. Azure Maps rates its color
    /// contrast as partial and supports screen-reader descriptions.
    /// </remarks>
    GrayscaleLight = 6,
    /// <summary>
    /// Selects the dark Azure road style for low-light conditions.
    /// </summary>
    /// <remarks>
    /// Azure Maps rates this style as partial for color contrast and supports screen-reader
    /// descriptions.
    /// </remarks>
    Night = 7,
    /// <summary>Selects the high-contrast dark Azure road style.</summary>
    /// <remarks>
    /// Azure Maps defines this as a fully accessible color-contrast style for dark
    /// high-contrast mode and supports screen-reader descriptions.
    /// </remarks>
    HighContrastDark = 8,
    /// <summary>Selects the high-contrast light Azure road style.</summary>
    /// <remarks>
    /// Azure Maps defines this as a fully accessible color-contrast style for light
    /// high-contrast mode and supports screen-reader descriptions.
    /// </remarks>
    HighContrastLight = 9,
    /// <summary>
    /// Selects Azure's <c>satellite_road_labels</c> style.
    /// </summary>
    /// <remarks>
    /// Azure Maps supports screen-reader descriptions but does not rate the unlimited
    /// imagery/overlay color combinations as accessible for color contrast.
    /// </remarks>
    SatelliteWithRoads = 10,
    /// <summary>Selects Azure's main colorful vector road style.</summary>
    /// <remarks>
    /// Azure Maps rates this style as partial for color contrast and supports screen-reader
    /// descriptions from its vector data.
    /// </remarks>
    Road = 11,
    /// <summary>Selects Azure's dark grayscale vector road style.</summary>
    /// <remarks>
    /// Designed primarily for business-intelligence scenarios and for colorful overlays
    /// such as weather radar. Azure Maps rates its color contrast as partial and supports
    /// screen-reader descriptions.
    /// </remarks>
    GrayscaleDark = 12,
    /// <summary>
    /// Selects Azure's vector road style with Earth contours and shaded-relief definitions.
    /// </summary>
    /// <remarks>
    /// Azure Maps rates this style as partial for color contrast and supports screen-reader
    /// descriptions.
    /// </remarks>
    RoadShadedRelief = 13,
}
