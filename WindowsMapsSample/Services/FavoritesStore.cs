using Windows.Storage;
using WindowsMapsSample.Models;

namespace WindowsMapsSample.Services;

internal interface IFavoritesStore
{
    IReadOnlyList<Place> Load();
    void Save(IReadOnlyList<Place> favorites);
}

internal sealed class FavoritesStore : IFavoritesStore
{
    private const string CountKey = "WindowsMapsSample.Favorites.Count";
    private const string ItemKeyPrefix = "WindowsMapsSample.Favorites.Item.";
    private const string NameKey = "Name";
    private const string AddressKey = "Address";
    private const string LatitudeKey = "Latitude";
    private const string LongitudeKey = "Longitude";

    public IReadOnlyList<Place> Load()
    {
        var settings = ApplicationData.Current.LocalSettings.Values;
        if (!settings.TryGetValue(CountKey, out object? value) || value is not int count || count <= 0)
            return [];

        var favorites = new List<Place>(count);
        for (int index = 0; index < count; index++)
        {
            if (!settings.TryGetValue(ItemKeyPrefix + index, out object? item) ||
                item is not ApplicationDataCompositeValue values ||
                !TryReadPlace(values, out Place? place) ||
                place is null)
            {
                continue;
            }

            favorites.Add(place);
        }

        return favorites;
    }

    public void Save(IReadOnlyList<Place> favorites)
    {
        var settings = ApplicationData.Current.LocalSettings.Values;
        foreach (string key in settings.Keys
            .Where(static key => key.StartsWith(ItemKeyPrefix, StringComparison.Ordinal))
            .ToArray())
        {
            settings.Remove(key);
        }

        for (int index = 0; index < favorites.Count; index++)
        {
            Place place = favorites[index];
            var values = new ApplicationDataCompositeValue
            {
                [NameKey] = place.Name,
                [AddressKey] = place.Address,
                [LatitudeKey] = place.Latitude,
                [LongitudeKey] = place.Longitude,
            };
            settings[ItemKeyPrefix + index] = values;
        }

        settings[CountKey] = favorites.Count;
    }

    private static bool TryReadPlace(ApplicationDataCompositeValue values, out Place? place)
    {
        if (values.TryGetValue(NameKey, out object? nameValue) &&
            nameValue is string name &&
            name.Length > 0 &&
            values.TryGetValue(AddressKey, out object? addressValue) &&
            addressValue is string address &&
            values.TryGetValue(LatitudeKey, out object? latitudeValue) &&
            latitudeValue is double latitude &&
            values.TryGetValue(LongitudeKey, out object? longitudeValue) &&
            longitudeValue is double longitude &&
            double.IsFinite(latitude) &&
            double.IsFinite(longitude) &&
            latitude is >= -90 and <= 90 &&
            longitude is >= -180 and <= 180)
        {
            place = new Place(name, address, latitude, longitude);
            return true;
        }

        place = null;
        return false;
    }
}
