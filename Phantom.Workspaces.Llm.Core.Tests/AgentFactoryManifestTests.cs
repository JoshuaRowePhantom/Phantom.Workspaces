using AgentSchema;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentFactoryManifestTests
{
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
                ToolResourceFactory = new FixedToolResourceFactory(),
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
                ToolResourceFactory = new FixedToolResourceFactory(),
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
                    ToolResourceFactory = new FixedToolResourceFactory(),
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
                ToolResourceFactory = new FixedToolResourceFactory(),
            });

        var promptAgent = Assert.IsType<PromptAgent>(definition);
        Assert.Equal("example", promptAgent.Name);
        Assert.NotSame(manifest.Template, definition);
    }
}
