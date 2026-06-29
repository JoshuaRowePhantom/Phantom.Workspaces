using System;
using System.Collections.Generic;
using System.Linq;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Trust;

/// <summary>
/// An in-memory registry of <see cref="RemoteTrustedExecutor"/> instances keyed by client instance
/// id (a user-computer-profile entity id). Supports both explicit registration and automatic
/// synchronisation from a <see cref="ReverseExecutionRegistry"/>: when a connecting instance
/// announces an HTTP endpoint in its <c>register</c> frame, the registry creates a
/// <see cref="RemoteTrustedExecutor"/> for it; when the instance disconnects, the auto-synced
/// executor is removed. Manually-registered executors are not affected by auto-sync.
/// </summary>
public sealed class RemoteExecutionRegistry : IDisposable
{
    private readonly Dictionary<string, RemoteTrustedExecutor> executorsByInstanceId =
        new(StringComparer.Ordinal);

    /// <summary>Tracks which instance ids were added by auto-sync so only those are removed on disconnect.</summary>
    private readonly HashSet<string> syncedInstanceIds = new(StringComparer.Ordinal);

    private readonly object gate = new();
    private ReverseExecutionRegistry? reverseRegistry;

    /// <summary>Raised whenever the set of registered remote executors changes.</summary>
    public event EventHandler? ExecutorsChanged;

    /// <summary>
    /// Subscribes to <paramref name="reverseRegistry"/> and automatically registers or removes
    /// remote executors as connections with announced endpoints come and go. Call this once; calling
    /// it a second time replaces the previous subscription.
    /// </summary>
    public void SyncFrom(ReverseExecutionRegistry reverseRegistry)
    {
        ArgumentNullException.ThrowIfNull(reverseRegistry);

        if (this.reverseRegistry is not null)
        {
            this.reverseRegistry.ConnectionsChanged -= this.OnReverseConnectionsChanged;
        }

        this.reverseRegistry = reverseRegistry;
        this.reverseRegistry.ConnectionsChanged += this.OnReverseConnectionsChanged;
        this.ApplyReverseSnapshot(reverseRegistry);
    }

    /// <summary>
    /// Explicitly registers or replaces a <see cref="RemoteTrustedExecutor"/> for the given client
    /// instance and endpoint. Raises <see cref="ExecutorsChanged"/>.
    /// </summary>
    public void Register(string clientInstanceId, string endpoint, string? devTunnelAccessToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        lock (this.gate)
        {
            this.executorsByInstanceId[clientInstanceId] =
                new RemoteTrustedExecutor(clientInstanceId, endpoint, devTunnelAccessToken);
            // Explicitly-registered entries are not tracked as auto-synced.
            this.syncedInstanceIds.Remove(clientInstanceId);
        }

        this.ExecutorsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Explicitly removes the registered executor for <paramref name="clientInstanceId"/>. Does
    /// nothing if the instance is not registered. Raises <see cref="ExecutorsChanged"/> when removed.
    /// </summary>
    public void Unregister(string clientInstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientInstanceId);

        bool removed;
        lock (this.gate)
        {
            removed = this.executorsByInstanceId.Remove(clientInstanceId);
            this.syncedInstanceIds.Remove(clientInstanceId);
        }

        if (removed)
        {
            this.ExecutorsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Whether a <see cref="RemoteTrustedExecutor"/> is currently registered for the instance.</summary>
    public bool IsRegistered(string clientInstanceId)
    {
        lock (this.gate)
        {
            return this.executorsByInstanceId.ContainsKey(clientInstanceId);
        }
    }

    /// <summary>
    /// Returns the registered executor for <paramref name="clientInstanceId"/>, or
    /// <see langword="false"/> if none is registered.
    /// </summary>
    public bool TryGetExecutor(string clientInstanceId, out RemoteTrustedExecutor executor)
    {
        lock (this.gate)
        {
            return this.executorsByInstanceId.TryGetValue(clientInstanceId, out executor!);
        }
    }

    private void OnReverseConnectionsChanged(object? sender, EventArgs e)
    {
        if (this.reverseRegistry is { } registry)
        {
            this.ApplyReverseSnapshot(registry);
        }
    }

    private void ApplyReverseSnapshot(ReverseExecutionRegistry registry)
    {
        var instances = registry.GetConnectedInstances();
        var nowConnectedWithEndpoint = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instance in instances)
        {
            if (!string.IsNullOrWhiteSpace(instance.AnnouncedEndpoint))
            {
                nowConnectedWithEndpoint.Add(instance.ClientInstanceId);
            }
        }

        bool changed = false;
        lock (this.gate)
        {
            // Remove auto-synced executors for instances that are no longer connected with an endpoint.
            var toRemove = this.syncedInstanceIds
                .Where(id => !nowConnectedWithEndpoint.Contains(id))
                .ToList();
            foreach (var id in toRemove)
            {
                this.executorsByInstanceId.Remove(id);
                this.syncedInstanceIds.Remove(id);
                changed = true;
            }

            // Add auto-synced executors for newly-connected instances with announced endpoints.
            foreach (var instance in instances)
            {
                if (!string.IsNullOrWhiteSpace(instance.AnnouncedEndpoint)
                    && !this.executorsByInstanceId.ContainsKey(instance.ClientInstanceId))
                {
                    this.executorsByInstanceId[instance.ClientInstanceId] =
                        new RemoteTrustedExecutor(instance.ClientInstanceId, instance.AnnouncedEndpoint);
                    this.syncedInstanceIds.Add(instance.ClientInstanceId);
                    changed = true;
                }
            }
        }

        if (changed)
        {
            this.ExecutorsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.reverseRegistry is not null)
        {
            this.reverseRegistry.ConnectionsChanged -= this.OnReverseConnectionsChanged;
            this.reverseRegistry = null;
        }
    }
}
