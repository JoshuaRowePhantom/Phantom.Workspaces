using System.Collections.Concurrent;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Tests.Access;

internal sealed class ObservedHttpAccessBroker(IAccessBroker borrowedBroker) : IAccessBroker
{
    public ConcurrentQueue<IAccessResult> AcquiredResults { get; } = new();

    public async Task<AccessOutcome<TResult>> AcquireAsync<TResult>(
        AccessRequest<TResult> request,
        CancellationToken cancellationToken = default)
        where TResult : IAccessResult
    {
        var outcome = await borrowedBroker.AcquireAsync(request, cancellationToken);
        if (outcome is AccessSucceeded<TResult> success)
        {
            this.AcquiredResults.Enqueue(success.Result);
        }
        return outcome;
    }

    // The fixture retains ownership of the wrapped broker.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
