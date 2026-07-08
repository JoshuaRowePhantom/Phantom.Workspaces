using System;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Gui.Shared.Utilities;

/// <summary>
/// Accepts <see cref="IAsyncDisposable"/> registrations at any time and disposes them all
/// when <see cref="DisposeAsync"/> is called. Registrations that arrive after disposal
/// begins are disposed immediately. <see cref="DisposeAsync"/> completes only after every
/// registered item has finished disposing.
/// </summary>
internal sealed class AsyncDisposableCollection : IAsyncDisposable
{
    // Starts at 1 — the "close" token held by DisposeAsync itself.
    private int _pending = 1;
    private readonly TaskCompletionSource _disposed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _allCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask DisposeAsync()
    {
        _disposed.TrySetResult();
        DecrementPending();
        return new ValueTask(_allCompleted.Task);
    }

    public void Add(IAsyncDisposable disposable)
    {
        Interlocked.Increment(ref _pending);
        _ = RunDisposalAsync(disposable);
    }

    private async Task RunDisposalAsync(IAsyncDisposable disposable)
    {
        await _disposed.Task.ConfigureAwait(false);
        try   { await disposable.DisposeAsync().ConfigureAwait(false); }
        finally { DecrementPending(); }
    }

    private void DecrementPending()
    {
        if (Interlocked.Decrement(ref _pending) == 0)
            _allCompleted.TrySetResult();
    }
}
