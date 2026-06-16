using System;
using System.Collections.ObjectModel;
using System.Linq;
using Phantom.Workspaces.Llm.Trust;

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
/// <see cref="ReverseExecutionRegistry"/>; the outbound list ("where we are connected to") is fed by
/// the connecting-side worker/clients as they are wired in. See
/// <c>docs/design/reverse-tunnel-trust-execution.md</c>.
/// </summary>
public sealed class ConnectionStatusViewModel : ViewModelBase, IDisposable
{
    private readonly ReverseExecutionRegistry registry;
    private readonly Action<Action> dispatch;

    /// <param name="registry">The reverse-execution registry providing the inbound connections.</param>
    /// <param name="dispatch">
    /// Marshals a refresh onto the UI thread. Defaults to running synchronously (tests); the GUI
    /// passes a dispatcher post.
    /// </param>
    public ConnectionStatusViewModel(ReverseExecutionRegistry registry, Action<Action>? dispatch = null)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.dispatch = dispatch ?? (action => action());
        this.registry.ConnectionsChanged += this.OnConnectionsChanged;
        this.RefreshInbound();
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
