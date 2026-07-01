using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Management;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Concrete <see cref="IDevTunnelManagementClient"/> / <see cref="IDevTunnelLookupClient"/> that wraps
/// the Dev Tunnels SDK <see cref="ITunnelManagementClient"/>. This is thin SDK glue — all orchestration
/// and resolution logic (and their tests) live in <see cref="DevTunnelHostService"/> /
/// <see cref="DevTunnelEndpointResolver"/> behind the seams; this type just maps the seam calls onto the
/// SDK. The current SDK <see cref="Tunnel"/> object is cached so port/access/url operations reuse the
/// rich SDK state keyed by the domain-level tunnel id.
/// </summary>
internal sealed class DevTunnelManagementClientWrapper : IDevTunnelManagementClient, IDevTunnelLookupClient
{
    private readonly ITunnelManagementClient managementClient;
    private Tunnel? currentTunnel;

    public DevTunnelManagementClientWrapper(ITunnelManagementClient managementClient)
    {
        this.managementClient = managementClient ?? throw new ArgumentNullException(nameof(managementClient));
    }

    /// <summary>The Dev Tunnels SDK management client, for SDK-glue collaborators (e.g. the relay host).</summary>
    internal ITunnelManagementClient ManagementClient => this.managementClient;

    /// <summary>The SDK tunnel last ensured/fetched, for SDK-glue collaborators (e.g. the relay host).</summary>
    internal Tunnel? CurrentTunnel => this.currentTunnel;

    /// <summary>
    /// Fetches a fresh copy of the ensured tunnel that is ready to host: it includes the forwarded
    /// <see cref="Tunnel.Ports"/> (required non-null by the SDK relay host) and host access tokens. Use
    /// this immediately before connecting the relay host, since access-control updates clear the cached
    /// tunnel's ports (ports cannot be sent on a tunnel update).
    /// </summary>
    internal async Task<Tunnel> GetConnectReadyTunnelAsync(string tunnelId, CancellationToken cancellationToken = default)
    {
        var tunnel = this.RequireCurrentTunnel(tunnelId);
        var refreshed = await this.managementClient
            .GetTunnelAsync(tunnel, CreateGetTunnelOptions(), cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Dev tunnel '{tunnelId}' could not be fetched for hosting.");

        // The SDK relay host requires a non-null Ports collection.
        refreshed.Ports ??= [];
        this.currentTunnel = refreshed;
        return refreshed;
    }

    public async Task<DevTunnelDescriptor> EnsureTunnelAsync(
        string? tunnelId,
        string? tunnelName,
        CancellationToken cancellationToken = default)
    {
        // The logical tunnel name is carried as a label rather than the SDK custom Name: custom tunnel
        // names require a service feature that is disabled for most accounts (a 403 "allow custom
        // tunnel names feature is disabled"), whereas labels are generally available. Every
        // Workspaces-owned tunnel also carries a stable marker label so a client can auto-discover it.
        var isAuto = DevTunnelNaming.IsAuto(tunnelName);
        var nameLabel = isAuto ? null : tunnelName;

        var markerTunnels = await this.ListWorkspacesTunnelsAsync(CreateHostRequestOptions(nameLabel), cancellationToken)
            .ConfigureAwait(false);

        Tunnel? tunnel = null;
        if (!string.IsNullOrWhiteSpace(tunnelId))
        {
            tunnel = markerTunnels.FirstOrDefault(candidate => string.Equals(candidate.TunnelId, tunnelId, StringComparison.Ordinal));
        }

        if (tunnel is null && nameLabel is not null)
        {
            tunnel = markerTunnels.FirstOrDefault(candidate => HasLabel(candidate, nameLabel));
        }
        else if (tunnel is null && isAuto)
        {
            if (markerTunnels.Count > 1)
            {
                throw new InvalidOperationException(
                    "Multiple Workspaces dev tunnels exist; set a specific dev tunnel name instead of \"auto\".");
            }

            tunnel = markerTunnels.Count == 1 ? markerTunnels[0] : null;
        }

        tunnel ??= await this.managementClient
            .CreateTunnelAsync(
                new Tunnel { Labels = BuildLabels(nameLabel) },
                CreateHostRequestOptions(),
                cancellationToken)
            .ConfigureAwait(false);

        this.currentTunnel = tunnel;
        return new DevTunnelDescriptor(
            tunnel.TunnelId ?? string.Empty,
            isAuto ? tunnel.TunnelId ?? string.Empty : tunnelName!);
    }

    public async Task SetSingleForwardedPortAsync(
        string tunnelId,
        int localPort,
        string protocol,
        CancellationToken cancellationToken = default)
    {
        var tunnel = this.RequireCurrentTunnel(tunnelId);
        var requestOptions = CreateHostRequestOptions();
        var portNumber = checked((ushort)localPort);

        var existingPorts = await this.managementClient
            .ListTunnelPortsAsync(tunnel, requestOptions, cancellationToken)
            .ConfigureAwait(false);

        foreach (var stalefort in (existingPorts ?? []).Where(port => port.PortNumber != portNumber))
        {
            await this.managementClient
                .DeleteTunnelPortAsync(tunnel, stalefort.PortNumber, requestOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        // Unconditionally delete the target port to allow protocol changes.
        // DeleteTunnelPortAsync returns false (not throws) if the port does not exist.
        // We must NOT use CreateOrUpdateTunnelPortAsync after this because it acts as an upsert
        // (PUT) — if the server still sees the port (e.g., due to stale ListTunnelPortsAsync data),
        // it would UPDATE the protocol, which the Dev Tunnels service rejects.
        await this.managementClient
            .DeleteTunnelPortAsync(tunnel, portNumber, requestOptions, cancellationToken)
            .ConfigureAwait(false);

        // Clear any cached port data so the SDK does not send stale protocol information.
        tunnel.Ports = null;

        await this.managementClient
            .CreateTunnelPortAsync(
                tunnel,
                new TunnelPort { PortNumber = portNumber, Protocol = protocol },
                requestOptions,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string?> ApplyAccessModeAsync(
        string tunnelId,
        DevTunnelAccessMode accessMode,
        CancellationToken cancellationToken = default)
    {
        var tunnel = this.RequireCurrentTunnel(tunnelId);

        // Update only the tunnel's access control. Ports are managed individually via
        // SetSingleForwardedPortAsync; including them on a tunnel update is rejected by the service
        // ("Batch update of ports is not supported"), so clear them (and endpoints) from the payload.
        tunnel.Ports = null;
        tunnel.Endpoints = null;
        tunnel.AccessControl = new TunnelAccessControl
        {
            Entries = accessMode == DevTunnelAccessMode.Anonymous
                ?
                [
                    new TunnelAccessControlEntry
                    {
                        Type = TunnelAccessControlEntryType.Anonymous,
                        Subjects = [],
                        Scopes = [TunnelAccessScopes.Connect],
                    },
                ]
                : [],
        };

        this.currentTunnel = await this.managementClient
            .UpdateTunnelAsync(tunnel, CreateHostRequestOptions(), cancellationToken)
            .ConfigureAwait(false);

        if (accessMode == DevTunnelAccessMode.Anonymous)
        {
            return null;
        }

        // Re-fetch the tunnel with a connect-scope token so the host can expose it to operators
        // for cross-account distribution. Connect tokens are short-lived — re-fetched on each start.
        var withConnectToken = await this.managementClient
            .GetTunnelAsync(
                this.currentTunnel,
                new TunnelRequestOptions { TokenScopes = [TunnelAccessScopes.Connect] },
                cancellationToken)
            .ConfigureAwait(false);

        string? connectToken = null;
        withConnectToken?.AccessTokens?.TryGetValue(TunnelAccessScopes.Connect, out connectToken);
        return connectToken;
    }

    public async Task<string> GetAccessPointUrlAsync(
        string tunnelId,
        int localPort,
        CancellationToken cancellationToken = default)
    {
        var tunnel = this.RequireCurrentTunnel(tunnelId);
        var refreshed = await this.managementClient
            .GetTunnelAsync(tunnel, CreateGetTunnelOptions(), cancellationToken)
            .ConfigureAwait(false);
        if (refreshed is not null)
        {
            this.currentTunnel = refreshed;
            tunnel = refreshed;
        }

        var endpoint = tunnel.Endpoints?.FirstOrDefault();
        if (endpoint?.PortUriFormat is { Length: > 0 } portUriFormat)
        {
            return portUriFormat.Replace(TunnelEndpoint.PortToken, localPort.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        var forwardedUri = tunnel.Ports?
            .FirstOrDefault(port => port.PortNumber == localPort)?
            .PortForwardingUris?
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedUri))
        {
            return forwardedUri;
        }

        throw new InvalidOperationException($"Dev tunnel '{tunnelId}' did not expose a public access point for port {localPort}.");
    }

    public async Task<DevTunnelLookupResult> LookupByNameAsync(string tunnelName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tunnelName);

        var markerTunnels = await this.ListWorkspacesTunnelsAsync(CreateConnectRequestOptions(tunnelName), cancellationToken)
            .ConfigureAwait(false);
        var tunnel = markerTunnels.FirstOrDefault(candidate => HasLabel(candidate, tunnelName))
            ?? throw new InvalidOperationException($"Dev tunnel '{tunnelName}' was not found.");

        return ToLookupResult(tunnel);
    }

    public async Task<DevTunnelLookupResult> DiscoverSingleAsync(CancellationToken cancellationToken = default)
    {
        var markerTunnels = await this.ListWorkspacesTunnelsAsync(CreateConnectRequestOptions(), cancellationToken)
            .ConfigureAwait(false);
        return markerTunnels.Count switch
        {
            0 => throw new InvalidOperationException(
                "No Workspaces dev tunnel was found to connect to automatically; host one, or set a specific dev tunnel name."),
            1 => ToLookupResult(markerTunnels[0]),
            _ => throw new InvalidOperationException(
                "Multiple Workspaces dev tunnels were found; set a specific dev tunnel name instead of \"auto\"."),
        };
    }

    /// <summary>
    /// Lists the caller's tunnels and keeps only Workspaces-owned ones (carrying the marker label),
    /// filtering client-side so the result is correct regardless of server-side label-filter support.
    /// </summary>
    private async Task<IReadOnlyList<Tunnel>> ListWorkspacesTunnelsAsync(
        TunnelRequestOptions requestOptions,
        CancellationToken cancellationToken)
    {
        var ownedTunnels = await this.managementClient
            .ListTunnelsAsync(null, null, requestOptions, true, cancellationToken)
            .ConfigureAwait(false);
        return (ownedTunnels ?? [])
            .Where(candidate => HasLabel(candidate, DevTunnelNaming.WorkspacesMarkerLabel))
            .ToList();
    }

    private static DevTunnelLookupResult ToLookupResult(Tunnel tunnel)
    {
        var forwardedPorts = (tunnel.Ports ?? [])
            .Select(port => (int)port.PortNumber)
            .ToArray();
        string? connectToken = null;
        tunnel.AccessTokens?.TryGetValue(TunnelAccessScopes.Connect, out connectToken);
        return new DevTunnelLookupResult(
            tunnel.TunnelId ?? string.Empty,
            tunnel.ClusterId ?? string.Empty,
            forwardedPorts,
            connectToken);
    }

    private static string[] BuildLabels(string? nameLabel)
        => string.IsNullOrWhiteSpace(nameLabel)
            ? [DevTunnelNaming.WorkspacesMarkerLabel]
            : [DevTunnelNaming.WorkspacesMarkerLabel, nameLabel];

    private static bool HasLabel(Tunnel tunnel, string? label)
        => !string.IsNullOrWhiteSpace(label)
            && tunnel.Labels is { } labels
            && Array.Exists(labels, candidate => string.Equals(candidate, label, StringComparison.Ordinal));

    private Tunnel RequireCurrentTunnel(string tunnelId)
    {
        if (this.currentTunnel is null || !string.Equals(this.currentTunnel.TunnelId, tunnelId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Dev tunnel '{tunnelId}' has not been ensured by this client; call {nameof(EnsureTunnelAsync)} first.");
        }

        return this.currentTunnel;
    }

    private static TunnelRequestOptions CreateHostRequestOptions(string? nameLabel = null)
        => new()
        {
            IncludePorts = true,
            IncludeAccessControl = true,
            TokenScopes = [TunnelAccessScopes.Manage, TunnelAccessScopes.Host],
            Labels = BuildLabels(nameLabel),
            RequireAllLabels = true,
        };

    private static TunnelRequestOptions CreateConnectRequestOptions(string? nameLabel = null)
        => new()
        {
            IncludePorts = true,
            TokenScopes = [TunnelAccessScopes.Connect],
            Labels = BuildLabels(nameLabel),
            RequireAllLabels = true,
        };

    private static TunnelRequestOptions CreateGetTunnelOptions()
        => new()
        {
            IncludePorts = true,
            IncludeAccessControl = true,
            TokenScopes = [TunnelAccessScopes.Manage, TunnelAccessScopes.Host],
        };
}
