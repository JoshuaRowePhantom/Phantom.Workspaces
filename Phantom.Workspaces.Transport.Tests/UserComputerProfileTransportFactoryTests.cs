using System.Text.Json;
using System.Threading.Channels;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Testing;
using Phantom.Workspaces.Transport.ReverseHttp;

namespace Phantom.Workspaces.Transport.Tests;

public sealed class UserComputerProfileTransportFactoryTests
{
    private static readonly EntityId LocalProfileId = new("11111111-1111-1111-1111-111111111111");
    private static readonly EntityId RemoteProfileId = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task UserComputerProfileTransportFactory_LocalTarget_RoutesLocal()
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
        Assert.Equal("https://remote.example/", descriptor.GetProperty("url").GetString());
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
    public async Task UserComputerProfileTransportFactory_TargetInLiveInboundRegistry_RoutesThroughLocalRelay()
    {
        var dataAccessLayer = await CreateSeededDataAccessLayerAsync(RemoteProfileId);
        var registry = new CapturingTransportFactoryRegistry();
        await using var liveRegistry = new ReverseHttpServerTransportFactory();
        var registration = new TestMessageChannel();
        using var request = JsonDocument.Parse(
            $$"""{"type":"reverse-register","entity-id":"{{RemoteProfileId}}"}""");
        await using var registrationLease = await liveRegistry.OnChannelOpenAsync(request.RootElement, registration);
        var factory = CreateFactory(dataAccessLayer, registry, liveRegistry: liveRegistry);

        await using var transport = await factory.ConnectToAsync(ProfileDescriptor());

        Assert.NotNull(transport);
        Assert.Empty(registry.Descriptors);
        Assert.True(liveRegistry.IsRegistered(RemoteProfileId.ToString()));
    }

    [Fact]
    public async Task UserComputerProfileTransportFactory_TargetLiveRegistered_PrefersLiveRelayOverPersistedRoute()
    {
        var dataAccessLayer = await CreateSeededDataAccessLayerAsync(
            RemoteProfileId,
            Routes(
                """
                "direct-http": {
                  "descriptor": { "type": "http", "url": "https://persisted.example/" },
                  "owner-profile-entity-id": "22222222-2222-2222-2222-222222222222",
                  "last-confirmed": "2030-01-01T00:00:00Z",
                  "expires-at": "2030-01-01T00:02:00Z"
                }
                """));
        var registry = new CapturingTransportFactoryRegistry();
        await using var liveRegistry = new ReverseHttpServerTransportFactory();
        var registration = new TestMessageChannel();
        using var request = JsonDocument.Parse(
            $$"""{"type":"reverse-register","entity-id":"{{RemoteProfileId}}"}""");
        await using var registrationLease = await liveRegistry.OnChannelOpenAsync(request.RootElement, registration);
        var factory = CreateFactory(
            dataAccessLayer,
            registry,
            liveRegistry: liveRegistry,
            timeProvider: new StaticTimeProvider(new DateTimeOffset(2026, 9, 18, 18, 0, 0, TimeSpan.Zero)));

        await using var transport = await factory.ConnectToAsync(ProfileDescriptor());

        Assert.NotNull(transport);
        Assert.Empty(registry.Descriptors);
    }

    [Fact]
    public async Task UserComputerProfileTransportFactory_MultiplePersistedRoutes_AttemptsByPriorityThenType()
    {
        var dataAccessLayer = await CreateSeededDataAccessLayerAsync(
            RemoteProfileId,
            Routes(
                """
                "reverse-http:33333333-3333-4333-8333-333333333333": {
                  "descriptor": { "type": "reverse-http", "hub-urls": ["https://hub.example/"], "entity-id": "22222222-2222-2222-2222-222222222222" },
                  "owner-profile-entity-id": "22222222-2222-2222-2222-222222222222",
                  "priority": 50,
                  "last-confirmed": "2026-09-18T18:00:00Z",
                  "expires-at": "2026-09-18T18:02:00Z"
                },
                "direct-http": {
                  "descriptor": { "type": "http", "url": "https://direct.example/" },
                  "owner-profile-entity-id": "22222222-2222-2222-2222-222222222222",
                  "priority": 50,
                  "last-confirmed": "2026-09-18T18:00:00Z",
                  "expires-at": "2026-09-18T18:02:00Z"
                }
                """));
        var registry = new FallbackTransportFactoryRegistry("http");
        var factory = CreateFactory(
            dataAccessLayer,
            registry,
            timeProvider: new StaticTimeProvider(new DateTimeOffset(2026, 9, 18, 18, 1, 0, TimeSpan.Zero)));

        var transport = await factory.ConnectToAsync(ProfileDescriptor());

        Assert.Same(registry.Transport, transport);
        Assert.Equal(["http", "reverse-http"], registry.Descriptors.Select(item => item.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task UserComputerProfileTransportFactory_ExpiredPersistedRoute_IsIgnored()
    {
        var dataAccessLayer = await CreateSeededDataAccessLayerAsync(
            RemoteProfileId,
            Routes(
                """
                "direct-http": {
                  "descriptor": { "type": "http", "url": "https://expired.example/" },
                  "owner-profile-entity-id": "22222222-2222-2222-2222-222222222222",
                  "priority": 1,
                  "last-confirmed": "2026-09-18T17:00:00Z",
                  "expires-at": "2026-09-18T17:02:00Z"
                },
                "reverse-http:33333333-3333-4333-8333-333333333333": {
                  "descriptor": { "type": "reverse-http", "hub-urls": ["https://hub.example/"], "entity-id": "22222222-2222-2222-2222-222222222222" },
                  "owner-profile-entity-id": "22222222-2222-2222-2222-222222222222",
                  "last-confirmed": "2026-09-18T18:00:00Z",
                  "expires-at": "2026-09-18T18:02:00Z"
                }
                """));
        var registry = new CapturingTransportFactoryRegistry();
        var factory = CreateFactory(
            dataAccessLayer,
            registry,
            timeProvider: new StaticTimeProvider(new DateTimeOffset(2026, 9, 18, 18, 1, 0, TimeSpan.Zero)));

        _ = await factory.ConnectToAsync(ProfileDescriptor());

        var descriptor = Assert.Single(registry.Descriptors);
        Assert.Equal("reverse-http", descriptor.GetProperty("type").GetString());
    }

    [Fact]
    public async Task UserComputerProfileTransportFactory_UntrustedTransientDescriptor_DoesNotPreemptAuthoritativeRoutes()
    {
        var dataAccessLayer = await CreateSeededDataAccessLayerAsync(
            RemoteProfileId,
            Routes(
                """
                "direct-http": {
                  "descriptor": { "type": "http", "url": "https://authoritative.example/" },
                  "owner-profile-entity-id": "22222222-2222-2222-2222-222222222222",
                  "last-confirmed": "2026-09-18T18:00:00Z",
                  "expires-at": "2026-09-18T18:02:00Z"
                }
                """));
        var registry = new CapturingTransportFactoryRegistry();
        var factory = CreateFactory(
            dataAccessLayer,
            registry,
            timeProvider: new StaticTimeProvider(new DateTimeOffset(2026, 9, 18, 18, 1, 0, TimeSpan.Zero)),
            trustedTransientRoutesEnabled: false);

        _ = await factory.ConnectToAsync(
            Parse(
                $$"""
                {
                  "type": "user-computer-profile",
                  "entity-id": "{{RemoteProfileId}}",
                  "force-route": { "type": "http", "url": "https://untrusted.example/" }
                }
                """));

        Assert.Equal(
            "https://authoritative.example/",
            Assert.Single(registry.Descriptors).GetProperty("url").GetString());
    }

    [Fact]
    public async Task UserComputerProfileTransportFactory_TrustedForceRouteOverride_PrecedesPersistedButNotLive()
    {
        var dataAccessLayer = await CreateSeededDataAccessLayerAsync(
            RemoteProfileId,
            Routes(
                """
                "direct-http": {
                  "descriptor": { "type": "http", "url": "https://persisted.example/" },
                  "owner-profile-entity-id": "22222222-2222-2222-2222-222222222222",
                  "last-confirmed": "2026-09-18T18:00:00Z",
                  "expires-at": "2026-09-18T18:02:00Z"
                }
                """));
        var registry = new CapturingTransportFactoryRegistry();
        var descriptor = Parse(
            $$"""
            {
              "type": "user-computer-profile",
              "entity-id": "{{RemoteProfileId}}",
              "force-route": { "type": "http", "url": "https://forced.example/" }
            }
            """);
        var factory = CreateFactory(
            dataAccessLayer,
            registry,
            timeProvider: new StaticTimeProvider(new DateTimeOffset(2026, 9, 18, 18, 1, 0, TimeSpan.Zero)));

        _ = await factory.ConnectToAsync(descriptor);

        Assert.Equal("https://forced.example/", Assert.Single(registry.Descriptors).GetProperty("url").GetString());

        registry.Descriptors.Clear();
        await using var liveRegistry = new ReverseHttpServerTransportFactory();
        var registration = new TestMessageChannel();
        using var registrationRequest = JsonDocument.Parse(
            $$"""{"type":"reverse-register","entity-id":"{{RemoteProfileId}}"}""");
        await using var registrationLease = await liveRegistry.OnChannelOpenAsync(registrationRequest.RootElement, registration);
        var liveFactory = CreateFactory(
            dataAccessLayer,
            registry,
            liveRegistry: liveRegistry,
            timeProvider: new StaticTimeProvider(new DateTimeOffset(2026, 9, 18, 18, 1, 0, TimeSpan.Zero)));

        await using var liveTransport = await liveFactory.ConnectToAsync(descriptor);

        Assert.NotNull(liveTransport);
        Assert.Empty(registry.Descriptors);
    }

    [Fact]
    public async Task UserComputerProfileTransportFactory_NoLocalNoLiveNoRouteNoOverride_ThrowsNoRoute()
    {
        var dataAccessLayer = await CreateSeededDataAccessLayerAsync(RemoteProfileId);
        var registry = new CapturingTransportFactoryRegistry();
        var factory = CreateFactory(dataAccessLayer, registry, trustedTransientRoutesEnabled: false);

        var exception = await Assert.ThrowsAsync<TransportException>(
            () => factory.ConnectToAsync(ProfileDescriptor()));

        Assert.Equal(
            $"Remote user computer profile descriptor '{RemoteProfileId}' has no transient route and no configured reverse HTTP hub.",
            exception.Message);
        Assert.Empty(registry.Descriptors);
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
        IReadOnlyCollection<string>? reverseHttpHubUrls = null,
        ReverseHttpServerTransportFactory? liveRegistry = null,
        TimeProvider? timeProvider = null,
        bool trustedTransientRoutesEnabled = true)
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
            reverseHttpHubUrls,
            liveRegistry,
            null,
            timeProvider,
            trustedTransientRoutesEnabled);
    }

    private static async Task<IDataAccessLayer> CreateSeededDataAccessLayerAsync(
        EntityId? profileId = null,
        string? reachability = null)
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
                  {{(reachability is null ? string.Empty : "," + reachability)}}
                }
                """),
        };
        await fixture.SeedManyValidAsync(documents);
        return fixture.DataAccessLayer;
    }

    private static JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement ProfileDescriptor()
        => Parse($$"""{"type":"user-computer-profile","entity-id":"{{RemoteProfileId}}"}""");

    private static string Routes(string routes)
        => $"\"reachability\": {{ \"routes\": {{ {routes} }} }}";

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

    private sealed class FallbackTransportFactoryRegistry(string failingType) : ITransportFactoryRegistry
    {
        public CapturingTransport Transport { get; } = new();

        public List<JsonElement> Descriptors { get; } = [];

        public void Register(ITransportFactory factory)
        {
        }

        public Task<ITransport> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
        {
            this.Descriptors.Add(connectionDescriptor.Clone());
            return string.Equals(
                connectionDescriptor.GetProperty("type").GetString(),
                failingType,
                StringComparison.Ordinal)
                    ? Task.FromException<ITransport>(new TransportException("Route unavailable."))
                    : Task.FromResult<ITransport>(this.Transport);
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

    private sealed class StaticTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
