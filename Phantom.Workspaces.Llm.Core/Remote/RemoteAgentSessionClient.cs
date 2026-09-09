using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Llm.Remote;

public sealed class RemoteAgentSessionClient : IAsyncDisposable
{
    private readonly ITransport transport;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, PendingCommand> pending = new();
    private readonly ConcurrentDictionary<Guid, Guid> abandonedCommands = new();
    private IMessageChannel? channel;
    private CancellationTokenSource? pumpCancellation;
    private Task? pump;
    private AgentSessionOpenRequest? openRequest;
    private TaskCompletionSource<AgentSessionServerFrame>? ready;
    private RuntimeEpoch? runtimeEpoch;
    private DateTimeOffset? reconnectDeadline;
    private bool connectAttempted;
    private bool reconnecting;
    private bool detached;
    private bool terminal;
    private bool disposed;
    private Task? reconnectAttempt;

    public RemoteAgentSessionClient(ITransport transport)
        : this(transport, TimeProvider.System)
    {
    }

    internal RemoteAgentSessionClient(ITransport transport, TimeProvider timeProvider)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public event EventHandler<AgentSessionServerFrame>? FrameReceived;
    internal event EventHandler? UnexpectedlyDisconnected;
    public ReplayCursor? LastAppliedCursor { get; private set; }

    public static async Task<AgentSessionRemoteStatus> GetStatusAsync(
        AgentSessionStatusRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Transport);
        ArgumentNullException.ThrowIfNull(request.OpenRequest);
        if (request.OpenRequest.OpenIntent != AgentSessionOpenIntent.Status)
            throw new ArgumentException("Status requests require the Status open intent.", nameof(request));

        IMessageChannel? channel = null;
        try
        {
            channel = await request.Transport.ConnectToMessageChannelAsync(
                AgentSessionProtocolCodec.SerializeOpen(request.OpenRequest), ct).ConfigureAwait(false);
            var raw = await channel.Reader.ReadAsync(ct).ConfigureAwait(false);
            var frame = AgentSessionProtocolCodec.DeserializeFrame(raw);
            return AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(frame) is SessionStatusEvent status
                ? status.Status
                : AgentSessionRemoteStatus.Unavailable;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return AgentSessionRemoteStatus.Unavailable;
        }
        finally
        {
            if (channel is not null)
                await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task ConnectAsync(AgentSessionOpenRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await this.lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            this.ThrowIfDisposed();
            if (this.connectAttempted) throw new InvalidOperationException("ConnectAsync may only be called once.");
            this.connectAttempted = true;
            AgentSessionProtocolCodec.SerializeOpen(request);
            this.openRequest = request;
            try
            {
                await this.OpenChannelAsync(request, ct).ConfigureAwait(false);
            }
            catch
            {
                this.connectAttempted = false;
                this.openRequest = null;
                throw;
            }
        }
        finally
        {
            this.lifecycleGate.Release();
        }
    }

    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        Task attempt;
        await this.lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            this.ThrowIfDisposed();
            if (this.reconnectAttempt is { IsCompleted: false } currentAttempt)
            {
                attempt = currentAttempt;
            }
            else
            {
                if (!this.connectAttempted || this.openRequest is null || this.channel is not null
                    || this.detached || this.terminal || this.reconnectDeadline is null
                    || this.timeProvider.GetUtcNow() >= this.reconnectDeadline)
                    throw new InvalidOperationException("The session is not eligible for reconnect.");

                var request = this.openRequest with
                {
                    OpenIntent = AgentSessionOpenIntent.Attach,
                    ReplayCursor = this.LastAppliedCursor,
                };
                this.reconnecting = true;
                attempt = this.OpenChannelAsync(request, ct);
                this.reconnectAttempt = attempt;
            }
        }
        finally
        {
            this.lifecycleGate.Release();
        }

        await attempt.WaitAsync(ct).ConfigureAwait(false);
    }

    public Task<AgentInputQueueCommandResult> CreateQueueAsync(
        CreateAgentInputQueueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return this.SendResultAsync<AgentInputQueueCommandResult>(new CreateQueueCommand
        {
            ExpectedRevision = request.ExpectedRevision,
            Configuration = request.Configuration,
            CommandId = request.CommandId,
            CorrelationId = Guid.NewGuid(),
            RuntimeEpoch = this.RequireEpoch(),
        }, ct);
    }

    public Task<AgentInputQueueCommandResult> DeleteQueueAsync(
        DeleteAgentInputQueueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return this.SendResultAsync<AgentInputQueueCommandResult>(new DeleteQueueCommand
        {
            ExpectedRevision = request.ExpectedRevision,
            QueueId = RequireText(request.QueueId, nameof(request.QueueId)),
            CommandId = request.CommandId,
            CorrelationId = Guid.NewGuid(),
            RuntimeEpoch = this.RequireEpoch(),
        }, ct);
    }

    public Task<AgentInputQueueCommandResult> EnqueueAsync(
        EnqueueAgentInputRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Messages);
        return this.SendResultAsync<AgentInputQueueCommandResult>(new EnqueueInputCommand
        {
            ExpectedRevision = request.ExpectedRevision,
            TargetQueueId = RequireText(request.TargetQueueId, nameof(request.TargetQueueId)),
            Messages = JsonSerializer.SerializeToElement(request.Messages, AIJsonUtilities.DefaultOptions),
            CommandId = request.CommandId,
            CorrelationId = Guid.NewGuid(),
            RuntimeEpoch = this.RequireEpoch(),
        }, ct);
    }

    public Task<AgentInputQueueCommandResult> EditAsync(
        EditAgentInputQueueItemRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Messages);
        return this.SendResultAsync<AgentInputQueueCommandResult>(new EditQueueItemCommand
        {
            ExpectedRevision = request.ExpectedRevision,
            QueueId = RequireText(request.QueueId, nameof(request.QueueId)),
            ItemId = RequireText(request.ItemId, nameof(request.ItemId)),
            Messages = JsonSerializer.SerializeToElement(request.Messages, AIJsonUtilities.DefaultOptions),
            CommandId = request.CommandId,
            CorrelationId = Guid.NewGuid(),
            RuntimeEpoch = this.RequireEpoch(),
        }, ct);
    }

    public Task<AgentInputQueueCommandResult> RemoveAsync(
        RemoveAgentInputQueueItemRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return this.SendResultAsync<AgentInputQueueCommandResult>(new RemoveQueueItemCommand
        {
            ExpectedRevision = request.ExpectedRevision,
            QueueId = RequireText(request.QueueId, nameof(request.QueueId)),
            ItemId = RequireText(request.ItemId, nameof(request.ItemId)),
            CommandId = request.CommandId,
            CorrelationId = Guid.NewGuid(),
            RuntimeEpoch = this.RequireEpoch(),
        }, ct);
    }

    public Task<AgentInputQueueCommandResult> MoveAsync(
        MoveAgentInputQueueItemRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return this.SendResultAsync<AgentInputQueueCommandResult>(new MoveQueueItemCommand
        {
            ExpectedRevision = request.ExpectedRevision,
            SourceQueueId = RequireText(request.SourceQueueId, nameof(request.SourceQueueId)),
            ItemId = RequireText(request.ItemId, nameof(request.ItemId)),
            TargetQueueId = RequireText(request.TargetQueueId, nameof(request.TargetQueueId)),
            BeforeItemId = request.BeforeItemId,
            CommandId = request.CommandId,
            CorrelationId = Guid.NewGuid(),
            RuntimeEpoch = this.RequireEpoch(),
        }, ct);
    }

    public Task<AgentInputQueueCommandResult> ConfigureAsync(
        ConfigureAgentInputQueueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return this.SendResultAsync<AgentInputQueueCommandResult>(new ConfigureQueueCommand
        {
            ExpectedRevision = request.ExpectedRevision,
            QueueId = RequireText(request.QueueId, nameof(request.QueueId)),
            Configuration = request.Configuration,
            CommandId = request.CommandId,
            CorrelationId = Guid.NewGuid(),
            RuntimeEpoch = this.RequireEpoch(),
        }, ct);
    }

    public Task InterruptAsync(Guid commandId, CancellationToken ct = default)
        => this.SendNoResultAsync(new InterruptCommand
        {
            CommandId = commandId, CorrelationId = Guid.NewGuid(), RuntimeEpoch = this.RequireEpoch(),
        }, ct);

    public async Task TerminateAsync(TerminateAgentSessionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var frame = await this.SendAsync(new TerminateSessionCommand
        {
            Reason = RequireText(request.Reason, nameof(request.Reason)),
            CommandId = request.CommandId,
            CorrelationId = Guid.NewGuid(),
            RuntimeEpoch = this.RequireEpoch(),
        }, ct, acceptTerminal: true).ConfigureAwait(false);
        if (frame.Type != "session-terminal")
            ReadCompletion(frame);
    }

    public Task<RemoteSubagentDescriptor> OpenSubagentAsync(
        OpenAgentSubagentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return this.SendResultAsync<RemoteSubagentDescriptor>(new OpenSubagentCommand
        {
            AgentId = RequireText(request.AgentId, nameof(request.AgentId)),
            CommandId = request.CommandId, CorrelationId = Guid.NewGuid(), RuntimeEpoch = this.RequireEpoch(),
        }, ct);
    }

    public Task RespondToModalAsync(RespondToAgentModalRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return this.SendNoResultAsync(new ModalResponseCommand
        {
            ModalId = RequireText(request.ModalId, nameof(request.ModalId)),
            Response = request.Response.Clone(), CommandId = request.CommandId,
            CorrelationId = Guid.NewGuid(), RuntimeEpoch = this.RequireEpoch(),
        }, ct);
    }

    public Task SetToolEnabledAsync(SetAgentToolEnabledRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return this.SendNoResultAndAuthoritativeEventAsync(new SetToolEnabledCommand
        {
            ToolId = RequireText(request.ToolId, nameof(request.ToolId)), Enabled = request.Enabled,
            CommandId = request.CommandId, CorrelationId = Guid.NewGuid(), RuntimeEpoch = this.RequireEpoch(),
        }, frame => AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(frame)
            is ToolsChangedEvent changed
            && changed.Tools
                .Select(tool => JsonSerializer.Deserialize<AgentChatToolItem>(
                    tool.GetRawText(), AIJsonUtilities.DefaultOptions))
                .Any(tool => tool is not null
                    && tool.Id == request.ToolId
                    && tool.IsEnabled == request.Enabled), ct);
    }

    public Task SetContinueInBackgroundAsync(
        SetAgentSessionRetentionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return this.SendNoResultAndAuthoritativeEventAsync(new SetContinueInBackgroundCommand
        {
            ContinueInBackground = request.ContinueInBackground, CommandId = request.CommandId,
            CorrelationId = Guid.NewGuid(), RuntimeEpoch = this.RequireEpoch(),
        }, frame => AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(frame)
            is SessionRetentionChangedEvent changed
            && changed.ContinueInBackground == request.ContinueInBackground, ct);
    }

    public async Task DetachAsync(CancellationToken ct = default)
    {
        if (this.disposed || this.detached) return;
        this.detached = true;
        try
        {
            if (this.channel is not null && this.runtimeEpoch is { } epoch)
            {
                await this.channel.Writer.WriteAsync(AgentSessionProtocolCodec.SerializeCommand(new DetachCommand
                {
                    CommandId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), RuntimeEpoch = epoch,
                }), ct).ConfigureAwait(false);
            }
        }
        catch when (!ct.IsCancellationRequested) { }
        await this.CloseChannelAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (this.disposed) return;
        this.disposed = true;
        await this.CloseChannelAsync().ConfigureAwait(false);
        this.lifecycleGate.Dispose();
    }

    private async Task OpenChannelAsync(AgentSessionOpenRequest request, CancellationToken ct)
    {
        var opened = await this.transport.ConnectToMessageChannelAsync(
            AgentSessionProtocolCodec.SerializeOpen(request), ct).ConfigureAwait(false);
        this.channel = opened;
        this.ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pumpCancellation = new();
        this.pump = this.PumpAsync(opened, this.pumpCancellation.Token);
        try
        {
            await this.ready.Task.WaitAsync(ct).ConfigureAwait(false);
            this.reconnectDeadline = null;
        }
        catch
        {
            await this.CloseChannelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task PumpAsync(IMessageChannel activeChannel, CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
            await foreach (var raw in activeChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var frame = AgentSessionProtocolCodec.DeserializeFrame(raw);
                this.AcceptFrame(frame);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            await activeChannel.DisposeAsync().ConfigureAwait(false);
            if (ReferenceEquals(this.channel, activeChannel))
            {
                this.channel = null;
                if (!this.detached && !this.terminal && !this.disposed)
                {
                    this.reconnectDeadline = this.timeProvider.GetUtcNow().AddSeconds(5);
                    this.UnexpectedlyDisconnected?.Invoke(this, EventArgs.Empty);
                }
            }
            failure ??= new RemoteAgentProtocolException("The remote session channel closed.");
            this.ready?.TrySetException(failure);
            foreach (var pendingCommand in this.pending.Values)
                pendingCommand.Completion.TrySetException(failure);
        }
    }

    private void AcceptFrame(AgentSessionServerFrame frame)
    {
        if (this.runtimeEpoch is { } epoch && epoch != frame.RuntimeEpoch)
            throw new RemoteAgentProtocolException("The runtime epoch changed.");
        if (this.LastAppliedCursor is { } cursor && frame.Sequence != cursor.Sequence + 1)
            throw new RemoteAgentProtocolException("A server frame sequence gap or regression was detected.");

        var value = AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(frame);
        if (this.runtimeEpoch is null && value is not SessionSnapshotEvent)
            throw new RemoteAgentProtocolException("The first attached-session frame must be a snapshot.");
        this.runtimeEpoch ??= frame.RuntimeEpoch;
        this.LastAppliedCursor = new ReplayCursor { Epoch = frame.RuntimeEpoch, Sequence = frame.Sequence };
        if (value is SessionSnapshotEvent || this.reconnecting)
        {
            this.reconnecting = false;
            this.ready?.TrySetResult(frame);
        }
        if (value is SessionTerminalEvent)
            this.terminal = true;
        if (value is CommandCompletedEvent or OperationErrorEvent)
        {
            if (this.pending.TryGetValue(frame.CorrelationId, out var pendingCommand))
            {
                if (value is CommandCompletedEvent && pendingCommand.AcceptTerminal)
                {
                    this.FrameReceived?.Invoke(this, frame);
                    return;
                }
                this.pending.TryRemove(frame.CorrelationId, out _);
                if (value is CommandCompletedEvent completed && completed.CommandId != pendingCommand.CommandId)
                    throw new RemoteAgentProtocolException("A command acknowledgement contained a mismatched command id.");
                if (value is OperationErrorEvent error && error.Error.CorrelationId != frame.CorrelationId)
                    throw new RemoteAgentProtocolException("An operation error contained a mismatched correlation id.");
                pendingCommand.Completion.TrySetResult(frame);
            }
            else
            {
                if (!this.abandonedCommands.TryRemove(frame.CorrelationId, out var abandonedCommandId))
                    throw new RemoteAgentProtocolException("A command response contained an unknown correlation id.");
                if (value is CommandCompletedEvent abandonedCompletion
                    && abandonedCompletion.CommandId != abandonedCommandId)
                    throw new RemoteAgentProtocolException("A command acknowledgement contained a mismatched command id.");
                if (value is OperationErrorEvent abandonedError
                    && abandonedError.Error.CorrelationId != frame.CorrelationId)
                    throw new RemoteAgentProtocolException("An operation error contained a mismatched correlation id.");
            }
        }
        else if (value is SessionTerminalEvent
            && this.pending.TryGetValue(frame.CorrelationId, out var terminalCommand))
        {
            if (terminalCommand.AcceptTerminal)
            {
                this.pending.TryRemove(frame.CorrelationId, out _);
                terminalCommand.Completion.TrySetResult(frame);
            }
        }
        this.FrameReceived?.Invoke(this, frame);
    }

    private async Task<T> SendResultAsync<T>(AgentSessionCommand command, CancellationToken ct)
    {
        var frame = await this.SendAsync(command, ct).ConfigureAwait(false);
        var completed = ReadCompletion(frame);
        if (completed.Result is not { } result)
            throw new RemoteAgentProtocolException("The command result was missing.");
        var value = JsonSerializer.Deserialize<T>(result.GetRawText(), AgentSessionProtocolCodec.Options)
            ?? throw new RemoteAgentProtocolException("The command result was null.");
        if (value is RemoteSubagentDescriptor descriptor
            && (string.IsNullOrWhiteSpace(descriptor.AgentSessionId)
                || string.IsNullOrWhiteSpace(descriptor.AgentId)
                || string.IsNullOrWhiteSpace(descriptor.OwningProfileEntityId)
                || descriptor.OwnershipGeneration < 0
                || descriptor.RuntimeEpoch.Value == Guid.Empty))
            throw new RemoteAgentProtocolException("The remote subagent descriptor is invalid.");
        if (value is AgentInputQueueCommandResult queueResult
            && (queueResult.CommandId != command.CommandId || queueResult.Revision < 0))
            throw new RemoteAgentProtocolException("The queue command result is invalid.");
        return value;
    }

    private async Task SendNoResultAsync(AgentSessionCommand command, CancellationToken ct)
    {
        var frame = await this.SendAsync(command, ct).ConfigureAwait(false);
        _ = ReadCompletion(frame);
    }

    private async Task SendNoResultAndAuthoritativeEventAsync(
        AgentSessionCommand command,
        Func<AgentSessionServerFrame, bool> isAuthoritativeEvent,
        CancellationToken ct)
    {
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(object? sender, AgentSessionServerFrame frame)
        {
            if (isAuthoritativeEvent(frame))
                applied.TrySetResult();
        }

        this.FrameReceived += OnFrame;
        try
        {
            await this.SendNoResultAsync(command, ct).ConfigureAwait(false);
            await applied.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            this.FrameReceived -= OnFrame;
        }
    }

    private async Task<AgentSessionServerFrame> SendAsync(
        AgentSessionCommand command, CancellationToken ct, bool acceptTerminal = false)
    {
        this.ThrowIfDisposed();
        if (command.CommandId == Guid.Empty) throw new ArgumentException("Command id cannot be empty.");
        var activeChannel = this.channel ?? throw new InvalidOperationException("The client is not connected.");
        if (this.terminal) throw new InvalidOperationException("The session is terminal.");
        var completion = new TaskCompletionSource<AgentSessionServerFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!this.pending.TryAdd(
            command.CorrelationId,
            new PendingCommand(command.CommandId, completion, acceptTerminal)))
            throw new InvalidOperationException("Duplicate correlation id.");
        try
        {
            await activeChannel.Writer.WriteAsync(AgentSessionProtocolCodec.SerializeCommand(command), ct).ConfigureAwait(false);
            AgentSessionServerFrame frame;
            try
            {
                frame = await completion.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                this.abandonedCommands.TryAdd(command.CorrelationId, command.CommandId);
                throw;
            }
            if (!acceptTerminal && frame.Type == "session-terminal")
                throw new RemoteAgentProtocolException("The session terminated before command completion.");
            return frame;
        }
        finally
        {
            this.pending.TryRemove(command.CorrelationId, out _);
        }
    }

    private static CommandCompletedEvent ReadCompletion(AgentSessionServerFrame frame)
        => AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(frame) switch
        {
            CommandCompletedEvent completed => completed,
            OperationErrorEvent error => throw RemoteAgentSessionException.FromWire(error.Error),
            _ => throw new RemoteAgentProtocolException("Expected command completion or operation error."),
        };

    private RuntimeEpoch RequireEpoch()
    {
        this.ThrowIfDisposed();
        return this.runtimeEpoch ?? throw new InvalidOperationException("The client is not connected.");
    }

    private async Task CloseChannelAsync()
    {
        var cancellation = Interlocked.Exchange(ref this.pumpCancellation, null);
        cancellation?.Cancel();
        var activeChannel = Interlocked.Exchange(ref this.channel, null);
        if (activeChannel is not null)
            await activeChannel.DisposeAsync().ConfigureAwait(false);
        cancellation?.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (this.disposed || this.detached) throw new ObjectDisposedException(nameof(RemoteAgentSessionClient));
    }

    private static string RequireText(string? value, string name)
        => !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"{name} must be nonblank.", name);

    private sealed record PendingCommand(
        Guid CommandId,
        TaskCompletionSource<AgentSessionServerFrame> Completion,
        bool AcceptTerminal);
}
