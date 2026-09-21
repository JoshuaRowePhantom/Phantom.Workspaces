using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Testing;
using Phantom.Workspaces.Transport.ReverseHttp;

namespace Phantom.Workspaces.Transport.Tests;

public sealed class ReachabilityRouteStoreTests
{
    private static readonly EntityId ProfileId = new("10000000-0000-4000-8000-000000000003");
    private static readonly EntityId HubId = new("20000000-0000-4000-8000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReachabilityRouteStore_DirectListenerPublishesAfterBind_UpsertsOwnedDirectHttpSlot()
    {
        var (store, _) = await CreateStoreAsync();

        await store.UpsertRouteAsync(
            ProfileId,
            CreateRoute("direct-http", HttpDescriptor("https://machine.example/"), ProfileId));

        var route = Assert.Single(await store.GetRoutesAsync(ProfileId));
        Assert.Equal("direct-http", route.RouteId);
        Assert.Equal(ProfileId, route.OwnerProfileEntityId);
        Assert.Equal("https://machine.example/", route.Descriptor.GetProperty("url").GetString());
        Assert.True(route.ExpiresAt > route.LastConfirmed);
    }

    [Fact]
    public async Task ReachabilityRouteStore_ReverseClientRegistersWithHub_UpsertsPerHubSlot()
    {
        var (store, _) = await CreateStoreAsync();
        var http = new ReverseHttpClientTransportFactoryTests.FakeHttpTransportFactory();
        await using var factory = new ReverseHttpClientTransportFactory(
            http,
            "https://hub.example/",
            ProfileId.ToString(),
            store,
            HubId,
            new StaticTimeProvider(Now));

        await factory.EnsureRegisteredAsync();

        var route = Assert.Single(await store.GetRoutesAsync(ProfileId));
        Assert.Equal($"reverse-http:{HubId}", route.RouteId);
        Assert.Equal("reverse-http", route.Descriptor.GetProperty("type").GetString());
        Assert.Null(factory.LastReachabilityPublicationError);
    }

    [Fact]
    public async Task ReachabilityRouteStore_ReconnectRotatesUrl_UpdatesSameSlotInPlace()
    {
        var (store, _) = await CreateStoreAsync();
        var routeId = $"reverse-http:{HubId}";
        await store.UpsertRouteAsync(
            ProfileId,
            CreateRoute(routeId, ReverseDescriptor("https://old-hub.example/"), ProfileId));

        await store.UpsertRouteAsync(
            ProfileId,
            CreateRoute(routeId, ReverseDescriptor("https://new-hub.example/"), ProfileId));

        var route = Assert.Single(await store.GetRoutesAsync(ProfileId));
        Assert.Equal(routeId, route.RouteId);
        Assert.Equal("https://new-hub.example/", route.Descriptor.GetProperty("hub-urls")[0].GetString());
    }

    [Fact]
    public async Task ReachabilityRouteStore_GracefulShutdown_RemovesOnlyOwnedSlot()
    {
        var otherOwner = new EntityId("30000000-0000-4000-8000-000000000001");
        var (store, fixture) = await CreateStoreAsync(
            $$"""
            "direct-http": {{RouteJson(HttpDescriptor("https://machine.example/"), ProfileId)}},
            "reverse-http:{{HubId}}": {{RouteJson(ReverseDescriptor("https://other.example/", otherOwner), otherOwner)}}
            """);

        await store.RemoveRouteAsync(ProfileId, "direct-http", ProfileId);

        var routes = await store.GetRoutesAsync(ProfileId);
        var other = Assert.Single(routes);
        Assert.Equal(otherOwner, other.OwnerProfileEntityId);
        _ = fixture;
    }

    [Fact]
    public async Task ReachabilityRouteStore_ConcurrentDirectAndHubPublishers_DoNotClobberEachOther()
    {
        var (store, _) = await CreateStoreAsync();

        await Task.WhenAll(
            store.UpsertRouteAsync(
                ProfileId,
                CreateRoute("direct-http", HttpDescriptor("https://machine.example/"), ProfileId)),
            store.UpsertRouteAsync(
                ProfileId,
                CreateRoute($"reverse-http:{HubId}", ReverseDescriptor("https://hub.example/"), ProfileId)));

        var routes = await store.GetRoutesAsync(ProfileId);
        Assert.Equal(2, routes.Count);
        Assert.Contains(routes, route => route.RouteId == "direct-http");
        Assert.Contains(routes, route => route.RouteId == $"reverse-http:{HubId}");
    }

    [Fact]
    public async Task ReachabilityRouteStore_StartupCrashRecovery_ClearsOnlyOwnerScopedStaleSlots()
    {
        var otherOwner = new EntityId("30000000-0000-4000-8000-000000000001");
        var (store, _) = await CreateStoreAsync(
            $$"""
            "direct-http": {{RouteJson(HttpDescriptor("https://machine.example/"), ProfileId)}},
            "reverse-http:{{HubId}}": {{RouteJson(ReverseDescriptor("https://other.example/", otherOwner), otherOwner)}}
            """);

        await store.ClearOwnedRoutesAsync(ProfileId, ProfileId);

        var other = Assert.Single(await store.GetRoutesAsync(ProfileId));
        Assert.Equal(otherOwner, other.OwnerProfileEntityId);
    }

    [Fact]
    public async Task ReachabilityRouteStore_RoutePersistFailsButRegistrationLive_SurfacesAndKeepsLocalReachability()
    {
        var failingStore = new FailingRouteStore();
        var http = new ReverseHttpClientTransportFactoryTests.FakeHttpTransportFactory();
        await using var factory = new ReverseHttpClientTransportFactory(
            http,
            "https://hub.example/",
            ProfileId.ToString(),
            failingStore,
            HubId,
            new StaticTimeProvider(Now));

        var channel = await factory.EnsureRegisteredAsync();

        Assert.NotNull(channel);
        Assert.NotNull(factory.LastReachabilityPublicationError);
        Assert.Equal(["https://hub.example/"], factory.HubUrls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ReachabilityRouteStore_ExpiresAtNotAfterLastConfirmed_IsRejectedAtWriteTime(int secondsAfter)
    {
        var (store, _) = await CreateStoreAsync();
        var route = CreateRoute(
            "direct-http",
            HttpDescriptor("https://machine.example/"),
            ProfileId,
            expiresAt: Now.AddSeconds(secondsAfter));

        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertRouteAsync(ProfileId, route));
        Assert.Empty(await store.GetRoutesAsync(ProfileId));
    }

    [Fact]
    public async Task ReachabilityRouteStore_ReverseDescriptorEntityIdMismatchesOwner_IsRejectedAtWriteTime()
    {
        var (store, _) = await CreateStoreAsync();
        var route = CreateRoute(
            $"reverse-http:{HubId}",
            ReverseDescriptor(
                "https://hub.example/",
                new EntityId("30000000-0000-4000-8000-000000000001")),
            ProfileId);

        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertRouteAsync(ProfileId, route));
        Assert.Empty(await store.GetRoutesAsync(ProfileId));
    }

    [Theory]
    [InlineData("https://machine.example/?access_token=secret")]
    [InlineData("https://machine.example/#token=secret")]
    [InlineData("https://user:pass@machine.example/")]
    [InlineData("http://public.example/")]
    public async Task ReachabilityRouteStore_UrlWithCredentialQueryOrFragment_IsRejectedAtWriteTime(string url)
    {
        var (store, _) = await CreateStoreAsync();
        var route = CreateRoute("direct-http", HttpDescriptor(url), ProfileId);

        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertRouteAsync(ProfileId, route));
        Assert.Empty(await store.GetRoutesAsync(ProfileId));
    }

    private static ReachabilityRoute CreateRoute(
        string routeId,
        JsonElement descriptor,
        EntityId owner,
        DateTimeOffset? expiresAt = null)
        => new()
        {
            RouteId = routeId,
            Descriptor = descriptor,
            OwnerProfileEntityId = owner,
            Priority = 100,
            LastConfirmed = Now,
            ExpiresAt = expiresAt ?? Now.AddMinutes(2),
        };

    private static JsonElement HttpDescriptor(string url)
        => JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["type"] = "http",
            ["url"] = url,
        });

    private static JsonElement ReverseDescriptor(string url, EntityId? entityId = null)
        => JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["type"] = "reverse-http",
            ["hub-urls"] = new[] { url },
            ["entity-id"] = (entityId ?? ProfileId).ToString(),
        });

    private static string RouteJson(JsonElement descriptor, EntityId owner)
        => $$"""
             {
               "descriptor": {{descriptor.GetRawText()}},
               "owner-profile-entity-id": "{{owner}}",
               "last-confirmed": "{{Now:O}}",
               "expires-at": "{{Now.AddMinutes(2):O}}"
             }
             """;

    private static async Task<(DataAccessReachabilityRouteStore Store, ValidatingEntitySeedFixture Fixture)> CreateStoreAsync(
        string routes = "")
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        await fixture.SeedManyValidAsync(
            [
                Parse(
                    """
                    {
                      "entity-id": "10000000-0000-4000-8000-000000000001",
                      "entity-types": ["entity", "user"],
                      "names": [["users", "username", "reachability-store"]]
                    }
                    """),
                Parse(
                    """
                    {
                      "entity-id": "10000000-0000-4000-8000-000000000002",
                      "entity-types": ["entity", "computer"],
                      "names": [["computers", "name", "reachability-store"]]
                    }
                    """),
                Parse(
                    $$"""
                    {
                      "entity-id": "{{ProfileId}}",
                      "entity-types": ["entity", "user-computer-profile"],
                      "computer-reference": ["computers", "name", "reachability-store"],
                      "user-reference": ["users", "username", "reachability-store"],
                      "reachability": { "routes": { {{routes}} } }
                    }
                    """),
            ]);
        return (new DataAccessReachabilityRouteStore(fixture.DataAccessLayer), fixture);
    }

    private static JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FailingRouteStore : IReachabilityRouteStore
    {
        public Task<IReadOnlyList<ReachabilityRoute>> GetRoutesAsync(
            EntityId profileEntityId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ReachabilityRoute>>([]);

        public Task UpsertRouteAsync(
            EntityId profileEntityId,
            ReachabilityRoute route,
            CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("Persistence unavailable."));

        public Task RemoveRouteAsync(
            EntityId profileEntityId,
            string routeId,
            EntityId ownerProfileEntityId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ClearOwnedRoutesAsync(
            EntityId profileEntityId,
            EntityId ownerProfileEntityId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StaticTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
