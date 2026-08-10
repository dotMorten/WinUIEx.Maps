namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Identifies the renderer pipeline that consumes a layer-plan entry.
/// </summary>
internal enum LayerRenderKind
{
    MapElements,
    RasterTiles,
}

/// <summary>
/// Captures the render-order position, visibility, opacity, zoom range, fade settings, and
/// source geometry needed to draw one UI layer without reading the layer object.
/// </summary>
/// <remarks>
/// Instances are created on the UI thread and published as an ordered array to the render
/// thread. <see cref="LayerIndex"/> preserves public map-element ordering, while
/// <see cref="RuntimeId"/> joins raster plan entries to manager and renderer state.
/// </remarks>
internal readonly record struct LayerRenderSnapshot(
    LayerRenderKind Kind,
    int LayerIndex,
    long RuntimeId,
    bool IsVisible,
    double Opacity,
    TimeSpan FadeDuration,
    double MinZoom,
    double MaxZoom,
    int MinSourceZoom,
    int TileSize);

/// <summary>
/// Classifies built-in Azure and custom raster sources for acquisition behavior and
/// privacy-safe ETW routing.
/// </summary>
internal enum RasterSourceKind
{
    Azure,
    Custom,
}

/// <summary>
/// Accumulates UI-thread layer snapshots in visual order before publishing one render plan.
/// </summary>
/// <remarks>
/// The builder is UI-thread-confined. The resulting array is treated as immutable after it
/// is handed to <see cref="MapRenderer"/>.
/// </remarks>
internal sealed class LayerRenderPlanBuilder
{
    private readonly List<LayerRenderSnapshot> _items = [];

    /// <summary>
    /// Appends a layer snapshot while preserving the UI-defined render order.
    /// </summary>
    internal void Add(LayerRenderSnapshot item) => _items.Add(item);

    /// <summary>
    /// Materializes the accumulated render plan as a standalone publication array.
    /// </summary>
    internal LayerRenderSnapshot[] Build() => _items.ToArray();
}

/// <summary>
/// Publishes the synchronized pair of ordered render-plan entries and raster acquisition
/// snapshots derived from the same UI-layer state.
/// </summary>
/// <remarks>
/// <see cref="MapControl"/> sends <see cref="RenderPlan"/> to the renderer and
/// <see cref="RasterLayers"/> to <see cref="RasterTileManager"/>. A hidden Azure snapshot,
/// when present, is prepended to both arrays so acquisition identity and base-map render
/// ordering stay aligned.
/// </remarks>
internal readonly record struct LayerSnapshotPublication(
    LayerRenderSnapshot[] RenderPlan,
    TileLayerSnapshot[] RasterLayers)
{
    /// <summary>
    /// Inserts the control-owned Azure layer before public layers so the base map renders
    /// behind all user content.
    /// </summary>
    /// <remarks>
    /// When no Azure snapshot is present, the original publication arrays are reused.
    /// </remarks>
    internal static LayerSnapshotPublication PrependHiddenAzure(
        TileLayerSnapshot? azureSnapshot,
        LayerRenderSnapshot[] publicRenderPlan,
        TileLayerSnapshot[] publicRasterLayers)
    {
        if (azureSnapshot is null)
        {
            return new LayerSnapshotPublication(publicRenderPlan, publicRasterLayers);
        }

        TileLayerSnapshot snapshot = azureSnapshot;
        LayerRenderSnapshot hiddenRender = new(
            LayerRenderKind.RasterTiles,
            -1,
            snapshot.RuntimeId,
            snapshot.IsVisible,
            snapshot.Opacity,
            snapshot.FadeDuration,
            snapshot.MinZoom,
            snapshot.MaxZoom,
            snapshot.MinSourceZoom,
            snapshot.TileSize);
        return new LayerSnapshotPublication(
            [hiddenRender, .. publicRenderPlan],
            [snapshot, .. publicRasterLayers]);
    }
}

/// <summary>
/// Carries generation-tagged attribution text from a raster worker to UI-thread dispatch.
/// </summary>
/// <remarks>
/// <see cref="SourceId"/> and <see cref="Generation"/> allow stale updates to be rejected.
/// <see cref="Text"/> is display content and must not be written to ETW.
/// </remarks>
internal readonly record struct RasterAttributionUpdate(
    long SourceId,
    long Generation,
    string Text);

/// <summary>
/// Immutable, source-specific raster acquisition state captured on the UI thread.
/// </summary>
/// <remarks>
/// <para>
/// Instances cross to scheduler worker tasks. Every implementation must be immutable and
/// thread-safe. Acquisition and attribution methods may run concurrently on background
/// threads, must honor <see cref="CancellationToken"/> promptly, and must never read a
/// <see cref="TileLayer"/>, any dependency property, or another <c>DependencyObject</c>.
/// </para>
/// <para>
/// <see cref="SourceKey"/> defines pixel-producing identity across UI publications. Managers
/// compare it only in process to decide when generations, requests, and caches become stale.
/// Implementations return decoded CPU pixels; renderer reservation, upload backpressure,
/// device epochs, cache ownership, fading, and native disposal remain outside the session.
/// </para>
/// </remarks>
internal abstract class RasterTileAcquisitionSession
{
    /// <summary>
    /// Gets an immutable equality key containing every value that changes acquired pixels.
    /// </summary>
    /// <remarks>
    /// This value may contain private request configuration. It is for in-process equality
    /// only and must never be written to ETW, exceptions surfaced to callers, or diagnostics.
    /// </remarks>
    internal abstract object SourceKey { get; }

    internal abstract RasterSourceKind SourceKind { get; }

    internal abstract int TileSize { get; }

    internal abstract int MinSourceZoom { get; }

    internal abstract int MaxSourceZoom { get; }

    internal abstract bool CanAcquire { get; }

    internal virtual bool SupportsAttribution => false;

    internal virtual int TelemetryStyle => -1;

    /// <summary>
    /// Selects the source zoom for an immutable scene.
    /// </summary>
    /// <remarks>This method may be called from any scheduler thread.</remarks>
    internal abstract int GetSourceZoom(MapScene scene);

    /// <summary>
    /// Returns whether a source tile participates in this acquisition session.
    /// </summary>
    /// <remarks>This method may be called from any scheduler thread.</remarks>
    internal abstract bool IncludesTile(TileId id);

    /// <summary>
    /// Acquires and decodes one raster tile without accessing UI-thread state.
    /// </summary>
    /// <remarks>
    /// This method may be called concurrently on background threads. Implementations must
    /// bound response/decode work and observe <paramref name="cancellationToken"/> promptly.
    /// </remarks>
    internal abstract Task<DecodedRasterTile> GetTileAsync(
        TileId id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Acquires attribution for the source zoom, if this session supplies attribution.
    /// </summary>
    /// <remarks>
    /// This method may be called from a background thread and must not access layer or
    /// dependency-object state.
    /// </remarks>
    internal virtual Task<string?> GetAttributionAsync(
        int zoom,
        CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}

/// <summary>
/// Captures all UI-layer state required to schedule and render one raster source without
/// retaining the originating dependency object.
/// </summary>
/// <remarks>
/// Created on the UI thread and treated as immutable by manager workers. Revision describes
/// UI publication state, while the acquisition session's source key controls pixel cache and
/// generation invalidation. Visibility, opacity, display zoom range, and fade duration can
/// change without exposing mutable UI properties to background threads.
/// </remarks>
internal sealed record TileLayerSnapshot(
    long RuntimeId,
    long Revision,
    RasterTileAcquisitionSession Acquisition,
    double MinZoom,
    double MaxZoom,
    bool IsVisible,
    double Opacity,
    TimeSpan FadeDuration)
{
    internal object SourceKey => Acquisition.SourceKey;

    internal int MinSourceZoom => Acquisition.MinSourceZoom;

    internal int MaxSourceZoom => Acquisition.MaxSourceZoom;

    internal int TileSize => Acquisition.TileSize;
}
