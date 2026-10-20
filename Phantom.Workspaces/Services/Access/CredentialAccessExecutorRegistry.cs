using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;

namespace Phantom.Workspaces.Services.Access;

internal sealed record CredentialDependencyKey
{
    public required Type RequestType { get; init; }
    public CredentialDigest? Identity { get; init; }
}

internal sealed class CredentialAccessExecutorRegistry : IAsyncDisposable
{
    private readonly Dictionary<Type, IRegistration> registrations = new();
    private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool sealedForDispatch;
    private int disposalStarted;

    public void Add<TRequest, TResult>(ICredentialAccessExecutor<TRequest, TResult> executor)
        where TRequest : CredentialAccessRequest<TResult>
        where TResult : ICredentialAccessResult
    {
        this.AddRegistration(typeof(TRequest), new CredentialRegistration<TRequest, TResult>(executor));
    }

    public void AddCompletion<TRequest, TResult>(ICompletionAuthorizationExecutor<TRequest, TResult> executor)
        where TRequest : CompletionAuthorizationRequest<TResult>
        where TResult : IAccessResult
    {
        this.AddRegistration(typeof(TRequest), new CompletionRegistration<TRequest, TResult>(executor));
    }

    internal Task<AccessOutcome<TResult>> DispatchAsync<TResult>(
        AccessBroker broker,
        AccessRequest<TResult> request,
        IReadOnlyList<CredentialDependencyKey> chain,
        CancellationToken cancellationToken)
        where TResult : IAccessResult
    {
        this.sealedForDispatch = true;
        return this.registrations.TryGetValue(request.GetType(), out var registration)
            ? registration.DispatchAsync(broker, request, chain, cancellationToken)
            : Task.FromResult<AccessOutcome<TResult>>(AccessBroker.Failure<TResult>(AccessErrorCode.UnsupportedRequest));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposalStarted, 1) != 0)
        {
            await this.disposal.Task.ConfigureAwait(false);
            return;
        }

        this.sealedForDispatch = true;
        var failures = new List<Exception>();
        foreach (var registration in this.registrations.Values)
        {
            try
            {
                await registration.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
        {
            var failure = new AggregateException("One or more credential executors could not be disposed.", failures);
            this.disposal.TrySetException(failure);
            throw failure;
        }

        this.disposal.TrySetResult();
    }

    private void AddRegistration(
        Type type,
        IRegistration registration)
    {
        if (!type.IsSealed || type.IsAbstract)
        {
            throw new InvalidOperationException("An executor must register one closed concrete request type.");
        }

        if (this.sealedForDispatch)
        {
            throw new InvalidOperationException("Executor registrations are immutable after dispatch begins.");
        }

        this.registrations.Add(type, registration);
    }

    private interface IRegistration : IAsyncDisposable
    {
        Task<AccessOutcome<TRequestedResult>> DispatchAsync<TRequestedResult>(
            AccessBroker broker,
            AccessRequest<TRequestedResult> request,
            IReadOnlyList<CredentialDependencyKey> chain,
            CancellationToken cancellationToken)
            where TRequestedResult : IAccessResult;
    }

    private sealed class CredentialRegistration<TRequest, TResult>(ICredentialAccessExecutor<TRequest, TResult> executor) : IRegistration
        where TRequest : CredentialAccessRequest<TResult>
        where TResult : ICredentialAccessResult
    {
        public async Task<AccessOutcome<TRequestedResult>> DispatchAsync<TRequestedResult>(
            AccessBroker broker,
            AccessRequest<TRequestedResult> request,
            IReadOnlyList<CredentialDependencyKey> chain,
            CancellationToken cancellationToken)
            where TRequestedResult : IAccessResult
        {
            if (request is not TRequest typedRequest)
            {
                throw new InvalidOperationException("Executor request/result registration mismatch.");
            }

            var key = new CredentialDependencyKey { RequestType = typeof(TRequest), Identity = typedRequest.Credential.LogicalIdentifier };
            if (chain.Contains(key))
            {
                return AccessBroker.Failure<TRequestedResult>(AccessErrorCode.DependencyCycle);
            }

            var outcome = await broker.AcquireCredentialAsync(
                typedRequest, executor, chain.Append(key).ToArray(), cancellationToken).ConfigureAwait(false);
            return outcome is AccessOutcome<TRequestedResult> typedOutcome
                ? typedOutcome : throw new InvalidOperationException("Executor returned an undeclared result type.");
        }

        public ValueTask DisposeAsync() => executor is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
    }

    private sealed class CompletionRegistration<TRequest, TResult>(ICompletionAuthorizationExecutor<TRequest, TResult> executor) : IRegistration
        where TRequest : CompletionAuthorizationRequest<TResult>
        where TResult : IAccessResult
    {
        public async Task<AccessOutcome<TRequestedResult>> DispatchAsync<TRequestedResult>(
            AccessBroker broker,
            AccessRequest<TRequestedResult> request,
            IReadOnlyList<CredentialDependencyKey> chain,
            CancellationToken cancellationToken)
            where TRequestedResult : IAccessResult
        {
            if (request is not TRequest typedRequest)
            {
                throw new InvalidOperationException("Executor request/result registration mismatch.");
            }

            var key = new CredentialDependencyKey { RequestType = typeof(TRequest) };
            if (chain.Contains(key))
            {
                return AccessBroker.Failure<TRequestedResult>(AccessErrorCode.DependencyCycle);
            }

            var outcome = await broker.AcquireCompletionAsync(
                typedRequest, executor, chain.Append(key).ToArray(), cancellationToken).ConfigureAwait(false);
            return outcome is AccessOutcome<TRequestedResult> typedOutcome
                ? typedOutcome : throw new InvalidOperationException("Executor returned an undeclared result type.");
        }

        public ValueTask DisposeAsync() => executor is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
    }
}
