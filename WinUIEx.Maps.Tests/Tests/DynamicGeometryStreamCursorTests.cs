using Microsoft.VisualStudio.TestTools.UnitTesting;
using static WinUIEx.Maps.Rendering.DirectXInterop;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class DynamicGeometryStreamCursorTests
{
    [TestMethod]
    public void InitialWriteDiscardsAndSubsequentWritesAppendThroughExactCapacity()
    {
        DynamicGeometryStreamCursor cursor = new(12);
        Assert.AreEqual(new DynamicGeometryStreamRange(0, true), cursor.Reserve(3));
        Assert.AreEqual(new DynamicGeometryStreamRange(3, false), cursor.Reserve(6));
        Assert.AreEqual(new DynamicGeometryStreamRange(9, false), cursor.Reserve(3));
        Assert.AreEqual(new DynamicGeometryStreamRange(0, true), cursor.Reserve(3));
        Assert.AreEqual(new DynamicGeometryStreamRange(3, false), cursor.Reserve(3));
    }

    [TestMethod]
    public void InsufficientTailSpaceWrapsBeforeWritingWithoutSplittingTriangles()
    {
        DynamicGeometryStreamCursor cursor = new(12);
        cursor.Reserve(9);
        Assert.AreEqual(new DynamicGeometryStreamRange(0, true), cursor.Reserve(6));
        Assert.AreEqual(new DynamicGeometryStreamRange(6, false), cursor.Reserve(3));
    }

    [TestMethod]
    public void FullCapacityWritesAlwaysDiscard()
    {
        DynamicGeometryStreamCursor cursor = new(65_535);
        Assert.AreEqual(new DynamicGeometryStreamRange(0, true), cursor.Reserve(65_535));
        Assert.AreEqual(new DynamicGeometryStreamRange(0, true), cursor.Reserve(65_535));
    }

    [TestMethod]
    public void ResetForcesDiscardAndIndependentBuffersRetainTheirOwnCursors()
    {
        DynamicGeometryStreamCursor geometry = new(12);
        DynamicGeometryStreamCursor pattern = new(12);
        geometry.Reserve(3);
        pattern.Reserve(6);
        geometry.Reset();
        Assert.AreEqual(new DynamicGeometryStreamRange(0, true), geometry.Reserve(3));
        Assert.AreEqual(new DynamicGeometryStreamRange(6, false), pattern.Reserve(3));
    }

    [TestMethod]
    [DataRow(-3)]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(4)]
    [DataRow(15)]
    [DataRow(int.MaxValue)]
    public void InvalidCountsDoNotAdvanceCursor(int count)
    {
        DynamicGeometryStreamCursor cursor = new(12);
        cursor.Reserve(3);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => cursor.Reserve(count));
        Assert.AreEqual(new DynamicGeometryStreamRange(3, false), cursor.Reserve(3));
    }

    [TestMethod]
    [DataRow(-3)]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(int.MaxValue)]
    public void InvalidCapacitiesAreRejected(int capacity) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DynamicGeometryStreamCursor(capacity));
}
