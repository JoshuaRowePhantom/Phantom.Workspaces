using System.IO;
using System.Reflection;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Tests;

// Issue #1400: the agent-manifest / agent-definition / mcp-server / agent-session schemas described
// MCP-server tool resources as vaguely "resolved at runtime" and mis-documented secret/OAuth
// handling. These tests guard that the schema description text documents the reconciled behavior:
// entity-name (not serverName) resolution against the machine / ${USER}/mcp-servers / defaults
// prefixes, the ${SECRET:Name} / ${ENV_VAR} semantics, and the OAuth connection fields.
public sealed class SchemaDocumentationTests
{
    [Fact]
    public void AgentSchemas_BuiltinSelections_DocumentInventoryAndRuntimeBoundaries()
    {
        var definition = LoadSchema(LlmCoreAssembly, "Phantom.Workspaces.Llm.Core.JsonSchemas.AgentDefinition.json");
        var manifest = LoadSchema(LlmCoreAssembly, "Phantom.Workspaces.Llm.Core.JsonSchemas.agent-manifest.json");
        foreach (var kind in new[] { "web_search", "web_request", "web", "filesystem", "workspace-entity",
                     "workspace-gui", "current-session", "agent-session", "chat-history" })
        {
            var schema = definition.GetProperty("$defs").EnumerateObject()
                .Select(property => property.Value)
                .Single(value => value.TryGetProperty("properties", out var properties)
                    && properties.TryGetProperty("kind", out var discriminator)
                    && discriminator.TryGetProperty("const", out var constant)
                    && constant.GetString() == kind);
            Assert.False(string.IsNullOrWhiteSpace(schema.GetProperty("description").GetString()));
            Assert.NotEmpty(schema.GetProperty("examples").EnumerateArray());
        }

        var resource = manifest.GetProperty("$defs").GetProperty("toolResource");
        var description = resource.GetProperty("description").GetString()!;
        foreach (var name in FixedToolResources.DefaultNames)
        {
            Assert.Contains(name, description, StringComparison.Ordinal);
        }
        Assert.Contains("not forwarded", resource.GetProperty("properties").GetProperty("options")
            .GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("diagnostic", resource.GetProperty("properties").GetProperty("id")
            .GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("GUI context", definition.GetProperty("$defs").GetProperty("workspaceGuiTool")
            .GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("in-memory", definition.GetProperty("$defs").GetProperty("filesystemTool")
            .GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    private static readonly Assembly LlmCoreAssembly = typeof(AgentManifestJsonSchema).Assembly;
    private static readonly Assembly DataCoreAssembly = typeof(IDataAccessLayer).Assembly;

    private static JsonElement LoadSchema(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        using var document = JsonDocument.Parse(reader.ReadToEnd());
        return document.RootElement.Clone();
    }

    private static string ConnectionDescription(JsonElement mcpServerSchema, string kindConst, string propertyName)
    {
        var oneOf = mcpServerSchema.GetProperty("properties").GetProperty("connection").GetProperty("oneOf");
        foreach (var connection in oneOf.EnumerateArray())
        {
            var properties = connection.GetProperty("properties");
            if (properties.GetProperty("kind").TryGetProperty("const", out var kind)
                && kind.GetString() == kindConst
                && properties.TryGetProperty(propertyName, out var property))
            {
                return property.GetProperty("description").GetString()!;
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"No '{propertyName}' property found on the '{kindConst}' connection.");
    }

    [Fact]
    public void AgentManifestSchema_McpServerEntityToolResource_DescriptionDocumentsEntityNameResolution()
    {
        var schema = LoadSchema(LlmCoreAssembly, "Phantom.Workspaces.Llm.Core.JsonSchemas.agent-manifest.json");
        var description = schema
            .GetProperty("$defs").GetProperty("toolResource")
            .GetProperty("properties").GetProperty("name")
            .GetProperty("description").GetString()!;

        Assert.Contains("mcp-server", description);
        Assert.Contains("search prefix", description);
        Assert.Contains("${USER}/mcp-servers", description);
        Assert.Contains("defaults/mcp-servers", description);
        Assert.Contains("entity name", description);
        Assert.Contains("not the", description);
    }

    [Fact]
    public void McpServerSchema_ServerName_DescriptionClarifiesItIsNotResolutionKey()
    {
        var schema = LoadSchema(LlmCoreAssembly, "Phantom.Workspaces.Llm.Core.JsonSchemas.mcp-server.json");
        var description = schema
            .GetProperty("properties").GetProperty("serverName")
            .GetProperty("description").GetString()!;

        Assert.Contains("NOT the manifest-resolution key", description);
        Assert.Contains("entity name", description);
    }

    [Fact]
    public void McpServerSchema_ApiKey_DescriptionDocumentsSecretAndEnvSemantics()
    {
        var schema = LoadSchema(LlmCoreAssembly, "Phantom.Workspaces.Llm.Core.JsonSchemas.mcp-server.json");
        var description = ConnectionDescription(schema, "key", "apiKey");

        Assert.Contains("${ENV_VAR}", description);
        Assert.Contains("${SECRET:Name}", description);
        Assert.Contains("secret store", description);
        Assert.Contains("${GITHUB_TOKEN}", description);
    }

    [Fact]
    public void McpServerSchema_OAuthConnection_DescriptionDocumentsFieldsAndSecretResolution()
    {
        var schema = LoadSchema(LlmCoreAssembly, "Phantom.Workspaces.Llm.Core.JsonSchemas.mcp-server.json");

        var clientId = ConnectionDescription(schema, "oauth", "clientId");
        var clientSecret = ConnectionDescription(schema, "oauth", "clientSecret");
        var authenticationMode = ConnectionDescription(schema, "oauth", "authenticationMode");

        Assert.Contains("${SECRET:Name}", clientId);
        Assert.Contains("secret-aware resolver", clientId);
        Assert.Contains("${SECRET:Name}", clientSecret);
        Assert.Contains("secret-aware resolver", clientSecret);
        Assert.Contains("Defaults to 'system'", authenticationMode);

        var connectionDescription = schema
            .GetProperty("properties").GetProperty("connection")
            .GetProperty("description").GetString()!;
        Assert.Contains("Anonymous, key, and oauth", connectionDescription);
        Assert.Contains("rejected", connectionDescription);
    }

    [Fact]
    public void McpServerSchema_TopLevel_DescriptionDocumentsEntityNameLocation()
    {
        var schema = LoadSchema(DataCoreAssembly, "Phantom.Workspaces.Data.JsonSchemas.mcp-server.json");
        var description = schema.GetProperty("description").GetString()!;

        Assert.Contains("entity name", description);
        Assert.Contains("${USER}/mcp-servers", description);
        Assert.Contains("defaults/mcp-servers", description);
        Assert.Contains("not the 'serverName'", description);
    }
}
