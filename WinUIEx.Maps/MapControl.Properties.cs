using Microsoft.UI.Xaml;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Rendering.Diagnostics;
using Windows.Devices.Geolocation;

namespace WinUIEx.Maps;

public sealed partial class MapControl
{
    /// <summary>
    /// Gets or sets the geographic location at the center of the viewport.
    /// </summary>
    /// <value>
    /// The camera center, or <see langword="null"/> to use zero degrees latitude and
    /// longitude. The default is <see langword="null"/>.
    /// </value>
    /// <remarks>
    /// Camera calculations wrap longitude and clamp latitude to the Web Mercator range.
    /// Pointer and manipulation input replace this value with a new <see cref="Geopoint"/>.
    /// Get or set this dependency property only on the UI thread.
    /// </remarks>
    public Geopoint? Center
    {
        get => (Geopoint?)GetValue(CenterProperty);
        set => SetValue(CenterProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="Center"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty CenterProperty =
        DependencyProperty.Register(
            nameof(Center),
            typeof(Geopoint),
            typeof(MapControl),
            new PropertyMetadata(null, OnCenterPropertyChanged));

    private static void OnCenterPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        MapControl control = (MapControl)dependencyObject;
        if (!control._suppressCameraUpdate)
        {
            control.UpdateCameraTarget();
        }
    }

    /// <summary>
    /// Gets or sets the directional heading of the map in degrees.
    /// </summary>
    /// <value>
    /// The clockwise heading, where 0 or 360 is north, 90 is east, 180 is south, and 270 is
    /// west. The default is 0.
    /// </value>
    /// <remarks>
    /// Values are normalized to the range from 0 inclusive to 360 exclusive. A non-finite
    /// value is normalized to 0. Touch rotation updates this property directly after its
    /// activation threshold is crossed. This follows the directional convention of
    /// <see href="https://learn.microsoft.com/uwp/api/windows.ui.xaml.controls.maps.mapcontrol.heading">
    /// UWP MapControl.Heading</see>.
    /// </remarks>
    public double Heading
    {
        get => (double)GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="Heading"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty HeadingProperty =
        DependencyProperty.Register(
            nameof(Heading),
            typeof(double),
            typeof(MapControl),
            new PropertyMetadata(0d, OnHeadingPropertyChanged));

    private static void OnHeadingPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        MapControl control = (MapControl)dependencyObject;
        if (control._isNormalizingHeading)
        {
            return;
        }

        double heading = MapCamera.NormalizeHeading((double)args.NewValue);
        if (heading != (double)args.NewValue)
        {
            control._isNormalizingHeading = true;
            try
            {
                control.SetValue(HeadingProperty, heading);
            }
            finally
            {
                control._isNormalizingHeading = false;
            }
        }

        if (!control._suppressCameraUpdate)
        {
            control.UpdateCameraTarget();
        }
    }

    /// <summary>
    /// Gets or sets the collection of independently manipulable map layers.
    /// </summary>
    /// <remarks>
    /// The first layer is rendered bottom-most. Each control starts with its own empty
    /// collection. Assign or mutate this collection only on the control's UI thread.
    /// </remarks>
    public MapLayerCollection Layers
    {
        get => (MapLayerCollection)GetValue(LayersProperty);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            EnsureUiThread();
            SetValue(LayersProperty, value);
        }
    }

    /// <summary>
    /// Identifies the <see cref="Layers"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty LayersProperty =
        DependencyProperty.Register(
            nameof(Layers),
            typeof(MapLayerCollection),
            typeof(MapControl),
            new PropertyMetadata(null, OnLayersPropertyChanged));

    private static void OnLayersPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        MapControl control = (MapControl)dependencyObject;
        if (control._isRestoringLayers)
        {
            return;
        }

        if (args.NewValue is not MapLayerCollection newLayers)
        {
            control.RestoreLayersProperty();
            return;
        }
        if (ReferenceEquals(control._layers, newLayers))
        {
            return;
        }

        if (control._layers is not null &&
            control._lifecycleSubscriptionsAttached)
        {
            control._layers.Changing -= control.OnCollectionChanging;
            control._layers.CollectionChanged -= control.OnLayersChanged;
            control.DetachAllLayers();
        }

        control._layers = newLayers;
        if (control._lifecycleSubscriptionsAttached)
        {
            newLayers.Changing += control.OnCollectionChanging;
            newLayers.CollectionChanged += control.OnLayersChanged;
            control.AttachAllLayers();
        }
        control.MarkMapElementSnapshotDirty();
        control.PublishLayerSnapshots();
        control.UpdateAttribution();
        control.TraceLayersChanged("CollectionReplaced");
    }

    /// <summary>
    /// Gets or sets the Azure Maps subscription key used by the hidden base-map layer.
    /// </summary>
    /// <value>
    /// An Azure Maps Primary Key or Secondary Key, or an empty string when no key is
    /// configured. The default is an empty string.
    /// </value>
    /// <remarks>
    /// This property implements Azure Maps shared-key authentication and is used only for
    /// nonblank Azure base-map styles. Obtain a key from the <c>Authentication</c> page of an
    /// Azure Maps account in the Azure portal; see the
    /// <see href="https://learn.microsoft.com/azure/azure-maps/how-to-manage-authentication">
    /// Azure Maps authentication documentation</see>. Load keys from secure configuration or
    /// an environment variable instead of hardcoding or committing them. Public layers remain
    /// available without a key: use <see cref="MapStyle.Blank"/> with a custom
    /// <see cref="TileLayer"/> when no Azure base map is wanted. Get or set this dependency
    /// property only on the UI thread.
    /// </remarks>
    public string MapServiceToken
    {
        get => (string)GetValue(MapServiceTokenProperty);
        set => SetValue(MapServiceTokenProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="MapServiceToken"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty MapServiceTokenProperty =
        DependencyProperty.Register(
            nameof(MapServiceToken),
            typeof(string),
            typeof(MapControl),
            new PropertyMetadata(string.Empty, OnMapServiceTokenChanged));

    private static void OnMapServiceTokenChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        MapControl control = (MapControl)dependencyObject;
        control.ReplaceAzureTileLayer();
        control.UpdateAzureAuthenticationInfoBar();
        control.PublishLayerSnapshots();
        if (string.IsNullOrWhiteSpace(control.MapServiceToken) &&
            control.MapStyle != MapStyle.Blank &&
            control.IsLoaded)
        {
            MapControlEventSource.Log.ControlFailure(
                "MapServiceToken.Missing",
                nameof(InvalidOperationException),
                0);
        }
    }

    /// <summary>
    /// Gets or sets the Azure base-map style rendered behind the public layers.
    /// </summary>
    /// <value>The selected style. The default is <see cref="MapStyle.Road"/>.</value>
    /// <remarks>
    /// Nonblank styles use an implementation-owned Azure layer and require
    /// <see cref="MapServiceToken"/>. <see cref="MapStyle.Blank"/> removes that hidden layer
    /// without changing the identity or indexes of entries in <see cref="Layers"/>. Azure
    /// tile requests use this control's <see cref="FrameworkElement.Language"/> value as an
    /// IETF language tag when it is explicitly set on the map. When it is not set, Azure's
    /// default language is requested. Azure applies its own fallback when localized data is
    /// unavailable. Get or set this dependency property only on the UI thread.
    /// </remarks>
    public MapStyle MapStyle
    {
        get => (MapStyle)GetValue(MapStyleProperty);
        set => SetValue(MapStyleProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="MapStyle"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty MapStyleProperty =
        DependencyProperty.Register(
            nameof(MapStyle),
            typeof(MapStyle),
            typeof(MapControl),
            new PropertyMetadata(MapStyle.Road, OnMapStyleChanged));

    private static void OnMapStyleChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        MapControl control = (MapControl)dependencyObject;
        control.ReplaceAzureTileLayer();
        control.UpdateAzureAuthenticationInfoBar();
        control.PublishLayerSnapshots();
        RequestResourceCollection();
    }

    /// <summary>
    /// Gets or sets the camera tilt in degrees.
    /// </summary>
    /// <value>
    /// The tilt from straight down, from 0 through 60 degrees. The default is 0.
    /// </value>
    /// <remarks>
    /// Values outside the supported range are clamped. A non-finite value is normalized to
    /// 0. Get or set this dependency property only on the UI thread.
    /// </remarks>
    public double Pitch
    {
        get => (double)GetValue(PitchProperty);
        set => SetValue(PitchProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="Pitch"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty PitchProperty =
        DependencyProperty.Register(
            nameof(Pitch),
            typeof(double),
            typeof(MapControl),
            new PropertyMetadata(0d, OnPitchPropertyChanged));

    private static void OnPitchPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        MapControl control = (MapControl)dependencyObject;
        if (control._isNormalizingPitch)
        {
            return;
        }

        double pitch = MapCamera.NormalizePitch((double)args.NewValue);
        if (pitch != (double)args.NewValue)
        {
            control._isNormalizingPitch = true;
            try
            {
                control.SetValue(PitchProperty, pitch);
            }
            finally
            {
                control._isNormalizingPitch = false;
            }
        }

        if (!control._suppressCameraUpdate)
        {
            control.UpdateCameraTarget();
        }
    }

    /// <summary>
    /// Gets or sets the target Web Mercator zoom level of the camera.
    /// </summary>
    /// <value>
    /// The target zoom, where zero displays the full world at the equator. The default is 0.
    /// </value>
    /// <remarks>
    /// The displayed camera normalizes finite values to the supported range from 0 through 22
    /// and treats a non-finite value as 0. Pointer and manipulation input update this property.
    /// Get or set it only on the UI thread.
    /// </remarks>
    public double ZoomLevel
    {
        get => (double)GetValue(ZoomLevelProperty);
        set => SetValue(ZoomLevelProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="ZoomLevel"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty ZoomLevelProperty =
        DependencyProperty.Register(
            nameof(ZoomLevel),
            typeof(double),
            typeof(MapControl),
            new PropertyMetadata(0d, OnZoomLevelPropertyChanged));

    private static void OnZoomLevelPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        MapControl control = (MapControl)dependencyObject;
        if (!control._suppressCameraUpdate)
        {
            control.UpdateCameraTarget();
        }
    }
}
