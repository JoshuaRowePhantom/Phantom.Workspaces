using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// A live duplex connection from a connected instance (the connecting instance "C") to this server
/// (the connected-to instance "S"), over which S streams reverse agent-execution requests and
/// receives streamed results. The transport (a WebSocket) implements this; the registry and the
/// reverse executor depend only on this abstraction. See
/// <c>docs/design/reverse-tunnel-trust-execution.md</c>.
/// </summary>
public interface IReverseConnection
{
    /// <summary>The client instance id (a user-computer-profile entity id) claimed by C.</summary>
    string ClientInstanceId { get; }

    /// <summary>
    /// The absolute base URL of C's own Phantom.Workspaces HTTP endpoint, as announced in the
    /// <c>register</c> frame. <see langword="null"/> when C did not announce an endpoint.
    /// </summary>
    string? AnnouncedEndpoint { get; }

    /// <summary>When the connection was established.</summary>
    DateTimeOffset ConnectedAt { get; }

    /// <summary>The number of reverse executions currently in flight on this connection.</summary>
    int InFlightCount { get; }

    /// <summary>
    /// Sends a reverse execution request to C and streams back the resulting
    /// <see cref="ChatResponseUpdate"/>s. Throws if the connection drops before the turn completes.
    /// </summary>
    IAsyncEnumerable<ChatResponseUpdate> ExecuteAsync(RemoteAgentRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a bidirectional byte stream on C and returns a duplex <see cref="System.IO.Stream"/>
    /// that relays data over the reverse channel. Throws if the connection drops.
    /// </summary>
    Task<System.IO.Stream> OpenStreamAsync(TrustedStreamRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a run-tool request to C and awaits its completion. Throws if the handler on C fails
    /// or the connection drops before the tool completes.
    /// </summary>
    Task RunToolAsync(TrustedToolRequest request, CancellationToken cancellationToken);
}

/// <summary>A point-in-time status of a connected instance, for the connection-status GUI.</summary>
public sealed record ConnectedInstanceStatus(
    string ClientInstanceId,
    DateTimeOffset ConnectedAt,
    int InFlightCount,
    string? AnnouncedEndpoint = null);

/// <summary>
/// An in-memory registry of the instances currently connected to this server for reverse execution,
/// keyed by client instance id. The connection's lifetime is the liveness signal: registering on
/// connect, unregistering on disconnect. Exposes a snapshot and a change event for the
/// connection-status GUI.
/// </summary>
public sealed class ReverseExecutionRegistry
{
    private readonly Dictionary<string, IReverseConnection> connectionsByInstanceId = new(StringComparer.Ordinal);
    private readonly object gate = new();

    /// <summary>Raised whenever the set of connected instances changes.</summary>
    public event EventHandler? ConnectionsChanged;

    /// <summary>
    /// Registers a connection for its client instance id, replacing any prior connection for the
    /// same id (a reconnect supersedes the old one).
    /// </summary>
    public void Register(IReverseConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(connection.ClientInstanceId))
        {
            throw new ArgumentException("A reverse connection must have a client instance id.", nameof(connection));
        }

        lock (this.gate)
        {
            this.connectionsByInstanceId[connection.ClientInstanceId] = connection;
        }

        this.ConnectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Unregisters the given connection if it is still the current connection for its instance id (a
    /// stale connection that was already superseded by a reconnect is ignored).
    /// </summary>
    public void Unregister(IReverseConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        bool removed;
        lock (this.gate)
        {
            removed = this.connectionsByInstanceId.TryGetValue(connection.ClientInstanceId, out var current)
                && ReferenceEquals(current, connection)
                && this.connectionsByInstanceId.Remove(connection.ClientInstanceId);
        }

        if (removed)
        {
            this.ConnectionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Returns the live connection for the given client instance id, if any.</summary>
    public bool TryGetConnection(string clientInstanceId, out IReverseConnection connection)
    {
        lock (this.gate)
        {
            return this.connectionsByInstanceId.TryGetValue(clientInstanceId, out connection!);
        }
    }

    /// <summary>Whether an instance with the given id is currently connected.</summary>
    public bool IsConnected(string clientInstanceId)
    {
        lock (this.gate)
        {
            return this.connectionsByInstanceId.ContainsKey(clientInstanceId);
        }
    }

    /// <summary>A snapshot of all connected instances and their status.</summary>
    public IReadOnlyList<ConnectedInstanceStatus> GetConnectedInstances()
    {
        lock (this.gate)
        {
            return this.connectionsByInstanceId.Values
                .Select(connection => new ConnectedInstanceStatus(
                    connection.ClientInstanceId,
                    connection.ConnectedAt,
                    connection.InFlightCount,
                    connection.AnnouncedEndpoint))
                .ToArray();
        }
    }
}
