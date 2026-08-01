using System.Text.Json;
using AgentSchema;
using MongoDB.Bson;

namespace Phantom.Workspaces.Llm.Interfaces;

/// <summary>
/// Canonical, well-formed <see cref="AgentDefinition"/> shape for hosted GitHub Copilot
/// sub-agents (provider <c>github-copilot-subagent</c>). Fixes #1187: prior to this the router
/// spawned every hosted sub-agent with a two-field synthetic definition
/// (<c>{"kind":"prompt","model":{"provider":"github-copilot-subagent"}}</c>) that lacked a
/// model id, name, tools, and per-sub-agent identity — legacy rows whose
/// <see cref="Interfaces.PersistedAgent.AgentDefinitionJson"/> is null (the case that
/// motivated #1186) also fall back through this helper so restore yields a full definition
/// rather than propagating <see langword="null"/> into <see cref="AgentFactory"/>.
///
/// The <c>model.id</c> sentinel <c>"cli-hosted"</c> is not semantically meaningful — the CLI
/// owns model selection for hosted sub-agents — but keeps the schema well-formed and lets
/// <see cref="AgentFactory"/> resolve the <c>github-copilot-subagent</c> provider fast-path
/// (per #912) without falling into the model-id-null throw.
/// </summary>
public static class CopilotSubAgentDefinitionDefaults
{
    public const string HostedSubAgentProvider = "github-copilot-subagent";
    public const string HostedSubAgentModelId = "cli-hosted";
    public const string HostedSubAgentDefaultName = "copilot-subagent";
    public const string HostedSubAgentDefaultDisplayName = "GitHub Copilot Sub-Agent";
    public const string HostedSubAgentDefaultDescription = "Hosted GitHub Copilot sub-agent";

    /// <summary>
    /// Builds the canonical hosted-Copilot sub-agent <see cref="AgentDefinition"/> JSON. The
    /// per-sub-agent identity fields (<c>subAgentSessionId</c>, display name, description,
    /// name) are folded into the payload so distinct sub-agents produce distinct definitions
    /// (rather than sharing a single static instance) and so the round-trip through
    /// persistence preserves the identity a UI or transcript needs.
    /// </summary>
    public static string BuildJson(
        string subAgentSessionId,
        string? displayName,
        string? description,
        string? name)
    {
        var effectiveDisplayName = string.IsNullOrWhiteSpace(displayName)
            ? HostedSubAgentDefaultDisplayName
            : displayName!;
        var effectiveDescription = string.IsNullOrWhiteSpace(description)
            ? HostedSubAgentDefaultDescription
            : description!;
        var effectiveName = string.IsNullOrWhiteSpace(name)
            ? HostedSubAgentDefaultName
            : name!;

        return $$"""
        {
          "kind": "prompt",
          "name": {{JsonSerializer.Serialize(effectiveName)}},
          "displayName": {{JsonSerializer.Serialize(effectiveDisplayName)}},
          "description": {{JsonSerializer.Serialize(effectiveDescription)}},
          "instructions": "",
          "model": {
            "id": "{{HostedSubAgentModelId}}",
            "provider": "{{HostedSubAgentProvider}}"
          },
          "tools": []
        }
        """;
    }

    /// <summary>
    /// Constructs a fully-populated <see cref="AgentDefinition"/> instance for a hosted
    /// Copilot sub-agent. Callers propagate per-sub-agent identity fields captured from the
    /// spawning tool call so every hosted sub-agent has its own definition (never the shared
    /// static instance the router used before #1187).
    /// </summary>
    public static AgentDefinition Create(
        string subAgentSessionId,
        string? displayName,
        string? description,
        string? name)
    {
        var json = BuildJson(subAgentSessionId, displayName, description, name);
        return AgentDefinition.FromJson(json)
            ?? throw new InvalidOperationException(
                "Failed to parse the canonical hosted Copilot sub-agent AgentDefinition.");
    }

    /// <summary>
    /// Builds the BSON representation of the canonical hosted-Copilot sub-agent
    /// <see cref="AgentDefinition"/> for persistence-store migration paths (legacy rows whose
    /// <see cref="Interfaces.PersistedAgent.AgentDefinitionJson"/> was never written).
    /// </summary>
    public static BsonDocument BuildBsonJson(string subAgentSessionId)
        => BsonDocument.Parse(BuildJson(subAgentSessionId, displayName: null, description: null, name: null));
}
