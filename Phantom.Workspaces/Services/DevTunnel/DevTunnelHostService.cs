using System;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Default <see cref="IDevTunnelHostService"/> that orchestrates the management client and relay host
/// to expose the GUI's local listening port over a Workspaces-owned tunnel. The Dev Tunnels SDK types
/// are confined to the injected <see cref="IDevTunnelManagementClient"/> and
/// <see cref="IDevTunnelRelayHost"/> implementations, so this orchestration is fully unit-testable.
/// </summary>
public sealed class DevTunnelHostService : IDevTunnelHostService
{
    private readonly IDevTunnelManagementClient managementClient;
    private readonly IDevTunnelRelayHost relayHost;
    private DevTunnelDescriptor? descriptor;

    public DevTunnelHostService(
        IDevTunnelManagementClient managementClient,
        IDevTunnelRelayHost relayHost)
    {
        this.managementClient = managementClient ?? throw new ArgumentNullException(nameof(managementClient));
        this.relayHost = relayHost ?? throw new ArgumentNullException(nameof(relayHost));
    }

    public DevTunnelHostStatus Status { get; private set; } = DevTunnelHostStatus.Stopped;

    public event EventHandler<DevTunnelHostStatus>? StatusChanged;

    public async Task StartAsync(int localPort, string protocol, DevTunnelConfiguration configuration, CancellationToken cancellationToken = default)
    {
        this.SetStatus(this.Status with { State = DevTunnelHostState.Starting, AccessMode = configuration.AccessMode, LastError = null });
        try
        {
            this.descriptor = await this.managementClient
                .EnsureTunnelAsync(configuration.TunnelId, configuration.TunnelName, cancellationToken)
                .ConfigureAwait(false);

            await this.managementClient
                .SetSingleForwardedPortAsync(this.descriptor.TunnelId, localPort, protocol, cancellationToken)
                .ConfigureAwait(false);

            await this.managementClient
                .ApplyAccessModeAsync(this.descriptor.TunnelId, configuration.AccessMode, cancellationToken)
                .ConfigureAwait(false);

            await this.relayHost
                .StartAsync(this.descriptor.TunnelId, localPort, cancellationToken)
                .ConfigureAwait(false);

            var accessPointUrl = await this.managementClient
                .GetAccessPointUrlAsync(this.descriptor.TunnelId, localPort, cancellationToken)
                .ConfigureAwait(false);

            this.SetStatus(new DevTunnelHostStatus(
                DevTunnelHostState.Hosting,
                accessPointUrl,
                this.descriptor.TunnelId,
                configuration.AccessMode,
                LastError: null));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            this.SetStatus(new DevTunnelHostStatus(
                DevTunnelHostState.Error,
                AccessPointUrl: null,
                this.descriptor?.TunnelId,
                configuration.AccessMode,
                exception.Message));
            throw;
        }
    }

    public async Task ReconfigureAsync(int localPort, string protocol, DevTunnelConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await this.StopAsync(cancellationToken).ConfigureAwait(false);
        await this.StartAsync(localPort, protocol, configuration, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (this.relayHost.IsRunning)
        {
            await this.relayHost.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        this.SetStatus(new DevTunnelHostStatus(
            DevTunnelHostState.Stopped,
            AccessPointUrl: null,
            this.descriptor?.TunnelId,
            this.Status.AccessMode,
            LastError: null));
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync().ConfigureAwait(false);
        await this.relayHost.DisposeAsync().ConfigureAwait(false);
    }

    private void SetStatus(DevTunnelHostStatus status)
    {
        this.Status = status;
        this.StatusChanged?.Invoke(this, status);
    }
}
