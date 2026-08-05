using AgentSchema;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using System.Collections.Specialized;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class ISubAgentTableTests
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

    private static AgentChat CreateParentChat(
        IAgentPersistenceStore? store = null,
        AgentServices? services = null,
        TaskScheduler? foregroundScheduler = null)
    {
        store ??= new InMemoryAgentPersistenceStore();
        var createTask = AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = EchoAgentDefinition,
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "parent-chat",
            AgentServices = services,
            ForegroundScheduler = foregroundScheduler ?? TaskScheduler.Default,
        });

        // Initialization now unconditionally dispatches session init onto the foreground scheduler
        // and awaits it (issue #1100). A CapturingTaskScheduler only runs work when driven, so run
        // the queued init task here to let creation complete; work it queues as a side effect (e.g.
        // sub-agent stub adds, the processing-loop start) stays pending for the test to drain.
        if (foregroundScheduler is CapturingTaskScheduler capturing)
        {
            capturing.RunPending();
        }

        return createTask.GetAwaiter().GetResult();
    }

    private static AgentChat CreateChildChat(IAgentPersistenceStore? store = null)
    {
        store ??= new InMemoryAgentPersistenceStore();
        return AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = EchoAgentDefinition,
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "child-chat",
            ForegroundScheduler = TaskScheduler.Default,
        }).GetAwaiter().GetResult();
    }

    private static AgentChatFactory CreateFactory(
        IAgentPersistenceStore? store = null,
        DeterministicTestChatClient? client = null)
    {
        store ??= new InMemoryAgentPersistenceStore();
        client ??= new DeterministicTestChatClient();
        var services = new AgentServices { ChatClientOverride = client };
        return new AgentChatFactory(store, services, TaskScheduler.Default);
    }

    /// <summary>
    /// A <see cref="TaskScheduler"/> that queues tasks without running them until
    /// <see cref="Drain"/> is called. Used to verify that mutations are scheduled
    /// (not run inline) and to drain the scheduler deterministically.
    /// </summary>
    private sealed class CapturingTaskScheduler : TaskScheduler
    {
        private readonly List<Task> _queue = [];

        public int QueuedCount => _queue.Count;

        /// <summary>
        /// Executes all queued tasks (including any recursively queued by executed tasks)
        /// until the queue is empty.
        /// </summary>
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

        /// <summary>
        /// Executes the tasks currently queued in a single pass, leaving any tasks they queue as a
        /// side effect pending. Used to drive AgentChat initialization to completion without also
        /// running the init-queued mutations, so the test retains control of when those run.
        /// </summary>
        public void RunPending()
        {
            var tasks = _queue.ToList();
            _queue.Clear();
            foreach (var task in tasks)
                TryExecuteTask(task);
        }

        protected override IEnumerable<Task>? GetScheduledTasks() => _queue;
        protected override void QueueTask(Task task) => _queue.Add(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }

    [Fact]
    public async Task Add_ReturnsSubAgent()
    {
        await using var parent = CreateParentChat();
        await using var child = CreateChildChat();

        var subAgent = await ((ISubAgentTable)parent).Add(child);

        Assert.NotNull(subAgent);
    }

    [Fact]
    public async Task Add_SubAgent_HasCorrectSessionId()
    {
        await using var parent = CreateParentChat();
        await using var child = CreateChildChat();

        var subAgent = await ((ISubAgentTable)parent).Add(child);

        Assert.Equal(child.AgentSessionId, subAgent.SessionId.Value);
    }

    [Fact]
    public async Task ISubAgentTable_Add_ReturnsSubAgentWithCorrectAgentChat()
    {
        await using var parent = CreateParentChat();
        await using var child = CreateChildChat();

        var subAgent = await ((ISubAgentTable)parent).Add(child);

        Assert.Same(child, subAgent.AgentChat);
    }

    [Fact]
    public async Task ISubAgentTable_Add_AppearsInSubAgents()
    {
        var scheduler = new CapturingTaskScheduler();
        await using var parent = CreateParentChat(foregroundScheduler: scheduler);
        await using var child = CreateChildChat();

        var subAgent = await ((ISubAgentTable)parent).Add(child);
        scheduler.Drain();

        Assert.Contains(subAgent, parent.SubAgents);
    }

    [Fact]
    public async Task Add_AppendsToSubAgentsCollection()
    {
        var scheduler = new CapturingTaskScheduler();
        await using var parent = CreateParentChat(foregroundScheduler: scheduler);
        await using var child = CreateChildChat();

        var subAgent = await ((ISubAgentTable)parent).Add(child);
        scheduler.Drain();

        Assert.Single(parent.SubAgents);
        Assert.Same(subAgent, parent.SubAgents[0]);
    }

    [Fact]
    public async Task Add_AppendsToSubAgents_OnForegroundScheduler()
    {
        var scheduler = new CapturingTaskScheduler();
        await using var parent = CreateParentChat(foregroundScheduler: scheduler);
        await using var child = CreateChildChat();

        // Drain initialization tasks (including the processing loop startup).
        scheduler.Drain();

        // Mutation must be queued on the foreground scheduler, not applied inline.
        _ = ((ISubAgentTable)parent).Add(child);
        Assert.Equal(1, scheduler.QueuedCount);
        Assert.Empty(parent.SubAgents);

        scheduler.Drain();
        Assert.Single(parent.SubAgents);
    }

    [Fact]
    public async Task SubAgents_CollectionChanged_FiresOnForegroundThread()
    {
        var scheduler = new CapturingTaskScheduler();
        await using var parent = CreateParentChat(foregroundScheduler: scheduler);
        await using var child = CreateChildChat();

        // Drain initialization tasks first.
        scheduler.Drain();

        bool collectionChangedFired = false;
        ((INotifyCollectionChanged)parent.SubAgents).CollectionChanged += (_, _) =>
        {
            collectionChangedFired = true;
        };

        _ = ((ISubAgentTable)parent).Add(child);

        // Before draining the scheduler, CollectionChanged must not have fired.
        Assert.False(collectionChangedFired);

        scheduler.Drain();

        // After draining (which runs on the capturing scheduler), it should have fired.
        Assert.True(collectionChangedFired);
    }

    [Fact]
    public async Task Add_DuplicateSessionId_ThrowsInvalidOperationException()
    {
        await using var parent = CreateParentChat();
        await using var child = CreateChildChat();

        await ((ISubAgentTable)parent).Add(child);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await ((ISubAgentTable)parent).Add(child));
    }

    [Fact]
    public async Task SubAgent_AcquireLeaseAsync_DelegatesToChildAgentChat()
    {
        var store = new InMemoryAgentPersistenceStore();
        var sessionId = new AgentSessionId("child-session-1");
        var definitionJson = BsonDocument.Parse(EchoAgentDefinition.ToJson());
        await store.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = sessionId.Value,
                AgentDefinitionJson = definitionJson,
            }
        });

        await using var factory = CreateFactory(store: store);
        await using var childLease = await factory.CreateAsync(EchoAgentDefinition, sessionId);
        var childChat = childLease.AgentChat;

        var factoryServices = new AgentServices { RunningAgentChatFactory = factory };
        await using var parent = CreateParentChat(services: factoryServices);

        var subAgent = await ((ISubAgentTable)parent).Add(childChat);

        await using var lease = await subAgent.AcquireLeaseAsync();

        Assert.Same(childChat, lease.AgentChat);
    }

    [Fact]
    public async Task AgentChat_GetService_ISubAgentTable_ReturnsSelf()
    {
        await using var parent = CreateParentChat();

        var result = parent.GetService(typeof(ISubAgentTable));

        Assert.Same(parent, result);
    }

    [Fact]
    public async Task ISubAgentTable_Add_PersistsParentChildLink()
    {
        var store = new InMemoryAgentPersistenceStore();
        await using var parent = CreateParentChat(store: store);
        await using var child = CreateChildChat();

        await ((ISubAgentTable)parent).Add(child);

        var childIds = await store.ReadSubAgentChildIdsAsync(parent.AgentSessionId);
        var childId = Assert.Single(childIds);
        Assert.Equal(child.AgentSessionId, childId.Value);
    }
}
