using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Services;

public interface IAgentSessionRuntimeContextFactory
{
    AgentSessionRuntimeContext Create(JsonElement agentSessionEntity);
}

public sealed class AgentSessionRuntimeContextFactory : IAgentSessionRuntimeContextFactory
{
    private readonly ITransportFactoryRegistryProvider registryProvider;

    public AgentSessionRuntimeContextFactory(ITransportFactoryRegistryProvider registryProvider)
    {
        this.registryProvider = registryProvider ?? throw new ArgumentNullException(nameof(registryProvider));
    }

    public AgentSessionRuntimeContext Create(JsonElement agentSessionEntity)
    {
        ValidatePersistedShape(agentSessionEntity);

        var sessionExecutor = AgentSessionExecutorBindings.ReadSessionExecutor(agentSessionEntity);
        var components = AgentSessionExecutorBindings.ReadComponentBindings(agentSessionEntity);
        using var componentsDocument = JsonDocument.Parse(JsonSerializer.Serialize(components));
        var bindings = ExecutorBindings.FromPersistableMap(componentsDocument.RootElement, sessionExecutor);

        ValidateDescriptor(bindings.SessionExecutor, "executor-bindings.session");
        foreach (var binding in bindings.Bindings)
        {
            ValidateDescriptor(binding.Value, $"executor-bindings.components.{binding.Key}");
        }

        var registry = this.registryProvider.Registry;
        if (registry is null
            && (IsNonlocal(bindings.SessionExecutor) || bindings.Bindings.Values.Any(IsNonlocal)))
        {
            throw new InvalidOperationException(
                "Persisted nonlocal executor bindings require a configured transport registry.");
        }

        return new AgentSessionRuntimeContext(bindings, registry);
    }

    private static void ValidatePersistedShape(JsonElement entity)
    {
        if (entity.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("agent-session");
        }

        if (!entity.TryGetProperty(AgentSessionExecutorBindings.RootKey, out var root))
        {
            return;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(AgentSessionExecutorBindings.RootKey);
        }

        if (root.TryGetProperty(AgentSessionExecutorBindings.SessionKey, out var session)
            && session.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"{AgentSessionExecutorBindings.RootKey}.{AgentSessionExecutorBindings.SessionKey}");
        }

        if (!root.TryGetProperty(AgentSessionExecutorBindings.ComponentsKey, out var components))
        {
            return;
        }

        if (components.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"{AgentSessionExecutorBindings.RootKey}.{AgentSessionExecutorBindings.ComponentsKey}");
        }

        foreach (var component in components.EnumerateObject())
        {
            if (component.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.String))
            {
                throw Invalid(
                    $"{AgentSessionExecutorBindings.RootKey}.{AgentSessionExecutorBindings.ComponentsKey}.{component.Name}");
            }
        }
    }

    private static void ValidateDescriptor(JsonElement descriptor, string key)
    {
        if (descriptor.ValueKind != JsonValueKind.Object
            || !descriptor.TryGetProperty(ExecutorBindings.TypePropertyName, out var type)
            || type.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(type.GetString()))
        {
            throw Invalid(key);
        }

        if (string.Equals(type.GetString(), AgentSessionExecutorBindings.RemoteDescriptorType, StringComparison.Ordinal)
            && (!descriptor.TryGetProperty(ExecutorBindings.EntityIdPropertyName, out var entityId)
                || entityId.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(entityId.GetString())))
        {
            throw Invalid(key);
        }
    }

    private static bool IsNonlocal(JsonElement descriptor)
        => !string.Equals(
            descriptor.GetProperty(ExecutorBindings.TypePropertyName).GetString(),
            AgentSessionExecutorBindings.LocalDescriptorType,
            StringComparison.Ordinal);

    private static InvalidOperationException Invalid(string key)
        => new($"Persisted executor binding '{key}' is invalid.");
}
