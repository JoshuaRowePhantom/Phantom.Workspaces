using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Services;

/// <summary>
/// Extracts the <c>agent-definition</c> tool entries declared by a sub-agent-dispatcher's
/// <see cref="AgentDefinition"/> (or <see cref="AgentManifest"/>) into resolved
/// <see cref="AgentDefinitionTool"/> instances.
/// </summary>
/// <remarks>
/// Each entry supplies either an inline <c>definition</c> or a <c>manifest-reference</c> (an
/// entity-name path). Resolution is delegated entirely to the shared
/// <see cref="IAgentDefinitionResolver"/> (issue #999) — this extractor never fetches entities or
/// projects manifests itself. A <c>manifest-reference</c> is resolved by reusing the resolver's
/// existing <c>agent-definition-reference</c> code path.
/// </remarks>
public static class AgentDefinitionToolExtractor
{
    /// <summary>
    /// Reads the <c>tools</c> array from <paramref name="dispatcherDefinition"/> and resolves every
    /// <c>kind == "agent-definition"</c> entry into an <see cref="AgentDefinitionTool"/>.
    /// </summary>
    /// <param name="dispatcherDefinition">The dispatcher definition or manifest JSON carrying the tools array.</param>
    /// <param name="resolver">The shared agent-definition resolver.</param>
    /// <param name="parameters">Optional parameter values forwarded to manifest projection.</param>
    /// <param name="toolResourceFactory">Optional tool-resource factory forwarded to manifest projection.</param>
    /// <param name="cancellationToken">A token to cancel resolution.</param>
    public static async Task<IReadOnlyList<AgentDefinitionTool>> ExtractAgentDefinitionToolsAsync(
        JsonElement dispatcherDefinition,
        IAgentDefinitionResolver resolver,
        IReadOnlyDictionary<string, string>? parameters = null,
        IToolResourceFactory? toolResourceFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        var tools = new List<AgentDefinitionTool>();
        if (dispatcherDefinition.ValueKind != JsonValueKind.Object
            || !dispatcherDefinition.TryGetProperty("tools", out var toolsElement)
            || toolsElement.ValueKind != JsonValueKind.Array)
        {
            return tools;
        }

        foreach (var entry in toolsElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("kind", out var kindElement)
                || kindElement.ValueKind != JsonValueKind.String
                || !string.Equals(kindElement.GetString(), "agent-definition", StringComparison.Ordinal))
            {
                continue;
            }

            var name = GetRequiredString(entry, "name");
            var description = GetRequiredString(entry, "description");

            var request = BuildResolveRequest(entry, name, parameters, toolResourceFactory);
            var resolved = await resolver.ResolveAsync(request, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Agent definition tool '{name}' could not be resolved to an agent definition.");

            tools.Add(new AgentDefinitionTool
            {
                Name = name,
                Description = description,
                Definition = resolved.Definition,
            });
        }

        return tools;
    }

    private static AgentDefinitionResolveRequest BuildResolveRequest(
        JsonElement entry,
        string name,
        IReadOnlyDictionary<string, string>? parameters,
        IToolResourceFactory? toolResourceFactory)
    {
        if (entry.TryGetProperty("definition", out var definitionElement)
            && definitionElement.ValueKind == JsonValueKind.Object)
        {
            return new AgentDefinitionResolveRequest
            {
                AgentDefinition = PhantomAgentSchema.AgentDefinitionFromJson(definitionElement.GetRawText()),
                Parameters = parameters,
                ToolResourceFactory = toolResourceFactory,
            };
        }

        if (entry.TryGetProperty("manifest-reference", out var referenceElement))
        {
            using var document = JsonDocument.Parse(
                $"{{\"agent-definition-reference\":{referenceElement.GetRawText()}}}");
            return new AgentDefinitionResolveRequest
            {
                AgentSessionEntity = document.RootElement.Clone(),
                Parameters = parameters,
                ToolResourceFactory = toolResourceFactory,
            };
        }

        throw new InvalidOperationException(
            $"Agent definition tool '{name}' must specify either an inline 'definition' or a 'manifest-reference'.");
    }

    private static string GetRequiredString(JsonElement entry, string propertyName)
    {
        if (!entry.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new InvalidOperationException(
                $"An agent-definition tool entry is missing the required '{propertyName}' property.");
        }

        return element.GetString()!;
    }
}
