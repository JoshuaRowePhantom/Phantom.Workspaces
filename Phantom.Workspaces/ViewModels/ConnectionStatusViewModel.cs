using System;
using System.Collections.ObjectModel;
using System.Linq;
using Phantom.Workspaces.Services.DevTunnel;
using Phantom.Workspaces.Transport.ReverseHttp;

namespace Phantom.Workspaces.ViewModels;

/// <summary>An instance currently connected to this server for reverse execution ("who is connected to us").</summary>
public sealed class InboundConnectionViewModel
{
    public InboundConnectionViewModel(string clientInstanceId, DateTimeOffset connectedAt, int inFlightCount)
    {
        this.ClientInstanceId = clientInstanceId;
        this.ConnectedAt = connectedAt;
        this.InFlightCount = inFlightCount;
    }

    /// <summary>The remote client instance id (a user-computer-profile entity id).</summary>
    public string ClientInstanceId { get; }

    /// <summary>When the instance connected.</summary>
    public DateTimeOffset ConnectedAt { get; }

    /// <summary>How many reverse executions are currently in flight for this instance.</summary>
    public int InFlightCount { get; }
}

/// <summary>
/// Surfaces network connectivity for the connection-status window (opened from the top-right network
/// icon). The inbound list ("who is connected to us") is projected live from the
/// <see cref="ReverseConnectionStatusRegistry"/>; the outbound list ("where we are connected to") is
/// fed by the connecting-side worker/clients as they are wired in. See
/// <c>docs/design/reverse-tunnel-trust-execution.md</c>.
/// </summary>
public sealed class ConnectionStatusViewModel : ViewModelBase, IDisposable
{
    private readonly ReverseConnectionStatusRegistry registry;
    private readonly Action<Action> dispatch;
    private string? accessPoint;
    private string? localAccessPoint;
    private string? tunnelName;
    private DevTunnelHostState? devTunnelState;
    private string? devTunnelError;

    /// <param name="registry">The transport-layer connection-status registry providing the inbound connections.</param>
    /// <param name="dispatch">
    /// Marshals a refresh onto the UI thread. Defaults to running synchronously (tests); the GUI
    /// passes a dispatcher post.
    /// </param>
    public ConnectionStatusViewModel(ReverseConnectionStatusRegistry registry, Action<Action>? dispatch = null)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.dispatch = dispatch ?? (action => action());
        this.registry.ConnectionsChanged += this.OnConnectionsChanged;
        this.RefreshInbound();
    }

    /// <summary>
    /// The dev tunnel access point (the public URL where this instance is reachable for remote
    /// access), shown so it can be selected and copied. Null when the tunnel is not hosting.
    /// </summary>
    public string? AccessPoint
    {
        get => this.accessPoint;
        private set
        {
            if (this.SetProperty(ref this.accessPoint, value))
            {
                this.RaisePropertyChanged(nameof(this.HasAccessPoint));
            }
        }
    }

    /// <summary>Whether a dev tunnel access point is currently available to display.</summary>
    public bool HasAccessPoint => !string.IsNullOrWhiteSpace(this.accessPoint);

    /// <summary>Sets the dev tunnel (public) access point URL to display (or null to hide it).</summary>
    public void SetAccessPoint(string? accessPoint) => this.AccessPoint = accessPoint;

    /// <summary>
    /// The local access point (the URL the web server binds to on this machine), shown alongside the
    /// dev tunnel access point so the locally-reachable address is always visible.
    /// </summary>
    public string? LocalAccessPoint
    {
        get => this.localAccessPoint;
        private set
        {
            if (this.SetProperty(ref this.localAccessPoint, value))
            {
                this.RaisePropertyChanged(nameof(this.HasLocalAccessPoint));
            }
        }
    }

    /// <summary>Whether a local access point is currently available to display.</summary>
    public bool HasLocalAccessPoint => !string.IsNullOrWhiteSpace(this.localAccessPoint);

    /// <summary>Sets the local web-server access point URL to display (or null to hide it).</summary>
    public void SetLocalAccessPoint(string? localAccessPoint) => this.LocalAccessPoint = localAccessPoint;

    /// <summary>The configured dev tunnel name, shown when a dev tunnel is in use.</summary>
    public string? TunnelName
    {
        get => this.tunnelName;
        private set
        {
            if (this.SetProperty(ref this.tunnelName, value))
            {
                this.RaisePropertyChanged(nameof(this.HasDevTunnel));
            }
        }
    }

    /// <summary>Sets the dev tunnel name to display (or null when no dev tunnel is configured).</summary>
    public void SetTunnelName(string? tunnelName) => this.TunnelName = tunnelName;

    /// <summary>Whether a dev tunnel is in use (a name is configured), so its status should be shown.</summary>
    public bool HasDevTunnel => !string.IsNullOrWhiteSpace(this.tunnelName);

    /// <summary>A human-readable description of the dev tunnel host status.</summary>
    public string DevTunnelStatusText => this.devTunnelState switch
    {
        null => "Not started",
        DevTunnelHostState.Stopped => "Stopped",
        DevTunnelHostState.Starting => "Starting…",
        DevTunnelHostState.Hosting => "Hosting",
        DevTunnelHostState.Reconnecting => "Reconnecting…",
        DevTunnelHostState.Error => "Error",
        _ => this.devTunnelState.ToString() ?? "Unknown",
    };

    /// <summary>
    /// Whether the dev tunnel is in a state that should be flagged to the user (an error, or actively
    /// reconnecting). Drives the warning glyph in the network display.
    /// </summary>
    public bool HasProblem =>
        this.devTunnelState is DevTunnelHostState.Error or DevTunnelHostState.Reconnecting;

    /// <summary>Detail about the current problem (the last error), when <see cref="HasProblem"/>.</summary>
    public string? ProblemText => this.HasProblem
        ? this.devTunnelError ?? this.DevTunnelStatusText
        : null;

    /// <summary>Updates the displayed dev tunnel host status (state, public access point, last error).</summary>
    public void SetDevTunnelStatus(DevTunnelHostState state, string? accessPointUrl, string? lastError)
    {
        this.devTunnelState = state;
        this.devTunnelError = lastError;
        if (!string.IsNullOrWhiteSpace(accessPointUrl))
        {
            this.AccessPoint = accessPointUrl;
        }

        this.RaisePropertyChanged(nameof(this.DevTunnelStatusText));
        this.RaisePropertyChanged(nameof(this.HasProblem));
        this.RaisePropertyChanged(nameof(this.ProblemText));
    }

    /// <summary>Instances currently connected to us (inbound reverse-execution connections).</summary>
    public ObservableCollection<InboundConnectionViewModel> Inbound { get; } = new();

    /// <summary>Connections this instance has made outward (populated as the worker/clients are wired in).</summary>
    public ObservableCollection<OutboundConnectionViewModel> Outbound { get; } = new();

    /// <summary>Whether any instance is currently connected to us.</summary>
    public bool HasInboundConnections => this.Inbound.Count > 0;

    private void OnConnectionsChanged(object? sender, EventArgs e) => this.dispatch(this.RefreshInbound);

    private void RefreshInbound()
    {
        this.Inbound.Clear();
        foreach (var status in this.registry.GetConnectedInstances().OrderBy(status => status.ConnectedAt))
        {
            this.Inbound.Add(new InboundConnectionViewModel(status.ClientInstanceId, status.ConnectedAt, status.InFlightCount));
        }

        this.RaisePropertyChanged(nameof(this.HasInboundConnections));
    }

    public void Dispose()
    {
        this.registry.ConnectionsChanged -= this.OnConnectionsChanged;
    }
}

/// <summary>A connection this instance has made to a connected-to instance ("where we are connected to").</summary>
public sealed class OutboundConnectionViewModel
{
    public OutboundConnectionViewModel(string endpoint, string state)
    {
        this.Endpoint = endpoint;
        this.State = state;
    }

    /// <summary>The endpoint / tunnel URL of the connected-to instance.</summary>
    public string Endpoint { get; }

    /// <summary>The connection state (e.g. connecting / connected / retrying / failed).</summary>
    public string State { get; }
}
