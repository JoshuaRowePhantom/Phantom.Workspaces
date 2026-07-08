using Phantom.Workspaces.Gui.Shared.Utilities;

namespace Phantom.Workspaces.Gui.Shared.Tests.Utilities;

public sealed class AsyncDisposableCollectionTests
{
    private sealed class TrackingDisposable : IAsyncDisposable
    {
        private readonly TaskCompletionSource _disposedSource = new();

        public int DisposeCount { get; private set; }
        public Task DisposedTask => _disposedSource.Task;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _disposedSource.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingDisposable(TaskCompletionSource gate) : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public async ValueTask DisposeAsync()
        {
            await gate.Task.ConfigureAwait(false);
            Disposed = true;
        }
    }

    private sealed class ThrowingDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => throw new InvalidOperationException("Test exception");
    }

    [Fact]
    public async Task DisposeAsync_NoItems_CompletesImmediately()
    {
        var collection = new AsyncDisposableCollection();
        var task = collection.DisposeAsync();
        Assert.True(task.IsCompleted);
        await task;
    }

    [Fact]
    public async Task DisposeAsync_SingleItemAddedBeforeDispose_ItemIsDisposed()
    {
        var collection = new AsyncDisposableCollection();
        var item = new TrackingDisposable();
        collection.Add(item);

        await collection.DisposeAsync();

        Assert.Equal(1, item.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_ItemAddedAfterDispose_ItemDisposedImmediately()
    {
        var collection = new AsyncDisposableCollection();
        await collection.DisposeAsync();

        var item = new TrackingDisposable();
        collection.Add(item);

        await item.DisposedTask.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, item.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_MultipleItemsAddedBeforeDispose_AllItemsDisposed()
    {
        var collection = new AsyncDisposableCollection();
        var items = Enumerable.Range(0, 5).Select(_ => new TrackingDisposable()).ToList();
        foreach (var item in items)
            collection.Add(item);

        await collection.DisposeAsync();

        foreach (var item in items)
            Assert.Equal(1, item.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_ItemAddedBeforeAndAfterDispose_AllItemsDisposed()
    {
        var collection = new AsyncDisposableCollection();
        var before = new TrackingDisposable();
        collection.Add(before);

        await collection.DisposeAsync();

        var after = new TrackingDisposable();
        collection.Add(after);

        await after.DisposedTask.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, before.DisposeCount);
        Assert.Equal(1, after.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_ItemThrows_OtherItemsStillDisposed()
    {
        var collection = new AsyncDisposableCollection();
        var good1 = new TrackingDisposable();
        var bad = new ThrowingDisposable();
        var good2 = new TrackingDisposable();

        collection.Add(good1);
        collection.Add(bad);
        collection.Add(good2);

        // DisposeAsync should complete (not hang) even though one item throws
        await collection.DisposeAsync();

        Assert.Equal(1, good1.DisposeCount);
        Assert.Equal(1, good2.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_AwaitsAllItemsBeforeCompleting()
    {
        var collection = new AsyncDisposableCollection();
        var gate = new TaskCompletionSource();
        var blocking = new BlockingDisposable(gate);
        collection.Add(blocking);

        var disposeTask = collection.DisposeAsync();
        await Task.Yield();

        // Not yet complete because blocking item is still waiting
        Assert.False(disposeTask.IsCompleted);
        Assert.False(blocking.Disposed);

        gate.SetResult();
        await disposeTask;

        Assert.True(blocking.Disposed);
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotDeadlock()
    {
        var collection = new AsyncDisposableCollection();
        var item = new TrackingDisposable();
        collection.Add(item);

        await collection.DisposeAsync();
        await collection.DisposeAsync(); // second call must not hang

        Assert.Equal(1, item.DisposeCount);
    }

    [Fact]
    public async Task Add_ConcurrentWithDispose_ItemIsEventuallyDisposed()
    {
        // Run many iterations to catch races
        for (var i = 0; i < 100; i++)
        {
            var collection = new AsyncDisposableCollection();
            var item = new TrackingDisposable();

            var addTask = Task.Run(() => collection.Add(item), TestContext.Current.CancellationToken);
            var disposeTask = Task.Run(() => collection.DisposeAsync().AsTask(), TestContext.Current.CancellationToken);

            await Task.WhenAll(addTask, disposeTask);

            await item.DisposedTask.WaitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, item.DisposeCount);
        }
    }
}
