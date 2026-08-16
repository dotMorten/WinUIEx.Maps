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
    private readonly Dictionary<VirtualKey, long> _modifiedNavigationKeys = [];
    private readonly Dictionary<uint, MapElementHitTarget> _hoveredMapElements = [];
    private readonly HashSet<uint> _pointerFocusRequests = [];
    private bool _isDescriptionDetailShortcutPressed;
    private bool _usePointerFocusVisual;

    /// <inheritdoc />
    protected override void OnGotFocus(RoutedEventArgs e)
    {
        base.OnGotFocus(e);
        if (FocusState == FocusState.Pointer)
        {
            _usePointerFocusVisual = true;
        }
        UpdateFocusVisualState();
    }

    /// <inheritdoc />
    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _isDescriptionDetailShortcutPressed = false;
        _usePointerFocusVisual = false;
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
                _ => _usePointerFocusVisual ? "PointerFocused" : "Focused",
            },
            true);
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);

        bool shiftPressed = IsShiftPressed();
        bool controlPressed = IsControlPressed();
        bool menuPressed = IsMenuPressed();
        if (e.Key == VirtualKey.D &&
            controlPressed &&
            (menuPressed || shiftPressed))
        {
            CancelKeyboardNavigation();
            if (!_isDescriptionDetailShortcutPressed)
            {
                _isDescriptionDetailShortcutPressed = true;
                ToggleAccessibilityDescriptionDetail();
            }
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            CancelKeyboardNavigation();
            Focus(FocusState.Keyboard);
            e.Handled = true;
            return;
        }

        if (shiftPressed && IsArrowKey(e.Key))
        {
            if (_navigationKeys.Count != 0)
            {
                _navigationKeys.Clear();
                PublishKeyboardNavigation();
            }
            if (_modifiedNavigationKeys.TryAdd(
                    e.Key,
                    Stopwatch.GetTimestamp()))
            {
                CancelPendingViewChange();
                PublishKeyboardNavigation();
            }
            e.Handled = true;
            return;
        }

        if (controlPressed ||
            menuPressed ||
            IsWindowsKeyPressed() ||
            (shiftPressed && e.Key is not PlusKey and not MinusKey))
        {
            CancelKeyboardNavigation();
            return;
        }

        if (!IsNavigationKey(e.Key))
        {
            return;
        }
        if (_modifiedNavigationKeys.ContainsKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (_navigationKeys.TryAdd(e.Key, Stopwatch.GetTimestamp()))
        {
            CancelPendingViewChange();
            PublishKeyboardNavigation();
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnKeyUp(KeyRoutedEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == VirtualKey.D)
        {
            _isDescriptionDetailShortcutPressed = false;
        }
        if (_modifiedNavigationKeys.Remove(
                e.Key,
                out long modifiedPressedTimestamp))
        {
            TimeSpan modifiedHeldDuration =
                Stopwatch.GetElapsedTime(modifiedPressedTimestamp);
            PublishKeyboardNavigation();
            if (modifiedHeldDuration < KeyboardNavigationState.HoldThreshold)
            {
                ApplyModifiedArrowNavigation(e.Key);
            }
            else
            {
                CommitKeyboardNavigation();
            }
            e.Handled = true;
            return;
        }
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
        if (ShouldFocusFromPointer(point, e.Pointer.PointerDeviceType) &&
            _pointerFocusRequests.Add(e.Pointer.PointerId))
        {
            FocusFromPointer();
        }
    }

    private void OnMapElementPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Microsoft.UI.Input.PointerPoint point = e.GetCurrentPoint(this);
        if (ShouldFocusFromPointer(point, e.Pointer.PointerDeviceType) &&
            _pointerFocusRequests.Add(e.Pointer.PointerId))
        {
            FocusFromPointer();
        }
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
        if (_pointerFocusRequests.Remove(e.Pointer.PointerId))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (IsLoaded)
                {
                    FocusFromPointer();
                }
            });
        }
        RaiseMapElementPointerEvent(
            e,
            MapElementInputEventKind.PointerReleased,
            static (layer, element, args) => layer.RaisePointerReleased(element, args));
    }

    private static bool ShouldFocusFromPointer(
        Microsoft.UI.Input.PointerPoint point,
        Microsoft.UI.Input.PointerDeviceType pointerDeviceType)
    {
        if (IsModifierKeyPressed())
        {
            return false;
        }
        return pointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse
            ? point.Properties.IsLeftButtonPressed
            : point.IsInContact;
    }

    private void FocusFromPointer()
    {
        _usePointerFocusVisual = true;
        if (!Focus(FocusState.Pointer) &&
            !Focus(FocusState.Programmatic))
        {
            _usePointerFocusVisual = false;
        }
        UpdateFocusVisualState();
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

        CancelPendingViewChange();
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

    private static bool IsArrowKey(VirtualKey key) =>
        key is VirtualKey.Left or VirtualKey.Right or
            VirtualKey.Up or VirtualKey.Down;

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
        int heading = GetModifiedNavigationDirection(
            VirtualKey.Left,
            VirtualKey.Right);
        int pitch = GetModifiedNavigationDirection(
            VirtualKey.Down,
            VirtualKey.Up);
        long startTimestamp = _navigationKeys.Values
            .Concat(_modifiedNavigationKeys.Values)
            .DefaultIfEmpty()
            .Min();
        if (!_runtimeResourcesReleased)
        {
            _renderer.SetKeyboardNavigation(new KeyboardNavigationState(
                horizontal,
                vertical,
                zoom,
                heading,
                pitch,
                startTimestamp));
        }
    }

    private void CancelKeyboardNavigation()
    {
        if (_navigationKeys.Count == 0 &&
            _modifiedNavigationKeys.Count == 0)
        {
            return;
        }

        _navigationKeys.Clear();
        _modifiedNavigationKeys.Clear();
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

    private int GetModifiedNavigationDirection(
        VirtualKey negative,
        VirtualKey positive) =>
        (_modifiedNavigationKeys.ContainsKey(positive) ? 1 : 0) -
        (_modifiedNavigationKeys.ContainsKey(negative) ? 1 : 0);

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

        const double distance = 100;
        PanByPixels(-horizontal * distance, -vertical * distance);
    }

    private void ApplyModifiedArrowNavigation(VirtualKey key)
    {
        CancelPendingViewChange();
        switch (key)
        {
            case VirtualKey.Left:
                Heading -= 15;
                break;
            case VirtualKey.Right:
                Heading += 15;
                break;
            case VirtualKey.Up:
                Pitch += 10;
                break;
            case VirtualKey.Down:
                Pitch -= 10;
                break;
        }
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
        CancelPendingViewChange();
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
        return IsShiftPressed() ||
            IsControlPressed() ||
            IsMenuPressed() ||
            IsWindowsKeyPressed();
    }

    private static bool IsShiftPressed() =>
        IsVirtualKeyPressed(VirtualKey.LeftShift) ||
        IsVirtualKeyPressed(VirtualKey.RightShift);

    private static bool IsControlPressed() =>
        IsVirtualKeyPressed(VirtualKey.LeftControl) ||
        IsVirtualKeyPressed(VirtualKey.RightControl);

    private static bool IsMenuPressed() =>
        IsVirtualKeyPressed(VirtualKey.LeftMenu) ||
        IsVirtualKeyPressed(VirtualKey.RightMenu);

    private static bool IsWindowsKeyPressed() =>
        IsVirtualKeyPressed(VirtualKey.LeftWindows) ||
        IsVirtualKeyPressed(VirtualKey.RightWindows);

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
