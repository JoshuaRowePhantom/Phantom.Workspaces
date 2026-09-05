using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Data;

/// <summary>
/// Reads the <c>executor-bindings</c> and typed <c>parameter-selections</c> keys authored by
/// <see cref="AgentSessionEntityFactory"/> back off an <c>agent-session</c> entity, so a resumed session
/// rebuilds the same executor topology (issue #1437, per-component-executor-binding).
/// </summary>
/// <remarks>
/// Reuse-first / no new schema: every binding IS a <c>type</c>-discriminated transport
/// connection-descriptor. The session executor is the explicit <c>{"type":"local"}</c> default that
/// per-component executors override. <b>Back-compat (M6):</b> when <c>executor-bindings.session</c> is
/// absent but the legacy <c>host-profile-entity-id</c> is present, the session executor is derived as
/// <c>{"type":"user-computer-profile","entity-id":&lt;that id&gt;}</c>.
/// </remarks>
public static class AgentSessionExecutorBindings
{
    /// <summary>The root key carrying the session executor and per-component bindings.</summary>
    public const string RootKey = "executor-bindings";

    /// <summary>The key (under <see cref="RootKey"/>) carrying the explicit session executor descriptor.</summary>
    public const string SessionKey = "session";

    /// <summary>The key (under <see cref="RootKey"/>) carrying the per-component name → descriptor map.</summary>
    public const string ComponentsKey = "components";

    /// <summary>The root key carrying the typed launch-parameter selections (M7).</summary>
    public const string ParameterSelectionsKey = "parameter-selections";

    /// <summary>The legacy root key naming the single host user-computer-profile entity (M6 fallback).</summary>
    public const string HostProfileKey = "host-profile-entity-id";

    /// <summary>The connection-descriptor <c>type</c> discriminator property name.</summary>
    public const string TypeProperty = "type";

    /// <summary>The <c>entity-id</c> property name carried by a <c>user-computer-profile</c> descriptor.</summary>
    public const string EntityIdProperty = "entity-id";

    /// <summary>Descriptor <c>type</c> for the local, in-process transport.</summary>
    public const string LocalDescriptorType = "local";

    /// <summary>Descriptor <c>type</c> for a remote target resolved via user-computer profile.</summary>
    public const string RemoteDescriptorType = "user-computer-profile";

    /// <summary>The local client instance identifier (<c>"."</c>).</summary>
    public const string LocalClientInstance = ".";

    /// <summary>
    /// Reads the explicit session executor descriptor: <c>executor-bindings.session</c> when present;
    /// otherwise the M6 fallback derived from <c>host-profile-entity-id</c>; otherwise
    /// <c>{"type":"local"}</c>.
    /// </summary>
    public static JsonElement ReadSessionExecutor(JsonElement entityData)
    {
        if (entityData.ValueKind == JsonValueKind.Object
            && entityData.TryGetProperty(RootKey, out var bindings)
            && bindings.ValueKind == JsonValueKind.Object
            && bindings.TryGetProperty(SessionKey, out var session)
            && session.ValueKind == JsonValueKind.Object)
        {
            return session.Clone();
        }

        if (entityData.ValueKind == JsonValueKind.Object
            && entityData.TryGetProperty(HostProfileKey, out var hostProfile)
            && hostProfile.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(hostProfile.GetString()))
        {
            return UserComputerProfileDescriptor(hostProfile.GetString()!);
        }

        return LocalDescriptor();
    }

    /// <summary>
    /// Reads the per-component name → connection-descriptor map from <c>executor-bindings.components</c>.
    /// Returns an empty map when absent.
    /// </summary>
    public static IReadOnlyDictionary<string, JsonElement> ReadComponentBindings(JsonElement entityData)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (entityData.ValueKind == JsonValueKind.Object
            && entityData.TryGetProperty(RootKey, out var bindings)
            && bindings.ValueKind == JsonValueKind.Object
            && bindings.TryGetProperty(ComponentsKey, out var components)
            && components.ValueKind == JsonValueKind.Object)
        {
            foreach (var component in components.EnumerateObject())
            {
                result[component.Name] = component.Value.Clone();
            }
        }

        return result;
    }

    /// <summary>Reads the typed <c>parameter-selections</c> map. Returns an empty map when absent.</summary>
    public static IReadOnlyDictionary<string, JsonElement> ReadParameterSelections(JsonElement entityData)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (entityData.ValueKind == JsonValueKind.Object
            && entityData.TryGetProperty(ParameterSelectionsKey, out var selections)
            && selections.ValueKind == JsonValueKind.Object)
        {
            foreach (var selection in selections.EnumerateObject())
            {
                result[selection.Name] = selection.Value.Clone();
            }
        }

        return result;
    }

    /// <summary>
    /// Derives the string client-instance key for a <c>local</c> (<c>"."</c>) or
    /// <c>user-computer-profile</c> (its <c>entity-id</c>) connection-descriptor — the key the resumed
    /// string-keyed executor topology binds on.
    /// </summary>
    /// <exception cref="InvalidOperationException">If the descriptor has no derivable client instance.</exception>
    public static string DeriveClientInstance(JsonElement descriptor)
    {
        if (descriptor.ValueKind == JsonValueKind.Object
            && descriptor.TryGetProperty(TypeProperty, out var type)
            && type.ValueKind == JsonValueKind.String)
        {
            var descriptorType = type.GetString();
            if (string.Equals(descriptorType, LocalDescriptorType, StringComparison.Ordinal))
            {
                return LocalClientInstance;
            }

            if (string.Equals(descriptorType, RemoteDescriptorType, StringComparison.Ordinal)
                && descriptor.TryGetProperty(EntityIdProperty, out var entityId)
                && entityId.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(entityId.GetString()))
            {
                return entityId.GetString()!;
            }
        }

        throw new InvalidOperationException(
            "Connection-descriptor has no derivable client instance (expected 'local' or 'user-computer-profile').");
    }

    /// <summary>Builds a fresh <c>{"type":"local"}</c> connection-descriptor.</summary>
    public static JsonElement LocalDescriptor()
    {
        using var document = JsonDocument.Parse("""{"type":"local"}""");
        return document.RootElement.Clone();
    }

    /// <summary>Builds a <c>{"type":"user-computer-profile","entity-id":&lt;id&gt;}</c> connection-descriptor.</summary>
    public static JsonElement UserComputerProfileDescriptor(string entityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        var descriptor = new JsonObject
        {
            [TypeProperty] = RemoteDescriptorType,
            [EntityIdProperty] = entityId,
        };

        return JsonSerializer.Deserialize<JsonElement>(descriptor.ToJsonString());
    }
}
