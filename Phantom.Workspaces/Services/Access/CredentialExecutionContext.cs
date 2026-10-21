using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;

namespace Phantom.Workspaces.Services.Access;

internal sealed class CredentialExecutionContext : ICredentialExecutionContext
{
    private readonly AccessBroker broker;
    private readonly IReadOnlyList<CredentialDependencyKey> chain;
    private readonly Func<CredentialLeaseRequiredActionKind, CancellationToken, ValueTask<UserAuthorizationRequestIdentifier>>? require;
    private readonly bool canPerform;

    internal CredentialExecutionContext(
        AccessBroker broker,
        IReadOnlyList<CredentialDependencyKey> chain,
        UserAuthorizationRequestIdentifier requestIdentifier,
        long revision,
        Guid acquisitionGeneration,
        AccessRequestor requestor,
        CredentialUseDescriptor use,
        AccessPresentationScope presentationScope,
        ReleasePolicyRequest? policyRequest,
        bool canPerform,
        Func<CredentialLeaseRequiredActionKind, CancellationToken, ValueTask<UserAuthorizationRequestIdentifier>>? require)
    {
        this.broker = broker;
        this.chain = chain;
        this.RequestIdentifier = requestIdentifier;
        this.Revision = revision;
        this.AcquisitionGeneration = acquisitionGeneration;
        this.Requestor = requestor;
        this.Use = use;
        this.PresentationScope = presentationScope;
        this.PolicyRequest = policyRequest;
        this.canPerform = canPerform;
        this.require = require;
    }

    public UserAuthorizationRequestIdentifier RequestIdentifier { get; }
    public long Revision { get; }
    public Guid AcquisitionGeneration { get; }
    public AccessRequestor Requestor { get; }
    public CredentialUseDescriptor Use { get; }
    public AccessPresentationScope PresentationScope { get; }
    public ReleasePolicyRequest? PolicyRequest { get; internal set; }
    public IReleaseAuthorizationPolicy Policy => this.broker.Policy;

    public async ValueTask<AccessOutcome<TResult>> AcquireDependencyAsync<TResult>(
        AccessRequest<TResult> request,
        CancellationToken cancellationToken)
        where TResult : IAccessResult
    {
        if (this.PolicyRequest is null)
        {
            return new AccessDenied<TResult> { Reason = AccessDenialReason.PolicyDenied };
        }

        return await this.broker.AcquireNestedAsync(request with
        {
            Requestor = this.Requestor,
            Use = this.Use,
            PresentationScope = this.PresentationScope,
        }, this.chain, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ProviderAcquisitionOutcome> ExecuteProviderAcquisitionAsync(
        IProviderAcquisitionOperation operation,
        ProviderInteractionPermit permit,
        CancellationToken cancellationToken)
    {
        var outcome = await this.broker.Coordinator.ExecuteOrJoinAsync(operation, permit, cancellationToken).ConfigureAwait(false);
        if (outcome is ProviderAcquisitionSucceeded)
        {
            await this.broker.RefreshPendingSourcesAsync(cancellationToken).ConfigureAwait(false);
        }

        return outcome;
    }

    public async ValueTask<ProviderAcquisitionOutcome> ExecutePrivateProviderDependencyAsync(
        IProviderAcquisitionOperation operation,
        ProviderInteractionPermit parentGenerationPermit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!this.canPerform || this.PolicyRequest is null || parentGenerationPermit is null ||
            operation.RequestIdentifier != this.RequestIdentifier ||
            operation.Revision != this.Revision || operation.AcquisitionGeneration != this.AcquisitionGeneration ||
            operation.ExecutorKind == CredentialExecutorKind.DevTunnelConnect ||
            operation.CompatibilityKey.Protocol == ProviderProtocolKind.TunnelConnect ||
            !parentGenerationPermit.IsActivePrivateExecution(this) ||
            (this.PolicyRequest.Credential.AccountIdentifier is { } account &&
                operation.CompatibilityKey.Account != account))
        {
            return new ProviderAcquisitionDenied();
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, parentGenerationPermit.ExecutionCancellation);
        if (!await parentGenerationPermit.AuthorizesPrivateUseAsync(this, cancellation.Token).ConfigureAwait(false))
        {
            return new ProviderAcquisitionDenied();
        }

        var permit = new ProviderInteractionPermit(
            this.RequestIdentifier,
            this.Revision,
            operation.ExecutorKind,
            this.AcquisitionGeneration,
            operation.CompatibilityKey,
            () => parentGenerationPermit.IsActivePrivateExecution(this),
            privateDependencyBinding: new PrivateProviderDependencyBinding
            {
                Context = this,
                ParentPermit = parentGenerationPermit,
            });
        var outcome = await this.ExecuteProviderAcquisitionAsync(operation, permit, cancellation.Token).ConfigureAwait(false);
        return await parentGenerationPermit.AuthorizesPrivateUseAsync(this, cancellation.Token).ConfigureAwait(false)
            ? outcome
            : new ProviderAcquisitionDenied();
    }

    public ICredentialLeaseContext CreateLeaseContext(ICredentialGenerationValidator generationValidator)
    {
        if (this.require is null || this.PolicyRequest is null)
        {
            throw new InvalidOperationException("Completion-only authorization cannot export a credential lease.");
        }

        return new LeaseContext(this.Policy, generationValidator, this.require);
    }

    private sealed class LeaseContext(
        IReleaseAuthorizationPolicy policy,
        ICredentialGenerationValidator generationValidator,
        Func<CredentialLeaseRequiredActionKind, CancellationToken, ValueTask<UserAuthorizationRequestIdentifier>> require)
        : ICredentialLeaseContext
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly Dictionary<CredentialLeaseRequiredActionKind, UserAuthorizationRequestIdentifier> requirements = new();

        public IReleaseAuthorizationPolicy Policy => policy;
        public ValueTask<bool> IsCurrentGenerationAsync(
            CredentialMaterialGenerationIdentifier generation,
            CancellationToken cancellationToken) => generationValidator.IsCurrentGenerationAsync(generation, cancellationToken);

        public async ValueTask<UserAuthorizationRequestIdentifier> RequireAsync(
            ReleasePolicyRequest request,
            CredentialLeaseRequiredActionKind action,
            CancellationToken cancellationToken)
        {
            await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!this.requirements.TryGetValue(action, out var identifier))
                {
                    identifier = await require(action, cancellationToken).ConfigureAwait(false);
                    this.requirements.Add(action, identifier);
                }

                return identifier;
            }
            finally
            {
                this.gate.Release();
            }
        }
    }
}
