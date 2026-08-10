using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;
using WinUIEx.Maps;
using MapIcon = WinUIEx.Maps.MapIcon;
using MapElementsLayer = WinUIEx.Maps.MapElementsLayer;

namespace MapSample.Samples.Performance;

public sealed partial class StressTestPage : Page
{
    private const int StressIconCount = 100_000;
    private const int StressUpdateCount = 2_000;
    private readonly MapElementsLayer _stressLayer = new();
    private CancellationTokenSource? _stressCancellation;
    private MapIcon[]? _stressIcons;

    public StressTestPage()
    {
        InitializeComponent();
        Map.Center = CreateLocation(0, 0);
        Map.ZoomLevel = 2;
        Map.Layers.Add(new TileLayer(
                   new TileLayerOptions
                   {
                       TileUrl = "https://tile.openstreetmap.org/[level]/[column]/[row].png",
                       TileSize = 256,
                       MaxSourceZoom = 19,
                   },
                   "openstreetmap-sample")
        {
            Attribution = "© OpenStreetMap contributors",
            AttributionLink = new Uri("https://www.openstreetmap.org/copyright"),
        });
    }

    private async void StressToggle_Checked(object sender, RoutedEventArgs e)
    {
        CancellationTokenSource cancellation = new();
        _stressCancellation = cancellation;
        StressStatus.Text = "Creating 100,000 icons...";
        IconElement[] iconElements = CreateStressIconElements();
        bool workerStarted = false;
        try
        {
            MapIcon[]? icons = await Task.Run(
                () => CreateStressIcons(iconElements, cancellation.Token));
            if (!ReferenceEquals(_stressCancellation, cancellation) ||
                cancellation.IsCancellationRequested ||
                icons is null)
            {
                return;
            }

            _stressIcons = icons;
            _stressLayer.MapElements.AddRange(icons);
            Map.Layers.Add(_stressLayer);
            StressStatus.Text =
                $"Running: {StressIconCount:N0} icons, {StressUpdateCount:N0} updates/cycle";
            _ = Task.Run(() => RunStressUpdates(
                icons,
                iconElements,
                cancellation,
                cancellation.Token));
            workerStarted = true;
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_stressCancellation, cancellation))
            {
                StressStatus.Text = $"Stress test failed: {exception.Message}";
                StressToggle.IsChecked = false;
            }
        }
        finally
        {
            if (!workerStarted)
            {
                cancellation.Dispose();
            }
        }
    }

    private void StressToggle_Unchecked(object sender, RoutedEventArgs e) =>
        StopStressTest(removeIcons: true);

    private void StopStressTest(bool removeIcons)
    {
        CancellationTokenSource? cancellation = _stressCancellation;
        _stressCancellation = null;
        cancellation?.Cancel();
        if (removeIcons)
        {
            Map.Layers.Remove(_stressLayer);
            _stressLayer.MapElements.Clear();
            StressStatus.Text = "Inactive";
        }
        _stressIcons = null;
    }

    private static IconElement[] CreateStressIconElements()
    {
        string[] glyphs =
        [
            "\uE707", "\uE80F", "\uE734", "\uE7C3", "\uE8EC",
            "\uE77B", "\uE81E", "\uE7B3", "\uE8B7", "\uE8D4",
        ];
        return glyphs.Select(glyph => (IconElement)new FontIcon
        {
            Glyph = glyph,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.Colors.Orange),
        }).ToArray();
    }

    private static MapIcon[]? CreateStressIcons(
        IconElement[] iconElements,
        CancellationToken cancellationToken)
    {
        MapIcon[] icons = new MapIcon[StressIconCount];
        for (int index = 0; index < icons.Length; index++)
        {
            if ((index & 1023) == 0 && cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            (double longitude, double latitude) = GetStressLocation(index, 0);
            icons[index] = new MapIcon(
                iconElements[index % iconElements.Length],
                CreateLocation(longitude, latitude));
        }
        return icons;
    }

    private void RunStressUpdates(
        MapIcon[] icons,
        IconElement[] iconElements,
        CancellationTokenSource owner,
        CancellationToken cancellationToken)
    {
        long cycle = 1;
        Random random = new();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                for (int offset = 0;
                    offset < StressUpdateCount && !cancellationToken.IsCancellationRequested;
                    offset++)
                {
                    int index = random.Next(icons.Length);
                    MapIcon icon = icons[index];
                    BasicGeoposition current = icon.Location.Position;
                    double longitude = current.Longitude + random.NextDouble() - 0.5;
                    longitude = ((longitude + 180) % 360 + 360) % 360 - 180;
                    double latitude = Math.Clamp(
                        current.Latitude + random.NextDouble() - 0.5,
                        -80,
                        80);
                    icon.Location = CreateLocation(longitude, latitude);
                    icon.IconElement =
                        iconElements[(index + (int)cycle) % iconElements.Length];
                }

                cycle++;
                if (cancellationToken.WaitHandle.WaitOne(16))
                {
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ReferenceEquals(_stressCancellation, owner))
                {
                    _stressCancellation = null;
                    StressStatus.Text = $"Stress test failed: {exception.Message}";
                    StressToggle.IsChecked = false;
                }
            });
        }
        finally
        {
            owner.Dispose();
        }
    }

    private static (double Longitude, double Latitude) GetStressLocation(
        int index,
        long cycle)
    {
        const int columns = 400;
        int column = index % columns;
        int row = index / columns;
        double phase = cycle * 0.075 + index * 0.013;
        double longitude = -179.5 + (column * (359.0 / (columns - 1))) +
            (Math.Sin(phase) * 0.15);
        double latitude = -80 + (row * (160.0 / 249.0)) +
            (Math.Cos(phase) * 0.1);
        return (longitude, Math.Clamp(latitude, -80, 80));
    }

    private static Geopoint CreateLocation(double longitude, double latitude) =>
        new(new BasicGeoposition
        {
            Longitude = longitude,
            Latitude = latitude,
        });

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        StopStressTest(removeIcons: true);
        base.OnNavigatedFrom(e);
    }
}
