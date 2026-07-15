using System.Text.Json;
using System.Threading.Channels;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Transport.Tests;

public sealed class UserComputerProfileTransportFactoryTests
{
    private static readonly EntityId LocalProfileId = new("11111111-1111-1111-1111-111111111111");
    private static readonly EntityId RemoteProfileId = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task UserComputerProfileTransportFactory_LocalEntity_RoutesToLocalTransport()
    {
        var dataAccessLayer = new EntityLookupDataAccessLayer(
            (LocalProfileId, """{"entity-id":"11111111-1111-1111-1111-111111111111"}"""));
        var registry = new CapturingTransportFactoryRegistry();
        var factory = CreateFactory(dataAccessLayer, registry);

        var transport = await factory.ConnectToAsync(
            JsonDocument.Parse("""{"type":"user-computer-profile","entity-id":"11111111-1111-1111-1111-111111111111"}""").RootElement);

        Assert.Same(registry.Transport, transport);
        var descriptor = Assert.Single(registry.Descriptors);
        Assert.Equal("local", descriptor.GetProperty("type").GetString());
    }

    [Fact]
    public async Task UserComputerProfileTransportFactory_RemoteEntity_RoutesViaDescriptor()
    {
        var dataAccessLayer = new EntityLookupDataAccessLayer(
            (RemoteProfileId, """
            {
              "entity-id": "22222222-2222-2222-2222-222222222222",
              "connection-descriptor": { "type": "http", "url": "https://remote.example" }
            }
            """));
        var registry = new CapturingTransportFactoryRegistry();
        var factory = CreateFactory(dataAccessLayer, registry);

        var transport = await factory.ConnectToAsync(
            JsonDocument.Parse("""{"type":"user-computer-profile","entity-id":"22222222-2222-2222-2222-222222222222"}""").RootElement);

        Assert.Same(registry.Transport, transport);
        var descriptor = Assert.Single(registry.Descriptors);
        Assert.Equal("http", descriptor.GetProperty("type").GetString());
        Assert.Equal("https://remote.example", descriptor.GetProperty("url").GetString());
    }

    [Fact]
    public async Task UserComputerProfileTransportFactory_NonProfileDescriptor_ReturnsNull()
    {
        var registry = new CapturingTransportFactoryRegistry();
        var factory = CreateFactory(new EntityLookupDataAccessLayer(), registry);

        var transport = await factory.ConnectToAsync(JsonDocument.Parse("""{"type":"http"}""").RootElement);

        Assert.Null(transport);
        Assert.Empty(registry.Descriptors);
    }

    [Fact]
    public async Task UserComputerProfileTransportFactory_TargetDescriptor_ForwardsThroughRemoteTransport()
    {
        var dataAccessLayer = new EntityLookupDataAccessLayer(
            (RemoteProfileId, """
            {
              "entity-id": "22222222-2222-2222-2222-222222222222",
              "connection-descriptor": { "type": "http", "url": "https://remote.example" }
            }
            """));
        var registry = new CapturingTransportFactoryRegistry();
        var factory = CreateFactory(dataAccessLayer, registry);

        var transport = await factory.ConnectToAsync(
            JsonDocument.Parse(
                """
                {
                  "type": "user-computer-profile",
                  "entity-id": "22222222-2222-2222-2222-222222222222",
                  "target": { "type": "local-mcp", "name": "agent" }
                }
                """).RootElement);

        Assert.NotSame(registry.Transport, transport);
        await transport!.ConnectToMessageChannelAsync(JsonDocument.Parse("""{"type":"ignored"}""").RootElement);
        Assert.NotNull(registry.Transport.LastChannelRequest);
        Assert.Equal("local-mcp", registry.Transport.LastChannelRequest.Value.GetProperty("type").GetString());
        Assert.Equal("agent", registry.Transport.LastChannelRequest.Value.GetProperty("name").GetString());
    }

    private static UserComputerProfileTransportFactory CreateFactory(
        IDataAccessLayer dataAccessLayer,
        ITransportFactoryRegistry registry)
    {
        var session = new WorkspaceEntitySession
        {
            UserEntityId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ComputerEntityId = new EntityId("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            UserComputerProfileEntityId = LocalProfileId,
        };
        return new UserComputerProfileTransportFactory(dataAccessLayer, session, registry);
    }

    private sealed class CapturingTransportFactoryRegistry : ITransportFactoryRegistry
    {
        public CapturingTransport Transport { get; } = new();

        public List<JsonElement> Descriptors { get; } = [];

        public void Register(ITransportFactory factory)
        {
        }

        public Task<ITransport> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
        {
            this.Descriptors.Add(connectionDescriptor.Clone());
            return Task.FromResult<ITransport>(this.Transport);
        }
    }

    private sealed class CapturingTransport : ITransport
    {
        public JsonElement? LastChannelRequest { get; private set; }

        public JsonElement? LastStreamRequest { get; private set; }

        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
        {
            this.LastChannelRequest = request.Clone();
            return Task.FromResult<IMessageChannel>(new TestMessageChannel());
        }

        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
        {
            this.LastStreamRequest = request.Clone();
            return Task.FromResult<Stream>(new MemoryStream());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestMessageChannel : IMessageChannel
    {
        public ChannelWriter<JsonElement> Writer { get; } = Channel.CreateUnbounded<JsonElement>().Writer;

        public ChannelReader<JsonElement> Reader { get; } = Channel.CreateUnbounded<JsonElement>().Reader;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EntityLookupDataAccessLayer : IDataAccessLayer
    {
        private readonly Dictionary<EntityId, JsonElement> entities = [];

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
                    ModifiedTime = new Timestamp(DateTimeOffset.UnixEpoch, "test"),
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
            => throw new NotSupportedException();

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
