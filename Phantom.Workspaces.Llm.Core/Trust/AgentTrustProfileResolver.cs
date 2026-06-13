using AgentSchema;

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

        var profileName = ReadProfileReference(agentDefinition);
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return null;
        }

        return await trustProfileProvider.ResolveAsync(profileName, cancellationToken).ConfigureAwait(false);
    }

    private static string? ReadProfileReference(AgentDefinition agentDefinition)
    {
        if (agentDefinition.Metadata is null
            || !agentDefinition.Metadata.TryGetValue(MetadataKey, out var value)
            || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element => element.GetString(),
            _ => value.ToString(),
        };
    }
}
