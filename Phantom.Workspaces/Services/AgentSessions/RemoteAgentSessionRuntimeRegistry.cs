namespace Phantom.Workspaces.Services.AgentSessions;

internal interface IRemoteAgentSessionRuntimeRegistry
{
    ValueTask<RemoteAgentSessionLease?> TryGetAsync(string sessionId, long ownershipGeneration, CancellationToken ct = default);
    ValueTask<RemoteAgentSessionLease> GetOrStartAsync(
        PersistedAgentSessionRuntimeIntent intent,
        Func<CancellationToken, Task<RemoteAgentSessionLease>> startAsync,
        CancellationToken ct = default);
    ValueTask<bool> TryTerminateAsync(TerminateAgentSessionRuntimeRequest request, CancellationToken ct = default);
    ValueTask SetContinueInBackgroundAsync(UpdateAgentSessionRuntimeRetentionRequest request, CancellationToken ct = default);
}

internal interface IAgentSessionChildRegistry
{
    ValueTask<bool> ContainsAsync(
        string sessionId, long ownershipGeneration, string childAgentId, CancellationToken ct = default);
}

internal sealed class RemoteAgentSessionRuntimeRegistry :
    IRemoteAgentSessionRuntimeRegistry, IAgentSessionChildRegistry, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<RuntimeKey, RegistryEntry> entries = [];
    private bool disposed;

    internal RemoteAgentSessionRuntimeRegistry(TimeProvider timeProvider)
        => ArgumentNullException.ThrowIfNull(timeProvider);

    public async ValueTask<RemoteAgentSessionLease?> TryGetAsync(string sessionId, long ownershipGeneration, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RegistryEntry entry;
        lock (this.gate)
        {
            if (!this.entries.TryGetValue(new(sessionId, ownershipGeneration), out entry!))
                return null;
        }
        var lease = entry.Lease ?? await entry.StartTask.WaitAsync(ct).ConfigureAwait(false);
        lock (this.gate)
        {
            if (this.disposed
                || !this.entries.TryGetValue(new(sessionId, ownershipGeneration), out var current)
                || !ReferenceEquals(current, entry))
                return null;
            entry.Lease ??= lease;
            return lease.IsFenced ? null : lease;
        }
    }

    public async ValueTask<RemoteAgentSessionLease> GetOrStartAsync(
        PersistedAgentSessionRuntimeIntent intent,
        Func<CancellationToken, Task<RemoteAgentSessionLease>> startAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(startAsync);
        var key = new RuntimeKey(intent.AgentSessionId, intent.OwnershipGeneration);
        while (true)
        {
            RegistryEntry entry;
            lock (this.gate)
            {
                ObjectDisposedException.ThrowIf(this.disposed, this);
                if (!this.entries.TryGetValue(key, out entry!))
                {
                    entry = new RegistryEntry(startAsync(ct));
                    this.entries.Add(key, entry);
                }
            }

            RemoteAgentSessionLease lease;
            try
            {
                lease = await entry.StartTask.WaitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                if (entry.StartTask.IsFaulted || entry.StartTask.IsCanceled)
                    this.RemoveEntry(key, entry);
                throw;
            }

            bool rejectStartedLease;
            lock (this.gate)
            {
                rejectStartedLease = this.disposed;
                if (rejectStartedLease)
                {
                    entry.MarkRemoved();
                }
                else
                {
                    entry.Lease ??= lease;
                }
            }
            if (rejectStartedLease)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(RemoteAgentSessionRuntimeRegistry));
            }

            lease.Terminated -= this.OnTerminated;
            lease.Terminated += this.OnTerminated;
            if (lease.HasTerminated)
                this.OnTerminated(lease, EventArgs.Empty);
            if (!lease.IsFenced)
                return lease;

            await entry.Removal.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<bool> TryTerminateAsync(TerminateAgentSessionRuntimeRequest request, CancellationToken ct = default)
    {
        var lease = await this.TryGetAsync(request.SessionId, request.OwnershipGeneration, ct).ConfigureAwait(false);
        if (lease is null || lease.Epoch != request.Epoch) return false;
        return await lease.TryTerminateAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask SetContinueInBackgroundAsync(UpdateAgentSessionRuntimeRetentionRequest request, CancellationToken ct = default)
    {
        var lease = await this.TryGetAsync(request.SessionId, request.OwnershipGeneration, ct).ConfigureAwait(false);
        if (lease is null || lease.Epoch != request.Epoch)
            throw new InvalidOperationException("The remote agent session runtime changed.");
        await lease.SetContinueInBackgroundAsync(request.ContinueInBackground, ct).ConfigureAwait(false);
    }

    public async ValueTask<bool> ContainsAsync(
        string sessionId, long ownershipGeneration, string childAgentId, CancellationToken ct = default)
    {
        var lease = await this.TryGetAsync(sessionId, ownershipGeneration, ct).ConfigureAwait(false);
        return lease is not null && lease.Chat.SubAgents.Any(
            child => string.Equals(child.AgentId, childAgentId, StringComparison.Ordinal));
    }

    public async ValueTask DisposeAsync()
    {
        RegistryEntry[] entries;
        lock (this.gate)
        {
            if (this.disposed) return;
            this.disposed = true;
            entries = this.entries.Values.ToArray();
            this.entries.Clear();
            foreach (var entry in entries) entry.MarkRemoved();
        }
        foreach (var entry in entries)
        {
            try { await (await entry.StartTask.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
            catch { }
        }
    }

    private void OnTerminated(object? sender, EventArgs args)
    {
        if (sender is not RemoteAgentSessionLease lease) return;
        lock (this.gate)
        {
            var key = new RuntimeKey(lease.SessionId, lease.OwnershipGeneration);
            if (this.entries.TryGetValue(key, out var entry) && ReferenceEquals(entry.Lease, lease))
            {
                this.entries.Remove(key);
                entry.MarkRemoved();
            }
        }
    }

    private void RemoveEntry(RuntimeKey key, RegistryEntry entry)
    {
        lock (this.gate)
        {
            if (this.entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            {
                this.entries.Remove(key);
                entry.MarkRemoved();
            }
        }
    }

    private readonly record struct RuntimeKey(string SessionId, long Generation);

    private sealed class RegistryEntry(Task<RemoteAgentSessionLease> startTask)
    {
        private readonly TaskCompletionSource removed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<RemoteAgentSessionLease> StartTask { get; } = startTask;
        internal RemoteAgentSessionLease? Lease { get; set; }
        internal Task Removal => this.removed.Task;

        internal void MarkRemoved() => this.removed.TrySetResult();
    }
}
