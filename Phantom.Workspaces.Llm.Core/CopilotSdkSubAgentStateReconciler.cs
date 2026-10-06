using GitHub.Copilot.Rpc;
using GitHub.Copilot;

namespace Phantom.Workspaces.Llm;

#pragma warning disable GHCP001
/// <summary>
/// Serializes complete task snapshots. An invalidation arriving during a fetch forces a
/// second fetch, so a stale in-flight response never becomes the final published state.
/// </summary>
internal sealed class CopilotSdkSubAgentStateReconciler(
    Func<CancellationToken, Task<IReadOnlyList<TaskInfo>>> fetch,
    Action<IReadOnlyList<TaskInfo>> publish) : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private bool dirty;
    private bool disposed;
    private Task? pump;
    private readonly SemaphoreSlim eventGate = new(1, 1);
    private readonly HashSet<Guid> processedEventIds = [];
    private readonly Dictionary<Guid, SessionEvent> pendingEvents = [];
    private Guid? lastEventId;

    internal async Task<bool> ObserveEventAsync(
        SessionEvent live,
        Func<CancellationToken, Task<IReadOnlyList<SessionEvent>>> replay,
        Action<SessionEvent> apply)
    {
        await this.eventGate.WaitAsync(this.shutdown.Token).ConfigureAwait(false);
        try
        {
            if (live.Id != Guid.Empty && this.processedEventIds.Contains(live.Id)) return false;
            if (this.lastEventId is { } previous && live.ParentId is { } parent && parent != previous)
            {
                var events = await replay(this.shutdown.Token).ConfigureAwait(false);
                foreach (var recovered in OrderReplay(events))
                    this.AcceptOrQueue(recovered, apply);
            }
            this.AcceptOrQueue(live, apply);
            return live.Id != Guid.Empty && this.pendingEvents.ContainsKey(live.Id);
        }
        finally
        {
            this.eventGate.Release();
        }
    }

    private void AcceptOrQueue(SessionEvent value, Action<SessionEvent> apply)
    {
        if (value.Id != Guid.Empty && this.processedEventIds.Contains(value.Id)) return;
        if (this.lastEventId is { } previous && value.ParentId is { } parent && parent != previous)
        {
            if (value.Id != Guid.Empty) this.pendingEvents[value.Id] = value;
            return;
        }
        this.AcceptEvent(value, apply);
        while (this.lastEventId is { } accepted)
        {
            var next = this.pendingEvents.Values.FirstOrDefault(e => e.ParentId == accepted);
            if (next is null) break;
            this.pendingEvents.Remove(next.Id);
            this.AcceptEvent(next, apply);
        }
    }

    private static IEnumerable<SessionEvent> OrderReplay(IReadOnlyList<SessionEvent> events)
    {
        var pending = events.OrderBy(e => e.Timestamp).ToList();
        var emitted = new HashSet<Guid>();
        while (pending.Count > 0)
        {
            var next = pending.FindIndex(e => e.ParentId is not { } parent
                || emitted.Contains(parent)
                || pending.All(candidate => candidate.Id != parent));
            if (next < 0) next = 0;
            var selected = pending[next];
            pending.RemoveAt(next);
            if (selected.Id != Guid.Empty) emitted.Add(selected.Id);
            yield return selected;
        }
    }

    private void AcceptEvent(SessionEvent value, Action<SessionEvent> apply)
    {
        if (value.Id != Guid.Empty && !this.processedEventIds.Add(value.Id)) return;
        apply(value);
        if (value.Id != Guid.Empty) this.lastEventId = value.Id;
    }

    internal Task InvalidateAsync()
    {
        lock (this.gate)
        {
            if (this.disposed) return Task.CompletedTask;
            this.dirty = true;
            return this.pump ??= Task.Run(this.PumpAsync);
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                lock (this.gate) this.dirty = false;
                IReadOnlyList<TaskInfo> snapshot;
                try
                {
                    snapshot = await fetch(this.shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (this.shutdown.IsCancellationRequested)
                {
                    return;
                }
                await this.eventGate.WaitAsync(this.shutdown.Token).ConfigureAwait(false);
                try
                {
                    lock (this.gate)
                    {
                        if (this.disposed) return;
                        publish(snapshot);
                        if (this.pendingEvents.Count > 0)
                        {
                            this.lastEventId = this.pendingEvents.Values.OrderBy(e => e.Timestamp).Last().Id;
                            this.pendingEvents.Clear();
                        }
                        if (!this.dirty)
                        {
                            this.pump = null;
                            return;
                        }
                    }
                }
                finally { this.eventGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (this.shutdown.IsCancellationRequested) { }
        catch
        {
            lock (this.gate) this.pump = null;
            throw;
        }
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            this.disposed = true;
            this.shutdown.Cancel();
        }
    }
}
