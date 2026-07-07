using AgentSchema;
using MongoDB.Bson;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using System.Linq;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentChatResumeTests
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

    private static AgentDefinition EchoAgentDefinition =>
        AgentDefinitionLoader.LoadAgentFromJson(EchoAgentDefinitionJson);

    /// <summary>
    /// Stores <paramref name="count"/> child sessions under <paramref name="parentSessionId"/>
    /// and returns their session ID strings.
    /// </summary>
    private static async Task<string[]> StoreChildrenAsync(
        InMemoryAgentPersistenceStore store,
        string parentSessionId,
        int count)
    {
        var childIds = new string[count];
        for (var i = 0; i < count; i++)
        {
            childIds[i] = $"resume-child-{i}";
            await store.StoreAsync(new StoreRequestAgent
            {
                Agent = new PersistedAgent
                {
                    AgentSessionId = childIds[i],
                    AgentDefinitionJson = BsonDocument.Parse(EchoAgentDefinition.ToJson()),
                }
            });
            await store.AddSubAgentLinkAsync(parentSessionId, childIds[i]);
        }
        return childIds;
    }

    private static async Task<AgentChat> CreateRestoredParentAsync(
        InMemoryAgentPersistenceStore store,
        string parentSessionId,
        AgentServices? services = null,
        TaskScheduler? foregroundScheduler = null) =>
        await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = EchoAgentDefinition,
            AgentSessionId = parentSessionId,
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "restored-parent",
            AgentServices = services,
            ForegroundScheduler = foregroundScheduler,
        });

    private static AgentChatFactory CreateFactory(InMemoryAgentPersistenceStore store) =>
        new(store, new AgentServices { ChatClientOverride = new DeterministicTestChatClient() }, TaskScheduler.Default);

    /// <summary>
    /// A <see cref="TaskScheduler"/> that queues tasks without executing them until
    /// <see cref="Drain"/> is called. Enables deterministic verification that mutations
    /// are scheduled (not run inline) and provides explicit drain control.
    /// </summary>
    private sealed class CapturingTaskScheduler : TaskScheduler
    {
        private readonly List<Task> _queue = [];

        public int QueuedCount => _queue.Count;

        public void Drain()
        {
            while (_queue.Count > 0)
            {
                var tasks = _queue.ToList();
                _queue.Clear();
                foreach (var task in tasks)
                    TryExecuteTask(task);
            }
        }

        protected override IEnumerable<Task>? GetScheduledTasks() => _queue;
        protected override void QueueTask(Task task) => _queue.Add(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }

    [Fact]
    public async Task AgentChat_Resume_CreatesLazySubAgentStubsWithoutCreatingAgentChat()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-lazy";
        await StoreChildrenAsync(store, parentSessionId, 1);

        var scheduler = new CapturingTaskScheduler();
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services, scheduler);
        scheduler.Drain();

        var stub = Assert.IsType<SubAgent>(Assert.Single(parent.SubAgents));
        Assert.Null(stub.AgentChat);
    }

    [Fact]
    public async Task AgentChat_Resume_SubAgentCount_MatchesStoredLinks()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-count";
        await StoreChildrenAsync(store, parentSessionId, 2);

        var scheduler = new CapturingTaskScheduler();
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services, scheduler);
        scheduler.Drain();

        Assert.Equal(2, parent.SubAgents.Count);
    }

    [Fact]
    public async Task AgentChat_Resume_EachStubHasCorrectSessionId()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-ids";
        var childIds = await StoreChildrenAsync(store, parentSessionId, 2);

        var scheduler = new CapturingTaskScheduler();
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services, scheduler);
        scheduler.Drain();

        var stubSessionIds = parent.SubAgents.Cast<SubAgent>().Select(s => s.SessionId.Value).ToList();
        Assert.Contains(childIds[0], stubSessionIds);
        Assert.Contains(childIds[1], stubSessionIds);
    }

    [Fact]
    public async Task AgentChat_Resume_NoFactory_SubAgentsIsEmpty()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-nofactory";
        await StoreChildrenAsync(store, parentSessionId, 2);

        var scheduler = new CapturingTaskScheduler();
        // No factory in services
        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services: null, scheduler);
        scheduler.Drain();

        Assert.Empty(parent.SubAgents);
    }

    [Fact]
    public async Task AgentChat_Resume_MultipleChildren_EachLoadedIndependently()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-multi";
        var childIds = await StoreChildrenAsync(store, parentSessionId, 2);

        var scheduler = new CapturingTaskScheduler();
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services, scheduler);
        scheduler.Drain();

        var stubs = parent.SubAgents.Cast<SubAgent>().ToList();

        await using var lease0 = await stubs.First(s => s.SessionId.Value == childIds[0]).AcquireLeaseAsync();
        await using var lease1 = await stubs.First(s => s.SessionId.Value == childIds[1]).AcquireLeaseAsync();

        Assert.Equal(childIds[0], lease0.AgentChat.AgentSessionId);
        Assert.Equal(childIds[1], lease1.AgentChat.AgentSessionId);
        Assert.NotSame(lease0.AgentChat, lease1.AgentChat);
    }

    [Fact]
    public async Task AgentChat_Resume_SubAgentsAddedOnForegroundScheduler()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-scheduler";
        await StoreChildrenAsync(store, parentSessionId, 1);

        var scheduler = new CapturingTaskScheduler();
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services, scheduler);

        // Before draining: stub is queued but not yet in SubAgents
        Assert.Empty(parent.SubAgents);

        scheduler.Drain();

        // After draining: stub is present
        Assert.Single(parent.SubAgents);
    }
}
