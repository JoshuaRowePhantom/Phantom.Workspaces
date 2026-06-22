using System.Text.Json;
using AgentSchema;
using Json.Schema;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentManifestLoaderTests
{
    private const string ValidManifestJson = """
    {
      "name": "example",
      "displayName": "Example Manifest",
      "description": "An example manifest.",
      "template": {
        "kind": "prompt",
        "name": "example",
        "model": {
          "id": "echo",
          "provider": "echo",
          "apiType": "Echo"
        }
      },
      "resources": [
        {
          "kind": "tool",
          "id": "mcp-server-entity",
          "name": "github"
        },
        {
          "kind": "tool",
          "id": "fixed",
          "name": "workspace-entity"
        }
      ]
    }
    """;

    [Fact]
    public void AgentManifestJsonSchema_AcceptsValidManifest()
    {
        using var document = JsonDocument.Parse(ValidManifestJson);
        var result = AgentManifestJsonSchema.Value.Evaluate(
            document.RootElement,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void AgentManifestJsonSchema_RejectsManifestWithoutTemplate()
    {
        using var document = JsonDocument.Parse("""
        {
          "name": "no-template",
          "displayName": "No Template"
        }
        """);
        var result = AgentManifestJsonSchema.Value.Evaluate(
            document.RootElement,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void LoadManifestFromJson_RoundTripsTemplateAndResources()
    {
        var manifest = AgentManifestLoader.LoadManifestFromJson(ValidManifestJson);

        Assert.Equal("example", manifest.Name);
        Assert.Equal("Example Manifest", manifest.DisplayName);

        var template = Assert.IsType<PromptAgent>(manifest.Template);
        Assert.Equal("example", template.Name);
        Assert.Equal("echo", template.Model?.Id);

        Assert.Equal(2, manifest.Resources.Count);
        var toolResources = manifest.Resources.OfType<ToolResource>().ToArray();
        Assert.Equal(2, toolResources.Length);

        var mcpResource = Assert.Single(toolResources, resource => resource.Id == "mcp-server-entity");
        Assert.Equal("github", mcpResource.Name);

        var fixedResource = Assert.Single(toolResources, resource => resource.Id == "fixed");
        Assert.Equal("workspace-entity", fixedResource.Name);
    }

    [Fact]
    public void LoadManifestFromJson_WhenTemplateMissing_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => AgentManifestLoader.LoadManifestFromJson("""
            {
              "name": "no-template",
              "displayName": "No Template"
            }
            """));

        Assert.Contains("AgentManifest schema", exception.Message, StringComparison.Ordinal);
    }
}
