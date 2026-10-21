using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Testing;

/// <summary>Deterministic authorization and fixed-generation leases for caller-boundary tests.</summary>
public sealed class StoredSecretAccessTestBroker : IAccessBroker, ICredentialLeaseContext, IReleaseAuthorizationPolicy
{
    private readonly Func<string, string?> readMaterial;
    private readonly List<StoredSecretAccessRequest> requests = [];
    private readonly List<SensitiveValueLease> leases = [];
    private readonly object gate = new();

    public StoredSecretAccessTestBroker(string material)
        : this(_ => material)
    {
    }

    public StoredSecretAccessTestBroker(Func<string, string?> readMaterial)
    {
        this.readMaterial = readMaterial;
    }

    public bool Authorized { get; set; } = true;
    public bool CurrentGeneration { get; set; } = true;
    public AccessRequestor Requestor { get; init; } = new()
    {
        Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(
            AccessRequestorKind.Application,
            "worker-provider-test-host"),
        Kind = AccessRequestorKind.Application,
        SafeDisplayName = "Test worker",
    };
    public CredentialUseLineage Lineage { get; init; } = new()
    {
        Session = CredentialIdentityFactory.CreateLineageIdentifier("host-session", Guid.NewGuid().ToString("N")),
    };
    public Action? OnAcquiring { get; set; }
    public Action<CredentialLeaseRequiredActionKind>? OnRequired { get; set; }
    public int Validations { get; private set; }
    public IReleaseAuthorizationPolicy Policy => this;
    public IReadOnlyList<StoredSecretAccessRequest> Requests
    {
        get { lock (this.gate) { return this.requests.ToArray(); } }
    }
    public IReadOnlyList<SensitiveValueLease> Leases
    {
        get { lock (this.gate) { return this.leases.ToArray(); } }
    }

    public Task<AccessOutcome<TResult>> AcquireAsync<TResult>(
        AccessRequest<TResult> request,
        CancellationToken cancellationToken = default)
        where TResult : IAccessResult
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is not StoredSecretAccessRequest stored
            || stored.Sources.Count != 1
            || stored.Credential.Provider != CredentialProviderKind.StoredSecret
            || stored.Credential.Kind != CredentialKind.Secret
            || string.IsNullOrWhiteSpace(stored.Reference.Name)
            || stored.Sources[0].Kind != CredentialSourceKind.Environment
            || stored.Sources[0].Provider != CredentialProviderKind.StoredSecret
            || stored.Sources[0].Identifier != CredentialIdentityFactory.CreateSourceIdentifier(
                CredentialProviderKind.StoredSecret,
                $"{CredentialSourceKind.Environment}:{stored.Reference.Name}")
            || stored.Requestor != this.Requestor
            || stored.Use.Kind != CredentialUseKind.Agent
            || stored.Use.Lineage.Session != this.Lineage.Session
            || stored.Use.Lineage.Key != CredentialIdentityFactory.CreateLineageIdentifier(
                "environment-key",
                stored.Reference.Name)
            || stored.Use.Lineage.ProviderConfiguration != CredentialIdentityFactory.CreateLineageIdentifier(
                "worker-provider",
                stored.Credential.LogicalIdentifier.Digest))
        {
            throw new InvalidOperationException("Expected an environment-only worker credential request with exact host context.");
        }
        lock (this.gate) { this.requests.Add(stored); }
        this.OnAcquiring?.Invoke();
        if (!this.Authorized)
        {
            return Task.FromResult<AccessOutcome<TResult>>(new AccessAuthorizationRequired<TResult>
            {
                RequestIdentifier = new() { Value = Guid.NewGuid() },
            });
        }

        var material = this.readMaterial(stored.Reference.Name);
        if (string.IsNullOrWhiteSpace(material))
        {
            return Task.FromResult<AccessOutcome<TResult>>(new AccessFailed<TResult>
            {
                ErrorCode = AccessErrorCode.ProviderFailure,
                SafeMessage = "The test source is unavailable.",
                CanRetry = false,
            });
        }
        var lease = new SensitiveValueLease(
            material.AsMemory(),
            new CredentialLeaseBinding
            {
                AuthorizationIdentifier = new() { Value = Guid.NewGuid() },
                MaterialGeneration = new() { Value = Guid.NewGuid() },
                Request = new ReleasePolicyRequest
                {
                    RequestIdentifier = new() { Value = Guid.NewGuid() },
                    RequestGeneration = 1,
                    Credential = stored.Credential,
                    SourceIdentifier = stored.Sources[0].Identifier,
                    RequestorIdentifier = stored.Requestor.Identifier,
                    Use = stored.Use,
                },
            },
            this);
        lock (this.gate) { this.leases.Add(lease); }
        AccessOutcome<StoredSecretAccessResult> outcome = new AccessSucceeded<StoredSecretAccessResult>
        {
            Result = new StoredSecretAccessResult { Lease = lease },
        };
        return Task.FromResult(outcome is AccessOutcome<TResult> typed
            ? typed
            : throw new InvalidOperationException("Unexpected result type."));
    }

    public ValueTask<bool> IsCurrentGenerationAsync(
        CredentialMaterialGenerationIdentifier generation,
        CancellationToken cancellationToken) => ValueTask.FromResult(this.CurrentGeneration);

    public ValueTask<UserAuthorizationRequestIdentifier> RequireAsync(
        ReleasePolicyRequest request,
        CredentialLeaseRequiredActionKind action,
        CancellationToken cancellationToken)
    {
        this.OnRequired?.Invoke(action);
        return ValueTask.FromResult(new UserAuthorizationRequestIdentifier { Value = Guid.NewGuid() });
    }

    public ValueTask<ReleaseAuthorizationValidation> ValidateAsync(
        ReleaseAuthorizationIdentifier identifier,
        ReleasePolicyRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.Validations++;
        return ValueTask.FromResult(this.Authorized
            ? ReleaseAuthorizationValidation.Valid
            : ReleaseAuthorizationValidation.AuthorizationRequired);
    }

    public ValueTask<ReleasePolicyEvaluation> EvaluateAsync(
        ReleasePolicyRequest request,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public ValueTask<ReleaseAuthorization> CommitAsync(
        ReleasePolicyDecision decision,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public ValueTask FinalizeCredentialIdentityAsync(
        ReleaseAuthorizationIdentifier identifier,
        ResolvedCredentialIdentity identity,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<ReleaseMemoryDescriptor>> ListAsync(
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public ValueTask ForgetAsync(
        ReleaseMemoryIdentifier identifier,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public async ValueTask DisposeAsync()
    {
        foreach (var lease in this.Leases)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }
}
