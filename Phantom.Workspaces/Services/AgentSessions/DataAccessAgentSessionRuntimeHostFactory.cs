using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Remote;

namespace Phantom.Workspaces.Services.AgentSessions;

internal sealed class DataAccessAgentSessionRuntimeHostFactory : IAgentSessionRuntimeHostFactory
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

    public async Task<RemoteAgentSessionLease> StartAsync(
        PersistedAgentSessionRuntimeIntent intent, CancellationToken ct)
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
            this.dataAccessLayer, persisted, query.GetRequiredAuthoritativeTime());
        await state.RecordInterruptedAndAcquireAsync(intent, ct).ConfigureAwait(false);
        RunningAgentChatLease? chatLease = null;
        try
        {
            chatLease = await this.runningChats.AcquireAsync(new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId(intent.AgentSessionId),
                AgentSessionEntity = data,
                AgentServices = (this.hostServices ?? new AgentServices()) with
                {
                    CurrentSessionContext = new CurrentSessionContext
                    {
                        AgentSessionId = intent.AgentSessionId,
                        OwningProfileEntityId = intent.OwningProfileEntityId,
                        OwnershipGeneration = intent.OwnershipGeneration,
                        RuntimeEpoch = state.Epoch,
                    },
                },
                AgentDefinitionResolver = this.definitionResolver,
                EntityId = persisted.EntityId.ToString(),
                EntityName = intent.AgentSessionId,
                AcquisitionMode = AgentChatAcquisitionMode.Local,
            }, ct).ConfigureAwait(false);

            var chat = chatLease.AgentChat;
            RemoteAgentSessionLease? runtime = null;
            var ownership = new AgentSessionOwnershipLease(
                this.timeProvider,
                token => state.RenewAsync(token),
                async token =>
                {
                    if (runtime is not null)
                        await runtime.ReportFatalFailureAsync(token).ConfigureAwait(false);
                },
                token => state.ReleaseAsync(token));
            runtime = new RemoteAgentSessionLease(
                intent.AgentSessionId,
                intent.OwnershipGeneration,
                state.Epoch,
                chat,
                intent.ContinueInBackground,
                () => Snapshot(chat),
                (value, token) => state.PersistRetentionAsync(value, token),
                token => state.PersistStoppedAsync(token),
                this.timeProvider,
                ownership,
                chatLease);
            ownership.Start(state.LeasePeriod);
            return runtime;
        }
        catch
        {
            if (chatLease is not null)
                await chatLease.DisposeAsync().ConfigureAwait(false);
            await state.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<bool> TryTakeOverAsync(
        AgentSessionTakeoverRequest request, CancellationToken ct)
    {
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
        History = chat.History.Select(item => JsonSerializer.SerializeToElement(item)).ToArray(),
        RunningItems = chat.RunningItems.Select(item => JsonSerializer.SerializeToElement(item)).ToArray(),
        Tools = chat.GetToolSnapshot().Select(item => JsonSerializer.SerializeToElement(item)).ToArray(),
        Subagents = chat.SubAgents.Select(item => JsonSerializer.SerializeToElement(item, item.GetType())).ToArray(),
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

    private sealed class PersistedRuntimeState
    {
        private readonly IDataAccessLayer dataAccessLayer;
        private readonly SemaphoreSlim gate = new(1, 1);
        private EntitySnapshot entity;
        private JsonElement data;
        private DateTimeOffset authoritativeTime;

        internal PersistedRuntimeState(
            IDataAccessLayer dataAccessLayer,
            EntitySnapshot entity,
            DateTimeOffset authoritativeTime)
        {
            this.dataAccessLayer = dataAccessLayer;
            this.entity = entity;
            this.data = entity.Data!.Value;
            this.authoritativeTime = authoritativeTime;
        }

        internal RuntimeEpoch Epoch { get; private set; }
        internal DateTimeOffset Expiry { get; private set; }
        internal AgentSessionOwnershipLeasePeriod LeasePeriod
            => new(this.authoritativeTime, this.Expiry);

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
            var query = await this.dataAccessLayer.QueryAsync(new QueryRequest
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
            if (query.AuthoritativeTimestamp is not { } timestamp)
                return null;
            this.authoritativeTime = timestamp.DateTime;
            this.Expiry = timestamp.DateTime + LeaseDuration;
            return await this.TryReplaceAsync(new Dictionary<string, object?>
            {
                ["runtime-lease-expiry"] = this.Expiry.ToString("O"),
            }, "Renew agent session runtime lease", ct).ConfigureAwait(false)
                ? this.LeasePeriod
                : null;
        }

        internal ValueTask PersistRetentionAsync(bool value, CancellationToken ct)
            => this.ReplaceAsync(new Dictionary<string, object?>
            {
                ["continue-in-background"] = value,
            }, "Update agent session retention", ct);

        internal ValueTask PersistStoppedAsync(CancellationToken ct)
            => this.ReplaceAsync(new Dictionary<string, object?>
            {
                ["runtime-state"] = "stopped",
                ["last-stopped-runtime-epoch"] = this.Epoch.Value.ToString("D"),
                ["runtime-epoch"] = null,
                ["runtime-lease-expiry"] = null,
            }, "Stop agent session runtime", ct);

        internal async ValueTask ReleaseAsync(CancellationToken ct)
        {
            if (ReadString(this.data, "runtime-epoch") == this.Epoch.Value.ToString("D"))
                await this.PersistStoppedAsync(ct).ConfigureAwait(false);
        }

        private async ValueTask ReplaceAsync(
            IReadOnlyDictionary<string, object?> updates, string comment, CancellationToken ct)
        {
            if (!await this.TryReplaceAsync(updates, comment, ct).ConfigureAwait(false))
                throw new InvalidOperationException("The persisted agent session changed concurrently.");
        }

        private async ValueTask<bool> TryReplaceAsync(
            IReadOnlyDictionary<string, object?> updates, string comment, CancellationToken ct)
        {
            await this.gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var replacement = Merge(this.data, updates);
                var result = await this.dataAccessLayer.UpdateAsync(new UpdateRequest
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
                }, ct).ConfigureAwait(false);
                var changed = result.EntityResults.SingleOrDefault();
                if (changed is null || changed.Errors.Count != 0
                    || changed.ConcurrencyMatchState == ConcurrencyMatchState.NotMatched)
                    return false;
                this.data = replacement;
                this.entity = changed.CurrentEntity ?? this.entity with
                {
                    Data = replacement,
                    ConcurrencyTag = changed.ConcurrencyTag,
                };
                return true;
            }
            finally
            {
                this.gate.Release();
            }
        }
    }
}
