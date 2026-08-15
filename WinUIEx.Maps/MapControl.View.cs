using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Rendering.Diagnostics;
using Windows.Devices.Geolocation;

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
