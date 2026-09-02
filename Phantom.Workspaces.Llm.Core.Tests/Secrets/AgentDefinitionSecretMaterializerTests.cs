using System.Security;
using System.Text.RegularExpressions;
using AgentSchema;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm.Core.Tests.Secrets;

public sealed class AgentDefinitionSecretMaterializerTests
{
    [Fact]
    public async Task MaterializeAsync_NoSecretPlaceholders_ReturnsDefinitionUnchanged_AndProviderNotCalled()
    {
        var definition = LoadDefinition("""{ "kind": "prompt", "name": "a", "instructions": "hello" }""");
        var original = definition.ToJson();
        var provider = new FakeSecretProvider();

        var result = await new AgentDefinitionSecretMaterializer().MaterializeAsync(
            definition, provider, CancellationToken.None, Manifest(definition));

        Assert.Same(definition, result.Definition);
        Assert.Equal(original, result.Definition.ToJson());
        Assert.Equal(0, provider.CallCount);
        Assert.False(result.Resolver.TryResolve("${SECRET:any}", out _));
    }

    [Fact]
    public async Task MaterializeAsync_SinglePlaceholder_CallsProviderAndRewritesToReferenceToken()
    {
        var definition = WithModelSecret();
        var provider = new FakeSecretProvider();
        provider.Secrets["GithubApiToken"] = ToSecureString("plain-secret-value");

        var result = await new AgentDefinitionSecretMaterializer().MaterializeAsync(
            definition, provider, CancellationToken.None, Manifest(definition));

        var request = Assert.Single(provider.Requests);
        Assert.Equal("GithubApiToken", request.SecretName);
        Assert.IsType<GitHubLoginSecretSource>(request.DefaultSecretSource);

        var usage = Assert.Single(new SecretUsageScanner().Scan(result.Definition));
        Assert.Matches("^[0-9a-f]{32}$", usage.SecretName);
        Assert.DoesNotContain("GithubApiToken", result.Definition.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("plain-secret-value", result.Definition.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaterializeAsync_ReturnedResolver_TryResolveHandle_ReturnsSecureStringRetriever()
    {
        var definition = WithModelSecret();
        var provider = new FakeSecretProvider();
        provider.Secrets["GithubApiToken"] = ToSecureString("plain-secret-value");

        var result = await new AgentDefinitionSecretMaterializer().MaterializeAsync(
            definition, provider, CancellationToken.None, Manifest(definition));

        var token = Regex.Match(result.Definition.ToJson(), "\\$\\{SECRET:[^}]+\\}").Value;
        Assert.True(result.Resolver.TryResolve(token, out var retriever));
        using var secret = await retriever.Secret(CancellationToken.None);
        Assert.Equal("plain-secret-value", Phantom.Workspaces.Llm.Secrets.SecureStringMarshal.Use(secret, plain => plain));
    }

    [Fact]
    public async Task MaterializeAsync_ReturnedResolver_TryResolveUnknownToken_ReturnsFalse()
    {
        var definition = WithModelSecret();
        var provider = new FakeSecretProvider();
        provider.Secrets["GithubApiToken"] = ToSecureString("plain-secret-value");

        var result = await new AgentDefinitionSecretMaterializer().MaterializeAsync(
            definition, provider, CancellationToken.None, Manifest(definition));

        Assert.False(result.Resolver.TryResolve("${SECRET:unknown}", out _));
    }

    [Fact]
    public async Task MaterializeAsync_ProviderReturnsNull_ThrowsSecretMaterializationRefusedException()
    {
        var definition = WithModelSecret();
        var provider = new FakeSecretProvider { ReturnNull = true };

        await Assert.ThrowsAsync<SecretMaterializationRefusedException>(() =>
            new AgentDefinitionSecretMaterializer().MaterializeAsync(
                definition, provider, CancellationToken.None, Manifest(definition)));
    }

    [Fact]
    public async Task MaterializeAsync_ProviderReturnsFailureForRequestedSecret_ThrowsSecretMaterializationFailedException()
    {
        var definition = WithModelSecret("${SECRET:MissingSecret}");
        var provider = new FakeSecretProvider();
        provider.Failures.Add(new SecretRequestFailure("MissingSecret", "missing", SecretRequestFailureReason.DoesntExist));

        var ex = await Assert.ThrowsAsync<SecretMaterializationFailedException>(() =>
            new AgentDefinitionSecretMaterializer().MaterializeAsync(
                definition, provider, CancellationToken.None, Manifest(definition)));

        Assert.Equal("MissingSecret", Assert.Single(ex.Failures).SecretName);
    }

    [Fact]
    public async Task MaterializeAsync_ProviderReturnsFailureForRequestedSecret_ExceptionCarriesAllFailures()
    {
        var definition = WithTwoSecrets();
        var provider = new FakeSecretProvider();
        provider.Failures.Add(new SecretRequestFailure("FirstSecret", "first failed", SecretRequestFailureReason.Other));
        provider.Failures.Add(new SecretRequestFailure("SecondSecret", "second failed", SecretRequestFailureReason.Other));

        var ex = await Assert.ThrowsAsync<SecretMaterializationFailedException>(() =>
            new AgentDefinitionSecretMaterializer().MaterializeAsync(
                definition, provider, CancellationToken.None, Manifest(definition)));

        Assert.Equal(2, ex.Failures.Count);
    }

    [Fact]
    public async Task MaterializeAsync_ExceptionMessageContainsNoSecretValue()
    {
        const string secretValue = "super-secret-value";
        var definition = WithModelSecret();
        var provider = new FakeSecretProvider();
        provider.Secrets["GithubApiToken"] = ToSecureString(secretValue);
        provider.Failures.Add(new SecretRequestFailure("GithubApiToken", "github failed", SecretRequestFailureReason.Other));

        var ex = await Assert.ThrowsAsync<SecretMaterializationFailedException>(() =>
            new AgentDefinitionSecretMaterializer().MaterializeAsync(
                definition, provider, CancellationToken.None, Manifest(definition)));

        Assert.DoesNotContain(secretValue, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretValue, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaterializeAsync_SecretInToolOptions_RewritesToolOptionToReferenceToken()
    {
        var definition = LoadDefinition("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "tools": [ { "name": "t0", "kind": "mcp", "description": "tool ${SECRET:ToolSecret}" } ]
        }
        """);
        var provider = new FakeSecretProvider();
        provider.Secrets["ToolSecret"] = ToSecureString("tool-secret-value");

        await new AgentDefinitionSecretMaterializer().MaterializeAsync(definition, provider, CancellationToken.None, Manifest(definition));

        Assert.DoesNotContain("ToolSecret", definition.ToJson(), StringComparison.Ordinal);
        Assert.Contains("${SECRET:", definition.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaterializeAsync_SecretInSystemPrompt_RewritesToReferenceToken()
    {
        var definition = LoadDefinition("""{ "kind": "prompt", "name": "a", "instructions": "Use ${SECRET:SystemSecret}" }""");
        var provider = new FakeSecretProvider();
        provider.Secrets["SystemSecret"] = ToSecureString("system-secret-value");

        await new AgentDefinitionSecretMaterializer().MaterializeAsync(definition, provider, CancellationToken.None, Manifest(definition));

        Assert.DoesNotContain("SystemSecret", definition.ToJson(), StringComparison.Ordinal);
        Assert.Contains("${SECRET:", definition.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaterializeAsync_PlaceholderNeverSilentlyDropped_OnAnyFailurePath()
    {
        var definition = WithModelSecret("${SECRET:MissingSecret}");
        var provider = new FakeSecretProvider { ReturnNull = true };

        await Assert.ThrowsAsync<SecretMaterializationRefusedException>(() =>
            new AgentDefinitionSecretMaterializer().MaterializeAsync(definition, provider, CancellationToken.None, Manifest(definition)));

        Assert.Contains("${SECRET:MissingSecret}", definition.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaterializeAsync_DefinitionNeverCarriesPlaintextSecret()
    {
        const string secretValue = "plain-secret-value";
        var definition = WithModelSecret();
        var provider = new FakeSecretProvider();
        provider.Secrets["GithubApiToken"] = ToSecureString(secretValue);

        await new AgentDefinitionSecretMaterializer().MaterializeAsync(definition, provider, CancellationToken.None, Manifest(definition));

        Assert.DoesNotContain(secretValue, definition.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaterializeAsync_CandidateSources_IncludeEnumeratedCredentialStoreAndLoginSources()
    {
        var definition = WithModelSecret("${SECRET:ApiKey}");
        var provider = new FakeSecretProvider();
        provider.Secrets["ApiKey"] = ToSecureString("secret");
        var store = new FakePlatformSecretStore(["ApiKey", "Other"]);

        await new AgentDefinitionSecretMaterializer(platformSecretStore: store).MaterializeAsync(
            definition, provider, CancellationToken.None, Manifest(definition));

        var request = Assert.Single(provider.Requests);
        Assert.Contains(request.CandidateSecretSources, s => s is CredentialStoreSecretSource { CredentialName: "ApiKey" });
        Assert.Contains(request.CandidateSecretSources, s => s is GitHubLoginSecretSource);
        Assert.Contains(request.CandidateSecretSources, s => s is AwsLoginSecretSource);
        Assert.Contains(request.CandidateSecretSources, s => s is AzureLoginSecretSource);
        Assert.Equal(new CredentialStoreSecretSource("ApiKey"), request.DefaultSecretSource);
    }

    [Fact]
    public async Task AgentDefinitionSecretMaterializer_McpToolKeyConnectionApiKey_IsRewrittenToHandle()
    {
        // #1398: an McpTool "key" connection apiKey ${SECRET:Name} is covered by the scanner and must
        // be rewritten to an opaque ${SECRET:<handle>} handle registered with the resolver.
        var definition = WithMcpKeySecret("${SECRET:McpApiKey}");
        var provider = new FakeSecretProvider();
        provider.Secrets["McpApiKey"] = ToSecureString("mcp-plain-secret");

        var result = await new AgentDefinitionSecretMaterializer().MaterializeAsync(
            definition, provider, CancellationToken.None, Manifest(definition));

        var request = Assert.Single(provider.Requests);
        Assert.Equal("McpApiKey", request.SecretName);

        var usage = Assert.Single(new SecretUsageScanner().Scan(result.Definition));
        Assert.Matches("^[0-9a-f]{32}$", usage.SecretName);
        Assert.DoesNotContain("McpApiKey", result.Definition.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("mcp-plain-secret", result.Definition.ToJson(), StringComparison.Ordinal);

        var token = Regex.Match(result.Definition.ToJson(), "\\$\\{SECRET:[^}]+\\}").Value;
        Assert.True(result.Resolver.TryResolve(token, out var retriever));
        using var secret = await retriever.Secret(CancellationToken.None);
        Assert.Equal("mcp-plain-secret", Phantom.Workspaces.Llm.Secrets.SecureStringMarshal.Use(secret, plain => plain));
    }

    [Fact]
    public async Task AgentDefinitionSecretMaterializer_NullManifest_ScansAndRewritesDefinition()
    {
        var definition = WithModelSecret();
        var provider = new FakeSecretProvider();
        provider.Secrets["GithubApiToken"] = ToSecureString("plain-secret-value");

        var result = await new AgentDefinitionSecretMaterializer().MaterializeAsync(
            definition,
            provider,
            CancellationToken.None);

        var request = Assert.Single(provider.Requests);
        Assert.Equal("GithubApiToken", request.SecretName);

        var usage = Assert.Single(new SecretUsageScanner().Scan(result.Definition));
        Assert.Matches("^[0-9a-f]{32}$", usage.SecretName);
        Assert.DoesNotContain("GithubApiToken", result.Definition.ToJson(), StringComparison.Ordinal);

        var token = Regex.Match(result.Definition.ToJson(), "\\$\\{SECRET:[^}]+\\}").Value;
        Assert.True(result.Resolver.TryResolve(token, out _));
    }

    [Fact]
    public async Task AgentDefinitionSecretMaterializer_SessionReopenWithLineage_RecomputesManifestScopeHash_MatchesPriorGrant()
    {
        var manifest = Manifest(WithModelSecret());
        var scanned = WithModelSecret();
        var usage = Assert.Single(new SecretUsageScanner().Scan(scanned));
        var expectedManifestIdentityHash = new AgentManifestSecretUseMemoryFactory()
            .Build(manifest, usage.SecretName, usage.JsonPath)
            .Single(m => m.Scope == SecretUseScope.ManifestIdentity)
            .Hash;

        // A manifest-less session definition carrying the origin manifest lineage metadata.
        var sessionDefinition = WithModelSecret();
        sessionDefinition.Metadata = new Dictionary<string, object>
        {
            [AgentManifestSecretUseMemoryFactory.OriginManifestIdMetadataKey] =
                "11111111-1111-1111-1111-111111111111",
            [AgentManifestSecretUseMemoryFactory.OriginManifestContentHashMetadataKey] =
                AgentManifestSecretUseMemoryFactory.ComputeManifestContentHash(manifest),
        };

        var provider = new FakeSecretProvider();
        provider.Secrets["GithubApiToken"] = ToSecureString("plain-secret-value");

        await new AgentDefinitionSecretMaterializer().MaterializeAsync(
            sessionDefinition,
            provider,
            CancellationToken.None,
            manifest: null,
            agentSessionId: "reopened-session");

        var request = Assert.Single(provider.Requests);
        var manifestIdentityMemory = Assert.Single(
            request.Memories, m => m.Scope == SecretUseScope.ManifestIdentity);
        Assert.Equal(expectedManifestIdentityHash, manifestIdentityMemory.Hash);
    }

    [Fact]
    public async Task AgentDefinitionSecretMaterializer_ThisSessionGrant_DoesNotMatchOtherSession()
    {
        var provider = new FakeSecretProvider();
        provider.Secrets["GithubApiToken"] = ToSecureString("plain-secret-value");

        await new AgentDefinitionSecretMaterializer().MaterializeAsync(
            WithModelSecret(), provider, CancellationToken.None, manifest: null, agentSessionId: "session-A");
        await new AgentDefinitionSecretMaterializer().MaterializeAsync(
            WithModelSecret(), provider, CancellationToken.None, manifest: null, agentSessionId: "session-B");

        var sessionAHash = provider.Requests[0].Memories
            .Single(m => m.Scope == SecretUseScope.SessionIdentity).Hash;
        var sessionBHash = provider.Requests[1].Memories
            .Single(m => m.Scope == SecretUseScope.SessionIdentity).Hash;

        Assert.NotEqual(sessionAHash, sessionBHash);
    }

    private static AgentDefinition WithMcpKeySecret(string secret = "${SECRET:McpApiKey}") => LoadDefinition($$"""
    {
      "kind": "prompt",
      "name": "test-agent",
      "model": { "id": "m", "provider": "echo", "apiType": "Echo" },
      "tools": [
        {
          "kind": "mcp",
          "name": "github",
          "serverName": "github",
          "connection": { "kind": "key", "endpoint": "https://api.githubcopilot.com/mcp/", "apiKey": "{{secret}}" },
          "approvalMode": { "kind": "never" }
        }
      ]
    }
    """);

    private static AgentDefinition WithModelSecret(string secret = "${SECRET:GithubApiToken}") => LoadDefinition($$"""
    {
      "kind": "prompt",
      "name": "test-agent",
      "model": {
        "id": "m",
        "provider": "p",
        "apiType": "OpenAI",
        "options": { "additionalProperties": { "ApiToken": "{{secret}}" } }
      }
    }
    """);

    private static AgentDefinition WithTwoSecrets() => LoadDefinition("""
    {
      "kind": "prompt",
      "name": "test-agent",
      "instructions": "Use ${SECRET:FirstSecret}",
      "additionalInstructions": "Then ${SECRET:SecondSecret}"
    }
    """);

    private static AgentManifest Manifest(AgentDefinition definition) => AgentManifestLoader.LoadManifestFromJson("""
    {
      "name": "test-manifest",
      "displayName": "Test Manifest",
      "metadata": { "entity-id": "11111111-1111-1111-1111-111111111111" },
      "template": {
        "kind": "prompt",
        "name": "test-agent",
        "model": { "id": "m", "provider": "echo", "apiType": "Echo" }
      }
    }
    """);

    private static AgentDefinition LoadDefinition(string json)
        => AgentDefinition.FromJson(json) ?? throw new InvalidOperationException("Failed to load definition.");

    private static SecureString ToSecureString(string value)
    {
        var secure = new SecureString();
        foreach (var ch in value)
        {
            secure.AppendChar(ch);
        }

        secure.MakeReadOnly();
        return secure;
    }

    private sealed class FakeSecretProvider : ISecretProvider
    {
        public int CallCount { get; private set; }
        public bool ReturnNull { get; set; }
        public List<SecretRequest> Requests { get; } = [];
        public Dictionary<string, SecureString> Secrets { get; } = [];
        public List<SecretRequestFailure> Failures { get; } = [];

        public Task<RequestSecretsResult?> RequestSecretsAsync(IReadOnlyList<SecretRequest> requests, CancellationToken cancellationToken)
        {
            this.CallCount++;
            this.Requests.AddRange(requests);
            if (this.ReturnNull)
            {
                return Task.FromResult<RequestSecretsResult?>(null);
            }

            var retrievers = requests
                .Where(request => this.Secrets.ContainsKey(request.SecretName))
                .Select(request => new SecretRetriever
                {
                    SecretName = request.SecretName,
                    Secret = _ => Task.FromResult(this.Secrets[request.SecretName]),
                })
                .ToArray();

            return Task.FromResult<RequestSecretsResult?>(new RequestSecretsResult(retrievers, this.Failures));
        }
    }

    private sealed class FakePlatformSecretStore(IReadOnlyList<string> names) : IPlatformSecretStore
    {
        public Task<SecureString?> ReadAsync(string name, CancellationToken ct) => Task.FromResult<SecureString?>(null);
        public Task WriteAsync(string name, SecureString value, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(string name, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken ct) => Task.FromResult(names);
    }
}
