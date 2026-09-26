namespace WinUIEx.Maps.Rendering;

internal sealed partial class MapRenderer
{
    internal bool TryHitTestIncident(long sourceId, double x, double y,
        out VectorTileFeature? feature)
    {
        feature = null;
        lock (RenderLock)
        {
            if (!_rasterLayers.TryGetValue(sourceId, out var state) || state.Scene is null ||
                x < 0 || y < 0 || x > _viewportWidth || y > _viewportHeight)
                return false;
            LayerRenderSnapshot layer = _layerRenderPlan.FirstOrDefault(l => l.RuntimeId == sourceId);
            if (!layer.IsVisible || layer.Opacity <= 0 ||
                _displayZoom < layer.MinZoom || _displayZoom >= layer.MaxZoom)
                return false;
            MapScene activeScene = CreateCurrentRasterScene(state.Scene.TileZoom);
            double bestDistance = double.PositiveInfinity;
            bool bestIsPoint = false;
            foreach ((RasterTileKey key, VectorTileCacheEntry tile) in _vectorTiles)
            {
                if (key.SourceId != sourceId ||
                    (key.Id.Zoom != state.Scene.TileZoom && !state.FallbackTileZooms.Contains(key.Id.Zoom)))
                    continue;
                if (key.Id.Zoom != state.Scene.TileZoom &&
                    GetVectorGeometryReplacementOpacity(sourceId, key.Id, activeScene,
                        layer.FadeDuration, state.FallbackTileZooms) >= 1)
                    continue;
                foreach (VisibleTile instance in GetVisibleCachedTileInstances(key.Id,
                    _displayLongitude, _displayLatitude, _displayZoom, _viewportWidth,
                    _viewportHeight, _displayHeading, _displayPitch, 0))
                {
                    const double lineTolerance = 8;
                    const double pointTolerance = AzureOverlayAcquisitionSession.IncidentSymbolSize / 2d;
                    double tolerance = Math.Max(lineTolerance, pointTolerance);
                    double left = double.PositiveInfinity, top = double.PositiveInfinity;
                    double right = double.NegativeInfinity, bottom = double.NegativeInfinity;
                    for (int corner = 0; corner < 4; corner++)
                    {
                        MapCamera.UntransformViewportOffset(
                            x + ((corner & 1) == 0 ? -tolerance : tolerance) - _viewportWidth / 2,
                            y + ((corner & 2) == 0 ? -tolerance : tolerance) - _viewportHeight / 2,
                            _displayHeading, _displayPitch, _viewportHeight, out double px, out double py);
                        px = (px + _viewportWidth / 2 - instance.Left) / instance.Size;
                        py = (py + _viewportHeight / 2 - instance.Top) / instance.Size;
                        left = Math.Min(left, px); right = Math.Max(right, px);
                        top = Math.Min(top, py); bottom = Math.Max(bottom, py);
                    }
                    foreach (var candidate in tile.GetIncidentIndex().Query(left, top, right, bottom))
                    {
                        double distance = double.PositiveInfinity;
                        foreach (VectorTilePoint point in candidate.Points)
                        {
                            MapScreenPoint p = ProjectVectorPoint(point, instance, _viewportWidth,
                                _viewportHeight, _displayHeading, _displayPitch);
                            if (TrafficIncidentIcons.Contains(x - p.X, y - p.Y))
                                distance = Math.Min(distance, Math.Pow(p.X - x, 2) + Math.Pow(p.Y - y, 2));
                        }
                        foreach (VectorTileLine line in candidate.Lines)
                        {
                            for (int i = 1; i < line.Points.Length; i++)
                            {
                                MapScreenPoint a = ProjectVectorPoint(line.Points[i - 1], instance,
                                    _viewportWidth, _viewportHeight, _displayHeading, _displayPitch);
                                MapScreenPoint b = ProjectVectorPoint(line.Points[i], instance,
                                    _viewportWidth, _viewportHeight, _displayHeading, _displayPitch);
                                distance = Math.Min(distance, IncidentSegmentDistanceSquared(x, y, a, b));
                            }
                        }
                        bool isPoint = candidate.Points.Length != 0;
                        if ((isPoint ? double.IsFinite(distance) : distance <= lineTolerance * lineTolerance) &&
                            ((isPoint && !bestIsPoint) ||
                             (isPoint == bestIsPoint && distance < bestDistance)))
                        {
                            bestDistance = distance;
                            bestIsPoint = isPoint;
                            feature = candidate;
                        }
                    }
                }
            }
            return feature is not null;
        }
    }

    internal static double IncidentSegmentDistanceSquared(double x, double y,
        MapScreenPoint a, MapScreenPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double length = dx * dx + dy * dy;
        double t = length == 0 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / length, 0, 1);
        double px = a.X + t * dx - x, py = a.Y + t * dy - y;
        return px * px + py * py;
    }
}
