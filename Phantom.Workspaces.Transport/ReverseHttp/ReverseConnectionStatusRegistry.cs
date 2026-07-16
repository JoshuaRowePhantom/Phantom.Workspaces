using System.Collections.Concurrent;

namespace Phantom.Workspaces.Transport.ReverseHttp;

/// <summary>
/// Tracks the set of currently-registered reverse-HTTP executor clients and raises a change
/// event. Transport-layer replacement for <c>ReverseExecutionRegistry.GetConnectedInstances()</c> /
/// <c>ConnectionsChanged</c>, fed by <see cref="ReverseHttpServerTransportFactory"/>.
/// </summary>
public sealed class ReverseConnectionStatusRegistry
{
    private readonly ConcurrentDictionary<string, ReverseConnectionStatus> connections = new(StringComparer.Ordinal);

    public event EventHandler? ConnectionsChanged;

    public void OnRegistered(string clientInstanceId, DateTimeOffset connectedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientInstanceId);

        this.connections[clientInstanceId] = new ReverseConnectionStatus
        {
            ClientInstanceId = clientInstanceId,
            ConnectedAt = connectedAt,
            InFlightCount = 0,
        };

        this.RaiseConnectionsChanged();
    }

    public void OnUnregistered(string clientInstanceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientInstanceId);

        if (this.connections.TryRemove(clientInstanceId, out _))
        {
            this.RaiseConnectionsChanged();
        }
    }

    public void OnInFlightChanged(string clientInstanceId, int inFlightCount)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientInstanceId);

        if (this.connections.TryGetValue(clientInstanceId, out var existing))
        {
            this.connections[clientInstanceId] = existing with { InFlightCount = inFlightCount };
            this.RaiseConnectionsChanged();
        }
    }

    public IReadOnlyList<ReverseConnectionStatus> GetConnectedInstances()
        => this.connections.Values
            .OrderBy(static status => status.ConnectedAt)
            .ThenBy(static status => status.ClientInstanceId, StringComparer.Ordinal)
            .ToList();

    private void RaiseConnectionsChanged() => this.ConnectionsChanged?.Invoke(this, EventArgs.Empty);
}
