using System.Buffers;

namespace WinUIEx.Maps.Rendering;

// Only the temporary builder is pooled. Published arrays remain immutable and may
// outlive the tile cache while a cancelled geometry worker finishes.
internal sealed class VectorStyleResultBuilder<T> : IDisposable
{
    private static readonly ArrayPool<T> Pool = ArrayPool<T>.Create(1024, 2);
    private T[] _items = [];
    private int _count;

    internal void Add(T item)
    {
        if (_count == _items.Length)
        {
            T[] replacement = Pool.Rent(Math.Max(16, checked(_count * 2)));
            _items.AsSpan(0, _count).CopyTo(replacement);
            if (_items.Length != 0)
                Pool.Return(_items, clearArray: true);
            _items = replacement;
        }
        _items[_count++] = item;
    }

    internal T[] ToArray() => _count == 0 ? [] : _items.AsSpan(0, _count).ToArray();

    public void Dispose()
    {
        if (_items.Length != 0)
            Pool.Return(_items, clearArray: true);
        _items = [];
        _count = 0;
    }
}
