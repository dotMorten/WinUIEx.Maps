using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MapControl = WinUIEx.Maps.MapControl;

namespace MapSample.Samples.Performance;

public sealed partial class LifetimeStressPage : Page
{
    private CancellationTokenSource? _lifetimeCancellation;
    private int _iteration;

    public LifetimeStressPage()
    {
        InitializeComponent();
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
                MapControl map = PerformanceMapFactory.Create();
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
}
