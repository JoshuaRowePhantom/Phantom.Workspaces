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
    /// session id, optional parameter-values / host-profile, and the #1437 executor-bindings +
    /// typed parameter-selections keys) as a JSON-safe <see cref="JsonElement"/>.
    /// </summary>
    public static JsonElement CreateEntityData(CreateAgentSessionEntityDataRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.HostProfileEntityId == default)
        {
            throw new ArgumentException("A host profile entity ID is required.", nameof(request));
        }

        if (request.OwnershipGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Ownership generation must not be negative.");
        }

        if (request.TrustProfileReference.HasValue != (request.ExpectedTrustProfileRevision is not null))
        {
            throw new ArgumentException(
                "Trust profile reference and expected revision must both be present or both be absent.",
                nameof(request));
        }

        if (request.TrustProfileReference is { } trustProfileReferenceValue
            && (trustProfileReferenceValue.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(trustProfileReferenceValue.GetString())))
        {
            throw new ArgumentException("Trust profile reference must be a non-empty string.", nameof(request));
        }

        if (request.ExpectedTrustProfileRevision is { } expectedRevision
            && string.IsNullOrWhiteSpace(expectedRevision))
        {
            throw new ArgumentException(
                "Expected trust profile revision must be a non-empty string.",
                nameof(request));
        }

        var entityId = new EntityId();

        // Assemble the document with JsonNode rather than string interpolation so free-text values
        // (the agent display name, the computer name, and the human-readable timestamp) cannot
        // break the JSON when they contain quotes or other special characters (issue #1397).
        var namesArray = new JsonArray(
            request.AgentSessionNames
                .Select(entityName => (JsonNode)new JsonArray(
                    entityName.Components
                        .Select(component => (JsonNode)JsonValue.Create(component)!)
                        .ToArray()))
                .ToArray());

        // Human-readable, culture-aware local creation time plus the originating computer, so the
        // sessions list can distinguish otherwise identically-named sessions.
        var localTime = request.CurrentTime.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
        var displayName = $"{request.AgentDisplayName} session - {localTime} on {request.ComputerName}";

        var root = new JsonObject
        {
            ["entity-id"] = entityId.ToString(),
            ["entity-types"] = new JsonArray("entity", "agent-session"),
            ["names"] = namesArray,
            ["display-name"] = new JsonObject { ["default"] = displayName },
            ["agent-source-entity-id"] = request.AgentDefinitionEntityId.ToString(),
            ["agent-session-id"] = request.AgentSessionId,
            ["host-profile-entity-id"] = request.HostProfileEntityId.ToString(),
            ["ownership-generation"] = request.OwnershipGeneration,
            ["continue-in-background"] = request.ContinueInBackground,
        };

        if (request.ParameterValues is { Count: > 0 })
        {
            var parameterValuesObject = new JsonObject();
            foreach (var parameterValue in request.ParameterValues)
            {
                parameterValuesObject[parameterValue.Key] = parameterValue.Value;
            }

            root["parameter-values"] = parameterValuesObject;
        }

        // NEW (#1437): the typed executor selections and resolved executor bindings. parameter-selections
        // is a sibling of the string->string parameter-values map (M7); executor-bindings carries the
        // explicit session executor plus per-component connection-descriptor objects (M6). All values are
        // authored via JsonNode so the JsonElement descriptors round-trip verbatim.
        if (request.ParameterSelections is { Count: > 0 })
        {
            var parameterSelectionsObject = new JsonObject();
            foreach (var parameterSelection in request.ParameterSelections)
            {
                parameterSelectionsObject[parameterSelection.Key] =
                    JsonNode.Parse(parameterSelection.Value.GetRawText());
            }

            root["parameter-selections"] = parameterSelectionsObject;
        }

        var hasComponentBindings = request.ExecutorComponentBindings is { ValueKind: JsonValueKind.Object } componentsProbe
            && componentsProbe.EnumerateObject().Any();
        var executorBindings = new JsonObject
        {
            [AgentSessionExecutorBindings.SessionKey] = request.SessionExecutor is { } session
                ? JsonNode.Parse(session.GetRawText())
                : new JsonObject { ["type"] = AgentSessionExecutorBindings.LocalDescriptorType },
        };

        var components = new JsonObject();
        if (hasComponentBindings
            && request.ExecutorComponentBindings is { ValueKind: JsonValueKind.Object } componentsElement)
        {
            foreach (var component in componentsElement.EnumerateObject())
            {
                components[component.Name] = JsonNode.Parse(component.Value.GetRawText());
            }
        }

        executorBindings[AgentSessionExecutorBindings.ComponentsKey] = components;
        root[AgentSessionExecutorBindings.RootKey] = executorBindings;

        if (request.TrustProfileReference is { } trustProfileReference)
        {
            root["trust-profile-reference"] = JsonNode.Parse(trustProfileReference.GetRawText());
            root["expected-trust-profile-revision"] = request.ExpectedTrustProfileRevision;
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
