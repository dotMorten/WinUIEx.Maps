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
    private readonly TouchPitchState _touchPitch = new();

    private void OnTouchContactPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            // XAML manipulation tracking does not capture routed pointer events.
            // Own the contact so release outside the map still retires it.
            _touchPitch.Press(e.Pointer.PointerId, CapturePointer(e.Pointer),
                e.GetCurrentPoint(this).Position);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            // Intermediate points are newest first. Pair only samples from the
            // same platform frame, never one new contact with one stale contact.
            var points = e.GetIntermediatePoints(this);
            for (int i = points.Count - 1; i >= 0; i--)
            {
                var point = points[i];
                _touchPitch.Move(point.PointerId, point.Position, point.FrameId);
            }
        }
    }

    private void OnTouchContactReleased(object sender, PointerRoutedEventArgs e)
    {
        _touchPitch.Release(e.Pointer.PointerId);
    }

    private void OnTouchContactCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch &&
            _touchPitch.Cancel(e.Pointer.PointerId))
        {
            _touchRotation.Reset();
        }
    }

    private void ResetTouchManipulation(bool preserveContactEvidence = false)
    {
        _touchRotation.Reset();
        _touchPitch.Reset(preserveContactEvidence);
    }

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
            _animationsEnabled);
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
        MapCenter target = !_runtimeResourcesReleased && _animationsEnabled
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
            if (!_runtimeResourcesReleased && !_animationsEnabled)
            {
                _renderer.SetCameraTargetImmediately(
                    target.Longitude,
                    target.Latitude,
                    targetZoom,
                    viewportWidth,
                    viewportHeight,
                    Heading,
                    Pitch);
            }
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
    protected override void OnManipulationStarting(
        ManipulationStartingRoutedEventArgs e)
    {
        base.OnManipulationStarting(e);
        if (!_animationsEnabled)
        {
            e.Mode &= ~ManipulationModes.TranslateInertia;
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
            // Started can arrive between the routed moves for one native frame.
            // Keep the pair baseline established by capture, not a half-updated pair.
            ResetTouchManipulation(preserveContactEvidence: true);
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

        if (e.IsInertial && !_animationsEnabled)
        {
            e.Complete();
            e.Handled = true;
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
        ResetTouchManipulation();
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
        if (TryApplyTouchPitch(e, out Point translation, out double scale))
        {
            return;
        }

        BasicGeoposition position = Center?.Position ?? new BasicGeoposition();
        double currentZoom = ZoomLevel;
        double currentHeading = Heading;
        MapCenter target = MapCamera.PanByPixels(
            position.Longitude,
            position.Latitude,
            currentZoom,
            translation.X,
            translation.Y,
            currentHeading,
            Pitch,
            _panel?.ActualHeight ?? ActualHeight);
        double targetZoom = currentZoom;
        if (MapCamera.TryGetZoomDeltaFromScale(
                scale,
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

    private bool TryApplyTouchPitch(
        ManipulationDeltaRoutedEventArgs e,
        out Point translation,
        out double scale)
    {
        if (!_touchPitch.TryGetPitchDelta(
                e.Delta.Translation, e.Delta.Scale, e.Delta.Expansion,
                e.Delta.Rotation, e.IsInertial,
                out translation, out scale, out double pitchDelta))
        {
            return false;
        }
        if (pitchDelta == 0)
        {
            return true;
        }

        _suppressCameraUpdate = true;
        try
        {
            // Up increases pitch, consistent with Shift+Up. XAML distances are DIPs.
            Pitch = MapCamera.NormalizePitch(Pitch + pitchDelta);
            if (!_runtimeResourcesReleased)
            {
                BasicGeoposition position = Center?.Position ?? new BasicGeoposition();
                _renderer.SetCameraTargetImmediately(
                    position.Longitude, position.Latitude, ZoomLevel,
                    _panel?.ActualWidth ?? ActualWidth,
                    _panel?.ActualHeight ?? ActualHeight, Heading, Pitch);
            }
        }
        finally
        {
            _suppressCameraUpdate = false;
        }
        return true;
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

// Kept independent of routed events so native contact ordering and classifier
// decisions can also be covered without an interactive desktop.
internal sealed class TouchPitchState
{
    private const double ActivationDistance = 16;
    private const double DegreesPerPixel = 0.25;
    private readonly HashSet<uint> _contacts = [];
    private readonly Dictionary<uint, Point> _starts = [];
    private readonly Dictionary<uint, Point> _current = [];
    private readonly Dictionary<uint, Dictionary<uint, Point>> _frames = [];
    private readonly Queue<uint> _frameOrder = [];
    private Point _firstMotion;
    private Point _secondMotion;
    private double _separationChange;
    private double _angleChange;
    private bool _hasPair;
    private uint? _lastPairedFrame;
    private Point _translation;
    private double _scale = 1;
    private bool _isPitch;
    private bool _isRejected;
    private bool _isCanceled;

    internal void Press(uint id, bool isCaptured = true, Point position = default)
    {
        // Only captured contacts have a guaranteed release/capture-lost route.
        if (isCaptured && _contacts.Add(id))
        {
            _current[id] = position;
            ResetContactEvidence();
            if (_contacts.Count > 2)
            {
                _isRejected = true;
            }
        }
    }

    internal void Move(uint id, Point position, uint frame)
    {
        if (!_contacts.Contains(id))
        {
            return;
        }
        _current[id] = position;
        // A late release can leave the previous normal-mode lock set before
        // the next ManipulationStarted. Still collect its new pair's evidence;
        // the manipulation callback, not raw moves, owns mode changes.
        if (_contacts.Count != 2 || _isPitch || _isCanceled)
        {
            return;
        }
        if (!_frames.TryGetValue(frame, out var samples))
        {
            // Bounded coalescing history; missing a matching sample delays
            // classification, it never manufactures parallel movement.
            if (_frames.Count == 16)
            {
                _frames.Remove(_frameOrder.Dequeue());
            }
            _frames[frame] = samples = [];
            _frameOrder.Enqueue(frame);
        }
        samples[id] = position;
        if (samples.Count != 2 ||
            (_lastPairedFrame is uint last && unchecked((int)(frame - last)) <= 0))
        {
            return;
        }
        _lastPairedFrame = frame;
        uint first = _contacts.First();
        uint second = _contacts.Last();
        Point a = samples[first], b = samples[second];
        Point startA = _starts[first], startB = _starts[second];
        _firstMotion = new(a.X - startA.X, a.Y - startA.Y);
        _secondMotion = new(b.X - startB.X, b.Y - startB.Y);
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double startDx = startB.X - startA.X, startDy = startB.Y - startA.Y;
        _separationChange = Math.Sqrt(dx * dx + dy * dy) -
            Math.Sqrt(startDx * startDx + startDy * startDy);
        _angleChange = MapCamera.ShortestHeadingDelta(
            Math.Atan2(startDy, startDx) * 180 / Math.PI,
            Math.Atan2(dy, dx) * 180 / Math.PI);
        _hasPair = true;
    }

    internal void Release(uint id)
    {
        if (_contacts.Remove(id))
        {
            _current.Remove(id);
            _isCanceled |= _isPitch;
            _isRejected = true;
            _frames.Clear();
            _frameOrder.Clear();
            _hasPair = false;
        }
    }

    internal bool Cancel(uint id)
    {
        if (!_contacts.Remove(id))
        {
            return false;
        }
        _current.Remove(id);
        ResetCandidate();
        _isCanceled = true;
        return true;
    }

    internal void Clear()
    {
        _contacts.Clear();
        _current.Clear();
        Reset();
    }

    internal void Reset(bool preserveContactEvidence = false)
    {
        _isPitch = false;
        _isRejected = false;
        _isCanceled = false;
        ResetCandidate(preserveContactEvidence);
    }

    private void ResetCandidate(bool preserveContactEvidence = false)
    {
        _translation = default;
        _scale = 1;
        if (preserveContactEvidence)
        {
            return;
        }
        ResetContactEvidence();
    }

    private void ResetContactEvidence()
    {
        _frames.Clear();
        _frameOrder.Clear();
        _hasPair = false;
        _lastPairedFrame = null;
        _starts.Clear();
        foreach (var contact in _current)
        {
            _starts[contact.Key] = contact.Value;
        }
    }

    internal bool TryGetPitchDelta(
        Point delta, double deltaScale, double expansion, double rotation, bool isInertial,
        out Point translation, out double scale, out double pitchDelta)
    {
        translation = delta;
        scale = deltaScale;
        pitchDelta = 0;
        if (_isCanceled || (_isPitch && (isInertial || _contacts.Count != 2)))
        {
            // Do not pan, zoom, or apply inertia after a pitch contact lifts.
            return true;
        }
        if (isInertial || _contacts.Count != 2 || _isRejected)
        {
            // A still-undecided pair may lift before reaching a threshold.
            // Flush its buffered normal motion rather than dropping it.
            translation = new(_translation.X + delta.X, _translation.Y + delta.Y);
            scale = _scale * deltaScale;
            _translation = default;
            _scale = 1;
            return false;
        }
        double verticalDelta = delta.Y;
        if (!_isPitch)
        {
            _translation = new Point(_translation.X + delta.X, _translation.Y + delta.Y);
            _scale *= deltaScale;
            if (!_hasPair)
            {
                return true;
            }
            _hasPair = false;
            double vertical = Math.Min(Math.Abs(_firstMotion.Y), Math.Abs(_secondMotion.Y));
            // Separation wins competing evidence, including a drifting midpoint.
            if (Math.Abs(_separationChange) > 6 ||
                Math.Abs(_angleChange) >= TouchRotationState.ActivationThreshold ||
                Math.Abs((_firstMotion.X + _secondMotion.X) / 2) > Math.Max(4, vertical / 2))
            {
                _isRejected = true;
                // Replay pending translation/scale when this proves to be pan/pinch.
                translation = _translation;
                scale = _scale;
                _translation = default;
                _scale = 1;
                return false;
            }
            double tolerance = Math.Max(4, vertical / 4);
            if (vertical <= ActivationDistance ||
                Math.Sign(_firstMotion.Y) != Math.Sign(_secondMotion.Y) ||
                Math.Abs(_firstMotion.X) > tolerance ||
                Math.Abs(_secondMotion.X) > tolerance ||
                Math.Abs(_firstMotion.X - _secondMotion.X) > tolerance ||
                Math.Abs(_firstMotion.Y - _secondMotion.Y) > tolerance)
            {
                return true;
            }
            _isPitch = true;
            // Classification establishes the origin: no deferred camera jump.
            verticalDelta = 0;
        }
        pitchDelta = -verticalDelta * DegreesPerPixel;
        return true;
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
