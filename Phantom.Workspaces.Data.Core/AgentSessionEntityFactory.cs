using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Data;

/// <summary>
/// Data-layer factory that authors the <c>agent-session</c> entity document and its derived names.
/// Extracted from the GUI <c>AgentSessionShortcutContext</c> (issue #1403) so the shortcut context
/// orchestrates the entity <c>UpdateAsync</c> without owning JSON authoring or name sanitization.
/// Preserves the issue #1397 behavior: the display name includes a human-readable local creation
/// time and originating computer, the simple entity name embeds a sanitized computer name plus a
/// sortable timestamp and the session id, and the document is assembled with <see cref="JsonNode"/>
/// so free-text values cannot break the JSON.
/// </summary>
public static class AgentSessionEntityFactory
{
    /// <summary>
    /// Builds the simple (unqualified) name component for a new agent-session entity: a sortable
    /// UTC timestamp, the sanitized originating computer name, and the session id.
    /// </summary>
    public static string CreateSessionSimpleName(
        string agentSessionId,
        DateTimeOffset currentTime,
        string computerName)
    {
        var timestampComponent = currentTime.ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture);
        var computerComponent = SanitizeNameComponent(computerName);
        return $"session-{timestampComponent}-{computerComponent}-{agentSessionId}";
    }

    /// <summary>
    /// Authors the agent-session entity document (names, display name, source-definition reference,
    /// session id, and optional parameter-values / host-profile) as a JSON-safe <see cref="JsonElement"/>.
    /// </summary>
    public static JsonElement CreateEntityData(
        EntityId agentDefinitionEntityId,
        string agentDisplayName,
        string agentSessionId,
        IReadOnlyCollection<EntityName> agentSessionNames,
        DateTimeOffset currentTime,
        string computerName,
        IReadOnlyDictionary<string, string>? parameterValues = null,
        EntityId? hostProfileEntityId = null)
    {
        var entityId = new EntityId();

        // Assemble the document with JsonNode rather than string interpolation so free-text values
        // (the agent display name, the computer name, and the human-readable timestamp) cannot
        // break the JSON when they contain quotes or other special characters (issue #1397).
        var namesArray = new JsonArray(
            agentSessionNames
                .Select(entityName => (JsonNode)new JsonArray(
                    entityName.Components
                        .Select(component => (JsonNode)JsonValue.Create(component)!)
                        .ToArray()))
                .ToArray());

        // Human-readable, culture-aware local creation time plus the originating computer, so the
        // sessions list can distinguish otherwise identically-named sessions.
        var localTime = currentTime.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
        var displayName = $"{agentDisplayName} session - {localTime} on {computerName}";

        var root = new JsonObject
        {
            ["entity-id"] = entityId.ToString(),
            ["entity-types"] = new JsonArray("entity", "agent-session"),
            ["names"] = namesArray,
            ["display-name"] = new JsonObject { ["default"] = displayName },
            ["agent-source-entity-id"] = agentDefinitionEntityId.ToString(),
            ["agent-session-id"] = agentSessionId,
        };

        if (parameterValues is { Count: > 0 })
        {
            var parameterValuesObject = new JsonObject();
            foreach (var parameterValue in parameterValues)
            {
                parameterValuesObject[parameterValue.Key] = parameterValue.Value;
            }

            root["parameter-values"] = parameterValuesObject;
        }

        if (hostProfileEntityId is { } profileId && profileId != default)
        {
            root["host-profile-entity-id"] = profileId.ToString();
        }

        return JsonSerializer.Deserialize<JsonElement>(root.ToJsonString());
    }

    private static string SanitizeNameComponent(string value)
    {
        var sanitized = new string(
            value
                .ToLowerInvariant()
                .Select(character => (character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') ? character : '-')
                .ToArray());
        return string.IsNullOrEmpty(sanitized) ? "unknown" : sanitized;
    }
}
