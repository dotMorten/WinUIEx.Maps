using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using Windows.System;

namespace WinUIEx.Maps;

/// <summary>
/// Provides pointer input data for a <see cref="MapElement"/> hit in a
/// <see cref="MapElementsLayer"/>.
/// </summary>
public sealed class MapElementPointerEventArgs : EventArgs
{
    private readonly PointerRoutedEventArgs _args;

    internal MapElementPointerEventArgs(MapElement mapElement, PointerRoutedEventArgs args)
    {
        MapElement = mapElement;
        _args = args;
    }

    /// <summary>Gets the topmost map element under the pointer.</summary>
    public MapElement MapElement { get; }

    /// <summary>Gets the underlying WinUI pointer routed-event arguments.</summary>
    public PointerRoutedEventArgs OriginalEventArgs => _args;

    /// <summary>Gets the pointer associated with the event.</summary>
    public Pointer Pointer => _args.Pointer;

    /// <summary>Gets the keyboard modifiers active when the event occurred.</summary>
    public VirtualKeyModifiers KeyModifiers => _args.KeyModifiers;

    /// <summary>Gets or sets whether the underlying routed event is handled.</summary>
    public bool Handled
    {
        get => _args.Handled;
        set => _args.Handled = value;
    }

    /// <summary>Gets the pointer point relative to the specified element.</summary>
    public PointerPoint GetCurrentPoint(UIElement? relativeTo) =>
        _args.GetCurrentPoint(relativeTo);

    /// <summary>Gets intermediate pointer points relative to the specified element.</summary>
    public IList<PointerPoint> GetIntermediatePoints(UIElement? relativeTo) =>
        _args.GetIntermediatePoints(relativeTo);
}

/// <summary>
/// Provides tap input data for a <see cref="MapElement"/> hit in a
/// <see cref="MapElementsLayer"/>.
/// </summary>
public sealed class MapElementTappedEventArgs : EventArgs
{
    private readonly TappedRoutedEventArgs _args;

    internal MapElementTappedEventArgs(
        MapElement mapElement,
        Geopoint location,
        TappedRoutedEventArgs args)
    {
        MapElement = mapElement;
        Location = location;
        _args = args;
    }

    /// <summary>Gets the topmost map element at the tap position.</summary>
    public MapElement MapElement { get; }

    /// <summary>Gets the geographic location at the tap position.</summary>
    public Geopoint Location { get; }

    /// <summary>Gets the underlying WinUI tapped routed-event arguments.</summary>
    public TappedRoutedEventArgs OriginalEventArgs => _args;

    /// <summary>Gets the pointer device type that produced the tap.</summary>
    public PointerDeviceType PointerDeviceType => _args.PointerDeviceType;

    /// <summary>Gets or sets whether the underlying routed event is handled.</summary>
    public bool Handled
    {
        get => _args.Handled;
        set => _args.Handled = value;
    }

    /// <summary>Gets the tap position relative to the specified element.</summary>
    public Point GetPosition(UIElement? relativeTo) => _args.GetPosition(relativeTo);
}

/// <summary>
/// Provides right-tap input data for a <see cref="MapElement"/> hit in a
/// <see cref="MapElementsLayer"/>.
/// </summary>
public sealed class MapElementRightTappedEventArgs : EventArgs
{
    private readonly RightTappedRoutedEventArgs _args;

    internal MapElementRightTappedEventArgs(
        MapElement mapElement,
        Geopoint location,
        RightTappedRoutedEventArgs args)
    {
        MapElement = mapElement;
        Location = location;
        _args = args;
    }

    /// <summary>Gets the topmost map element at the right-tap position.</summary>
    public MapElement MapElement { get; }

    /// <summary>Gets the geographic location at the right-tap position.</summary>
    public Geopoint Location { get; }

    /// <summary>Gets the underlying WinUI right-tapped routed-event arguments.</summary>
    public RightTappedRoutedEventArgs OriginalEventArgs => _args;

    /// <summary>Gets the pointer device type that produced the right-tap.</summary>
    public PointerDeviceType PointerDeviceType => _args.PointerDeviceType;

    /// <summary>Gets or sets whether the underlying routed event is handled.</summary>
    public bool Handled
    {
        get => _args.Handled;
        set => _args.Handled = value;
    }

    /// <summary>Gets the right-tap position relative to the specified element.</summary>
    public Point GetPosition(UIElement? relativeTo) => _args.GetPosition(relativeTo);
}
