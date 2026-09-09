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
    private readonly Dictionary<string, AttachmentState> attachments = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, CommandCacheEntry> commands = [];
    private readonly TimeProvider timeProvider;
    private readonly Func<bool, CancellationToken, ValueTask> persistRetentionAsync;
    private readonly Func<CancellationToken, ValueTask> persistTerminalAsync;
    private readonly Func<AgentSessionSnapshot> snapshotFactory;
    private readonly AgentSessionOwnershipLease? ownershipLease;
    private Task? termination;
    private bool fenced;
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
        AgentSessionOwnershipLease? ownershipLease = null)
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
        this.Replay = new AgentSessionReplayBuffer(epoch, this.timeProvider);
        this.Chat.InformationChanged += this.OnInformationChanged;
        this.Chat.UsageChanged += this.OnUsageChanged;
        this.Chat.ToolsChanged += this.OnToolsChanged;
        this.Chat.TurnCompleted += this.OnTurnCompleted;
        this.Chat.InputQueues.Changed += this.OnQueuesChanged;
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
    internal int ViewerCount { get { lock (this.gate) return this.attachments.Count; } }
    internal bool ContinueInBackground { get { lock (this.gate) return this.continueInBackground; } }

    internal RemoteAgentAttachmentLease Attach(AttachRemoteAgentSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (this.gate)
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

    internal async ValueTask SetContinueInBackgroundAsync(bool value, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            if (this.fenced) throw new InvalidOperationException("The runtime is stopping.");
            if (this.continueInBackground == value) return;
        }

        await this.persistRetentionAsync(value, ct).ConfigureAwait(false);
        var stop = false;
        lock (this.gate)
        {
            if (this.fenced) throw new InvalidOperationException("The runtime is stopping.");
            this.continueInBackground = value;
            stop = !value && this.attachments.Count == 0;
        }
        await this.PublishRetentionChangedAsync(ct).ConfigureAwait(false);
        if (stop) await this.TryTerminateAsync(ct).ConfigureAwait(false);
    }

    internal ValueTask PublishAsync(
        AgentSessionServerEvent value, Guid correlationId = default, CancellationToken ct = default)
    {
        var frame = this.Replay.Append(correlationId == default ? Guid.NewGuid() : correlationId, value);
        return this.PublishFrameAsync(frame, ct);
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
    {
        Task task;
        lock (this.gate)
        {
            if (this.termination is not null)
            {
                task = this.termination;
            }
            else
            {
                this.fenced = true;
                task = this.termination = this.TerminateCoreAsync();
            }
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
        bool stop;
        lock (this.gate)
        {
            if (!this.attachments.TryGetValue(token, out var state) || state.Generation != generation) return;
            this.attachments.Remove(token);
            state.GraceTimer?.Dispose();
            stop = !this.fenced && this.attachments.Count == 0 && !this.continueInBackground;
        }
        await this.PublishRetentionChangedAsync().ConfigureAwait(false);
        if (stop) await this.TryTerminateAsync().ConfigureAwait(false);
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

    internal async ValueTask PublishToAttachmentAsync(
        string token, AgentSessionServerEvent value, CancellationToken ct)
    {
        var frame = this.Replay.Append(Guid.NewGuid(), value);
        IMessageChannel channel;
        lock (this.gate)
        {
            if (!this.attachments.TryGetValue(token, out var state) || state.Disconnected)
                throw new ObjectDisposedException(nameof(RemoteAgentAttachmentLease));
            channel = state.Channel;
        }
        await channel.Writer.WriteAsync(AgentSessionProtocolCodec.SerializeFrame(frame), ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await this.TryTerminateAsync().ConfigureAwait(false);

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

    private async ValueTask PublishFrameAsync(AgentSessionServerFrame frame, CancellationToken ct)
    {
        IMessageChannel[] channels;
        lock (this.gate)
            channels = this.attachments.Values.Where(value => !value.Disconnected).Select(value => value.Channel).ToArray();
        foreach (var channel in channels)
            await channel.Writer.WriteAsync(AgentSessionProtocolCodec.SerializeFrame(frame), ct).ConfigureAwait(false);
    }

    private async Task TerminateCoreAsync()
    {
        AttachmentState[] states;
        lock (this.gate) states = this.attachments.Values.ToArray();
        foreach (var state in states) state.GraceTimer?.Dispose();

        this.Chat.Interrupt();
        this.Chat.InformationChanged -= this.OnInformationChanged;
        this.Chat.UsageChanged -= this.OnUsageChanged;
        this.Chat.ToolsChanged -= this.OnToolsChanged;
        this.Chat.TurnCompleted -= this.OnTurnCompleted;
        this.Chat.InputQueues.Changed -= this.OnQueuesChanged;
        ((INotifyCollectionChanged)this.Chat.SubAgents).CollectionChanged -= this.OnSubagentsChanged;
        ((INotifyCollectionChanged)this.Chat.Modals).CollectionChanged -= this.OnModalsChanged;
        await this.Chat.DisposeAsync().ConfigureAwait(false);
        await this.persistTerminalAsync(CancellationToken.None).ConfigureAwait(false);
        if (this.ownershipLease is not null)
            await this.ownershipLease.ReleaseAsync().ConfigureAwait(false);
        var terminal = this.Replay.Append(Guid.NewGuid(), new SessionTerminalEvent
        {
            Reason = "runtime-stopped",
            CompletionState = System.Text.Json.JsonSerializer.SerializeToElement(new { stopped = true }),
        });
        foreach (var state in states)
        {
            if (!state.Disconnected)
            {
                try { await state.Channel.Writer.WriteAsync(AgentSessionProtocolCodec.SerializeFrame(terminal)).ConfigureAwait(false); }
                catch (System.Threading.Channels.ChannelClosedException) { }
            }
            await state.Channel.DisposeAsync().ConfigureAwait(false);
        }
        lock (this.gate) this.attachments.Clear();
        this.Terminated?.Invoke(this, EventArgs.Empty);
    }

    private sealed class AttachmentState(IMessageChannel channel)
    {
        internal IMessageChannel Channel { get; set; } = channel;
        internal bool Disconnected { get; set; }
        internal ITimer? GraceTimer { get; set; }
        internal long Generation { get; set; } = 1;
    }

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
        this.PublishFromOwner(new QueueChangedEvent
        {
            Revision = snapshot.Revision,
            Queues = snapshot.Queues,
            RemovedQueueIds = [],
        });
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
