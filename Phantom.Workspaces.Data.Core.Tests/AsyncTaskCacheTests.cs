namespace Phantom.Workspaces.Data.Tests;

public sealed class AsyncTaskCacheTests
{
    [Fact]
    public async Task GetOrFetchAsync_ConcurrentCallsForSameKey_InvokesFactoryExactlyOnce()
    {
        var cache = new AsyncTaskCache<string, int>();
        var factoryCallCount = 0;
        var tcs = new TaskCompletionSource<int>();

        const int workerCount = 8;
        using var startBarrier = new Barrier(workerCount);

        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(async () =>
            {
                startBarrier.SignalAndWait();
                return await cache.GetOrFetchAsync("key", async (_, _) =>
                {
                    Interlocked.Increment(ref factoryCallCount);
                    return await tcs.Task.ConfigureAwait(false);
                });
            }))
            .ToArray();

        tcs.SetResult(42);
        var results = await Task.WhenAll(workers);

        Assert.Equal(1, factoryCallCount);
        Assert.All(results, r => Assert.Equal(42, r));
    }

    [Fact]
    public async Task GetOrFetchAsync_WhenFactoryFails_EvictsKeyAndAllowsRetry()
    {
        var cache = new AsyncTaskCache<string, int>();
        var attempt = 0;

        var firstResult = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrFetchAsync("key", (_, _) =>
            {
                Interlocked.Increment(ref attempt);
                return Task.FromException<int>(new InvalidOperationException("fetch failed"));
            }));
        Assert.Equal("fetch failed", firstResult.Message);
        Assert.Equal(1, attempt);

        var secondResult = await cache.GetOrFetchAsync("key", (_, _) =>
        {
            Interlocked.Increment(ref attempt);
            return Task.FromResult(99);
        });
        Assert.Equal(99, secondResult);
        Assert.Equal(2, attempt);
    }

    [Fact]
    public async Task GetOrFetchAsync_WhenFactorySucceeds_SubsequentCallsReturnCachedTask()
    {
        var cache = new AsyncTaskCache<string, int>();
        var factoryCallCount = 0;

        var first = cache.GetOrFetchAsync("key", (_, _) =>
        {
            Interlocked.Increment(ref factoryCallCount);
            return Task.FromResult(7);
        });
        var second = cache.GetOrFetchAsync("key", (_, _) =>
        {
            Interlocked.Increment(ref factoryCallCount);
            return Task.FromResult(99);
        });

        Assert.Same(first, second);
        Assert.Equal(7, await first);
        Assert.Equal(1, factoryCallCount);
    }

    [Fact]
    public async Task GetOrFetchAsync_CancellationOfOneCaller_DoesNotCancelOtherCallers()
    {
        var cache = new AsyncTaskCache<string, int>();
        var tcs = new TaskCompletionSource<int>();

        using var cts = new CancellationTokenSource();

        var caller1Task = cache.GetOrFetchAsync("key", (_, _) => tcs.Task, cts.Token);
        var caller2Task = cache.GetOrFetchAsync("key", (_, _) => tcs.Task, CancellationToken.None);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller1Task);

        tcs.SetResult(55);
        var caller2Result = await caller2Task;
        Assert.Equal(55, caller2Result);
    }

    [Fact]
    public async Task GetOrFetchAsync_DifferentKeys_InvokeFactoryIndependently()
    {
        var cache = new AsyncTaskCache<string, int>();
        var factoryCallCount = 0;

        var result1 = await cache.GetOrFetchAsync("key1", (_, _) =>
        {
            Interlocked.Increment(ref factoryCallCount);
            return Task.FromResult(1);
        });
        var result2 = await cache.GetOrFetchAsync("key2", (_, _) =>
        {
            Interlocked.Increment(ref factoryCallCount);
            return Task.FromResult(2);
        });

        Assert.Equal(1, result1);
        Assert.Equal(2, result2);
        Assert.Equal(2, factoryCallCount);
    }
}
