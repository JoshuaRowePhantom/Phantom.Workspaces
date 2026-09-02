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
        var tool = Assert.Single(prompt.Tools!);
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
        var tool = Assert.Single(prompt.Tools!);
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

    [Fact]
    public void CreateStdioTransport_WithEnvParameters_SetsEnvironmentVariables()
    {
        var endpoint = new Uri("stdio://?command=my-server&env=KUSTO_SERVICE_URI%3Dhttps%3A%2F%2Fcluster.kusto.windows.net");

        var options = Phantom.Workspaces.Llm.McpToolContextProvider.BuildStdioTransportOptions(endpoint, "kusto");

        Assert.NotNull(options.EnvironmentVariables);
        Assert.Equal(
            "https://cluster.kusto.windows.net",
            options.EnvironmentVariables!["KUSTO_SERVICE_URI"]);
    }

    [Fact]
    public void CreateStdioTransport_WithMultipleEnvParameters_SetsAllVariables()
    {
        var endpoint = new Uri("stdio://?command=my-server&env=FOO%3Dbar&env=BAZ%3Dqux%3Dwith%3Dequals");

        var options = Phantom.Workspaces.Llm.McpToolContextProvider.BuildStdioTransportOptions(endpoint, "server");

        Assert.NotNull(options.EnvironmentVariables);
        Assert.Equal("bar", options.EnvironmentVariables!["FOO"]);
        Assert.Equal("qux=with=equals", options.EnvironmentVariables!["BAZ"]);
    }

    [Fact]
    public void CreateStdioTransport_WithoutEnvParameters_LeavesEnvironmentUnset()
    {
        var endpoint = new Uri("stdio://?command=my-server");

        var options = Phantom.Workspaces.Llm.McpToolContextProvider.BuildStdioTransportOptions(endpoint, "server");

        Assert.Null(options.EnvironmentVariables);
    }

    [Fact]
    public void CreateStdioTransport_WithMalformedEnvEntry_Throws()
    {
        var endpoint = new Uri("stdio://?command=my-server&env=NO_SEPARATOR_HERE");

        var exception = Assert.Throws<InvalidOperationException>(
            () => Phantom.Workspaces.Llm.McpToolContextProvider.BuildStdioTransportOptions(endpoint, "server"));

        Assert.Contains("NAME=value", exception.Message);
    }
}
