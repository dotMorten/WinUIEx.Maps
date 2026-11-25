namespace WinUIEx.Maps.Maui;

internal static class MapMath
{
    internal static double WrapLongitude(double value) => ((value + 180) % 360 + 360) % 360 - 180;

    internal static double WorldY(double latitude)
    {
        double sin = Math.Sin(Math.Clamp(latitude, -85.05112878, 85.05112878) * Math.PI / 180);
        return 0.5 - Math.Log((1 + sin) / (1 - sin)) / (4 * Math.PI);
    }

    internal static (double Latitude, double Longitude) CirclePoint(
        double latitude, double longitude, double radiusMeters, double bearing)
    {
        double angular = radiusMeters / 6371008.8;
        double lat = latitude * Math.PI / 180;
        double lon = longitude * Math.PI / 180;
        double angle = bearing * Math.PI / 180;
        double resultLat = Math.Asin(Math.Sin(lat) * Math.Cos(angular) +
            Math.Cos(lat) * Math.Sin(angular) * Math.Cos(angle));
        double resultLon = lon + Math.Atan2(Math.Sin(angle) * Math.Sin(angular) * Math.Cos(lat),
            Math.Cos(angular) - Math.Sin(lat) * Math.Sin(resultLat));
        return (resultLat * 180 / Math.PI, WrapLongitude(resultLon * 180 / Math.PI));
    }

    internal static List<List<T>> Cluster<T>(
        IEnumerable<T> items, Func<T, (double Latitude, double Longitude, string Key)> getPosition, double zoom)
    {
        // World-space cells keep membership stable during panning and wrap across the date line.
        int cells = Math.Max(1, (int)Math.Ceiling(256 * Math.Pow(2, Math.Min(zoom, 22)) / 64));
        var buckets = new Dictionary<(string Key, int X, int Y), List<T>>();
        var groups = new List<List<T>>();
        foreach (T item in items)
        {
            var (latitude, longitude, key) = getPosition(item);
            if (string.IsNullOrEmpty(key))
            {
                groups.Add([item]);
                continue;
            }
            int x = (int)Math.Floor((WrapLongitude(longitude) + 180) / 360 * cells + 0.5) % cells;
            int y = (int)Math.Floor(WorldY(latitude) * cells);
            var bucket = (key, x, y);
            if (!buckets.TryGetValue(bucket, out var group))
            {
                group = [];
                buckets.Add(bucket, group);
                groups.Add(group);
            }
            group.Add(item);
        }
        return groups;
    }

    internal static (double Latitude, double Longitude) Centroid(
        IEnumerable<(double Latitude, double Longitude)> positions)
    {
        var points = positions.ToList();
        double origin = points[0].Longitude;
        return (points.Average(p => p.Latitude),
            WrapLongitude(origin + points.Average(p => WrapLongitude(p.Longitude - origin))));
    }

    internal static List<TMarker> ReconcileClusters<T, TMarker>(
        IEnumerable<(IReadOnlyList<T> Pins, TMarker Marker)> existing,
        IEnumerable<IReadOnlyList<T>> groups,
        Func<IReadOnlyList<T>, TMarker> createMarker) where T : class
    {
        var candidates = new Dictionary<T, (IReadOnlyList<T> Pins, TMarker Marker)>(
            ReferenceEqualityComparer.Instance);
        foreach (var entry in existing)
            candidates.TryAdd(entry.Pins[0], entry);

        var result = new List<TMarker>();
        foreach (var group in groups)
        {
            if (candidates.TryGetValue(group[0], out var candidate) &&
                candidate.Pins.Count == group.Count &&
                candidate.Pins.SequenceEqual(group, ReferenceEqualityComparer.Instance))
            {
                result.Add(candidate.Marker);
                candidates.Remove(group[0]);
            }
            else
            {
                result.Add(createMarker(group));
            }
        }
        return result;
    }
}
