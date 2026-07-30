using System.Collections.ObjectModel;
using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Vector;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class SubAgentDispatcherPersistenceTests
{
    private const string EchoAgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "echo-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
          "tools": []
        }
        """;

    private static AgentDefinitionTool CreateDefaultTool() => new()
    {
        Name = "default",
        Description = "Default echo agent",
        Definition = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentDefinitionJson),
    };

    private static SubAgentDispatcherOptions CreateOptions() =>
        new() { AgentDefinitionTools = [CreateDefaultTool()] };

    [Fact]
    public async Task RestoreSubAgentsAsync_RebuildsSubAgentsFromChildEntities()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var dispatcherEntityName = new EntityName("dispatchers", "test-dispatcher");
        var dataAccessLayer = new RecordingDataAccessLayer();
        var factory = new RestoringAgentChatFactory();

        var firstUpdated = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var secondUpdated = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

        dataAccessLayer.SeedChild(
            dispatcherEntityName,
            id: "alpha",
            description: "first sub-agent",
            sessionId: "session-alpha",
            modifiedTime: firstUpdated);
        dataAccessLayer.SeedChild(
            dispatcherEntityName,
            id: "beta",
            description: "second sub-agent",
            sessionId: "session-beta",
            modifiedTime: secondUpdated);

        var client = new SubAgentDispatcherChatClient(
            factory,
            new DeterministicEmbeddingsProvider(),
            dataAccessLayer,
            dispatcherEntityName,
            CreateOptions());

        await client.RestoreSubAgentsAsync(timeout.Token);

        var snapshots = client.GetSubAgentSnapshotsForTest()
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(2, snapshots.Count);

        Assert.Equal("alpha", snapshots[0].Id);
        Assert.Equal("first sub-agent", snapshots[0].Description);
        Assert.Equal(firstUpdated, snapshots[0].LastUpdated);

        Assert.Equal("beta", snapshots[1].Id);
        Assert.Equal("second sub-agent", snapshots[1].Description);
        Assert.Equal(secondUpdated, snapshots[1].LastUpdated);

        // Both sessions should have been re-leased through the factory.
        Assert.Contains(new AgentSessionId("session-alpha"), factory.LeasedSessions);
        Assert.Contains(new AgentSessionId("session-beta"), factory.LeasedSessions);

        client.Dispose();
    }

    [Fact]
    public async Task PersistSubAgent_OnCreate_WritesChildEntityWithParentReference()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var dispatcherEntityName = new EntityName("dispatchers", "test-dispatcher");
        var dataAccessLayer = new RecordingDataAccessLayer();
        var factory = new RestoringAgentChatFactory();

        var client = new SubAgentDispatcherChatClient(
            factory,
            new DeterministicEmbeddingsProvider(),
            dataAccessLayer,
            dispatcherEntityName,
            CreateOptions());

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.User, "new: persist me"),
        };

        await foreach (var _ in client.GetStreamingResponseAsync(messages, cancellationToken: timeout.Token))
        {
        }

        var write = Assert.Single(
            dataAccessLayer.WrittenEntities.Values,
            e => e.TryGetProperty("sub-agent-description", out _));

        Assert.True(write.TryGetProperty("parent-agent-session-ids", out var parents));
        Assert.Equal(JsonValueKind.Array, parents.ValueKind);
        Assert.True(parents.GetArrayLength() >= 1);

        Assert.True(write.TryGetProperty("entity-types", out var types));
        var typeValues = types.EnumerateArray().Select(t => t.GetString()).ToArray();
        Assert.Contains("agent-session", typeValues);

        client.Dispose();
    }

    /// <summary>
    /// A data access layer that stores replaced entities in memory and supports child enumeration
    /// by name prefix, mirroring the behaviour the dispatcher relies on for restore.
    /// </summary>
    private sealed class RecordingDataAccessLayer : IDataAccessLayer
    {
        private readonly Dictionary<EntityId, StoredEntity> _entities = new();

        public IReadOnlyDictionary<EntityId, JsonElement> WrittenEntities =>
            _entities.ToDictionary(e => e.Key, e => e.Value.Data);

        public void SeedChild(
            EntityName dispatcherName,
            string id,
            string description,
            string sessionId,
            DateTimeOffset modifiedTime)
        {
            var entityId = new EntityId(Guid.NewGuid());
            var name = new EntityName([.. dispatcherName.Components, id]);
            var data = new Dictionary<string, object?>
            {
                ["entity-id"] = entityId.ToString(),
                ["entity-types"] = new[] { "entity", "agent-session" },
                ["names"] = new[] { name.Components },
                ["display-name"] = new Dictionary<string, object?> { ["default"] = id },
                ["agent-session-id"] = sessionId,
                ["sub-agent-description"] = description,
                ["parent-agent-session-ids"] = new[] { Guid.NewGuid().ToString("D") },
            };

            _entities[entityId] = new StoredEntity(
                entityId,
                name,
                JsonSerializer.SerializeToElement(data),
                new Timestamp(modifiedTime, "seed"));
        }

        public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
        {
            var results = new List<EntityUpdateResult>();
            foreach (var change in request.Changes)
            {
                var entityId = change.EntityId ?? new EntityId(Guid.NewGuid());
                if (change.Data is { } data)
                {
                    var name = ReadName(data);
                    _entities[entityId] = new StoredEntity(
                        entityId,
                        name,
                        data.Clone(),
                        new Timestamp(DateTimeOffset.UtcNow, "write"));
                }

                results.Add(new EntityUpdateResult
                {
                    UpdateState = UpdateState.Updated,
                    RequestedEntityId = entityId,
                    ResultingEntityId = entityId,
                    ConcurrencyMatchState = ConcurrencyMatchState.Matched,
                    Errors = [],
                });
            }

            return Task.FromResult(new UpdateResult { EntityResults = results });
        }

        public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
        {
            var matched = new List<EntitySnapshot>();
            foreach (var entityRequest in request.Entities)
            {
                foreach (var stored in _entities.Values)
                {
                    if (entityRequest.EntityId is { } requestedId && stored.Id != requestedId)
                    {
                        continue;
                    }

                    if (entityRequest.EntityName is { } requestedName
                        && !MatchesName(stored.Name, requestedName, entityRequest.EnumerateChildren))
                    {
                        continue;
                    }

                    matched.Add(new EntitySnapshot
                    {
                        EntityId = stored.Id,
                        ModifiedTime = stored.ModifiedTime,
                        Data = stored.Data,
                        Relationships = [],
                    });
                }
            }

            return Task.FromResult(new GetResult
            {
                Batches = [new TimestampedEntityBatch { Entities = matched }],
            });
        }

        private static bool MatchesName(EntityName candidate, EntityName requested, EnumerateChildrenAction enumerate)
        {
            var candidateComponents = candidate.Components;
            var requestedComponents = requested.Components;
            if (!candidateComponents.Take(requestedComponents.Length).SequenceEqual(requestedComponents, StringComparer.Ordinal))
            {
                return false;
            }

            return enumerate switch
            {
                EnumerateChildrenAction.EnumerateSelf => candidateComponents.Length == requestedComponents.Length,
                EnumerateChildrenAction.EnumerateChildren => candidateComponents.Length == requestedComponents.Length + 1,
                EnumerateChildrenAction.EnumerateAllChildren => candidateComponents.Length > requestedComponents.Length,
                _ => false,
            };
        }

        private static EntityName ReadName(JsonElement data)
        {
            if (data.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in names.EnumerateArray())
                {
                    if (name.ValueKind == JsonValueKind.Array)
                    {
                        return new EntityName(name.EnumerateArray().Select(c => c.GetString() ?? string.Empty).ToArray());
                    }
                }
            }

            return EntityName.Root;
        }

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new QueryResult { Batches = [] });

        public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new GetHistoryResult { History = [] });

#pragma warning disable CS0618 // Type or member is obsolete
        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExportResult { ChangeBatches = [], FinalSnapshotTime = new Timestamp(DateTimeOffset.UtcNow, "fake") });
#pragma warning restore CS0618

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new GetChangedEntitiesResult { Entities = [] });

        private sealed record StoredEntity(EntityId Id, EntityName Name, JsonElement Data, Timestamp ModifiedTime);
    }

    /// <summary>
    /// A factory whose <see cref="GetAsync"/> creates a lease on demand, simulating loading a
    /// persisted session on restart.
    /// </summary>
    private sealed class RestoringAgentChatFactory : IRunningAgentChatFactory
    {
        public Dictionary<AgentSessionId, RunningAgentChatLease> Leases { get; } = new();
        public HashSet<AgentSessionId> LeasedSessions { get; } = new();
        public ObservableCollection<RunningAgentChat> RunningSessions { get; } = new();

        public async Task<RunningAgentChatLease> GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition = null,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            bool registerAsRunningAgent = true, CancellationToken ct = default)
        {
            if (Leases.TryGetValue(sessionId, out var existing))
            {
                return existing;
            }

            var agentDefinition = definition ?? AgentDefinitionLoader.LoadAgentFromJson(EchoAgentDefinitionJson);
            var store = new InMemoryAgentPersistenceStore();
            var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
            {
                AgentDefinition = agentDefinition,
                ConfiguredStore = store,
                DisplayNameOverride = displayNameOverride ?? "test-agent",
                DescriptionOverride = descriptionOverride,
            });

            var lease = new RunningAgentChatLease(sessionId, chat, () => ValueTask.CompletedTask);
            Leases[sessionId] = lease;
            LeasedSessions.Add(sessionId);
            return lease;
        }

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, CancellationToken ct = default)
            => GetOrCreateAsync(sessionId, ct: ct);

        public Task<RunningAgentChatLease> CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            string? nameOverride = null, CancellationToken ct = default)
            => GetOrCreateAsync(sessionId, definition, services, displayNameOverride, descriptionOverride, ct: ct);
    }
}
