using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Services;

public interface IAgentSessionRuntimeContextFactory
{
    AgentSessionRuntimeContext Create(JsonElement agentSessionEntity);

    AgentSessionRuntimeContext Create(JsonElement agentSessionEntity, EntityId? localProfileEntityId)
        => Create(agentSessionEntity);
}

public sealed class AgentSessionRuntimeContextFactory : IAgentSessionRuntimeContextFactory
{
    private readonly ITransportFactoryRegistryProvider registryProvider;

    public AgentSessionRuntimeContextFactory(ITransportFactoryRegistry? transportFactoryRegistry)
        : this(new TransportFactoryRegistryProvider(transportFactoryRegistry))
    {
    }

    private AgentSessionRuntimeContextFactory(ITransportFactoryRegistryProvider registryProvider)
    {
        this.registryProvider = registryProvider ?? throw new ArgumentNullException(nameof(registryProvider));
    }

    internal static AgentSessionRuntimeContextFactory FromProvider(
        ITransportFactoryRegistryProvider registryProvider)
        => new(registryProvider);

    public AgentSessionRuntimeContext Create(JsonElement agentSessionEntity)
        => Create(agentSessionEntity, null);

    public AgentSessionRuntimeContext Create(JsonElement agentSessionEntity, EntityId? localProfileEntityId)
    {
        ValidatePersistedShape(agentSessionEntity);

        var hasPersistedBindings = agentSessionEntity.TryGetProperty(
            AgentSessionExecutorBindings.RootKey,
            out _);
        var isCurrentPersistenceShape = agentSessionEntity.TryGetProperty("ownership-generation", out _);
        var sessionExecutor = !hasPersistedBindings && isCurrentPersistenceShape
            ? AgentSessionExecutorBindings.LocalDescriptor()
            : AgentSessionExecutorBindings.ReadSessionExecutor(agentSessionEntity);
        if (IsLegacyLocalProfile(agentSessionEntity, localProfileEntityId))
        {
            sessionExecutor = AgentSessionExecutorBindings.LocalDescriptor();
        }

        var components = AgentSessionExecutorBindings.ReadComponentBindings(agentSessionEntity);
        using var componentsDocument = JsonDocument.Parse(JsonSerializer.Serialize(components));
        var bindings = ExecutorBindings.FromPersistableMap(componentsDocument.RootElement, sessionExecutor);

        ValidateDescriptor(bindings.SessionExecutor, "executor-bindings.session");
        foreach (var binding in bindings.Bindings)
        {
            ValidateDescriptor(binding.Value, $"executor-bindings.components.{binding.Key}");
        }

        var transportFactoryRegistry = this.registryProvider.Registry;
        if (transportFactoryRegistry is null
            && (IsNonlocal(bindings.SessionExecutor) || bindings.Bindings.Values.Any(IsNonlocal)))
        {
            throw new InvalidOperationException(
                "Persisted nonlocal executor bindings require a configured transport registry.");
        }

        var agentSessionId = ReadRequiredString(agentSessionEntity, "agent-session-id");
        var owningProfileEntityId = ReadOwningProfileEntityId(agentSessionEntity, localProfileEntityId);
        var ownershipGeneration = ReadNonnegativeInt64(agentSessionEntity, "ownership-generation") ?? 0;
        var continueInBackground = ReadBoolean(agentSessionEntity, "continue-in-background") ?? false;
        var trustProfileReference = ReadOptionalString(agentSessionEntity, "trust-profile-reference");
        var expectedTrustProfileRevision =
            ReadRevision(agentSessionEntity, "expected-trust-profile-revision");
        if ((trustProfileReference is null) != (expectedTrustProfileRevision is null))
        {
            throw Invalid("trust-profile-reference/expected-trust-profile-revision");
        }

        return new AgentSessionRuntimeContext
        {
            Intent = new PersistedAgentSessionRuntimeIntent
            {
                AgentSessionId = agentSessionId,
                OwningProfileEntityId = owningProfileEntityId,
                OwnershipGeneration = ownershipGeneration,
                ExecutorBindings = bindings,
                TrustProfileReference = trustProfileReference,
                ExpectedTrustProfileRevision = expectedTrustProfileRevision,
                ContinueInBackground = continueInBackground,
            },
            TransportFactoryRegistry = transportFactoryRegistry,
        };
    }

    private static bool IsLegacyLocalProfile(JsonElement entity, EntityId? localProfileEntityId)
    {
        if (localProfileEntityId is null
            || entity.TryGetProperty(AgentSessionExecutorBindings.RootKey, out var root)
                && root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(AgentSessionExecutorBindings.SessionKey, out _))
        {
            return false;
        }

        foreach (var propertyName in new[]
                 {
                     AgentSessionExecutorBindings.HostProfileKey,
                     AgentSessionExecutorBindings.OwningProfileKey,
                 })
        {
            if (entity.TryGetProperty(propertyName, out var profile)
                && profile.ValueKind == JsonValueKind.String
                && string.Equals(
                    profile.GetString(),
                    localProfileEntityId.Value.ToString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidatePersistedShape(JsonElement entity)
    {
        if (entity.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("agent-session");
        }

        if (entity.TryGetProperty("compiled-policy", out _))
        {
            throw Invalid("compiled-policy");
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

    private static string ReadOwningProfileEntityId(JsonElement entity, EntityId? localProfileEntityId)
    {
        foreach (var propertyName in new[]
                 {
                     AgentSessionExecutorBindings.HostProfileKey,
                     AgentSessionExecutorBindings.OwningProfileKey,
                 })
        {
            if (entity.TryGetProperty(propertyName, out var owner))
            {
                if (owner.ValueKind == JsonValueKind.String
                    && Guid.TryParse(owner.GetString(), out _))
                {
                    return owner.GetString()!;
                }

                throw Invalid(propertyName);
            }
        }

        if (localProfileEntityId is { } localProfile && localProfile != default)
        {
            return localProfile.ToString();
        }

        throw Invalid(AgentSessionExecutorBindings.HostProfileKey);
    }

    private static string ReadRequiredString(JsonElement entity, string propertyName)
        => ReadOptionalString(entity, propertyName) ?? throw Invalid(propertyName);

    private static string? ReadOptionalString(JsonElement entity, string propertyName)
    {
        if (!entity.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Invalid(propertyName);
        }

        return value.GetString();
    }

    private static long? ReadNonnegativeInt64(JsonElement entity, string propertyName)
    {
        if (!entity.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var result)
            || result < 0)
        {
            throw Invalid(propertyName);
        }

        return result;
    }

    private static string? ReadRevision(JsonElement entity, string propertyName)
    {
        if (!entity.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString();
        }

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var legacyRevision)
            && legacyRevision >= 0)
        {
            return legacyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        throw Invalid(propertyName);
    }

    private static bool? ReadBoolean(JsonElement entity, string propertyName)
    {
        if (!entity.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid(propertyName),
        };
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
