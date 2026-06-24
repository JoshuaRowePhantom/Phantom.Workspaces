using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DevTunnels.Connections;
using Microsoft.DevTunnels.Contracts;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Concrete <see cref="IDevTunnelRelayHost"/> wrapping the Dev Tunnels SDK
/// <see cref="TunnelRelayTunnelHost"/>. Thin SDK glue: starts an in-process relay host for the ensured
/// tunnel and forwards relay connections to the local listening port. The rich SDK <see cref="Tunnel"/>
/// to host is read from the management wrapper that ensured it.
/// </summary>
internal sealed class TunnelRelayDevTunnelHost : IDevTunnelRelayHost
{
    private readonly DevTunnelManagementClientWrapper managementClientWrapper;
    private TunnelRelayTunnelHost? relayHost;

    public TunnelRelayDevTunnelHost(DevTunnelManagementClientWrapper managementClientWrapper)
    {
        this.managementClientWrapper = managementClientWrapper ?? throw new ArgumentNullException(nameof(managementClientWrapper));
    }

    public bool IsRunning => this.relayHost is not null;

    public async Task StartAsync(string tunnelId, int localPort, CancellationToken cancellationToken = default)
    {
        // Fetch a fresh, connect-ready tunnel (includes the forwarded ports the SDK relay host requires;
        // the cached tunnel's ports were cleared by the access-control update, which cannot carry ports).
        var tunnel = await this.managementClientWrapper
            .GetConnectReadyTunnelAsync(tunnelId, cancellationToken)
            .ConfigureAwait(false);

        var host = new TunnelRelayTunnelHost(
            this.managementClientWrapper.ManagementClient,
            new TraceSource(nameof(TunnelRelayDevTunnelHost)));
        await host.ConnectAsync(tunnel, new TunnelConnectionOptions { EnableRetry = true }, cancellationToken).ConfigureAwait(false);
        this.relayHost = host;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (this.relayHost is not null)
        {
            await this.relayHost.DisposeAsync().ConfigureAwait(false);
            this.relayHost = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync().ConfigureAwait(false);
    }
}
