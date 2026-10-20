namespace Phantom.Workspaces.Llm.Interfaces.Access;

public interface ICredentialUseSession : IAsyncDisposable
{
    ValueTask<TResult> UseAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken);
}

public abstract record CredentialUseOutcome<TResult>
{
    private protected CredentialUseOutcome() { }
}
public sealed record CredentialUseSucceeded<TResult> : CredentialUseOutcome<TResult>
{
    public required TResult Result { get; init; }
}
public sealed record CredentialUseAuthorizationRequired<TResult> : CredentialUseOutcome<TResult>
{
    public required UserAuthorizationRequestIdentifier RequestIdentifier { get; init; }
}
public sealed record CredentialUseReacquisitionRequired<TResult> : CredentialUseOutcome<TResult>
{
    public required UserAuthorizationRequestIdentifier RequestIdentifier { get; init; }
}
public sealed record CredentialUseDenied<TResult> : CredentialUseOutcome<TResult>;
public sealed record CredentialUseFailed<TResult> : CredentialUseOutcome<TResult>
{
    public required AccessErrorCode ErrorCode { get; init; }
    public required string SafeMessage { get; init; }
}

public sealed class CredentialUseLease : IAsyncDisposable
{
    private readonly ICredentialUseSession session;
    private readonly LeaseUseGuard guard;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;

    public CredentialUseLease(
        ICredentialUseSession session,
        CredentialLeaseBinding binding,
        ICredentialLeaseContext context,
        TimeProvider? timeProvider = null)
    {
        this.session = session;
        this.guard = new LeaseUseGuard(binding, context, timeProvider ?? TimeProvider.System);
    }

    public async ValueTask<CredentialUseOutcome<TResult>> UseAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref this.disposed) != 0)
        {
            return new CredentialUseFailed<TResult> { ErrorCode = AccessErrorCode.Disposed, SafeMessage = "The credential lease is disposed." };
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.lifetime.Token);
        await this.gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var validation = await this.guard.ValidateAsync(cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            return validation.Kind switch
            {
                LeaseValidationKind.AuthorizationRequired => new CredentialUseAuthorizationRequired<TResult> { RequestIdentifier = validation.RequestIdentifier! },
                LeaseValidationKind.ReacquisitionRequired => new CredentialUseReacquisitionRequired<TResult> { RequestIdentifier = validation.RequestIdentifier! },
                LeaseValidationKind.Denied => new CredentialUseDenied<TResult>(),
                LeaseValidationKind.Failed => new CredentialUseFailed<TResult>
                {
                    ErrorCode = AccessErrorCode.PersistenceFailure,
                    SafeMessage = "Release authorization could not be validated.",
                },
                _ => new CredentialUseSucceeded<TResult>
                {
                    Result = await this.session.UseAsync(operation, cancellation.Token).ConfigureAwait(false),
                },
            };
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            await this.disposal.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            try
            {
                await this.lifetime.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                await this.gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await this.session.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    this.gate.Release();
                }
            }

            this.disposal.TrySetResult();
        }
        catch (Exception exception)
        {
            this.disposal.TrySetException(exception);
            throw;
        }
    }
}
