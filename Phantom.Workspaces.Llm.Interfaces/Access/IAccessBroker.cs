namespace Phantom.Workspaces.Llm.Interfaces.Access;

public interface IAccessBroker : IAsyncDisposable
{
    Task<AccessOutcome<TResult>> AcquireAsync<TResult>(
        AccessRequest<TResult> request,
        CancellationToken cancellationToken = default)
        where TResult : IAccessResult;
}
