using System.Collections.ObjectModel;
using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Data.Vector;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services;

namespace Phantom.Workspaces.Tests;

public sealed class AgentFactorySubAgentDispatcherIntegrationTests
{
    private const string DispatcherDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "dispatcher",
          "model": { "id": "sub-agent-dispatcher", "provider": "sub-agent-dispatcher" }
        }
        """;

    private const string DispatcherToolsJson =
        """
        {
          "kind": "prompt",
          "name": "dispatcher",
          "model": { "id": "sub-agent-dispatcher", "provider": "sub-agent-dispatcher" },
          "tools": [
            {
              "kind": "agent-definition",
              "name": "default",
              "description": "The default echo sub-agent",
              "definition": {
                "kind": "prompt",
                "name": "echo-agent",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
              }
            }
          ]
        }
        """;

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task CreateChatClient_WithDependencies_BuildsSubAgentDispatcherChatClient()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var resolver = new AgentDefinitionResolver(dataAccessLayer);
        var tools = await AgentDefinitionToolExtractor.ExtractAgentDefinitionToolsAsync(
            Parse(DispatcherToolsJson), resolver, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(tools, static tool => tool.Name == "default");

        var agent = AgentDefinitionLoader.LoadAgentFromJson(DispatcherDefinitionJson);
        var dependencies = new SubAgentDispatcherDependencies
        {
            RunningAgentChatFactory = new StubRunningAgentChatFactory(),
            EmbeddingsProvider = new DeterministicEmbeddingsProvider(),
            DataAccessLayer = dataAccessLayer,
            DispatcherEntityName = new EntityName("dispatchers", "test-dispatcher"),
            AgentDefinitionTools = tools,
        };

        var result = AgentFactory.CreateChatClient(
            agent,
            services: null,
            dispatcherDependencies: dependencies);

        Assert.IsType<SubAgentDispatcherChatClient>(result.ChatClient);
        Assert.Equal("Sub-agent dispatcher", result.DisplayName);
    }

    [Fact]
    public async Task CreateChatClient_FactoryFallsBackToAgentServices()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var resolver = new AgentDefinitionResolver(dataAccessLayer);
        var tools = await AgentDefinitionToolExtractor.ExtractAgentDefinitionToolsAsync(
            Parse(DispatcherToolsJson), resolver, cancellationToken: TestContext.Current.CancellationToken);

        var agent = AgentDefinitionLoader.LoadAgentFromJson(DispatcherDefinitionJson);
        var services = new AgentServices
        {
            RunningAgentChatFactory = new StubRunningAgentChatFactory(),
        };
        var dependencies = new SubAgentDispatcherDependencies
        {
            RunningAgentChatFactory = null,
            EmbeddingsProvider = new DeterministicEmbeddingsProvider(),
            DataAccessLayer = dataAccessLayer,
            DispatcherEntityName = new EntityName("dispatchers", "test-dispatcher"),
            AgentDefinitionTools = tools,
        };

        var result = AgentFactory.CreateChatClient(agent, services, dispatcherDependencies: dependencies);

        Assert.IsType<SubAgentDispatcherChatClient>(result.ChatClient);
    }

    [Fact]
    public void CreateChatClient_WithoutDependencies_ThrowsRequiringDependencies()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson(DispatcherDefinitionJson);

        var exception = Assert.Throws<InvalidOperationException>(() => AgentFactory.CreateChatClient(agent));
        Assert.Contains("SubAgentDispatcherDependencies", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultDispatcherManifest_LoadsAndDeclaresDispatcherAgentType()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var populator = new SchemaPopulator(dataAccessLayer);
        var errors = await populator.Populate();
        Assert.Empty(errors);

        var getResult = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = new EntityName("defaults", "agent-manifests", "sub-agent-dispatcher"),
                    },
                ],
            },
            TestContext.Current.CancellationToken);

        var manifest = getResult.Batches
            .SelectMany(static batch => batch.Entities)
            .Select(static entity => entity.Data)
            .OfType<JsonElement>()
            .Single();

        Assert.Equal("sub-agent-dispatcher", manifest.GetProperty("agent-type").GetString());
        var tool = manifest.GetProperty("tools").EnumerateArray().Single();
        Assert.Equal("default", tool.GetProperty("name").GetString());
    }

    private sealed class StubRunningAgentChatFactory : Phantom.Workspaces.Llm.IRunningAgentChatFactory
    {
        public ObservableCollection<RunningAgentChat> RunningSessions { get; } = new();

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<RunningAgentChatLease> CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<RunningAgentChatLease> GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition = null,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            bool registerAsRunningAgent = true, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
