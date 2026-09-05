using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Chat;

namespace Phantom.Workspaces.Tests;

public sealed class WorkspacesTransportCompositionTests
{
    private static readonly EntityId LocalProfileId = new("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Composition_TransportFactoryRegistry_BuildsLocalTransportFactory()
    {
        await using var composition = CreateComposition();

        using var descriptor = JsonDocument.Parse("""{"type":"local"}""");
        var transport = await composition.TransportFactoryRegistry.ConnectToAsync(descriptor.RootElement, Ct());

        Assert.NotNull(transport);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Composition_TransportFactoryRegistry_BuildsUserComputerProfileTransportFactory()
    {
        await using var composition = CreateComposition();

        // The session's own profile id resolves to a local descriptor, so the user-computer-profile
        // factory routes back through the registry to the LocalTransportFactory: proves both factories
        // are registered and built in the composition.
        using var descriptor = JsonDocument.Parse(
            """{"type":"user-computer-profile","entity-id":"11111111-1111-1111-1111-111111111111"}""");
        var transport = await composition.TransportFactoryRegistry.ConnectToAsync(descriptor.RootElement, Ct());

        Assert.NotNull(transport);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Composition_UnknownDescriptor_ThrowsTransportException()
    {
        await using var composition = CreateComposition();

        using var descriptor = JsonDocument.Parse("""{"type":"nonexistent-transport"}""");
        await Assert.ThrowsAsync<TransportException>(
            () => composition.TransportFactoryRegistry.ConnectToAsync(descriptor.RootElement, Ct()));
    }

    [Fact]
    public async Task Composition_ExposesTrustedExecutorAndHostSurfaces()
    {
        await using var composition = CreateComposition();

        Assert.True(composition.TrustedExecutor.CanExecute("some-target"));
        Assert.NotNull(composition.TransportHost);
        Assert.NotNull(composition.ConnectionStatusRegistry);
        Assert.NotNull(composition.LocalListeners);
        Assert.Empty(composition.HubFactories);
    }

    [Fact]
    public async Task Composition_LocalListeners_ServesChatClientChannelInProduction()
    {
        // Issue #1314: the production WorkspacesTransportComposition must register a
        // ChatClientTransportListener on LocalListeners so that an incoming `chat-client`
        // channel carrying an `agent-definition` is dispatched to a listener that builds
        // the executor IChatClient via AgentFactory. Without this, LocalListeners is empty
        // and remote chat-client channels have no listener in production.
        await using var composition = CreateComposition();

        var agentDef = new AgentSchema.PromptAgent
        {
            Name = "echo-agent",
            Instructions = "",
            Model = new AgentSchema.Model { Provider = "echo", Id = "echo-model" },
        };
        var agentDefJson = agentDef.ToJson();
        var openRequest = JsonSerializer.SerializeToDocument(new Dictionary<string, object>
        {
            ["type"] = "chat-client",
            ["agent-definition"] = agentDefJson,
        }).RootElement.Clone();

        var channel = new StubMessageChannel();
        var handle = await composition.LocalListeners.OnChannelOpenAsync(openRequest, channel, Ct());

        Assert.NotNull(handle);
        await handle!.DisposeAsync();
    }

    [Fact]
    public async Task Composition_ExposesRemoteMcpHostHandler()
    {
        await using var composition = CreateComposition();

        Assert.NotNull(composition.RemoteMcpHostHandler);
    }

    [Fact]
    public async Task Composition_RegistersProductionMcpTransportListener()
    {
        // Issue #1438: the production composition must register an McpTransportListener on
        // LocalListeners so an incoming `{"type":"mcp","connection":{...}}` channel — opened by a
        // remote-bound McpToolContextProvider on another machine — is served by this machine's
        // RemoteMcpHostHandler. A connection with no endpoint is not hostable, but the listener still
        // accepts the `mcp` channel and returns a session handle (with a null inner host).
        await using var composition = CreateComposition();

        var openRequest = Json("""{"type":"mcp","connection":{}}""");
        var channel = new StubMessageChannel();
        var handle = await composition.LocalListeners.OnChannelOpenAsync(openRequest, channel, Ct());

        Assert.NotNull(handle);
        await handle!.DisposeAsync();
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class StubMessageChannel : IMessageChannel
    {
        private readonly System.Threading.Channels.Channel<JsonElement> reader
            = System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();
        private readonly System.Threading.Channels.Channel<JsonElement> writer
            = System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();

        public System.Threading.Channels.ChannelReader<JsonElement> Reader => this.reader.Reader;

        public System.Threading.Channels.ChannelWriter<JsonElement> Writer => this.writer.Writer;

        public ValueTask DisposeAsync()
        {
            this.reader.Writer.TryComplete();
            this.writer.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Composition_WithHubFactories_ExposesThemToTransportHost()
    {
        var dataAccessLayer = new EntityLookupDataAccessLayer(
            (LocalProfileId, """{"entity-id":"11111111-1111-1111-1111-111111111111"}"""));
        var session = new WorkspaceEntitySession
        {
            UserEntityId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ComputerEntityId = new EntityId("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            UserComputerProfileEntityId = LocalProfileId,
        };
        var hubFactory = new Phantom.Workspaces.Transport.ReverseHttp.ReverseHttpClientTransportFactory(
            "http://localhost:5282",
            LocalProfileId.ToString());

        await using var composition = new WorkspacesTransportComposition(dataAccessLayer, session, [hubFactory]);

        var exposed = Assert.Single(composition.HubFactories);
        Assert.Same(hubFactory, exposed);
        Assert.Same(hubFactory, Assert.Single(composition.TransportHost.HubFactories));
    }

    private static WorkspacesTransportComposition CreateComposition()
    {
        var dataAccessLayer = new EntityLookupDataAccessLayer(
            (LocalProfileId, """{"entity-id":"11111111-1111-1111-1111-111111111111"}"""));
        var session = new WorkspaceEntitySession
        {
            UserEntityId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ComputerEntityId = new EntityId("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            UserComputerProfileEntityId = LocalProfileId,
        };
        return new WorkspacesTransportComposition(dataAccessLayer, session);
    }

    private static CancellationToken Ct() => new CancellationTokenSource(System.TimeSpan.FromSeconds(10)).Token;

    private sealed class EntityLookupDataAccessLayer : IDataAccessLayer
    {
        private readonly System.Collections.Generic.Dictionary<EntityId, JsonElement> entities = [];

        public EntityLookupDataAccessLayer(params (EntityId EntityId, string Json)[] seedEntities)
        {
            foreach (var (entityId, json) in seedEntities)
            {
                this.entities[entityId] = JsonDocument.Parse(json).RootElement.Clone();
            }
        }

        public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
        {
            var snapshots = request.Entities
                .Where(entity => entity.EntityId is not null && this.entities.ContainsKey(entity.EntityId.Value))
                .Select(entity => new EntitySnapshot
                {
                    EntityId = entity.EntityId!.Value,
                    ModifiedTime = new Timestamp(System.DateTimeOffset.UnixEpoch, "test"),
                    Data = this.entities[entity.EntityId.Value].Clone(),
                    Relationships = [],
                })
                .ToArray();
            return Task.FromResult(new GetResult
            {
                Batches =
                [
                    new TimestampedEntityBatch
                    {
                        Entities = snapshots,
                    },
                ],
            });
        }

        public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
            => throw new System.NotSupportedException();

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            => throw new System.NotSupportedException();

        public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
            => throw new System.NotSupportedException();

        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
            => throw new System.NotSupportedException();

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
            => throw new System.NotSupportedException();
    }
}
