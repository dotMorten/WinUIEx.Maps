using System.Collections.ObjectModel;
using WindowsMapsSample.Models;
using WindowsMapsSample.Services;

namespace WindowsMapsSample.ViewModels;

internal sealed class Favorites
{
    private readonly IFavoritesStore _store;

    internal Favorites(IFavoritesStore store)
    {
        _store = store;
        foreach (Place place in store.Load())
        {
            if (!Contains(place)) Items.Add(place);
        }
    }

    internal ObservableCollection<Place> Items { get; } = [];

    internal bool Contains(Place place) =>
        Items.Any(item => IsSamePlace(item, place));

    internal void Toggle(Place place)
    {
        var existing = Items.FirstOrDefault(item => IsSamePlace(item, place));
        if (existing is not null)
            Items.Remove(existing);
        else
            Items.Add(place);
        _store.Save(Items);
    }

    internal bool Rename(Place place, string name)
    {
        string normalized = name.Trim();
        int index = Items.IndexOf(place);
        if (normalized.Length == 0 || index < 0) return false;
        if (Items[index].Name == normalized) return true;
        Items[index] = Items[index] with { Name = normalized };
        _store.Save(Items);
        return true;
    }

    private static bool IsSamePlace(Place left, Place right) =>
        left.Latitude == right.Latitude && left.Longitude == right.Longitude;
}
