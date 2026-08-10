using Microsoft.UI.Xaml;

namespace WinUIEx.Maps;

/// <summary>
/// Represents independently manipulable data displayed by a <see cref="MapControl"/>.
/// </summary>
/// <remarks>
/// Layers and their collections must be created, assigned, and mutated on the owning
/// <see cref="MapControl"/>'s UI thread. The built-in renderer recognizes
/// <see cref="TileLayer"/> and <see cref="MapElementsLayer"/>; deriving directly from this
/// class does not by itself establish a custom rendering contract.
/// </remarks>
public class MapLayer : DependencyObject
{
    private bool _isRestoringValue;
    private bool _isVisible = true;
    private double _opacity = 1;
    private string _attribution = string.Empty;
    private Uri? _attributionLink;
    private long _revision;

    /// <summary>
    /// Gets or sets whether this layer participates in rendering and tile acquisition.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="true"/>. Hiding a raster layer cancels or avoids
    /// network work that is no longer needed. Read and write this dependency property only on
    /// the owning UI thread.
    /// </remarks>
    public bool IsVisible
    {
        get => (bool)GetValue(IsVisibleProperty);
        set => SetValue(IsVisibleProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="IsVisible"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty IsVisibleProperty =
        DependencyProperty.Register(
            nameof(IsVisible),
            typeof(bool),
            typeof(MapLayer),
            new PropertyMetadata(true, OnLayerPropertyChanged));

    /// <summary>
    /// Gets or sets the layer opacity, from 0 (transparent) through 1 (opaque).
    /// </summary>
    /// <value>The default is 1.</value>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The assigned value is not finite or is outside the inclusive range [0, 1].
    /// </exception>
    /// <remarks>
    /// The value multiplies the opacity of every item rendered by this layer. Values
    /// supplied through the dependency-property system that are invalid are rejected
    /// by restoring the last valid value. An opacity of zero also suppresses raster tile
    /// acquisition. Read and write this dependency property only on the owning UI thread.
    /// </remarks>
    public double Opacity
    {
        get => (double)GetValue(OpacityProperty);
        set
        {
            ValidateOpacity(value, nameof(value));
            SetValue(OpacityProperty, value);
        }
    }

    /// <summary>
    /// Identifies the <see cref="Opacity"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OpacityProperty =
        DependencyProperty.Register(
            nameof(Opacity),
            typeof(double),
            typeof(MapLayer),
            new PropertyMetadata(1d, OnLayerPropertyChanged));

    /// <summary>
    /// Gets or sets the attribution text displayed by the owning map.
    /// </summary>
    /// <value>An empty string by default.</value>
    /// <remarks>
    /// Visible layers with non-empty attribution are displayed in layer order in the map's
    /// attribution area. Set <see cref="AttributionLink"/> to make this text a hyperlink.
    /// Read and write this dependency property only on the owning UI thread.
    /// </remarks>
    public string Attribution
    {
        get => (string)GetValue(AttributionProperty);
        set => SetValue(AttributionProperty, value ?? string.Empty);
    }

    /// <summary>
    /// Identifies the <see cref="Attribution"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty AttributionProperty =
        DependencyProperty.Register(
            nameof(Attribution),
            typeof(string),
            typeof(MapLayer),
            new PropertyMetadata(string.Empty, OnLayerPropertyChanged));

    /// <summary>
    /// Gets or sets the optional destination opened by the attribution text.
    /// </summary>
    /// <value><see langword="null"/> by default.</value>
    /// <remarks>
    /// The link is used only when <see cref="Attribution"/> is non-empty. Read and write this
    /// dependency property only on the owning UI thread.
    /// </remarks>
    public Uri? AttributionLink
    {
        get => (Uri?)GetValue(AttributionLinkProperty);
        set => SetValue(AttributionLinkProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="AttributionLink"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty AttributionLinkProperty =
        DependencyProperty.Register(
            nameof(AttributionLink),
            typeof(Uri),
            typeof(MapLayer),
            new PropertyMetadata(null, OnLayerPropertyChanged));

    internal event EventHandler<MapLayerChangedEventArgs>? Changed;

    internal long Revision => _revision;

    internal void NotifyChanged(DependencyProperty property)
    {
        long revision = ++_revision;
        Changed?.Invoke(this, new MapLayerChangedEventArgs(property, revision));
    }

    private static void OnLayerPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        MapLayer layer = (MapLayer)dependencyObject;
        if (layer._isRestoringValue)
        {
            return;
        }

        object? validValue;
        if (args.Property == IsVisibleProperty && args.NewValue is bool isVisible)
        {
            layer._isVisible = isVisible;
            validValue = isVisible;
        }
        else if (args.Property == OpacityProperty &&
            args.NewValue is double opacity &&
            IsValidOpacity(opacity))
        {
            layer._opacity = opacity;
            validValue = opacity;
        }
        else if (args.Property == AttributionProperty &&
            args.NewValue is string attribution)
        {
            layer._attribution = attribution;
            validValue = attribution;
        }
        else if (args.Property == AttributionLinkProperty &&
            (args.NewValue is null || args.NewValue is Uri))
        {
            layer._attributionLink = (Uri?)args.NewValue;
            validValue = args.NewValue;
        }
        else
        {
            validValue = args.Property switch
            {
                _ when args.Property == IsVisibleProperty => layer._isVisible,
                _ when args.Property == OpacityProperty => layer._opacity,
                _ when args.Property == AttributionProperty => layer._attribution,
                _ => layer._attributionLink,
            };
            layer._isRestoringValue = true;
            try
            {
                layer.SetValue(args.Property, validValue);
            }
            finally
            {
                layer._isRestoringValue = false;
            }
            return;
        }

        layer.NotifyChanged(args.Property);
    }

    private static bool IsValidOpacity(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 1;

    private static void ValidateOpacity(double value, string parameterName)
    {
        if (!IsValidOpacity(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Opacity must be finite and in the inclusive range [0, 1].");
        }
    }
}

internal sealed class MapLayerChangedEventArgs(
    DependencyProperty property,
    long revision) : EventArgs
{
    internal DependencyProperty Property { get; } = property;

    internal long Revision { get; } = revision;
}
