using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Tests;

public sealed class AgentSessionRuntimeContextFactoryTests
{
    [Fact]
    public void Create_PersistedSplitBindings_ReconstructsRuntimeContext()
    {
        var registry = new TransportFactoryRegistry();
        var provider = new TransportFactoryRegistryProvider(registry);
        var factory = new AgentSessionRuntimeContextFactory(provider);

        var context = factory.Create(Json(
            """
            {
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

        Assert.Equal("local", context.ExecutorBindings.SessionExecutor.GetProperty("type").GetString());
        var worker = context.ExecutorBindings.ResolveComponent("worker");
        Assert.Equal("user-computer-profile", worker.GetProperty("type").GetString());
        Assert.Same(registry, context.TransportFactoryRegistry);
    }

    [Fact]
    public void Create_NoPersistedBindings_UsesLocalDefaultsWithoutRegistry()
    {
        var factory = new AgentSessionRuntimeContextFactory(new TransportFactoryRegistryProvider());

        var context = factory.Create(Json("""{"agent-session-id":"local-session"}"""));

        Assert.Equal("local", context.ExecutorBindings.SessionExecutor.GetProperty("type").GetString());
        Assert.Empty(context.ExecutorBindings.Bindings);
        Assert.Null(context.TransportFactoryRegistry);
    }

    [Theory]
    [InlineData("host-profile-entity-id")]
    [InlineData("owning-profile-entity-id")]
    public void Create_LegacyHostProfile_UsesSessionExecutorFallback(string propertyName)
    {
        var registry = new TransportFactoryRegistry();
        var factory = new AgentSessionRuntimeContextFactory(new TransportFactoryRegistryProvider(registry));

        var context = factory.Create(Json(
            $$"""{"{{propertyName}}":"22222222-2222-2222-2222-222222222222"}"""));

        Assert.Equal(
            "22222222-2222-2222-2222-222222222222",
            ExecutorBindings.DeriveClientInstance(context.ExecutorBindings.SessionExecutor));
        Assert.Same(registry, context.TransportFactoryRegistry);
    }

    [Theory]
    [InlineData("host-profile-entity-id")]
    [InlineData("owning-profile-entity-id")]
    public void Create_LegacyLocalProfile_UsesLocalSessionExecutor(string propertyName)
    {
        var localProfileEntityId = new EntityId("22222222-2222-2222-2222-222222222222");
        var factory = new AgentSessionRuntimeContextFactory(new TransportFactoryRegistryProvider());

        var context = factory.Create(
            Json($$"""{"{{propertyName}}":"{{localProfileEntityId}}"}"""),
            localProfileEntityId);

        Assert.Equal("local", context.ExecutorBindings.SessionExecutor.GetProperty("type").GetString());
        Assert.Null(context.TransportFactoryRegistry);
    }

    [Theory]
    [InlineData("""{"executor-bindings":[]}""", "executor-bindings")]
    [InlineData("""{"executor-bindings":{"session":[]}}""", "executor-bindings.session")]
    [InlineData("""{"executor-bindings":{"components":[]}}""", "executor-bindings.components")]
    [InlineData("""{"executor-bindings":{"components":{"worker":42}}}""", "executor-bindings.components.worker")]
    [InlineData("""{"executor-bindings":{"components":{"worker":{}}}}""", "executor-bindings.components.worker")]
    public void Create_MalformedBinding_ReportsBindingKey(string json, string expectedKey)
    {
        var factory = new AgentSessionRuntimeContextFactory(new TransportFactoryRegistryProvider());

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Create(Json(json)));

        Assert.Contains(expectedKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_NonlocalBindingWithoutRegistry_ReportsConfigurationError()
    {
        var factory = new AgentSessionRuntimeContextFactory(new TransportFactoryRegistryProvider());

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Create(Json(
            """
            {
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
        var factory = new AgentSessionRuntimeContextFactory(new TransportFactoryRegistryProvider());

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Create(Json("[]")));

        Assert.Contains("agent-session", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_LegacyLocalStringComponentDescriptor_NormalizesToLocalAndDoesNotRequireRegistry()
    {
        // Persistence back-compat: a bare "." (local client instance) string in the components map
        // normalises to {"type":"local"}, which must pass validation and must not require a registry.
        var factory = new AgentSessionRuntimeContextFactory(new TransportFactoryRegistryProvider());

        var context = factory.Create(Json(
            """
            {
              "executor-bindings": {
                "session": { "type": "local" },
                "components": {
                  "worker": "."
                }
              }
            }
            """));

        var worker = context.ExecutorBindings.ResolveComponent("worker");
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
        var factory = new AgentSessionRuntimeContextFactory(new TransportFactoryRegistryProvider(registry));

        var context = factory.Create(Json(
            """
            {
              "executor-bindings": {
                "components": {
                  "worker": "55555555-5555-5555-5555-555555555555"
                }
              }
            }
            """));

        var worker = context.ExecutorBindings.ResolveComponent("worker");
        Assert.Equal("user-computer-profile", worker.GetProperty("type").GetString());
        Assert.Equal("55555555-5555-5555-5555-555555555555", worker.GetProperty("entity-id").GetString());
        Assert.Same(registry, context.TransportFactoryRegistry);
    }

    [Fact]
    public void Create_BlankDescriptorType_ReportsBindingKey()
    {
        var factory = new AgentSessionRuntimeContextFactory(new TransportFactoryRegistryProvider());

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
        var factory = new AgentSessionRuntimeContextFactory(new TransportFactoryRegistryProvider());

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
        var factory = new AgentSessionRuntimeContextFactory(new TransportFactoryRegistryProvider());

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

    private static JsonElement Json(string json)
        => JsonDocument.Parse(json).RootElement.Clone();
}
