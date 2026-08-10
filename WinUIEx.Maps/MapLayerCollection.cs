using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace WinUIEx.Maps;

/// <summary>
/// Provides an observable, identity-unique collection that defines map-layer draw order.
/// </summary>
/// <remarks>
/// The first layer is rendered bottom-most. Null entries and duplicate layer references
/// are rejected. Once attached, mutate the collection on the owning
/// <see cref="MapControl"/>'s UI thread. Use <see cref="AddRange"/> and
/// <see cref="RemoveRange"/> for bulk changes so the control republishes its ordered layer
/// state once.
/// </remarks>
public sealed class MapLayerCollection : ObservableCollection<MapLayer>
{
    private readonly HashSet<MapLayer> _itemsByIdentity =
        new(ReferenceEqualityComparer.Instance);

    internal event EventHandler? Changing;

    /// <summary>
    /// Appends a sequence of layers and publishes one collection-change notification.
    /// </summary>
    /// <param name="layers">The non-null, identity-unique layers to append.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="layers"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The sequence contains a null layer, repeats a layer reference, or contains a layer
    /// reference already in the collection.
    /// </exception>
    /// <remarks>
    /// The sequence is materialized and validated before the collection is changed. An empty
    /// sequence produces no notification. Mutate an attached collection only on the owning
    /// map control's UI thread.
    /// </remarks>
    public void AddRange(IEnumerable<MapLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        List<MapLayer> added = [.. layers];
        ValidateRange(added, nameof(layers));
        if (added.Count == 0)
        {
            return;
        }

        OnChanging();
        CheckReentrancy();
        int startIndex = Count;
        foreach (MapLayer layer in added)
        {
            Items.Add(layer);
            _itemsByIdentity.Add(layer);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add,
            added,
            startIndex));
    }

    /// <summary>
    /// Removes a contiguous range and publishes one collection-change notification.
    /// </summary>
    /// <param name="index">The zero-based index of the first layer to remove.</param>
    /// <param name="count">The number of layers to remove.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="index"/> or <paramref name="count"/> is negative.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The requested range extends beyond the collection.
    /// </exception>
    /// <remarks>
    /// A zero count produces no notification. Mutate an attached collection only on the
    /// owning map control's UI thread.
    /// </remarks>
    public void RemoveRange(int index, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (index > Count - count)
        {
            throw new ArgumentException("The requested range is outside the collection.");
        }
        if (count == 0)
        {
            return;
        }

        OnChanging();
        CheckReentrancy();
        List<MapLayer> removed = new(count);
        for (int offset = 0; offset < count; offset++)
        {
            removed.Add(Items[index + offset]);
        }
        for (int offset = count - 1; offset >= 0; offset--)
        {
            Items.RemoveAt(index + offset);
        }
        _itemsByIdentity.ExceptWith(removed);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Remove,
            removed,
            index));
    }

    /// <summary>
    /// Inserts a non-null layer whose object identity is not already in the collection.
    /// </summary>
    /// <param name="index">The zero-based insertion index.</param>
    /// <param name="item">The layer to insert.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="item"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The same layer instance is already in the collection.
    /// </exception>
    protected override void InsertItem(int index, MapLayer item)
    {
        ValidateNewItem(item, -1);
        OnChanging();
        base.InsertItem(index, item);
        _itemsByIdentity.Add(item);
    }

    /// <summary>
    /// Replaces a layer while preserving the collection's identity-uniqueness rule.
    /// </summary>
    /// <param name="index">The zero-based index of the layer to replace.</param>
    /// <param name="item">The replacement layer.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="item"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The replacement instance already occurs at another index.
    /// </exception>
    protected override void SetItem(int index, MapLayer item)
    {
        ValidateNewItem(item, index);
        MapLayer previous = Items[index];
        OnChanging();
        base.SetItem(index, item);
        _itemsByIdentity.Remove(previous);
        _itemsByIdentity.Add(item);
    }

    /// <summary>
    /// Removes the layer at the specified index and updates identity tracking.
    /// </summary>
    /// <param name="index">The zero-based index of the layer to remove.</param>
    protected override void RemoveItem(int index)
    {
        MapLayer removed = Items[index];
        OnChanging();
        base.RemoveItem(index);
        _itemsByIdentity.Remove(removed);
    }

    /// <summary>
    /// Removes all layers and clears identity tracking.
    /// </summary>
    /// <remarks>An already empty collection produces no notification.</remarks>
    protected override void ClearItems()
    {
        if (Count == 0)
        {
            return;
        }

        OnChanging();
        base.ClearItems();
        _itemsByIdentity.Clear();
    }

    /// <summary>
    /// Moves a layer to a new draw-order position without changing identity membership.
    /// </summary>
    /// <param name="oldIndex">The zero-based index of the layer to move.</param>
    /// <param name="newIndex">The zero-based destination index.</param>
    protected override void MoveItem(int oldIndex, int newIndex)
    {
        OnChanging();
        base.MoveItem(oldIndex, newIndex);
    }

    private void OnChanging() => Changing?.Invoke(this, EventArgs.Empty);

    private void ValidateRange(IReadOnlyList<MapLayer> layers, string parameterName)
    {
        HashSet<MapLayer> unique = new(ReferenceEqualityComparer.Instance);
        foreach (MapLayer? layer in layers)
        {
            if (layer is null)
            {
                throw new ArgumentException("Map layers cannot contain null.", parameterName);
            }
            if (!unique.Add(layer) || _itemsByIdentity.Contains(layer))
            {
                throw new ArgumentException(
                    "A MapLayer instance can occur only once in a collection.",
                    parameterName);
            }
        }
    }

    private void ValidateNewItem(MapLayer? item, int replacedIndex)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_itemsByIdentity.Contains(item) &&
            (replacedIndex < 0 || !ReferenceEquals(Items[replacedIndex], item)))
        {
            throw new ArgumentException(
                "A MapLayer instance can occur only once in a collection.",
                nameof(item));
        }
    }

}
