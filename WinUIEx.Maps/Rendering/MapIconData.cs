namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Captures one map icon's texture identity, geographic anchor, logical dimensions, and
/// layer position for render-thread use.
/// </summary>
/// <remarks>
/// Created from UI objects on the UI thread; it contains no XAML reference and is safe to
/// publish to renderer state.
/// </remarks>
internal readonly record struct MapIconSnapshot(
    long TextureId,
    double Longitude,
    double Latitude,
    uint Width,
    uint Height,
    int LayerIndex = 0,
    double NormalizedAnchorX = 0.5,
    double NormalizedAnchorY = 0.5,
    int ElementIndex = -1,
    int OrderIndex = -1,
    bool IsEnabled = true);

/// <summary>
/// Associates an incremental icon snapshot with its stable position in the published icon
/// array.
/// </summary>
internal readonly record struct MapIconSnapshotUpdate(
    int Index,
    MapIconSnapshot Snapshot);

/// <summary>
/// Indexes immutable icon snapshots in a fixed Web Mercator grid for viewport candidate
/// selection while preserving layer order.
/// </summary>
/// <remarks>
/// <para>
/// The renderer protects this mutable index with <c>_mapElementsSync</c>. UI publications
/// may rebuild or incrementally update it while the render thread requests candidates.
/// </para>
/// <para>
/// Cell queries expand by the largest known icon dimensions so icons anchored just outside
/// the geographic viewport are still considered. Exact projection and rectangle culling
/// occur later on the render thread.
/// </para>
/// </remarks>
internal sealed class MapIconSpatialIndex
{
    private const int GridSize = 64;
    private readonly Dictionary<int, List<int>> _cells = [];
    private MapIconSnapshot[] _icons = [];
    private double _maximumHorizontalAnchorExtent;
    private double _maximumVerticalAnchorExtent;

    /// <summary>
    /// Replaces all icon snapshots and rebuilds the spatial grid and visibility bounds.
    /// </summary>
    public void Rebuild(MapIconSnapshot[] icons)
    {
        _icons = icons;
        _cells.Clear();
        _maximumHorizontalAnchorExtent = 0;
        _maximumVerticalAnchorExtent = 0;
        for (int index = 0; index < icons.Length; index++)
        {
            Add(index, icons[index]);
        }
    }

    /// <summary>
    /// Applies indexed icon changes, moving entries between grid cells while conservatively
    /// expanding culling bounds.
    /// </summary>
    public void Update(IReadOnlyList<MapIconSnapshotUpdate> updates)
    {
        foreach (MapIconSnapshotUpdate update in updates)
        {
            if ((uint)update.Index >= (uint)_icons.Length)
            {
                continue;
            }

            MapIconSnapshot previous = _icons[update.Index];
            int previousCell = GetCell(previous.Longitude, previous.Latitude);
            int currentCell = GetCell(update.Snapshot.Longitude, update.Snapshot.Latitude);
            if (previousCell != currentCell &&
                _cells.TryGetValue(previousCell, out List<int>? previousIndexes))
            {
                previousIndexes.Remove(update.Index);
                if (previousIndexes.Count == 0)
                {
                    _cells.Remove(previousCell);
                }
            }

            _icons[update.Index] = update.Snapshot;
            if (previousCell != currentCell)
            {
                GetOrCreateCell(currentCell).Add(update.Index);
            }
            UpdateAnchorExtents(update.Snapshot);
        }
    }

    /// <summary>
    /// Returns icons from grid cells intersecting the viewport, ordered by layer and widened
    /// for icon dimensions and world wrapping.
    /// </summary>
    public MapIconSnapshot[] GetVisible(
        double longitude,
        double latitude,
        double zoom,
        double viewportWidth,
        double viewportHeight,
        double heading = 0,
        double pitch = 0)
    {
        if (_icons.Length == 0 ||
            viewportWidth <= 0 ||
            viewportHeight <= 0)
        {
            return [];
        }

        double worldSize = 256 * Math.Pow(2, Math.Clamp(zoom, 0, MapCamera.MaximumTileZoom));
        MapCamera.GetMapPlaneViewportBounds(
            viewportWidth,
            viewportHeight,
            heading,
            pitch,
            out double minimumX,
            out double minimumY,
            out double maximumX,
            out double maximumY);
        double pitchScale = 1 / Math.Max(
            Math.Cos(MapCamera.NormalizePitch(pitch) * Math.PI / 180),
            0.5);
        minimumX -= _maximumHorizontalAnchorExtent * pitchScale;
        maximumX += _maximumHorizontalAnchorExtent * pitchScale;
        minimumY -= _maximumVerticalAnchorExtent * pitchScale;
        maximumY += _maximumVerticalAnchorExtent * pitchScale;
        double minimumWorldX = minimumX / worldSize;
        double maximumWorldX = maximumX / worldSize;
        double minimumWorldY = minimumY / worldSize;
        double maximumWorldY = maximumY / worldSize;
        if ((maximumWorldX - minimumWorldX) >= 1 ||
            (maximumWorldY - minimumWorldY) >= 1)
        {
            return [.. _icons];
        }

        double centerX = MapCamera.LongitudeToWorldX(longitude);
        double cameraY = MapCamera.LatitudeToWorldY(latitude);
        double centerY = Math.Clamp(cameraY, 0, 1);
        int firstWorldX = (int)Math.Floor((centerX + minimumWorldX) * GridSize);
        int lastWorldX = (int)Math.Floor((centerX + maximumWorldX) * GridSize);
        if ((lastWorldX - firstWorldX + 1) >= GridSize)
        {
            return [.. _icons];
        }
        int firstY = Math.Max(
            0,
            (int)Math.Floor((centerY + minimumWorldY) * GridSize));
        int lastY = Math.Min(
            GridSize - 1,
            (int)Math.Floor((centerY + maximumWorldY) * GridSize));

        HashSet<int> visibleIndexes = [];
        for (int y = firstY; y <= lastY; y++)
        {
            for (int worldX = firstWorldX; worldX <= lastWorldX; worldX++)
            {
                int x = ((worldX % GridSize) + GridSize) % GridSize;
                if (!_cells.TryGetValue((y * GridSize) + x, out List<int>? indexes))
                {
                    continue;
                }
                foreach (int index in indexes)
                {
                    visibleIndexes.Add(index);
                }
            }
        }
        return
        [
            .. visibleIndexes
                .OrderBy(index => _icons[index].LayerIndex)
                .ThenBy(index => GetOrderIndex(index, _icons[index]))
                .Select(index => _icons[index]),
        ];
    }

    /// <summary>
    /// Finds the last rendered visible icon containing a viewport point, using the spatial
    /// grid to avoid scanning unrelated icons.
    /// </summary>
    public bool TryHitTest(
        double longitude,
        double latitude,
        double zoom,
        double viewportWidth,
        double viewportHeight,
        double viewportX,
        double viewportY,
        bool[] visibleLayers,
        out int iconIndex)
    {
        return TryHitTest(
            longitude,
            latitude,
            zoom,
            viewportWidth,
            viewportHeight,
            viewportX,
            viewportY,
            visibleLayers,
            0,
            0,
            out iconIndex);
    }

    public bool TryHitTest(
        double longitude,
        double latitude,
        double zoom,
        double viewportWidth,
        double viewportHeight,
        double viewportX,
        double viewportY,
        bool[] visibleLayers,
        double heading,
        out int iconIndex)
    {
        return TryHitTest(
            longitude,
            latitude,
            zoom,
            viewportWidth,
            viewportHeight,
            viewportX,
            viewportY,
            visibleLayers,
            heading,
            0,
            out iconIndex);
    }

    public bool TryHitTest(
        double longitude,
        double latitude,
        double zoom,
        double viewportWidth,
        double viewportHeight,
        double viewportX,
        double viewportY,
        bool[] visibleLayers,
        double heading,
        double pitch,
        out int iconIndex)
    {
        return TryHitTest(
            longitude,
            latitude,
            zoom,
            viewportWidth,
            viewportHeight,
            viewportX,
            viewportY,
            visibleLayers,
            heading,
            pitch,
            out iconIndex,
            out _);
    }

    internal bool TryHitTest(
        double longitude,
        double latitude,
        double zoom,
        double viewportWidth,
        double viewportHeight,
        double viewportX,
        double viewportY,
        bool[] visibleLayers,
        double heading,
        out int elementIndex,
        out int orderIndex)
    {
        return TryHitTest(
            longitude,
            latitude,
            zoom,
            viewportWidth,
            viewportHeight,
            viewportX,
            viewportY,
            visibleLayers,
            heading,
            0,
            out elementIndex,
            out orderIndex);
    }

    internal bool TryHitTest(
        double longitude,
        double latitude,
        double zoom,
        double viewportWidth,
        double viewportHeight,
        double viewportX,
        double viewportY,
        bool[] visibleLayers,
        double heading,
        double pitch,
        out int elementIndex,
        out int orderIndex)
    {
        elementIndex = -1;
        orderIndex = -1;
        if (_icons.Length == 0 ||
            viewportWidth <= 0 ||
            viewportHeight <= 0 ||
            viewportX < 0 ||
            viewportY < 0 ||
            viewportX > viewportWidth ||
            viewportY > viewportHeight ||
            !MapCamera.TryGetLocationFromOffset(
                longitude,
                latitude,
                zoom,
                viewportWidth,
                viewportHeight,
                viewportX,
                viewportY,
                heading,
                pitch,
                out MapCenter hitLocation))
        {
            return false;
        }

        double worldSize = 256 * Math.Pow(2, Math.Clamp(zoom, 0, MapCamera.MaximumTileZoom));
        double worldX = MapCamera.LongitudeToWorldX(hitLocation.Longitude);
        double worldY = Math.Clamp(MapCamera.LatitudeToWorldY(hitLocation.Latitude), 0, 1);
        int firstWorldX = (int)Math.Floor(
            (worldX - (_maximumHorizontalAnchorExtent / worldSize)) * GridSize);
        int lastWorldX = (int)Math.Floor(
            (worldX + (_maximumHorizontalAnchorExtent / worldSize)) * GridSize);
        int firstY = Math.Max(
            0,
            (int)Math.Floor(
                (worldY - (_maximumVerticalAnchorExtent / worldSize)) * GridSize));
        int lastY = Math.Min(
            GridSize - 1,
            (int)Math.Floor(
                (worldY + (_maximumVerticalAnchorExtent / worldSize)) * GridSize));

        if ((lastWorldX - firstWorldX + 1) >= GridSize)
        {
            int fullScanIndex = -1;
            int fullScanOrder = -1;
            for (int index = 0; index < _icons.Length; index++)
            {
                int currentOrder = GetOrderIndex(index, _icons[index]);
                if (currentOrder > fullScanOrder &&
                    ContainsPoint(
                        index,
                        longitude,
                        latitude,
                        zoom,
                        viewportWidth,
                        viewportHeight,
                        viewportX,
                        viewportY,
                        visibleLayers,
                        heading,
                        pitch))
                {
                    fullScanIndex = index;
                    fullScanOrder = currentOrder;
                }
            }
            if (fullScanIndex < 0)
            {
                return false;
            }

            MapIconSnapshot fullScanCandidate = _icons[fullScanIndex];
            elementIndex = GetElementIndex(fullScanIndex, fullScanCandidate);
            orderIndex = fullScanOrder;
            return true;
        }

        int candidateIndex = -1;
        int candidateOrder = -1;
        for (int y = firstY; y <= lastY; y++)
        {
            for (int candidateWorldX = firstWorldX;
                candidateWorldX <= lastWorldX;
                candidateWorldX++)
            {
                int x = ((candidateWorldX % GridSize) + GridSize) % GridSize;
                if (!_cells.TryGetValue((y * GridSize) + x, out List<int>? indexes))
                {
                    continue;
                }

                foreach (int index in indexes)
                {
                    int currentOrder = GetOrderIndex(index, _icons[index]);
                    if (currentOrder > candidateOrder &&
                        ContainsPoint(
                            index,
                            longitude,
                            latitude,
                            zoom,
                            viewportWidth,
                            viewportHeight,
                            viewportX,
                            viewportY,
                            visibleLayers,
                            heading,
                            pitch))
                    {
                        candidateIndex = index;
                        candidateOrder = currentOrder;
                    }
                }
            }
        }

        if (candidateIndex < 0)
        {
            return false;
        }

        MapIconSnapshot candidate = _icons[candidateIndex];
        elementIndex = GetElementIndex(candidateIndex, candidate);
        orderIndex = candidateOrder;
        return true;
    }

    private bool ContainsPoint(
        int index,
        double longitude,
        double latitude,
        double zoom,
        double viewportWidth,
        double viewportHeight,
        double viewportX,
        double viewportY,
        bool[] visibleLayers,
        double heading,
        double pitch)
    {
        MapIconSnapshot icon = _icons[index];
        if (!icon.IsEnabled ||
            (uint)icon.LayerIndex >= (uint)visibleLayers.Length ||
            !visibleLayers[icon.LayerIndex] ||
            !MapCamera.TryProjectLocation(
                icon.Longitude,
                icon.Latitude,
                longitude,
                latitude,
                zoom,
                viewportWidth,
                viewportHeight,
                heading,
                pitch,
                out MapViewportPoint point))
        {
            return false;
        }

        MapViewportPoint topLeft = MapRenderer.GetMapIconTopLeft(point, icon);
        return viewportX >= topLeft.X &&
            viewportY >= topLeft.Y &&
            viewportX < topLeft.X + icon.Width &&
            viewportY < topLeft.Y + icon.Height;
    }

    /// <summary>
    /// Adds an icon index to its spatial cell and updates aggregate culling bounds.
    /// </summary>
    private void Add(int index, MapIconSnapshot icon)
    {
        GetOrCreateCell(GetCell(icon.Longitude, icon.Latitude)).Add(index);
        UpdateAnchorExtents(icon);
    }

    private void UpdateAnchorExtents(MapIconSnapshot icon)
    {
        _maximumHorizontalAnchorExtent = Math.Max(
            _maximumHorizontalAnchorExtent,
            icon.Width * Math.Max(
                Math.Abs(icon.NormalizedAnchorX),
                Math.Abs(1 - icon.NormalizedAnchorX)));
        _maximumVerticalAnchorExtent = Math.Max(
            _maximumVerticalAnchorExtent,
            icon.Height * Math.Max(
                Math.Abs(icon.NormalizedAnchorY),
                Math.Abs(1 - icon.NormalizedAnchorY)));
    }

    private static int GetElementIndex(int index, MapIconSnapshot icon) =>
        icon.ElementIndex >= 0 ? icon.ElementIndex : index;

    private static int GetOrderIndex(int index, MapIconSnapshot icon) =>
        icon.OrderIndex >= 0 ? icon.OrderIndex : index;

    /// <summary>
    /// Gets the mutable index list for a grid cell, creating it when first populated.
    /// </summary>
    private List<int> GetOrCreateCell(int cell)
    {
        if (!_cells.TryGetValue(cell, out List<int>? indexes))
        {
            indexes = [];
            _cells.Add(cell, indexes);
        }
        return indexes;
    }

    /// <summary>
    /// Maps a geographic location to a clamped Web Mercator grid-cell index.
    /// </summary>
    private static int GetCell(double longitude, double latitude)
    {
        int x = Math.Min(
            GridSize - 1,
            (int)(MapCamera.LongitudeToWorldX(longitude) * GridSize));
        int y = Math.Clamp(
            (int)(MapCamera.LatitudeToWorldY(latitude) * GridSize),
            0,
            GridSize - 1);
        return (y * GridSize) + x;
    }
}

/// <summary>
/// Carries a versioned UI-rasterized icon BGRA buffer and pixel dimensions to the dedicated
/// GPU upload thread.
/// </summary>
/// <remarks>
/// The texture identifier and version reject superseded UI rasterizations. Pixel bytes are
/// retained only for device recreation and must never be included in ETW.
/// </remarks>
internal sealed record MapIconPixelData(
    long TextureId,
    long Version,
    byte[] Pixels,
    uint Width,
    uint Height);
