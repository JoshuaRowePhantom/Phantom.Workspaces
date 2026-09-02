using System.Reflection;
using System.Text.Json;
using Json.Schema;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class McpServerSchemaTests
{
    private const string ResourceName = "Phantom.Workspaces.Llm.Core.JsonSchemas.mcp-server.json";

    private static readonly JsonSchema Schema = LoadSchema();

    [Fact]
    public void McpServerSchema_AcceptsAnonymousKeyAndOAuthConnections()
    {
        Assert.True(Evaluate("""
        {
          "serverName": "anonymous-server",
          "connection": {
            "kind": "Anonymous",
            "endpoint": "https://example.com/mcp/"
          }
        }
        """));

        Assert.True(Evaluate("""
        {
          "serverName": "github",
          "connection": {
            "kind": "key",
            "endpoint": "https://api.githubcopilot.com/mcp/",
            "apiKey": "${GITHUB_TOKEN}"
          }
        }
        """));

        Assert.True(Evaluate("""
        {
          "serverName": "oauth-server",
          "connection": {
            "kind": "oauth",
            "endpoint": "https://mcp.example.com/",
            "clientId": "${SECRET:ExampleClientId}",
            "scopes": ["read", "write"]
          }
        }
        """));
    }

    [Fact]
    public void McpServerSchema_RejectsUnknownKindAndAdditionalProperties()
    {
        Assert.False(Evaluate("""
        {
          "serverName": "bad-kind",
          "connection": {
            "kind": "reference",
            "endpoint": "https://mcp.example.com/"
          }
        }
        """));

        Assert.False(Evaluate("""
        {
          "serverName": "bad-prop",
          "connection": {
            "kind": "Anonymous",
            "endpont": "https://mcp.example.com/"
          }
        }
        """));

        Assert.False(Evaluate("""
        {
          "serverName": "missing-apikey",
          "connection": {
            "kind": "key",
            "endpoint": "https://api.githubcopilot.com/mcp/"
          }
        }
        """));
    }

    [Fact]
    public void McpServerSchema_EmbeddedConnectionExamples_AllValidateAgainstSchema()
    {
        var (schema, examples) = AgentDefinitionMcpToolTests.LoadMcpConnectionSchema(
            ResourceName,
            root => root
                .GetProperty("properties")
                .GetProperty("connection"));

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

    private static bool Evaluate(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = Schema.Evaluate(
            document.RootElement,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        return result.IsValid;
    }

    private static JsonSchema LoadSchema()
    {
        var assembly = typeof(AgentDefinitionJsonSchema).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"Embedded schema resource '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }
}
