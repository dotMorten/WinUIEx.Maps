using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class NativeGeometryBufferTests
{
    [TestMethod]
    public void GrowthAndChunkBoundariesPreserveEveryVertex()
    {
        using MapRenderer.NativeGeometryBuffer buffer = new();
        const int count = 140_001;
        for (int index = 0; index < count; index++)
            buffer.Add(new MapScreenPoint(index, -index));

        Assert.AreEqual(count, buffer.Count);
        Assert.HasCount(3, buffer.Chunks);
        foreach (MapRenderer.GeometryBufferChunk chunk in buffer.Chunks)
        {
            Assert.AreEqual(0, chunk.Capacity % 3);
            Assert.IsTrue(chunk.Capacity <= 65_535);
        }
        MapScreenPoint[] points = buffer.ToArray();
        Assert.HasCount(count, points);
        for (int index = 0; index < count; index++)
            Assert.AreEqual(new MapScreenPoint(index, -index), points[index]);
    }

    [TestMethod]
    public void DisposeImmediatelyReleasesChunksAndRejectsReuse()
    {
        MapRenderer.NativeGeometryBuffer buffer = new();
        buffer.Add(new MapScreenPoint(1, 2));
        MapRenderer.GeometryBufferChunk chunk = buffer.Chunks[0];
        Assert.IsGreaterThan(0L, buffer.ByteSize);
        buffer.Dispose();
        Assert.AreEqual(0L, buffer.ByteSize);
        Assert.AreEqual(0L, chunk.ByteSize);
        Assert.AreEqual(0, buffer.Count);
        Assert.IsEmpty(buffer.Chunks);
        buffer.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => buffer.Add(default));
        Assert.ThrowsExactly<ObjectDisposedException>(() => buffer.ToArray());
    }

    [TestMethod]
    public void CancellationUnwindsAndReleasesNativeScratch()
    {
        MapRenderer.GeometryBufferChunk? chunk = null;
        Assert.ThrowsExactly<OperationCanceledException>(() =>
        {
            using MapRenderer.NativeGeometryBuffer buffer = new();
            for (int index = 0; index < 70_000; index++)
                buffer.Add(new MapScreenPoint(index, index));
            chunk = buffer.Chunks[0];
            new CancellationToken(canceled: true).ThrowIfCancellationRequested();
        });
        Assert.IsNotNull(chunk);
        Assert.AreEqual(0L, chunk.ByteSize);
        Assert.AreEqual(0, chunk.Count);
    }

    [TestMethod]
    public void LargeRepeatedBuildsDoNotAllocateManagedVertexArrays()
    {
        Build();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int run = 0; run < 5; run++)
            Build();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsLessThan(64_000L, allocated,
            "Repeated geometry builds should allocate only small ownership objects, not vertex arrays.");

        static void Build()
        {
            using MapRenderer.NativeGeometryBuffer buffer = new();
            for (int index = 0; index < 140_001; index++)
                buffer.Add(new MapScreenPoint(index, -index));
        }
    }
}
