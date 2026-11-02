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
        var query = await this.FindAsync(sessionId, ct).ConfigureAwait(false);
        return query.Entity?.Data is JsonElement data
            && data.TryGetProperty("restart-operations", out var journal)
            && JsonSerializer.Deserialize<Dictionary<string, RestartOperation>>(
                journal, Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions)!.Values.Any(operation =>
                    operation.Request.ExpectedRuntime.OwnershipGeneration == generation
                    && operation.State is AgentSessionOwnerRestartState.Committed or AgentSessionOwnerRestartState.Blocked);
    }

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
        var operation = request.CommandId == Guid.Empty
            ? operations.Values.LastOrDefault(value => value.Request.ExpectedRuntime == request.ExpectedRuntime)
            : operations.GetValueOrDefault(request.CommandId.ToString("D"));
        if (operation is null)
            return null;
        if (request.CommandId != Guid.Empty && operation.Request != request)
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
            this.PublishRestartState(table, hosted.Runtime.Chat, request, operation.ToResult());
            return operation.ToResult();
        }

        if (hosted?.Runtime.HasTerminated == true
            && operation?.State == AgentSessionOwnerRestartState.Stopped
            && TryReadLeaseExpiry(persisted, out var stoppedExpiry)
            && stoppedExpiry <= query.GetRequiredAuthoritativeTime())
        {
            state = new PersistedRuntimeState(
                this.dataAccessLayer, query.Entity!, query.GetRequiredAuthoritativeTime());
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
                    async _ =>
                    {
                        if (!hosted.Runtime.HasTerminated)
                            await this.WaitForRestartCleanupAsync(() => hosted.Runtime.RetireWithinRestartTransitionAsync(
                                replacementIdentity.RuntimeEpoch).AsTask()).ConfigureAwait(false);
                        shutdownConfirmed = true;
                        operation = operation! with { State = AgentSessionOwnerRestartState.Stopped };
                        await state.SaveRestartAsync(operation, CancellationToken.None).ConfigureAwait(false);
                        await state.RotateForRestartAsync(replacementIdentity, CancellationToken.None).ConfigureAwait(false);
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
                await table.ReattachLocalViewersAsync(oldRuntime.Chat, replacementIdentity).ConfigureAwait(false);
            operation = operation! with { State = AgentSessionOwnerRestartState.Succeeded, Message = null };
            await state.SaveRestartAsync(operation, CancellationToken.None, markRunning: true).ConfigureAwait(false);
            if (replacementLease.AgentChat is AgentChat newChat)
                replacement.ResumeAfterRestartViewersReady(
                    operation.ViewerAttachmentTokens, newChat.InputQueueManager.ResumeAfterRuntimeHandoff);
            this.retiredRuntimeSources.TryRemove(expected, out _);
            this.PublishRestartState(table, oldRuntime?.Chat, request, operation.ToResult());
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
                state.OwnerLifetime.Cancel();
                if (recoveredOwnership is not null)
                    await this.WaitForRestartCleanupAsync(() => recoveredOwnership.DisposeAsync().AsTask()).ConfigureAwait(false);
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
                    finally
                    {
                        try
                        {
                            await this.WaitForRestartCleanupAsync(() => table.TerminateOwnerRuntimeExactAsync(
                                replacementLease.SessionId, replacementLease.AgentChat, CancellationToken.None)).ConfigureAwait(false);
                        }
                        finally
                        {
                            await this.WaitForRestartCleanupAsync(() => replacementLease.DisposeAsync().AsTask()).ConfigureAwait(false);
                            if (replacement is not null)
                                await this.WaitForRestartCleanupAsync(() =>
                                    replacement.RetireForRestartAsync(replacement.Epoch).AsTask()).ConfigureAwait(false);
                        }
                    }
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
                return durable.ToResult();
            }
            if (replacementLease is null && recoveredOwnership is not null)
            {
                operation = operation with
                {
                    State = AgentSessionOwnerRestartState.Blocked,
                    Message = "Replacement ownership cleanup has not been confirmed. Replacement is blocked.",
                };
                await state.SaveRestartAsync(operation, CancellationToken.None).ConfigureAwait(false);
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
                await state.SaveRestartAsync(operation, CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (recoveredOwnership is not null)
                        await this.WaitForRestartCleanupAsync(() => recoveredOwnership.DisposeAsync().AsTask()).ConfigureAwait(false);
                    if (replacement is not null)
                        await this.WaitForRestartCleanupAsync(() => replacement.DisposeAsync().AsTask()).ConfigureAwait(false);
                    else
                    {
                        await this.WaitForRestartCleanupAsync(() => table.TerminateOwnerRuntimeExactAsync(
                            replacementLease.SessionId, replacementLease.AgentChat, CancellationToken.None)).ConfigureAwait(false);
                        await this.WaitForRestartCleanupAsync(() => replacementLease.DisposeAsync().AsTask()).ConfigureAwait(false);
                    }
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
                State = shutdownConfirmed ? AgentSessionOwnerRestartState.Stopped : AgentSessionOwnerRestartState.Blocked,
                Message = shutdownConfirmed
                    ? "The session is stopped with pending input preserved. Retry this restart operation."
                    : "Old-runtime shutdown or final persistence failed. Replacement is blocked.",
            };
            await state.SaveRestartAsync(operation, CancellationToken.None).ConfigureAwait(false);
            this.PublishRestartState(table, oldRuntime?.Chat, request, operation.ToResult());
            if (this.hostedRuntimes.TryGetValue(expected.AgentSessionId, out var stoppedRuntime))
            {
                if (stoppedRuntime.Runtime.Chat is AgentChat stoppedChat)
                    await stoppedChat.PublishRuntimeRestartDiagnosticAsync(operation.Message!).ConfigureAwait(false);
                await stoppedRuntime.Runtime.PublishRestartStoppedAsync(new SessionRestartStoppedEvent
                    {
                        Message = operation.Message!, Request = request, Result = operation.ToResult(),
                    }, blockRuntime: !shutdownConfirmed).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        bool allowStoppedRetry = true)
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
