using System.Text.Json;
using AgentSchema;
using Json.Schema;
using GitHub.Copilot;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentDefinitionGithubCliBuiltinToolsTests
{
    [Fact]
    public void AgentDefinitionJsonSchema_GithubCliBuiltinToolsEntry_RoundTrips()
    {
        var json = """
        {
          "kind": "prompt",
          "name": "copilot-agent",
          "model": {
            "id": "gpt-5",
            "provider": "github-copilot",
            "apiType": "OpenAI"
          },
          "tools": [
            {
              "kind": "github-cli-builtin-tools",
              "client-mode": "empty",
              "available-tools": { "tools": ["mcp:*"] },
              "excluded-tools": { "tools": ["shell"] }
            }
          ]
        }
        """;

        var result = Evaluate(json);
        Assert.True(result.IsValid);

        var agent = AgentDefinitionLoader.LoadAgentFromJson(json);
        var promptAgent = Assert.IsType<PromptAgent>(agent);
        var tool = Assert.IsType<GitHubCliBuiltinToolsTool>(Assert.Single(promptAgent.Tools!));
        Assert.Equal(CopilotClientMode.Empty, tool.ClientMode);
        Assert.Equal(["mcp:*"], tool.AvailableTools!.Tools);
        Assert.Equal(["shell"], tool.ExcludedTools!.Tools);
    }

    [Fact]
    public void AgentDefinitionJsonSchema_GithubCliBuiltinToolsUnknownOptionKey_Rejects()
    {
        var result = Evaluate("""
        {
          "kind": "prompt",
          "name": "copilot-agent",
          "model": {
            "id": "gpt-5",
            "provider": "github-copilot",
            "apiType": "OpenAI"
          },
          "tools": [
            {
              "kind": "github-cli-builtin-tools",
              "disabledTools": ["shell"]
            }
          ]
        }
        """);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void AgentDefinitionJsonSchema_GithubCliBuiltinToolsSelectorNeitherToolsNorIsolated_Rejects()
    {
        var result = Evaluate("""
        {
          "kind": "prompt",
          "name": "copilot-agent",
          "model": {
            "id": "gpt-5",
            "provider": "github-copilot",
            "apiType": "OpenAI"
          },
          "tools": [
            {
              "kind": "github-cli-builtin-tools",
              "available-tools": {}
            }
          ]
        }
        """);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void AgentDefinitionJsonSchema_ClientModeEmptyWithoutAvailableTools_Rejects()
    {
        var result = Evaluate("""
        {
          "kind": "prompt",
          "name": "copilot-agent",
          "model": {
            "id": "gpt-5",
            "provider": "github-copilot",
            "apiType": "OpenAI"
          },
          "tools": [
            {
              "kind": "github-cli-builtin-tools",
              "client-mode": "empty"
            }
          ]
        }
        """);

        Assert.False(result.IsValid);
    }

    private static EvaluationResults Evaluate(string json)
    {
        using var document = JsonDocument.Parse(json);
        return AgentDefinitionJsonSchema.Value.Evaluate(
            document.RootElement,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });
    }
}
