namespace WindowsMapsSample.Models;

internal static class RouteDistanceFormatter
{
    private const double MetersPerMile = 1609.344;
    private const double FeetPerMeter = 3.280839895;

    internal static string FormatForUser(double? meters) =>
        Format(
            meters,
            new System.Globalization.RegionInfo(
                Windows.System.UserProfile.GlobalizationPreferences.HomeGeographicRegion).IsMetric);

    internal static string Format(double? meters, bool isMetric)
    {
        if (meters is not double value) return "";
        if (isMetric)
            return value < 1000 ? $"{value:N0} m" : $"{value / 1000:N1} km";

        double feet = value * FeetPerMeter;
        return feet < 528
            ? $"{feet:N0} ft"
            : $"{value / MetersPerMile:N1} mi";
    }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial record Place(string Name, string Address, double Latitude, double Longitude, PlaceBounds? Bounds = null)
{
    public override string ToString() => Name;
}

public sealed record PlaceBounds(double West, double South, double East, double North);

public sealed record SearchArea(double Latitude, double Longitude, int RadiusInMeters)
{
    public double DistanceTo(double latitude, double longitude)
    {
        double latitudeDelta = (latitude - Latitude) * Math.PI / 180;
        double longitudeDelta = (longitude - Longitude) * Math.PI / 180;
        double haversine = Math.Pow(Math.Sin(latitudeDelta / 2), 2) +
            Math.Cos(Latitude * Math.PI / 180) * Math.Cos(latitude * Math.PI / 180) *
            Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        return 6371000 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(haversine, 0, 1)));
    }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial record EndpointSuggestion(string Label, Place? Place)
{
    public string Glyph => Place is null ? "\uE81D" : "\uE707";
    public override string ToString() => Label;
}

public enum RouteManeuverIcon
{
    Straight,
    TurnLeft,
    TurnRight,
    BearLeft,
    BearRight,
    ForkLeft,
    ForkRight,
    SharpLeft,
    SharpRight,
    UTurn,
    UTurnRight,
    MergeLeft,
    MergeRight,
    ExitLeft,
    ExitRight,
    RoundaboutLeft,
    RoundaboutRight,
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial record RouteStep(string Instruction, double Latitude, double Longitude, double? Meters = null, string? Maneuver = null)
{
    public string Distance => RouteDistanceFormatter.FormatForUser(Meters);

    public RouteManeuverIcon ManeuverIcon
    {
        get
        {
            string maneuver = Maneuver?.ToUpperInvariant() ?? string.Empty;
            bool left = maneuver.Contains("LEFT", StringComparison.Ordinal);
            bool right = maneuver.Contains("RIGHT", StringComparison.Ordinal);
            if (maneuver.Contains("ROUNDABOUT", StringComparison.Ordinal))
                return right ? RouteManeuverIcon.RoundaboutRight : RouteManeuverIcon.RoundaboutLeft;
            if (maneuver.Contains("UTURN", StringComparison.Ordinal))
                return right ? RouteManeuverIcon.UTurnRight : RouteManeuverIcon.UTurn;
            if (maneuver.Contains("MERGE", StringComparison.Ordinal))
                return right ? RouteManeuverIcon.MergeRight : RouteManeuverIcon.MergeLeft;
            if (maneuver.Contains("EXIT", StringComparison.Ordinal))
                return left ? RouteManeuverIcon.ExitLeft : RouteManeuverIcon.ExitRight;
            if (maneuver.Contains("KEEP", StringComparison.Ordinal))
                return left ? RouteManeuverIcon.ForkLeft : RouteManeuverIcon.ForkRight;
            if (maneuver.Contains("SHARP", StringComparison.Ordinal))
                return left ? RouteManeuverIcon.SharpLeft : RouteManeuverIcon.SharpRight;
            if (maneuver.Contains("BEAR", StringComparison.Ordinal) || maneuver.Contains("SLIGHT", StringComparison.Ordinal))
                return left ? RouteManeuverIcon.BearLeft : RouteManeuverIcon.BearRight;
            if (left) return RouteManeuverIcon.TurnLeft;
            if (right) return RouteManeuverIcon.TurnRight;
            return RouteManeuverIcon.Straight;
        }
    }
}

public sealed record MapRoute(
    IReadOnlyList<Place> Points, IReadOnlyList<RouteStep> Steps, double? Meters, double? Seconds)
{
    public string Summary =>
        $"{(Seconds is double seconds ? $"{Math.Ceiling(seconds / 60):N0} min" : "Time unavailable")}  \u2022  " +
        (Meters is double meters ? RouteDistanceFormatter.FormatForUser(meters) : "Distance unavailable");
}
