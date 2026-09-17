using System.Text.Json;
using System.Threading.Channels;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Testing;

namespace Phantom.Workspaces.Transport.Tests;

public sealed class UserComputerProfileTransportFactoryTests
{
    private static readonly EntityId LocalProfileId = new("11111111-1111-1111-1111-111111111111");
    private static readonly EntityId RemoteProfileId = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task UserComputerProfileTransportFactory_LocalEntity_RoutesToLocalTransport()
    {
        var dataAccessLayer = await CreateSeededDataAccessLayerAsync(LocalProfileId);
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
        var dataAccessLayer = await CreateSeededDataAccessLayerAsync(RemoteProfileId);
        var registry = new CapturingTransportFactoryRegistry();
        var factory = CreateFactory(dataAccessLayer, registry);

        var transport = await factory.ConnectToAsync(
            JsonDocument.Parse(
                """
                {
                  "type": "user-computer-profile",
                  "entity-id": "22222222-2222-2222-2222-222222222222",
                  "connection-descriptor": { "type": "http", "url": "https://remote.example" }
                }
                """).RootElement);

        Assert.Same(registry.Transport, transport);
        var descriptor = Assert.Single(registry.Descriptors);
        Assert.Equal("http", descriptor.GetProperty("type").GetString());
        Assert.Equal("https://remote.example", descriptor.GetProperty("url").GetString());
    }

    [Fact]
    public async Task UserComputerProfileTransportFactory_NonProfileDescriptor_ReturnsNull()
    {
        var dataAccessLayer = await CreateSeededDataAccessLayerAsync();
        var registry = new CapturingTransportFactoryRegistry();
        var factory = CreateFactory(dataAccessLayer, registry);

        var transport = await factory.ConnectToAsync(JsonDocument.Parse("""{"type":"http"}""").RootElement);

        Assert.Null(transport);
        Assert.Empty(registry.Descriptors);
    }

    [Fact]
    public async Task UserComputerProfileTransportFactory_TargetDescriptor_ForwardsThroughRemoteTransport()
    {
        var dataAccessLayer = await CreateSeededDataAccessLayerAsync(RemoteProfileId);
        var registry = new CapturingTransportFactoryRegistry();
        var factory = CreateFactory(dataAccessLayer, registry);

        var transport = await factory.ConnectToAsync(
            JsonDocument.Parse(
                """
                {
                  "type": "user-computer-profile",
                  "entity-id": "22222222-2222-2222-2222-222222222222",
                  "connection-descriptor": { "type": "http", "url": "https://remote.example" },
                  "target": { "type": "local-mcp", "name": "agent" }
                }
                """).RootElement);

        Assert.NotSame(registry.Transport, transport);
        await transport!.ConnectToMessageChannelAsync(JsonDocument.Parse("""{"type":"ignored"}""").RootElement);
        Assert.NotNull(registry.Transport.LastChannelRequest);
        Assert.Equal("local-mcp", registry.Transport.LastChannelRequest.Value.GetProperty("type").GetString());
        Assert.Equal("agent", registry.Transport.LastChannelRequest.Value.GetProperty("name").GetString());
    }

    [Fact]
    public async Task UserComputerProfileTransportFactory_RemoteEntity_RoutesViaConfiguredHub()
    {
        var dataAccessLayer = await CreateSeededDataAccessLayerAsync(RemoteProfileId);
        var registry = new CapturingTransportFactoryRegistry();
        var factory = CreateFactory(
            dataAccessLayer,
            registry,
            ["https://hub.example"]);

        var transport = await factory.ConnectToAsync(
            JsonDocument.Parse(
                """{"type":"user-computer-profile","entity-id":"22222222-2222-2222-2222-222222222222"}""").RootElement);

        Assert.Same(registry.Transport, transport);
        var descriptor = Assert.Single(registry.Descriptors);
        Assert.Equal("reverse-http", descriptor.GetProperty("type").GetString());
        Assert.Equal("https://hub.example", descriptor.GetProperty("hub-urls")[0].GetString());
        Assert.Equal(RemoteProfileId.ToString(), descriptor.GetProperty("entity-id").GetString());
    }

    [Fact]
    public async Task UserComputerProfileTransportFactory_DescriptorMissingEntityId_ThrowsTransportException()
    {
        var dataAccessLayer = await CreateSeededDataAccessLayerAsync();
        var registry = new CapturingTransportFactoryRegistry();
        var factory = CreateFactory(dataAccessLayer, registry);

        var exception = await Assert.ThrowsAsync<TransportException>(
            () => factory.ConnectToAsync(JsonDocument.Parse("""{"type":"user-computer-profile"}""").RootElement));

        Assert.Equal("User computer profile descriptors must include entity-id.", exception.Message);
        Assert.Empty(registry.Descriptors);
    }

    private static UserComputerProfileTransportFactory CreateFactory(
        IDataAccessLayer dataAccessLayer,
        ITransportFactoryRegistry registry,
        IReadOnlyCollection<string>? reverseHttpHubUrls = null)
    {
        var session = new WorkspaceEntitySession
        {
            UserEntityId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ComputerEntityId = new EntityId("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            UserComputerProfileEntityId = LocalProfileId,
        };
        return new UserComputerProfileTransportFactory(
            dataAccessLayer,
            session,
            registry,
            reverseHttpHubUrls);
    }

    private static async Task<IDataAccessLayer> CreateSeededDataAccessLayerAsync(
        EntityId? profileId = null)
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        if (profileId is null)
        {
            return fixture.DataAccessLayer;
        }

        var computerId = profileId == LocalProfileId
            ? new EntityId("33333333-3333-4333-8333-333333333333")
            : new EntityId("44444444-4444-4444-8444-444444444444");
        var profileName = profileId == LocalProfileId ? "local" : "remote";
        var documents = new[]
        {
            Parse(
                """
                {
                  "entity-id": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                  "entity-types": ["entity", "user"],
                  "names": [["users", "username", "transport-test"]]
                }
                """),
            Parse(
                $$"""
                {
                  "entity-id": "{{computerId}}",
                  "entity-types": ["entity", "computer"],
                  "names": [["computers", "name", "{{profileName}}"]]
                }
                """),
            Parse(
                $$"""
                {
                  "entity-id": "{{profileId}}",
                  "entity-types": ["entity", "user-computer-profile"],
                  "computer-reference": ["computers", "name", "{{profileName}}"],
                  "user-reference": ["users", "username", "transport-test"]
                }
                """),
        };
        await fixture.SeedManyValidAsync(documents);
        return fixture.DataAccessLayer;
    }

    private static JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

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

}
