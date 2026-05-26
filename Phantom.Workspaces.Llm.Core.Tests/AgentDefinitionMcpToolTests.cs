using System.Text.Json;
using AgentSchema;
using Json.Schema;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentDefinitionMcpToolTests
{
    [Fact]
    public void AgentDefinitionJsonSchema_AcceptsMcpTool()
    {
        var instance = ParseElement("""
        {
          "kind": "prompt",
          "name": "mcp-agent",
          "model": {
            "id": "echo",
            "provider": "echo",
            "apiType": "Echo"
          },
          "tools": [
            {
              "name": "filesystem",
              "kind": "mcp",
              "description": "Filesystem MCP server",
              "connection": {
                "kind": "Anonymous",
                "endpoint": "http://localhost:3000"
              },
              "serverName": "filesystem",
              "serverDescription": "Filesystem MCP server",
              "approvalMode": {
                "kind": "never"
              },
              "allowedTools": ["read_file"]
            }
          ]
        }
        """);

        var result = AgentDefinitionJsonSchema.Value.Evaluate(
            instance,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void LoadAgentFromJson_RoundTripsMcpTool()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "mcp-agent",
          "model": {
            "id": "echo",
            "provider": "echo",
            "apiType": "Echo"
          },
          "tools": [
            {
              "name": "filesystem",
              "kind": "mcp",
              "description": "Filesystem MCP server",
              "connection": {
                "kind": "Anonymous",
                "endpoint": "http://localhost:3000"
              },
              "serverName": "filesystem",
              "serverDescription": "Filesystem MCP server",
              "approvalMode": {
                "kind": "never"
              },
              "allowedTools": ["read_file"]
            }
          ]
        }
        """);

        var prompt = Assert.IsType<PromptAgent>(agent);
        var tool = Assert.Single(prompt.Tools);
        var mcpTool = Assert.IsType<McpTool>(tool);

        Assert.Equal("filesystem", mcpTool.Name);
        Assert.Equal("mcp", mcpTool.Kind);
        Assert.Equal("filesystem", mcpTool.ServerName);
        Assert.Equal("Filesystem MCP server", mcpTool.ServerDescription);
        Assert.Equal(new[] { "read_file" }, mcpTool.AllowedTools);
    }

    [Fact]
    public void AgentDefinitionJsonSchema_AcceptsMcpToolWithApiKeyConnection()
    {
        var instance = ParseElement("""
        {
          "kind": "prompt",
          "name": "mcp-agent",
          "model": {
            "id": "echo",
            "provider": "echo",
            "apiType": "Echo"
          },
          "tools": [
            {
              "name": "github",
              "kind": "mcp",
              "connection": {
                "kind": "key",
                "endpoint": "https://api.githubcopilot.com/mcp/",
                "apiKey": "${GITHUB_TOKEN}"
              },
              "serverName": "github",
              "approvalMode": { "kind": "never" }
            }
          ]
        }
        """);

        var result = AgentDefinitionJsonSchema.Value.Evaluate(
            instance,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void LoadAgentFromJson_RoundTripsMcpToolWithApiKeyConnection()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "mcp-agent",
          "model": {
            "id": "echo",
            "provider": "echo",
            "apiType": "Echo"
          },
          "tools": [
            {
              "name": "github",
              "kind": "mcp",
              "connection": {
                "kind": "key",
                "endpoint": "https://api.githubcopilot.com/mcp/",
                "apiKey": "${GITHUB_TOKEN}"
              },
              "serverName": "github",
              "serverDescription": "GitHub MCP server",
              "approvalMode": { "kind": "never" }
            }
          ]
        }
        """);

        var prompt = Assert.IsType<PromptAgent>(agent);
        var tool = Assert.Single(prompt.Tools);
        var mcpTool = Assert.IsType<McpTool>(tool);

        Assert.Equal("github", mcpTool.Name);
        Assert.Equal("github", mcpTool.ServerName);

        var conn = Assert.IsType<AgentSchema.ApiKeyConnection>(mcpTool.Connection);
        Assert.Equal("https://api.githubcopilot.com/mcp/", conn.Endpoint);
        Assert.Equal("${GITHUB_TOKEN}", conn.ApiKey);
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
