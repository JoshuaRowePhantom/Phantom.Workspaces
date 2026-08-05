using System;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Management;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Services.DevTunnel;
using Phantom.Workspaces.Transport.ReverseHttp;
using Phantom.Workspaces.Web.Server;

namespace Phantom.Workspaces.IntegrationTests;

/// <summary>
/// xUnit async-lifetime fixture that creates and hosts a real dev tunnel in Anonymous access mode,
/// starts a local Kestrel server with the transport reverse WebSocket endpoint
/// (<c>/reverse-transport/connect</c>), and tears down both on disposal. Requires
/// <c>PHANTOM_INTEGRATION_GITHUB_TOKEN</c> to be set; if absent the fixture initialises silently so
/// individual tests can skip gracefully with <c>Skip.If</c>.
/// </summary>
public sealed class InProcessDevTunnelFixture : IAsyncLifetime
{
    private static readonly ProductInfoHeaderValue UserAgent =
        new("Phantom.Workspaces.IntegrationTests", "1.0");

    private WebApplication? app;
    private IDevTunnelHostService? hostService;
    private ITunnelManagementClient? managementClient;
    private string? tunnelId;
    private readonly CancellationTokenSource appLifetime = new();

    /// <summary>The relay base URI that dev-tunnel clients connect through.</summary>
    public Uri? RelayBaseUri { get; private set; }

    /// <summary>The local TCP port the Kestrel server listens on; 0 when not started.</summary>
    public int LocalPort { get; private set; }

    /// <summary>The tunnel auth token (null — Anonymous access mode requires no token).</summary>
    public string? AccessToken => null;

    /// <summary>The name label of the hosted tunnel; null when not started.</summary>
    public string? TunnelName { get; private set; }

    /// <summary>The registry that records reverse transport connection status from test clients.</summary>
    public ReverseConnectionStatusRegistry StatusRegistry { get; } = new();

    public async ValueTask InitializeAsync()
    {
        var token = Environment.GetEnvironmentVariable("PHANTOM_INTEGRATION_GITHUB_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            // No credentials: initialise silently so tests can skip via Skip.If.
            return;
        }

        LocalPort = GetFreePort();
        TunnelName = $"pw-integ-{Guid.NewGuid():N}";

        // Start a local Kestrel server that accepts WebSocket upgrade requests and handles them as
        // reverse transport connections.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, LocalPort));
        app = builder.Build();
        app.UseWebSockets();
        app.MapTransportReverseEndpoints(new ReverseHttpServerTransportFactory(StatusRegistry), StatusRegistry);
        await app.StartAsync(appLifetime.Token).ConfigureAwait(false);

        // Create and host the dev tunnel pointing at LocalPort (Anonymous mode: no connect token).
        var factory = new DevTunnelServiceFactory(new StaticTokenProvider(token));
        hostService = factory.CreateHostService();
        await hostService.StartAsync(
            LocalPort,
            protocol: "http",
            new DevTunnelConfiguration
            {
                TunnelName = TunnelName,
                AccessMode = DevTunnelAccessMode.Anonymous,
            },
            CancellationToken.None).ConfigureAwait(false);

        tunnelId = hostService.Status.TunnelId;
        RelayBaseUri = new Uri(hostService.Status.AccessPointUrl
            ?? throw new InvalidOperationException("Tunnel started but AccessPointUrl was null."));

        // A separate management client for best-effort tunnel deletion on disposal.
        managementClient = new TunnelManagementClient(
            UserAgent,
            async () => new AuthenticationHeaderValue(TunnelAuthenticationSchemes.GitHub, token),
            ManagementApiVersions.Version20230927Preview);
    }

    public async ValueTask DisposeAsync()
    {
        if (hostService is not null)
        {
            await hostService.DisposeAsync().ConfigureAwait(false);
        }

        if (managementClient is not null && !string.IsNullOrEmpty(tunnelId))
        {
            try
            {
                await managementClient.DeleteTunnelAsync(
                    new Tunnel { TunnelId = tunnelId },
                    new TunnelRequestOptions(),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup; the tunnel will expire via service TTL if deletion fails.
            }
        }

        await appLifetime.CancelAsync().ConfigureAwait(false);

        if (app is not null)
        {
            await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }

        appLifetime.Dispose();
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

    private sealed class StaticTokenProvider(string token) : IDevTunnelAuthTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(token);
    }
}
