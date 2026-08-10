using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using WinUIEx.Maps.Rendering;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using System.Diagnostics;

namespace WinUIEx.Maps;

public sealed partial class MapControl
{
    private readonly TouchRotationState _touchRotation = new();

    private const VirtualKey PlusKey = (VirtualKey)187;
    private const VirtualKey MinusKey = (VirtualKey)189;
    private readonly Dictionary<VirtualKey, long> _navigationKeys = [];
    private readonly Dictionary<uint, MapElementHitTarget> _hoveredMapElements = [];

    /// <inheritdoc />
    protected override void OnGotFocus(RoutedEventArgs e)
    {
        base.OnGotFocus(e);
        UpdateFocusVisualState();
    }

    /// <inheritdoc />
    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        CancelKeyboardNavigation();
        UpdateFocusVisualState();
    }

    private void UpdateFocusVisualState()
    {
        VisualStateManager.GoToState(
            this,
            FocusState switch
            {
                FocusState.Pointer => "PointerFocused",
                FocusState.Unfocused => "Unfocused",
                _ => "Focused",
            },
            true);
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);

        if (IsModifierKeyPressed())
        {
            CancelKeyboardNavigation();
            return;
        }

        if (!IsNavigationKey(e.Key))
        {
            return;
        }

        if (_navigationKeys.TryAdd(e.Key, Stopwatch.GetTimestamp()))
        {
            PublishKeyboardNavigation();
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnKeyUp(KeyRoutedEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Handled || !_navigationKeys.Remove(e.Key, out long pressedTimestamp))
        {
            return;
        }

        TimeSpan heldDuration = Stopwatch.GetElapsedTime(pressedTimestamp);
        PublishKeyboardNavigation();
        if (heldDuration < KeyboardNavigationState.HoldThreshold)
        {
            ApplyDiscreteKeyboardNavigation(e.Key);
        }
        else
        {
            CommitKeyboardNavigation();
        }
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled)
        {
            return;
        }

        Microsoft.UI.Input.PointerPoint point = e.GetCurrentPoint(this);
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse &&
            point.Properties.IsLeftButtonPressed &&
            !IsModifierKeyPressed())
        {
            Focus(FocusState.Pointer);
        }
    }

    private void OnMapElementPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        RaiseMapElementPointerEvent(
            e,
            MapElementInputEventKind.PointerPressed,
            static (layer, element, args) => layer.RaisePointerPressed(element, args));
    }

    private void OnMapElementPointerEntered(object sender, PointerRoutedEventArgs e) =>
        UpdateHoveredMapElement(e);

    private void OnMapElementPointerExited(object sender, PointerRoutedEventArgs e)
    {
        uint pointerId = e.Pointer.PointerId;
        if (_hoveredMapElements.Remove(pointerId, out MapElementHitTarget previous))
        {
            previous.Layer.RaisePointerExited(previous.Element, e);
        }
    }

    private void OnMapElementPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        MapElementHitTarget? target = UpdateHoveredMapElement(e);
        if ((_elementInputHandlers & MapElementInputEventKind.PointerMoved) != 0 &&
            target is MapElementHitTarget hit)
        {
            hit.Layer.RaisePointerMoved(hit.Element, e);
        }
    }

    private void OnMapElementPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        RaiseMapElementPointerEvent(
            e,
            MapElementInputEventKind.PointerReleased,
            static (layer, element, args) => layer.RaisePointerReleased(element, args));
    }

    private void OnMapElementTapped(object sender, TappedRoutedEventArgs e)
    {
        Point mapPosition = e.GetPosition(this);
        if ((_elementInputHandlers & MapElementInputEventKind.Tapped) == 0 ||
            !TryHitTestMapElement(
                e.GetPosition((UIElement?)_panel ?? this),
                out MapElementHitTarget hit) ||
            !TryGetLocationFromOffset(mapPosition, out Geopoint location))
        {
            return;
        }

        hit.Layer.RaiseTapped(hit.Element, location, e);
    }

    private void OnMapElementRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        Point mapPosition = e.GetPosition(this);
        if ((_elementInputHandlers & MapElementInputEventKind.RightTapped) == 0 ||
            !TryHitTestMapElement(
                e.GetPosition((UIElement?)_panel ?? this),
                out MapElementHitTarget hit) ||
            !TryGetLocationFromOffset(mapPosition, out Geopoint location))
        {
            return;
        }

        hit.Layer.RaiseRightTapped(hit.Element, location, e);
    }

    /// <inheritdoc />
    protected override void OnDoubleTapped(DoubleTappedRoutedEventArgs e)
    {
        base.OnDoubleTapped(e);

        SetZoomTarget(
            ZoomLevel + 1,
            e.GetPosition((UIElement?)_panel ?? this));
        e.Handled = true;
    }

    private MapElementHitTarget? UpdateHoveredMapElement(PointerRoutedEventArgs e)
    {
        if ((_elementInputHandlers & MapElementInputEventKind.PointerHover) == 0)
        {
            return null;
        }

        uint pointerId = e.Pointer.PointerId;
        bool hasPrevious = _hoveredMapElements.TryGetValue(
            pointerId,
            out MapElementHitTarget previous);
        bool hasCurrent = TryHitTestMapElement(
            e.GetCurrentPoint((UIElement?)_panel ?? this).Position,
            out MapElementHitTarget current);
        if (hasPrevious && (!hasCurrent || !previous.Equals(current)))
        {
            _hoveredMapElements.Remove(pointerId);
            previous.Layer.RaisePointerExited(previous.Element, e);
        }
        if (hasCurrent)
        {
            _hoveredMapElements[pointerId] = current;
            if (!hasPrevious || !previous.Equals(current))
            {
                current.Layer.RaisePointerEntered(current.Element, e);
            }
            return current;
        }

        return null;
    }

    private void RaiseMapElementPointerEvent(
        PointerRoutedEventArgs args,
        MapElementInputEventKind kind,
        Action<MapElementsLayer, MapElement, PointerRoutedEventArgs> raise)
    {
        if ((_elementInputHandlers & kind) == 0 ||
            !TryHitTestMapElement(
                args.GetCurrentPoint((UIElement?)_panel ?? this).Position,
                out MapElementHitTarget hit))
        {
            return;
        }

        raise(hit.Layer, hit.Element, args);
    }

    private bool TryHitTestMapElement(Point panelPoint, out MapElementHitTarget hit)
    {
        if (!_runtimeResourcesReleased &&
            _renderer.TryHitTestMapElement(panelPoint.X, panelPoint.Y, out int index) &&
            (uint)index < (uint)_publishedMapElements.Count &&
            (uint)index < (uint)_publishedElementLayers.Count)
        {
            hit = new MapElementHitTarget(
                _publishedElementLayers[index],
                _publishedMapElements[index]);
            return true;
        }

        hit = default;
        return false;
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        Microsoft.UI.Input.PointerPoint point = e.GetCurrentPoint((UIElement?)_panel ?? this);
        int wheelDelta = point.Properties.MouseWheelDelta;
        if (wheelDelta == 0)
        {
            return;
        }

        int levels = Math.Max(1, (int)Math.Round(Math.Abs(wheelDelta) / 120d));
        SetZoomTarget(
            ZoomLevel + (Math.Sign(wheelDelta) * levels),
            point.Position);
        e.Handled = true;
    }

    private void SetZoomTarget(double zoom, Point anchor)
    {
        double targetZoom = Math.Clamp(zoom, 0, MapCamera.MaximumTileZoom);
        if (targetZoom == ZoomLevel)
        {
            return;
        }

        BasicGeoposition position = Center?.Position ?? new BasicGeoposition();
        double viewportWidth = _panel?.ActualWidth ?? ActualWidth;
        double viewportHeight = _panel?.ActualHeight ?? ActualHeight;
        double horizontalOffset = anchor.X - (viewportWidth / 2);
        double verticalOffset = anchor.Y - (viewportHeight / 2);
        MapCenter target = !_runtimeResourcesReleased
            ? _renderer.SetZoomTarget(
                targetZoom,
                horizontalOffset,
                verticalOffset,
                viewportWidth,
                viewportHeight,
                Heading,
                Pitch)
            : MapCamera.CenterForLocationAtOffset(
                MapCamera.LocationAtOffset(
                    position.Longitude,
                    position.Latitude,
                    ZoomLevel,
                    horizontalOffset,
                    verticalOffset,
                    Heading,
                    Pitch,
                    viewportHeight),
                targetZoom,
                horizontalOffset,
                verticalOffset,
                Heading,
                Pitch,
                viewportHeight);

        _suppressCameraUpdate = true;
        try
        {
            Center = new Geopoint(new BasicGeoposition
            {
                Longitude = target.Longitude,
                Latitude = target.Latitude,
            });
            ZoomLevel = targetZoom;
        }
        finally
        {
            _suppressCameraUpdate = false;
        }
    }

    private static bool IsNavigationKey(VirtualKey key)
    {
        return key is
            VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or
            VirtualKey.GamepadDPadLeft or VirtualKey.GamepadDPadRight or
            VirtualKey.GamepadDPadUp or VirtualKey.GamepadDPadDown or
            VirtualKey.GamepadLeftThumbstickLeft or VirtualKey.GamepadLeftThumbstickRight or
            VirtualKey.GamepadLeftThumbstickUp or VirtualKey.GamepadLeftThumbstickDown or
            VirtualKey.Add or PlusKey or VirtualKey.Subtract or MinusKey;
    }

    private readonly record struct MapElementHitTarget(
        MapElementsLayer Layer,
        MapElement Element);

    private void PublishKeyboardNavigation()
    {
        int horizontal = GetNavigationDirection(
            VirtualKey.Left, VirtualKey.GamepadDPadLeft, VirtualKey.GamepadLeftThumbstickLeft,
            VirtualKey.Right, VirtualKey.GamepadDPadRight, VirtualKey.GamepadLeftThumbstickRight);
        int vertical = GetNavigationDirection(
            VirtualKey.Up, VirtualKey.GamepadDPadUp, VirtualKey.GamepadLeftThumbstickUp,
            VirtualKey.Down, VirtualKey.GamepadDPadDown, VirtualKey.GamepadLeftThumbstickDown);
        int zoom = GetNavigationDirection(
            VirtualKey.Subtract, MinusKey, VirtualKey.None,
            VirtualKey.Add, PlusKey, VirtualKey.None);
        long startTimestamp = _navigationKeys.Count == 0 ? 0 : _navigationKeys.Values.Min();
        if (!_runtimeResourcesReleased)
        {
            _renderer.SetKeyboardNavigation(new KeyboardNavigationState(
                horizontal, vertical, zoom, startTimestamp));
        }
    }

    private void CancelKeyboardNavigation()
    {
        if (_navigationKeys.Count == 0)
        {
            return;
        }

        _navigationKeys.Clear();
        PublishKeyboardNavigation();
    }

    private int GetNavigationDirection(
        VirtualKey negativeFirst, VirtualKey negativeSecond, VirtualKey negativeThird,
        VirtualKey positiveFirst, VirtualKey positiveSecond, VirtualKey positiveThird)
    {
        bool negative = IsNavigationKeyHeld(negativeFirst) ||
            IsNavigationKeyHeld(negativeSecond) || IsNavigationKeyHeld(negativeThird);
        bool positive = IsNavigationKeyHeld(positiveFirst) ||
            IsNavigationKeyHeld(positiveSecond) || IsNavigationKeyHeld(positiveThird);
        return (positive ? 1 : 0) - (negative ? 1 : 0);
    }

    private bool IsNavigationKeyHeld(VirtualKey key) =>
        key != VirtualKey.None && _navigationKeys.ContainsKey(key);

    private void ApplyDiscreteKeyboardNavigation(VirtualKey key)
    {
        int horizontal = GetNavigationDirectionForKey(
            key, VirtualKey.Left, VirtualKey.GamepadDPadLeft, VirtualKey.GamepadLeftThumbstickLeft,
            VirtualKey.Right, VirtualKey.GamepadDPadRight, VirtualKey.GamepadLeftThumbstickRight);
        int vertical = GetNavigationDirectionForKey(
            key, VirtualKey.Up, VirtualKey.GamepadDPadUp, VirtualKey.GamepadLeftThumbstickUp,
            VirtualKey.Down, VirtualKey.GamepadDPadDown, VirtualKey.GamepadLeftThumbstickDown);
        int zoom = GetNavigationDirectionForKey(
            key, VirtualKey.Subtract, MinusKey, VirtualKey.None,
            VirtualKey.Add, PlusKey, VirtualKey.None);
        if (zoom != 0)
        {
            SetZoomTarget(ZoomLevel + zoom, new Point(ActualWidth / 2, ActualHeight / 2));
            return;
        }

        double distance = Math.Min(ActualWidth, ActualHeight) / 2;
        PanByPixels(-horizontal * distance, -vertical * distance);
    }

    private static int GetNavigationDirectionForKey(
        VirtualKey key,
        VirtualKey negativeFirst, VirtualKey negativeSecond, VirtualKey negativeThird,
        VirtualKey positiveFirst, VirtualKey positiveSecond, VirtualKey positiveThird)
    {
        return key == positiveFirst || key == positiveSecond || key == positiveThird
            ? 1
            : key == negativeFirst || key == negativeSecond || key == negativeThird ? -1 : 0;
    }

    private void CommitKeyboardNavigation()
    {
        if (_runtimeResourcesReleased ||
            !_renderer.TryGetDisplayedCamera(
                out MapCenter center,
                out double zoom,
                out double heading,
                out double pitch))
        {
            return;
        }

        _suppressCameraUpdate = true;
        try
        {
            Center = new Geopoint(new BasicGeoposition
            {
                Longitude = center.Longitude,
                Latitude = center.Latitude,
            });
            ZoomLevel = zoom;
            Heading = heading;
            Pitch = pitch;
            _renderer.SetCameraTargetImmediately(
                center.Longitude,
                center.Latitude,
                zoom,
                _panel?.ActualWidth ?? ActualWidth,
                _panel?.ActualHeight ?? ActualHeight,
                heading,
                pitch);
        }
        finally
        {
            _suppressCameraUpdate = false;
        }
    }

    /// <inheritdoc />
    protected override void OnManipulationStarted(
        ManipulationStartedRoutedEventArgs e)
    {
        base.OnManipulationStarted(e);
        if (e.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            _touchRotation.Reset();
        }
    }

    /// <inheritdoc />
    protected override void OnManipulationDelta(ManipulationDeltaRoutedEventArgs e)
    {
        base.OnManipulationDelta(e);

        if (!CanManipulate(e.PointerDeviceType))
        {
            return;
        }

        if (e.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            ApplyTouchManipulation(e);
        }
        else
        {
            if (e.IsInertial)
            {
                e.Complete();
            }
            else
            {
                PanByPixels(e.Delta.Translation.X, e.Delta.Translation.Y);
            }
        }

        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnManipulationCompleted(
        ManipulationCompletedRoutedEventArgs e)
    {
        base.OnManipulationCompleted(e);
        if (e.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            return;
        }

        bool rotated = _touchRotation.IsActive;
        _touchRotation.Reset();
        if (rotated &&
            Math.Abs(MapCamera.ShortestHeadingDelta(Heading, 0)) <=
                TouchRotationState.SnapThreshold)
        {
            Heading = 0;
        }
    }

    private static bool CanManipulate(Microsoft.UI.Input.PointerDeviceType pointerDeviceType)
    {
        return !IsModifierKeyPressed() &&
            (pointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse ||
             IsVirtualKeyPressed(VirtualKey.LeftButton));
    }

    private static bool IsModifierKeyPressed()
    {
        return IsVirtualKeyPressed(VirtualKey.LeftShift) ||
            IsVirtualKeyPressed(VirtualKey.RightShift) ||
            IsVirtualKeyPressed(VirtualKey.LeftControl) ||
            IsVirtualKeyPressed(VirtualKey.RightControl) ||
            IsVirtualKeyPressed(VirtualKey.LeftMenu) ||
            IsVirtualKeyPressed(VirtualKey.RightMenu) ||
            IsVirtualKeyPressed(VirtualKey.LeftWindows) ||
            IsVirtualKeyPressed(VirtualKey.RightWindows);
    }

    private static bool IsVirtualKeyPressed(VirtualKey key)
    {
        return (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) &
            CoreVirtualKeyStates.Down) != 0;
    }

    private void ApplyTouchManipulation(ManipulationDeltaRoutedEventArgs e)
    {
        BasicGeoposition position = Center?.Position ?? new BasicGeoposition();
        double currentZoom = ZoomLevel;
        double currentHeading = Heading;
        MapCenter target = MapCamera.PanByPixels(
            position.Longitude,
            position.Latitude,
            currentZoom,
            e.Delta.Translation.X,
            e.Delta.Translation.Y,
            currentHeading,
            Pitch,
            _panel?.ActualHeight ?? ActualHeight);
        double targetZoom = currentZoom;
        if (MapCamera.TryGetZoomDeltaFromScale(
                e.Delta.Scale,
                out double zoomDelta))
        {
            targetZoom = Math.Clamp(currentZoom + zoomDelta, 0, MapCamera.MaximumTileZoom);
        }
        double targetHeading = MapCamera.NormalizeHeading(
            currentHeading +
            _touchRotation.GetRotationDelta(-e.Cumulative.Rotation));
        if (targetZoom != currentZoom || targetHeading != currentHeading)
        {
            double viewportWidth = _panel?.ActualWidth ?? ActualWidth;
            double viewportHeight = _panel?.ActualHeight ?? ActualHeight;
            double horizontalOffset = e.Position.X - (viewportWidth / 2);
            double verticalOffset = e.Position.Y - (viewportHeight / 2);
            MapCenter anchor = MapCamera.LocationAtOffset(
                target.Longitude,
                target.Latitude,
                currentZoom,
                horizontalOffset,
                verticalOffset,
                currentHeading,
                Pitch,
                viewportHeight);
            target = MapCamera.CenterForLocationAtOffset(
                anchor,
                targetZoom,
                horizontalOffset,
                verticalOffset,
                targetHeading,
                Pitch,
                viewportHeight);
        }

        if (targetZoom == currentZoom &&
            targetHeading == currentHeading &&
            target.Longitude == position.Longitude &&
            target.Latitude == position.Latitude)
        {
            return;
        }

        _suppressCameraUpdate = true;
        try
        {
            Center = new Geopoint(new BasicGeoposition
            {
                Longitude = target.Longitude,
                Latitude = target.Latitude,
            });
            ZoomLevel = targetZoom;
            Heading = targetHeading;
            if (!_runtimeResourcesReleased)
            {
                _renderer.SetCameraTargetImmediately(
                    target.Longitude,
                    target.Latitude,
                    targetZoom,
                    _panel?.ActualWidth ?? ActualWidth,
                    _panel?.ActualHeight ?? ActualHeight,
                    targetHeading,
                    Pitch);
            }
        }
        finally
        {
            _suppressCameraUpdate = false;
        }
    }

    private void PanByPixels(double horizontalDelta, double verticalDelta)
    {
        if (horizontalDelta == 0 && verticalDelta == 0)
        {
            return;
        }

        BasicGeoposition position = Center?.Position ?? new BasicGeoposition();
        MapCenter target = MapCamera.PanByPixels(
            position.Longitude,
            position.Latitude,
            ZoomLevel,
            horizontalDelta,
            verticalDelta,
            Heading,
            Pitch,
            _panel?.ActualHeight ?? ActualHeight);
        Center = new Geopoint(new BasicGeoposition
        {
            Longitude = target.Longitude,
            Latitude = target.Latitude,
        });
    }
}

internal sealed class TouchRotationState
{
    internal const double ActivationThreshold = 10;
    internal const double SnapThreshold = 10;
    private double _appliedRotation;

    internal bool IsActive { get; private set; }

    internal double GetRotationDelta(double cumulativeRotation)
    {
        if (!double.IsFinite(cumulativeRotation))
        {
            return 0;
        }

        if (!IsActive)
        {
            if (Math.Abs(cumulativeRotation) < ActivationThreshold)
            {
                return 0;
            }

            IsActive = true;
            _appliedRotation = Math.CopySign(
                ActivationThreshold,
                cumulativeRotation);
        }

        double delta = cumulativeRotation - _appliedRotation;
        _appliedRotation = cumulativeRotation;
        return delta;
    }

    internal void Reset()
    {
        IsActive = false;
        _appliedRotation = 0;
    }
}
