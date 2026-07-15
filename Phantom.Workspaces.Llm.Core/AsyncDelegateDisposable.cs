using System;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Simple <see cref="IAsyncDisposable"/> wrapper around a <see cref="Func{ValueTask}"/>.
/// </summary>
internal sealed class AsyncDelegateDisposable : IAsyncDisposable
{
    private Func<ValueTask>? disposeAsync;

    public AsyncDelegateDisposable(Func<ValueTask> disposeAsync)
    {
        this.disposeAsync = disposeAsync ?? throw new ArgumentNullException(nameof(disposeAsync));
    }

    public async ValueTask DisposeAsync()
    {
        var dispose = Interlocked.Exchange(ref this.disposeAsync, null);
        if (dispose is not null)
        {
            await dispose().ConfigureAwait(false);
        }
    }
}
