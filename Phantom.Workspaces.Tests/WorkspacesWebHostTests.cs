using System;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Services;
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
}
