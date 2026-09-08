using System.Text.Json;
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

    [Fact]
    public void Create_LegacyHostProfile_UsesSessionExecutorFallback()
    {
        var registry = new TransportFactoryRegistry();
        var factory = new AgentSessionRuntimeContextFactory(new TransportFactoryRegistryProvider(registry));

        var context = factory.Create(Json(
            """{"host-profile-entity-id":"22222222-2222-2222-2222-222222222222"}"""));

        Assert.Equal(
            "22222222-2222-2222-2222-222222222222",
            ExecutorBindings.DeriveClientInstance(context.ExecutorBindings.SessionExecutor));
        Assert.Same(registry, context.TransportFactoryRegistry);
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

    private static JsonElement Json(string json)
        => JsonDocument.Parse(json).RootElement.Clone();
}
