using AgentSchema;
using System.Text.Json;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// Resolves the trust profile referenced by an agent definition's metadata into an effective
/// composed <see cref="TrustProfile"/>.
/// </summary>
/// <remarks>
/// An agent definition references a trust profile by name through
/// <c>Metadata["trust-profile"]</c>. When absent, no trust profile applies and resolution
/// returns <see langword="null"/>.
/// </remarks>
public static class AgentTrustProfileResolver
{
    /// <summary>The agent-definition metadata key carrying the trust profile reference.</summary>
    public const string MetadataKey = "trust-profile";

    /// <summary>
    /// Resolves the agent definition's referenced trust profile, or <see langword="null"/> when
    /// the definition references none.
    /// </summary>
    public static async ValueTask<TrustProfile?> ResolveAsync(
        AgentDefinition agentDefinition,
        ITrustProfileProvider trustProfileProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentDefinition);
        ArgumentNullException.ThrowIfNull(trustProfileProvider);

        var metadata = ReadTrustProfileMetadata(agentDefinition);
        if (metadata is null)
        {
            return null;
        }

        var profileName = ReadProfileReference(metadata.Value);
        if (!string.IsNullOrWhiteSpace(profileName))
        {
            return await trustProfileProvider.ResolveAsync(profileName, cancellationToken).ConfigureAwait(false);
        }

        if (metadata.Value.ValueKind == JsonValueKind.Object)
        {
            var profileEntity = TrustProfileEntityReader.Read(metadata.Value);
            return TrustProfileComposer.Compose([profileEntity.Definition]);
        }

        return null;
    }

    private static JsonElement? ReadTrustProfileMetadata(AgentDefinition agentDefinition)
    {
        if (agentDefinition.Metadata is null
            || !agentDefinition.Metadata.TryGetValue(MetadataKey, out var value)
            || value is null)
        {
            return null;
        }

        return value switch
        {
            JsonElement element => element.Clone(),
            string text => JsonSerializer.SerializeToElement(text),
            _ => JsonSerializer.SerializeToElement(value),
        };
    }

    private static string? ReadProfileReference(JsonElement metadata)
    {
        if (metadata.ValueKind == JsonValueKind.String)
        {
            return metadata.GetString();
        }

        if (metadata.ValueKind != JsonValueKind.Object
            || !metadata.TryGetProperty("$ref", out var reference)
            || reference.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (reference.TryGetProperty("entity-name", out var entityName) && entityName.ValueKind == JsonValueKind.Array)
        {
            string? lastComponent = null;
            foreach (var component in entityName.EnumerateArray())
            {
                if (component.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(component.GetString()))
                {
                    lastComponent = component.GetString();
                }
            }

            return lastComponent;
        }

        return null;
    }
}
