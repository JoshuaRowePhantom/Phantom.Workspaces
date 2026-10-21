namespace Phantom.Workspaces.Llm.Interfaces.Access.Execution;

internal abstract record ProviderAcquisitionOutcome;
internal sealed record ProviderAcquisitionSucceeded : ProviderAcquisitionOutcome
{
    public required CredentialMaterialGenerationIdentifier CommittedGeneration { get; init; }
}
internal sealed record ProviderAcquisitionDenied : ProviderAcquisitionOutcome;
internal sealed record ProviderAcquisitionCancelled : ProviderAcquisitionOutcome;
internal sealed record ProviderAcquisitionShuttingDown : ProviderAcquisitionOutcome;
internal sealed record ProviderAcquisitionRenewalRequired : ProviderAcquisitionOutcome;
internal sealed record ProviderAcquisitionFailed : ProviderAcquisitionOutcome
{
    public required AccessErrorCode ErrorCode { get; init; }
}

internal interface IProviderAcquisitionOperation
{
    ProviderAcquisitionKey CompatibilityKey { get; }
    UserAuthorizationRequestIdentifier RequestIdentifier { get; }
    long Revision { get; }
    CredentialExecutorKind ExecutorKind { get; }
    Guid AcquisitionGeneration { get; }

    ValueTask<ProviderAcquisitionOutcome> ExecuteAsync(
        ProviderAcquisitionCommitContext commitContext,
        CancellationToken cancellationToken);
}

internal interface IProviderAcquisitionCoordinator : IAsyncDisposable
{
    ValueTask<ProviderAcquisitionOutcome> ExecuteOrJoinAsync(
        IProviderAcquisitionOperation operation,
        ProviderInteractionPermit permit,
        CancellationToken cancellationToken);
}

internal sealed class ProviderAcquisitionCommitContext
{
    private readonly Func<bool> isActive;
    private readonly CancellationToken cancellationToken;
    private readonly SemaphoreSlim commitGate;
    private int committed;

    internal ProviderAcquisitionCommitContext(
        Func<bool> isActive,
        CancellationToken cancellationToken,
        SemaphoreSlim commitGate,
        ProviderInteractionPermit interactionPermit)
    {
        this.isActive = isActive;
        this.cancellationToken = cancellationToken;
        this.commitGate = commitGate;
        this.InteractionPermit = interactionPermit;
    }

    internal CredentialMaterialGenerationIdentifier? CommittedGeneration { get; private set; }
    internal ProviderInteractionPermit InteractionPermit { get; }

    public async ValueTask<ProviderAcquisitionOutcome> CommitAsync(
        CredentialMaterialGenerationIdentifier materialGeneration,
        Func<CancellationToken, ValueTask> commit)
    {
        if (!this.isActive() || this.cancellationToken.IsCancellationRequested)
        {
            return new ProviderAcquisitionCancelled();
        }

        await this.commitGate.WaitAsync(this.cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await this.InteractionPermit.AuthorizesAcquisitionAsync(this.cancellationToken).ConfigureAwait(false))
            {
                return new ProviderAcquisitionDenied();
            }

            if (!this.isActive() || this.cancellationToken.IsCancellationRequested)
            {
                return new ProviderAcquisitionCancelled();
            }

            if (Interlocked.CompareExchange(ref this.committed, 1, 0) != 0)
            {
                throw new InvalidOperationException("A provider generation can be committed only once.");
            }

            await commit(this.cancellationToken).ConfigureAwait(false);
            this.CommittedGeneration = materialGeneration;
            return new ProviderAcquisitionSucceeded { CommittedGeneration = materialGeneration };
        }
        finally
        {
            this.commitGate.Release();
        }
    }
}
