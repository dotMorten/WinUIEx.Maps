using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace WinUIEx.Maps.Rendering;

internal sealed class MapIconUploadQueue
{
    private const int MaximumMapElementBurst = 8;
    private readonly ConcurrentQueue<MapIconPixelData> _mapElements = new();
    private readonly ConcurrentQueue<MapIconPixelData> _vectorTextures = new();
    private int _mapElementBurst;

    internal bool IsEmpty => _mapElements.IsEmpty && _vectorTextures.IsEmpty;
    internal int MapElementCount => _mapElements.Count;
    internal int VectorTextureCount => _vectorTextures.Count;

    internal void Enqueue(MapIconPixelData data) =>
        (data.IsMapElement ? _mapElements : _vectorTextures).Enqueue(data);

    // A single upload worker consumes both lanes; reserve progress for labels under sustained icon updates.
    internal bool TryDequeue([NotNullWhen(true)] out MapIconPixelData? data)
    {
        if (_mapElementBurst < MaximumMapElementBurst && _mapElements.TryDequeue(out data))
        {
            _mapElementBurst++;
            return true;
        }
        if (_vectorTextures.TryDequeue(out data))
        {
            _mapElementBurst = 0;
            return true;
        }
        if (_mapElements.TryDequeue(out data))
        {
            _mapElementBurst = 1;
            return true;
        }
        _mapElementBurst = 0;
        return false;
    }
}
