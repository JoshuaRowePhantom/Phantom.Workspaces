using System;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using System.Text.Json;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Testing;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Http;
using Phantom.Workspaces.Transport.ReverseHttp;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class WorkspacesWebHostTests
{
    [Fact]
    public async Task Constructor_ExposesTransportConnectionStatusRegistry()
    {
        var statusRegistry = new ReverseConnectionStatusRegistry();

        await using var host = new WorkspacesWebHost(statusRegistry);

        // The host now sources its reverse hub from the transport connection-status registry rather
        // than a ReverseExecutionRegistry, and exposes the same instance it maps into the server.
        Assert.Same(statusRegistry, host.ConnectionStatusRegistry);
        Assert.False(host.IsRunning);
        Assert.Null(host.ListenUrl);
    }

    [Fact]
    public async Task StartAsync_MapsTransportConnectEndpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = new WorkspacesWebHost(new ReverseConnectionStatusRegistry());
        var settings = new RemoteHostingSettings { Enabled = true, ListenUrl = $"http://127.0.0.1:{GetFreePort()}" };
        var dal = new InMemoryDataAccessLayer();

        await host.StartAsync(settings, dal, ct);
        try
        {
            var patterns = host.GetMappedRoutePatterns();
            Assert.Contains(HttpServerTransportFactory.EndpointPath, patterns);
        }
        finally
        {
            await host.StopAsync(ct);
        }
    }

    [Fact]
    public async Task StartAsync_MapsTransportReverseEndpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = new WorkspacesWebHost(new ReverseConnectionStatusRegistry());
        var settings = new RemoteHostingSettings { Enabled = true, ListenUrl = $"http://127.0.0.1:{GetFreePort()}" };
        var dal = new InMemoryDataAccessLayer();

        await host.StartAsync(settings, dal, ct);
        try
        {
            var patterns = host.GetMappedRoutePatterns();
            Assert.Contains("/reverse-transport/connect", patterns);
        }
        finally
        {
            await host.StopAsync(ct);
        }
    }

    [Fact]
    public async Task ReverseHttpClient_AgainstEmbeddedHost_RegistersSuccessfully()
    {
        var ct = TestContext.Current.CancellationToken;
        var statusRegistry = new ReverseConnectionStatusRegistry();
        await using var host = new WorkspacesWebHost(statusRegistry);
        var port = GetFreePort();
        var listenUrl = $"http://127.0.0.1:{port}";
        var settings = new RemoteHostingSettings { Enabled = true, ListenUrl = listenUrl };
        var dal = new InMemoryDataAccessLayer();

        await host.StartAsync(settings, dal, ct);
        try
        {
            await WaitForHostAsync(listenUrl, ct);

            await using var client = new ReverseHttpClientTransportFactory(listenUrl, entityId: "test-instance");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            // Regression guard for #1209: prior to the /transport/connect mapping this threw
            // "Failed to connect: … '404' when status code '101' was expected".
            var channel = await client.EnsureRegisteredAsync(timeout.Token);
            Assert.NotNull(channel);

            var connected = await WaitForRegistrationAsync(statusRegistry, "test-instance", TimeSpan.FromSeconds(30), ct);
            Assert.True(connected, "Expected the embedded host to record the client instance registration.");
        }
        finally
        {
            await host.StopAsync(ct);
        }
    }

    [Fact]
    public async Task StopAsync_DisposesHttpServerTransportFactory()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = new WorkspacesWebHost(new ReverseConnectionStatusRegistry());
        var settings = new RemoteHostingSettings { Enabled = true, ListenUrl = $"http://127.0.0.1:{GetFreePort()}" };
        var dal = new InMemoryDataAccessLayer();

        await host.StartAsync(settings, dal, ct);
        Assert.NotNull(host.HttpServerTransportFactory);
        Assert.False(host.HttpServerTransportFactoryWasDisposed);

        await host.StopAsync(ct);

        Assert.Null(host.HttpServerTransportFactory);
        Assert.True(host.HttpServerTransportFactoryWasDisposed);
    }

    [Fact]
    public async Task WorkspacesWebHost_NonLoopbackListenUrl_BindsRequestedAddress()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = new WorkspacesWebHost(new ReverseConnectionStatusRegistry());
        var port = GetFreePort();
        var settings = new RemoteHostingSettings { Enabled = true, ListenUrls = [$"http://0.0.0.0:{port}"] };
        var dal = new InMemoryDataAccessLayer();

        await host.StartAsync(settings, dal, ct);
        try
        {
            await WaitForHostAsync($"http://127.0.0.1:{port}", ct);
            Assert.Contains(host.ListenUrls, address => Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Host == "0.0.0.0");
        }
        finally
        {
            await host.StopAsync(ct);
        }
    }

    [Fact]
    public async Task WorkspacesWebHost_MultipleListenUrls_BindsAllRequestedAddresses()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = new WorkspacesWebHost(new ReverseConnectionStatusRegistry());
        var port1 = GetFreePort();
        var port2 = GetFreePort();
        var url1 = $"http://127.0.0.1:{port1}";
        var url2 = $"http://127.0.0.1:{port2}";
        var settings = new RemoteHostingSettings { Enabled = true, ListenUrls = [url1, url2] };
        var dal = new InMemoryDataAccessLayer();

        await host.StartAsync(settings, dal, ct);
        try
        {
            await WaitForHostAsync(url1, ct);
            await WaitForHostAsync(url2, ct);
            Assert.Contains(host.ListenUrls, address => string.Equals(address, url1, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(host.ListenUrls, address => string.Equals(address, url2, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await host.StopAsync(ct);
        }
    }

    [Fact]
    public async Task ReachabilityRouteStore_DirectListenerPublishesAfterBind_UpsertsOwnedDirectHttpSlot()
    {
        var ct = TestContext.Current.CancellationToken;
        var profileId = new EntityId("10000000-0000-4000-8000-000000000003");
        var fixture = await CreateReachabilityFixtureAsync();
        var routeStore = new DataAccessReachabilityRouteStore(fixture.DataAccessLayer);
        var statusRegistry = new ReverseConnectionStatusRegistry();
        await using var reverseServer = new ReverseHttpServerTransportFactory(statusRegistry);
        await using var host = new WorkspacesWebHost(
            statusRegistry,
            reverseServer,
            routeStore,
            profileId);
        var settings = new RemoteHostingSettings
        {
            Enabled = true,
            ListenUrl = $"http://127.0.0.1:{GetFreePort()}",
        };

        Assert.Empty(await routeStore.GetRoutesAsync(profileId, ct));
        await host.StartAsync(settings, fixture.DataAccessLayer, ct);
        var route = Assert.Single(await routeStore.GetRoutesAsync(profileId, ct));

        Assert.Equal("direct-http", route.RouteId);
        Assert.Equal(host.ListenUrl, route.Descriptor.GetProperty("url").GetString()?.TrimEnd('/'));

        await host.StopAsync(ct);
        Assert.Empty(await routeStore.GetRoutesAsync(profileId, ct));
    }

    [Fact]
    public async Task ReachabilityRouteStore_PublicEndpointChanges_UpdatesOwnedDirectHttpSlot()
    {
        var ct = TestContext.Current.CancellationToken;
        var profileId = new EntityId("10000000-0000-4000-8000-000000000003");
        var fixture = await CreateReachabilityFixtureAsync();
        var routeStore = new DataAccessReachabilityRouteStore(fixture.DataAccessLayer);
        var statusRegistry = new ReverseConnectionStatusRegistry();
        await using var reverseServer = new ReverseHttpServerTransportFactory(statusRegistry);
        await using var host = new WorkspacesWebHost(
            statusRegistry,
            reverseServer,
            routeStore,
            profileId);
        var settings = new RemoteHostingSettings
        {
            Enabled = true,
            ListenUrl = $"http://127.0.0.1:{GetFreePort()}",
        };
        await host.StartAsync(settings, fixture.DataAccessLayer, ct);

        await host.SetPublishedEndpointAsync("https://machine.example/tunnel", ct);

        var route = Assert.Single(await routeStore.GetRoutesAsync(profileId, ct));
        Assert.Equal("https://machine.example/tunnel", route.Descriptor.GetProperty("url").GetString());
    }

    [Fact]
    public async Task ReachabilityRouteStore_DirectListenerLeaseInterval_RenewsOwnedSlot()
    {
        var ct = TestContext.Current.CancellationToken;
        var profileId = new EntityId("10000000-0000-4000-8000-000000000003");
        var fixture = await CreateReachabilityFixtureAsync();
        var routeStore = new RecordingRenewalRouteStore();
        var statusRegistry = new ReverseConnectionStatusRegistry();
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 9, 18, 18, 0, 0, TimeSpan.Zero));
        await using var reverseServer = new ReverseHttpServerTransportFactory(statusRegistry);
        await using var host = new WorkspacesWebHost(
            statusRegistry,
            reverseServer,
            routeStore,
            profileId,
            timeProvider);
        var settings = new RemoteHostingSettings
        {
            Enabled = true,
            ListenUrl = $"http://127.0.0.1:{GetFreePort()}",
        };
        await host.StartAsync(settings, fixture.DataAccessLayer, ct);
        var initial = await routeStore.PublishedRoutes.ReadAsync(ct);

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var renewed = await routeStore.PublishedRoutes.ReadAsync(ct);

        Assert.Equal(initial.RouteId, renewed.RouteId);
        Assert.Equal(initial.ExpiresAt.AddMinutes(1), renewed.ExpiresAt);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task WaitForHostAsync(string listenUrl, CancellationToken cancellationToken)
    {
        using var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(TimeSpan.FromSeconds(30));
        while (!overall.IsCancellationRequested)
        {
            try
            {
                using var response = await httpClient.GetAsync(listenUrl, overall.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
                // Server still starting.
            }

            await Task.Yield();
        }

        throw new TimeoutException($"Host did not become ready at {listenUrl}.");
    }

    private static async Task<bool> WaitForRegistrationAsync(
        ReverseConnectionStatusRegistry statusRegistry,
        string entityId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (statusRegistry.GetConnectedInstances().Any(s => s.ClientInstanceId == entityId))
            {
                return true;
            }

            await Task.Yield();
        }

        return false;
    }

    private static async Task<ValidatingEntitySeedFixture> CreateReachabilityFixtureAsync()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        await fixture.SeedManyValidAsync(
            [
                JsonDocument.Parse(
                    """
                    {
                      "entity-id": "10000000-0000-4000-8000-000000000001",
                      "entity-types": ["entity", "user"],
                      "names": [["users", "username", "web-host-reachability"]]
                    }
                    """).RootElement,
                JsonDocument.Parse(
                    """
                    {
                      "entity-id": "10000000-0000-4000-8000-000000000002",
                      "entity-types": ["entity", "computer"],
                      "names": [["computers", "name", "web-host-reachability"]]
                    }
                    """).RootElement,
                JsonDocument.Parse(
                    """
                    {
                      "entity-id": "10000000-0000-4000-8000-000000000003",
                      "entity-types": ["entity", "user-computer-profile"],
                      "computer-reference": ["computers", "name", "web-host-reachability"],
                      "user-reference": ["users", "username", "web-host-reachability"]
                    }
                    """).RootElement,
            ]);
        return fixture;
    }

    private sealed class RecordingRenewalRouteStore : IReachabilityRouteStore
    {
        private readonly System.Threading.Channels.Channel<ReachabilityRoute> publishedRoutes =
            System.Threading.Channels.Channel.CreateUnbounded<ReachabilityRoute>();

        public System.Threading.Channels.ChannelReader<ReachabilityRoute> PublishedRoutes
            => this.publishedRoutes.Reader;

        public Task<IReadOnlyList<ReachabilityRoute>> GetRoutesAsync(
            EntityId profileEntityId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ReachabilityRoute>>([]);

        public Task UpsertRouteAsync(
            EntityId profileEntityId,
            ReachabilityRoute route,
            CancellationToken cancellationToken = default)
        {
            this.publishedRoutes.Writer.TryWrite(route);
            return Task.CompletedTask;
        }

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
}
