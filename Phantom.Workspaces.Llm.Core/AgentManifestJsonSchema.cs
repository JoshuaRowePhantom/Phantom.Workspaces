using System.Reflection;
using Json.Schema;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Provides access to the agent-manifest JSON schema.
/// Agent manifests are declarative agent configurations with tool resource references
/// that are resolved at runtime based on execution context (user, machine, workspace).
/// </summary>
public static class AgentManifestJsonSchema
{
    private const string ResourceName = "Phantom.Workspaces.Llm.Core.JsonSchemas.agent-manifest.json";

    /// <summary>
    /// Gets the agent-manifest JSON schema.
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
