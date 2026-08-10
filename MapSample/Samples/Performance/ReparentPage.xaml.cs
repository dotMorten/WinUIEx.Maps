using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Threading;
using System.Threading.Tasks;
using MapControl = WinUIEx.Maps.MapControl;

namespace MapSample.Samples.Performance;

public sealed partial class ReparentPage : Page
{
    private readonly MapControl _map = PerformanceMapFactory.Create();
    private CancellationTokenSource? _moveCancellation;
    private bool _isOnLeft = true;
    private int _moveCount;

    public ReparentPage()
    {
        InitializeComponent();
        AutomationProperties.SetAutomationId(_map, "MapSurface");
        LeftHost.Children.Add(_map);
        UpdateStatus();
    }

    private void MoveMap_Click(object sender, RoutedEventArgs e) => MoveMap();

    private void AutoMoveToggle_Checked(object sender, RoutedEventArgs e)
    {
        CancellationTokenSource cancellation = new();
        _moveCancellation = cancellation;
        _ = RunMovesAsync(cancellation);
    }

    private void AutoMoveToggle_Unchecked(object sender, RoutedEventArgs e) =>
        StopMoves();

    private async Task RunMovesAsync(CancellationTokenSource owner)
    {
        try
        {
            while (!owner.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), owner.Token);
                MoveMap();
            }
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_moveCancellation, owner))
            {
                _moveCancellation = null;
            }
            owner.Dispose();
        }
    }

    private void MoveMap()
    {
        Grid current = _isOnLeft ? LeftHost : RightHost;
        Grid destination = _isOnLeft ? RightHost : LeftHost;
        current.Children.Remove(_map);
        destination.Children.Add(_map);
        _isOnLeft = !_isOnLeft;
        _moveCount++;
        UpdateStatus();
    }

    private void StopMoves()
    {
        _moveCancellation?.Cancel();
        _moveCancellation = null;
    }

    private void UpdateStatus() =>
        MoveStatus.Text =
            $"{(_isOnLeft ? "Left" : "Right")} column; {_moveCount:N0} moves";

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        StopMoves();
        base.OnNavigatedFrom(e);
    }
}
