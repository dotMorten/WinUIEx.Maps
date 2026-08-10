namespace WinUIEx.Maps;

/// <summary>
/// Provides the lightweight base for geographic content hosted by a
/// <see cref="MapElementsLayer"/>.
/// </summary>
/// <remarks>
/// This type deliberately does not derive from a WinUI dependency object, which keeps large
/// element sets inexpensive. The built-in renderer recognizes <see cref="MapIcon"/>,
/// <see cref="MapPolygon"/>, and <see cref="MapPolyline"/>; deriving another element type
/// does not by itself establish a custom drawing contract.
/// Built-in element properties publish immutable snapshots and may be changed from a worker
/// thread. Layer and element collection mutations remain UI-thread-only while attached to a
/// <see cref="MapControl"/>.
/// </remarks>
public abstract class MapElement
{
    private MapElementState _state = new(true, true, 0);

    internal event EventHandler? Changed;

    /// <summary>
    /// Gets or sets whether this element participates in pointer and tap input. The default
    /// is <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// A disabled element remains visible but is skipped during hit testing, allowing input
    /// to target an enabled element beneath it.
    /// </remarks>
    public bool IsEnabled
    {
        get => Volatile.Read(ref _state).IsEnabled;
        set => UpdateState(
            state => state.IsEnabled == value
                ? state
                : state with { IsEnabled = value });
    }

    /// <summary>
    /// Gets or sets whether this element is rendered. The default is
    /// <see langword="true"/>.
    /// </summary>
    public bool IsVisible
    {
        get => Volatile.Read(ref _state).IsVisible;
        set => UpdateState(
            state => state.IsVisible == value
                ? state
                : state with { IsVisible = value });
    }

    /// <summary>
    /// Gets or sets the element's draw order within its containing
    /// <see cref="MapElementsLayer"/>. The default is 0.
    /// </summary>
    /// <remarks>
    /// Elements with larger values render above elements with smaller values. Elements with
    /// equal values retain their order in <see cref="MapElementsLayer.MapElements"/>.
    /// This value never changes ordering between separate layers.
    /// </remarks>
    public int ZIndex
    {
        get => Volatile.Read(ref _state).ZIndex;
        set => UpdateState(
            state => state.ZIndex == value
                ? state
                : state with { ZIndex = value });
    }

    internal MapElementState GetBaseState() => Volatile.Read(ref _state);

    /// <summary>
    /// Notifies attached map controls that atomically published element state has changed.
    /// </summary>
    /// <remarks>
    /// Built-in derived types may call this from a worker thread only when their state
    /// publication is thread-safe. The control coalesces the notification onto its UI
    /// thread.
    /// </remarks>
    private protected void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void UpdateState(Func<MapElementState, MapElementState> update)
    {
        while (true)
        {
            MapElementState current = Volatile.Read(ref _state);
            MapElementState replacement = update(current);
            if (ReferenceEquals(current, replacement))
            {
                return;
            }
            if (ReferenceEquals(
                Interlocked.CompareExchange(ref _state, replacement, current),
                current))
            {
                OnChanged();
                return;
            }
        }
    }
}

internal sealed record MapElementState(
    bool IsEnabled,
    bool IsVisible,
    int ZIndex);
