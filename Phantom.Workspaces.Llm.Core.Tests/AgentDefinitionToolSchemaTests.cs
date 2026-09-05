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

    [Fact]
    public void ModelOptionsExecutor_RoundTrips_InEmbeddedAgentDefinition()
    {
        // Issue #1443: the model executor binding is carried in ModelOptions.AdditionalProperties, so it
        // must survive a Save/Load round-trip of the embedded agent definition without any schema change.
        // AgentSchema persists the open bag under an explicit nested `additionalProperties` object.
        var json = """
        {
          "kind": "prompt",
          "name": "dispatcher",
          "model": {
            "id": "gpt-5",
            "provider": "copilot",
            "options": { "additionalProperties": { "executor": "model-host" } }
          }
        }
        """;

        var definition = Phantom.Workspaces.Llm.PhantomAgentSchema.AgentDefinitionFromJson(json);
        var roundTripped = Phantom.Workspaces.Llm.PhantomAgentSchema.AgentDefinitionFromJson(definition.ToJson());

        var prompt = Assert.IsType<AgentSchema.PromptAgent>(roundTripped);
        Assert.NotNull(prompt.Model?.Options?.AdditionalProperties);
        Assert.True(prompt.Model!.Options!.AdditionalProperties!.TryGetValue("executor", out var executor));
        Assert.Equal("model-host", executor?.ToString());
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
