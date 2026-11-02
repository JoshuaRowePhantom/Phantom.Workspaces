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

internal interface IAgentSessionRestartRuntimeHostFactory
{
    Task<AgentSessionOwnerRestartResult> RestartAsync(
        AgentSessionOwnerRestartRequest request,
        IRemoteAgentSessionRuntimeRegistry registry,
        CancellationToken cancellationToken);
    Task<AgentSessionOwnerRestartResult?> GetRestartStatusAsync(
        AgentSessionOwnerRestartRequest request,
        CancellationToken cancellationToken);
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
            if (this.runtimeFactory is DataAccessAgentSessionRuntimeHostFactory restartFactory)
                await restartFactory.RecoverPendingRestartAsync(
                    open.AgentSessionId, this.runtimeRegistry, ct).ConfigureAwait(false);
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
            initial = await runtime.AttachAndCaptureInitialStateAsync(new AttachRemoteAgentSessionRequest
            {
                AttachmentToken = open.AttachmentToken,
                Channel = request.Channel,
                Cursor = open.ReplayCursor,
            }, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (runtime.IsFenced)
        {
            throw new AgentSessionUnavailableException();
        }
        var attachment = initial.Attachment;
        try
        {
            if (open.ReplayCursor is { } previous && previous.Epoch != runtime.Epoch)
            {
                if (this.runtimeFactory is not DataAccessAgentSessionRuntimeHostFactory restartFactory
                    || !await restartFactory.CanReconcileEpochAsync(
                        open.AgentSessionId, previous.Epoch, runtime.Epoch, ct).ConfigureAwait(false))
                    throw new AgentSessionUnavailableException();
                var handoff = AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
                    previous.Epoch, previous.Sequence + 1, Guid.NewGuid(),
                    new SessionRestartingEvent { ReplacementRuntimeEpoch = runtime.Epoch });
                await request.Channel.Writer.WriteAsync(
                    AgentSessionProtocolCodec.SerializeFrame(handoff), ct).ConfigureAwait(false);
            }
            foreach (var frame in initial.Frames)
            {
                await request.Channel.Writer.WriteAsync(
                    AgentSessionProtocolCodec.SerializeFrame(frame), ct).ConfigureAwait(false);
            }
            if (!await attachment.ActivateAsync(ct).ConfigureAwait(false))
                throw new AgentSessionUnavailableException();
            attachment.StartReceiving((command, token) =>
                this.DispatchCommandAsync(request.Peer, open, runtime, attachment, command, token));
            return attachment;
        }
        catch
        {
            await attachment.AbortOpenAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal ValueTask DispatchCommandAsync(
        TransportPeerIdentity peer,
        AgentSessionOpenRequest open,
        RemoteAgentSessionLease runtime,
        RemoteAgentAttachmentLease attachment,
        AgentSessionCommand command,
        CancellationToken ct = default)
        => this.HandleCommandAsync(peer, open, runtime, attachment, command, ct);

    internal Task TakeOverAsync(
        TransportPeerIdentity peer, AgentSessionTakeoverRequest request, CancellationToken ct = default)
        => this.TakeOverCoreAsync(peer, request, ct);

    internal async Task<AgentSessionOwnerRestartResult> RestartAsync(
        TransportPeerIdentity peer,
        AgentSessionOwnerRestartRequest request,
        CancellationToken cancellationToken)
    {
        var expected = request.ExpectedRuntime;
        var gate = this.openGates.GetOrAdd(expected.AgentSessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var restartFactory = await this.AuthorizeRestartAsync(peer, expected, cancellationToken).ConfigureAwait(false);
            return await restartFactory.RestartAsync(
                request, this.runtimeRegistry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

        internal async Task<AgentSessionOwnerRestartResult?> GetRestartStatusAsync(
            TransportPeerIdentity peer,
            AgentSessionOwnerRestartRequest request,
            CancellationToken cancellationToken)
        {
            using var transition = await this.EnterTransitionAsync(
                request.ExpectedRuntime.AgentSessionId, cancellationToken).ConfigureAwait(false);
            var factory = await this.AuthorizeRestartAsync(
                peer, request.ExpectedRuntime, cancellationToken).ConfigureAwait(false);
            return await factory.GetRestartStatusAsync(request, cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask<IAgentSessionRestartRuntimeHostFactory> AuthorizeRestartAsync(
            TransportPeerIdentity peer,
            AgentSessionRuntimeIdentity expected,
            CancellationToken cancellationToken)
        {
            var authorization = await this.authorizer.AuthorizeAsync(peer, new AgentSessionAuthorizationRequest
            {
                AgentSessionId = expected.AgentSessionId,
                ExpectedOwningProfileEntityId = expected.OwningProfileEntityId,
                ExpectedOwnershipGeneration = expected.OwnershipGeneration,
                Operation = AgentSessionAuthorizationOperation.Restart,
            }, cancellationToken).ConfigureAwait(false);
            if (!authorization.IsAllowed
                || this.runtimeFactory is not IAgentSessionRestartRuntimeHostFactory factory)
                throw new AgentSessionUnavailableException();
            return factory;
        }
        internal AgentSessionRuntimeIdentity? GetIdentity(Phantom.Workspaces.Llm.IAgentChat chat)
            => (this.runtimeFactory as DataAccessAgentSessionRuntimeHostFactory)?.GetIdentity(chat);

        internal void AddLocalViewer(IAgentChat chat)
            => (this.runtimeFactory as DataAccessAgentSessionRuntimeHostFactory)?.FindRuntime(chat)?.AddLocalViewer();

        internal ValueTask ReleaseLocalViewerAsync(IAgentChat chat)
            => (this.runtimeFactory as DataAccessAgentSessionRuntimeHostFactory)?.FindRuntime(chat)?.ReleaseLocalViewerAsync()
                ?? ValueTask.CompletedTask;

        internal async Task<bool> TerminateLocalRuntimeAsync(
            TransportPeerIdentity peer,
            AgentSessionRuntimeIdentity identity,
            CancellationToken cancellationToken)
        {
            using var transition = await this.EnterTransitionAsync(identity.AgentSessionId, cancellationToken).ConfigureAwait(false);
            var authorization = await this.authorizer.AuthorizeAsync(peer, new AgentSessionAuthorizationRequest
            {
                AgentSessionId = identity.AgentSessionId,
                ExpectedOwningProfileEntityId = identity.OwningProfileEntityId,
                ExpectedOwnershipGeneration = identity.OwnershipGeneration,
                Operation = AgentSessionAuthorizationOperation.Terminate,
            }, cancellationToken).ConfigureAwait(false);
            if (!authorization.IsAllowed)
                throw new AgentSessionUnavailableException();
            return await this.runtimeRegistry.TryTerminateAsync(new TerminateAgentSessionRuntimeRequest
            {
                SessionId = identity.AgentSessionId,
                OwnershipGeneration = identity.OwnershipGeneration,
                Epoch = identity.RuntimeEpoch,
            }, CancellationToken.None).ConfigureAwait(false);
        }

        internal async Task SetLocalRetentionAsync(
            TransportPeerIdentity peer,
            AgentSessionRuntimeIdentity identity,
            bool continueInBackground,
            CancellationToken cancellationToken)
        {
            using var transition = await this.EnterTransitionAsync(identity.AgentSessionId, cancellationToken).ConfigureAwait(false);
            var authorization = await this.authorizer.AuthorizeAsync(peer, new AgentSessionAuthorizationRequest
            {
                AgentSessionId = identity.AgentSessionId,
                ExpectedOwningProfileEntityId = identity.OwningProfileEntityId,
                ExpectedOwnershipGeneration = identity.OwnershipGeneration,
                Operation = AgentSessionAuthorizationOperation.SetBackgroundPreference,
            }, cancellationToken).ConfigureAwait(false);
            if (!authorization.IsAllowed)
                throw new AgentSessionUnavailableException();
            await this.runtimeRegistry.SetContinueInBackgroundAsync(new UpdateAgentSessionRuntimeRetentionRequest
            {
                SessionId = identity.AgentSessionId,
                OwnershipGeneration = identity.OwnershipGeneration,
                Epoch = identity.RuntimeEpoch,
                ContinueInBackground = continueInBackground,
            }, cancellationToken).ConfigureAwait(false);
        }

        internal async Task<RunningAgentChatLease> EnsureLocalRuntimeAsync(
            TransportPeerIdentity peer,
            AcquireAgentChatRequest request,
            Func<Task<RunningAgentChatLease>> acquireViewer,
            CancellationToken cancellationToken)
        {
            var factory = this.runtimeFactory as DataAccessAgentSessionRuntimeHostFactory
                ?? throw new AgentSessionUnavailableException();
            var entity = request.AgentSessionEntity!.Value;
            var expectedOwner = entity.TryGetProperty("owning-profile-entity-id", out var owner)
                ? owner.GetString()! : entity.GetProperty("host-profile-entity-id").GetString()!;
            var generation = entity.GetProperty("ownership-generation").GetInt64();
            var gate = this.openGates.GetOrAdd(request.AgentSessionId.Value, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var authorized = await this.authorizer.AuthorizeAsync(peer, new AgentSessionAuthorizationRequest
                {
                    AgentSessionId = request.AgentSessionId.Value,
                    ExpectedOwningProfileEntityId = expectedOwner,
                    ExpectedOwnershipGeneration = generation,
                    Operation = AgentSessionAuthorizationOperation.Open,
                }, cancellationToken).ConfigureAwait(false);
                if (!authorized.IsAllowed)
                    throw new AgentSessionUnavailableException();
                var intent = await factory.LoadIntentAsync(
                    request.AgentSessionId.Value, cancellationToken).ConfigureAwait(false)
                    ?? throw new AgentSessionUnavailableException();
                await factory.RecoverPendingRestartAsync(
                    request.AgentSessionId.Value, this.runtimeRegistry, cancellationToken).ConfigureAwait(false);
                await this.runtimeRegistry.GetOrStartAsync(
                    intent,
                    token => factory.StartLocalAsync(intent, request, token),
                    cancellationToken).ConfigureAwait(false);
                return await acquireViewer().ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
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
        var openGate = this.openGates.GetOrAdd(
            request.AgentSessionId, static _ => new SemaphoreSlim(1, 1));
        await openGate.WaitAsync(ct).ConfigureAwait(false);
        try
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
            var replacement = await this.runtimeFactory.LoadIntentAsync(
                request.AgentSessionId, ct).ConfigureAwait(false);
            if (replacement is null
                || replacement.OwnershipGeneration != request.ExpectedOwnershipGeneration + 1
                || !string.Equals(
                    replacement.OwningProfileEntityId,
                    request.NewOwningProfileEntityId,
                    StringComparison.OrdinalIgnoreCase))
                throw new AgentSessionTakeoverBlockedException();
            await this.runtimeRegistry.GetOrStartAsync(
                replacement,
                token => this.runtimeFactory.StartAsync(replacement, token),
                ct).ConfigureAwait(false);
        }
        finally
        {
            openGate.Release();
        }
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
            using var transition = await this.EnterTransitionAsync(open.AgentSessionId, ct).ConfigureAwait(false);
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
            if (command.RuntimeEpoch != runtime.Epoch)
                throw new AgentSessionUnavailableException();
            attachment.EnsureConnected();

            if (command is RestartViewerReadyCommand)
            {
                runtime.AcknowledgeRestartViewer(attachment.AttachmentToken);
                await attachment.PublishAsync(new CommandCompletedEvent
                {
                    CommandId = command.CommandId,
                    Result = JsonSerializer.SerializeToElement(true),
                }, command.CorrelationId, ct).ConfigureAwait(false);
                return;
            }

            if (command is TerminateSessionCommand)
            {
                await runtime.TryTerminateAsync(ct).ConfigureAwait(false);
                return;
            }

            if (command is DetachCommand)
            {
                await attachment.ReleaseExplicitlyAsync().ConfigureAwait(false);
                return;
            }
            if (command is DetachIfIdleCommand)
            {
                if (!await attachment.TryReleaseIdleAsync(command).ConfigureAwait(false))
                    await attachment.PublishAsync(new CommandCompletedEvent
                    {
                        CommandId = command.CommandId,
                        Result = JsonSerializer.SerializeToElement(false),
                    }, command.CorrelationId, ct).ConfigureAwait(false);
                return;
            }
            if (command is OpenSubagentCommand childCommand)
                await this.AuthorizeSubagentAsync(peer, runtime, childCommand, ct).ConfigureAwait(false);
            var result = await runtime.ExecuteCommandOnceAsync(
                command, token => this.ExecuteCommandAsync(runtime, command, token), ct).ConfigureAwait(false);
            await attachment.PublishAsync(result, command.CorrelationId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            await attachment.PublishAsync(new OperationErrorEvent
            {
                Error = new RemoteAgentOperationError
                {
                    Code = error is AgentSessionUnavailableException ? "unauthorized" : "internal-error",
                    Operation = command.Type,
                    IsRetryable = false,
                    Message = "The operation could not be completed.",
                    CorrelationId = command.CorrelationId,
                },
            }, command.CorrelationId, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask<IDisposable> EnterTransitionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var semaphore = this.openGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new OwnerTransition(semaphore);
    }

    private sealed class OwnerTransition(SemaphoreSlim semaphore) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 0)
                semaphore.Release();
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
                await runtime.Chat.InterruptAsync(ct).ConfigureAwait(false);
                break;
            case OpenSubagentCommand value:
                result = await this.OpenSubagentAsync(runtime, value, ct).ConfigureAwait(false);
                break;
            case TerminateSessionCommand:
            case DetachCommand:
                break;
            default:
                throw new InvalidOperationException("Unsupported agent session command.");
        }
        if (result is AgentInputQueueCommandResult queueResult
            && queueResult.Status is AgentInputQueueCommandStatus.Conflict or AgentInputQueueCommandStatus.Rejected)
        {
            return new OperationErrorEvent
            {
                Error = new RemoteAgentOperationError
                {
                    Code = queueResult.Status == AgentInputQueueCommandStatus.Conflict ? "conflict" : "rejected",
                    Operation = command.Type,
                    IsRetryable = queueResult.Status == AgentInputQueueCommandStatus.Conflict,
                    Message = queueResult.Status == AgentInputQueueCommandStatus.Conflict
                        ? "The queue changed before this command was applied."
                        : "The queue command was rejected.",
                    CorrelationId = command.CorrelationId,
                },
            };
        }
        return new CommandCompletedEvent
        {
            CommandId = command.CommandId,
            Result = result is null
                ? null
                : JsonSerializer.SerializeToElement(result, AgentSessionProtocolCodec.Options),
        };
    }

    private async Task<PersistedAgentSessionRuntimeIntent> ResolveChildIntentAsync(
        RemoteAgentSessionLease parent,
        OpenSubagentCommand command,
        CancellationToken ct)
    {
        var child = parent.Chat.SubAgents.FirstOrDefault(
            value => string.Equals(value.AgentId, command.AgentId, StringComparison.Ordinal))
            ?? throw new AgentSessionUnavailableException();
        var childSessionId = child switch
        {
            SubAgent subagent => subagent.SessionId.Value,
            AgentChat chat => chat.AgentSessionId,
            _ => throw new AgentSessionUnavailableException(),
        };
        var intent = await this.runtimeFactory.LoadIntentAsync(childSessionId, ct).ConfigureAwait(false)
            ?? throw new AgentSessionUnavailableException();
        if (intent.AgentSessionId != childSessionId)
            throw new AgentSessionUnavailableException();
        return intent;
    }

    private async Task AuthorizeSubagentAsync(
        TransportPeerIdentity peer, RemoteAgentSessionLease parent,
        OpenSubagentCommand command, CancellationToken ct)
    {
        var intent = await this.ResolveChildIntentAsync(parent, command, ct).ConfigureAwait(false);
        if (!await this.AuthorizeAsync(peer, new AgentSessionOpenRequest
        {
            ProtocolVersion = 1,
            AgentSessionId = intent.AgentSessionId,
            ExpectedOwningProfileEntityId = intent.OwningProfileEntityId,
            ExpectedOwnershipGeneration = intent.OwnershipGeneration,
            OpenIntent = AgentSessionOpenIntent.Attach,
            AttachmentToken = Guid.NewGuid().ToString("N"),
            Capabilities = [],
        }, AgentSessionAuthorizationOperation.Open, ct).ConfigureAwait(false))
            throw new AgentSessionUnavailableException();
    }

    private async Task<RemoteSubagentDescriptor> OpenSubagentAsync(
        RemoteAgentSessionLease parent, OpenSubagentCommand command, CancellationToken ct)
    {
        var intent = await this.ResolveChildIntentAsync(parent, command, ct).ConfigureAwait(false);
        var runtime = await this.runtimeRegistry.TryGetAsync(
            intent.AgentSessionId, intent.OwnershipGeneration, ct).ConfigureAwait(false)
            ?? await this.runtimeRegistry.GetOrStartAsync(
                intent,
                token => this.runtimeFactory.StartAsync(intent, token),
                ct).ConfigureAwait(false);
        if (!parent.TryKeepChildAlive(runtime))
            throw new AgentSessionUnavailableException();

        return new RemoteSubagentDescriptor
        {
            AgentSessionId = intent.AgentSessionId,
            AgentId = command.AgentId,
            OwningProfileEntityId = intent.OwningProfileEntityId,
            OwnershipGeneration = intent.OwnershipGeneration,
            RuntimeEpoch = runtime.Epoch,
        };
    }

    private static IReadOnlyList<ChatMessage> DeserializeMessages(JsonElement messages)
        => JsonSerializer.Deserialize<ChatMessage[]>(
            messages,
            Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions)
            ?? throw new InvalidOperationException("Messages are required.");
}

internal sealed class AgentSessionUnavailableException : Exception;
internal sealed class AgentSessionTakeoverBlockedException : Exception;
