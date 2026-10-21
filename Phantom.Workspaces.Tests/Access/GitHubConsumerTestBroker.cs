using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Services.Access;

namespace Phantom.Workspaces.Tests.Access;

internal sealed class GitHubConsumerTestBroker : IAccessBroker
{
    private readonly ReleaseAuthorizationPolicy policy = new(new ReleaseMemoryStore(
        Path.Combine(Environment.CurrentDirectory, ".scratch", "github-consumer-policies", Guid.NewGuid().ToString("N"), "releases.json")));
    public List<GitHubCredentialAccessRequest> Requests { get; } = [];
    public List<SensitiveValueLease> Leases { get; } = [];
    public bool Deny { get; init; }
    public Func<string?> MaterialProvider { get; init; } = () => "test-material";

    public async Task<AccessOutcome<TResult>> AcquireAsync<TResult>(
        AccessRequest<TResult> request,
        CancellationToken cancellationToken = default)
        where TResult : IAccessResult
    {
        var github = Assert.IsType<GitHubCredentialAccessRequest>(request);
        this.Requests.Add(github);
        var material = this.MaterialProvider();
        if (this.Deny || string.IsNullOrWhiteSpace(material))
        {
            return new AccessDenied<TResult> { Reason = AccessDenialReason.UserDenied };
        }
        var policyRequest = new ReleasePolicyRequest
        {
            RequestIdentifier = new() { Value = Guid.NewGuid() },
            RequestGeneration = 1,
            Credential = github.Credential with
            {
                AccountIdentifier = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "test-account"),
            },
            SourceIdentifier = github.Sources[0].Identifier,
            RequestorIdentifier = github.Requestor.Identifier,
            Use = github.Use,
        };
        var authorization = await this.policy.CommitAsync(new ReleasePolicyDecision { Request = policyRequest, Scope = ReleaseMemoryScopeKind.AllowOnce }, cancellationToken);
        var lease = new SensitiveValueLease(material.AsMemory(), new CredentialLeaseBinding
        {
            AuthorizationIdentifier = authorization.Identifier,
            MaterialGeneration = new() { Value = Guid.NewGuid() },
            Request = policyRequest,
        }, new Context(this.policy));
        this.Leases.Add(lease);
        return (AccessOutcome<TResult>)(object)new AccessSucceeded<GitHubCredentialAccessResult>
        {
            Result = new GitHubCredentialAccessResult { Lease = lease },
        };
    }

    public ValueTask DisposeAsync() => this.policy.DisposeAsync();

    private sealed class Context(IReleaseAuthorizationPolicy policy) : ICredentialLeaseContext
    {
        public IReleaseAuthorizationPolicy Policy => policy;
        public ValueTask<bool> IsCurrentGenerationAsync(CredentialMaterialGenerationIdentifier generation, CancellationToken cancellationToken) => ValueTask.FromResult(true);
        public ValueTask<UserAuthorizationRequestIdentifier> RequireAsync(ReleasePolicyRequest request, CredentialLeaseRequiredActionKind action, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected lease renewal.");
    }
}
