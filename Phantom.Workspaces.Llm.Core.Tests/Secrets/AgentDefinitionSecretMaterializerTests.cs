using System.Text.RegularExpressions;
using System.Text.Json;
using AgentSchema;
using Moq;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm.Core.Tests.Secrets;

public sealed class AgentDefinitionSecretMaterializerTests
{
    [Fact]
    public async Task AgentDefinitionSecretMaterializer_MultipleUses_SubmitsOneBrokerRequestPerUse()
    {
        var definition = AgentDefinition.FromJson("""{"kind":"prompt","name":"test","instructions":"${SECRET:Repeated} ${SECRET:Repeated} ${SECRET:Other}"}""")!;
        await using var broker = new ManifestAccessTestBroker();
        await using var result = await new AgentDefinitionSecretMaterializer().MaterializeAsync(
            definition, broker, new CredentialUseLineage(), CancellationToken.None);
        Assert.Equal(3, broker.Requests.Count);
        Assert.Equal(new[] { "Repeated", "Repeated", "Other" }, broker.Requests.Select(request => request.Reference.Name));
        Assert.Equal(3, broker.Requests.Select(request => request.Use.Fingerprint).Distinct().Count());
        var tokens = Regex.Matches(definition.ToJson(), @"\$\{SECRET:[^}]+\}").Select(match => match.Value).ToArray();
        Assert.Equal(3, tokens.Distinct().Count());
        Assert.DoesNotContain("Repeated", definition.ToJson());
        Assert.DoesNotContain("material", definition.ToJson());
        Assert.All(tokens, token => Assert.True(result.Resolver.TryResolve(token, out _)));
        Assert.All(broker.Requests, request => Assert.All(request.Sources,
            source => Assert.True(source.Kind is CredentialSourceKind.Environment or CredentialSourceKind.SecretStore)));
    }

    [Fact]
    public async Task AgentDefinitionSecretMaterializer_AccessFailure_FailsClosed()
    {
        var definition = AgentDefinition.FromJson("""{"kind":"prompt","name":"test","instructions":"${SECRET:One} ${SECRET:Two}"}""")!;
        var original = definition.ToJson();
        await using var broker = new ManifestAccessTestBroker { FailAt = 2 };
        await Assert.ThrowsAsync<SecretMaterializationFailedException>(() => new AgentDefinitionSecretMaterializer().MaterializeAsync(
            definition, broker, new CredentialUseLineage(), CancellationToken.None));
        Assert.Equal(original, definition.ToJson());
        Assert.IsType<SensitiveValueUseFailed<int>>(await broker.Results.Single().Lease.UseAsync((_, _) => ValueTask.FromResult(1), CancellationToken.None));
    }

    [Fact]
    public async Task SecretPlaceholderResolver_Handle_UsesFixedGenerationLeaseOnly()
    {
        var definition = AgentDefinition.FromJson("""{"kind":"prompt","name":"test","instructions":"${SECRET:One}"}""")!;
        await using var broker = new ManifestAccessTestBroker();
        var result = await new AgentDefinitionSecretMaterializer().MaterializeAsync(
            definition, broker, new CredentialUseLineage(), CancellationToken.None);
        var token = Regex.Match(definition.ToJson(), @"\$\{SECRET:[^}]+\}").Value;
        Assert.True(result.Resolver.TryResolve(token, out var lease));
        Assert.IsType<SensitiveValueUseSucceeded<string>>(await lease.UseAsync((value, _) => ValueTask.FromResult(value), CancellationToken.None));
        broker.Context.Current = false;
        Assert.IsType<SensitiveValueUseReacquisitionRequired<int>>(await lease.UseAsync((_, _) => ValueTask.FromResult(1), CancellationToken.None));
        Assert.Single(broker.Requests);
        await result.DisposeAsync();
        Assert.False(result.Resolver.TryResolve(token, out _));
        Assert.IsType<SensitiveValueUseFailed<int>>(await lease.UseAsync((_, _) => ValueTask.FromResult(1), CancellationToken.None));
        Assert.Null(typeof(AgentFactory).GetMethod("ResolveRequiredSecretOrEnvAsync",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic));
    }

    [Fact]
    public async Task MaterializeAsync_NoSecrets_DoesNotRequestCredential()
    {
        var definition = AgentDefinition.FromJson("""{"kind":"prompt","name":"test","instructions":"hello"}""")!;
        await using var broker = new ManifestAccessTestBroker();
        await using var result = await new AgentDefinitionSecretMaterializer().MaterializeAsync(
            definition, broker, new CredentialUseLineage(), CancellationToken.None);
        Assert.Empty(broker.Requests);
        Assert.False(result.Resolver.TryResolve("${SECRET:unknown}", out _));
    }

    [Fact]
    public async Task MaterializeAsync_OAuthClientSecret_DoesNotAdmitManifestCredentialWork()
    {
        var definition = AgentDefinition.FromJson("""
        {
          "kind": "prompt",
          "name": "test",
          "tools": [{
            "name": "oauth-tool",
            "kind": "mcp",
            "connection": {
              "kind": "oauth",
              "endpoint": "https://resource.test/mcp",
              "clientId": "client",
              "clientSecret": "${SECRET:ProviderSecret}"
            }
          }]
        }
        """)!;
        await using var broker = new ManifestAccessTestBroker { Deny = true };
        await using var result = await new AgentDefinitionSecretMaterializer().MaterializeAsync(
            definition,
            broker,
            new CredentialUseLineage(),
            CancellationToken.None);

        Assert.Empty(broker.Requests);
        Assert.False(result.Resolver.TryResolve("${SECRET:ProviderSecret}", out _));
        var tool = Assert.IsAssignableFrom<McpTool>(Assert.Single(Assert.IsType<PromptAgent>(definition).Tools!));
        Assert.Equal("${SECRET:ProviderSecret}", Assert.IsType<OAuthConnection>(tool.Connection).ClientSecret);
    }

    [Fact]
    public async Task MaterializeAsync_ArbitraryPromptShapedModelOptions_AcquiresManifestLeaseUsableAtSdkBoundary()
    {
        var definition = AgentDefinition.FromJson("""
        {
          "kind": "prompt",
          "name": "test",
          "model": {
            "id": "m",
            "provider": "p",
            "apiType": "OpenAI",
            "options": {
              "additionalProperties": {
                "payload": {
                  "kind": "prompt",
                  "tools": {
                    "nested": {
                      "kind": "mcp",
                      "connection": {
                        "kind": "oauth",
                        "clientSecret": "${SECRET:ModelSecret}"
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """)!;
        await using var broker = new ManifestAccessTestBroker();
        await using var materialized = await new AgentDefinitionSecretMaterializer().MaterializeAsync(
            definition,
            broker,
            new CredentialUseLineage(),
            CancellationToken.None);
        var request = Assert.Single(broker.Requests);
        Assert.Equal("ModelSecret", request.Reference.Name);
        var handle = Regex.Match(materialized.Definition.ToJson(), @"\$\{SECRET:[^}]+\}").Value;
        Assert.True(materialized.Resolver.TryResolve(handle, out _));
        var model = Assert.IsType<PromptAgent>(materialized.Definition).Model!;
        var credentials = new AgentCredentialLeaseSet(null, model.Options, materialized.Resolver);

        var sdkOptions = await credentials.UseAsync(
            (_, options, _) => Task.FromResult(JsonSerializer.Serialize(options)),
            CancellationToken.None);

        Assert.Contains(broker.Material, sdkOptions);
        Assert.DoesNotContain("${SECRET:", sdkOptions);
    }

    [Fact]
    public async Task MaterializeAsync_Denial_DoesNotRewriteOrAcquireLaterOccurrences()
    {
        var definition = AgentDefinition.FromJson("""{"kind":"prompt","name":"test","instructions":"${SECRET:One} ${SECRET:Two}"}""")!;
        var original = definition.ToJson();
        await using var broker = new ManifestAccessTestBroker { Deny = true };
        await Assert.ThrowsAsync<SecretMaterializationRefusedException>(() => new AgentDefinitionSecretMaterializer().MaterializeAsync(
            definition, broker, new CredentialUseLineage(), CancellationToken.None));
        Assert.Single(broker.Requests);
        Assert.Equal(original, definition.ToJson());
    }

    [Fact]
    public async Task MaterializeAsync_McpServerApiKey_IsDeferredAndNotAcquired()
    {
        var definition = AgentDefinition.FromJson("""
        {
          "kind":"prompt","name":"test",
          "tools":[{"name":"key-tool","kind":"mcp","connection":{"kind":"key","endpoint":"https://resource.test/mcp","apiKey":"${SECRET:McpKey}"}}]
        }
        """)!;
        var original = definition.ToJson();
        await using var broker = new ManifestAccessTestBroker();
        await using var result = await new AgentDefinitionSecretMaterializer().MaterializeAsync(
            definition, broker, new CredentialUseLineage(), CancellationToken.None);

        Assert.Empty(broker.Requests);
        Assert.Equal(original, definition.ToJson());
        Assert.Equal("${SECRET:McpKey}", Assert.Single(result.DeferredSecretReferences));
        Assert.False(result.Resolver.TryResolve("${SECRET:McpKey}", out _));
    }

    [Fact]
    public async Task MaterializeAsync_CoreAndMcp_MaterializesOnlyCoreAndDefersMcp()
    {
        var definition = AgentDefinition.FromJson("""
        {
          "kind":"prompt","name":"test","instructions":"${SECRET:CoreSecret}",
          "tools":[{"name":"key-tool","kind":"mcp","connection":{"kind":"key","endpoint":"https://resource.test/mcp","apiKey":"${SECRET:McpKey}"}}]
        }
        """)!;
        await using var broker = new ManifestAccessTestBroker();
        await using var result = await new AgentDefinitionSecretMaterializer().MaterializeAsync(
            definition, broker, new CredentialUseLineage(), CancellationToken.None);

        Assert.Equal("CoreSecret", Assert.Single(broker.Requests).Reference.Name);
        Assert.Equal("${SECRET:McpKey}", Assert.Single(result.DeferredSecretReferences));
        var json = definition.ToJson();
        Assert.DoesNotContain("CoreSecret", json);
        Assert.Contains("${SECRET:McpKey}", json);
    }

    [Fact]
    public async Task MaterializeAsync_RequiredCoreDenied_FailsClosedEvenWithDeferredMcp()
    {
        var definition = AgentDefinition.FromJson("""
        {
          "kind":"prompt","name":"test","instructions":"${SECRET:CoreSecret}",
          "tools":[{"name":"key-tool","kind":"mcp","connection":{"kind":"key","endpoint":"https://resource.test/mcp","apiKey":"${SECRET:McpKey}"}}]
        }
        """)!;
        var original = definition.ToJson();
        await using var broker = new ManifestAccessTestBroker { Deny = true };
        await Assert.ThrowsAsync<SecretMaterializationRefusedException>(() => new AgentDefinitionSecretMaterializer().MaterializeAsync(
            definition, broker, new CredentialUseLineage(), CancellationToken.None));
        Assert.Single(broker.Requests);
        Assert.Equal(original, definition.ToJson());
    }

    [Fact]
    public async Task MaterializeSecretsIfNeededAsync_McpServerApiKey_DefersReferenceOntoServicesWithoutAcquiring()
    {
        var definition = AgentDefinition.FromJson("""
        {
          "kind":"prompt","name":"test",
          "tools":[{"name":"key-tool","kind":"mcp","connection":{"kind":"key","endpoint":"https://resource.test/mcp","apiKey":"${SECRET:McpKey}"}}]
        }
        """)!;
        await using var broker = new ManifestAccessTestBroker();
        var prepared = await AgentFactory.MaterializeSecretsIfNeededAsync(
            definition,
            new AgentServices { AccessBroker = broker },
            manifest: null,
            agentSessionId: null,
            CancellationToken.None);

        Assert.Empty(broker.Requests);
        Assert.Equal("${SECRET:McpKey}", Assert.Single(prepared.Services!.DeferredSecretReferences!));
    }
}

internal sealed class ManifestAccessTestBroker : IAccessBroker
{
    public List<StoredSecretAccessRequest> Requests { get; } = [];
    public List<StoredSecretAccessResult> Results { get; } = [];
    public LeaseContext Context { get; } = new();
    public int FailAt { get; init; }
    public bool Deny { get; init; }
    public string Material { get; init; } = "material";

    public Task<AccessOutcome<TResult>> AcquireAsync<TResult>(
        AccessRequest<TResult> request,
        CancellationToken cancellationToken = default)
        where TResult : IAccessResult
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stored = Assert.IsType<StoredSecretAccessRequest>(request);
        this.Requests.Add(stored);
        AccessOutcome<StoredSecretAccessResult> outcome = this.Deny
            ? new AccessDenied<StoredSecretAccessResult> { Reason = AccessDenialReason.UserDenied }
            : this.Requests.Count == this.FailAt
                ? new AccessFailed<StoredSecretAccessResult> { ErrorCode = AccessErrorCode.ProviderFailure, SafeMessage = "Credential unavailable.", CanRetry = false }
                : new AccessSucceeded<StoredSecretAccessResult> { Result = CreateResult(this.Material, this.Context) };
        if (outcome is AccessSucceeded<StoredSecretAccessResult> succeeded)
        {
            this.Results.Add(succeeded.Result);
        }
        return Task.FromResult((AccessOutcome<TResult>)(object)outcome);
    }

    public static StoredSecretAccessResult CreateResult(
        string material,
        LeaseContext? context = null)
    {
        var requestor = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Manifest, "test");
        var lineage = new CredentialUseLineage();
        return new StoredSecretAccessResult
        {
            Lease = new SensitiveValueLease(material.AsMemory(), new CredentialLeaseBinding
            {
                AuthorizationIdentifier = new() { Value = Guid.NewGuid() },
                MaterialGeneration = new() { Value = Guid.NewGuid() },
                Request = new ReleasePolicyRequest
                {
                    RequestIdentifier = new() { Value = Guid.NewGuid() },
                    RequestGeneration = 1,
                    Credential = new CredentialReference
                    {
                        LogicalIdentifier = new() { Digest = new string('a', 64) },
                        Provider = CredentialProviderKind.StoredSecret,
                        Kind = CredentialKind.Secret,
                    },
                    SourceIdentifier = new() { Digest = new string('b', 64) },
                    RequestorIdentifier = requestor,
                    Use = new CredentialUseDescriptor
                    {
                        Kind = CredentialUseKind.Manifest,
                        Lineage = lineage,
                        Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(CredentialUseKind.Manifest, lineage, requestor, "test"),
                        SafeDisplayName = "Test",
                        MemoryScopes = [ReleaseMemoryScopeKind.AllowOnce],
                    },
                },
            }, context ?? new LeaseContext()),
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal sealed class LeaseContext : ICredentialLeaseContext
    {
        public LeaseContext()
        {
            var policy = new Mock<IReleaseAuthorizationPolicy>();
            policy.Setup(value => value.ValidateAsync(It.IsAny<ReleaseAuthorizationIdentifier>(), It.IsAny<ReleasePolicyRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ReleaseAuthorizationValidation.Valid);
            this.Policy = policy.Object;
        }

        public bool Current { get; set; } = true;
        public IReleaseAuthorizationPolicy Policy { get; }
        public ValueTask<bool> IsCurrentGenerationAsync(CredentialMaterialGenerationIdentifier generation, CancellationToken cancellationToken) => ValueTask.FromResult(this.Current);
        public ValueTask<UserAuthorizationRequestIdentifier> RequireAsync(ReleasePolicyRequest request, CredentialLeaseRequiredActionKind action, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new UserAuthorizationRequestIdentifier { Value = Guid.NewGuid() });
    }
}
