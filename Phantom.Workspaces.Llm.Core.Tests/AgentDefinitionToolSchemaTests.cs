using System.Text.Json;
using Json.Schema;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentDefinitionToolSchemaTests
{
    [Fact]
    public void AgentDefinitionJsonSchema_AcceptsInlineAgentDefinitionTool()
    {
        var instance = ParseElement("""
        {
          "kind": "prompt",
          "name": "dispatcher",
          "model": {
            "id": "echo",
            "provider": "echo"
          },
          "tools": [
            {
              "kind": "agent-definition",
              "name": "default",
              "description": "The default sub-agent",
              "definition": {
                "kind": "prompt",
                "name": "sub",
                "model": { "id": "echo", "provider": "echo" }
              }
            }
          ]
        }
        """);

        var result = Evaluate(instance);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void AgentDefinitionJsonSchema_AcceptsManifestReferenceAgentDefinitionTool()
    {
        var instance = ParseElement("""
        {
          "kind": "prompt",
          "name": "dispatcher",
          "model": {
            "id": "echo",
            "provider": "echo"
          },
          "tools": [
            {
              "kind": "agent-definition",
              "name": "helper",
              "description": "A referenced sub-agent",
              "manifest-reference": ["agent-manifests", "helper"]
            }
          ]
        }
        """);

        var result = Evaluate(instance);

        Assert.True(result.IsValid);
    }

    private static EvaluationResults Evaluate(JsonElement instance)
    {
        return AgentDefinitionJsonSchema.Value.Evaluate(
            instance,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
