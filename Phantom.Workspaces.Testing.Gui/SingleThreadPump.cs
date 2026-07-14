using System.Collections.Concurrent;

namespace Phantom.Workspaces.Testing.Gui;

/// <summary>
/// A single-threaded message pump with a SynchronizationContext for tests that need
/// serialized access to UI-affine data structures.
/// </summary>
public sealed class SingleThreadPump : IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = [];
    private readonly Thread thread;

    public SingleThreadPump(bool installSynchronizationContext)
    {
        this.Context = new PumpSynchronizationContext(this.queue);
        this.thread = new Thread(() =>
        {
            if (installSynchronizationContext)
            {
                SynchronizationContext.SetSynchronizationContext(this.Context);
            }

            foreach (var (callback, state) in this.queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        })
        {
            IsBackground = true,
            Name = "test-foreground-context-pump",
        };
        this.thread.Start();
    }

    public SynchronizationContext Context { get; }

    public int ThreadId => this.thread.ManagedThreadId;

    /// <summary>
    /// Posts <paramref name="work"/> to the pump thread and completes when the task
    /// returned by <paramref name="work"/> completes.
    /// </summary>
    public Task<T> PostAsync<T>(Func<Task<T>> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.Context.Post(
            _ => work().ContinueWith(
                task =>
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        completion.SetResult(task.Result);
                    }
                    else if (task.IsFaulted)
                    {
                        completion.SetException(
                            task.Exception!.InnerExceptions.Count == 1
                                ? task.Exception.InnerException!
                                : task.Exception);
                    }
                    else
                    {
                        completion.SetCanceled();
                    }
                },
                TaskScheduler.Default),
            null);
        return completion.Task;
    }

    public void Dispose() => this.queue.CompleteAdding();

    private sealed class PumpSynchronizationContext(
        BlockingCollection<(SendOrPostCallback Callback, object? State)> queue) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new NotSupportedException("Synchronous Send is not supported by the test pump.");
    }
}
