namespace WinUIEx.Maps;

/// <summary>
/// Defines an immutable geographic bounding box for a <see cref="TileLayer"/>.
/// </summary>
/// <remarks>
/// Longitudes use degrees from -180 through 180 and latitudes use the Web Mercator range
/// from -85.05112878 through 85.05112878. West must be less than east and south must be less
/// than north. Bounds do not wrap across the antimeridian.
/// </remarks>
public readonly struct TileLayerBounds
{
    /// <summary>
    /// Initializes an ordered, non-wrapping geographic bounding box.
    /// </summary>
    /// <param name="west">The western longitude, from -180 through 180.</param>
    /// <param name="south">
    /// The southern latitude, from -85.05112878 through 85.05112878.
    /// </param>
    /// <param name="east">The eastern longitude, from -180 through 180.</param>
    /// <param name="north">
    /// The northern latitude, from -85.05112878 through 85.05112878.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A coordinate is non-finite, outside Web Mercator, or the edges are not ordered.
    /// </exception>
    public TileLayerBounds(double west, double south, double east, double north)
    {
        if (!IsValid(west, south, east, north))
        {
            throw new ArgumentOutOfRangeException(
                nameof(west),
                "Bounds must be finite, ordered, and inside the Web Mercator world.");
        }

        West = west;
        South = south;
        East = east;
        North = north;
    }

    /// <summary>Gets the western longitude in degrees.</summary>
    /// <value>A value from -180 through 180 that is less than <see cref="East"/>.</value>
    public double West { get; }

    /// <summary>Gets the southern latitude in degrees.</summary>
    /// <value>
    /// A value in the Web Mercator latitude range that is less than <see cref="North"/>.
    /// </value>
    public double South { get; }

    /// <summary>Gets the eastern longitude in degrees.</summary>
    /// <value>A value from -180 through 180 that is greater than <see cref="West"/>.</value>
    public double East { get; }

    /// <summary>Gets the northern latitude in degrees.</summary>
    /// <value>
    /// A value in the Web Mercator latitude range that is greater than <see cref="South"/>.
    /// </value>
    public double North { get; }

    /// <summary>Gets bounds covering the complete Web Mercator world.</summary>
    /// <value>
    /// Longitudes -180 through 180 and latitudes -85.05112878 through 85.05112878.
    /// </value>
    public static TileLayerBounds World { get; } =
        new(-180, -85.05112878, 180, 85.05112878);

    internal static bool IsValid(TileLayerBounds value) =>
        IsValid(value.West, value.South, value.East, value.North);

    private static bool IsValid(double west, double south, double east, double north) =>
        double.IsFinite(west) &&
        double.IsFinite(south) &&
        double.IsFinite(east) &&
        double.IsFinite(north) &&
        west is >= -180 and <= 180 &&
        east is >= -180 and <= 180 &&
        south is >= -85.05112878 and <= 85.05112878 &&
        north is >= -85.05112878 and <= 85.05112878 &&
        west < east &&
        south < north;
}
