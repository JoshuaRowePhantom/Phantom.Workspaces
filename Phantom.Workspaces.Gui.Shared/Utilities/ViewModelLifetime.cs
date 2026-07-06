using System;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Gui.Shared.Utilities;

/// <summary>
/// Manages the lifetime of background async work started by a ViewModel.
/// All running work is automatically cancelled and awaited when <see cref="DisposeAsync"/> is called.
/// </summary>
public sealed class ViewModelLifetime : IAsyncDisposable
{
    private readonly CancellationTokenSource cts = new();
    private readonly AsyncDisposableCollection tasks = new();

    public CancellationToken Token => cts.Token;

    /// <summary>
    /// Starts <paramref name="work"/> as a fire-and-forget task with automatic
    /// cancellation when this lifetime is disposed. <see cref="OperationCanceledException"/>
    /// is swallowed; all other exceptions remain unobserved.
    /// </summary>
    public void Run(Func<CancellationToken, Task> work)
    {
        tasks.Add(new TaskAdapter(RunCoreAsync(work)));
    }

    private async Task RunCoreAsync(Func<CancellationToken, Task> work)
    {
        try
        {
            await work(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on dispose — swallow silently.
        }
    }

    public ValueTask DisposeAsync()
    {
        cts.Cancel();
        return tasks.DisposeAsync();
    }

    private sealed class TaskAdapter(Task task) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new ValueTask(task);
    }
}
