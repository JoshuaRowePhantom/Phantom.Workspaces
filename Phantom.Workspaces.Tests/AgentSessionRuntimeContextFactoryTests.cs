using System.Text.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Tests;

public sealed class AgentSessionRuntimeContextFactoryTests
{
    private const string SessionId = "session-1484";
    private const string OwnerId = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void Constructor_NullRegistry_AllowsLocalOnlyHydration()
    {
        var factory = new AgentSessionRuntimeContextFactory(null);

        var context = factory.Create(SessionJson());

        Assert.Null(context.TransportFactoryRegistry);
        Assert.Equal("local", context.Intent.ExecutorBindings.SessionExecutor.GetProperty("type").GetString());
    }

    [Fact]
    public void FromProvider_RegistryPublishedAfterConstruction_HydratesNonlocalBindings()
    {
        var provider = new TransportFactoryRegistryProvider();
        var factory = AgentSessionRuntimeContextFactory.FromProvider(provider);
        var registry = new TransportFactoryRegistry();
        provider.Publish(registry);

        var context = factory.Create(Json(
            """
            {
              "agent-session-id": "session-1484",
              "host-profile-entity-id": "22222222-2222-2222-2222-222222222222",
              "executor-bindings": {
                "session": {
                  "type": "user-computer-profile",
                  "entity-id": "22222222-2222-2222-2222-222222222222"
                }
              }
            }
            """));

        Assert.Same(registry, context.TransportFactoryRegistry);
    }

    [Fact]
    public void AgentSessionRuntimeIntentData_InitProperties_PreserveOnlyPersistableIntent()
    {
        var properties = typeof(AgentSessionRuntimeIntentData).GetProperties()
            .Select(property => property.Name)
            .Order()
            .ToArray();

        Assert.Equal(
            [
                nameof(AgentSessionRuntimeIntentData.ContinueInBackground),
                nameof(AgentSessionRuntimeIntentData.ExecutorBindings),
                nameof(AgentSessionRuntimeIntentData.ExpectedTrustProfileRevision),
                nameof(AgentSessionRuntimeIntentData.OwnershipGeneration),
                nameof(AgentSessionRuntimeIntentData.OwningProfileEntityId),
                nameof(AgentSessionRuntimeIntentData.TrustProfileReference),
            ],
            properties);
        Assert.All(
            typeof(AgentSessionRuntimeIntentData).GetProperties(),
            property => Assert.Contains(
                typeof(IsExternalInit),
                property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers()));
    }

    [Fact]
    public void CreateAgentSessionEntityDataRequest_RequiredInitProperties_AreMarkedRequired()
    {
        var requiredProperties = typeof(CreateAgentSessionEntityDataRequest).GetProperties()
            .Where(property => property.GetCustomAttribute<RequiredMemberAttribute>() is not null)
            .Select(property => property.Name)
            .Order()
            .ToArray();

        Assert.Equal(
            [
                nameof(CreateAgentSessionEntityDataRequest.AgentDefinitionEntityId),
                nameof(CreateAgentSessionEntityDataRequest.AgentDisplayName),
                nameof(CreateAgentSessionEntityDataRequest.AgentSessionId),
                nameof(CreateAgentSessionEntityDataRequest.AgentSessionNames),
                nameof(CreateAgentSessionEntityDataRequest.ComputerName),
                nameof(CreateAgentSessionEntityDataRequest.CurrentTime),
                nameof(CreateAgentSessionEntityDataRequest.HostProfileEntityId),
            ],
            requiredProperties);
    }

    [Fact]
    public void CreateAgentSessionEntityDataRequest_OptionalProperties_UseDocumentedDefaults()
    {
        var request = new CreateAgentSessionEntityDataRequest
        {
            AgentDefinitionEntityId = default,
            AgentDisplayName = "",
            AgentSessionId = "",
            AgentSessionNames = [],
            CurrentTime = default,
            ComputerName = "",
            HostProfileEntityId = default,
        };

        Assert.Null(request.ParameterValues);
        Assert.Null(request.SessionExecutor);
        Assert.Null(request.ExecutorComponentBindings);
        Assert.Null(request.ParameterSelections);
        Assert.Equal(0, request.OwnershipGeneration);
        Assert.Null(request.TrustProfileReference);
        Assert.Null(request.ExpectedTrustProfileRevision);
        Assert.False(request.ContinueInBackground);
    }

    [Fact]
    public void CreateEntityData_NamedInitializer_PersistsMappedFields()
    {
        var owner = new EntityId(OwnerId);
        var data = AgentSessionEntityFactory.CreateEntityData(new CreateAgentSessionEntityDataRequest
        {
            AgentDefinitionEntityId = new EntityId(),
            AgentDisplayName = "Agent",
            AgentSessionId = SessionId,
            AgentSessionNames = [new EntityName("sessions", SessionId)],
            CurrentTime = DateTimeOffset.UnixEpoch,
            ComputerName = "host",
            HostProfileEntityId = owner,
            ParameterValues = new Dictionary<string, string> { ["topic"] = "runtime intent" },
            ParameterSelections = new Dictionary<string, JsonElement>
            {
                ["executor"] = Json("""{"user-computer-profile":"22222222-2222-2222-2222-222222222222"}"""),
            },
        });

        Assert.Equal(owner.ToString(), data.GetProperty("host-profile-entity-id").GetString());
        Assert.Equal(0, data.GetProperty("ownership-generation").GetInt64());
        Assert.Equal("runtime intent", data.GetProperty("parameter-values").GetProperty("topic").GetString());
        Assert.Equal(JsonValueKind.Object, data.GetProperty("executor-bindings").ValueKind);
        Assert.False(data.GetProperty("continue-in-background").GetBoolean());
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData(OwnerId, -1)]
    public void PersistedAgentSessionRuntimeIntent_Init_InvalidOwnerOrGeneration_RejectsValue(
        string owner,
        long generation)
    {
        Assert.ThrowsAny<ArgumentException>(() => new PersistedAgentSessionRuntimeIntent
        {
            AgentSessionId = SessionId,
            OwningProfileEntityId = owner,
            OwnershipGeneration = generation,
            ExecutorBindings = new ExecutorBindings(),
        });
    }

    [Fact]
    public void AgentSessionRuntimeContext_Init_StoresIntentAndProcessRegistry()
    {
        var registry = new TransportFactoryRegistry();
        var intent = Intent();

        var context = new AgentSessionRuntimeContext
        {
            Intent = intent,
            TransportFactoryRegistry = registry,
        };

        Assert.Same(intent, context.Intent);
        Assert.Same(registry, context.TransportFactoryRegistry);
    }

    [Fact]
    public void Create_PersistedSplitBindings_ReconstructsRuntimeContext()
    {
        var registry = new TransportFactoryRegistry();
        var factory = new AgentSessionRuntimeContextFactory(registry);

        var context = factory.Create(Json(
            """
            {
              "agent-session-id": "session-1484",
              "host-profile-entity-id": "22222222-2222-2222-2222-222222222222",
              "executor-bindings": {
                "session": { "type": "local" },
                "components": {
                  "worker": {
                    "type": "user-computer-profile",
                    "entity-id": "11111111-1111-1111-1111-111111111111"
                  }
                }
              }
            }
            """));

        Assert.Equal("local", context.Intent.ExecutorBindings.SessionExecutor.GetProperty("type").GetString());
        var worker = context.Intent.ExecutorBindings.ResolveComponent("worker");
        Assert.Equal("user-computer-profile", worker.GetProperty("type").GetString());
        Assert.Same(registry, context.TransportFactoryRegistry);
    }

    [Fact]
    public void Create_NoPersistedBindings_UsesLocalDefaultsWithoutRegistry()
    {
        var factory = new AgentSessionRuntimeContextFactory(null);

        var context = factory.Create(SessionJson());

        Assert.Equal("local", context.Intent.ExecutorBindings.SessionExecutor.GetProperty("type").GetString());
        Assert.Empty(context.Intent.ExecutorBindings.Bindings);
        Assert.Null(context.TransportFactoryRegistry);
    }

    [Theory]
    [InlineData("host-profile-entity-id")]
    [InlineData("owning-profile-entity-id")]
    public void Create_LegacyHostProfile_UsesSessionExecutorFallback(string propertyName)
    {
        var registry = new TransportFactoryRegistry();
        var factory = new AgentSessionRuntimeContextFactory(registry);

        var context = factory.Create(Json(
            $$"""{"agent-session-id":"{{SessionId}}","{{propertyName}}":"{{OwnerId}}"}"""));

        Assert.Equal(
            "22222222-2222-2222-2222-222222222222",
            ExecutorBindings.DeriveClientInstance(context.Intent.ExecutorBindings.SessionExecutor));
        Assert.Same(registry, context.TransportFactoryRegistry);
    }

    [Theory]
    [InlineData("host-profile-entity-id")]
    [InlineData("owning-profile-entity-id")]
    public void Create_LegacyLocalProfile_UsesLocalSessionExecutor(string propertyName)
    {
        var localProfileEntityId = new EntityId("22222222-2222-2222-2222-222222222222");
        var factory = new AgentSessionRuntimeContextFactory(null);

        var context = factory.Create(
            Json($$"""{"agent-session-id":"{{SessionId}}","{{propertyName}}":"{{localProfileEntityId}}"}"""),
            localProfileEntityId);

        Assert.Equal("local", context.Intent.ExecutorBindings.SessionExecutor.GetProperty("type").GetString());
        Assert.Null(context.TransportFactoryRegistry);
    }

    [Theory]
    [InlineData("""{"executor-bindings":[]}""", "executor-bindings")]
    [InlineData("""{"executor-bindings":{"session":[]}}""", "executor-bindings.session")]
    [InlineData("""{"executor-bindings":{"components":[]}}""", "executor-bindings.components")]
    [InlineData("""{"executor-bindings":{"components":{"worker":42}}}""", "executor-bindings.components.worker")]
    [InlineData("""{"executor-bindings":{"components":{"worker":{}}}}""", "executor-bindings.components.worker")]
    public void Create_MalformedBinding_ReportsBindingKeyWithoutValue(string json, string expectedKey)
    {
        var factory = new AgentSessionRuntimeContextFactory(null);

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Create(Json(json)));

        Assert.Contains(expectedKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_NonlocalBindingWithoutRegistry_ReportsConfigurationError()
    {
        var factory = new AgentSessionRuntimeContextFactory(null);

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Create(Json(
            """
            {
              "agent-session-id": "session-1484",
              "host-profile-entity-id": "22222222-2222-2222-2222-222222222222",
              "executor-bindings": {
                "session": { "type": "local" },
                "components": {
                  "worker": {
                    "type": "user-computer-profile",
                    "entity-id": "33333333-3333-3333-3333-333333333333"
                  }
                }
              }
            }
            """)));

        Assert.Contains("transport registry", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_NonObjectSessionEntity_ReportsBindingKey()
    {
        var factory = new AgentSessionRuntimeContextFactory(null);

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Create(Json("[]")));

        Assert.Contains("agent-session", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_LegacyLocalStringComponentDescriptor_NormalizesToLocalAndDoesNotRequireRegistry()
    {
        // Persistence back-compat: a bare "." (local client instance) string in the components map
        // normalises to {"type":"local"}, which must pass validation and must not require a registry.
        var factory = new AgentSessionRuntimeContextFactory(null);

        var context = factory.Create(Json(
            """
            {
              "agent-session-id": "session-1484",
              "host-profile-entity-id": "22222222-2222-2222-2222-222222222222",
              "executor-bindings": {
                "session": { "type": "local" },
                "components": {
                  "worker": "."
                }
              }
            }
            """));

        var worker = context.Intent.ExecutorBindings.ResolveComponent("worker");
        Assert.Equal("local", worker.GetProperty("type").GetString());
        Assert.Null(context.TransportFactoryRegistry);
    }

    [Fact]
    public void Create_LegacyRemoteStringComponentDescriptor_NormalizesToUserComputerProfile()
    {
        // A bare entity-id string in the components map normalises to a user-computer-profile
        // descriptor with the entity-id — a nonlocal binding that must succeed when a registry is
        // configured (i.e. the persisted-legacy split-binding case survives hydration).
        var registry = new TransportFactoryRegistry();
        var factory = new AgentSessionRuntimeContextFactory(registry);

        var context = factory.Create(Json(
            """
            {
              "agent-session-id": "session-1484",
              "host-profile-entity-id": "22222222-2222-2222-2222-222222222222",
              "executor-bindings": {
                "components": {
                  "worker": "55555555-5555-5555-5555-555555555555"
                }
              }
            }
            """));

        var worker = context.Intent.ExecutorBindings.ResolveComponent("worker");
        Assert.Equal("user-computer-profile", worker.GetProperty("type").GetString());
        Assert.Equal("55555555-5555-5555-5555-555555555555", worker.GetProperty("entity-id").GetString());
        Assert.Same(registry, context.TransportFactoryRegistry);
    }

    [Fact]
    public void Create_BlankDescriptorType_ReportsBindingKey()
    {
        var factory = new AgentSessionRuntimeContextFactory(null);

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Create(Json(
            """
            {
              "executor-bindings": {
                "session": { "type": "   " }
              }
            }
            """)));

        Assert.Contains("executor-bindings.session", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_BlankRemoteEntityId_ReportsBindingKey()
    {
        var factory = new AgentSessionRuntimeContextFactory(null);

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Create(Json(
            """
            {
              "executor-bindings": {
                "components": {
                  "worker": {
                    "type": "user-computer-profile",
                    "entity-id": "   "
                  }
                }
              }
            }
            """)));

        Assert.Contains("executor-bindings.components.worker", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_NonlocalSessionBindingWithoutRegistry_ReportsConfigurationError()
    {
        // Guard the "session executor is nonlocal, no registry" branch specifically — distinct from a
        // nonlocal component with no registry. A persisted nonlocal session default must never
        // silently downgrade to local execution.
        var factory = new AgentSessionRuntimeContextFactory(null);

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Create(Json(
            """
            {
              "executor-bindings": {
                "session": {
                  "type": "user-computer-profile",
                  "entity-id": "66666666-6666-6666-6666-666666666666"
                }
              }
            }
            """)));

        Assert.Contains("transport registry", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_TrustIntent_PreservesReferenceAndExpectedRevision()
    {
        var context = new AgentSessionRuntimeContextFactory(null).Create(SessionJson(
            ",\"trust-profile-reference\":\"trusted/default\",\"expected-trust-profile-revision\":\"revision-7\""));

        Assert.Equal("trusted/default", context.Intent.TrustProfileReference);
        Assert.Equal("revision-7", context.Intent.ExpectedTrustProfileRevision);
    }

    [Fact]
    public void Create_LegacyNumericTrustRevision_PreservesCompatibleTextValue()
    {
        var context = new AgentSessionRuntimeContextFactory(null).Create(SessionJson(
            ""","trust-profile-reference":"trusted/default","expected-trust-profile-revision":7"""));

        Assert.Equal("7", context.Intent.ExpectedTrustProfileRevision);
    }

    [Fact]
    public void AgentSessionEntityFactory_CreateEntityData_DefaultBackground_PersistsFalse()
    {
        var data = CreateEntityData();

        Assert.False(data.GetProperty("continue-in-background").GetBoolean());
    }

    [Fact]
    public void AgentSessionEntityFactory_CreateEntityData_BackgroundEnabled_PersistsTrue()
    {
        var data = CreateEntityData(continueInBackground: true);

        Assert.True(data.GetProperty("continue-in-background").GetBoolean());
    }

    [Fact]
    public void AgentSessionEntityFactory_CreateEntityData_DefaultOwner_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            AgentSessionEntityFactory.CreateEntityData(new CreateAgentSessionEntityDataRequest
            {
                AgentDefinitionEntityId = new EntityId(),
                AgentDisplayName = "Agent",
                AgentSessionId = SessionId,
                AgentSessionNames = [new EntityName("sessions", SessionId)],
                CurrentTime = DateTimeOffset.UnixEpoch,
                ComputerName = "host",
                HostProfileEntityId = default,
            }));
    }

    [Fact]
    public void AgentSessionEntityFactory_CreateEntityData_RuntimeAuthority_PersistsOwnerGenerationBindingsAndTrust()
    {
        var trustReference = JsonSerializer.SerializeToElement("trusted/high");
        var data = AgentSessionEntityFactory.CreateEntityData(new CreateAgentSessionEntityDataRequest
        {
            AgentDefinitionEntityId = new EntityId(),
            AgentDisplayName = "Agent",
            AgentSessionId = SessionId,
            AgentSessionNames = [new EntityName("sessions", SessionId)],
            CurrentTime = DateTimeOffset.UnixEpoch,
            ComputerName = "host",
            HostProfileEntityId = new EntityId(OwnerId),
            SessionExecutor = Json("""{"type":"local"}"""),
            ExecutorComponentBindings = Json("""{"worker":{"type":"local"}}"""),
            OwnershipGeneration = 4,
            TrustProfileReference = trustReference,
            ExpectedTrustProfileRevision = "revision-9",
            ContinueInBackground = true,
        });

        Assert.Equal(OwnerId, data.GetProperty("host-profile-entity-id").GetString());
        Assert.Equal(4, data.GetProperty("ownership-generation").GetInt64());
        Assert.Equal("local", data.GetProperty("executor-bindings").GetProperty("session").GetProperty("type").GetString());
        Assert.Equal("trusted/high", data.GetProperty("trust-profile-reference").GetString());
        Assert.Equal("revision-9", data.GetProperty("expected-trust-profile-revision").GetString());
        Assert.True(data.GetProperty("continue-in-background").GetBoolean());
    }

    [Fact]
    public void Create_MissingContinueInBackground_UsesFalse()
    {
        var context = new AgentSessionRuntimeContextFactory(null).Create(SessionJson());

        Assert.False(context.Intent.ContinueInBackground);
    }

    [Fact]
    public void Create_ExplicitContinueInBackground_PreservesTrue()
    {
        var context = new AgentSessionRuntimeContextFactory(null).Create(
            SessionJson(""","continue-in-background":true"""));

        Assert.True(context.Intent.ContinueInBackground);
    }

    [Fact]
    public void Create_CompiledPolicyProperty_RejectsEntity()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new AgentSessionRuntimeContextFactory(null).Create(
                SessionJson(""","compiled-policy":{}""")));

        Assert.Contains("compiled-policy", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(""","ownership-generation":-1""", "ownership-generation")]
    [InlineData(",\"ownership-generation\":\"zero\"", "ownership-generation")]
    [InlineData(",\"expected-trust-profile-revision\":-1,\"trust-profile-reference\":\"trusted/default\"", "expected-trust-profile-revision")]
    [InlineData(",\"continue-in-background\":\"yes\"", "continue-in-background")]
    [InlineData(",\"trust-profile-reference\":\"trusted/default\"", "trust-profile-reference")]
    [InlineData(""","expected-trust-profile-revision":1""", "trust-profile-reference")]
    public void Create_MalformedRuntimeIntent_ReportsField(string properties, string expectedField)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new AgentSessionRuntimeContextFactory(null).Create(SessionJson(properties)));

        Assert.Contains(expectedField, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_LegacyMissingOwner_UsesSuppliedLocalProfile()
    {
        var localProfileEntityId = new EntityId("33333333-3333-3333-3333-333333333333");
        var factory = new AgentSessionRuntimeContextFactory(null);

        var context = factory.Create(
            Json($$"""{"agent-session-id":"{{SessionId}}"}"""),
            localProfileEntityId);

        Assert.Equal(localProfileEntityId.ToString(), context.Intent.OwningProfileEntityId);
        Assert.Equal("local", context.Intent.ExecutorBindings.SessionExecutor.GetProperty("type").GetString());
        Assert.Null(context.TransportFactoryRegistry);
    }

    [Fact]
    public void Create_LegacyMissingOwner_WithoutSuppliedLocalProfile_ReportsHostProfile()
    {
        var factory = new AgentSessionRuntimeContextFactory(null);

        var exception = Assert.Throws<InvalidOperationException>(
            () => factory.Create(Json($$"""{"agent-session-id":"{{SessionId}}"}""")));

        Assert.Contains("host-profile-entity-id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_LegacyMissingOwner_WithDefaultSuppliedLocalProfile_ReportsHostProfile()
    {
        var factory = new AgentSessionRuntimeContextFactory(null);

        var exception = Assert.Throws<InvalidOperationException>(
            () => factory.Create(
                Json($$"""{"agent-session-id":"{{SessionId}}"}"""),
                default(EntityId)));

        Assert.Contains("host-profile-entity-id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_MissingAgentSessionId_ReportsField()
    {
        var factory = new AgentSessionRuntimeContextFactory(null);

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Create(Json(
            $$"""{"host-profile-entity-id":"{{OwnerId}}","ownership-generation":0}""")));

        Assert.Contains("agent-session-id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_BlankAgentSessionId_ReportsField()
    {
        var factory = new AgentSessionRuntimeContextFactory(null);

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Create(Json(
            $$"""{"agent-session-id":"   ","host-profile-entity-id":"{{OwnerId}}","ownership-generation":0}""")));

        Assert.Contains("agent-session-id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_NonStringHostProfileEntityId_ReportsField()
    {
        var factory = new AgentSessionRuntimeContextFactory(null);

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Create(Json(
            $$"""{"agent-session-id":"{{SessionId}}","host-profile-entity-id":42}""")));

        Assert.Contains("host-profile-entity-id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_InvalidHostProfileGuid_ReportsField()
    {
        var factory = new AgentSessionRuntimeContextFactory(null);

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Create(Json(
            $$"""{"agent-session-id":"{{SessionId}}","host-profile-entity-id":"not-a-guid","ownership-generation":0}""")));

        Assert.Contains("host-profile-entity-id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentSessionEntityTypeView_GroupsByHostProfile()
    {
        const string ResourceName =
            "Phantom.Workspaces.Data.JsonEntities.entity_type_views.agent-session-entity-type-view.json";
        using var stream = typeof(AgentSessionEntityFactory).Assembly.GetManifestResourceStream(ResourceName);
        Assert.NotNull(stream);
        using var document = JsonDocument.Parse(stream!);

        var group = document.RootElement.GetProperty("group-by-parent");
        Assert.Equal("field", group.GetProperty("source").GetString());
        Assert.Equal("host-profile-entity-id", group.GetProperty("field-path")[0].GetString());
        Assert.Equal("user-computer-profile", group.GetProperty("parent-entity-type-names")[0].GetString());
    }

    private static PersistedAgentSessionRuntimeIntent Intent()
        => new()
        {
            AgentSessionId = SessionId,
            OwningProfileEntityId = OwnerId,
            OwnershipGeneration = 0,
            ExecutorBindings = new ExecutorBindings(),
        };

    private static JsonElement CreateEntityData(bool continueInBackground = false, EntityId? owner = null)
        => AgentSessionEntityFactory.CreateEntityData(new CreateAgentSessionEntityDataRequest
        {
            AgentDefinitionEntityId = new EntityId(),
            AgentDisplayName = "Agent",
            AgentSessionId = SessionId,
            AgentSessionNames = [new EntityName("sessions", SessionId)],
            CurrentTime = DateTimeOffset.UnixEpoch,
            ComputerName = "host",
            HostProfileEntityId = owner ?? new EntityId(OwnerId),
            ContinueInBackground = continueInBackground,
        });

    private static JsonElement SessionJson(string extraProperties = "")
        => Json(
            $$"""
            {
              "agent-session-id": "{{SessionId}}",
              "host-profile-entity-id": "{{OwnerId}}",
              "ownership-generation": 0
              {{extraProperties}}
            }
            """);

    private static JsonElement Json(string json)
        => JsonDocument.Parse(json).RootElement.Clone();
}
