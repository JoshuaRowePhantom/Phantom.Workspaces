using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;

namespace Phantom.Workspaces.Llm.Core.Tests;

internal static class EntraSessionTestAuthorization
{
    internal static (ICredentialExecutionContext Context, ProviderInteractionPermit Permit) PrivateConnect(
        EntraCredentialAccessRequest configuration,
        CredentialAccountIdentifier? account = null,
        Func<bool>? isActive = null,
        Func<ReleaseAuthorizationValidation>? acquisitionValidation = null)
    {
        var request = PolicyRequest(configuration, account) with
        {
            Credential = configuration.Credential with
            {
                Provider = CredentialProviderKind.DevTunnel,
                Kind = CredentialKind.ConnectToken,
                AccountIdentifier = account ?? DefaultAccount(),
            },
        };
        var context = Context(configuration, request, ReleaseAuthorizationValidation.Valid, acquisitionValidation);
        var key = new ProviderAcquisitionKey
        {
            Configuration = request.Credential.LogicalIdentifier,
            Source = request.SourceIdentifier,
            Account = request.Credential.AccountIdentifier,
            Protocol = ProviderProtocolKind.TunnelConnect,
        };
        var authorization = new ReleaseAuthorization
        {
            Identifier = new() { Value = Guid.NewGuid() },
            RequestIdentifier = request.RequestIdentifier,
            RequestGeneration = request.RequestGeneration,
            Scope = ReleaseMemoryScopeKind.AllowOnce,
        };
        var permit = new ProviderInteractionPermit(
            context.RequestIdentifier, context.Revision, CredentialExecutorKind.DevTunnelConnect,
            context.AcquisitionGeneration, key, () => true,
            new ProviderExecutionAuthorizationBinding
            {
                Policy = context.Policy,
                Authorization = authorization,
                Request = request,
            }).BindExecution(isActive ?? (() => true));
        return (context, permit);
    }

    internal static (ICredentialExecutionContext Context, ReleaseAuthorization Authorization) Export(
        EntraCredentialAccessRequest configuration,
        CredentialAccountIdentifier? account = null,
        ReleaseAuthorizationValidation validation = ReleaseAuthorizationValidation.Valid)
    {
        var request = PolicyRequest(configuration, account);
        return (Context(configuration, request, validation), new ReleaseAuthorization
        {
            Identifier = new() { Value = Guid.NewGuid() },
            RequestIdentifier = request.RequestIdentifier,
            RequestGeneration = request.RequestGeneration,
            Scope = ReleaseMemoryScopeKind.AllowOnce,
        });
    }

    private static ReleasePolicyRequest PolicyRequest(
        EntraCredentialAccessRequest configuration,
        CredentialAccountIdentifier? account) => new()
    {
        RequestIdentifier = new() { Value = Guid.NewGuid() },
        RequestGeneration = 1,
        Credential = configuration.Credential with
        {
            AccountIdentifier = account ?? configuration.Credential.AccountIdentifier ?? DefaultAccount(),
        },
        SourceIdentifier = configuration.CreateSourceIdentifier(),
        RequestorIdentifier = configuration.Requestor.Identifier,
        Use = configuration.Use,
    };

    private static ICredentialExecutionContext Context(
        EntraCredentialAccessRequest configuration,
        ReleasePolicyRequest request,
        ReleaseAuthorizationValidation validation,
        Func<ReleaseAuthorizationValidation>? acquisitionValidation = null)
    {
        return new ExecutionContext(configuration, request, new Policy(validation, acquisitionValidation));
    }

    private static CredentialAccountIdentifier DefaultAccount() =>
        CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, "stable-test-account");

    private sealed class Policy(
        ReleaseAuthorizationValidation validation,
        Func<ReleaseAuthorizationValidation>? acquisitionValidation) :
        IReleaseAuthorizationPolicy, IProviderAcquisitionAuthorizationPolicy
    {
        public ValueTask<ReleaseAuthorizationValidation> ValidateAsync(
            ReleaseAuthorizationIdentifier identifier,
            ReleasePolicyRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(validation);

        public ValueTask<ReleaseAuthorizationValidation> ValidateAcquisitionAuthorizationAsync(
            ReleaseAuthorization authorization,
            ReleasePolicyRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(acquisitionValidation?.Invoke() ?? validation);

        public ValueTask<ReleasePolicyEvaluation> EvaluateAsync(
            ReleasePolicyRequest request,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public ValueTask<ReleaseAuthorization> CommitAsync(
            ReleasePolicyDecision decision,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public ValueTask FinalizeCredentialIdentityAsync(
            ReleaseAuthorizationIdentifier identifier,
            ResolvedCredentialIdentity identity,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public ValueTask<IReadOnlyList<ReleaseMemoryDescriptor>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();
        public ValueTask ForgetAsync(ReleaseMemoryIdentifier identifier, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();
    }

    private sealed class ExecutionContext(
        EntraCredentialAccessRequest configuration,
        ReleasePolicyRequest request,
        IReleaseAuthorizationPolicy policy) : ICredentialExecutionContext
    {
        public UserAuthorizationRequestIdentifier RequestIdentifier => request.RequestIdentifier;
        public long Revision => request.RequestGeneration;
        public Guid AcquisitionGeneration { get; } = Guid.NewGuid();
        public AccessRequestor Requestor => configuration.Requestor;
        public CredentialUseDescriptor Use => configuration.Use;
        public AccessPresentationScope PresentationScope => configuration.PresentationScope;
        public ReleasePolicyRequest? PolicyRequest => request;
        public IReleaseAuthorizationPolicy Policy => policy;

        public ValueTask<AccessOutcome<TResult>> AcquireDependencyAsync<TResult>(
            AccessRequest<TResult> dependency,
            CancellationToken cancellationToken) where TResult : IAccessResult =>
            throw new InvalidOperationException("Dependency acquisition is not part of this context fixture.");

        public ValueTask<ProviderAcquisitionOutcome> ExecuteProviderAcquisitionAsync(
            IProviderAcquisitionOperation operation,
            ProviderInteractionPermit permit,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Provider execution is not part of this context fixture.");

        public ValueTask<ProviderAcquisitionOutcome> ExecutePrivateProviderDependencyAsync(
            IProviderAcquisitionOperation operation,
            ProviderInteractionPermit parentGenerationPermit,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Private acquisition is not part of this context fixture.");

        public ICredentialLeaseContext CreateLeaseContext(ICredentialGenerationValidator generationValidator) =>
            throw new InvalidOperationException("Lease creation is not part of this context fixture.");
    }
}
