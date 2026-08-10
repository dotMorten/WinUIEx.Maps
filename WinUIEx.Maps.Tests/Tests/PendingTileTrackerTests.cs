using Microsoft.VisualStudio.TestTools.UnitTesting;

using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class PendingTileTrackerTests
{
    [TestMethod]
    public void TileCanOnlyBeReservedOnce()
    {
        PendingTileTracker tracker = new();
        TileId id = new(12, 100, 200);
        RasterTileKey key = new(7, id);

        long reservation = tracker.TryReserve(key);
        Assert.AreNotEqual(0, reservation);
        Assert.IsTrue(tracker.Contains(key));
        Assert.AreEqual(0, tracker.TryReserve(key));
    }

    [TestMethod]
    public void StaleCompletionDoesNotReleaseNewGeneration()
    {
        PendingTileTracker tracker = new();
        TileId id = new(12, 100, 200);
        RasterTileKey key = new(7, id);
        long staleReservation = tracker.TryReserve(key);
        tracker.Clear();
        long currentReservation = tracker.TryReserve(key);

        tracker.Release(key, staleReservation);

        Assert.IsTrue(tracker.Contains(key));
        tracker.Release(key, currentReservation);
        Assert.IsFalse(tracker.Contains(key));
    }

    [TestMethod]
    public void SameCoordinateIsTrackedIndependentlyPerRasterSource()
    {
        PendingTileTracker tracker = new();
        TileId id = new(12, 100, 200);
        RasterTileKey first = new(1, id);
        RasterTileKey second = new(2, id);

        Assert.AreNotEqual(0, tracker.TryReserve(first));
        Assert.AreNotEqual(0, tracker.TryReserve(second));

        tracker.RemoveSource(1);

        Assert.IsFalse(tracker.Contains(first));
        Assert.IsTrue(tracker.Contains(second));
    }
}
