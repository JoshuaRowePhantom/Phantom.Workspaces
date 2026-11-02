using System.Collections.Concurrent;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Remote;

namespace Phantom.Workspaces.Services.AgentSessions;

internal sealed partial class DataAccessAgentSessionRuntimeHostFactory
{
    private readonly ConcurrentDictionary<string, HostedRuntime> hostedRuntimes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<AgentSessionRuntimeIdentity, RemoteAgentSessionLease> retiredRuntimeSources = new();
    private event Action<AgentSessionOwnerRestartRequest, AgentSessionOwnerRestartResult>? RestartStateChanged;
    private readonly ConcurrentDictionary<Task, byte> unfinishedRestartCleanup = new();
    private static readonly ConcurrentDictionary<Task, byte> pendingPersistence = new();
    private readonly ConcurrentDictionary<string, AgentSessionOwnerRestartResult> uncertainRestarts = new(StringComparer.Ordinal);

    private sealed class RestartPersistenceTimeoutException()
        : TimeoutException("The durable restart transition could not be confirmed within the owner deadline.");

    private static void ObservePendingPersistence(Task operation)
    {
        pendingPersistence.TryAdd(operation, 0);
        _ = operation.ContinueWith(completed =>
        {
            _ = completed.Exception;
            pendingPersistence.TryRemove(completed, out _);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task WaitForRestartCleanupAsync(Func<Task> startCleanup)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), this.timeProvider);
        var cleanup = startCleanup();
        this.unfinishedRestartCleanup.TryAdd(cleanup, 0);
        _ = cleanup.ContinueWith(task =>
        {
            _ = task.Exception;
            this.unfinishedRestartCleanup.TryRemove(task, out _);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await cleanup.WaitAsync(deadline.Token).ConfigureAwait(false);
    }

    internal async IAsyncEnumerable<AgentSessionOwnerRestartResult?> ObserveRestartAsync(
        AgentSessionRuntimeIdentity expected, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var changes = Channel.CreateUnbounded<AgentSessionOwnerRestartResult>();
        void Changed(AgentSessionOwnerRestartRequest request, AgentSessionOwnerRestartResult result)
        {
            if (request.ExpectedRuntime == expected)
                changes.Writer.TryWrite(result);
        }
        this.RestartStateChanged += Changed;
        try
        {
            var current = await this.GetRestartStatusAsync(new AgentSessionOwnerRestartRequest
            {
                CommandId = Guid.Empty, ExpectedRuntime = expected,
            }, cancellationToken).ConfigureAwait(false);
            yield return current;
            if (current is null)
                yield break;
            await foreach (var result in changes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                if (result != current)
                {
                    current = result;
                    yield return result;
                }
        }
        finally
        {
            this.RestartStateChanged -= Changed;
            changes.Writer.TryComplete();
        }
    }

    private void PublishRestartState(
        RunningAgentChatTable table, IAgentChat? original,
        AgentSessionOwnerRestartRequest request, AgentSessionOwnerRestartResult result)
    {
        if (original is not null)
            table.PublishRestartState(original, request, result);
        this.RestartStateChanged?.Invoke(request, result);
    }

    internal async Task<bool> IsRestartCleanupUnconfirmedAsync(string sessionId, long generation, CancellationToken ct)
    {
        if (this.uncertainRestarts.ContainsKey(sessionId))
            return true;
        var query = await this.FindAsync(sessionId, ct).ConfigureAwait(false);
        return query.Entity?.Data is JsonElement data
            && data.TryGetProperty("restart-operations", out var journal)
            && JsonSerializer.Deserialize<Dictionary<string, RestartOperation>>(
                journal, Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions)!.Values.Any(operation =>
                    operation.Request.ExpectedRuntime.OwnershipGeneration == generation
                    && this.HasUnconfirmedRestartCleanup(operation));
    }

    private bool HasUnconfirmedRestartCleanup(RestartOperation operation)
        => operation.State is AgentSessionOwnerRestartState.Committed or AgentSessionOwnerRestartState.Blocked
            || operation.StartupUnconfirmed && !(operation.State == AgentSessionOwnerRestartState.Succeeded
                && this.hostedRuntimes.TryGetValue(operation.Replacement.AgentSessionId, out var current)
                && current.Runtime.Epoch == operation.Replacement.RuntimeEpoch
                && !current.Runtime.IsFenced && !current.State.PersistenceUncertain);

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
        public bool StartupUnconfirmed { get; init; }

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
            && !hosted.State.PersistenceUncertain
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
        if (this.uncertainRestarts.TryGetValue(request.ExpectedRuntime.AgentSessionId, out var uncertain))
            return uncertain with { CommandId = request.CommandId };
        var query = await this.FindAsync(request.ExpectedRuntime.AgentSessionId, cancellationToken).ConfigureAwait(false);
        if (query.Entity is null)
            return null;
        var state = new PersistedRuntimeState(
            this.dataAccessLayer, query.Entity, query.GetRequiredAuthoritativeTime(), this.timeProvider);
        var operations = await state.LoadRestartsAsync(cancellationToken).ConfigureAwait(false);
        var operation = request.CommandId == Guid.Empty
            ? operations.Values.LastOrDefault(value => value.Request.ExpectedRuntime == request.ExpectedRuntime)
            : operations.GetValueOrDefault(request.CommandId.ToString("D"));
        if (operation is null)
            return null;
        if (request.CommandId != Guid.Empty && operation.Request != request)
            throw new InvalidOperationException("The restart command id was reused with a different payload.");
        if (operation.State == AgentSessionOwnerRestartState.Succeeded && this.HasUnconfirmedRestartCleanup(operation))
            return operation.ToResult() with
            {
                State = AgentSessionOwnerRestartState.Blocked,
                Message = "The replacement's startup or cleanup has not been confirmed.",
            };
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
        if (this.uncertainRestarts.TryGetValue(expected.AgentSessionId, out var uncertain))
            return uncertain with { CommandId = request.CommandId };
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
            this.dataAccessLayer, query.Entity, query.GetRequiredAuthoritativeTime(), this.timeProvider);
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
            if (operation.State == AgentSessionOwnerRestartState.Succeeded && this.HasUnconfirmedRestartCleanup(operation))
                operation = operation with
                {
                    State = AgentSessionOwnerRestartState.Blocked,
                    Message = "The replacement's startup or cleanup has not been confirmed.",
                };
            await state.SaveRestartAsync(operation, CancellationToken.None).ConfigureAwait(false);
            if (operation.State == AgentSessionOwnerRestartState.Succeeded
                && hosted?.Runtime.Epoch == operation.Replacement.RuntimeEpoch
                && hosted.Runtime.Chat is AgentChat completedChat)
            {
                hosted.Runtime.ResumeAfterRestartViewersReady(
                    operation.ViewerAttachmentTokens, completedChat.InputQueueManager.ResumeAfterRuntimeHandoff);
                await state.ConfirmRestartCompletionAsync(operation).ConfigureAwait(false);
            }
            return operation.ToResult();
        }
        if (operation?.State == AgentSessionOwnerRestartState.Committed || operation?.StartupUnconfirmed == true)
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
                    await this.WaitForRestartCleanupAsync(() =>
                        originalRuntime.RetireForRestartAsync(operation.Replacement.RuntimeEpoch).AsTask()).ConfigureAwait(false);
                await state.RunRestartPhaseAsync(token => table.ReattachLocalViewersAsync(
                    originalRuntime.Chat, operation.Replacement, token)).ConfigureAwait(false);
            }
            operation = operation with
            {
                State = AgentSessionOwnerRestartState.Succeeded, Message = null, StartupUnconfirmed = true,
            };
            await state.SaveRestartAsync(operation, CancellationToken.None, markRunning: true).ConfigureAwait(false);
            if (hosted.Runtime.Chat is AgentChat alreadyReplaced)
                hosted.Runtime.ResumeAfterRestartViewersReady(
                    operation.ViewerAttachmentTokens,
                    alreadyReplaced.InputQueueManager.ResumeAfterRuntimeHandoff);
            this.retiredRuntimeSources.TryRemove(expected, out _);
            this.PublishRestartState(table, hosted.Runtime.Chat, request, operation.ToResult());
            await state.ConfirmRestartCompletionAsync(operation).ConfigureAwait(false);
            return operation.ToResult();
        }

        if (hosted?.Runtime.HasTerminated == true
            && operation?.State == AgentSessionOwnerRestartState.Stopped
            && TryReadLeaseExpiry(persisted, out var stoppedExpiry)
            && stoppedExpiry <= query.GetRequiredAuthoritativeTime())
        {
            state = new PersistedRuntimeState(
                this.dataAccessLayer, query.Entity!, query.GetRequiredAuthoritativeTime(), this.timeProvider);
            hosted = hosted with { State = state };
            this.hostedRuntimes[expected.AgentSessionId] = hosted;
            operation = operation with
            {
                Replacement = operation.Replacement with
                {
                    RuntimeEpoch = new RuntimeEpoch { Value = Guid.NewGuid() },
                },
                PreviousReplacementEpochs = operation.PreviousReplacementEpochs
                    .Append(operation.Replacement.RuntimeEpoch).ToArray(),
            };
        }
        var oldRuntime = hosted?.Runtime ?? this.retiredRuntimeSources.GetValueOrDefault(expected);
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
                    StartupUnconfirmed = true,
                    Replacement = replacementIdentity,
                    PreviousReplacementEpochs = operation.PreviousReplacementEpochs
                        .Append(operation.Replacement.RuntimeEpoch).ToArray(),
                };
                await state.SaveRestartAsync(operation, cancellationToken).ConfigureAwait(false);
                await state.RotateForRestartAsync(replacementIdentity, cancellationToken).ConfigureAwait(false);
                recoveredOwnership = this.CreateOwnershipLease(state, () => replacement);
                recoveredOwnership.Start(state.LeasePeriod);
                var context = this.contextFactory.Create(persisted);
                replacementLease = await state.RunRestartPhaseAsync(token => table.AcquireAsync(new AcquireAgentChatRequest
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
                }, token), async lateLease =>
                {
                    try
                    {
                        await table.TerminateOwnerRuntimeExactAsync(
                            lateLease.SessionId, lateLease.AgentChat, CancellationToken.None, this.timeProvider).ConfigureAwait(false);
                    }
                    finally { await lateLease.DisposeAsync().ConfigureAwait(false); }
                }).ConfigureAwait(false);
            }
            else
            {
                runtimeTransition = await hosted.Runtime.EnterRestartTransitionAsync(cancellationToken).ConfigureAwait(false);
                var remoteViewers = hosted.Runtime.CaptureRemoteViewerTokens();
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
                        var candidate = new RestartOperation
                        {
                            Request = request,
                            Replacement = replacementIdentity,
                            Checkpoint = checkpoint,
                            ViewerAttachmentTokens = remoteViewers,
                            State = AgentSessionOwnerRestartState.Committed,
                        };
                        try
                        {
                            await state.SaveRestartAsync(candidate, token).ConfigureAwait(false);
                            operation = candidate;
                        }
                        catch (Exception writeError)
                        {
                            // An acknowledgement may be lost after the durable write. Only a
                            // confirmed commit is allowed to cross the destructive boundary.
                            var authority = await state.RefreshAsync(
                                () => this.FindAsync(expected.AgentSessionId, CancellationToken.None),
                                CancellationToken.None).ConfigureAwait(false);
                            if (state.Expiry != default && authority.GetRequiredAuthoritativeTime() >= state.Expiry)
                                throw new AgentSessionUnavailableException();
                            var durable = await state.LoadRestartsAsync(CancellationToken.None).ConfigureAwait(false);
                            if (!durable.TryGetValue(request.CommandId.ToString("D"), out var committed))
                            {
                                if (writeError is not AgentSessionConcurrentUpdateException)
                                    throw;
                                // A concurrent metadata update is safe to merge only after
                                // refreshing and revalidating the same ownership and epoch.
                                await state.SaveRestartAsync(candidate, token).ConfigureAwait(false);
                                operation = candidate;
                                return;
                            }
                            if (committed.Request != candidate.Request
                                || committed.Replacement != candidate.Replacement
                                || committed.State != AgentSessionOwnerRestartState.Committed)
                                throw;
                            operation = committed;
                        }
                    },
                    async token =>
                    {
                        if (!hosted.Runtime.HasTerminated)
                            await this.WaitForRestartCleanupAsync(() => hosted.Runtime.RetireWithinRestartTransitionAsync(
                                replacementIdentity.RuntimeEpoch).AsTask()).ConfigureAwait(false);
                        shutdownConfirmed = true;
                        token.ThrowIfCancellationRequested();
                        operation = operation! with
                        {
                            State = AgentSessionOwnerRestartState.Stopped,
                            StartupUnconfirmed = true,
                        };
                        await state.SaveRestartAsync(operation, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        await state.RotateForRestartAsync(replacementIdentity, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        recoveredOwnership = this.CreateOwnershipLease(state, () => replacement);
                        recoveredOwnership.Start(state.LeasePeriod);
                    },
                    cancellationToken,
                    state.OwnerLifetime.Token, this.timeProvider).ConfigureAwait(false);
            }

            var intent = hosted is null ? this.contextFactory.Create(persisted).Intent
                : hosted.Intent with { ContinueInBackground = hosted.Runtime.ContinueInBackground };
            replacement = this.CreateRuntimeLease(
                intent, state, replacementLease, Context(replacementIdentity,
                    (replacementLease.AgentChat as AgentChat)?.RuntimeServices?.CurrentSessionContext as CurrentSessionContext), publish: false,
                ownership: recoveredOwnership);
            var publicationAuthority = await state.RefreshAsync(
                () => this.FindAsync(expected.AgentSessionId, CancellationToken.None),
                CancellationToken.None).ConfigureAwait(false);
            if (publicationAuthority.GetRequiredAuthoritativeTime() >= state.Expiry)
            {
                state.OwnerLifetime.Cancel();
                throw new AgentSessionUnavailableException();
            }
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
                if (!oldRuntime.HasTerminated)
                    await this.WaitForRestartCleanupAsync(() =>
                        oldRuntime.RetireForRestartAsync(replacementIdentity.RuntimeEpoch).AsTask()).ConfigureAwait(false);
            }
            if (oldRuntime is not null)
                await state.RunRestartPhaseAsync(token => table.ReattachLocalViewersAsync(
                    oldRuntime.Chat, replacementIdentity, token)).ConfigureAwait(false);
            operation = operation! with { State = AgentSessionOwnerRestartState.Succeeded, Message = null, StartupUnconfirmed = true };
            await state.SaveRestartAsync(operation, CancellationToken.None, markRunning: true).ConfigureAwait(false);
            if (replacementLease.AgentChat is AgentChat newChat)
                replacement.ResumeAfterRestartViewersReady(
                    operation.ViewerAttachmentTokens, newChat.InputQueueManager.ResumeAfterRuntimeHandoff);
            this.retiredRuntimeSources.TryRemove(expected, out _);
            this.PublishRestartState(table, oldRuntime?.Chat, request, operation.ToResult());
            await state.ConfirmRestartCompletionAsync(operation).ConfigureAwait(false);
            return operation.ToResult();
        }
        catch (OperationCanceledException) when (operation is null && cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception restartError)
        {
            if (operation is null)
                throw;
            if (restartError is AgentRuntimeRestartCleanupUnconfirmedException)
                shutdownConfirmed = false;
            if (state.PersistenceUncertain)
                return await this.QuarantineRestartAsync(
                    state, operation, table, oldRuntime, replacement, replacementLease, recoveredOwnership).ConfigureAwait(false);
            try
            {
                var authority = await state.RefreshAsync(
                    () => this.FindAsync(expected.AgentSessionId, CancellationToken.None),
                    CancellationToken.None).ConfigureAwait(false);
                if (state.Expiry != default && authority.GetRequiredAuthoritativeTime() >= state.Expiry)
                    throw new AgentSessionUnavailableException();
            }
            catch (Exception refreshError)
            {
                if (state.PersistenceUncertain)
                    return await this.QuarantineRestartAsync(
                        state, operation, table, oldRuntime, replacement, replacementLease, recoveredOwnership).ConfigureAwait(false);
                state.OwnerLifetime.Cancel();
                try
                {
                    if (replacementLease is not null)
                    {
                        try
                        {
                            await state.SaveRestartAsync(operation with
                            {
                                State = AgentSessionOwnerRestartState.Blocked,
                                Message = "Replacement authority or cleanup could not be confirmed.",
                            }, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (AgentSessionConcurrentUpdateException) when (refreshError is AgentSessionUnavailableException) { }
                    }
                }
                finally
                {
                    await this.CleanupRestartResourcesAsync(
                        table, replacement, replacementLease, recoveredOwnership, retireOnly: true).ConfigureAwait(false);
                }
                if (refreshError is not AgentSessionUnavailableException)
                    throw;
                return Changed(request);
            }
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
                this.PublishRestartState(table, oldRuntime?.Chat, request, durable.ToResult());
                await state.ConfirmRestartCompletionAsync(durable).ConfigureAwait(false);
                return durable.ToResult();
            }
            if (replacementLease is null && recoveredOwnership is not null)
            {
                operation = operation with
                {
                    State = AgentSessionOwnerRestartState.Blocked,
                    Message = "Replacement ownership cleanup has not been confirmed. Replacement is blocked.",
                };
                try
                {
                    await state.SaveRestartAsync(operation, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return await this.QuarantineRestartAsync(
                        state, operation, table, oldRuntime, replacement, replacementLease, recoveredOwnership).ConfigureAwait(false);
                }
                try
                {
                    await this.WaitForRestartCleanupAsync(() => recoveredOwnership.DisposeAsync().AsTask()).ConfigureAwait(false);
                }
                catch
                {
                    this.PublishRestartState(table, oldRuntime?.Chat, request, operation.ToResult());
                    throw;
                }
                if (hosted is null)
                    await state.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            if (replacementLease is not null
                && (!this.hostedRuntimes.TryGetValue(expected.AgentSessionId, out var published)
                    || !ReferenceEquals(published.Runtime, replacement)))
            {
                shutdownConfirmed = false;
                // Persist the acquisition fence before cleanup: an unconfirmed cleanup must
                // never leave a durable stopped journal that authorizes another runtime.
                operation = operation with
                {
                    State = AgentSessionOwnerRestartState.Blocked,
                    Message = "Unpublished replacement cleanup has not been confirmed. Replacement is blocked.",
                };
                try
                {
                    await state.SaveRestartAsync(operation, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return await this.QuarantineRestartAsync(
                        state, operation, table, oldRuntime, replacement, replacementLease, recoveredOwnership).ConfigureAwait(false);
                }
                try
                {
                    await this.CleanupRestartResourcesAsync(
                        table, replacement, replacementLease, recoveredOwnership).ConfigureAwait(false);
                }
                catch
                {
                    this.PublishRestartState(table, oldRuntime?.Chat, request, operation.ToResult());
                    throw;
                }
                shutdownConfirmed = true;
                if (hosted is not null)
                {
                    this.retiredRuntimeSources[expected] = hosted.Runtime;
                    this.hostedRuntimes.TryRemove(expected.AgentSessionId, out _);
                }
            }
            operation = operation with
            {
                StartupUnconfirmed = !shutdownConfirmed,
                State = shutdownConfirmed ? AgentSessionOwnerRestartState.Stopped : AgentSessionOwnerRestartState.Blocked,
                Message = shutdownConfirmed
                    ? "The session is stopped with pending input preserved. Retry this restart operation."
                    : "Old-runtime shutdown or final persistence failed. Replacement is blocked.",
            };
            await state.SaveRestartAsync(operation, CancellationToken.None).ConfigureAwait(false);
            this.PublishRestartState(table, oldRuntime?.Chat, request, operation.ToResult());
            if (this.hostedRuntimes.TryGetValue(expected.AgentSessionId, out var stoppedRuntime))
            {
                try
                {
                    if (stoppedRuntime.Runtime.Chat is AgentChat stoppedChat)
                        await this.WaitForRestartCleanupAsync(() =>
                            stoppedChat.PublishRuntimeRestartDiagnosticAsync(operation.Message!)).ConfigureAwait(false);
                }
                finally
                {
                    await stoppedRuntime.Runtime.PublishRestartStoppedAsync(new SessionRestartStoppedEvent
                        {
                            Message = operation.Message!, Request = request, Result = operation.ToResult(),
                        }, blockRuntime: !shutdownConfirmed).ConfigureAwait(false);
                }
            }
            return operation.ToResult();
        }
        finally
        {
            runtimeTransition?.Dispose();
        }
        }
        catch (RestartPersistenceTimeoutException) when (operation is not null)
        {
            return await this.QuarantineRestartAsync(
                state, operation, table, oldRuntime, replacement, replacementLease, recoveredOwnership).ConfigureAwait(false);
        }
    }

    private async Task CleanupRestartResourcesAsync(
        RunningAgentChatTable table,
        RemoteAgentSessionLease? replacement,
        RunningAgentChatLease? replacementLease,
        AgentSessionOwnershipLease? ownership,
        bool retireOnly = false)
    {
        var errors = new List<Exception>();
        async Task CleanAsync(Func<Task> cleanup)
        {
            try { await this.WaitForRestartCleanupAsync(cleanup).ConfigureAwait(false); }
            catch (Exception error)
            {
                lock (errors)
                    errors.Add(error);
            }
        }
        var cleanups = new List<Task>();
        if (ownership is not null)
            cleanups.Add(CleanAsync(() => ownership.DisposeAsync().AsTask()));
        if (replacement is not null && !retireOnly)
            cleanups.Add(CleanAsync(() => replacement.DisposeAsync().AsTask()));
        if (replacementLease is not null)
            cleanups.Add(CleanAsync(() => table.TerminateOwnerRuntimeExactAsync(
                replacementLease.SessionId, replacementLease.AgentChat, CancellationToken.None, this.timeProvider)));
        if (replacement is not null && retireOnly)
            cleanups.Add(CleanAsync(() => replacement.RetireForRestartAsync(replacement.Epoch).AsTask()));
        await Task.WhenAll(cleanups).ConfigureAwait(false);
        if (replacementLease is not null)
            await CleanAsync(() => replacementLease.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (errors.Count != 0 && (errors.Count == 1 || errors.All(error => error is OperationCanceledException)))
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1)
            throw new AggregateException(errors);
    }

    private async Task<AgentSessionOwnerRestartResult> QuarantineRestartAsync(
        PersistedRuntimeState state,
        RestartOperation operation,
        RunningAgentChatTable table,
        RemoteAgentSessionLease? original,
        RemoteAgentSessionLease? replacement,
        RunningAgentChatLease? replacementLease,
        AgentSessionOwnershipLease? ownership)
    {
        var blocked = operation with
        {
            State = AgentSessionOwnerRestartState.Blocked,
            StartupUnconfirmed = true,
            Message = "Durable restart state or cleanup is unconfirmed. Replacement is blocked.",
        };
        this.uncertainRestarts[operation.Request.ExpectedRuntime.AgentSessionId] = blocked.ToResult();
        this.PublishRestartState(table, original?.Chat, operation.Request, blocked.ToResult());
        // A failed journal write must never bypass actual resource cleanup. Each resource
        // has its own owner deadline; late work stays rooted and cannot publish a replacement.
        async Task CleanAsync(Func<Task> cleanup)
        {
            try { await this.WaitForRestartCleanupAsync(cleanup).ConfigureAwait(false); }
            catch (Exception) { }
        }
        try
        {
            await this.CleanupRestartResourcesAsync(
                table, replacement, replacementLease, ownership, retireOnly: true).ConfigureAwait(false);
        }
        catch (Exception) { }
        if (original is not null && !original.HasTerminated)
            await CleanAsync(() => original.RetireWithinRestartTransitionAsync(operation.Replacement.RuntimeEpoch).AsTask())
                .ConfigureAwait(false);
        var reconcile = this.FenceLatePersistenceAsync(state.PendingPersistence, blocked);
        ObservePendingPersistence(reconcile);
        return blocked.ToResult();
    }

    private async Task FenceLatePersistenceAsync(Task pending, RestartOperation blocked)
    {
        try { await pending.ConfigureAwait(false); }
        catch (Exception) { }
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), this.timeProvider);
            var queryTask = this.FindAsync(blocked.Request.ExpectedRuntime.AgentSessionId, deadline.Token);
            ObservePendingPersistence(queryTask);
            var query = await queryTask.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (query.Entity is null)
                return;
            var state = new PersistedRuntimeState(
                this.dataAccessLayer, query.Entity, query.GetRequiredAuthoritativeTime(), this.timeProvider);
            var operations = await state.LoadRestartsAsync(deadline.Token).ConfigureAwait(false);
            if (operations.TryGetValue(blocked.Request.CommandId.ToString("D"), out var durable)
                && durable.Request == blocked.Request && durable.Replacement == blocked.Replacement
                && ReadLong(query.Entity.Data!.Value, "ownership-generation")
                    == blocked.Request.ExpectedRuntime.OwnershipGeneration
                && string.Equals(ReadString(query.Entity.Data.Value, "owning-profile-entity-id")
                    ?? ReadString(query.Entity.Data.Value, "host-profile-entity-id"),
                    blocked.Request.ExpectedRuntime.OwningProfileEntityId, StringComparison.OrdinalIgnoreCase))
                await state.SaveRestartAsync(blocked, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The durable pre-start reservation remains conservative when storage is
            // unavailable; no retry is authorized from the quarantined local state.
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
        CancellationToken cancellationToken,
        bool allowStoppedRetry = true)
    {
        if (this.uncertainRestarts.TryGetValue(sessionId, out var uncertain))
            throw new InvalidOperationException(uncertain.Message);
        var query = await this.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (query.Entity is null)
            throw new AgentSessionUnavailableException();
        var state = this.hostedRuntimes.TryGetValue(sessionId, out var hosted)
            ? hosted.State : new PersistedRuntimeState(
                this.dataAccessLayer, query.Entity, query.GetRequiredAuthoritativeTime(), this.timeProvider);
        var operations = await state.LoadRestartsAsync(cancellationToken).ConfigureAwait(false);
        if (operations.Values.Any(operation => operation.StartupUnconfirmed
            && operation.State == AgentSessionOwnerRestartState.Succeeded
            && (hosted is null || hosted.Runtime.IsFenced
                || hosted.Runtime.Epoch != operation.Replacement.RuntimeEpoch)))
            throw new InvalidOperationException("The prior replacement's cleanup has not been confirmed.");
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
        if (!allowStoppedRetry && pending.State == AgentSessionOwnerRestartState.Stopped)
            throw new InvalidOperationException(pending.Message);
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
                this.dataAccessLayer, query.Entity, query.GetRequiredAuthoritativeTime(), this.timeProvider);
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
