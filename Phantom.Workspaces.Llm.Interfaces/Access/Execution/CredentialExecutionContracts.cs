namespace Phantom.Workspaces.Llm.Interfaces.Access.Execution;

internal sealed record CredentialGenerationInspection
{
    public required CredentialMaterialGenerationIdentifier Generation { get; init; }
    public DateTimeOffset? ExpiresOn { get; init; }
    public CredentialAccountIdentifier? AccountIdentifier { get; init; }
}

internal enum CredentialGenerationInvalidationOutcome { Invalidated, NoLongerCurrent, Missing }

internal interface ICredentialGenerationValidator
{
    ValueTask<bool> IsCurrentGenerationAsync(
        CredentialMaterialGenerationIdentifier generation,
        CancellationToken cancellationToken);
}

internal interface ICredentialGenerationCache : ICredentialGenerationValidator
{
    ValueTask<CredentialGenerationInspection?> InspectGenerationAsync(CancellationToken cancellationToken);

    ValueTask<CredentialGenerationInvalidationOutcome> InvalidateGenerationAsync(
        CredentialMaterialGenerationIdentifier generation,
        CancellationToken cancellationToken);
}

internal sealed record CredentialInspection
{
    public required Guid InspectionIdentifier { get; init; }
    public required CredentialReference Credential { get; init; }
    public required IReadOnlyList<CredentialSourceChoiceDescriptor> Sources { get; init; }
    public IReadOnlyDictionary<CredentialSourceIdentifier, ProviderAcquisitionKey> AcquisitionKeys { get; init; } =
        new Dictionary<CredentialSourceIdentifier, ProviderAcquisitionKey>();
    public IReadOnlyDictionary<CredentialSourceIdentifier, CredentialGenerationInspection> CachedGenerations { get; init; } =
        new Dictionary<CredentialSourceIdentifier, CredentialGenerationInspection>();
    public CredentialSourceIdentifier? SelectedSource { get; init; }
}

internal sealed record CompletionAuthorizationInspection
{
    public required bool IsCompleted { get; init; }
    public required CredentialSourceChoiceDescriptor Source { get; init; }
    public required ProviderAcquisitionKey AcquisitionKey { get; init; }
}

internal interface IAccessResultMaterializer<TResult> : IAsyncDisposable
    where TResult : IAccessResult
{
    ValueTask<TResult> MaterializeAsync(CancellationToken cancellationToken);
}

internal abstract record CredentialExecutionOutcome<TResult>
    where TResult : IAccessResult;
internal sealed record CredentialExecutionReady<TResult> : CredentialExecutionOutcome<TResult>
    where TResult : IAccessResult
{
    public required IAccessResultMaterializer<TResult> Materializer { get; init; }
    public ResolvedCredentialIdentity? ResolvedIdentity { get; init; }
}
internal sealed record CredentialExecutionInteractionRequired<TResult> : CredentialExecutionOutcome<TResult>
    where TResult : IAccessResult;
internal sealed record CredentialExecutionRenewalRequired<TResult> : CredentialExecutionOutcome<TResult>
    where TResult : IAccessResult;
internal sealed record CredentialExecutionMissing<TResult> : CredentialExecutionOutcome<TResult>
    where TResult : IAccessResult;
internal sealed record CredentialExecutionDenied<TResult> : CredentialExecutionOutcome<TResult>
    where TResult : IAccessResult;
internal sealed record CredentialExecutionFailed<TResult> : CredentialExecutionOutcome<TResult>
    where TResult : IAccessResult
{
    public required AccessErrorCode ErrorCode { get; init; }
    public required bool CanRetry { get; init; }
}

internal interface ICredentialAccessExecutor<TRequest, TResult>
    where TRequest : CredentialAccessRequest<TResult>
    where TResult : ICredentialAccessResult
{
    CredentialExecutorKind Kind { get; }
    ValueTask<CredentialInspection> InspectAsync(
        TRequest request,
        CancellationToken cancellationToken);
    ValueTask<CredentialExecutionOutcome<TResult>> ExecuteAsync(
        TRequest request,
        CredentialInspection inspection,
        ReleaseAuthorization authorization,
        ProviderInteractionPermit? interactionPermit,
        ICredentialExecutionContext context,
        CancellationToken cancellationToken);
}

internal interface ICompletionAuthorizationExecutor<TRequest, TResult>
    where TRequest : CompletionAuthorizationRequest<TResult>
    where TResult : IAccessResult
{
    CredentialExecutorKind Kind { get; }
    ValueTask<CompletionAuthorizationInspection> InspectAsync(
        TRequest request,
        CancellationToken cancellationToken);
    ValueTask<TResult> CreateCompletedResultAsync(CancellationToken cancellationToken);
    ValueTask<CredentialExecutionOutcome<TResult>> ExecuteAsync(
        TRequest request,
        ProviderInteractionPermit permit,
        ICredentialExecutionContext context,
        CancellationToken cancellationToken);
}

internal interface ICredentialExecutionContext
{
    UserAuthorizationRequestIdentifier RequestIdentifier { get; }
    long Revision { get; }
    Guid AcquisitionGeneration { get; }
    AccessRequestor Requestor { get; }
    CredentialUseDescriptor Use { get; }
    AccessPresentationScope PresentationScope { get; }
    ReleasePolicyRequest? PolicyRequest { get; }
    IReleaseAuthorizationPolicy Policy { get; }

    ValueTask<AccessOutcome<TResult>> AcquireDependencyAsync<TResult>(
        AccessRequest<TResult> request,
        CancellationToken cancellationToken)
        where TResult : IAccessResult;

    ValueTask<ProviderAcquisitionOutcome> ExecuteProviderAcquisitionAsync(
        IProviderAcquisitionOperation operation,
        ProviderInteractionPermit permit,
        CancellationToken cancellationToken);

    ValueTask<ProviderAcquisitionOutcome> ExecutePrivateProviderDependencyAsync(
        IProviderAcquisitionOperation operation,
        ProviderInteractionPermit parentGenerationPermit,
        CancellationToken cancellationToken);

    ICredentialLeaseContext CreateLeaseContext(ICredentialGenerationValidator generationValidator);
}
