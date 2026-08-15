using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using WinUIEx.Maps;
using MapControl = WinUIEx.Maps.MapControl;

namespace MapSample.Samples.Performance;

public sealed partial class LifetimeStressPage : Page
{
    private static readonly MapPickerItem[] MapOptions =
    [
        new("OpenStreetMap"),
        new(MapStyle.Road),
        new(MapStyle.GrayscaleDark),
        new(MapStyle.RoadShadedRelief),
        new(MapStyle.BlankAccessible),
        new(MapStyle.GrayscaleLight),
        new(MapStyle.Night),
        new(MapStyle.HighContrastDark),
        new(MapStyle.HighContrastLight),
        new(MapStyle.SatelliteWithRoads),
        new(MapStyle.RoadRaster),
        new(MapStyle.GrayscaleDarkRaster),
        new(MapStyle.Satellite),
        new(MapStyle.RoadShadedReliefRaster),
        new(MapStyle.Blank),
    ];

    private CancellationTokenSource? _lifetimeCancellation;
    private int _iteration;

    public LifetimeStressPage()
    {
        InitializeComponent();
        StylePicker.ItemsSource = MapOptions;
        StylePicker.SelectedIndex = 0;
    }

    private void LifetimeToggle_Checked(object sender, RoutedEventArgs e)
    {
        CancellationTokenSource cancellation = new();
        _lifetimeCancellation = cancellation;
        _ = RunLifetimeStressAsync(cancellation);
    }

    private void LifetimeToggle_Unchecked(object sender, RoutedEventArgs e) =>
        StopLifetimeStress();

    private async Task RunLifetimeStressAsync(CancellationTokenSource owner)
    {
        try
        {
            while (!owner.IsCancellationRequested)
            {
                MapHost.Children.Clear();
                MapControl map = CreateSelectedMap();
                AutomationProperties.SetAutomationId(map, "MapSurface");
                MapHost.Children.Add(map);
                _iteration++;
                UpdateMemoryStatus();
                await Task.Delay(TimeSpan.FromSeconds(1), owner.Token);
            }
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_lifetimeCancellation, owner))
            {
                _lifetimeCancellation = null;
            }
            owner.Dispose();
        }
    }

    private MapControl CreateSelectedMap()
    {
        MapPickerItem selection = StylePicker.SelectedItem as MapPickerItem ??
            MapOptions[0];
        MapControl map = PerformanceMapFactory.Create(
            selection.Style ?? MapStyle.Blank);
        if (selection.Style is null)
        {
            map.Layers.Insert(0, new TileLayer(
                new TileLayerOptions
                {
                    TileUrl = "https://tile.openstreetmap.org/[level]/[column]/[row].png",
                    TileSize = 256,
                    MaxSourceZoom = 19,
                },
                "openstreetmap-lifetime-stress")
            {
                Attribution = "© OpenStreetMap contributors",
                AttributionLink = new Uri("https://www.openstreetmap.org/copyright"),
            });
        }
        return map;
    }

    private void StopLifetimeStress()
    {
        _lifetimeCancellation?.Cancel();
        _lifetimeCancellation = null;
        MapHost.Children.Clear();
        UpdateMemoryStatus();
    }

    private void UpdateMemoryStatus()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        MemoryStatus.Text =
            $"Iteration: {_iteration:N0}; working set: {FormatBytes(process.WorkingSet64)}; " +
            $"private: {FormatBytes(process.PrivateMemorySize64)}; " +
            $"managed: {FormatBytes(GC.GetTotalMemory(false))}";
    }

    private static string FormatBytes(long bytes) =>
        $"{bytes / (1024d * 1024d):N1} MiB";

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        StopLifetimeStress();
        base.OnNavigatedFrom(e);
    }

    private sealed record MapPickerItem(string Name, MapStyle? Style = null)
    {
        internal MapPickerItem(MapStyle style)
            : this(style.ToString(), style)
        {
        }
    }
}
