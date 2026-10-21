using System.Text.Json;
using System.Text.Json.Serialization;
namespace Phantom.Workspaces.Llm.Interfaces.Access.Execution;

internal enum CredentialExecutorKind { StoredSecret, GitHub, Entra, OAuth, McpOAuth, DevTunnelIdentity, DevTunnelConnect, CliOwned }
internal enum ProviderProtocolKind { Credential, OAuth, Device, Sdk, Cli, TunnelIdentity, TunnelConnect }

internal sealed record ProviderAcquisitionKey
{
    public required LogicalCredentialIdentifier Configuration { get; init; }
    public required CredentialSourceIdentifier Source { get; init; }
    public CredentialAccountIdentifier? Account { get; init; }
    public required ProviderProtocolKind Protocol { get; init; }
}

internal sealed record ProviderExecutionAuthorizationBinding
{
    public required IReleaseAuthorizationPolicy Policy { get; init; }
    public required ReleaseAuthorization Authorization { get; init; }
    public required ReleasePolicyRequest Request { get; init; }
    public required ProviderAcquisitionKey AcquisitionKey { get; init; }
}

internal interface IProviderAcquisitionAuthorizationPolicy
{
    ValueTask<ReleaseAuthorizationValidation> ValidateAcquisitionAuthorizationAsync(
        ReleaseAuthorization authorization,
        ReleasePolicyRequest request,
        CancellationToken cancellationToken);
}

internal sealed record ProviderAcquisitionParticipant(ProviderInteractionPermit Permit, CancellationToken Cancellation);

internal sealed class ProviderAcquisitionAuthorizationGroup
{
    private readonly object gate = new();
    private readonly List<ProviderAcquisitionParticipant> members = new();
    private bool closed;

    internal void Add(ProviderAcquisitionParticipant participant)
    {
        lock (this.gate)
        {
            if (this.closed)
            {
                throw new InvalidOperationException("A completed acquisition cannot admit participants.");
            }
            this.members.Add(participant);
        }
    }

    internal int Remove(ProviderAcquisitionParticipant participant)
    {
        lock (this.gate)
        {
            this.members.Remove(participant);
            return this.members.Count;
        }
    }

    internal void Close()
    {
        lock (this.gate)
        {
            this.closed = true;
            this.members.Clear();
        }
    }

    internal async ValueTask<bool> AuthorizesAsync(
        ProviderInteractionPermit executionPermit,
        CancellationToken cancellationToken)
    {
        if (!executionPermit.IsBoundTo(this))
        {
            return false;
        }

        bool IsMember(ProviderAcquisitionParticipant participant)
        {
            bool member;
            lock (this.gate)
            {
                member = !this.closed && !participant.Cancellation.IsCancellationRequested &&
                    this.members.Contains(participant);
            }
            return member && executionPermit.IsBoundTo(this);
        }

        ProviderAcquisitionParticipant[] snapshot;
        lock (this.gate)
        {
            snapshot = this.members.ToArray();
        }

        foreach (var member in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsMember(member) &&
                await member.Permit.AuthorizesGroupMemberAsync(executionPermit, cancellationToken).ConfigureAwait(false) &&
                IsMember(member))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed record PrivateProviderDependencyBinding
{
    public required ICredentialExecutionContext Context { get; init; }
    public required ProviderInteractionPermit ParentPermit { get; init; }
}

[JsonConverter(typeof(SerializationGuard))]
internal sealed class ProviderInteractionPermit
{
    private readonly UserAuthorizationRequestIdentifier requestIdentifier;
    private readonly long revision;
    private readonly CredentialExecutorKind executor;
    private readonly Guid generation;
    private readonly ProviderAcquisitionKey key;
    private readonly Func<bool> isCurrent;
    private readonly ProviderExecutionAuthorizationBinding? authorizationBinding;
    private int consumed;
    private bool executionBound;
    private CancellationToken executionCancellation;
    private ProviderAcquisitionAuthorizationGroup? authorizationGroup;
    private readonly PrivateProviderDependencyBinding? privateDependencyBinding;

    internal ProviderInteractionPermit(
        UserAuthorizationRequestIdentifier requestIdentifier,
        long revision,
        CredentialExecutorKind executor,
        Guid generation,
        ProviderAcquisitionKey key,
        Func<bool> isCurrent,
        ProviderExecutionAuthorizationBinding? authorizationBinding = null,
        PrivateProviderDependencyBinding? privateDependencyBinding = null)
    {
        this.requestIdentifier = requestIdentifier;
        this.revision = revision;
        this.executor = executor;
        this.generation = generation;
        this.key = key;
        this.isCurrent = isCurrent;
        this.authorizationBinding = authorizationBinding;
        this.privateDependencyBinding = privateDependencyBinding;
    }

    internal bool Authorizes(
        UserAuthorizationRequestIdentifier requestIdentifier,
        long revision,
        CredentialExecutorKind executor,
        Guid generation,
        ProviderAcquisitionKey key) =>
        this.requestIdentifier == requestIdentifier && this.revision == revision &&
        this.executor == executor && this.generation == generation && this.key == key &&
        this.isCurrent();

    internal bool TryConsume(IProviderAcquisitionOperation operation) =>
        this.Authorizes(operation.RequestIdentifier, operation.Revision,
            operation.ExecutorKind, operation.AcquisitionGeneration, operation.CompatibilityKey) &&
        Interlocked.CompareExchange(ref this.consumed, 1, 0) == 0;

    internal bool BelongsToGeneration(
        UserAuthorizationRequestIdentifier requestIdentifier,
        long revision,
        Guid generation) =>
        this.requestIdentifier == requestIdentifier && this.revision == revision &&
        this.generation == generation && this.isCurrent();

    internal bool AuthorizesProvider(
        CredentialExecutorKind executor,
        ProviderAcquisitionKey key) =>
        this.executionBound && !this.executionCancellation.IsCancellationRequested &&
        this.executor == executor && this.key == key && this.isCurrent();

    internal CancellationToken ExecutionCancellation => this.executionCancellation;

    internal bool IsBoundTo(ProviderAcquisitionAuthorizationGroup group) =>
        ReferenceEquals(this.authorizationGroup, group) && this.executionBound &&
        Volatile.Read(ref this.consumed) == 1 && !this.executionCancellation.IsCancellationRequested &&
        this.isCurrent();

    internal bool AuthorizesActiveExecution(
        UserAuthorizationRequestIdentifier requestIdentifier,
        long revision,
        CredentialExecutorKind executor,
        Guid generation,
        ProviderAcquisitionKey key) =>
        Volatile.Read(ref this.consumed) == 1 &&
        this.Authorizes(requestIdentifier, revision, executor, generation, key) &&
        this.AuthorizesProvider(executor, key);

    internal bool IsActivePrivateExecution(ICredentialExecutionContext? context)
    {
        var request = context?.PolicyRequest;
        var binding = this.authorizationBinding;
        if (context is null || request is null || binding is null ||
            !ReferenceEquals(binding.Policy, context.Policy) || binding.Request != request ||
            binding.Authorization.RequestIdentifier != request.RequestIdentifier ||
            binding.Authorization.RequestGeneration != request.RequestGeneration ||
            request.Credential.Provider != CredentialProviderKind.DevTunnel ||
            request.Credential.Kind != CredentialKind.ConnectToken ||
            request.RequestIdentifier != context.RequestIdentifier ||
            request.RequestGeneration != context.Revision ||
            request.RequestorIdentifier != context.Requestor.Identifier ||
            request.Use.Fingerprint != context.Use.Fingerprint ||
            request.Use.Kind != context.Use.Kind ||
            request.Use.Lineage != context.Use.Lineage ||
            request.Use.SafeDisplayName != context.Use.SafeDisplayName ||
            !request.Use.MemoryScopes.SequenceEqual(context.Use.MemoryScopes))
        {
            return false;
        }

        return this.key.Protocol == ProviderProtocolKind.TunnelConnect &&
            this.HasExactAcquisitionBinding() && this.AuthorizesActiveExecution(
            context.RequestIdentifier, context.Revision, CredentialExecutorKind.DevTunnelConnect,
            context.AcquisitionGeneration, binding.AcquisitionKey);
    }

    // A captured accountless key is exact, not a wildcard; the policy account still pins private use.
    private bool HasExactAcquisitionBinding() =>
        this.authorizationBinding is { } binding && this.key == binding.AcquisitionKey &&
        this.key.Configuration == binding.Request.Credential.LogicalIdentifier &&
        this.key.Source == binding.Request.SourceIdentifier &&
        (this.key.Account is not { } issuedAccount ||
            binding.Request.Credential.AccountIdentifier is not { } policyAccount ||
            issuedAccount == policyAccount);

    internal async ValueTask<bool> AuthorizesPrivateUseAsync(
        ICredentialExecutionContext? context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var binding = this.authorizationBinding;
        if (!this.IsActivePrivateExecution(context) ||
            binding is null || binding.Policy is not IProviderAcquisitionAuthorizationPolicy policy)
        {
            return false;
        }

        // The operation context stays pinned; another admitted grant authorizes shared work, not caller release.
        var authorized = this.authorizationGroup is { } group
            ? await group.AuthorizesAsync(this, cancellationToken).ConfigureAwait(false)
            : await policy.ValidateAcquisitionAuthorizationAsync(
                binding.Authorization, binding.Request, cancellationToken).ConfigureAwait(false) ==
                    ReleaseAuthorizationValidation.Valid;
        cancellationToken.ThrowIfCancellationRequested();
        return authorized && this.IsActivePrivateExecution(context);
    }

    internal async ValueTask<bool> AuthorizesGroupMemberAsync(
        ProviderInteractionPermit executionPermit,
        CancellationToken cancellationToken)
    {
        var originPolicy = executionPermit.authorizationBinding?.Policy ??
            executionPermit.privateDependencyBinding?.Context.Policy;
        var originRequest = executionPermit.authorizationBinding?.Request ??
            executionPermit.privateDependencyBinding?.Context.PolicyRequest;
        if (originPolicy is null || originRequest is null)
        {
            return false;
        }

        // Compatible provider work can mix root and private callers; validate each member's own authority.
        if (this.privateDependencyBinding is { } dependency)
        {
            bool IsExactDependency() =>
                !this.executionBound && Volatile.Read(ref this.consumed) == 1 && this.isCurrent() &&
                this.executor == executionPermit.executor && this.key == executionPermit.key &&
                ReferenceEquals(dependency.Context.Policy, originPolicy) &&
                dependency.Context.PolicyRequest?.Credential.AccountIdentifier ==
                    originRequest.Credential.AccountIdentifier &&
                this.requestIdentifier == dependency.Context.RequestIdentifier &&
                this.revision == dependency.Context.Revision &&
                this.generation == dependency.Context.AcquisitionGeneration &&
                (dependency.Context.PolicyRequest?.Credential.AccountIdentifier is not { } account ||
                    this.key.Account == account);

            return IsExactDependency() &&
                await dependency.ParentPermit.AuthorizesPrivateUseAsync(
                    dependency.Context, cancellationToken).ConfigureAwait(false) &&
                IsExactDependency();
        }

        bool IsExactMember()
        {
            var binding = this.authorizationBinding;
            return !this.executionBound && Volatile.Read(ref this.consumed) == 1 && this.isCurrent() &&
                binding is not null && ReferenceEquals(binding.Policy, originPolicy) &&
                binding.Request.Credential.AccountIdentifier == originRequest.Credential.AccountIdentifier &&
                (executionPermit.authorizationBinding is not { } origin ||
                    binding.Request.Credential == origin.Request.Credential) &&
                this.executor == executionPermit.executor && this.key == executionPermit.key &&
                binding.Authorization.RequestIdentifier == this.requestIdentifier &&
                binding.Authorization.RequestGeneration == this.revision &&
                binding.Request.RequestIdentifier == this.requestIdentifier &&
                binding.Request.RequestGeneration == this.revision &&
                this.HasExactAcquisitionBinding();
        }

        if (!IsExactMember() || this.authorizationBinding!.Policy is not IProviderAcquisitionAuthorizationPolicy policy)
        {
            return false;
        }

        var validation = await policy.ValidateAcquisitionAuthorizationAsync(
            this.authorizationBinding.Authorization, this.authorizationBinding.Request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return validation == ReleaseAuthorizationValidation.Valid && IsExactMember();
    }

    internal async ValueTask<bool> AuthorizesAcquisitionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!this.executionBound || this.executionCancellation.IsCancellationRequested || !this.isCurrent())
        {
            return false;
        }

        if (this.privateDependencyBinding is not null || this.authorizationBinding is not null)
        {
            return this.authorizationGroup is { } group &&
                await group.AuthorizesAsync(this, cancellationToken).ConfigureAwait(false) &&
                !this.executionCancellation.IsCancellationRequested && this.isCurrent();
        }

        return true;
    }

    internal ProviderInteractionPermit BindExecution(
        Func<bool> isActive,
        CancellationToken executionCancellation = default,
        ProviderAcquisitionAuthorizationGroup? authorizationGroup = null)
    {
        var permit = new ProviderInteractionPermit(
            this.requestIdentifier, this.revision, this.executor, this.generation, this.key, isActive,
            this.authorizationBinding, this.privateDependencyBinding);
        permit.consumed = 1;
        permit.executionBound = true;
        permit.executionCancellation = executionCancellation;
        permit.authorizationGroup = authorizationGroup;
        return permit;
    }

    private sealed class SerializationGuard : JsonConverter<ProviderInteractionPermit>
    {
        public override ProviderInteractionPermit Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotSupportedException("Interaction permits cannot be serialized.");

        public override void Write(
            Utf8JsonWriter writer,
            ProviderInteractionPermit value,
            JsonSerializerOptions options) => throw new NotSupportedException("Interaction permits cannot be serialized.");
    }
}
