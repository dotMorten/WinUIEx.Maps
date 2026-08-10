namespace WinUIEx.Maps;

internal readonly record struct MapIconRasterDimensions(
    uint LogicalWidth,
    uint LogicalHeight,
    uint PixelWidth,
    uint PixelHeight)
{
    public static MapIconRasterDimensions Create(
        double logicalWidth,
        double logicalHeight,
        int pixelWidth,
        int pixelHeight)
    {
        return new MapIconRasterDimensions(
            checked((uint)Math.Ceiling(logicalWidth)),
            checked((uint)Math.Ceiling(logicalHeight)),
            checked((uint)pixelWidth),
            checked((uint)pixelHeight));
    }
}

internal sealed class MapIconTextureReferences
{
    private readonly Dictionary<object, Entry> _entries =
        new(ReferenceEqualityComparer.Instance);
    private long _nextId;

    public IReadOnlyCollection<Entry> Entries => _entries.Values;

    public Entry Add(object key)
    {
        if (_entries.TryGetValue(key, out Entry? entry))
        {
            entry.ReferenceCount++;
            return entry;
        }

        entry = new Entry(++_nextId);
        _entries.Add(key, entry);
        return entry;
    }

    public Entry? Remove(object key)
    {
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            return null;
        }

        entry.ReferenceCount--;
        if (entry.ReferenceCount > 0)
        {
            return null;
        }

        _entries.Remove(key);
        entry.Version++;
        return entry;
    }

    public bool TryGet(object key, out Entry? entry) => _entries.TryGetValue(key, out entry);

    internal sealed class Entry(long textureId)
    {
        public long TextureId { get; } = textureId;
        public int ReferenceCount { get; set; } = 1;
        public long Version { get; set; } = 1;
        public long QueuedVersion { get; set; }
        public uint Width { get; set; } = 32;
        public uint Height { get; set; } = 32;
    }
}
