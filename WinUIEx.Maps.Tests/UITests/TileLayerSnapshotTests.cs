using Microsoft.UI.Xaml.Controls;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests.UITests;

[TestClass]
[DoNotParallelize]
public sealed class TileLayerSnapshotTests
{
    [TestMethod]
    public Task UnusedLayerDoesNotKeepItsRetiredProviderAlive() =>
        MapControlTestHost.LoadUIAsync(() => new Grid(), _ =>
        {
            TileLayer layer = new(new TileLayerOptions
            {
                TileUrl = "https://tiles.example/{z}/{x}/{y}",
                StyleUrl = "https://tiles.example/style.json",
            });
            WeakReference reference = CaptureAcquisition(layer);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.IsFalse(reference.IsAlive);
            Assert.IsNotNull(layer.CreateSnapshot().Acquisition);
            GC.KeepAlive(layer);
            return Task.CompletedTask;
        });

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CaptureAcquisition(TileLayer layer) =>
        new(layer.CreateSnapshot().Acquisition);

    [TestMethod]
    public Task DisplayChangesPreserveCustomVectorAcquisitionIdentity() =>
        MapControlTestHost.LoadUIAsync(() => new Grid(), _ =>
        {
            TileLayer layer = new(new TileLayerOptions
            {
                TileUrl = "https://tiles.example/{z}/{x}/{y}",
                StyleUrl = "https://tiles.example/style.json",
            });
            TileLayerSnapshot initial = layer.CreateSnapshot();
            layer.Opacity = 0.5;
            layer.IsVisible = false;
            layer.FadeDuration = TimeSpan.Zero;
            layer.MinZoom = 2;
            TileLayerSnapshot display = layer.CreateSnapshot();
            Assert.AreSame(initial.Acquisition, display.Acquisition);
            Assert.AreEqual(0.5, display.Opacity);
            Assert.IsFalse(display.IsVisible);
            Assert.AreEqual(2, display.MinZoom);

            layer.RequestHeaders = new Dictionary<string, string>();
            layer.Subdomains = Array.Empty<string>();
            Assert.AreSame(initial.Acquisition, layer.CreateSnapshot().Acquisition);
            layer.StyleUrl = "https://tiles.example/other-style.json";
            TileLayerSnapshot changed = layer.CreateSnapshot();
            Assert.AreNotSame(initial.Acquisition, changed.Acquisition);
            Assert.AreNotEqual(initial.SourceKey, changed.SourceKey);
            layer.TileSize = 256;
            Assert.AreNotSame(changed.Acquisition, layer.CreateSnapshot().Acquisition);
            return Task.CompletedTask;
        });
}
