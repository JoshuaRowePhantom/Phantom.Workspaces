using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Transport;
using ProtocolReplayCursor = Phantom.Workspaces.Llm.Remote.ReplayCursor;
using System.Collections.Specialized;
using System.Text.Json;

namespace Phantom.Workspaces.Services.AgentSessions;

internal sealed class RemoteAgentSessionLease : IAsyncDisposable
{
    private static readonly TimeSpan ReconnectGrace = TimeSpan.FromSeconds(5);
    private readonly object gate = new();
    private readonly SemaphoreSlim transitionGate = new(1, 1);
    private readonly Dictionary<string, AttachmentState> attachments = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, CommandCacheEntry> commands = [];
    private readonly Dictionary<AgentChatRunningItem, string> runningItemIds =
        new(ReferenceEqualityComparer.Instance);
    private readonly TimeProvider timeProvider;
    private readonly Func<bool, CancellationToken, ValueTask> persistRetentionAsync;
    private readonly Func<CancellationToken, ValueTask> persistTerminalAsync;
    private readonly Func<AgentSessionSnapshot> snapshotFactory;
    private readonly AgentSessionOwnershipLease? ownershipLease;
    private readonly IAsyncDisposable? runtimeLifetime;
    private HashSet<string> queueIds;
    private Task? termination;
    private string terminalReason = "runtime-stopped";
    private bool fenced;
    private bool hasTerminated;
    private bool continueInBackground;

    internal RemoteAgentSessionLease(
        string sessionId,
        long ownershipGeneration,
        RuntimeEpoch epoch,
        IAgentChat chat,
        bool continueInBackground,
        Func<AgentSessionSnapshot> snapshotFactory,
        Func<bool, CancellationToken, ValueTask>? persistRetentionAsync = null,
        Func<CancellationToken, ValueTask>? persistTerminalAsync = null,
        TimeProvider? timeProvider = null,
        AgentSessionOwnershipLease? ownershipLease = null,
        IAsyncDisposable? runtimeLifetime = null)
    {
        this.SessionId = !string.IsNullOrWhiteSpace(sessionId) ? sessionId : throw new ArgumentException("Session id is required.", nameof(sessionId));
        this.OwnershipGeneration = ownershipGeneration >= 0 ? ownershipGeneration : throw new ArgumentOutOfRangeException(nameof(ownershipGeneration));
        this.Epoch = epoch;
        this.Chat = chat ?? throw new ArgumentNullException(nameof(chat));
        this.continueInBackground = continueInBackground;
        this.snapshotFactory = snapshotFactory ?? throw new ArgumentNullException(nameof(snapshotFactory));
        this.persistRetentionAsync = persistRetentionAsync ?? ((_, _) => ValueTask.CompletedTask);
        this.persistTerminalAsync = persistTerminalAsync ?? (_ => ValueTask.CompletedTask);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.ownershipLease = ownershipLease;
        this.runtimeLifetime = runtimeLifetime;
        this.Replay = new AgentSessionReplayBuffer(epoch, this.timeProvider);
        var initialQueues = this.Chat.InputQueues.Snapshot.Queues;
        this.queueIds = initialQueues.IsDefault
            ? []
            : initialQueues.Select(queue => queue.QueueId).ToHashSet(StringComparer.Ordinal);
        this.Chat.InformationChanged += this.OnInformationChanged;
        this.Chat.UsageChanged += this.OnUsageChanged;
        this.Chat.ToolsChanged += this.OnToolsChanged;
        this.Chat.TurnCompleted += this.OnTurnCompleted;
        this.Chat.InputQueues.Changed += this.OnQueuesChanged;
        ((INotifyCollectionChanged)this.Chat.RunningItems).CollectionChanged += this.OnRunningItemsChanged;
        foreach (var item in this.Chat.RunningItems)
        {
            this.runningItemIds[item] = Guid.NewGuid().ToString("N");
            item.Items.CollectionChanged += this.OnRunningItemUpdated;
        }
        ((INotifyCollectionChanged)this.Chat.SubAgents).CollectionChanged += this.OnSubagentsChanged;
        ((INotifyCollectionChanged)this.Chat.Modals).CollectionChanged += this.OnModalsChanged;
    }

    internal event EventHandler? Terminated;
    internal string SessionId { get; }
    internal long OwnershipGeneration { get; }
    internal RuntimeEpoch Epoch { get; }
    internal IAgentChat Chat { get; }
    internal AgentSessionReplayBuffer Replay { get; }
    internal bool IsFenced { get { lock (this.gate) return this.fenced; } }
    internal bool HasTerminated { get { lock (this.gate) return this.hasTerminated; } }
    internal int ViewerCount { get { lock (this.gate) return this.attachments.Count; } }
    internal bool ContinueInBackground { get { lock (this.gate) return this.continueInBackground; } }

    internal RemoteAgentAttachmentLease Attach(AttachRemoteAgentSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (this.gate)
            return this.AttachUnderLock(request);
    }

    private RemoteAgentAttachmentLease AttachUnderLock(AttachRemoteAgentSessionRequest request)
    {
        if (this.fenced)
            throw new InvalidOperationException("The runtime is stopping.");

        if (this.attachments.TryGetValue(request.AttachmentToken, out var existing))
        {
            if (!existing.Disconnected)
                throw new InvalidOperationException("The attachment token is already connected.");
            existing.GraceTimer?.Dispose();
            existing.Channel = request.Channel;
            existing.Disconnected = false;
            existing.GraceTimer = null;
            existing.Generation++;
            return new RemoteAgentAttachmentLease(this, request.AttachmentToken, existing.Generation, request.Cursor);
        }

        var state = new AttachmentState(request.Channel);
        this.attachments.Add(request.AttachmentToken, state);
        this.PublishRetentionChangedUnderLock(request.Channel);
        return new RemoteAgentAttachmentLease(this, request.AttachmentToken, state.Generation, request.Cursor);
    }

    internal AgentSessionSnapshot CaptureSnapshot()
    {
        lock (this.gate)
        {
            var snapshot = this.snapshotFactory();
            return snapshot with
            {
                ContinueInBackground = this.continueInBackground,
                ViewerCount = this.attachments.Count,
            };
        }
    }

    internal async ValueTask<InitialAttachmentState> AttachAndCaptureInitialStateAsync(
        AttachRemoteAgentSessionRequest request,
        CancellationToken ct = default)
    {
        await this.transitionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (this.gate)
            {
                var attachment = this.AttachUnderLock(request);
                if (request.Cursor is { } cursor)
                {
                    var replay = this.Replay.ReadAfter(cursor);
                    if (replay.IsCovered)
                        return new InitialAttachmentState(attachment, replay.Frames, null);
                }

                var snapshot = this.snapshotFactory() with
                {
                    ContinueInBackground = this.continueInBackground,
                    ViewerCount = this.attachments.Count,
                };
                var frame = this.Replay.Append(
                    Guid.NewGuid(),
                    new SessionSnapshotEvent { Snapshot = snapshot });
                return new InitialAttachmentState(attachment, [frame], snapshot);
            }
        }
        finally
        {
            this.transitionGate.Release();
        }
    }

    internal async ValueTask SetContinueInBackgroundAsync(bool value, CancellationToken ct = default)
    {
        var stop = false;
        await this.transitionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (this.gate)
            {
                if (this.fenced) throw new InvalidOperationException("The runtime is stopping.");
                if (this.continueInBackground == value) return;
            }

            await this.persistRetentionAsync(value, ct).ConfigureAwait(false);
            lock (this.gate)
            {
                if (this.fenced) throw new InvalidOperationException("The runtime is stopping.");
                this.continueInBackground = value;
                stop = !value && this.attachments.Count == 0;
            }
            await this.PublishRetentionChangedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            this.transitionGate.Release();
        }
        if (stop) await this.TryTerminateAsync(ct).ConfigureAwait(false);
    }

    internal async ValueTask PublishAsync(
        AgentSessionServerEvent value, Guid correlationId = default, CancellationToken ct = default)
    {
        AgentSessionServerFrame frame;
        IMessageChannel[] channels;
        lock (this.gate)
        {
            if (this.fenced) return;
            frame = this.Replay.Append(correlationId == default ? Guid.NewGuid() : correlationId, value);
            channels = this.attachments.Values
                .Where(state => !state.Disconnected)
                .Select(state => state.Channel)
                .ToArray();
        }
        var serialized = AgentSessionProtocolCodec.SerializeFrame(frame);
        foreach (var channel in channels)
            await channel.Writer.WriteAsync(serialized, ct).ConfigureAwait(false);
    }

    internal async ValueTask<AgentSessionServerEvent> ExecuteCommandOnceAsync(
        AgentSessionCommand command,
        Func<CancellationToken, Task<AgentSessionServerEvent>> executeAsync,
        CancellationToken ct)
    {
        var payload = AgentSessionProtocolCodec.SerializeCommand(command).GetRawText();
        Task<AgentSessionServerEvent> task;
        lock (this.gate)
        {
            if (this.fenced) throw new InvalidOperationException("The runtime is stopping.");
            if (this.commands.TryGetValue(command.CommandId, out var cached))
            {
                if (!string.Equals(cached.Payload, payload, StringComparison.Ordinal))
                    return new OperationErrorEvent
                    {
                        Error = new RemoteAgentOperationError
                        {
                            Code = "conflict",
                            Operation = command.Type,
                            IsRetryable = false,
                            Message = "The command id was already used for a different command.",
                            CorrelationId = command.CorrelationId,
                        },
                    };
                task = cached.Result;
            }
            else
            {
                task = executeAsync(ct);
                this.commands.Add(command.CommandId, new CommandCacheEntry(payload, task));
            }
        }
        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    internal async ValueTask<bool> TryTerminateAsync(CancellationToken ct = default)
        => await this.TerminateAsync("runtime-stopped", ct).ConfigureAwait(false);

    internal async ValueTask ReportFatalFailureAsync(CancellationToken ct = default)
        => await this.TerminateAsync("runtime-failed", ct).ConfigureAwait(false);

    private async ValueTask<bool> TerminateAsync(string reason, CancellationToken ct)
    {
        await this.transitionGate.WaitAsync(ct).ConfigureAwait(false);
        Task task;
        try
        {
            lock (this.gate)
                task = this.BeginTerminationUnderLock(reason);
        }
        finally
        {
            this.transitionGate.Release();
        }
        await task.WaitAsync(ct).ConfigureAwait(false);
        return true;
    }

    internal ValueTask MarkTransportLostAsync(string token)
    {
        lock (this.gate)
        {
            if (this.fenced || !this.attachments.TryGetValue(token, out var state) || state.Disconnected)
                return ValueTask.CompletedTask;
            state.Disconnected = true;
            state.GraceTimer = this.timeProvider.CreateTimer(
                _ => _ = this.ExpireDisconnectedAsync(token), null, ReconnectGrace, Timeout.InfiniteTimeSpan);
        }
        return ValueTask.CompletedTask;
    }

    internal async ValueTask ReleaseAsync(string token, long generation)
    {
        Task? terminationTask = null;
        var changed = false;
        await this.transitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (this.gate)
            {
                if (!this.attachments.TryGetValue(token, out var state) || state.Generation != generation)
                    return;
                this.attachments.Remove(token);
                state.GraceTimer?.Dispose();
                state.Released.TrySetResult();
                changed = true;
                if (!this.fenced && this.attachments.Count == 0 && !this.continueInBackground)
                    terminationTask = this.BeginTerminationUnderLock("runtime-stopped");
            }
        }
        finally
        {
            this.transitionGate.Release();
        }
        if (terminationTask is not null)
            await terminationTask.ConfigureAwait(false);
        else if (changed)
            await this.PublishRetentionChangedAsync().ConfigureAwait(false);
    }

    internal IMessageChannel? GetConnectedChannel(string token, long generation)
    {
        lock (this.gate)
            return this.attachments.TryGetValue(token, out var state)
                && state.Generation == generation
                && !state.Disconnected
                    ? state.Channel
                    : null;
    }

    internal Task GetReleaseTask(string token, long generation)
    {
        lock (this.gate)
            return this.attachments.TryGetValue(token, out var state)
                && state.Generation == generation
                    ? state.Released.Task
                    : Task.CompletedTask;
    }

    internal async ValueTask PublishToAttachmentAsync(
        string token, AgentSessionServerEvent value, CancellationToken ct)
    {
        AgentSessionServerFrame frame;
        IMessageChannel channel;
        lock (this.gate)
        {
            if (!this.attachments.TryGetValue(token, out var state) || state.Disconnected)
                throw new ObjectDisposedException(nameof(RemoteAgentAttachmentLease));
            frame = this.Replay.Append(Guid.NewGuid(), value);
            channel = state.Channel;
        }
        await channel.Writer.WriteAsync(AgentSessionProtocolCodec.SerializeFrame(frame), ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await this.TryTerminateAsync().ConfigureAwait(false);

    private Task BeginTerminationUnderLock(string reason)
    {
        if (this.termination is not null)
            return this.termination;
        this.fenced = true;
        this.terminalReason = reason;
        return this.termination = this.TerminateCoreAsync();
    }

    private async Task ExpireDisconnectedAsync(string token)
    {
        long generation;
        lock (this.gate)
        {
            if (!this.attachments.TryGetValue(token, out var state) || !state.Disconnected) return;
            generation = state.Generation;
        }
        await this.ReleaseAsync(token, generation).ConfigureAwait(false);
    }

    private void PublishRetentionChangedUnderLock(IMessageChannel excludedChannel)
    {
        var frame = this.Replay.Append(Guid.NewGuid(), new SessionRetentionChangedEvent
        {
            ContinueInBackground = this.continueInBackground,
            ViewerCount = this.attachments.Count,
        });
        var serialized = AgentSessionProtocolCodec.SerializeFrame(frame);
        foreach (var channel in this.attachments.Values
                     .Where(value => !value.Disconnected && !ReferenceEquals(value.Channel, excludedChannel))
                     .Select(value => value.Channel))
            channel.Writer.TryWrite(serialized);
    }

    private async Task PublishRetentionChangedAsync(CancellationToken ct = default)
    {
        bool background;
        int viewers;
        lock (this.gate)
        {
            if (this.fenced) return;
            background = this.continueInBackground;
            viewers = this.attachments.Count;
        }
        await this.PublishAsync(new SessionRetentionChangedEvent
        {
            ContinueInBackground = background,
            ViewerCount = viewers,
        }, ct: ct).ConfigureAwait(false);
    }

    private async Task TerminateCoreAsync()
    {
        var failures = new List<Exception>();
        AttachmentState[] states;
        lock (this.gate) states = this.attachments.Values.ToArray();
        foreach (var state in states) state.GraceTimer?.Dispose();

        if (this.ownershipLease is not null)
        {
            try { await this.ownershipLease.QuiesceAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        }
        try
        {
            if (this.Chat.IsBusy)
                await this.Chat.InterruptAsync().ConfigureAwait(false);
        }
        catch (Exception error) { failures.Add(error); }
        this.Chat.InformationChanged -= this.OnInformationChanged;
        this.Chat.UsageChanged -= this.OnUsageChanged;
        this.Chat.ToolsChanged -= this.OnToolsChanged;
        this.Chat.TurnCompleted -= this.OnTurnCompleted;
        this.Chat.InputQueues.Changed -= this.OnQueuesChanged;
        ((INotifyCollectionChanged)this.Chat.RunningItems).CollectionChanged -= this.OnRunningItemsChanged;
        lock (this.gate)
        {
            foreach (var item in this.runningItemIds.Keys)
                item.Items.CollectionChanged -= this.OnRunningItemUpdated;
            this.runningItemIds.Clear();
        }
        ((INotifyCollectionChanged)this.Chat.SubAgents).CollectionChanged -= this.OnSubagentsChanged;
        ((INotifyCollectionChanged)this.Chat.Modals).CollectionChanged -= this.OnModalsChanged;
        try
        {
            if (this.runtimeLifetime is not null)
                await this.runtimeLifetime.DisposeAsync().ConfigureAwait(false);
            else
                await this.Chat.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error) { failures.Add(error); }

        Exception? persistenceFailure = null;
        try
        {
            await this.persistTerminalAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            persistenceFailure = error;
            failures.Add(error);
        }

        if (this.ownershipLease is not null && persistenceFailure is null)
        {
            try { await this.ownershipLease.ReleaseAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        }
        if (this.ownershipLease is not null)
        {
            try { await this.ownershipLease.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        }

        JsonElement? serializedTerminal = null;
        if (persistenceFailure is null)
        {
            var terminal = this.Replay.Append(Guid.NewGuid(), new SessionTerminalEvent
            {
                Reason = this.terminalReason,
                CompletionState = System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    stopped = true,
                    failed = this.terminalReason == "runtime-failed",
                }),
            });
            serializedTerminal = AgentSessionProtocolCodec.SerializeFrame(terminal);
        }
        foreach (var state in states)
        {
            if (!state.Disconnected && serializedTerminal is { } terminal)
            {
                try { await state.Channel.Writer.WriteAsync(terminal).ConfigureAwait(false); }
                catch (Exception error) { failures.Add(error); }
            }
            try { await state.Channel.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        }
        lock (this.gate)
        {
            foreach (var state in this.attachments.Values)
                state.Released.TrySetResult();
            this.attachments.Clear();
            this.hasTerminated = persistenceFailure is null;
        }
        if (persistenceFailure is null && this.Terminated is { } terminated)
        {
            foreach (EventHandler handler in terminated.GetInvocationList())
            {
                try { handler(this, EventArgs.Empty); }
                catch (Exception error) { failures.Add(error); }
            }
        }
        if (failures.Count != 0)
            throw new AggregateException("Remote agent session shutdown failed.", failures);
    }

    private sealed class AttachmentState(IMessageChannel channel)
    {
        internal IMessageChannel Channel { get; set; } = channel;
        internal bool Disconnected { get; set; }
        internal ITimer? GraceTimer { get; set; }
        internal long Generation { get; set; } = 1;
        internal TaskCompletionSource Released { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal readonly record struct InitialAttachmentState(
        RemoteAgentAttachmentLease Attachment,
        IReadOnlyList<AgentSessionServerFrame> Frames,
        AgentSessionSnapshot? Snapshot);

    private sealed record CommandCacheEntry(string Payload, Task<AgentSessionServerEvent> Result);

    private void OnInformationChanged(object? sender, EventArgs args)
        => this.PublishFromOwner(new AgentInformationChangedEvent { Information = this.Chat.Information });

    private void OnUsageChanged(object? sender, EventArgs args)
        => this.PublishFromOwner(new UsageChangedEvent { Usage = this.Chat.Usage });

    private void OnToolsChanged(object? sender, EventArgs args)
        => this.PublishFromOwner(new ToolsChangedEvent
        {
            Tools = this.Chat.GetToolSnapshot().Select(item => JsonSerializer.SerializeToElement(item)).ToArray(),
        });

    private void OnTurnCompleted(object? sender, AgentChatHistoryItem item)
        => this.PublishFromOwner(new HistoryAppendedEvent { Item = JsonSerializer.SerializeToElement(item) });

    private void OnQueuesChanged(object? sender, EventArgs args)
    {
        var snapshot = this.Chat.InputQueues.Snapshot;
        var current = snapshot.Queues.IsDefault
            ? []
            : snapshot.Queues.Select(queue => queue.QueueId).ToHashSet(StringComparer.Ordinal);
        var removed = this.queueIds.Except(current, StringComparer.Ordinal).ToArray();
        this.queueIds = current;
        this.PublishFromOwner(new QueueChangedEvent
        {
            Revision = snapshot.Revision,
            Queues = snapshot.Queues,
            RemovedQueueIds = removed,
        });
    }

    private void OnRunningItemsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        lock (this.gate)
        {
            if (this.fenced) return;
            if (args.OldItems is not null)
            {
                foreach (AgentChatRunningItem item in args.OldItems)
                {
                    item.Items.CollectionChanged -= this.OnRunningItemUpdated;
                    if (this.runningItemIds.Remove(item, out var runId))
                    {
                        this.PublishFromOwner(new StreamingCompletedEvent
                        {
                            RunId = runId,
                            Item = JsonSerializer.SerializeToElement(item),
                        });
                    }
                }
            }
            if (args.NewItems is not null)
            {
                foreach (AgentChatRunningItem item in args.NewItems)
                {
                    var runId = Guid.NewGuid().ToString("N");
                    this.runningItemIds[item] = runId;
                    item.Items.CollectionChanged += this.OnRunningItemUpdated;
                    this.PublishFromOwner(new StreamingStartedEvent
                    {
                        RunId = runId,
                        Item = JsonSerializer.SerializeToElement(item),
                    });
                }
            }
            this.PublishFromOwner(new BusyChangedEvent { IsBusy = this.Chat.IsBusy });
        }
    }

    private void OnRunningItemUpdated(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (sender is not ICollection<AgentChatHistoryItem> items) return;
        lock (this.gate)
        {
            if (this.fenced) return;
            var entry = this.runningItemIds.FirstOrDefault(pair => ReferenceEquals(pair.Key.Items, items));
            if (entry.Key is null) return;
            this.PublishFromOwner(new StreamingUpdatedEvent
            {
                RunId = entry.Value,
                Update = JsonSerializer.SerializeToElement(entry.Key),
            });
        }
    }

    private void OnSubagentsChanged(object? sender, NotifyCollectionChangedEventArgs args)
        => this.PublishFromOwner(new SubagentsChangedEvent
        {
            Subagents = this.Chat.SubAgents.Select(item => JsonSerializer.SerializeToElement(item, item.GetType())).ToArray(),
        });

    private void OnModalsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == NotifyCollectionChangedAction.Remove && args.OldItems is { Count: > 0 })
        {
            foreach (AgentChatModal modal in args.OldItems)
                this.PublishFromOwner(new ModalDismissedEvent { ModalId = modal.Id });
            return;
        }
        foreach (var modal in this.Chat.Modals)
            this.PublishFromOwner(new ModalUpdatedEvent { Modal = modal });
    }

    private void PublishFromOwner(AgentSessionServerEvent value)
    {
        lock (this.gate)
        {
            if (this.fenced) return;
            var serialized = AgentSessionProtocolCodec.SerializeFrame(
                this.Replay.Append(Guid.NewGuid(), value));
            foreach (var channel in this.attachments.Values.Where(state => !state.Disconnected).Select(state => state.Channel))
                channel.Writer.TryWrite(serialized);
        }
    }
}

internal sealed class RemoteAgentAttachmentLease : IAsyncDisposable
{
    private readonly RemoteAgentSessionLease owner;
    private readonly string token;
    private readonly long generation;
    private int disposed;
    private CancellationTokenSource? receiveCancellation;

    internal RemoteAgentAttachmentLease(
        RemoteAgentSessionLease owner, string token, long generation, ProtocolReplayCursor? cursor)
    {
        this.owner = owner;
        this.token = token;
        this.generation = generation;
        this.Cursor = cursor ?? new ProtocolReplayCursor { Epoch = owner.Epoch, Sequence = owner.Replay.HighWaterMark };
    }

    internal ProtocolReplayCursor Cursor { get; }
    internal Task Released => this.owner.GetReleaseTask(this.token, this.generation);

    internal ValueTask PublishAsync(AgentSessionServerEvent value, CancellationToken ct = default)
        => this.owner.PublishToAttachmentAsync(this.token, value, ct);

    internal ValueTask MarkTransportLostAsync()
        => this.owner.MarkTransportLostAsync(this.token);

    internal void StartReceiving(Func<AgentSessionCommand, CancellationToken, ValueTask> handleAsync)
    {
        this.receiveCancellation = new CancellationTokenSource();
        _ = this.ReceiveAsync(handleAsync, this.receiveCancellation.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) == 0)
        {
            this.receiveCancellation?.Cancel();
            this.receiveCancellation?.Dispose();
            await this.owner.ReleaseAsync(this.token, this.generation).ConfigureAwait(false);
        }
    }

    private async Task ReceiveAsync(
        Func<AgentSessionCommand, CancellationToken, ValueTask> handleAsync,
        CancellationToken ct)
    {
        try
        {
            var channel = this.owner.GetConnectedChannel(this.token, this.generation);
            if (channel is null) return;
            await foreach (var value in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await handleAsync(AgentSessionProtocolCodec.DeserializeCommand(value), ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested) await this.MarkTransportLostAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch
        {
            await this.MarkTransportLostAsync().ConfigureAwait(false);
        }
    }
}
