using Microsoft.UI.Xaml;
using WinUIEx.Maps.Rendering.Diagnostics;

namespace WinUIEx.Maps;

public sealed partial class MapControl
{
    /// <summary>Gets or sets whether user input can pan the map. Programmatic navigation is unaffected.</summary>
    public bool IsPanEnabled
    {
        get => (bool)GetValue(IsPanEnabledProperty);
        set => SetValue(IsPanEnabledProperty, value);
    }

    /// <summary>Identifies the <see cref="IsPanEnabled"/> dependency property.</summary>
    public static readonly DependencyProperty IsPanEnabledProperty =
        DependencyProperty.Register(nameof(IsPanEnabled), typeof(bool), typeof(MapControl),
            new PropertyMetadata(true, OnInteractionEnabledChanged));

    /// <summary>Gets or sets whether user input can zoom the map. Programmatic navigation is unaffected.</summary>
    public bool IsZoomEnabled
    {
        get => (bool)GetValue(IsZoomEnabledProperty);
        set => SetValue(IsZoomEnabledProperty, value);
    }

    /// <summary>Identifies the <see cref="IsZoomEnabled"/> dependency property.</summary>
    public static readonly DependencyProperty IsZoomEnabledProperty =
        DependencyProperty.Register(nameof(IsZoomEnabled), typeof(bool), typeof(MapControl),
            new PropertyMetadata(true, OnInteractionEnabledChanged));

    private static void OnInteractionEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (MapControl)sender;
        if (control._navigationKeys.Count != 0 || control._modifiedNavigationKeys.Count != 0)
            control.CommitKeyboardNavigation();
        control.CancelKeyboardNavigation();
        control.CancelDirectManipulations();
        MapControlEventSource.Log.InteractionEnabledChanged(control.IsPanEnabled, control.IsZoomEnabled);
    }
}
