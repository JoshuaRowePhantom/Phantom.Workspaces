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
        this.relayHost.ConnectionStateChanged += this.OnRelayConnectionStateChanged;
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

            var connectToken = await this.managementClient
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
                ConnectToken: connectToken,
                LastError: null));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            this.SetStatus(new DevTunnelHostStatus(
                DevTunnelHostState.Error,
                AccessPointUrl: null,
                this.descriptor?.TunnelId,
                configuration.AccessMode,
                ConnectToken: null,
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
            ConnectToken: null,
            LastError: null));
    }

    public async ValueTask DisposeAsync()
    {
        // StopAsync tears down any in-flight SDK relay host via TunnelRelayDevTunnelHost.StopAsync,
        // which now routes through the safe-dispose helper that consumes the terminal shutdown
        // exceptions the SDK produces from its fire-and-forget background work (issue #1301).
        // DisposeAsync on the wrapper is a no-op once StopAsync has torn the SDK host down, but we
        // still call it to transfer ownership cleanly.
        this.relayHost.ConnectionStateChanged -= this.OnRelayConnectionStateChanged;
        await this.StopAsync().ConfigureAwait(false);
        await this.relayHost.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Surfaces the relay host's underlying SDK connection-state transitions (issue #1375) so the UI no
    /// longer appears healthy while remote hosting is down. A reported <c>Reconnecting</c> moves hosting
    /// to <see cref="DevTunnelHostState.Reconnecting"/>, a recovered <c>Connected</c> back to
    /// <see cref="DevTunnelHostState.Hosting"/>, and an abandoned <c>Failed</c> to
    /// <see cref="DevTunnelHostState.Error"/>. Ignored while stopped/starting, where the relay is not the
    /// authoritative signal (StartAsync/StopAsync own those transitions).
    /// </summary>
    private void OnRelayConnectionStateChanged(object? sender, DevTunnelConnectionState state)
    {
        if (this.Status.State is DevTunnelHostState.Stopped or DevTunnelHostState.Starting)
        {
            return;
        }

        switch (state)
        {
            case DevTunnelConnectionState.Reconnecting:
                this.SetStatus(this.Status with { State = DevTunnelHostState.Reconnecting });
                break;
            case DevTunnelConnectionState.Connected:
                this.SetStatus(this.Status with { State = DevTunnelHostState.Hosting, LastError = null });
                break;
            case DevTunnelConnectionState.Failed:
                this.SetStatus(this.Status with
                {
                    State = DevTunnelHostState.Error,
                    LastError = this.Status.LastError ?? "The dev tunnel relay disconnected and could not be re-established.",
                });
                break;
        }
    }

    private void SetStatus(DevTunnelHostStatus status)
    {
        this.Status = status;
        this.StatusChanged?.Invoke(this, status);
    }
}
