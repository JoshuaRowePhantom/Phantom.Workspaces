using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm.Echo;
using Phantom.Workspaces.Testing;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.ReverseHttp;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Transport.Tests.Scenarios;

public sealed class AgentStackMultiInstanceReachabilityTests
{
    private static readonly EntityId ProfileA = new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1593");
    private static readonly EntityId ProfileB = new("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbb1593");
    private static readonly EntityId ProfileD = new("dddddddd-dddd-4ddd-8ddd-dddddddd1593");
    private static readonly EntityId HubId = new("99999999-9999-4999-8999-999999991593");
    private static readonly EntityId UserId = new("eeeeeeee-eeee-4eee-8eee-eeeeeeee1593");
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AgentStackMultiInstance_HubInvokesRegisteredClient_RoutesThroughLiveInboundRegistry()
    {
        var ct = TransportScenarioSupport.TestToken();
        var executorRegistry = ChatRegistry();
        await using var harness = await HubRelayHarness.CreateAsync(executorRegistry, ct);
        var profileC = new EntityId(harness.ExecutorEntityId);
        var dataAccessLayer = await SeedProfilesAsync(profileC);
        var dummyB = new ReverseHttpClientTransportFactoryTests.FakeMessageChannel();
        using var registerB = JsonDocument.Parse(
            $$"""{"type":"reverse-register","entity-id":"{{ProfileB}}"}""");
        await using var registrationB = await harness.Fixture.ReverseHttpServer.OnChannelOpenAsync(
            registerB.RootElement,
            dummyB,
            ct);
        var factory = CreateProfileFactory(
            dataAccessLayer,
            ProfileA,
            new TransportFactoryRegistry(),
            harness.Fixture.ReverseHttpServer);

        var response = await RunChatAsync(factory, profileC, "A to C", ct);

        Assert.Equal("A to C", response);
        Assert.True(harness.Fixture.ReverseHttpServer.IsRegistered(ProfileB.ToString()));
    }

    [Fact]
    public async Task AgentStackMultiInstance_ClientInvokesPeerViaPersistedReverseRoute_RelaysThroughHub()
    {
        var ct = TransportScenarioSupport.TestToken();
        var executorRegistry = ChatRegistry();
        await using var harness = await HubRelayHarness.CreateAsync(executorRegistry, ct);
        var profileC = new EntityId(harness.ExecutorEntityId);
        var dataAccessLayer = await SeedProfilesAsync(profileC, ReverseRoute(profileC));
        var callerRegistry = new TransportFactoryRegistry();
        callerRegistry.Register(harness.CreateForwardingFactory(Peer(ProfileB)));
        var factory = CreateProfileFactory(dataAccessLayer, ProfileB, callerRegistry);

        var response = await RunChatAsync(factory, profileC, "B to C", ct);

        Assert.Equal("B to C", response);
    }

    [Fact]
    public async Task AgentStackMultiInstance_ClientInvokesDirectListener_UsesPersistedDirectHttpRoute()
    {
        var ct = TransportScenarioSupport.TestToken();
        var executorRegistry = ChatRegistry();
        await using var direct = await AgentStackCrossInstanceTests.ForwardHttpInstance.CreateAsync(executorRegistry, ct);
        var dataAccessLayer = await SeedProfilesAsync(
            new EntityId("cccccccc-cccc-4ccc-8ccc-cccccccc1593"),
            profileDRoutes: DirectRoute());
        var callerRegistry = new TransportFactoryRegistry();
        callerRegistry.Register(direct);
        var factoryB = CreateProfileFactory(dataAccessLayer, ProfileB, callerRegistry);
        var factoryA = CreateProfileFactory(dataAccessLayer, ProfileA, callerRegistry);

        var responses = await Task.WhenAll(
            RunChatAsync(factoryB, ProfileD, "B to D", ct),
            RunChatAsync(factoryA, ProfileD, "A to D", ct));

        Assert.Equal(["B to D", "A to D"], responses);
    }

    [Fact]
    public async Task AgentStackMultiInstance_TargetHasMultipleRoutes_PrefersHigherPriorityWithFallback()
    {
        var ct = TransportScenarioSupport.TestToken();
        var executorRegistry = ChatRegistry();
        await using var harness = await HubRelayHarness.CreateAsync(executorRegistry, ct);
        var profileC = new EntityId(harness.ExecutorEntityId);
        var dataAccessLayer = await SeedProfilesAsync(profileC, MultiRoutes(profileC));
        var registry = new TransportFactoryRegistry();
        var failingDirect = new FailingDirectFactory();
        registry.Register(failingDirect);
        registry.Register(harness.CreateForwardingFactory(Peer(ProfileA)));
        var factory = CreateProfileFactory(dataAccessLayer, ProfileA, registry);

        var response = await RunChatAsync(factory, profileC, "fallback", ct);

        Assert.Equal("fallback", response);
        Assert.Equal(1, failingDirect.Attempts);
    }

    [Fact]
    public async Task AgentStackMultiInstance_ExpiredPersistedRoute_IsNotUsed()
    {
        var profileC = new EntityId("cccccccc-cccc-4ccc-8ccc-cccccccc1593");
        var dataAccessLayer = await SeedProfilesAsync(profileC, ExpiredDirectRoute(profileC));
        var registry = new TransportFactoryRegistry();
        var direct = new FailingDirectFactory();
        registry.Register(direct);
        var factory = CreateProfileFactory(dataAccessLayer, ProfileA, registry);

        await Assert.ThrowsAsync<TransportException>(
            () => factory.ConnectToAsync(ProfileDescriptor(profileC)));

        Assert.Equal(0, direct.Attempts);
    }

    [Fact]
    public async Task AgentStackMultiInstance_ClientReconnects_PersistedRouteUpdatedAndReachable()
    {
        var ct = TransportScenarioSupport.TestToken();
        var executorRegistry = ChatRegistry();
        await using var harness = await HubRelayHarness.CreateAsync(executorRegistry, ct);
        var profileC = new EntityId(harness.ExecutorEntityId);
        var dataAccessLayer = await SeedProfilesAsync(profileC, ReverseRoute(profileC, "https://old-hub.example/"));
        var routeStore = new DataAccessReachabilityRouteStore(dataAccessLayer);
        await routeStore.UpsertRouteAsync(
            profileC,
            Route(
                $"reverse-http:{HubId}",
                ReverseDescriptor(profileC, "https://new-hub.example/"),
                profileC));
        var shim = new InProcessHubHttpTransportFactory(harness.Fixture, "https://new-hub.example/");
        var registry = new TransportFactoryRegistry();
        registry.Register(new ReverseHttpForwardingTransportFactory(shim, authenticatedPeer: Peer(ProfileB)));
        var factory = CreateProfileFactory(dataAccessLayer, ProfileB, registry);

        var response = await RunChatAsync(factory, profileC, "after reconnect", ct);

        Assert.Equal("after reconnect", response);
        Assert.Equal(["https://new-hub.example/"], shim.ConnectedUrls);
    }

    [Fact]
    public async Task AgentStackMultiInstance_TargetDisconnectsDuringRelay_PropagatesTransportFailure()
    {
        var ct = TransportScenarioSupport.TestToken();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executorRegistry = new TransportRegistry();
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        executorRegistry.Register(
            new RecordingListener(
                () => opened.TrySetResult(),
                () => disposed.TrySetResult()));
        await using var harness = await HubRelayHarness.CreateAsync(executorRegistry, ct);
        var profileC = new EntityId(harness.ExecutorEntityId);
        var dataAccessLayer = await SeedProfilesAsync(profileC, ReverseRoute(profileC));
        var registry = new TransportFactoryRegistry();
        registry.Register(harness.CreateForwardingFactory(Peer(ProfileB)));
        var factory = CreateProfileFactory(dataAccessLayer, ProfileB, registry);
        await using var transport = await factory.ConnectToAsync(ProfileDescriptor(profileC));
        await using var channel = await transport!.ConnectToMessageChannelAsync(
            Json("""{"type":"record"}"""),
            ct);
        await opened.Task.WaitAsync(ct);

        await harness.CrashExecutorAsync();

        await disposed.Task.WaitAsync(ct);
        Assert.False(harness.Fixture.ReverseHttpServer.IsRegistered(profileC.ToString()));
    }

    [Fact]
    public async Task AgentStackMultiInstance_LeaseExpiresWhileConnected_LiveRegistryStillServes()
    {
        var ct = TransportScenarioSupport.TestToken();
        var executorRegistry = ChatRegistry();
        await using var harness = await HubRelayHarness.CreateAsync(executorRegistry, ct);
        var profileC = new EntityId(harness.ExecutorEntityId);
        var dataAccessLayer = await SeedProfilesAsync(profileC, ExpiredDirectRoute(profileC));
        var factory = CreateProfileFactory(
            dataAccessLayer,
            ProfileA,
            new TransportFactoryRegistry(),
            harness.Fixture.ReverseHttpServer);

        var response = await RunChatAsync(factory, profileC, "live wins", ct);

        Assert.Equal("live wins", response);
    }

    [Fact]
    public async Task AgentStackMultiInstance_ConcurrentPublishersAndCallers_PreserveOwnershipAndIdentity()
    {
        var ct = TransportScenarioSupport.TestToken();
        var identities = new TransportPeerIdentityProvider();
        var listener = new IdentityListener(identities);
        var executorRegistry = new TransportRegistry();
        executorRegistry.Register(listener);
        await using var harness = await HubRelayHarness.CreateAsync(executorRegistry, ct, identities);
        var profileC = new EntityId(harness.ExecutorEntityId);
        var dataAccessLayer = await SeedProfilesAsync(profileC, ReverseRoute(profileC));
        var store = new DataAccessReachabilityRouteStore(dataAccessLayer);
        await Task.WhenAll(
            store.UpsertRouteAsync(profileC, Route("direct-http", HttpDescriptor("https://machine-c.example/"), profileC), ct),
            store.UpsertRouteAsync(profileC, Route($"reverse-http:{HubId}", ReverseDescriptor(profileC), profileC), ct));
        var registryB = new TransportFactoryRegistry();
        registryB.Register(
            harness.CreateForwardingFactory(
                Peer(ProfileB),
                ("https://hub-a.example/", InProcessHubHttpTransportFactory.HubBehavior.Healthy)));
        var factoryB = CreateProfileFactory(dataAccessLayer, ProfileB, registryB);
        var factoryA = CreateProfileFactory(
            dataAccessLayer,
            ProfileA,
            new TransportFactoryRegistry(),
            harness.Fixture.ReverseHttpServer);

        var identitiesSeen = await Task.WhenAll(
            ReadIdentityAsync(factoryA, profileC, ct),
            ReadIdentityAsync(factoryB, profileC, ct));

        Assert.Equal([ProfileA.ToString(), ProfileB.ToString()], identitiesSeen);
        Assert.Equal(2, (await store.GetRoutesAsync(profileC, ct)).Count);
    }

    [Fact]
    public async Task AgentStackMultiInstance_UnauthorizedClientInvokesPeer_IsRejected()
    {
        var ct = TransportScenarioSupport.TestToken();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executorRegistry = new TransportRegistry();
        executorRegistry.Register(new RecordingListener(() => opened.TrySetResult()));
        await using var server = new ReverseHttpServerTransportFactory(null, requireAuthenticatedRelays: true);
        await using var harness = await HubRelayHarness.CreateAsync(
            executorRegistry,
            ct,
            reverseHttpServer: server);
        var profileC = new EntityId(harness.ExecutorEntityId);
        var dataAccessLayer = await SeedProfilesAsync(profileC, ReverseRoute(profileC));
        var registry = new TransportFactoryRegistry();
        registry.Register(harness.CreateForwardingFactory());
        var factory = CreateProfileFactory(dataAccessLayer, ProfileB, registry);

        await Assert.ThrowsAsync<TransportException>(
            () => factory.ConnectToAsync(ProfileDescriptor(profileC), ct));

        Assert.False(opened.Task.IsCompleted);
    }

    [Fact]
    public async Task AgentStackMultiInstance_UnregisteredUnpublishedTarget_DoesNotFallBackToLocalExecution()
    {
        var profileC = new EntityId("cccccccc-cccc-4ccc-8ccc-cccccccc1593");
        var dataAccessLayer = await SeedProfilesAsync(profileC);
        var localAttempts = new FailingDirectFactory();
        var registry = new TransportFactoryRegistry();
        registry.Register(localAttempts);
        var factory = CreateProfileFactory(dataAccessLayer, ProfileA, registry);

        await Assert.ThrowsAsync<TransportException>(() => factory.ConnectToAsync(ProfileDescriptor(profileC)));

        Assert.Equal(0, localAttempts.Attempts);
    }

    [Fact]
    public async Task AgentStackMultiInstance_CallerCancelsPeerExecution_CancelsRelay()
    {
        var ct = TransportScenarioSupport.TestToken();
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executorRegistry = new TransportRegistry();
        executorRegistry.Register(new RecordingListener(onDisposed: () => disposed.TrySetResult()));
        await using var harness = await HubRelayHarness.CreateAsync(executorRegistry, ct);
        var profileC = new EntityId(harness.ExecutorEntityId);
        var dataAccessLayer = await SeedProfilesAsync(profileC);
        var factory = CreateProfileFactory(
            dataAccessLayer,
            ProfileA,
            new TransportFactoryRegistry(),
            harness.Fixture.ReverseHttpServer);
        await using var transport = await factory.ConnectToAsync(ProfileDescriptor(profileC), ct);
        var channel = await transport!.ConnectToMessageChannelAsync(Json("""{"type":"record"}"""), ct);

        await channel.DisposeAsync();

        await disposed.Task.WaitAsync(ct);
    }

    private static async Task<string> RunChatAsync(
        UserComputerProfileTransportFactory factory,
        EntityId target,
        string prompt,
        CancellationToken cancellationToken)
    {
        await using var transport = await factory.ConnectToAsync(BareProfileDescriptor(target), cancellationToken);
        using var client = new ChatClientOverTransport(transport!, TransportScenarioSupport.ChatClientRequest());
        return await TransportScenarioSupport.RunTurnAsync(client, prompt, cancellationToken);
    }

    private static async Task<string> ReadIdentityAsync(
        UserComputerProfileTransportFactory factory,
        EntityId target,
        CancellationToken cancellationToken)
    {
        await using var transport = await factory.ConnectToAsync(BareProfileDescriptor(target), cancellationToken);
        await using var channel = await transport!.ConnectToMessageChannelAsync(
            Json("""{"type":"identity"}"""),
            cancellationToken);
        var response = await channel.Reader.ReadAsync(cancellationToken);
        return response.GetProperty("peer").GetString()!;
    }

    private static UserComputerProfileTransportFactory CreateProfileFactory(
        IDataAccessLayer dataAccessLayer,
        EntityId localProfile,
        ITransportFactoryRegistry registry,
        ReverseHttpServerTransportFactory? liveRegistry = null)
        => new(
            dataAccessLayer,
            new WorkspaceEntitySession
            {
                UserEntityId = UserId,
                ComputerEntityId = ComputerId(localProfile),
                UserComputerProfileEntityId = localProfile,
            },
            registry,
            liveInboundRegistry: liveRegistry,
            timeProvider: new StaticTimeProvider(Now),
            trustedTransientRoutesEnabled: false);

    private static async Task<IDataAccessLayer> SeedProfilesAsync(
        EntityId profileC,
        string? profileCRoutes = null,
        string? profileDRoutes = null)
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var profiles = new[] { ProfileA, ProfileB, profileC, ProfileD };
        var documents = new List<JsonElement>
        {
            Json(
                $$"""
                {
                  "entity-id": "{{UserId}}",
                  "entity-types": ["entity", "user"],
                  "names": [["users", "by-id", "{{UserId}}"]]
                }
                """),
        };
        foreach (var profile in profiles)
        {
            var computer = ComputerId(profile);
            documents.Add(
                Json(
                    $$"""
                    {
                      "entity-id": "{{computer}}",
                      "entity-types": ["entity", "computer"],
                      "names": [["computers", "by-id", "{{computer}}"]]
                    }
                    """));
            var routes = profile == profileC
                ? profileCRoutes
                : profile == ProfileD
                    ? profileDRoutes
                    : null;
            documents.Add(
                Json(
                    $$"""
                    {
                      "entity-id": "{{profile}}",
                      "entity-types": ["entity", "user-computer-profile"],
                      "computer-reference": ["computers", "by-id", "{{computer}}"],
                      "user-reference": ["users", "by-id", "{{UserId}}"]
                      {{(routes is null ? string.Empty : "," + routes)}}
                    }
                    """));
        }

        await fixture.SeedManyValidAsync(documents);
        return fixture.DataAccessLayer;
    }

    private static EntityId ComputerId(EntityId profile)
    {
        var bytes = profile.Value.ToByteArray();
        bytes[0] ^= 0x5a;
        return new EntityId(new Guid(bytes));
    }

    private static string ReverseRoute(EntityId profile, string hubUrl = HubRelayHarness.DefaultHubUrl)
        => Routes(
            $$"""
            "reverse-http:{{HubId}}": {{RouteJson(ReverseDescriptor(profile, hubUrl), profile)}}
            """);

    private static string DirectRoute()
        => Routes(
            $$"""
            "direct-http": {{RouteJson(HttpDescriptor("https://machine-d.example/"), ProfileD, priority: 50)}}
            """);

    private static string ExpiredDirectRoute(EntityId profile)
        => Routes(
            $$"""
            "direct-http": {
              "descriptor": {{HttpDescriptor("https://expired.example/").GetRawText()}},
              "owner-profile-entity-id": "{{profile}}",
              "priority": 1,
              "last-confirmed": "{{Now.AddMinutes(-4):O}}",
              "expires-at": "{{Now.AddMinutes(-2):O}}"
            }
            """);

    private static string MultiRoutes(EntityId profile)
        => Routes(
            $$"""
            "direct-http": {{RouteJson(HttpDescriptor("https://preferred.example/"), profile, priority: 1)}},
            "reverse-http:{{HubId}}": {{RouteJson(ReverseDescriptor(profile), profile, priority: 2)}}
            """);

    private static string Routes(string routes)
        => $"\"reachability\": {{ \"routes\": {{ {routes} }} }}";

    private static string RouteJson(JsonElement descriptor, EntityId owner, int priority = 100)
        => $$"""
             {
               "descriptor": {{descriptor.GetRawText()}},
               "owner-profile-entity-id": "{{owner}}",
               "priority": {{priority}},
               "last-confirmed": "{{Now:O}}",
               "expires-at": "{{Now.AddMinutes(2):O}}"
             }
             """;

    private static ReachabilityRoute Route(
        string routeId,
        JsonElement descriptor,
        EntityId owner)
        => new()
        {
            RouteId = routeId,
            Descriptor = descriptor,
            OwnerProfileEntityId = owner,
            Priority = 100,
            LastConfirmed = Now,
            ExpiresAt = Now.AddMinutes(2),
        };

    private static JsonElement ReverseDescriptor(
        EntityId profile,
        string hubUrl = HubRelayHarness.DefaultHubUrl)
        => JsonSerializer.SerializeToElement(
            new Dictionary<string, object>
            {
                ["type"] = "reverse-http",
                ["hub-urls"] = new[] { hubUrl },
                ["entity-id"] = profile.ToString(),
            });

    private static JsonElement HttpDescriptor(string url)
        => JsonSerializer.SerializeToElement(
            new Dictionary<string, object>
            {
                ["type"] = "http",
                ["url"] = url,
            });

    private static JsonElement ProfileDescriptor(EntityId target)
        => Json(
            $$"""
            {
              "type": "user-computer-profile",
              "entity-id": "{{target}}",
              "target": { "type": "chat-client" }
            }
            """);

    private static JsonElement BareProfileDescriptor(EntityId target)
        => Json(
            $$"""
            {
              "type": "user-computer-profile",
              "entity-id": "{{target}}"
            }
            """);

    private static TransportRegistry ChatRegistry()
    {
        var registry = new TransportRegistry();
        registry.Register(new ChatClientTransportListener(new EchoChatClient()));
        return registry;
    }

    private static TransportPeerIdentity Peer(EntityId profile)
        => new()
        {
            AuthenticationScheme = "test",
            StablePeerId = profile.ToString(),
            UserEntityId = UserId.ToString(),
            UserComputerProfileEntityId = profile.ToString(),
        };

    private static JsonElement Json(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FailingDirectFactory : ITransportFactory
    {
        public int Attempts { get; private set; }

        public Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
        {
            if (!connectionDescriptor.TryGetProperty("type", out var type)
                || type.GetString() != "http")
            {
                return Task.FromResult<ITransport?>(null);
            }

            this.Attempts++;
            return Task.FromException<ITransport?>(new TransportException("Direct route unavailable."));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class IdentityListener(TransportPeerIdentityProvider identities) : ITransportListener
    {
        public async Task<IAsyncDisposable?> OnChannelOpenAsync(
            JsonElement request,
            IMessageChannel channel,
            CancellationToken ct = default)
        {
            if (request.GetProperty("type").GetString() != "identity")
            {
                return null;
            }

            var peer = identities.GetRequiredIdentity(channel);
            await channel.Writer.WriteAsync(
                JsonSerializer.SerializeToElement(
                    new Dictionary<string, string>
                    {
                        ["peer"] = peer.UserComputerProfileEntityId!,
                    }),
                ct);
            return new NoopLease();
        }

        public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingListener(
        Action? onOpen = null,
        Action? onDisposed = null) : ITransportListener
    {
        public Task<IAsyncDisposable?> OnChannelOpenAsync(
            JsonElement request,
            IMessageChannel channel,
            CancellationToken ct = default)
        {
            onOpen?.Invoke();
            return Task.FromResult<IAsyncDisposable?>(new CallbackLease(onDisposed));
        }

        public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CallbackLease(Action? callback) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            callback?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StaticTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
