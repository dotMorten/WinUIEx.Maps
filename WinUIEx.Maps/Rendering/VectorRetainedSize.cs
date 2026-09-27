using System.Runtime.CompilerServices;

namespace WinUIEx.Maps.Rendering;

// Ownership estimate, not GC heap or process commitment. Headers use conservative
// 64-bit sizes; payload strides follow the running architecture.
internal sealed class VectorRetainedSize(bool uniqueGeometry = false)
{
    private readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);
    internal long Bytes { get; private set; }

    internal bool AddObject(object value, long bytes)
    {
        if (!uniqueGeometry && !_seen.Add(value))
            return false;
        Bytes += bytes;
        return true;
    }

    internal void AddArray<T>(T[] array)
    {
        if (array.Length != 0)
            AddObject(array, 24 + (long)array.Length * Unsafe.SizeOf<T>());
    }

    internal void AddString(string? value)
    {
        if (!string.IsNullOrEmpty(value) && _seen.Add(value))
            Bytes += (24 + value.Length * 2L + 7) & ~7L;
    }

    internal void Exclude(object? value)
    {
        if (value is not null)
            _seen.Add(value);
    }
}
