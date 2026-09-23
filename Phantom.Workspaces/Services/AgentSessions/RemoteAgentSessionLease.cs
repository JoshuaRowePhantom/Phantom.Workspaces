using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Transport;
using ProtocolReplayCursor = Phantom.Workspaces.Llm.Remote.ReplayCursor;
using System.Collections.Specialized;
using System.Text.Json;
using System.Threading.Channels;

namespace Phantom.Workspaces.Services.AgentSessions;

internal sealed class RemoteAgentSessionLease : IAsyncDisposable
{
    private static readonly TimeSpan ReconnectGrace = TimeSpan.FromSeconds(5);
    private readonly object gate = new();
    private readonly SemaphoreSlim transitionGate = new(1, 1);
    private readonly Dictionary<string, AttachmentState> attachments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> attachmentGenerations = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, CommandCacheEntry> commands = [];
    private readonly Dictionary<AgentChatRunningItem, string> runningItemIds =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<AgentChatHistoryItem> runningHistoryItems =
        new(ReferenceEqualityComparer.Instance);
    private readonly TimeProvider timeProvider;
    private readonly AgentChatHistoryCollection? history;
    private readonly Func<bool, CancellationToken, ValueTask> persistRetentionAsync;
    private readonly Func<CancellationToken, ValueTask> persistTerminalAsync;
    private readonly Func<AgentSessionSnapshot> snapshotFactory;
    private readonly AgentSessionOwnershipLease? ownershipLease;
    private readonly IAsyncDisposable? runtimeLifetime;
    private readonly AttachmentPublisherLifetimeHooks? publisherLifetimeHooks;
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
        IAsyncDisposable? runtimeLifetime = null,
        CurrentSessionContext? sessionContext = null,
        AttachmentPublisherLifetimeHooks? publisherLifetimeHooks = null)
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
        this.publisherLifetimeHooks = publisherLifetimeHooks;
        this.SessionContext = sessionContext;
        this.history = this.Chat.History;
        this.Replay = new AgentSessionReplayBuffer(epoch, this.timeProvider);
        var initialQueues = this.Chat.InputQueues.Snapshot.Queues;
        this.queueIds = initialQueues.IsDefault
            ? []
            : initialQueues.Select(queue => queue.QueueId).ToHashSet(StringComparer.Ordinal);
        this.Chat.InformationChanged += this.OnInformationChanged;
        this.Chat.UsageChanged += this.OnUsageChanged;
        this.Chat.ToolsChanged += this.OnToolsChanged;
        if (this.history is not null)
            ((INotifyCollectionChanged)this.history).CollectionChanged += this.OnHistoryChanged;
        this.Chat.InputQueues.Changed += this.OnQueuesChanged;
        ((INotifyCollectionChanged)this.Chat.RunningItems).CollectionChanged += this.OnRunningItemsChanged;
        foreach (var item in this.Chat.RunningItems)
        {
            _ = this.GetOrCreateRunningItemIdUnderLock(item);
            foreach (var historyItem in item.Items)
                this.runningHistoryItems.Add(historyItem);
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
    internal CurrentSessionContext? SessionContext { get; }
    internal AgentSessionReplayBuffer Replay { get; }
    internal bool IsFenced { get { lock (this.gate) return this.fenced; } }
    internal bool HasTerminated { get { lock (this.gate) return this.hasTerminated; } }
    internal int ViewerCount
    {
        get
        {
            lock (this.gate)
                return this.ConnectedViewerCountUnderLock();
        }
    }
    internal bool ContinueInBackground { get { lock (this.gate) return this.continueInBackground; } }

    internal static JsonElement SerializeSubagent(IRunningSubAgent item) =>
        JsonSerializer.SerializeToElement(new
        {
            item.AgentId,
            item.DisplayName,
            item.Description,
            item.Name,
            item.CompletionState,
            item.LastUpdatedAt,
            SubAgents = item.SubAgents.Select(SerializeSubagent).ToArray(),
        }, Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions);

    internal RemoteAgentAttachmentLease Attach(AttachRemoteAgentSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (this.gate)
        {
            var attachment = this.AttachUnderLock(request, staged: false);
            this.PublishRetentionChangedUnderLock(request.Channel);
            return attachment;
        }
    }

    private RemoteAgentAttachmentLease AttachUnderLock(
        AttachRemoteAgentSessionRequest request,
        bool staged)
    {
        if (this.fenced)
            throw new InvalidOperationException("The runtime is stopping.");

        if (this.attachments.TryGetValue(request.AttachmentToken, out var existing))
        {
            if (!existing.Disconnected)
                throw new InvalidOperationException("The attachment token is already connected.");
            existing.GraceTimer?.Dispose();
            existing.Publisher.AbortUnderLock(
                new ObjectDisposedException(nameof(RemoteAgentAttachmentLease)));
            existing.Disconnected = false;
            existing.InboundFenced = false;
            existing.GraceTimer = null;
            existing.Generation++;
            this.attachmentGenerations[request.AttachmentToken] = existing.Generation;
            existing.ReceiveCancellation = new CancellationTokenSource();
            existing.Publisher = this.CreatePublisher(
                request.AttachmentToken,
                existing.Generation,
                request.Channel,
                staged);
            return new RemoteAgentAttachmentLease(this, request.AttachmentToken, existing.Generation, request.Cursor);
        }

        var generation = this.attachmentGenerations.TryGetValue(
            request.AttachmentToken,
            out var previousGeneration)
                ? previousGeneration + 1
                : 1;
        this.attachmentGenerations[request.AttachmentToken] = generation;
        var publisher = this.CreatePublisher(
            request.AttachmentToken,
            generation,
            request.Channel,
            staged);
        var state = new AttachmentState(publisher, generation);
        this.attachments.Add(request.AttachmentToken, state);
        return new RemoteAgentAttachmentLease(this, request.AttachmentToken, state.Generation, request.Cursor);
    }

    internal AgentSessionSnapshot CaptureSnapshot()
    {
        lock (this.gate)
        {
            var snapshot = this.CreateSnapshotUnderLock();
            return snapshot with
            {
                ContinueInBackground = this.continueInBackground,
                ViewerCount = this.ConnectedViewerCountUnderLock(),
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
                RemoteAgentAttachmentLease? attachment = null;
                try
                {
                    var reconnecting = this.attachments.TryGetValue(
                        request.AttachmentToken,
                        out var existing)
                        && existing.Disconnected;
                    attachment = this.AttachUnderLock(request, staged: true);
                    if (!reconnecting)
                        this.PublishRetentionChangedUnderLock(request.Channel);
                    ProtocolReplayCursor? replayResetCursor = null;
                    if (request.Cursor is { } cursor)
                    {
                        var replay = this.Replay.ReadAfter(cursor);
                        if (replay.IsCovered && replay.Frames.Count > 0)
                        {
                            this.PublishRetentionChangedUnderLock(request.Channel);
                            return new InitialAttachmentState(attachment, replay.Frames, null);
                        }
                        if (!replay.IsCovered
                            && cursor.Epoch == this.Epoch
                            && cursor.Sequence < this.Replay.HighWaterMark)
                            replayResetCursor = cursor;
                    }

                    var snapshot = this.CreateSnapshotUnderLock() with
                    {
                        ContinueInBackground = this.continueInBackground,
                        ViewerCount = this.ConnectedViewerCountUnderLock(),
                    };
                    var frame = this.Replay.Append(
                        Guid.NewGuid(),
                        new SessionSnapshotEvent { Snapshot = snapshot },
                        replayResetCursor);
                    var serialized = AgentSessionProtocolCodec.SerializeFrame(frame);
                    foreach (var state in this.attachments.Values.Where(
                                 value => !value.Disconnected
                                     && !ReferenceEquals(value.Channel, request.Channel)))
                        state.Publisher.QueueUnderLock(serialized, waitForWrite: false);
                    return new InitialAttachmentState(attachment, [frame], snapshot);
                }
                catch
                {
                    if (attachment is not null
                        && this.attachments.Remove(request.AttachmentToken, out var failed))
                    {
                        failed.Publisher.AbortUnderLock(
                            new ObjectDisposedException(nameof(RemoteAgentAttachmentLease)));
                        failed.Released.TrySetResult();
                        this.PublishRetentionChangedUnderLock(failed.Channel);
                    }
                    throw;
                }
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
        Task[] writes;
        lock (this.gate)
        {
            if (this.fenced) return;
            frame = this.Replay.Append(correlationId == default ? Guid.NewGuid() : correlationId, value);
            var serialized = AgentSessionProtocolCodec.SerializeFrame(frame);
            writes = this.attachments.Values
                .Where(state => !state.Disconnected)
                .Select(state => state.Publisher.QueueUnderLock(serialized, waitForWrite: true))
                .ToArray();
        }
        await Task.WhenAll(writes).WaitAsync(ct).ConfigureAwait(false);
    }

    internal async ValueTask<AgentSessionServerEvent> ExecuteCommandOnceAsync(
        AgentSessionCommand command,
        Func<CancellationToken, Task<AgentSessionServerEvent>> executeAsync,
        CancellationToken ct)
    {
        // Correlation ids identify one request/response exchange, not the logical
        // mutation. A retry keeps its command id but necessarily gets a new
        // correlation id, so exclude that transport detail from deduplication.
        var payload = AgentSessionProtocolCodec.SerializeCommand(
            command with { CorrelationId = new Guid("00000000-0000-0000-0000-000000000001") }).GetRawText();
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

    internal ValueTask MarkTransportLostAsync(string token, long generation)
    {
        lock (this.gate)
        {
            if (this.fenced
                || !this.attachments.TryGetValue(token, out var state)
                || state.Generation != generation
                || state.Disconnected)
                return ValueTask.CompletedTask;
            this.MarkDisconnectedUnderLock(token, state, fenceInbound: false);
        }
        return ValueTask.CompletedTask;
    }

    internal async ValueTask ReleaseAsync(string token, long generation)
        => await this.ReleaseCoreAsync(token, generation).ConfigureAwait(false);

    internal async ValueTask AbortOpenAsync(string token, long generation)
        => await this.ReleaseCoreAsync(token, generation).ConfigureAwait(false);

    private async ValueTask ReleaseCoreAsync(string token, long generation)
    {
        Task? terminationTask = null;
        Task? publisherTask = null;
        CancellationTokenSource? receiveCancellation = null;
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
                receiveCancellation = state.ReceiveCancellation;
                publisherTask = state.Publisher.AbortUnderLock(
                    new ObjectDisposedException(nameof(RemoteAgentAttachmentLease)));
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
        receiveCancellation?.Cancel();
        if (publisherTask is not null)
            await publisherTask.ConfigureAwait(false);
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

    internal CancellationToken GetReceiveCancellationToken(string token, long generation)
    {
        lock (this.gate)
            return this.attachments.TryGetValue(token, out var state)
                && state.Generation == generation
                && !state.InboundFenced
                    ? state.ReceiveCancellation.Token
                    : new CancellationToken(canceled: true);
    }

    internal void EnsureConnected(string token, long generation)
    {
        lock (this.gate)
        {
            if (this.fenced
                || !this.attachments.TryGetValue(token, out var state)
                || state.Generation != generation
                || state.InboundFenced)
                throw new ObjectDisposedException(nameof(RemoteAgentAttachmentLease));
        }
    }

    internal async ValueTask<bool> ActivateAttachmentAsync(
        string token,
        long generation,
        CancellationToken ct)
    {
        Task activation;
        lock (this.gate)
        {
            if (!this.attachments.TryGetValue(token, out var state)
                || state.Generation != generation
                || state.Disconnected)
                throw new ObjectDisposedException(nameof(RemoteAgentAttachmentLease));
            activation = state.Publisher.ActivateUnderLock();
        }
        await activation.WaitAsync(ct).ConfigureAwait(false);
        lock (this.gate)
            return this.attachments.TryGetValue(token, out var state)
                && state.Generation == generation
                && !state.Disconnected
                && !this.fenced;
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
        string token,
        long generation,
        AgentSessionServerEvent value,
        Guid correlationId,
        CancellationToken ct)
    {
        AgentSessionServerFrame frame;
        Task[] writes;
        lock (this.gate)
        {
            if (!this.attachments.TryGetValue(token, out var state)
                || state.Generation != generation
                || state.Disconnected)
                throw new ObjectDisposedException(nameof(RemoteAgentAttachmentLease));
            frame = this.Replay.Append(correlationId, value);
            var serialized = AgentSessionProtocolCodec.SerializeFrame(frame);
            writes = this.attachments.Values
                .Where(attachment => !attachment.Disconnected)
                .Select(attachment => attachment.Publisher.QueueUnderLock(
                    serialized, waitForWrite: true))
                .ToArray();
        }
        await Task.WhenAll(writes).WaitAsync(ct).ConfigureAwait(false);
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

    private async Task ExpireDisconnectedAsync(string token, long generation)
    {
        lock (this.gate)
        {
            if (!this.attachments.TryGetValue(token, out var state)
                || state.Generation != generation
                || !state.Disconnected)
                return;
        }
        await this.ReleaseAsync(token, generation).ConfigureAwait(false);
    }

    private void PublishRetentionChangedUnderLock(IMessageChannel? excludedChannel)
    {
        var recipients = this.attachments.Values.Where(
            value => !value.Disconnected
                && (excludedChannel is null
                    || !ReferenceEquals(value.Channel, excludedChannel))).ToArray();
        if (recipients.Length == 0)
            return;
        var frame = this.Replay.Append(Guid.NewGuid(), new SessionRetentionChangedEvent
        {
            ContinueInBackground = this.continueInBackground,
            ViewerCount = this.ConnectedViewerCountUnderLock(),
        });
        var serialized = AgentSessionProtocolCodec.SerializeFrame(frame);
        foreach (var state in recipients)
            state.Publisher.QueueUnderLock(serialized, waitForWrite: false);
    }

    private async Task PublishRetentionChangedAsync(CancellationToken ct = default)
    {
        bool background;
        int viewers;
        lock (this.gate)
        {
            if (this.fenced) return;
            background = this.continueInBackground;
            viewers = this.ConnectedViewerCountUnderLock();
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
        if (this.history is not null)
            ((INotifyCollectionChanged)this.history).CollectionChanged -= this.OnHistoryChanged;
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

        Task[] terminalWrites = [];
        if (persistenceFailure is null)
        {
            lock (this.gate)
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
                var serialized = AgentSessionProtocolCodec.SerializeFrame(terminal);
                terminalWrites = this.attachments.Values
                    .Where(state => !state.Disconnected)
                    .Select(state => state.Publisher.QueueUnderLock(
                        serialized, waitForWrite: true))
                    .ToArray();
            }
        }
        foreach (var write in terminalWrites)
        {
            try { await write.ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        }

        Task[] publisherTasks;
        lock (this.gate)
            publisherTasks = states.Select(state => persistenceFailure is null
                    ? state.Publisher.CompleteUnderLock()
                    : state.Publisher.AbortUnderLock(
                        new InvalidOperationException("Terminal persistence failed.")))
                .ToArray();
        foreach (var publisherTask in publisherTasks)
        {
            try { await publisherTask.ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        }
        foreach (var state in states)
        {
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

    private int ConnectedViewerCountUnderLock()
        => this.attachments.Values.Count(state => !state.Disconnected);

    private AttachmentPublisher CreatePublisher(
        string token,
        long generation,
        IMessageChannel channel,
        bool staged)
        => new(
            channel,
            staged,
            (publisher, error) =>
                this.OnPublisherFault(token, generation, publisher, error),
            this.publisherLifetimeHooks);

    private void OnPublisherFault(
        string token,
        long generation,
        AttachmentPublisher publisher,
        Exception error)
    {
        CancellationTokenSource? receiveCancellation = null;
        lock (this.gate)
        {
            if (this.fenced
                || !this.attachments.TryGetValue(token, out var state)
                || state.Generation != generation
                || state.Disconnected
                || !ReferenceEquals(state.Publisher, publisher))
                return;
            receiveCancellation = this.MarkDisconnectedUnderLock(
                token,
                state,
                abortPublisher: false,
                fenceInbound: true);
        }
        receiveCancellation?.Cancel();
    }

    private CancellationTokenSource? MarkDisconnectedUnderLock(
        string token,
        AttachmentState state,
        bool abortPublisher = true,
        bool fenceInbound = true)
    {
        state.Disconnected = true;
        state.InboundFenced = fenceInbound;
        if (abortPublisher)
        {
            state.Publisher.AbortUnderLock(
                new ObjectDisposedException(nameof(RemoteAgentAttachmentLease)));
        }
        var generation = state.Generation;
        state.GraceTimer = this.timeProvider.CreateTimer(
            _ => ObserveBackgroundFault(this.ExpireDisconnectedAsync(token, generation)),
            null,
            ReconnectGrace,
            Timeout.InfiniteTimeSpan);
        this.PublishRetentionChangedUnderLock(state.Channel);
        return fenceInbound ? state.ReceiveCancellation : null;
    }

    private static void ObserveBackgroundFault(Task task)
        => _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private sealed class AttachmentState(
        AttachmentPublisher publisher,
        long generation)
    {
        internal IMessageChannel Channel => this.Publisher.Channel;
        internal AttachmentPublisher Publisher { get; set; } = publisher;
        internal CancellationTokenSource ReceiveCancellation { get; set; } = new();
        internal bool Disconnected { get; set; }
        internal bool InboundFenced { get; set; }
        internal ITimer? GraceTimer { get; set; }
        internal long Generation { get; set; } = generation;
        internal TaskCompletionSource Released { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class AttachmentPublisher
    {
        private readonly object cancellationLifetimeGate = new();
        private readonly Channel<Publication> publications =
            System.Threading.Channels.Channel.CreateUnbounded<Publication>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        private readonly List<Publication> staged = [];
        private readonly CancellationTokenSource cancellation = new();
        private readonly TaskCompletionSource start =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource activation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Action<AttachmentPublisher, Exception> onFault;
        private readonly AttachmentPublisherLifetimeHooks? lifetimeHooks;
        private readonly Task pump;
        private volatile PublicationState state;
        private volatile Exception? failure;

        internal AttachmentPublisher(
            IMessageChannel channel,
            bool staged,
            Action<AttachmentPublisher, Exception> onFault,
            AttachmentPublisherLifetimeHooks? lifetimeHooks)
        {
            this.Channel = channel;
            this.onFault = onFault;
            this.lifetimeHooks = lifetimeHooks;
            this.state = staged ? PublicationState.Staged : PublicationState.Active;
            ObserveFault(this.activation.Task);
            this.pump = this.RunAsync();
            if (!staged)
            {
                this.activation.TrySetResult();
                this.start.TrySetResult();
            }
        }

        internal IMessageChannel Channel { get; }

        internal Task QueueUnderLock(JsonElement frame, bool waitForWrite)
        {
            var completion = waitForWrite
                ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                : null;
            if (completion is not null)
                ObserveFault(completion.Task);
            var publication = new Publication(frame, completion);
            if (this.state == PublicationState.Staged)
            {
                this.staged.Add(publication);
            }
            else if (this.state == PublicationState.Active
                && this.publications.Writer.TryWrite(publication))
            {
            }
            else
            {
                completion?.TrySetException(this.failure
                    ?? new ObjectDisposedException(nameof(RemoteAgentAttachmentLease)));
            }
            return completion?.Task ?? Task.CompletedTask;
        }

        internal Task ActivateUnderLock()
        {
            if (this.state != PublicationState.Staged)
                return this.activation.Task;

            this.staged.Add(new Publication(null, this.activation));
            foreach (var publication in this.staged)
                this.publications.Writer.TryWrite(publication);
            this.staged.Clear();
            this.state = PublicationState.Active;
            this.start.TrySetResult();
            return this.activation.Task;
        }

        internal Task CompleteUnderLock()
        {
            if (this.state == PublicationState.Closed)
                return this.pump;
            this.state = PublicationState.Closed;
            this.publications.Writer.TryComplete();
            this.start.TrySetResult();
            return this.pump;
        }

        internal Task AbortUnderLock(Exception error)
        {
            if (this.state == PublicationState.Closed)
                return this.pump;
            this.failure = error;
            this.state = PublicationState.Closed;
            foreach (var publication in this.staged)
                publication.Completion?.TrySetException(error);
            this.staged.Clear();
            this.activation.TrySetException(error);
            lock (this.cancellationLifetimeGate)
            {
                this.publications.Writer.TryComplete();
                this.lifetimeHooks?.AfterQueueCompletedBeforeAbortCancellation?.Invoke();
                this.cancellation.Cancel();
            }
            this.start.TrySetResult();
            return this.pump;
        }

        private async Task RunAsync()
        {
            await this.start.Task.ConfigureAwait(false);
            try
            {
                await foreach (var publication in this.publications.Reader.ReadAllAsync(
                                   this.cancellation.Token).ConfigureAwait(false))
                {
                    if (publication.Frame is not { } frame)
                    {
                        publication.Completion?.TrySetResult();
                        continue;
                    }
                    try
                    {
                        await this.Channel.Writer.WriteAsync(
                            frame, this.cancellation.Token).ConfigureAwait(false);
                        publication.Completion?.TrySetResult();
                    }
                    catch (Exception error)
                    {
                        this.failure = error;
                        this.state = PublicationState.Closed;
                        this.onFault(this, error);
                        publication.Completion?.TrySetException(error);
                        this.publications.Writer.TryComplete();
                        while (this.publications.Reader.TryRead(out var remaining))
                            remaining.Completion?.TrySetException(error);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (this.cancellation.IsCancellationRequested)
            {
            }
            finally
            {
                var error = this.failure
                    ?? new ObjectDisposedException(nameof(RemoteAgentAttachmentLease));
                while (this.publications.Reader.TryRead(out var remaining))
                    remaining.Completion?.TrySetException(error);
                this.lifetimeHooks?.BeforeCancellationDisposed?.Invoke();
                lock (this.cancellationLifetimeGate)
                    this.cancellation.Dispose();
            }
        }

        private static void ObserveFault(Task task)
            => _ = task.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        private readonly record struct Publication(
            JsonElement? Frame,
            TaskCompletionSource? Completion);

        private enum PublicationState
        {
            Staged,
            Active,
            Closed,
        }
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

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.NewItems is null)
            return;
        lock (this.gate)
        {
            foreach (AgentChatHistoryItem item in args.NewItems)
            {
                if (this.runningHistoryItems.Contains(item))
                    continue;
                this.PublishHistoryItemUnderLock(item);
            }
        }
    }

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
                        var completedItems = item.Items.ToArray();
                        var terminalItem = completedItems.LastOrDefault()
                            ?? new AgentChatHistoryItem
                            {
                                Role = AgentChatHistoryItem.DiagnosticChatRole,
                                Timestamp = this.timeProvider.GetUtcNow(),
                                Contents = [],
                            };
                        this.PublishFromOwner(new StreamingCompletedEvent
                        {
                            RunId = runId,
                            Item = JsonSerializer.SerializeToElement(
                                terminalItem,
                                Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions),
                            Items = completedItems
                                .Select(historyItem => JsonSerializer.SerializeToElement(
                                    historyItem,
                                    Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions))
                                .ToArray(),
                        });
                        foreach (var historyItem in completedItems)
                            this.runningHistoryItems.Remove(historyItem);
                    }
                }
            }
            if (args.NewItems is not null)
            {
                foreach (AgentChatRunningItem item in args.NewItems)
                {
                    var runId = this.GetOrCreateRunningItemIdUnderLock(item);
                    foreach (var historyItem in item.Items)
                        this.runningHistoryItems.Add(historyItem);
                    item.Items.CollectionChanged += this.OnRunningItemUpdated;
                    var initialItem = item.Items.FirstOrDefault()
                        ?? new AgentChatHistoryItem
                        {
                            Role = AgentChatHistoryItem.DiagnosticChatRole,
                            Timestamp = this.timeProvider.GetUtcNow(),
                            Contents = [],
                        };
                    this.PublishFromOwner(new StreamingStartedEvent
                    {
                        RunId = runId,
                        Item = JsonSerializer.SerializeToElement(
                            initialItem,
                            Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions),
                    });
                }
            }
            this.PublishFromOwner(new BusyChangedEvent { IsBusy = this.Chat.RunningItems.Count > 0 });
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
            if (args.OldItems is not null)
            {
                foreach (AgentChatHistoryItem oldItem in args.OldItems)
                {
                    if (!entry.Key.Items.Any(item => ReferenceEquals(item, oldItem))
                        && this.runningHistoryItems.Remove(oldItem)
                        && this.history is not null
                        && this.history.Any(item => ReferenceEquals(item, oldItem)))
                    {
                        this.PublishHistoryItemUnderLock(oldItem);
                    }
                }
            }
            foreach (var historyItem in entry.Key.Items)
                this.runningHistoryItems.Add(historyItem);
            this.PublishFromOwner(new StreamingUpdatedEvent
            {
                RunId = entry.Value,
                Update = JsonSerializer.SerializeToElement(
                    entry.Key.Items.ToArray(),
                    Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions),
            });
        }
    }

    private void PublishHistoryItemUnderLock(AgentChatHistoryItem item)
        => this.PublishFromOwner(
            new HistoryAppendedEvent
            {
                Item = JsonSerializer.SerializeToElement(
                    item,
                    Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions),
            });

    private AgentSessionSnapshot CreateSnapshotUnderLock()
    {
        var snapshot = this.snapshotFactory();
        return snapshot with
        {
            RunningItems = this.Chat.RunningItems
                .Select(item => JsonSerializer.SerializeToElement(
                    new
                    {
                        RunId = this.GetOrCreateRunningItemIdUnderLock(item),
                        Items = item.Items.ToArray(),
                    },
                    Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions))
                .ToArray(),
        };
    }

    private string GetOrCreateRunningItemIdUnderLock(AgentChatRunningItem item)
    {
        // ObservableCollection exposes the new item before its synchronous CollectionChanged
        // handler can acquire this lease's gate. An attach snapshot holding the gate can therefore
        // observe the item first; assign its stable id lazily instead of indexing a lagging mirror.
        if (!this.runningItemIds.TryGetValue(item, out var runId))
        {
            runId = Guid.NewGuid().ToString("N");
            this.runningItemIds.Add(item, runId);
        }
        return runId;
    }

    private void OnSubagentsChanged(object? sender, NotifyCollectionChangedEventArgs args)
        => this.PublishFromOwner(new SubagentsChangedEvent
        {
            Subagents = this.Chat.SubAgents.Select(SerializeSubagent).ToArray(),
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
            foreach (var state in this.attachments.Values.Where(state => !state.Disconnected))
                state.Publisher.QueueUnderLock(serialized, waitForWrite: false);
        }
    }
}

internal sealed record AttachmentPublisherLifetimeHooks
{
    internal Action? AfterQueueCompletedBeforeAbortCancellation { get; init; }
    internal Action? BeforeCancellationDisposed { get; init; }
}

internal sealed class RemoteAgentAttachmentLease : IAsyncDisposable
{
    private readonly RemoteAgentSessionLease owner;
    private readonly string token;
    private readonly long generation;
    private int disposed;
    private int transportLost;
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

    internal ValueTask PublishAsync(
        AgentSessionServerEvent value,
        Guid correlationId,
        CancellationToken ct = default)
        => this.owner.PublishToAttachmentAsync(
            this.token,
            this.generation,
            value,
            correlationId,
            ct);

    internal void EnsureConnected()
        => this.owner.EnsureConnected(this.token, this.generation);

    internal ValueTask<bool> ActivateAsync(CancellationToken ct = default)
        => this.owner.ActivateAttachmentAsync(this.token, this.generation, ct);

    internal ValueTask AbortOpenAsync()
        => this.owner.AbortOpenAsync(this.token, this.generation);

    internal ValueTask MarkTransportLostAsync()
    {
        Volatile.Write(ref this.transportLost, 1);
        return this.owner.MarkTransportLostAsync(this.token, this.generation);
    }

    internal void StartReceiving(Func<AgentSessionCommand, CancellationToken, ValueTask> handleAsync)
    {
        this.receiveCancellation = new CancellationTokenSource();
        _ = this.ReceiveAsync(handleAsync, this.receiveCancellation.Token);
    }

    internal ValueTask ReleaseExplicitlyAsync()
        => this.owner.ReleaseAsync(this.token, this.generation);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) == 0)
        {
            this.receiveCancellation?.Cancel();
            this.receiveCancellation?.Dispose();
            if (Volatile.Read(ref this.transportLost) == 0)
                await this.owner.ReleaseAsync(this.token, this.generation).ConfigureAwait(false);
        }
    }

    private async Task ReceiveAsync(
        Func<AgentSessionCommand, CancellationToken, ValueTask> handleAsync,
        CancellationToken ct)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            this.owner.GetReceiveCancellationToken(this.token, this.generation));
        var receiveToken = linkedCancellation.Token;
        try
        {
            var channel = this.owner.GetConnectedChannel(this.token, this.generation);
            if (channel is null) return;
            await foreach (var value in channel.Reader.ReadAllAsync(receiveToken).ConfigureAwait(false))
                await handleAsync(
                    AgentSessionProtocolCodec.DeserializeCommand(value),
                    receiveToken).ConfigureAwait(false);
            if (!receiveToken.IsCancellationRequested)
                await this.MarkTransportLostAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (receiveToken.IsCancellationRequested) { }
        catch
        {
            await this.MarkTransportLostAsync().ConfigureAwait(false);
        }
    }
}
