using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Phantom.Workspaces.Llm.Core.Transport;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Llm.Core.Manifest;

/// <summary>
/// The resolved, persistable map of executor name → transport <b>connection-descriptor</b>
/// (<see cref="JsonElement"/>) plus the explicit <b>session executor</b> (issue #1436/#1437,
/// per-component-executor-binding). Knows how to project onto the existing routing primitives.
/// </summary>
/// <remarks>
/// Reuse-first / no new schema: every binding IS a <c>type</c>-discriminated transport
/// connection-descriptor (the same shape <c>ITransportFactoryRegistry.ConnectToAsync</c> consumes), NOT a
/// bespoke <c>ExecutorDescriptor</c>. The persisted component payload is a map of name →
/// connection-descriptor <b>object</b>, not a bare client-instance string.
/// </remarks>
public sealed record ExecutorBindings
{
    /// <summary>The persisted root key carrying the explicit session executor descriptor.</summary>
    public const string SessionKey = "session";

    /// <summary>The persisted root key carrying the per-component name → descriptor map.</summary>
    public const string ComponentsKey = "components";

    /// <summary>The connection-descriptor <c>type</c> discriminator property name.</summary>
    public const string TypePropertyName = "type";

    /// <summary>The <c>entity-id</c> property name carried by a <c>user-computer-profile</c> descriptor.</summary>
    public const string EntityIdPropertyName = "entity-id";

    /// <summary>The overall session executor descriptor; defaults to <c>{"type":"local"}</c>.</summary>
    public JsonElement SessionExecutor { get; init; } = LocalDescriptor();

    /// <summary>The resolved executor name → connection-descriptor map.</summary>
    public IReadOnlyDictionary<string, JsonElement> Bindings { get; init; }
        = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    /// <summary>
    /// Resolves the connection-descriptor a component binds to: an unset (null/empty) executor name
    /// inherits <see cref="SessionExecutor"/>; a bound name returns its descriptor; an unknown name throws.
    /// </summary>
    /// <exception cref="InvalidOperationException">If <paramref name="executorName"/> is not bound.</exception>
    public JsonElement ResolveComponent(string? executorName)
    {
        if (string.IsNullOrEmpty(executorName))
        {
            return this.SessionExecutor.Clone();
        }

        if (this.Bindings.TryGetValue(executorName, out var descriptor))
        {
            return descriptor.Clone();
        }

        throw new InvalidOperationException($"Executor binding '{executorName}' could not be resolved.");
    }

    /// <summary>
    /// Projects the session executor onto the string-keyed <see cref="ExecutorTopology"/> that keeps the
    /// existing GuiLocal <c>CustomTool</c> routing working via
    /// <c>DeferredTrustedExecutorSelector.SetTopology</c>. The GUI-local class always stays on the local
    /// instance (<c>"."</c>); the agent-executor and hosting classes follow the session executor.
    /// </summary>
    public ExecutorTopology ToTopology()
    {
        var sessionInstance = DeriveClientInstance(this.SessionExecutor);
        return new ExecutorTopology
        {
            AgentExecutorClientInstance = sessionInstance,
            HostingInstanceClientInstance = sessionInstance,
            GuiLocalClientInstance = TrustProfile.LocalClientInstance,
        };
    }

    /// <summary>
    /// Produces the <c>components</c> payload of the <c>executor-bindings</c> persisted shape: a map of
    /// executor name → connection-descriptor <b>object</b> (never a bare string).
    /// </summary>
    public JsonElement ToPersistableMap()
    {
        var components = new JsonObject();
        foreach (var binding in this.Bindings)
        {
            components[binding.Key] = JsonNode.Parse(binding.Value.GetRawText());
        }

        return JsonSerializer.Deserialize<JsonElement>(components.ToJsonString());
    }

    /// <summary>
    /// Reconstructs an <see cref="ExecutorBindings"/> from a persisted <c>components</c> map and an
    /// optional session executor descriptor. Back-compat: a bare string component value (a legacy
    /// client-instance string) is read as a <c>user-computer-profile</c> (or <c>local</c>) descriptor.
    /// </summary>
    public static ExecutorBindings FromPersistableMap(JsonElement componentsMap, JsonElement? sessionExecutor = null)
    {
        var bindings = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (componentsMap.ValueKind == JsonValueKind.Object)
        {
            foreach (var component in componentsMap.EnumerateObject())
            {
                bindings[component.Name] = NormalizeDescriptor(component.Value);
            }
        }

        return new ExecutorBindings
        {
            SessionExecutor = sessionExecutor is { } session ? session.Clone() : LocalDescriptor(),
            Bindings = bindings,
        };
    }

    /// <summary>
    /// Builds the resolved bindings from the manifest's parsed executor resources (the M5 executor
    /// PRE-PASS). Each <see cref="ExecutorResource"/> is turned into a connection-descriptor via
    /// <see cref="ExecutorResourceResolver"/>. This pass is independent of <c>IToolResourceFactory</c> —
    /// executors must exist before tools/model reference them by name.
    /// </summary>
    public static ExecutorBindings Build(
        IReadOnlyList<ExecutorResource> executorResources,
        IReadOnlyDictionary<string, JsonElement> parameterSelections,
        TrustProfile? trustProfile,
        ExecutorResourceResolver? resolver = null,
        JsonElement? sessionExecutor = null)
    {
        ArgumentNullException.ThrowIfNull(executorResources);
        ArgumentNullException.ThrowIfNull(parameterSelections);

        resolver ??= new ExecutorResourceResolver();

        var bindings = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var resource in executorResources)
        {
            bindings[resource.Name] = resolver.Resolve(resource, parameterSelections, trustProfile);
        }

        return new ExecutorBindings
        {
            SessionExecutor = sessionExecutor is { } session ? session.Clone() : LocalDescriptor(),
            Bindings = bindings,
        };
    }

    /// <summary>
    /// Derives the string client-instance key for a <c>local</c> (<c>"."</c>) or
    /// <c>user-computer-profile</c> (its <c>entity-id</c>) connection-descriptor, used by the string-keyed
    /// topology / trust checks.
    /// </summary>
    /// <exception cref="InvalidOperationException">If the descriptor has no derivable client instance.</exception>
    public static string DeriveClientInstance(JsonElement descriptor)
    {
        if (descriptor.ValueKind == JsonValueKind.Object
            && descriptor.TryGetProperty(TypePropertyName, out var type)
            && type.ValueKind == JsonValueKind.String)
        {
            var descriptorType = type.GetString();
            if (string.Equals(descriptorType, ExecutionTargetResolver.LocalDescriptorType, StringComparison.Ordinal))
            {
                return TrustProfile.LocalClientInstance;
            }

            if (string.Equals(descriptorType, ExecutionTargetResolver.RemoteDescriptorType, StringComparison.Ordinal)
                && descriptor.TryGetProperty(EntityIdPropertyName, out var entityId)
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

    private static JsonElement NormalizeDescriptor(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var clientInstance = value.GetString();
            var descriptor = string.Equals(clientInstance, TrustProfile.LocalClientInstance, StringComparison.Ordinal)
                ? new JsonObject { [TypePropertyName] = ExecutionTargetResolver.LocalDescriptorType }
                : new JsonObject
                {
                    [TypePropertyName] = ExecutionTargetResolver.RemoteDescriptorType,
                    [EntityIdPropertyName] = clientInstance,
                };

            return JsonSerializer.Deserialize<JsonElement>(descriptor.ToJsonString());
        }

        return value.Clone();
    }
}
