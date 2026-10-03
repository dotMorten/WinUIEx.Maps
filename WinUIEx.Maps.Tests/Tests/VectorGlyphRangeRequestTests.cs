using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class VectorGlyphRangeRequestTests
{
    [TestMethod]
    public async Task WorldGlyphRangesWaitForCapacityAndCompleteWithoutRetry()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0, active = 0, peak = 0;
        VectorGlyphRangeRequests requests = new(async (key, token) =>
        {
            Assert.IsFalse(token.CanBeCanceled);
            Interlocked.Increment(ref started);
            int count = Interlocked.Increment(ref active);
            peak = Math.Max(peak, count);
            try
            {
                await release.Task;
                return EmptyRange(key);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        Task<VectorGlyphRange>[] pending = Enumerable.Range(0, 96)
            .Select(i => requests.GetAsync(new("Test", i * 256), CancellationToken.None)).ToArray();
        Assert.AreEqual(32, started);
        Assert.IsTrue(pending.All(task => !task.IsCompleted));
        var duplicate = requests.GetAsync(new("Test", 0), CancellationToken.None);
        Assert.AreEqual(32, started, "A duplicate must join its active load even at full capacity.");
        release.SetResult();
        var results = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(96, started);
        Assert.AreEqual(32, peak);
        Assert.AreEqual(0, active);
        Assert.AreSame(results[0], await duplicate);
        Assert.HasCount(96, results.Select(range => range.RangeStart).Distinct());
    }

    [TestMethod]
    public async Task CanceledCapacityWaiterDoesNotStartOrCancelSharedLoads()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        VectorGlyphRangeRequests requests = new(async (key, token) =>
        {
            Interlocked.Increment(ref started);
            await release.Task;
            return EmptyRange(key);
        });
        Task<VectorGlyphRange>[] pending = Enumerable.Range(0, 32)
            .Select(i => requests.GetAsync(new("Test", i * 256), CancellationToken.None)).ToArray();
        using CancellationTokenSource cancellation = new();
        var waiting = requests.GetAsync(new("Test", 32 * 256), cancellation.Token);
        var joined = requests.GetAsync(new("Test", 0), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
        await Assert.ThrowsAsync<OperationCanceledException>(() => joined);
        Assert.IsTrue(pending.All(task => !task.IsCompleted));
        release.SetResult();
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(32, started);
    }

    [TestMethod]
    public async Task FailedAndSynchronousLoadsReleaseTheirSlots()
    {
        int attempts = 0;
        VectorGlyphRangeRequests requests = new((key, _) =>
            ++attempts == 1
                ? Task.FromException<VectorGlyphRange>(new InvalidDataException("Test failure"))
                : Task.FromResult(EmptyRange(key)));
        VectorGlyphRangeKey key = new("Test", 0);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => requests.GetAsync(key, CancellationToken.None));
        for (int i = 0; i < 64; i++)
            Assert.AreEqual(0, (await requests.GetAsync(key, CancellationToken.None)).RangeStart);
        Assert.AreEqual(65, attempts, "Completed results must not be retained outside the atlas budget.");
    }

    [TestMethod]
    public async Task AsynchronousFailureWakesCapacityWaiters()
    {
        TaskCompletionSource<VectorGlyphRange>[] loads = Enumerable.Range(0, 33)
            .Select(_ => new TaskCompletionSource<VectorGlyphRange>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        TaskCompletionSource admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        VectorGlyphRangeRequests requests = new((key, _) =>
        {
            int index = key.RangeStart / 256;
            if (index == 32)
                admitted.SetResult();
            return loads[index].Task;
        });
        var pending = Enumerable.Range(0, 33)
            .Select(i => requests.GetAsync(new("Test", i * 256), CancellationToken.None)).ToArray();
        try
        {
            Assert.IsFalse(admitted.Task.IsCompleted);
            loads[0].SetException(new InvalidDataException("Test failure"));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => pending[0]);
            await admitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(pending[32].IsCompleted);
        }
        finally
        {
            for (int i = 1; i < loads.Length; i++)
                loads[i].TrySetResult(EmptyRange(new("Test", i * 256)));
        }
        await Task.WhenAll(pending.Skip(1)).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static VectorGlyphRange EmptyRange(VectorGlyphRangeKey key) =>
        new(key.FontStack, key.RangeStart, new Dictionary<int, VectorGlyph>());
}
