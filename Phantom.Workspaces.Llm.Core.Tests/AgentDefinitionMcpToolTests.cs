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

    [Fact]
    public void AgentDefinitionJsonSchema_AcceptsMcpToolWithAnonymousConnection()
    {
        Assert.True(EvaluateAgent(McpAgentJson("""
        {
          "kind": "Anonymous",
          "endpoint": "https://example.com/mcp/"
        }
        """)));
    }

    [Fact]
    public void AgentDefinitionJsonSchema_AcceptsMcpToolWithOAuthConnection()
    {
        Assert.True(EvaluateAgent(McpAgentJson("""
        {
          "kind": "oauth",
          "endpoint": "https://mcp.example.com/"
        }
        """)));
    }

    [Fact]
    public void AgentDefinitionJsonSchema_AcceptsOAuthConnectionWithClientIdAndScopes()
    {
        Assert.True(EvaluateAgent(McpAgentJson("""
        {
          "kind": "oauth",
          "endpoint": "https://mcp.example.com/",
          "clientId": "${SECRET:ExampleClientId}",
          "scopes": ["read", "write"]
        }
        """)));
    }

    [Fact]
    public void AgentDefinitionJsonSchema_RejectsMcpConnectionWithUnknownKind()
    {
        Assert.False(EvaluateAgent(McpAgentJson("""
        {
          "kind": "reference",
          "endpoint": "https://mcp.example.com/"
        }
        """)));
    }

    [Fact]
    public void AgentDefinitionJsonSchema_RejectsMcpConnectionWithAdditionalProperty()
    {
        Assert.False(EvaluateAgent(McpAgentJson("""
        {
          "kind": "Anonymous",
          "endpont": "https://mcp.example.com/"
        }
        """)));
    }

    [Fact]
    public void AgentDefinitionJsonSchema_RejectsApiKeyConnectionMissingApiKey()
    {
        Assert.False(EvaluateAgent(McpAgentJson("""
        {
          "kind": "key",
          "endpoint": "https://api.githubcopilot.com/mcp/"
        }
        """)));
    }

    [Fact]
    public void AgentDefinitionJsonSchema_RejectsConnectionMissingEndpoint()
    {
        Assert.False(EvaluateAgent(McpAgentJson("""
        {
          "kind": "Anonymous"
        }
        """)));
    }

    [Fact]
    public void AgentDefinitionJsonSchema_RejectsMcpConnectionMissingKind()
    {
        Assert.False(EvaluateAgent(McpAgentJson("""
        {
          "endpoint": "https://mcp.example.com/"
        }
        """)));
    }

    [Fact]
    public void AgentDefinitionJsonSchema_RejectsOAuthConnectionWithNonStringScope()
    {
        Assert.False(EvaluateAgent(McpAgentJson("""
        {
          "kind": "oauth",
          "endpoint": "https://mcp.example.com/",
          "scopes": ["read", 42]
        }
        """)));
    }

    [Fact]
    public void LoadAgentFromJson_RoundTripsMcpToolWithOAuthConnection()
    {
        var json = McpAgentJson("""
        {
          "kind": "oauth",
          "endpoint": "https://mcp.example.com/",
          "clientId": "${SECRET:ExampleClientId}",
          "scopes": ["read", "write"]
        }
        """);

        var agent = AgentDefinitionLoader.LoadAgentFromJson(json);
        var prompt = Assert.IsType<PromptAgent>(agent);
        var tool = Assert.IsType<McpTool>(Assert.Single(prompt.Tools!));
        var conn = Assert.IsType<AgentSchema.OAuthConnection>(tool.Connection);

        Assert.Equal("oauth", conn.Kind);
        Assert.Equal("https://mcp.example.com/", conn.Endpoint);
        Assert.Equal("${SECRET:ExampleClientId}", conn.ClientId);
        Assert.Equal(new[] { "read", "write" }, conn.Scopes);

        // Round-trip: serialize and reload; the OAuth connection must survive unchanged.
        var reserialized = conn.ToJson();
        var reloaded = AgentSchema.OAuthConnection.FromJson(reserialized);

        Assert.Equal("oauth", reloaded.Kind);
        Assert.Equal("https://mcp.example.com/", reloaded.Endpoint);
        Assert.Equal("${SECRET:ExampleClientId}", reloaded.ClientId);
        Assert.Equal(new[] { "read", "write" }, reloaded.Scopes);
    }

    [Fact]
    public void AgentDefinitionJsonSchema_EmbeddedConnectionExamples_AllValidateAgainstSchema()
    {
        var connectionSchema = LoadMcpConnectionSchema(
            "Phantom.Workspaces.Llm.Core.JsonSchemas.AgentDefinition.json",
            root => root
                .GetProperty("$defs")
                .GetProperty("mcpTool")
                .GetProperty("properties")
                .GetProperty("connection"));

        var (schema, examples) = connectionSchema;
        Assert.NotEmpty(examples);

        foreach (var example in examples)
        {
            var result = schema.Evaluate(
                example,
                new EvaluationOptions
                {
                    OutputFormat = OutputFormat.Hierarchical,
                    RequireFormatValidation = false,
                });

            Assert.True(result.IsValid, $"Embedded connection example failed validation: {example}");
        }
    }

    private static bool EvaluateAgent(string json)
    {
        var result = AgentDefinitionJsonSchema.Value.Evaluate(
            ParseElement(json),
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        return result.IsValid;
    }

    private static string McpAgentJson(string connectionJson) => $$"""
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
              "name": "server",
              "kind": "mcp",
              "connection": {{connectionJson}},
              "serverName": "server",
              "approvalMode": { "kind": "never" }
            }
          ]
        }
        """;

    internal static (Json.Schema.JsonSchema Schema, List<JsonElement> Examples) LoadMcpConnectionSchema(
        string resourceName,
        Func<JsonElement, JsonElement> navigate)
    {
        var assembly = typeof(AgentDefinitionJsonSchema).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"Embedded schema resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        var schemaText = reader.ReadToEnd();

        using var document = JsonDocument.Parse(schemaText);
        var connectionNode = navigate(document.RootElement);

        var schema = Json.Schema.JsonSchema.FromText(connectionNode.GetRawText());

        var examples = new List<JsonElement>();
        foreach (var branch in connectionNode.GetProperty("oneOf").EnumerateArray())
        {
            if (branch.TryGetProperty("examples", out var exampleArray))
            {
                foreach (var example in exampleArray.EnumerateArray())
                {
                    examples.Add(example.Clone());
                }
            }
        }

        return (schema, examples);
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
