using AgentSchema;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Secrets;
using System.Security;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentFactoryManifestTests
{
    private static FixedToolResourceFactory CreateFixedFactory()
    {
        return new FixedToolResourceFactory(
            new Dictionary<(string Id, string Name), Tool>
            {
                [("fixed", "workspace-entity")] = new CustomTool { Kind = "workspace-entity", Name = "workspace-entity" },
                [("fixed", "filesystem")] = new CustomTool { Kind = "filesystem", Name = "filesystem" },
            });
    }

    private const string ManifestJson = """
    {
      "name": "example",
      "displayName": "Example Manifest",
      "template": {
        "kind": "prompt",
        "name": "example",
        "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
      },
      "resources": [
        { "kind": "tool", "id": "fixed", "name": "workspace-entity" },
        { "kind": "tool", "id": "fixed", "name": "filesystem" }
      ]
    }
    """;

    [Fact]
    public async Task CreateAgentDefinitionAsync_ResolvesToolResourcesIntoTemplateTools()
    {
        var manifest = AgentManifestLoader.LoadManifestFromJson(ManifestJson);

        var definition = await AgentFactory.CreateAgentDefinitionAsync(
            new CreateAgentDefinitionRequest
            {
                AgentManifest = manifest,
                ToolResourceFactory = CreateFixedFactory(),
            });

        var promptAgent = Assert.IsType<PromptAgent>(definition);
        var toolKinds = promptAgent.Tools!.Select(static tool => tool.Kind).ToArray();
        Assert.Equal(new[] { "workspace-entity", "filesystem" }, toolKinds);
    }

    [Fact]
    public async Task CreateAgentDefinitionAsync_DoesNotMutateManifestTemplate()
    {
        var manifest = AgentManifestLoader.LoadManifestFromJson(ManifestJson);

        await AgentFactory.CreateAgentDefinitionAsync(
            new CreateAgentDefinitionRequest
            {
                AgentManifest = manifest,
                ToolResourceFactory = CreateFixedFactory(),
            });

        var templateAgent = Assert.IsType<PromptAgent>(manifest.Template);
        Assert.True(templateAgent.Tools is null || templateAgent.Tools.Count == 0);
    }

    [Fact]
    public async Task CreateAgentDefinitionAsync_WhenResourceUnresolved_Throws()
    {
        var manifest = AgentManifestLoader.LoadManifestFromJson("""
        {
          "name": "example",
          "displayName": "Example Manifest",
          "template": {
            "kind": "prompt",
            "name": "example",
            "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
          },
          "resources": [
            { "kind": "tool", "id": "mcp-server-entity", "name": "github" }
          ]
        }
        """);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AgentFactory.CreateAgentDefinitionAsync(
                new CreateAgentDefinitionRequest
                {
                    AgentManifest = manifest,
                    ToolResourceFactory = CreateFixedFactory(),
                }));

        Assert.Contains("mcp-server-entity:github", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAgentDefinitionAsync_WithNoResources_ReturnsClonedTemplate()
    {
        var manifest = AgentManifestLoader.LoadManifestFromJson("""
        {
          "name": "example",
          "displayName": "Example Manifest",
          "template": {
            "kind": "prompt",
            "name": "example",
            "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
          }
        }
        """);

        var definition = await AgentFactory.CreateAgentDefinitionAsync(
            new CreateAgentDefinitionRequest
            {
                AgentManifest = manifest,
                ToolResourceFactory = CreateFixedFactory(),
            });

        var promptAgent = Assert.IsType<PromptAgent>(definition);
        Assert.Equal("example", promptAgent.Name);
        Assert.NotSame(manifest.Template, definition);
    }

    [Fact]
    public async Task AgentFactory_CreateAgentChat_ManifestPath_DoesNotMaterializeSecretsBeforeDefinition()
    {
        var manifest = LoadSecretManifest();

        // The manifest → definition projection must NOT touch secrets: the placeholder survives.
        var definition = await AgentFactory.CreateAgentDefinitionAsync(
            new CreateAgentDefinitionRequest { AgentManifest = manifest });
        Assert.Contains("${SECRET:GitHubToken}", definition.ToJson(), StringComparison.Ordinal);

        // Going through chat creation materializes exactly once, on the resulting definition.
        var provider = new CountingSecretProvider();
        provider.Secrets["GitHubToken"] = ToSecureString("super-secret-token");

        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentManifest = manifest,
            AgentServices = new AgentServices
            {
                SecretProvider = provider,
                ChatClientOverride = new DeterministicTestChatClient(),
            },
            PersistenceStoreFactory = (_, _) => ValueTask.FromResult<IAgentPersistenceStore>(new InMemoryAgentPersistenceStore()),
        });

        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task AgentFactory_CreateAgentDefinitionFromManifest_StampsOriginManifestLineage()
    {
        const string entityId = "33333333-3333-3333-3333-333333333333";
        var manifest = AgentManifestLoader.LoadManifestFromJson($$"""
        {
          "name": "example",
          "displayName": "Example Manifest",
          "metadata": { "entity-id": "{{entityId}}" },
          "template": {
            "kind": "prompt",
            "name": "example",
            "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
          }
        }
        """);

        var definition = await AgentFactory.CreateAgentDefinitionAsync(
            new CreateAgentDefinitionRequest { AgentManifest = manifest });

        Assert.NotNull(definition.Metadata);
        Assert.Equal(
            entityId,
            definition.Metadata![AgentManifestSecretUseMemoryFactory.OriginManifestIdMetadataKey]);
        Assert.Equal(
            AgentManifestSecretUseMemoryFactory.ComputeManifestContentHash(manifest),
            definition.Metadata![AgentManifestSecretUseMemoryFactory.OriginManifestContentHashMetadataKey]);
    }

    private static AgentManifest LoadSecretManifest()
        => AgentManifestLoader.LoadManifestFromJson("""
        {
          "name": "secret-agent",
          "displayName": "Secret Agent",
          "metadata": { "entity-id": "44444444-4444-4444-4444-444444444444" },
          "template": {
            "kind": "prompt",
            "name": "secret-agent",
            "model": {
              "id": "gpt-test",
              "provider": "github-copilot",
              "connection": { "kind": "key", "apiKey": "${SECRET:GitHubToken}" }
            }
          }
        }
        """);

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

    private sealed class CountingSecretProvider : ISecretProvider
    {
        public int CallCount { get; private set; }
        public Dictionary<string, SecureString> Secrets { get; } = [];

        public Task<RequestSecretsResult?> RequestSecretsAsync(IReadOnlyList<SecretRequest> requests, CancellationToken cancellationToken)
        {
            this.CallCount++;
            var retrievers = requests
                .Where(request => this.Secrets.ContainsKey(request.SecretName))
                .Select(request => new SecretRetriever
                {
                    SecretName = request.SecretName,
                    Secret = _ => Task.FromResult(this.Secrets[request.SecretName]),
                })
                .ToArray();

            return Task.FromResult<RequestSecretsResult?>(new RequestSecretsResult(retrievers, []));
        }
    }
}
