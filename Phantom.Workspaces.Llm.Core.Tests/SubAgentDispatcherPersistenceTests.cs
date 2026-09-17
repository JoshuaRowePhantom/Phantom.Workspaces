using System.Collections.ObjectModel;
using System.Text.Json;
using AgentSchema;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Vector;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Testing;

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
        var factory = new RestoringAgentChatFactory();

        var firstUpdated = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var secondUpdated = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(firstUpdated);
        var fixture = await ValidatingEntitySeedFixture.CreateAsync(timeProvider, timeout.Token);
        var dispatcherEntityId = await SeedAgentSessionAsync(
            fixture,
            dispatcherEntityName,
            "dispatcher-session",
            description: null,
            parentEntityId: null);

        await SeedAgentSessionAsync(
            fixture,
            dispatcherEntityName,
            id: "alpha",
            description: "first sub-agent",
            sessionId: "session-alpha",
            parentEntityId: dispatcherEntityId);
        timeProvider.SetUtcNow(secondUpdated);
        await SeedAgentSessionAsync(
            fixture,
            dispatcherEntityName,
            id: "beta",
            description: "second sub-agent",
            sessionId: "session-beta",
            parentEntityId: dispatcherEntityId);

        var client = new SubAgentDispatcherChatClient(
            factory,
            new DeterministicEmbeddingsProvider(),
            fixture.DataAccessLayer,
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
        var fixture = await ValidatingEntitySeedFixture.CreateAsync(timeout.Token);
        _ = await SeedAgentSessionAsync(
            fixture,
            dispatcherEntityName,
            "dispatcher-session",
            description: null,
            parentEntityId: null);
        var factory = new RestoringAgentChatFactory();

        var client = new SubAgentDispatcherChatClient(
            factory,
            new DeterministicEmbeddingsProvider(),
            fixture.DataAccessLayer,
            dispatcherEntityName,
            CreateOptions());

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.User, "new: persist me"),
        };

        await foreach (var _ in client.GetStreamingResponseAsync(messages, cancellationToken: timeout.Token))
        {
        }

        var children = await fixture.DataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = dispatcherEntityName,
                        EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
                    },
                ],
            },
            timeout.Token);
        var write = Assert.Single(
            children.Batches.SelectMany(static batch => batch.Entities),
            entity => entity.Data is { } data && data.TryGetProperty("sub-agent-description", out _)).Data!.Value;

        Assert.True(write.TryGetProperty("parent-agent-session-ids", out var parents));
        Assert.Equal(JsonValueKind.Array, parents.ValueKind);
        Assert.True(parents.GetArrayLength() >= 1);

        Assert.True(write.TryGetProperty("entity-types", out var types));
        var typeValues = types.EnumerateArray().Select(t => t.GetString()).ToArray();
        Assert.Contains("agent-session", typeValues);

        client.Dispose();
    }

    private static async Task<EntityId> SeedAgentSessionAsync(
        ValidatingEntitySeedFixture fixture,
        EntityName dispatcherName,
        string sessionId,
        string? description,
        EntityId? parentEntityId,
        string? id = null)
    {
        var entityId = new EntityId();
        var name = id is null
            ? dispatcherName
            : new EntityName([.. dispatcherName.Components, id]);
        var data = new Dictionary<string, object?>
        {
            ["entity-id"] = entityId.ToString(),
            ["entity-types"] = new[] { "entity", "agent-session" },
            ["names"] = new[] { name.Components },
            ["display-name"] = new Dictionary<string, object?> { ["default"] = id ?? "dispatcher" },
            ["agent-session-id"] = sessionId,
        };
        if (description is not null)
        {
            data["sub-agent-description"] = description;
        }

        if (parentEntityId is { } parent)
        {
            data["parent-agent-session-ids"] = new[] { parent.ToString() };
        }

        await fixture.SeedValidEntityAsync(JsonSerializer.SerializeToElement(data));
        return entityId;
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

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, bool registerAsRunningAgent = true, CancellationToken ct = default)
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
