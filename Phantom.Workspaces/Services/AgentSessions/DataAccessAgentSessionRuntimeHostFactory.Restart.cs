using System.Collections.Concurrent;
using System.Text.Json;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Remote;

namespace Phantom.Workspaces.Services.AgentSessions;

internal sealed partial class DataAccessAgentSessionRuntimeHostFactory
{
    private readonly ConcurrentDictionary<string, HostedRuntime> hostedRuntimes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<AgentSessionRuntimeIdentity, RemoteAgentSessionLease> retiredRuntimeSources = new();

    private sealed record HostedRuntime(
        RemoteAgentSessionLease Runtime,
        PersistedRuntimeState State,
        RunningAgentChatLease ChatLease,
        PersistedAgentSessionRuntimeIntent Intent);

    internal sealed record RestartOperation
    {
        public required AgentSessionOwnerRestartRequest Request { get; init; }
        public required AgentSessionRuntimeIdentity Replacement { get; init; }
        public required AgentRuntimeQueueCheckpoint Checkpoint { get; init; }
        public required string[] ViewerAttachmentTokens { get; init; }
        public RuntimeEpoch[] PreviousReplacementEpochs { get; init; } = [];
        public required AgentSessionOwnerRestartState State { get; init; }
        public string? Message { get; init; }

        internal AgentSessionOwnerRestartResult ToResult() => new()
        {
            CommandId = this.Request.CommandId,
            State = this.State,
            ReplacementRuntime = this.Replacement,
            Message = this.Message,
        };
    }

    internal AgentSessionRuntimeIdentity? GetIdentity(IAgentChat chat)
        => this.hostedRuntimes.Values.FirstOrDefault(hosted =>
            ReferenceEquals(hosted.Runtime.Chat, chat)
            && !hosted.Runtime.IsFenced
            && hosted.Runtime.CanRestartIndependently) is { } hosted
            ? new AgentSessionRuntimeIdentity
            {
                AgentSessionId = hosted.Intent.AgentSessionId,
                OwningProfileEntityId = hosted.Intent.OwningProfileEntityId,
                OwnershipGeneration = hosted.Intent.OwnershipGeneration,
                RuntimeEpoch = hosted.Runtime.Epoch,
            }
            : null;

    internal RemoteAgentSessionLease? FindRuntime(IAgentChat chat)
        => this.hostedRuntimes.Values.FirstOrDefault(hosted => ReferenceEquals(hosted.Runtime.Chat, chat))?.Runtime;

    public async Task<AgentSessionOwnerRestartResult?> GetRestartStatusAsync(
        AgentSessionOwnerRestartRequest request,
        CancellationToken cancellationToken)
    {
        var query = await this.FindAsync(request.ExpectedRuntime.AgentSessionId, cancellationToken).ConfigureAwait(false);
        if (query.Entity is null)
            return null;
        var state = new PersistedRuntimeState(
            this.dataAccessLayer, query.Entity, query.GetRequiredAuthoritativeTime());
        var operations = await state.LoadRestartsAsync(cancellationToken).ConfigureAwait(false);
        if (!operations.TryGetValue(request.CommandId.ToString("D"), out var operation))
            return null;
        if (operation.Request != request)
            throw new InvalidOperationException("The restart command id was reused with a different payload.");
        return operation.ToResult();
    }

    public async Task<AgentSessionOwnerRestartResult> RestartAsync(
        AgentSessionOwnerRestartRequest request,
        IRemoteAgentSessionRuntimeRegistry registry,
        CancellationToken cancellationToken)
    {
        if (request.CommandId == Guid.Empty)
            throw new ArgumentException("Restart command id is required.", nameof(request));
        var expected = request.ExpectedRuntime;
        if (this.runningChats is not RunningAgentChatTable table)
            throw new InvalidOperationException("The owning runtime table cannot replace this session.");
        var query = await this.FindAsync(expected.AgentSessionId, cancellationToken).ConfigureAwait(false);
        if (query.Entity?.Data is not JsonElement persisted
            || ReadLong(persisted, "ownership-generation") != expected.OwnershipGeneration
            || !string.Equals(
                ReadString(persisted, "owning-profile-entity-id") ?? ReadString(persisted, "host-profile-entity-id"),
                expected.OwningProfileEntityId, StringComparison.OrdinalIgnoreCase))
            return Changed(request);

        this.hostedRuntimes.TryGetValue(expected.AgentSessionId, out var hosted);
        var state = hosted?.State ?? new PersistedRuntimeState(
            this.dataAccessLayer, query.Entity, query.GetRequiredAuthoritativeTime());
        query = await state.RefreshAsync(
            () => this.FindAsync(expected.AgentSessionId, cancellationToken), cancellationToken).ConfigureAwait(false);
        persisted = query.Entity!.Data!.Value;
        var operations = await state.LoadRestartsAsync(cancellationToken).ConfigureAwait(false);
        operations.TryGetValue(request.CommandId.ToString("D"), out var operation);
        if (operation is not null && operation.Request != request)
            throw new InvalidOperationException("The restart command id was reused with a different payload.");
        if (operation is null)
        {
            operation = operations.Values.FirstOrDefault(value =>
                value.Request.ExpectedRuntime == expected);
            if (operation is not null)
                operation = operation with { Request = request };
        }
        if (operation?.State is AgentSessionOwnerRestartState.Succeeded or AgentSessionOwnerRestartState.Blocked)
        {
            await state.SaveRestartAsync(operation, CancellationToken.None).ConfigureAwait(false);
            if (operation.State == AgentSessionOwnerRestartState.Succeeded
                && hosted?.Runtime.Epoch == operation.Replacement.RuntimeEpoch
                && hosted.Runtime.Chat is AgentChat completedChat)
                hosted.Runtime.ResumeAfterRestartViewersReady(
                    operation.ViewerAttachmentTokens, completedChat.InputQueueManager.ResumeAfterRuntimeHandoff);
            return operation.ToResult();
        }
        if (operation?.State == AgentSessionOwnerRestartState.Committed)
        {
            operation = operation with
            {
                State = AgentSessionOwnerRestartState.Blocked,
                Message = "Old-runtime shutdown was not durably confirmed. A replacement has not been started.",
            };
            await state.SaveRestartAsync(operation, CancellationToken.None).ConfigureAwait(false);
            return operation.ToResult();
        }
        if (operation is not null && hosted?.Runtime.Epoch == operation.Replacement.RuntimeEpoch)
        {
            if (this.retiredRuntimeSources.TryGetValue(expected, out var originalRuntime))
            {
                if (!originalRuntime.HasTerminated)
                    await originalRuntime.RetireForRestartAsync(operation.Replacement.RuntimeEpoch).ConfigureAwait(false);
                await table.ReattachLocalViewersAsync(
                    originalRuntime.Chat, operation.Replacement).ConfigureAwait(false);
            }
            operation = operation with { State = AgentSessionOwnerRestartState.Succeeded, Message = null };
            await state.SaveRestartAsync(operation, CancellationToken.None, markRunning: true).ConfigureAwait(false);
            if (hosted.Runtime.Chat is AgentChat alreadyReplaced)
                hosted.Runtime.ResumeAfterRestartViewersReady(
                    operation.ViewerAttachmentTokens,
                    alreadyReplaced.InputQueueManager.ResumeAfterRuntimeHandoff);
            this.retiredRuntimeSources.TryRemove(expected, out _);
            return operation.ToResult();
        }

        var oldRuntime = hosted?.Runtime;
        var remoteViewers = oldRuntime?.CaptureRemoteViewerTokens() ?? [];
        var replacementIdentity = operation?.Replacement ?? expected with
        {
            RuntimeEpoch = new RuntimeEpoch { Value = Guid.NewGuid() },
        };
        if (operation is null
            && (oldRuntime is null || oldRuntime.IsFenced || !oldRuntime.CanRestartIndependently
                || oldRuntime.Epoch != expected.RuntimeEpoch
                || !TryReadRuntimeEpoch(persisted, out var persistedEpoch)
                || persistedEpoch != expected.RuntimeEpoch.Value))
            return Changed(request);

        RunningAgentChatLease? replacementLease = null;
        RemoteAgentSessionLease? replacement = null;
        AgentSessionOwnershipLease? recoveredOwnership = null;
        var shutdownConfirmed = operation?.State == AgentSessionOwnerRestartState.Stopped;
        IDisposable? runtimeTransition = null;
        try
        {
            if (hosted is null)
            {
                if (operation is null || !shutdownConfirmed)
                    return Changed(request);
                if (TryReadLeaseExpiry(persisted, out var expiry) && expiry > query.GetRequiredAuthoritativeTime())
                    return operation.ToResult() with
                    {
                        Message = "The prior owner lease has not expired. Retry after owner recovery releases its authority.",
                    };
                // A recovered host is a new runtime, even if a previous startup never acknowledged.
                replacementIdentity = replacementIdentity with
                {
                    RuntimeEpoch = new RuntimeEpoch { Value = Guid.NewGuid() },
                };
                operation = operation with
                {
                    Replacement = replacementIdentity,
                    PreviousReplacementEpochs = operation.PreviousReplacementEpochs
                        .Append(operation.Replacement.RuntimeEpoch).ToArray(),
                };
                await state.SaveRestartAsync(operation, cancellationToken).ConfigureAwait(false);
                await state.RotateForRestartAsync(replacementIdentity, cancellationToken).ConfigureAwait(false);
                recoveredOwnership = this.CreateOwnershipLease(state, () => replacement);
                recoveredOwnership.Start(state.LeasePeriod);
                var context = this.contextFactory.Create(persisted);
                replacementLease = await table.AcquireAsync(new AcquireAgentChatRequest
                {
                    OwnerRuntimeAcquisition = true,
                    RuntimeQueueCheckpoint = operation.Checkpoint,
                    AgentSessionId = new AgentSessionId(expected.AgentSessionId),
                    AgentSessionEntity = persisted,
                    AgentServices = (this.hostServices ?? new AgentServices()) with
                    {
                        CurrentSessionContext = Context(
                            replacementIdentity, this.hostServices?.CurrentSessionContext as CurrentSessionContext),
                        ExecutorBindings = context.Intent.ExecutorBindings,
                        ExecutorTransportFactoryRegistry = context.TransportFactoryRegistry,
                        RemoteAgentSessionRuntimeIntent = context.Intent,
                    },
                }, state.OwnerLifetime.Token).ConfigureAwait(false);
            }
            else
            {
                runtimeTransition = await hosted.Runtime.EnterRestartTransitionAsync(cancellationToken).ConfigureAwait(false);
                var original = hosted.Runtime.Chat as AgentChat
                    ?? throw new InvalidOperationException("The owning runtime is not independently replaceable.");
                var services = (original.RuntimeServices ?? this.hostServices ?? new AgentServices()) with
                {
                    CurrentSessionContext = (original.RuntimeServices?.CurrentSessionContext as CurrentSessionContext
                        ?? Context(expected)) with
                    {
                        RuntimeEpoch = replacementIdentity.RuntimeEpoch,
                    },
                };
                replacementLease = await table.RestartOwnedRuntimeAsync(
                    request,
                    original,
                    services,
                    async (checkpoint, token) =>
                    {
                        if (operation is not null)
                            return;
                        operation = new RestartOperation
                        {
                            Request = request,
                            Replacement = replacementIdentity,
                            Checkpoint = checkpoint,
                            ViewerAttachmentTokens = remoteViewers,
                            State = AgentSessionOwnerRestartState.Committed,
                        };
                        await state.SaveRestartAsync(operation, token).ConfigureAwait(false);
                    },
                    async _ =>
                    {
                        shutdownConfirmed = true;
                        operation = operation! with { State = AgentSessionOwnerRestartState.Stopped };
                        await state.SaveRestartAsync(operation, CancellationToken.None).ConfigureAwait(false);
                        await state.RotateForRestartAsync(replacementIdentity, CancellationToken.None).ConfigureAwait(false);
                    },
                    cancellationToken,
                    state.OwnerLifetime.Token).ConfigureAwait(false);
            }

            var intent = hosted is null ? this.contextFactory.Create(persisted).Intent
                : hosted.Intent with { ContinueInBackground = hosted.Runtime.ContinueInBackground };
            replacement = this.CreateRuntimeLease(
                intent, state, replacementLease, Context(replacementIdentity,
                    (replacementLease.AgentChat as AgentChat)?.RuntimeServices?.CurrentSessionContext as CurrentSessionContext), publish: false,
                ownership: recoveredOwnership);
            state.OwnerLifetime.Token.ThrowIfCancellationRequested();
            await registry.ReplaceForRestartAsync(oldRuntime, replacement, CancellationToken.None).ConfigureAwait(false);
            if (oldRuntime is not null)
                this.retiredRuntimeSources[expected] = oldRuntime;
            this.hostedRuntimes[expected.AgentSessionId] = new HostedRuntime(
                replacement, state, replacementLease, intent);
            if (oldRuntime is not null)
            {
                oldRuntime.PrepareRetirement(replacementIdentity.RuntimeEpoch);
                runtimeTransition?.Dispose();
                runtimeTransition = null;
                await oldRuntime.RetireForRestartAsync(replacementIdentity.RuntimeEpoch).ConfigureAwait(false);
            }
            if (oldRuntime is not null)
                await table.ReattachLocalViewersAsync(oldRuntime.Chat, replacementIdentity).ConfigureAwait(false);
            operation = operation! with { State = AgentSessionOwnerRestartState.Succeeded, Message = null };
            await state.SaveRestartAsync(operation, CancellationToken.None, markRunning: true).ConfigureAwait(false);
            if (replacementLease.AgentChat is AgentChat newChat)
                replacement.ResumeAfterRestartViewersReady(
                    operation.ViewerAttachmentTokens, newChat.InputQueueManager.ResumeAfterRuntimeHandoff);
            this.retiredRuntimeSources.TryRemove(expected, out _);
            return operation.ToResult();
        }
        catch (OperationCanceledException) when (operation is null && cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            if (operation is null)
                throw;
            await state.RefreshAsync(
                () => this.FindAsync(expected.AgentSessionId, CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
            var durableOperations = await state.LoadRestartsAsync(CancellationToken.None).ConfigureAwait(false);
            if (durableOperations.TryGetValue(request.CommandId.ToString("D"), out var durable)
                && durable.State == AgentSessionOwnerRestartState.Succeeded
                && this.hostedRuntimes.TryGetValue(expected.AgentSessionId, out var acknowledged)
                && acknowledged.Runtime.Epoch == durable.Replacement.RuntimeEpoch)
            {
                if (acknowledged.Runtime.Chat is AgentChat completedChat)
                    acknowledged.Runtime.ResumeAfterRestartViewersReady(
                        durable.ViewerAttachmentTokens, completedChat.InputQueueManager.ResumeAfterRuntimeHandoff);
                this.retiredRuntimeSources.TryRemove(expected, out _);
                return durable.ToResult();
            }
            if (hosted is null && replacementLease is null && recoveredOwnership is not null)
            {
                await recoveredOwnership.DisposeAsync().ConfigureAwait(false);
                await state.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            if (replacementLease is not null
                && (!this.hostedRuntimes.TryGetValue(expected.AgentSessionId, out var published)
                    || !ReferenceEquals(published.Runtime, replacement)))
            {
                if (recoveredOwnership is not null)
                    await recoveredOwnership.DisposeAsync().ConfigureAwait(false);
                await table.TerminateOwnerRuntimeExactAsync(
                    replacementLease.SessionId, replacementLease.AgentChat, CancellationToken.None).ConfigureAwait(false);
                await replacementLease.DisposeAsync().ConfigureAwait(false);
                shutdownConfirmed = false;
            }
            operation = operation with
            {
                State = shutdownConfirmed ? AgentSessionOwnerRestartState.Stopped : AgentSessionOwnerRestartState.Blocked,
                Message = shutdownConfirmed
                    ? "The session is stopped with pending input preserved. Retry this restart operation."
                    : "Old-runtime shutdown or final persistence failed. Replacement is blocked.",
            };
            await state.SaveRestartAsync(operation, CancellationToken.None).ConfigureAwait(false);
            if (this.hostedRuntimes.TryGetValue(expected.AgentSessionId, out var stoppedRuntime))
            {
                if (stoppedRuntime.Runtime.Chat is AgentChat stoppedChat)
                    await stoppedChat.PublishRuntimeRestartDiagnosticAsync(operation.Message!).ConfigureAwait(false);
                await stoppedRuntime.Runtime.PublishAsync(
                    new SessionRestartStoppedEvent { Message = operation.Message! },
                    ct: CancellationToken.None).ConfigureAwait(false);
            }
            return operation.ToResult();
        }
        finally
        {
            runtimeTransition?.Dispose();
        }
    }

    private static CurrentSessionContext Context(
        AgentSessionRuntimeIdentity identity, CurrentSessionContext? template = null)
        => template is not null ? template with
        {
            AgentSessionId = identity.AgentSessionId,
            OwningProfileEntityId = identity.OwningProfileEntityId,
            OwnershipGeneration = identity.OwnershipGeneration,
            RuntimeEpoch = identity.RuntimeEpoch,
        } : new()
    {
        AgentSessionId = identity.AgentSessionId,
        OwningProfileEntityId = identity.OwningProfileEntityId,
        OwnershipGeneration = identity.OwnershipGeneration,
        RuntimeEpoch = identity.RuntimeEpoch,
    };

    internal async Task RecoverPendingRestartAsync(
        string sessionId,
        IRemoteAgentSessionRuntimeRegistry registry,
        CancellationToken cancellationToken)
    {
        var query = await this.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (query.Entity is null)
            throw new AgentSessionUnavailableException();
        var state = this.hostedRuntimes.TryGetValue(sessionId, out var hosted)
            ? hosted.State : new PersistedRuntimeState(
                this.dataAccessLayer, query.Entity, query.GetRequiredAuthoritativeTime());
        var operations = await state.LoadRestartsAsync(cancellationToken).ConfigureAwait(false);
        var pending = operations.Values.LastOrDefault(operation =>
            operation.Request.ExpectedRuntime.OwnershipGeneration == ReadLong(query.Entity.Data!.Value, "ownership-generation")
            && string.Equals(operation.Request.ExpectedRuntime.OwningProfileEntityId,
                ReadString(query.Entity.Data.Value, "owning-profile-entity-id")
                    ?? ReadString(query.Entity.Data.Value, "host-profile-entity-id"),
                StringComparison.OrdinalIgnoreCase)
            && operation.State is AgentSessionOwnerRestartState.Stopped
                or AgentSessionOwnerRestartState.Committed or AgentSessionOwnerRestartState.Blocked);
        if (pending is null)
            return;
        var result = await this.RestartAsync(pending.Request, registry, cancellationToken).ConfigureAwait(false);
        if (result.State != AgentSessionOwnerRestartState.Succeeded)
            throw new InvalidOperationException(result.Message);
    }

    internal async ValueTask<bool> CanReconcileEpochAsync(
        string sessionId,
        RuntimeEpoch previous,
        RuntimeEpoch current,
        CancellationToken cancellationToken)
    {
        var query = await this.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (query.Entity is null)
            return false;
        var state = this.hostedRuntimes.TryGetValue(sessionId, out var hosted)
            ? hosted.State : new PersistedRuntimeState(
                this.dataAccessLayer, query.Entity, query.GetRequiredAuthoritativeTime());
        var operations = await state.LoadRestartsAsync(cancellationToken).ConfigureAwait(false);
        var visited = new HashSet<RuntimeEpoch>();
        while (visited.Add(previous))
        {
            var next = operations.Values.FirstOrDefault(operation =>
                operation.State == AgentSessionOwnerRestartState.Succeeded
                && (operation.Request.ExpectedRuntime.RuntimeEpoch == previous
                    || operation.PreviousReplacementEpochs.Contains(previous)));
            if (next is null)
                return false;
            previous = next.Replacement.RuntimeEpoch;
            if (previous == current)
                return true;
        }
        return false;
    }

    private static AgentSessionOwnerRestartResult Changed(AgentSessionOwnerRestartRequest request) => new()
    {
        CommandId = request.CommandId,
        State = AgentSessionOwnerRestartState.TargetChanged,
        Message = "The owning session runtime changed. Restart was not committed.",
    };
}
