using System.Reflection;
using Json.Schema;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Provides access to the mcp-server JSON schema.
/// MCP server entities register Model Context Protocol servers that can be referenced by name
/// from agent manifest tool resources and resolved into MCP tools at runtime.
/// </summary>
public static class McpServerJsonSchema
{
    private const string ResourceName = "Phantom.Workspaces.Llm.Core.JsonSchemas.mcp-server.json";

    /// <summary>
    /// Gets the mcp-server JSON schema.
    /// </summary>
    public static JsonSchema Value { get; } = LoadFromEmbeddedResource();

    private static JsonSchema LoadFromEmbeddedResource()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"Embedded schema resource '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        var schemaText = reader.ReadToEnd();
        return JsonSchema.FromText(schemaText);
    }
}
