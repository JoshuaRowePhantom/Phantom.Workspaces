using System.Runtime.CompilerServices;
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
    public async Task RunDisposalAsync_DisposableThrows_ExceptionIsObservedAndSwallowed()
    {
        // Issue #1084: a faulting DisposeAsync must be observed (surfaced to the error callback) and
        // swallowed, and the collection's DisposeAsync must still complete and drain the pending count.
        Exception? observed = null;
        var collection = new AsyncDisposableCollection(ex => observed = ex);
        var good = new TrackingDisposable();

        collection.Add(new ThrowingDisposable());
        collection.Add(good);

        // Must complete (pending fully drains) despite one item throwing.
        await collection.DisposeAsync();

        Assert.NotNull(observed);
        Assert.IsType<InvalidOperationException>(observed);
        Assert.Equal(1, good.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_DisposableThrows_DoesNotProduceUnobservedTaskException()
    {
        // Issue #1084: the fire-and-forget disposal must not leave an unobserved Task exception that
        // the GC finalizer would later rethrow as an AggregateException and crash the process.
        var unobserved = new List<Exception>();
        void Handler(object? _, UnobservedTaskExceptionEventArgs e)
        {
            lock (unobserved)
            {
                unobserved.Add(e.Exception);
            }
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            await RunAndReleaseAsync();

            // Force finalization so any unobserved faulted Task would surface here.
            for (var i = 0; i < 5; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }

        lock (unobserved)
        {
            Assert.Empty(unobserved);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static async Task RunAndReleaseAsync()
        {
            var collection = new AsyncDisposableCollection();
            collection.Add(new ThrowingDisposable());
            await collection.DisposeAsync();
        }
    }
}
