using AgentSchema;
using Microsoft.Extensions.AI;
using Moq;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Transport.Tests.Infrastructure;

internal sealed class ByokScenarioCredentials : IAccessBroker, ICredentialLeaseContext
{
    internal const string KeyName = "BYOK_SCENARIO_KEY";
    internal const string Reference = "${BYOK_SCENARIO_KEY}";
    private readonly Mock<IReleaseAuthorizationPolicy> policy = new(MockBehavior.Strict);

    internal ByokScenarioCredentials()
    {
        this.Services = new AgentServices
        {
            AccessBroker = this,
            AccessRequestor = new AccessRequestor
            {
                Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Agent, "byok-scenario"),
                Kind = AccessRequestorKind.Agent,
                SafeDisplayName = "BYOK scenario",
            },
            AccessLineage = new CredentialUseLineage
            {
                Session = CredentialIdentityFactory.CreateLineageIdentifier("session", "byok-scenario"),
            },
            AccessPresentationScope = AccessPresentationScope.Bootstrap,
        };
        this.policy.Setup(value => value.ValidateAsync(
            It.IsAny<ReleaseAuthorizationIdentifier>(),
            It.IsAny<ReleasePolicyRequest>(),
            It.IsAny<CancellationToken>()))
            .Returns((ReleaseAuthorizationIdentifier identifier, ReleasePolicyRequest request, CancellationToken ct) =>
            {
                ct.ThrowIfCancellationRequested();
                Assert.Contains(this.Bindings, binding =>
                    binding.AuthorizationIdentifier == identifier && binding.Request == request);
                Interlocked.Increment(ref this.validations);
                return ValueTask.FromResult(this.Authorized
                    ? ReleaseAuthorizationValidation.Valid
                    : ReleaseAuthorizationValidation.AuthorizationRequired);
            });
    }

    private int validations;
    private int generationChecks;
    internal AgentServices Services { get; }
    internal bool Authorized { get; set; } = true;
    internal CredentialMaterialGenerationIdentifier Generation { get; set; } = new() { Value = Guid.NewGuid() };
    internal List<StoredSecretAccessRequest> Requests { get; } = [];
    internal List<StoredSecretAccessResult> Results { get; } = [];
    internal List<CredentialLeaseBinding> Bindings { get; } = [];
    internal List<CredentialLeaseRequiredActionKind> RequiredActions { get; } = [];
    internal int Validations => Volatile.Read(ref this.validations);
    internal int GenerationChecks => Volatile.Read(ref this.generationChecks);
    public IReleaseAuthorizationPolicy Policy => this.policy.Object;

    internal static AgentDefinition Definition(string baseUrl, string reference = Reference) =>
        AgentDefinitionLoader.LoadAgentFromJson($$"""
        {
          "kind": "prompt",
          "name": "byok-scenario",
          "model": {
            "id": "gpt-test",
            "provider": "github-models",
            "connection": {
              "kind": "key",
              "endpoint": "{{baseUrl}}",
              "apiKey": "{{reference}}"
            }
          }
        }
        """);

    internal async Task<IChatClient> CreateExecutorAsync(string baseUrl, CancellationToken ct) =>
        (await AgentFactory.CreateChatClientAsync(
            Definition(baseUrl), this.Services, cancellationToken: ct)).ChatClient;

    public Task<AccessOutcome<TResult>> AcquireAsync<TResult>(
        AccessRequest<TResult> request,
        CancellationToken cancellationToken = default) where TResult : IAccessResult
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stored = Assert.IsType<StoredSecretAccessRequest>(request);
        this.Requests.Add(stored);
        if (!this.Authorized)
        {
            return Task.FromResult<AccessOutcome<TResult>>(new AccessAuthorizationRequired<TResult>
            {
                RequestIdentifier = new() { Value = Guid.NewGuid() },
            });
        }

        var binding = new CredentialLeaseBinding
        {
            AuthorizationIdentifier = new() { Value = Guid.NewGuid() },
            MaterialGeneration = this.Generation,
            Request = new ReleasePolicyRequest
            {
                RequestIdentifier = new() { Value = Guid.NewGuid() },
                RequestGeneration = 1,
                Credential = stored.Credential,
                SourceIdentifier = Assert.Single(stored.Sources).Identifier,
                RequestorIdentifier = stored.Requestor.Identifier,
                Use = stored.Use,
            },
        };
        this.Bindings.Add(binding);
        var result = new StoredSecretAccessResult
        {
            Lease = new SensitiveValueLease("scenario-credential".AsMemory(), binding, this),
        };
        this.Results.Add(result);
        AccessOutcome<StoredSecretAccessResult> outcome = new AccessSucceeded<StoredSecretAccessResult> { Result = result };
        return Task.FromResult(Assert.IsAssignableFrom<AccessOutcome<TResult>>(outcome));
    }

    public ValueTask<bool> IsCurrentGenerationAsync(
        CredentialMaterialGenerationIdentifier generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref this.generationChecks);
        return ValueTask.FromResult(generation == this.Generation);
    }

    public ValueTask<UserAuthorizationRequestIdentifier> RequireAsync(
        ReleasePolicyRequest request, CredentialLeaseRequiredActionKind action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.RequiredActions.Add(action);
        return ValueTask.FromResult(new UserAuthorizationRequestIdentifier { Value = Guid.NewGuid() });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var result in this.Results)
        {
            await result.DisposeAsync();
        }
    }
}
