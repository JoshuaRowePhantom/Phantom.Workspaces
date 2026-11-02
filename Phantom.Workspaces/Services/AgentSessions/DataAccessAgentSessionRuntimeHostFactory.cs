using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Remote;

namespace Phantom.Workspaces.Services.AgentSessions;

internal sealed partial class DataAccessAgentSessionRuntimeHostFactory :
    IAgentSessionRuntimeHostFactory, IAgentSessionRestartRuntimeHostFactory
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private readonly IDataAccessLayer dataAccessLayer;
    private readonly IRunningAgentChatTable runningChats;
    private readonly IAgentSessionRuntimeContextFactory contextFactory;
    private readonly IAgentDefinitionResolver definitionResolver;
    private readonly TimeProvider timeProvider;
    private readonly AgentServices? hostServices;

    internal DataAccessAgentSessionRuntimeHostFactory(
        IDataAccessLayer dataAccessLayer,
        IRunningAgentChatTable runningChats,
        IAgentSessionRuntimeContextFactory contextFactory,
        TimeProvider? timeProvider = null,
        AgentServices? hostServices = null)
    {
        this.dataAccessLayer = dataAccessLayer ?? throw new ArgumentNullException(nameof(dataAccessLayer));
        this.runningChats = runningChats ?? throw new ArgumentNullException(nameof(runningChats));
        this.contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        this.definitionResolver = new AgentDefinitionResolver(dataAccessLayer);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.hostServices = hostServices;
    }

    public async ValueTask<PersistedAgentSessionRuntimeIntent?> LoadIntentAsync(
        string sessionId, CancellationToken ct)
    {
        var result = await this.FindAsync(sessionId, ct).ConfigureAwait(false);
        return result.Entity?.Data is JsonElement data ? this.contextFactory.Create(data).Intent : null;
    }

    public Task<RemoteAgentSessionLease> StartAsync(
        PersistedAgentSessionRuntimeIntent intent, CancellationToken ct)
        => this.StartLocalAsync(intent, null, ct);

    internal async Task<RemoteAgentSessionLease> StartLocalAsync(
        PersistedAgentSessionRuntimeIntent intent,
        AcquireAgentChatRequest? localRequest,
        CancellationToken ct)
    {
        var query = await this.FindAsync(intent.AgentSessionId, ct).ConfigureAwait(false);
        var persisted = query.Entity ?? throw new AgentSessionUnavailableException();
        if (persisted.Data is not JsonElement data)
            throw new AgentSessionUnavailableException();
        if (ReadLong(data, "ownership-generation") != intent.OwnershipGeneration
            || !string.Equals(
                ReadString(data, "owning-profile-entity-id")
                    ?? ReadString(data, "host-profile-entity-id"),
                intent.OwningProfileEntityId,
                StringComparison.OrdinalIgnoreCase))
            throw new AgentSessionUnavailableException();

        var state = new PersistedRuntimeState(
            this.dataAccessLayer, persisted, query.GetRequiredAuthoritativeTime(), this.timeProvider);
        await state.RecordInterruptedAndAcquireAsync(intent, ct).ConfigureAwait(false);
        RunningAgentChatLease? chatLease = null;
        RemoteAgentSessionLease? runtime = null;
        var ownership = this.CreateOwnershipLease(state, () => runtime);
        ownership.Start(state.LeasePeriod);
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, state.OwnerLifetime.Token);
        try
        {
            var sessionContext = ((localRequest?.AgentServices?.CurrentSessionContext
                ?? this.hostServices?.CurrentSessionContext) as CurrentSessionContext
                ?? new CurrentSessionContext
                {
                    AgentSessionId = intent.AgentSessionId,
                    OwningProfileEntityId = intent.OwningProfileEntityId,
                    OwnershipGeneration = intent.OwnershipGeneration,
                }) with
            {
                AgentSessionId = intent.AgentSessionId,
                OwningProfileEntityId = intent.OwningProfileEntityId,
                OwnershipGeneration = intent.OwnershipGeneration,
                RuntimeEpoch = state.Epoch,
            };
            chatLease = await this.runningChats.AcquireAsync(new AcquireAgentChatRequest
            {
                OwnerRuntimeAcquisition = true,
                AgentSessionId = new AgentSessionId(intent.AgentSessionId),
                AgentSessionEntity = data,
                AgentServices = (localRequest?.AgentServices ?? this.hostServices ?? new AgentServices()) with
                {
                    CurrentSessionContext = sessionContext,
                },
                AgentDefinition = localRequest?.AgentDefinition,
                AgentManifest = localRequest?.AgentManifest,
                AgentDefinitionResolver = localRequest?.AgentDefinitionResolver ?? this.definitionResolver,
                ObserveInitializingChat = localRequest?.ObserveInitializingChat,
                CancelStartup = localRequest?.CancelStartup,
                ProgressCancellation = localRequest?.ProgressCancellation,
                Parameters = ReadStringDictionary(data, "parameter-values"),
                EntityId = persisted.EntityId.ToString(),
                EntityName = RunningAgentChatTable.ReadCanonicalDisplayName(data) ?? "",
                EntityDisplayName = RunningAgentChatTable.ReadCanonicalDisplayName(data),
                AcquisitionMode = AgentChatAcquisitionMode.Local,
            }, startupCancellation.Token).ConfigureAwait(false);

            state.OwnerLifetime.Token.ThrowIfCancellationRequested();
            runtime = this.CreateRuntimeLease(intent, state, chatLease, sessionContext, ownership: ownership);
            return runtime;
        }
        catch
        {
            await ownership.DisposeAsync().ConfigureAwait(false);
            if (chatLease is not null)
                await chatLease.DisposeAsync().ConfigureAwait(false);
            await state.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private RemoteAgentSessionLease CreateRuntimeLease(
        PersistedAgentSessionRuntimeIntent intent,
        PersistedRuntimeState state,
        RunningAgentChatLease chatLease,
        CurrentSessionContext sessionContext,
        bool publish = true,
        AgentSessionOwnershipLease? ownership = null)
    {
            state.OwnerLifetime.Token.ThrowIfCancellationRequested();
            var chat = chatLease.AgentChat;
            RemoteAgentSessionLease? runtime = null;
            if (ownership is null)
            {
                ownership = this.CreateOwnershipLease(state, () => runtime);
                ownership.Start(state.LeasePeriod);
            }
            runtime = new RemoteAgentSessionLease(
                intent.AgentSessionId,
                intent.OwnershipGeneration,
                state.Epoch,
                chat,
                intent.ContinueInBackground,
                () => Snapshot(chat),
                async (value, token) =>
                {
                    await state.PersistRetentionAsync(value, token).ConfigureAwait(false);
                    (this.runningChats as RunningAgentChatTable)?.ApplyOwnerRuntimeRetention(
                        new AgentSessionId(intent.AgentSessionId), value);
                },
                token => state.PersistStoppedAsync(token),
                this.timeProvider,
                ownership,
                chatLease,
                sessionContext,
                terminateRuntimeAsync: async () =>
                {
                    var terminated = this.runningChats is RunningAgentChatTable table
                        ? await table.TerminateOwnerRuntimeExactAsync(
                            chatLease.SessionId, chat, CancellationToken.None, this.timeProvider).ConfigureAwait(false)
                        : await this.runningChats.TerminateExactAsync(
                            chatLease.SessionId, chat, CancellationToken.None).ConfigureAwait(false);
                    if (!terminated
                        && !chatLease.IsRuntimeTerminal)
                        throw new InvalidOperationException("The owning runtime could not be terminated.");
                });
            runtime.HasDurableRestartAuthority = true;
            runtime.Terminated += (_, _) =>
            {
                if (runtime.IsRestartRetirement)
                    return;
                if (this.hostedRuntimes.TryGetValue(intent.AgentSessionId, out var current)
                    && ReferenceEquals(current.Runtime, runtime))
                    this.hostedRuntimes.TryRemove(
                        new KeyValuePair<string, HostedRuntime>(intent.AgentSessionId, current));
            };
            (this.runningChats as RunningAgentChatTable)?.ApplyOwnerRuntimeRetention(
                new AgentSessionId(intent.AgentSessionId), intent.ContinueInBackground);
            if (publish)
                this.hostedRuntimes[intent.AgentSessionId] = new HostedRuntime(
                    runtime, state, chatLease, intent);
            return runtime;
    }

    private AgentSessionOwnershipLease CreateOwnershipLease(
        PersistedRuntimeState state, Func<RemoteAgentSessionLease?> runtime)
        => new(
            this.timeProvider,
            token => state.RenewAsync(token),
            async token =>
            {
                state.OwnerLifetime.Cancel();
                if (runtime() is { } current)
                    await current.ReportFatalFailureAsync(token).ConfigureAwait(false);
            },
            token => state.ReleaseAsync(token));

    public async ValueTask<bool> TryTakeOverAsync(
        AgentSessionTakeoverRequest request, CancellationToken ct)
    {
        if (this.uncertainRestarts.ContainsKey(request.AgentSessionId))
            return false;
        var query = await this.FindAsync(request.AgentSessionId, ct).ConfigureAwait(false);
        var entity = query.Entity;
        if (entity?.Data is not JsonElement data
            || ReadLong(data, "ownership-generation") != request.ExpectedOwnershipGeneration
            || !string.Equals(
                ReadString(data, "owning-profile-entity-id")
                    ?? ReadString(data, "host-profile-entity-id"),
                request.ExpectedOwningProfileEntityId,
                StringComparison.OrdinalIgnoreCase))
            return false;
        if (data.TryGetProperty("restart-operations", out var journal)
            && JsonSerializer.Deserialize<Dictionary<string, RestartOperation>>(
                journal, Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions)!.Values.Any(operation =>
                    operation.Request.ExpectedRuntime.OwnershipGeneration == request.ExpectedOwnershipGeneration
                    && this.HasUnconfirmedRestartCleanup(operation)))
            return false;
        var hasEpoch = data.TryGetProperty("runtime-epoch", out _);
        var hasLease = data.TryGetProperty("runtime-lease-expiry", out _);
        if (hasEpoch || hasLease)
        {
            if (!hasEpoch || !hasLease
                || !TryReadRuntimeEpoch(data, out _)
                || !TryReadLeaseExpiry(data, out var expiry)
                || expiry > query.GetRequiredAuthoritativeTime())
                return false;
        }

        var replacement = Merge(data, new Dictionary<string, object?>
        {
            ["host-profile-entity-id"] = request.NewOwningProfileEntityId,
            ["owning-profile-entity-id"] = request.NewOwningProfileEntityId,
            ["ownership-generation"] = request.ExpectedOwnershipGeneration + 1,
            ["runtime-state"] = "stopped",
            ["runtime-epoch"] = null,
            ["runtime-lease-expiry"] = null,
        });
        return await TryReplaceAsync(this.dataAccessLayer, entity, replacement, "Take over agent session", ct)
            .ConfigureAwait(false);
    }

    private async Task<AuthoritativeQueryResult> FindAsync(string sessionId, CancellationToken ct)
    {
        var result = await this.dataAccessLayer.QueryAsync(new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("agent-session"),
                    Clause = new EntityTypeQueryClause
                    {
                        EntityTypeNames = new EntityTypeNameSet(["agent-session"]),
                    },
                },
            ],
            Timestamps = [null],
        }, ct).ConfigureAwait(false);
        var entity = result.Batches.SelectMany(batch => batch.Entities).FirstOrDefault(entity =>
            entity.Data is JsonElement value
            && string.Equals(ReadString(value, "agent-session-id"), sessionId, StringComparison.Ordinal));
        return new AuthoritativeQueryResult(entity, result.AuthoritativeTimestamp?.DateTime);
    }

    private readonly record struct AuthoritativeQueryResult(
        QueryEntitySnapshot? Entity, DateTimeOffset? AuthoritativeTime)
    {
        internal DateTimeOffset GetRequiredAuthoritativeTime()
            => this.AuthoritativeTime
                ?? throw new AgentSessionTakeoverBlockedException();
    }

    private static AgentSessionSnapshot Snapshot(IAgentChat chat) => new()
    {
        Information = chat.Information,
        Usage = chat.Usage,
        InputQueues = chat.InputQueues.Snapshot,
        IsBusy = chat.IsBusy,
        History = chat.History.Select(item => JsonSerializer.SerializeToElement(
            item,
            Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions)).ToArray(),
        // RemoteAgentSessionLease overlays its writer-synchronized immutable mirror.
        RunningItems = [],
        Tools = chat.GetToolSnapshot().Select(item => JsonSerializer.SerializeToElement(
            item,
            Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions)).ToArray(),
        // The lease overlays its synchronized child-state mirror.
        Subagents = [],
        Modals = chat.Modals.ToArray(),
        ContinueInBackground = false,
        ViewerCount = 0,
    };

    private static string? ReadString(JsonElement data, string name)
        => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadLong(JsonElement data, string name)
        => data.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;

    private static IReadOnlyDictionary<string, string>? ReadStringDictionary(
        JsonElement data,
        string propertyName)
    {
        if (!data.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new AgentSessionUnavailableException();
            }
            result[property.Name] = property.Value.GetString()!;
        }
        return result;
    }

    private static bool TryReadRuntimeEpoch(JsonElement data, out Guid epoch)
    {
        epoch = default;
        return data.TryGetProperty("runtime-epoch", out var value)
            && value.ValueKind == JsonValueKind.String
            && Guid.TryParse(value.GetString(), out epoch);
    }

    private static bool TryReadLeaseExpiry(JsonElement data, out DateTimeOffset expiry)
    {
        expiry = default;
        return data.TryGetProperty("runtime-lease-expiry", out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out expiry);
    }

    private static JsonElement Merge(JsonElement source, IReadOnlyDictionary<string, object?> updates)
    {
        var values = source.EnumerateObject().ToDictionary(
            property => property.Name,
            property => (object?)property.Value.Clone(),
            StringComparer.Ordinal);
        foreach (var update in updates)
        {
            if (update.Value is null) values.Remove(update.Key);
            else values[update.Key] = update.Value;
        }
        return JsonSerializer.SerializeToElement(values);
    }

    private static async ValueTask<bool> TryReplaceAsync(
        IDataAccessLayer dataAccessLayer,
        EntitySnapshot entity,
        JsonElement data,
        string comment,
        CancellationToken ct)
    {
        var result = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = comment } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = entity.EntityId,
                    ConcurrencyTag = entity.ConcurrencyTag,
                    Data = data,
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        }, ct).ConfigureAwait(false);
        return result.EntityResults.Count == 1
            && result.EntityResults.Single().Errors.Count == 0
            && result.EntityResults.Single().ConcurrencyMatchState != ConcurrencyMatchState.NotMatched;
    }

    private sealed class AgentSessionConcurrentUpdateException()
        : InvalidOperationException("The persisted agent session changed concurrently.");

    private sealed class PersistedRuntimeState
    {
        private readonly IDataAccessLayer dataAccessLayer;
        private readonly SemaphoreSlim gate = new(1, 1);
        private EntitySnapshot entity;
        private JsonElement data;
        private DateTimeOffset authoritativeTime;
        private readonly TimeProvider timeProvider;
        private RestartOperation? completionToConfirm;
        internal bool PersistenceUncertain { get; private set; }
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> persistenceOperations = new();
        internal Task PendingPersistence => Task.WhenAll(this.persistenceOperations.Keys);

        internal PersistedRuntimeState(
            IDataAccessLayer dataAccessLayer,
            EntitySnapshot entity,
            DateTimeOffset authoritativeTime,
            TimeProvider timeProvider)
        {
            this.dataAccessLayer = dataAccessLayer;
            this.entity = entity;
            this.data = entity.Data!.Value;
            this.authoritativeTime = authoritativeTime;
            this.timeProvider = timeProvider;
        }

        private async Task<T> AwaitPersistenceAsync<T>(
            Func<Task<T>> startOperation, CancellationToken cancellationToken, bool fenceOnTimeout = true)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), this.timeProvider);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
            var operation = startOperation();
            this.persistenceOperations.TryAdd(operation, 0);
            ObservePendingPersistence(operation);
            _ = operation.ContinueWith(completed => this.persistenceOperations.TryRemove(completed, out _),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try
            {
                var result = await operation.WaitAsync(lifetime.Token).ConfigureAwait(false);
                this.ThrowIfPersistenceUncertain();
                return result;
            }
            catch (Exception) when (!operation.IsCompleted)
            {
                if (!fenceOnTimeout)
                    throw new TimeoutException("The completed runtime's journal acknowledgement is pending.");
                this.PersistenceUncertain = true;
                this.OwnerLifetime.Cancel();
                throw new RestartPersistenceTimeoutException();
            }
        }

        private void ThrowIfPersistenceUncertain()
        {
            if (this.PersistenceUncertain)
                throw new RestartPersistenceTimeoutException();
        }

        internal async Task RunRestartPhaseAsync(Func<CancellationToken, Task> phase)
            => await this.RunRestartPhaseAsync(async token =>
            {
                await phase(token).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);

        internal async Task<T> RunRestartPhaseAsync<T>(
            Func<CancellationToken, Task<T>> phase,
            Func<T, Task>? cleanupLateResult = null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), this.timeProvider);
            var operation = phase(deadline.Token);
            ObservePendingPersistence(operation);
            try
            {
                var result = await operation.WaitAsync(deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                this.PersistenceUncertain = true;
                this.OwnerLifetime.Cancel();
                if (cleanupLateResult is not null)
                    ObservePendingPersistence(CleanupLateResultAsync(operation, cleanupLateResult));
                throw new RestartPersistenceTimeoutException();
            }
        }

        private static async Task CleanupLateResultAsync<T>(Task<T> operation, Func<T, Task> cleanup)
            => await cleanup(await operation.ConfigureAwait(false)).ConfigureAwait(false);

        internal RuntimeEpoch Epoch { get; private set; }
        internal CancellationTokenSource OwnerLifetime { get; } = new();
        internal DateTimeOffset Expiry { get; private set; }
        internal AgentSessionOwnershipLeasePeriod LeasePeriod
            => new(this.authoritativeTime, this.Expiry);

        internal async Task<AuthoritativeQueryResult> RefreshAsync(
            Func<Task<AuthoritativeQueryResult>> queryAsync, CancellationToken cancellationToken)
        {
            this.ThrowIfPersistenceUncertain();
            await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var query = await this.AwaitPersistenceAsync(queryAsync, cancellationToken).ConfigureAwait(false);
                if (query.Entity?.Data is not JsonElement refreshed
                    || ReadLong(refreshed, "ownership-generation") != ReadLong(this.data, "ownership-generation")
                    || !string.Equals(
                        ReadString(refreshed, "owning-profile-entity-id") ?? ReadString(refreshed, "host-profile-entity-id"),
                        ReadString(this.data, "owning-profile-entity-id") ?? ReadString(this.data, "host-profile-entity-id"),
                        StringComparison.OrdinalIgnoreCase))
                {
                    this.OwnerLifetime.Cancel();
                    throw new AgentSessionUnavailableException();
                }
                if (this.Epoch != default && TryReadRuntimeEpoch(refreshed, out var currentEpoch)
                    && currentEpoch != this.Epoch.Value)
                {
                    var operations = refreshed.TryGetProperty("restart-operations", out var journal)
                        ? JsonSerializer.Deserialize<Dictionary<string, RestartOperation>>(
                            journal, Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions)
                        : null;
                    if (operations?.Values.Any(operation =>
                        operation.Request.ExpectedRuntime.RuntimeEpoch == this.Epoch
                        && operation.Replacement.RuntimeEpoch.Value == currentEpoch
                        && operation.State is AgentSessionOwnerRestartState.Stopped
                            or AgentSessionOwnerRestartState.Succeeded) != true)
                    {
                        this.OwnerLifetime.Cancel();
                        throw new AgentSessionUnavailableException();
                    }
                    this.Epoch = new RuntimeEpoch { Value = currentEpoch };
                }
                this.entity = query.Entity;
                this.data = refreshed;
                this.authoritativeTime = query.GetRequiredAuthoritativeTime();
                if (this.Epoch != default)
                    this.Expiry = TryReadLeaseExpiry(refreshed, out var expiry) ? expiry : default;
                return query;
            }
            finally { this.gate.Release(); }
        }

        internal async ValueTask<Dictionary<string, RestartOperation>> LoadRestartsAsync(CancellationToken cancellationToken)
        {
            this.ThrowIfPersistenceUncertain();
            await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return this.data.TryGetProperty("restart-operations", out var journal)
                    ? JsonSerializer.Deserialize<Dictionary<string, RestartOperation>>(
                        journal, Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions)!
                    : new Dictionary<string, RestartOperation>(StringComparer.Ordinal);
            }
            finally { this.gate.Release(); }
        }

        internal async ValueTask SaveRestartAsync(
            RestartOperation operation, CancellationToken cancellationToken, bool markRunning = false,
            bool fenceOnTimeout = true)
        {
            var operations = await this.LoadRestartsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var existing in operations.Values.Where(value =>
                value.Request.ExpectedRuntime == operation.Request.ExpectedRuntime).ToArray())
                operations[existing.Request.CommandId.ToString("D")] = operation with { Request = existing.Request };
            operations[operation.Request.CommandId.ToString("D")] = operation;
            var updates = new Dictionary<string, object?>
            {
                ["restart-operations"] = JsonSerializer.SerializeToElement(
                    operations, Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions),
            };
            if (markRunning)
                updates["runtime-state"] = "running";
            await this.ReplaceAsync(updates, "Record agent session restart operation", cancellationToken, fenceOnTimeout).ConfigureAwait(false);
        }

        internal async Task ConfirmRestartCompletionAsync(RestartOperation operation)
        {
            this.completionToConfirm = operation;
            try
            {
                // Success is already durable and the published runtime is healthy. An
                // ambiguous acknowledgement here must not initiate destructive cleanup.
                // Renewal reloads authority and the CAS tag, and confirms this exact
                // successful operation even if this write never committed.
                await this.SaveRestartAsync(operation with { StartupUnconfirmed = false },
                    CancellationToken.None, fenceOnTimeout: false).ConfigureAwait(false);
            }
            catch (Exception) { }
        }

        internal async ValueTask RotateForRestartAsync(
            AgentSessionRuntimeIdentity replacement,
            CancellationToken cancellationToken)
        {
            this.ThrowIfPersistenceUncertain();
            var query = await this.AwaitPersistenceAsync(() => this.dataAccessLayer.QueryAsync(new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("agent-session"),
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet(["agent-session"]),
                        },
                    },
                ],
                Timestamps = [null],
            }, cancellationToken), cancellationToken).ConfigureAwait(false);
            var authoritativeTime = query.AuthoritativeTimestamp?.DateTime
                ?? throw new AgentSessionTakeoverBlockedException();
            if (this.Expiry != default && authoritativeTime >= this.Expiry)
            {
                this.OwnerLifetime.Cancel();
                throw new AgentSessionTakeoverBlockedException();
            }
            var expiry = authoritativeTime + LeaseDuration;
            await this.ReplaceAsync(new Dictionary<string, object?>
            {
                ["runtime-state"] = "restarting",
                ["runtime-epoch"] = replacement.RuntimeEpoch.Value.ToString("D"),
                ["runtime-lease-expiry"] = expiry.ToString("O"),
            }, "Fence replacement agent session runtime epoch", cancellationToken).ConfigureAwait(false);
            this.authoritativeTime = authoritativeTime;
            this.Expiry = expiry;
            this.Epoch = replacement.RuntimeEpoch;
        }

        internal async ValueTask RecordInterruptedAndAcquireAsync(
            PersistedAgentSessionRuntimeIntent intent, CancellationToken ct)
        {
            var now = this.authoritativeTime;
            var hasEpoch = this.data.TryGetProperty("runtime-epoch", out _);
            var hasLease = this.data.TryGetProperty("runtime-lease-expiry", out _);
            if (hasEpoch || hasLease)
            {
                if (!hasEpoch || !hasLease
                    || !TryReadRuntimeEpoch(this.data, out var abandoned)
                    || !TryReadLeaseExpiry(this.data, out var oldExpiryValue))
                    throw new AgentSessionTakeoverBlockedException();
                if (oldExpiryValue > now)
                    throw new AgentSessionTakeoverBlockedException();
                await this.ReplaceAsync(new Dictionary<string, object?>
                {
                    ["runtime-state"] = "interrupted",
                    ["last-stopped-runtime-epoch"] = abandoned.ToString("D"),
                    ["runtime-epoch"] = null,
                    ["runtime-lease-expiry"] = null,
                }, "Record interrupted agent session epoch", ct).ConfigureAwait(false);
            }

            this.Epoch = new RuntimeEpoch { Value = Guid.NewGuid() };
            this.Expiry = now + LeaseDuration;
            await this.ReplaceAsync(new Dictionary<string, object?>
            {
                ["runtime-state"] = "running",
                ["runtime-epoch"] = this.Epoch.Value.ToString("D"),
                ["runtime-lease-expiry"] = this.Expiry.ToString("O"),
                ["continue-in-background"] = intent.ContinueInBackground,
            }, "Acquire agent session runtime lease", ct).ConfigureAwait(false);
        }

        internal async ValueTask<AgentSessionOwnershipLeasePeriod?> RenewAsync(CancellationToken ct)
        {
            this.ThrowIfPersistenceUncertain();
            await this.gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                this.OwnerLifetime.Token.ThrowIfCancellationRequested();
                var query = await this.AwaitPersistenceAsync(() => this.dataAccessLayer.QueryAsync(new QueryRequest
                {
                    Clauses =
                    [
                        new TopLevelQueryClause
                        {
                            ClauseIdentifier = new QueryClauseIdentifier("agent-session"),
                            Clause = new EntityTypeQueryClause
                            {
                                EntityTypeNames = new EntityTypeNameSet(["agent-session"]),
                            },
                        },
                    ],
                    Timestamps = [null],
                }, ct), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var refreshedEntity = query.Batches.SelectMany(batch => batch.Entities)
                    .SingleOrDefault(candidate => candidate.EntityId == this.entity.EntityId);
                if (query.AuthoritativeTimestamp is not { } timestamp)
                    return null;
                if (refreshedEntity?.Data is not JsonElement refreshed
                    || ReadLong(refreshed, "ownership-generation") != ReadLong(this.data, "ownership-generation")
                    || !string.Equals(
                        ReadString(refreshed, "owning-profile-entity-id") ?? ReadString(refreshed, "host-profile-entity-id"),
                        ReadString(this.data, "owning-profile-entity-id") ?? ReadString(this.data, "host-profile-entity-id"),
                        StringComparison.OrdinalIgnoreCase)
                    || !TryReadRuntimeEpoch(refreshed, out var epoch) || epoch != this.Epoch.Value
                    || !TryReadLeaseExpiry(refreshed, out var persistedExpiry)
                    || timestamp.DateTime >= persistedExpiry || timestamp.DateTime >= this.Expiry
                    || ReadString(refreshed, "runtime-state") is not ("running" or "restarting"))
                {
                    this.OwnerLifetime.Cancel();
                    return null;
                }
                var expiry = timestamp.DateTime + LeaseDuration;
                var updates = new Dictionary<string, object?>
                {
                    ["runtime-lease-expiry"] = expiry.ToString("O"),
                };
                if (this.completionToConfirm is { } completion
                    && completion.Replacement.RuntimeEpoch == this.Epoch)
                {
                    var operations = refreshed.TryGetProperty("restart-operations", out var journal)
                        ? JsonSerializer.Deserialize<Dictionary<string, RestartOperation>>(
                            journal, Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions)
                        : null;
                    if (operations?.GetValueOrDefault(completion.Request.CommandId.ToString("D")) is not { } durable
                        || durable.Request != completion.Request || durable.Replacement != completion.Replacement
                        || durable.Replacement.RuntimeEpoch != this.Epoch
                        || durable.State != AgentSessionOwnerRestartState.Succeeded)
                    {
                        this.OwnerLifetime.Cancel();
                        return null;
                    }
                    foreach (var existing in operations.Values.Where(value =>
                        value.Request.ExpectedRuntime == completion.Request.ExpectedRuntime).ToArray())
                    {
                        if (existing.Replacement != completion.Replacement
                            || existing.State != AgentSessionOwnerRestartState.Succeeded)
                        {
                            this.OwnerLifetime.Cancel();
                            return null;
                        }
                        operations[existing.Request.CommandId.ToString("D")] = existing with { StartupUnconfirmed = false };
                    }
                    updates["restart-operations"] = JsonSerializer.SerializeToElement(
                        operations, Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions);
                }
                // Never merge a renewal onto the acknowledgement-dependent cached tag.
                // Keep query, identity validation and CAS under the same gate as writes.
                this.entity = refreshedEntity;
                this.data = refreshed;
                if (!await this.TryReplaceUnderGateAsync(updates, "Renew agent session runtime lease", ct).ConfigureAwait(false))
                    return null;
                this.authoritativeTime = timestamp.DateTime;
                this.Expiry = expiry;
                return this.LeasePeriod;
            }
            finally
            {
                this.gate.Release();
            }
        }

        internal ValueTask PersistRetentionAsync(bool value, CancellationToken ct)
            => this.ReplaceAsync(new Dictionary<string, object?>
            {
                ["continue-in-background"] = value,
            }, "Update agent session retention", ct);

        internal async ValueTask PersistStoppedAsync(CancellationToken ct)
        {
            var operations = await this.LoadRestartsAsync(ct).ConfigureAwait(false);
            foreach (var operation in operations.Values.ToArray())
                if (operation.State == AgentSessionOwnerRestartState.Succeeded
                    && operation.Replacement.RuntimeEpoch == this.Epoch)
                    operations[operation.Request.CommandId.ToString("D")] = operation with { StartupUnconfirmed = false };
            await this.ReplaceAsync(new Dictionary<string, object?>
            {
                ["runtime-state"] = "stopped",
                ["last-stopped-runtime-epoch"] = this.Epoch.Value.ToString("D"),
                ["runtime-epoch"] = null,
                ["runtime-lease-expiry"] = null,
                ["restart-operations"] = JsonSerializer.SerializeToElement(
                    operations, Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions),
            }, "Stop agent session runtime", ct).ConfigureAwait(false);
        }

        internal async ValueTask ReleaseAsync(CancellationToken ct)
        {
            if (ReadString(this.data, "runtime-epoch") == this.Epoch.Value.ToString("D"))
                await this.PersistStoppedAsync(ct).ConfigureAwait(false);
        }

        private async ValueTask ReplaceAsync(
            IReadOnlyDictionary<string, object?> updates, string comment, CancellationToken ct,
            bool fenceOnTimeout = true)
        {
            if (!await this.TryReplaceAsync(updates, comment, ct, fenceOnTimeout).ConfigureAwait(false))
                throw new AgentSessionConcurrentUpdateException();
        }

        private async ValueTask<bool> TryReplaceAsync(
            IReadOnlyDictionary<string, object?> updates, string comment, CancellationToken ct,
            bool fenceOnTimeout = true)
        {
            this.ThrowIfPersistenceUncertain();
            await this.gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await this.TryReplaceUnderGateAsync(updates, comment, ct, fenceOnTimeout).ConfigureAwait(false);
            }
            finally
            {
                this.gate.Release();
            }
        }

        private async ValueTask<bool> TryReplaceUnderGateAsync(
            IReadOnlyDictionary<string, object?> updates, string comment, CancellationToken ct,
            bool fenceOnTimeout = true)
        {
            this.ThrowIfPersistenceUncertain();
            var replacement = Merge(this.data, updates);
            ct.ThrowIfCancellationRequested();
            var result = await this.AwaitPersistenceAsync(() => this.dataAccessLayer.UpdateAsync(new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = comment } },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = this.entity.EntityId,
                        ConcurrencyTag = this.entity.ConcurrencyTag,
                        Data = replacement,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            }, ct), ct, fenceOnTimeout).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var changed = result.EntityResults.SingleOrDefault();
            if (changed is null || changed.ConcurrencyMatchState == ConcurrencyMatchState.NotMatched)
                return false;
            if (changed.Errors.Count != 0)
                throw new InvalidOperationException("The persisted agent session update was rejected.");
            this.data = replacement;
            this.entity = changed.CurrentEntity ?? this.entity with
            {
                Data = replacement,
                ConcurrencyTag = changed.ConcurrencyTag,
            };
            return true;
        }
    }
}
