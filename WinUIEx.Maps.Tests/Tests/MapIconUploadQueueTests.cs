using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class MapIconUploadQueueTests
{
    [TestMethod]
    public void NewMapElementPrecedesExistingVectorTextureBacklog()
    {
        MapIconUploadQueue queue = new();
        for (int index = 0; index < 1000; index++)
            queue.Enqueue(new(index, 1, [0, 0, 0, 255], 1, 1, IsMapElement: false));
        MapIconPixelData icon = new(1001, 1, [0, 0, 255, 255], 1, 1);
        queue.Enqueue(icon);
        Assert.IsTrue(queue.TryDequeue(out var first));
        Assert.AreSame(icon, first);
        Assert.AreEqual(0, queue.MapElementCount);
        Assert.AreEqual(1000, queue.VectorTextureCount);
        for (int index = 0; index < 1000; index++)
        {
            Assert.IsTrue(queue.TryDequeue(out var vector));
            Assert.AreEqual((long)index, vector.TextureId);
        }
        Assert.IsTrue(queue.IsEmpty);
        Assert.IsFalse(queue.TryDequeue(out _));
    }

    [TestMethod]
    public void SustainedMapElementsLeaveProgressForVectorTextures()
    {
        MapIconUploadQueue queue = new();
        MapIconPixelData vector = new(0, 1, [0, 0, 0, 255], 1, 1, IsMapElement: false);
        queue.Enqueue(vector);
        for (int index = 1; index <= 100; index++)
            queue.Enqueue(new(index, 1, [0, 0, 255, 255], 1, 1));
        for (int index = 1; index <= 8; index++)
        {
            Assert.IsTrue(queue.TryDequeue(out var icon));
            Assert.AreEqual((long)index, icon.TextureId);
        }
        Assert.IsTrue(queue.TryDequeue(out var next));
        Assert.AreSame(vector, next);
        Assert.IsTrue(queue.TryDequeue(out next));
        Assert.AreEqual(9L, next.TextureId);
    }

    [TestMethod]
    public void RetainedPixelsKeepTheirPriorityWhenRequeuedForDeviceRecreation()
    {
        MapIconPixelData vector = new(1, 1, [0, 0, 0, 255], 1, 1, IsMapElement: false);
        MapIconPixelData icon = new(2, 3, [0, 0, 255, 255], 1, 1);
        MapIconUploadQueue queue = new();
        queue.Enqueue(vector);
        queue.Enqueue(icon);
        Assert.IsTrue(queue.TryDequeue(out var retained));
        Assert.AreSame(icon, retained);
        queue.Enqueue(retained);
        Assert.IsTrue(queue.TryDequeue(out var recreated));
        Assert.AreSame(icon, recreated);
        Assert.IsTrue(queue.TryDequeue(out recreated));
        Assert.AreSame(vector, recreated);
    }
}
