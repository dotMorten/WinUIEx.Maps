using Microsoft.UI.Xaml;
using Windows.Devices.Geolocation;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Rendering.Diagnostics;

namespace WinUIEx.Maps;

public sealed partial class MapControl
{
    private readonly object _viewChangeSync = new();
    private PendingViewChange? _pendingViewChange;

    /// <summary>
    /// Sets the view of the map displayed in the <see cref="MapControl"/> using the specified
    /// center.
    /// </summary>
    /// <param name="center">
    /// The center to use in the view. For more info, see the <see cref="Center"/> property.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the asynchronous operation succeeded; otherwise,
    /// <see langword="false"/>.
    /// </returns>
    public Task<bool> TrySetViewAsync(Geopoint center) =>
        TrySetViewAsync(center, null);

    /// <summary>
    /// Sets the view of the map displayed in the <see cref="MapControl"/> using the specified
    /// center and zoom level.
    /// </summary>
    /// <param name="center">
    /// The center to use in the view. For more info, see the <see cref="Center"/> property.
    /// </param>
    /// <param name="zoomLevel">
    /// The zoom level to use in the view. For more info, see the
    /// <see cref="ZoomLevel"/> property.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the asynchronous operation succeeded; otherwise,
    /// <see langword="false"/>.
    /// </returns>
    public Task<bool> TrySetViewAsync(Geopoint center, double? zoomLevel) =>
        TrySetViewAsync(center, zoomLevel, null, null);

    /// <summary>
    /// Sets the view of the map displayed in the <see cref="MapControl"/> using the specified
    /// center, zoom level, heading, and pitch.
    /// </summary>
    /// <param name="center">
    /// The center to use in the view. For more info, see the <see cref="Center"/> property.
    /// </param>
    /// <param name="zoomLevel">
    /// The zoom level to use in the view. For more info, see the
    /// <see cref="ZoomLevel"/> property.
    /// </param>
    /// <param name="heading">
    /// The heading to use in the view. For more info, see the <see cref="Heading"/> property.
    /// </param>
    /// <param name="desiredPitch">
    /// The pitch to use in the view. For more info, see the <see cref="Pitch"/> property.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the asynchronous operation succeeded; otherwise,
    /// <see langword="false"/>.
    /// </returns>
    public Task<bool> TrySetViewAsync(
        Geopoint center,
        double? zoomLevel,
        double? heading,
        double? desiredPitch) =>
        TrySetViewAsync(
            center,
            zoomLevel,
            heading,
            desiredPitch,
            MapAnimationKind.Default);

    /// <summary>
    /// Sets the view of the map displayed in the <see cref="MapControl"/> using the specified
    /// center, zoom level, heading, and pitch. The view change uses the specified animation.
    /// </summary>
    /// <param name="center">
    /// The center to use in the view. For more info, see the <see cref="Center"/> property.
    /// </param>
    /// <param name="zoomLevel">
    /// The zoom level to use in the view. For more info, see the
    /// <see cref="ZoomLevel"/> property.
    /// </param>
    /// <param name="heading">
    /// The heading to use in the view. For more info, see the <see cref="Heading"/> property.
    /// </param>
    /// <param name="desiredPitch">
    /// The pitch to use in the view. For more info, see the <see cref="Pitch"/> property.
    /// </param>
    /// <param name="animation">
    /// The animation to use when changing the view. For more info, see
    /// <see cref="MapAnimationKind"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the asynchronous operation succeeded; otherwise,
    /// <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Heading values are normalized to their equivalent value from 0 inclusive to 360
    /// exclusive. Pitch and zoom values are truncated to the nearest supported value.
    /// A view operation returns <see langword="false"/> when a newer view operation replaces
    /// it before its requested view is displayed.
    /// </remarks>
    public Task<bool> TrySetViewAsync(
        Geopoint center,
        double? zoomLevel,
        double? heading,
        double? desiredPitch,
        MapAnimationKind animation)
    {
        ArgumentNullException.ThrowIfNull(center);
        EnsureUiThread();
        if (!Enum.IsDefined(animation))
        {
            throw new ArgumentOutOfRangeException(nameof(animation));
        }
        MapControlEventSource.Log.CameraViewChangeRequested(
            (int)animation,
            zoomLevel.HasValue,
            heading.HasValue,
            desiredPitch.HasValue);

        BasicGeoposition requestedCenter = center.Position;
        MapScene target = MapCamera.CreateScene(
            requestedCenter.Longitude,
            requestedCenter.Latitude,
            zoomLevel ?? ZoomLevel,
            Math.Max(1, _panel?.ActualWidth ?? ActualWidth),
            Math.Max(1, _panel?.ActualHeight ?? ActualHeight),
            heading ?? Heading,
            desiredPitch ?? Pitch);
        var normalizedCenter = new Geopoint(new BasicGeoposition
        {
            Longitude = target.Longitude,
            Latitude = target.Latitude,
            Altitude = requestedCenter.Altitude,
        });

        _suppressCameraUpdate = true;
        try
        {
            Center = normalizedCenter;
            ZoomLevel = target.Zoom;
            Heading = target.Heading;
            Pitch = target.Pitch;
        }
        finally
        {
            _suppressCameraUpdate = false;
        }

        PendingViewChange? replaced;
        lock (_viewChangeSync)
        {
            replaced = _pendingViewChange;
            _pendingViewChange = null;
        }
        replaced?.Completion.TrySetResult(false);

        if (!IsLoaded ||
            _panel is null ||
            _panel.ActualWidth <= 0 ||
            _panel.ActualHeight <= 0)
        {
            return Task.FromResult(true);
        }

        var pending = new PendingViewChange(
            target,
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_viewChangeSync)
        {
            _pendingViewChange = pending;
        }

        UpdateCameraTarget(
            forceImmediate: animation == MapAnimationKind.None,
            animation,
            preservePendingViewChange: true);
        if (_renderer.TryGetDisplayedCamera(
            out MapCenter displayedCenter,
            out double displayedZoom,
            out double displayedHeading,
            out double displayedPitch))
        {
            TryCompleteViewChange(
                displayedCenter,
                displayedZoom,
                displayedHeading,
                displayedPitch);
        }

        return pending.Completion.Task;
    }

    /// <summary>
    /// Sets the view of the map displayed in the <see cref="MapControl"/> to the contents of
    /// the specified <see cref="GeoboundingBox"/> with the specified margin. The view change
    /// uses the specified animation.
    /// </summary>
    /// <param name="bounds">The geographic area to display in the view.</param>
    /// <param name="margin">The margin to use in the view.</param>
    /// <param name="animation">
    /// The animation to use when changing the view. For more info, see
    /// <see cref="MapAnimationKind"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the asynchronous operation succeeded; otherwise,
    /// <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// If the area specified by the <see cref="GeoboundingBox"/> doesn't fill the
    /// <see cref="MapControl"/>, the control also displays the surrounding area outside the
    /// <see cref="GeoboundingBox"/>.
    /// </remarks>
    public Task<bool> TrySetViewBoundsAsync(
        GeoboundingBox bounds,
        Thickness? margin,
        MapAnimationKind animation)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        EnsureUiThread();
        if (!Enum.IsDefined(animation))
        {
            throw new ArgumentOutOfRangeException(nameof(animation));
        }

        double viewportWidth = _panel?.ActualWidth ?? ActualWidth;
        double viewportHeight = _panel?.ActualHeight ?? ActualHeight;
        if (!TryCalculateBoundsView(
            bounds,
            margin ?? new Thickness(),
            viewportWidth,
            viewportHeight,
            Heading,
            Pitch,
            out BasicGeoposition center,
            out double zoom))
        {
            return Task.FromResult(false);
        }

        return TrySetViewAsync(
            new Geopoint(center),
            zoom,
            null,
            null,
            animation);
    }

    internal static bool TryCalculateBoundsView(
        GeoboundingBox bounds,
        Thickness margin,
        double viewportWidth,
        double viewportHeight,
        double heading,
        double pitch,
        out BasicGeoposition center,
        out double zoom)
    {
        center = default;
        zoom = 0;
        if (!AreValidMargins(margin) ||
            !double.IsFinite(viewportWidth) ||
            !double.IsFinite(viewportHeight))
        {
            return false;
        }

        double availableWidth = viewportWidth - margin.Left - margin.Right;
        double availableHeight = viewportHeight - margin.Top - margin.Bottom;
        if (availableWidth <= 0 || availableHeight <= 0)
        {
            return false;
        }

        BasicGeoposition northwest = bounds.NorthwestCorner;
        BasicGeoposition southeast = bounds.SoutheastCorner;
        if (!double.IsFinite(northwest.Longitude) ||
            !double.IsFinite(northwest.Latitude) ||
            !double.IsFinite(southeast.Longitude) ||
            !double.IsFinite(southeast.Latitude) ||
            northwest.Latitude < southeast.Latitude)
        {
            return false;
        }

        double westX = MapCamera.LongitudeToWorldX(northwest.Longitude);
        double eastX = MapCamera.LongitudeToWorldX(southeast.Longitude);
        if (eastX < westX)
        {
            eastX++;
        }
        double centerLongitude =
            MapCamera.WorldXToLongitude((westX + eastX) / 2);
        double northY = MapCamera.LatitudeToWorldY(northwest.Latitude);
        double southY = MapCamera.LatitudeToWorldY(southeast.Latitude);
        double centerLatitude =
            MapCamera.WorldYToLatitude((northY + southY) / 2);
        var boundsCenter = new MapCenter(centerLongitude, centerLatitude);
        double horizontalOffset = (margin.Left - margin.Right) / 2;
        double verticalOffset = (margin.Top - margin.Bottom) / 2;
        BasicGeoposition[] corners =
        [
            northwest,
            new BasicGeoposition
            {
                Longitude = southeast.Longitude,
                Latitude = northwest.Latitude,
            },
            southeast,
            new BasicGeoposition
            {
                Longitude = northwest.Longitude,
                Latitude = southeast.Latitude,
            },
        ];

        bool Fits(double candidateZoom, out MapCenter candidateCenter)
        {
            candidateCenter = MapCamera.CenterForLocationAtOffset(
                boundsCenter,
                candidateZoom,
                horizontalOffset,
                verticalOffset,
                heading,
                pitch,
                viewportHeight);
            foreach (BasicGeoposition corner in corners)
            {
                if (!MapCamera.TryProjectLocation(
                    corner.Longitude,
                    corner.Latitude,
                    candidateCenter.Longitude,
                    candidateCenter.Latitude,
                    candidateZoom,
                    viewportWidth,
                    viewportHeight,
                    heading,
                    pitch,
                    out MapViewportPoint point) ||
                    point.X < margin.Left - 0.5 ||
                    point.X > viewportWidth - margin.Right + 0.5 ||
                    point.Y < margin.Top - 0.5 ||
                    point.Y > viewportHeight - margin.Bottom + 0.5)
                {
                    return false;
                }
            }
            return true;
        }

        const double searchStep = 1d / 64;
        double lower = MapCamera.MaximumTileZoom;
        double upper = MapCamera.MaximumTileZoom;
        MapCenter fittedCenter;
        if (!Fits(lower, out fittedCenter))
        {
            bool foundFit = false;
            for (upper = MapCamera.MaximumTileZoom;
                upper > 0;
                upper = Math.Max(0, upper - searchStep))
            {
                lower = Math.Max(0, upper - searchStep);
                if (Fits(lower, out fittedCenter))
                {
                    foundFit = true;
                    break;
                }
            }

            if (!foundFit)
            {
                return false;
            }
        }

        for (int iteration = 0; iteration < 48; iteration++)
        {
            double candidate = (lower + upper) / 2;
            if (Fits(candidate, out MapCenter candidateCenter))
            {
                lower = candidate;
                fittedCenter = candidateCenter;
            }
            else
            {
                upper = candidate;
            }
        }

        center = new BasicGeoposition
        {
            Longitude = fittedCenter.Longitude,
            Latitude = fittedCenter.Latitude,
        };
        zoom = lower;
        return true;
    }

    private static bool AreValidMargins(Thickness margin) =>
        double.IsFinite(margin.Left) &&
        double.IsFinite(margin.Top) &&
        double.IsFinite(margin.Right) &&
        double.IsFinite(margin.Bottom) &&
        margin.Left >= 0 &&
        margin.Top >= 0 &&
        margin.Right >= 0 &&
        margin.Bottom >= 0;

    private void OnRendererDisplayedCameraChanged(MapScene scene) =>
        TryCompleteViewChange(
            new MapCenter(scene.Longitude, scene.Latitude),
            scene.Zoom,
            scene.Heading,
            scene.Pitch);

    private void TryCompleteViewChange(
        MapCenter center,
        double zoom,
        double heading,
        double pitch)
    {
        PendingViewChange? pending;
        lock (_viewChangeSync)
        {
            pending = _pendingViewChange;
            if (pending is null ||
                !IsRequestedViewDisplayed(
                    pending.Target,
                    center,
                    zoom,
                    heading,
                    pitch))
            {
                return;
            }

            _pendingViewChange = null;
        }

        pending.Completion.TrySetResult(true);
    }

    private void CancelPendingViewChange()
    {
        PendingViewChange? pending;
        lock (_viewChangeSync)
        {
            pending = _pendingViewChange;
            _pendingViewChange = null;
        }
        pending?.Completion.TrySetResult(false);
    }

    private static bool IsRequestedViewDisplayed(
        MapScene target,
        MapCenter center,
        double zoom,
        double heading,
        double pitch) =>
        Math.Abs(MapCamera.ShortestHeadingDelta(
            center.Longitude,
            target.Longitude)) <= 0.000000001 &&
        Math.Abs(center.Latitude - target.Latitude) <= 0.000000001 &&
        Math.Abs(zoom - target.Zoom) <= 0.001 &&
        Math.Abs(MapCamera.ShortestHeadingDelta(heading, target.Heading)) <= 0.001 &&
        Math.Abs(pitch - target.Pitch) <= 0.001;

    private sealed record PendingViewChange(
        MapScene Target,
        TaskCompletionSource<bool> Completion);
}
