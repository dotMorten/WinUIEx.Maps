using MapSample.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Geolocation;
using System;
using WinUIEx.Maps;

namespace MapSample.Samples.Maps;

public sealed partial class MapComparisonPage : Page
{
    private bool _applyingWebCamera;
    private bool _webUpdateQueued;
    private bool _sendingWebCamera;
    private int _syncGeneration;
    private ComparisonCamera? _pendingWebCamera;
    private long[]? _cameraSubscriptions;
    private static readonly DependencyProperty[] CameraProperties =
    [
        WinUIEx.Maps.MapControl.CenterProperty,
        WinUIEx.Maps.MapControl.ZoomLevelProperty,
        WinUIEx.Maps.MapControl.HeadingProperty,
        WinUIEx.Maps.MapControl.PitchProperty,
    ];

    public MapComparisonPage()
    {
        InitializeComponent();
        WebMap.StatusChanged += (_, status) => WebCameraStatus.Text = status;
        Geopoint center = new(new BasicGeoposition
        {
            Longitude = -122.33,
            Latitude = 47.61,
        });
        NativeMap.Center = center;
        WebMap.Center = center;
        NativeMap.ZoomLevel = 10;
        // Azure's 512-pixel world at zoom 9 matches our 256-pixel world at zoom 10.
        WebMap.ZoomLevel = 9;
        WebMap.InitialViewFailed += (_, _) =>
        {
            SyncCameras.IsChecked = false;
            SyncCameras.IsEnabled = false;
            WebMapError.Title = "Unable to initialize the comparison view";
            WebMapError.Message = "The built-in map could not apply its starting camera.";
            WebMapError.IsOpen = true;
        };
        WebMap.InitialViewApplied += (_, _) =>
        {
            WebMapError.IsOpen = false;
            SyncCameras.IsEnabled = true;
        };
        WebMap.CameraChanged += WebMap_CameraChanged;
        WebMap.MapServiceErrorOccurred += (_, _) => WebMapError.IsOpen = true;
        string token = MapServiceTokenStore.Current;
        NativeMap.MapServiceToken = token;
        WebMap.MapServiceToken = token;
        GoToHomeButton.Visibility = string.IsNullOrWhiteSpace(token)
            ? Visibility.Visible
            : Visibility.Collapsed;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _cameraSubscriptions = new long[CameraProperties.Length];
        for (int i = 0; i < CameraProperties.Length; i++)
            _cameraSubscriptions[i] = NativeMap.RegisterPropertyChangedCallback(
                CameraProperties[i], NativeCameraChanged);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        SyncCameras.IsChecked = false;
        if (_cameraSubscriptions is not null)
        {
            for (int i = 0; i < CameraProperties.Length; i++)
                NativeMap.UnregisterPropertyChangedCallback(
                    CameraProperties[i], _cameraSubscriptions[i]);
            _cameraSubscriptions = null;
        }
    }

    private void SyncCameras_Changed(object sender, RoutedEventArgs e)
    {
        _syncGeneration++;
        _webUpdateQueued = false;
        _pendingWebCamera = null;
        if (SyncCameras.IsChecked == true)
            QueueWebCameraUpdate();
    }

    private void NativeCameraChanged(DependencyObject sender, DependencyProperty property)
    {
        if (!_applyingWebCamera && SyncCameras.IsChecked == true)
            QueueWebCameraUpdate();
    }

    private void QueueWebCameraUpdate()
    {
        if (_webUpdateQueued)
            return;
        _webUpdateQueued = true;
        int generation = _syncGeneration;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _syncGeneration)
                return;
            _webUpdateQueued = false;
            if (!IsLoaded || SyncCameras.IsChecked != true || NativeMap.Center is null)
                return;
            // The web control's zoom 0 is our zoom 1.
            if (NativeMap.ZoomLevel < 1)
                NativeMap.ZoomLevel = 1;
            var center = NativeMap.Center.Position;
            _pendingWebCamera = new ComparisonCamera(
                center.Longitude, center.Latitude, NativeMap.ZoomLevel,
                NativeMap.Heading, NativeMap.Pitch);
            SendWebCameraUpdates();
        }))
        {
            _webUpdateQueued = false;
            SyncCameras.IsChecked = false;
            WebMapError.Title = "Unable to synchronize cameras";
            WebMapError.Message = "The UI dispatcher is no longer accepting camera updates.";
            WebMapError.IsOpen = true;
        }
    }

    private async void SendWebCameraUpdates()
    {
        if (_sendingWebCamera)
            return;
        _sendingWebCamera = true;
        try
        {
            while (IsLoaded && SyncCameras.IsChecked == true &&
                _pendingWebCamera is ComparisonCamera camera)
            {
                _pendingWebCamera = null;
                if (!await WebMap.SetCameraAsync(camera))
                {
                    SyncCameras.IsChecked = false;
                    break;
                }
            }
        }
        finally
        {
            _sendingWebCamera = false;
        }
    }

    private async void WebMap_CameraChanged(object? sender, ComparisonCamera camera)
    {
        if (!IsLoaded || SyncCameras.IsChecked != true)
            return;
        if (NativeMap.Center is Geopoint nativeCenter &&
            camera.Matches(new ComparisonCamera(
                nativeCenter.Position.Longitude, nativeCenter.Position.Latitude,
                NativeMap.ZoomLevel, NativeMap.Heading, NativeMap.Pitch)))
            return;
        _syncGeneration++;
        _webUpdateQueued = false;
        _pendingWebCamera = null;
        System.Threading.Tasks.Task<bool> update;
        _applyingWebCamera = true;
        try
        {
            update = NativeMap.TrySetViewAsync(
                new Geopoint(new BasicGeoposition
                {
                    Longitude = camera.Longitude,
                    Latitude = camera.Latitude,
                }),
                Math.Clamp(camera.ZoomLevel, 1, 22),
                camera.Heading, camera.Pitch, MapAnimationKind.None);
        }
        finally
        {
            _applyingWebCamera = false;
        }
        if (camera.ZoomLevel is < 1 or > 22)
            QueueWebCameraUpdate();
        // A later camera update can legitimately supersede this one.
        await update;
    }

    private void GoToHome_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
            app.MainWindow?.NavigateHome();
    }
}
