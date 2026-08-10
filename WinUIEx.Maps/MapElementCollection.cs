using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace WinUIEx.Maps;

/// <summary>
/// Provides an observable, identity-unique collection of lightweight map elements.
/// </summary>
/// <remarks>
/// Null entries and repeated references to the same <see cref="MapElement"/> are rejected.
/// Once the collection is attached to a <see cref="MapElementsLayer"/> in a
/// <see cref="MapControl"/>, mutate it only on that control's UI thread. Use
/// <see cref="AddRange"/> and <see cref="RemoveRange"/> for bulk changes so observers receive
/// one collection notification instead of one notification per element.
/// </remarks>
public sealed class MapElementCollection : ObservableCollection<MapElement>
{
    private readonly HashSet<MapElement> _itemsByIdentity =
        new(ReferenceEqualityComparer.Instance);

    internal event EventHandler? Changing;

    /// <summary>
    /// Appends a sequence of elements and publishes one collection-change notification.
    /// </summary>
    /// <param name="elements">The non-null, identity-unique elements to append.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="elements"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The sequence contains a null element, repeats an element reference, or contains an
    /// element reference already in the collection.
    /// </exception>
    /// <remarks>
    /// The sequence is materialized and validated before the collection is changed. An empty
    /// sequence produces no notification. Mutate an attached collection only on its map
    /// control's UI thread.
    /// </remarks>
    public void AddRange(IEnumerable<MapElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        List<MapElement> added = [.. elements];
        ValidateRange(added, nameof(elements));
        if (added.Count == 0)
        {
            return;
        }

        OnChanging();
        CheckReentrancy();
        int startIndex = Count;
        foreach (MapElement element in added)
        {
            Items.Add(element);
            _itemsByIdentity.Add(element);
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
    /// <param name="index">The zero-based index of the first element to remove.</param>
    /// <param name="count">The number of elements to remove.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="index"/> or <paramref name="count"/> is negative.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The requested range extends beyond the collection.
    /// </exception>
    /// <remarks>
    /// A zero count produces no notification. Mutate an attached collection only on its map
    /// control's UI thread.
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
        List<MapElement> removed = new(count);
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
    /// Inserts a non-null element whose object identity is not already in the collection.
    /// </summary>
    /// <param name="index">The zero-based insertion index.</param>
    /// <param name="item">The element to insert.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="item"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The same element instance is already in the collection.
    /// </exception>
    protected override void InsertItem(int index, MapElement item)
    {
        ValidateNewItem(item, -1);
        OnChanging();
        base.InsertItem(index, item);
        _itemsByIdentity.Add(item);
    }

    /// <summary>
    /// Replaces an element while preserving the collection's identity-uniqueness rule.
    /// </summary>
    /// <param name="index">The zero-based index of the element to replace.</param>
    /// <param name="item">The replacement element.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="item"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The replacement instance already occurs at another index.
    /// </exception>
    protected override void SetItem(int index, MapElement item)
    {
        ValidateNewItem(item, index);
        MapElement previous = Items[index];
        OnChanging();
        base.SetItem(index, item);
        _itemsByIdentity.Remove(previous);
        _itemsByIdentity.Add(item);
    }

    /// <summary>
    /// Removes the element at the specified index and updates identity tracking.
    /// </summary>
    /// <param name="index">The zero-based index of the element to remove.</param>
    protected override void RemoveItem(int index)
    {
        MapElement removed = Items[index];
        OnChanging();
        base.RemoveItem(index);
        _itemsByIdentity.Remove(removed);
    }

    /// <summary>
    /// Removes all elements and clears identity tracking.
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
    /// Moves an element without changing its identity membership.
    /// </summary>
    /// <param name="oldIndex">The zero-based index of the element to move.</param>
    /// <param name="newIndex">The zero-based destination index.</param>
    protected override void MoveItem(int oldIndex, int newIndex)
    {
        OnChanging();
        base.MoveItem(oldIndex, newIndex);
    }

    private void OnChanging() => Changing?.Invoke(this, EventArgs.Empty);

    private void ValidateRange(IReadOnlyList<MapElement> elements, string parameterName)
    {
        HashSet<MapElement> unique = new(ReferenceEqualityComparer.Instance);
        foreach (MapElement? element in elements)
        {
            if (element is null)
            {
                throw new ArgumentException("Map elements cannot contain null.", parameterName);
            }
            if (!unique.Add(element) || _itemsByIdentity.Contains(element))
            {
                throw new ArgumentException(
                    "A MapElement instance can occur only once in a collection.",
                    parameterName);
            }
        }
    }

    private void ValidateNewItem(MapElement? item, int replacedIndex)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_itemsByIdentity.Contains(item) &&
            (replacedIndex < 0 || !ReferenceEquals(Items[replacedIndex], item)))
        {
            throw new ArgumentException(
                "A MapElement instance can occur only once in a collection.",
                nameof(item));
        }
    }

}
