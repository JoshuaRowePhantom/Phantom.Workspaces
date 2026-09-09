using System.Text.Json;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Transport;
using System.Collections.Concurrent;

namespace Phantom.Workspaces.Services.AgentSessions;

internal interface IAgentSessionRuntimeHostFactory
{
    ValueTask<PersistedAgentSessionRuntimeIntent?> LoadIntentAsync(string sessionId, CancellationToken ct);
    Task<RemoteAgentSessionLease> StartAsync(PersistedAgentSessionRuntimeIntent intent, CancellationToken ct);
    ValueTask<bool> TryTakeOverAsync(AgentSessionTakeoverRequest request, CancellationToken ct);
}

internal sealed class RemoteAgentSessionHost : IAsyncDisposable
{
    private readonly IAgentSessionAttachAuthorizer authorizer;
    private readonly IRemoteAgentSessionRuntimeRegistry runtimeRegistry;
    private readonly IAgentSessionRuntimeHostFactory runtimeFactory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> openGates = new(StringComparer.Ordinal);

    internal RemoteAgentSessionHost(
        IAgentSessionAttachAuthorizer authorizer,
        IRemoteAgentSessionRuntimeRegistry runtimeRegistry,
        IAgentSessionRuntimeHostFactory runtimeFactory)
    {
        this.authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        this.runtimeRegistry = runtimeRegistry ?? throw new ArgumentNullException(nameof(runtimeRegistry));
        this.runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
    }

    internal RemoteAgentSessionHost(
        IAgentSessionAttachAuthorizer authorizer,
        IRemoteAgentSessionRuntimeRegistry runtimeRegistry,
        IAgentSessionRuntimeContextFactory runtimeContextFactory)
        : this(
            authorizer,
            runtimeRegistry,
            runtimeContextFactory as IAgentSessionRuntimeHostFactory
                ?? new ContextOnlyRuntimeHostFactory(runtimeContextFactory))
    {
    }

    internal async Task<AgentSessionRemoteStatus> GetStatusAsync(
        TransportPeerIdentity peer, AgentSessionOpenRequest request, CancellationToken ct = default)
    {
        if (request.OpenIntent != AgentSessionOpenIntent.Status)
            throw new InvalidOperationException("Status requires the status open intent.");
        if (!await this.AuthorizeAsync(peer, request, AgentSessionAuthorizationOperation.Status, ct).ConfigureAwait(false))
            return AgentSessionRemoteStatus.Unavailable;
        var lease = await this.runtimeRegistry.TryGetAsync(
            request.AgentSessionId, request.ExpectedOwnershipGeneration, ct).ConfigureAwait(false);
        return lease is null ? AgentSessionRemoteStatus.NotRunning : AgentSessionRemoteStatus.Running;
    }

    internal async Task<RemoteAgentAttachmentLease> OpenAsync(
        OpenAgentSessionHostRequest request, CancellationToken ct = default)
    {
        if (request.OpenRequest.OpenIntent == AgentSessionOpenIntent.Status)
            throw new InvalidOperationException("Status does not attach.");
        var operation = request.OpenRequest.ReplayCursor is null
            ? AgentSessionAuthorizationOperation.Open
            : AgentSessionAuthorizationOperation.Reconnect;
        if (!await this.AuthorizeAsync(request.Peer, request.OpenRequest, operation, ct).ConfigureAwait(false))
            throw new AgentSessionUnavailableException();

        var open = request.OpenRequest;
        RemoteAgentSessionLease runtime;
        var openGate = this.openGates.GetOrAdd(open.AgentSessionId, static _ => new SemaphoreSlim(1, 1));
        await openGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await this.runtimeRegistry.TryGetAsync(
                open.AgentSessionId, open.ExpectedOwnershipGeneration, ct).ConfigureAwait(false);
            switch (open.OpenIntent)
            {
                case AgentSessionOpenIntent.Attach:
                    runtime = existing ?? throw new AgentSessionUnavailableException();
                    break;
                case AgentSessionOpenIntent.Start when existing is not null:
                    throw new InvalidOperationException("The agent session is already running.");
                default:
                    var intent = await this.runtimeFactory.LoadIntentAsync(open.AgentSessionId, ct).ConfigureAwait(false)
                        ?? throw new AgentSessionUnavailableException();
                    if (!string.Equals(intent.OwningProfileEntityId, open.ExpectedOwningProfileEntityId, StringComparison.OrdinalIgnoreCase)
                        || intent.OwnershipGeneration != open.ExpectedOwnershipGeneration)
                        throw new AgentSessionUnavailableException();
                    runtime = existing ?? await this.runtimeRegistry.GetOrStartAsync(
                        intent, token => this.runtimeFactory.StartAsync(intent, token), ct).ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            openGate.Release();
        }

        RemoteAgentSessionLease.InitialAttachmentState initial;
        try
        {
            initial = runtime.AttachAndCaptureInitialState(new AttachRemoteAgentSessionRequest
            {
                AttachmentToken = open.AttachmentToken,
                Channel = request.Channel,
                Cursor = open.ReplayCursor,
            });
        }
        catch (InvalidOperationException) when (runtime.IsFenced)
        {
            throw new AgentSessionUnavailableException();
        }
        foreach (var frame in initial.Frames)
        {
            await request.Channel.Writer.WriteAsync(
                AgentSessionProtocolCodec.SerializeFrame(frame), ct).ConfigureAwait(false);
        }
        var attachment = initial.Attachment;
        attachment.StartReceiving((command, token) =>
            this.HandleCommandAsync(request.Peer, open, runtime, attachment, command, token));
        return attachment;
    }

    internal Task TakeOverAsync(
        TransportPeerIdentity peer, AgentSessionTakeoverRequest request, CancellationToken ct = default)
        => this.TakeOverCoreAsync(peer, request, ct);

    public ValueTask DisposeAsync()
        => this.runtimeRegistry is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;

    private async ValueTask<bool> AuthorizeAsync(
        TransportPeerIdentity peer,
        AgentSessionOpenRequest request,
        AgentSessionAuthorizationOperation operation,
        CancellationToken ct)
        => (await this.authorizer.AuthorizeAsync(peer, new AgentSessionAuthorizationRequest
        {
            AgentSessionId = request.AgentSessionId,
            ExpectedOwningProfileEntityId = request.ExpectedOwningProfileEntityId,
            ExpectedOwnershipGeneration = request.ExpectedOwnershipGeneration,
            Operation = operation,
        }, ct).ConfigureAwait(false)).IsAllowed;

    private async Task TakeOverCoreAsync(
        TransportPeerIdentity peer, AgentSessionTakeoverRequest request, CancellationToken ct)
    {
        var decision = await this.authorizer.AuthorizeAsync(peer, new AgentSessionAuthorizationRequest
        {
            AgentSessionId = request.AgentSessionId,
            ExpectedOwningProfileEntityId = request.ExpectedOwningProfileEntityId,
            ExpectedOwnershipGeneration = request.ExpectedOwnershipGeneration,
            Operation = AgentSessionAuthorizationOperation.Takeover,
            NewOwningProfileEntityId = request.NewOwningProfileEntityId,
        }, ct).ConfigureAwait(false);
        if (!decision.IsAllowed) throw new AgentSessionUnavailableException();

        var existing = await this.runtimeRegistry.TryGetAsync(
            request.AgentSessionId, request.ExpectedOwnershipGeneration, ct).ConfigureAwait(false);
        if (existing is not null
            && !await this.runtimeRegistry.TryTerminateAsync(new TerminateAgentSessionRuntimeRequest
            {
                SessionId = request.AgentSessionId,
                OwnershipGeneration = request.ExpectedOwnershipGeneration,
                Epoch = existing.Epoch,
            }, ct).ConfigureAwait(false))
            throw new AgentSessionTakeoverBlockedException();
        if (!await this.runtimeFactory.TryTakeOverAsync(request, ct).ConfigureAwait(false))
            throw new AgentSessionTakeoverBlockedException();
    }

    private async ValueTask HandleCommandAsync(
        TransportPeerIdentity peer,
        AgentSessionOpenRequest open,
        RemoteAgentSessionLease runtime,
        RemoteAgentAttachmentLease attachment,
        AgentSessionCommand command,
        CancellationToken ct)
    {
        try
        {
            if (command.RuntimeEpoch != runtime.Epoch)
                throw new AgentSessionUnavailableException();
            var operation = command switch
            {
            SetToolEnabledCommand => AgentSessionAuthorizationOperation.SetToolState,
            SetContinueInBackgroundCommand => AgentSessionAuthorizationOperation.SetBackgroundPreference,
            InterruptCommand => AgentSessionAuthorizationOperation.Interrupt,
            TerminateSessionCommand => AgentSessionAuthorizationOperation.Terminate,
            OpenSubagentCommand => AgentSessionAuthorizationOperation.OpenSubagent,
            ModalResponseCommand => AgentSessionAuthorizationOperation.ModalResponse,
            _ => AgentSessionAuthorizationOperation.Send,
            };
            var authorized = await this.authorizer.AuthorizeAsync(peer, new AgentSessionAuthorizationRequest
            {
            AgentSessionId = open.AgentSessionId,
            ExpectedOwningProfileEntityId = open.ExpectedOwningProfileEntityId,
            ExpectedOwnershipGeneration = open.ExpectedOwnershipGeneration,
            Operation = operation,
            ChildAgentId = command is OpenSubagentCommand child ? child.AgentId : null,
            }, ct).ConfigureAwait(false);
            if (!authorized.IsAllowed) throw new AgentSessionUnavailableException();

            if (command is TerminateSessionCommand)
            {
                await runtime.TryTerminateAsync(ct).ConfigureAwait(false);
                return;
            }
            var result = await runtime.ExecuteCommandOnceAsync(
                command, token => this.ExecuteCommandAsync(runtime, command, token), ct).ConfigureAwait(false);
            await attachment.PublishAsync(result, ct).ConfigureAwait(false);
            if (command is DetachCommand)
                await attachment.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            await attachment.PublishAsync(new OperationErrorEvent
            {
                Error = new RemoteAgentOperationError
                {
                    Code = "operation-failed",
                    Operation = command.Type,
                    IsRetryable = false,
                    Message = "The operation could not be completed.",
                    CorrelationId = command.CorrelationId,
                },
            }, ct).ConfigureAwait(false);
        }
    }

    private async Task<AgentSessionServerEvent> ExecuteCommandAsync(
        RemoteAgentSessionLease runtime, AgentSessionCommand command, CancellationToken ct)
    {
        object? result = null;
        switch (command)
        {
            case CreateQueueCommand value:
                result = await runtime.Chat.InputQueues.CreateQueueAsync(new CreateAgentInputQueueRequest
                {
                    CommandId = value.CommandId, ExpectedRevision = value.ExpectedRevision, Configuration = value.Configuration,
                }, ct).ConfigureAwait(false);
                break;
            case DeleteQueueCommand value:
                result = await runtime.Chat.InputQueues.DeleteQueueAsync(new DeleteAgentInputQueueRequest
                {
                    CommandId = value.CommandId, ExpectedRevision = value.ExpectedRevision, QueueId = value.QueueId,
                }, ct).ConfigureAwait(false);
                break;
            case EnqueueInputCommand value:
                result = await runtime.Chat.InputQueues.EnqueueAsync(new EnqueueAgentInputRequest
                {
                    CommandId = value.CommandId, ExpectedRevision = value.ExpectedRevision,
                    TargetQueueId = value.TargetQueueId, Messages = DeserializeMessages(value.Messages),
                }, ct).ConfigureAwait(false);
                break;
            case EditQueueItemCommand value:
                result = await runtime.Chat.InputQueues.EditAsync(new EditAgentInputQueueItemRequest
                {
                    CommandId = value.CommandId, ExpectedRevision = value.ExpectedRevision,
                    QueueId = value.QueueId, ItemId = value.ItemId, Messages = DeserializeMessages(value.Messages),
                }, ct).ConfigureAwait(false);
                break;
            case RemoveQueueItemCommand value:
                result = await runtime.Chat.InputQueues.RemoveAsync(new RemoveAgentInputQueueItemRequest
                {
                    CommandId = value.CommandId, ExpectedRevision = value.ExpectedRevision,
                    QueueId = value.QueueId, ItemId = value.ItemId,
                }, ct).ConfigureAwait(false);
                break;
            case MoveQueueItemCommand value:
                result = await runtime.Chat.InputQueues.MoveAsync(new MoveAgentInputQueueItemRequest
                {
                    CommandId = value.CommandId, ExpectedRevision = value.ExpectedRevision,
                    SourceQueueId = value.SourceQueueId, ItemId = value.ItemId,
                    TargetQueueId = value.TargetQueueId, BeforeItemId = value.BeforeItemId,
                }, ct).ConfigureAwait(false);
                break;
            case ConfigureQueueCommand value:
                result = await runtime.Chat.InputQueues.ConfigureAsync(new ConfigureAgentInputQueueRequest
                {
                    CommandId = value.CommandId, ExpectedRevision = value.ExpectedRevision,
                    QueueId = value.QueueId, Configuration = value.Configuration,
                }, ct).ConfigureAwait(false);
                break;
            case SetToolEnabledCommand value:
                await runtime.Chat.SetToolEnabledAsync(value.ToolId, value.Enabled, ct).ConfigureAwait(false);
                break;
            case ModalResponseCommand value:
                await runtime.Chat.RespondToModalAsync(value.ModalId, value.Response, ct).ConfigureAwait(false);
                break;
            case SetContinueInBackgroundCommand value:
                await runtime.SetContinueInBackgroundAsync(value.ContinueInBackground, ct).ConfigureAwait(false);
                break;
            case InterruptCommand:
                runtime.Chat.Interrupt();
                break;
            case OpenSubagentCommand:
            case TerminateSessionCommand:
            case DetachCommand:
                break;
            default:
                throw new InvalidOperationException("Unsupported agent session command.");
        }
        return new CommandCompletedEvent
        {
            CommandId = command.CommandId,
            Result = result is null ? null : JsonSerializer.SerializeToElement(result),
        };
    }

    private static IReadOnlyList<ChatMessage> DeserializeMessages(JsonElement messages)
        => JsonSerializer.Deserialize<ChatMessage[]>(messages) ?? throw new InvalidOperationException("Messages are required.");
}

internal sealed class ContextOnlyRuntimeHostFactory : IAgentSessionRuntimeHostFactory
{
    private readonly IAgentSessionRuntimeContextFactory contextFactory;

    internal ContextOnlyRuntimeHostFactory(IAgentSessionRuntimeContextFactory contextFactory)
        => this.contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    public ValueTask<PersistedAgentSessionRuntimeIntent?> LoadIntentAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PersistedAgentSessionRuntimeIntent?>(null);
    }

    public Task<RemoteAgentSessionLease> StartAsync(PersistedAgentSessionRuntimeIntent intent, CancellationToken ct)
        => throw new InvalidOperationException("A persisted session entity is required to create the runtime context.");

    public ValueTask<bool> TryTakeOverAsync(AgentSessionTakeoverRequest request, CancellationToken ct)
        => ValueTask.FromResult(false);
}

internal sealed class AgentSessionUnavailableException : Exception;
internal sealed class AgentSessionTakeoverBlockedException : Exception;
