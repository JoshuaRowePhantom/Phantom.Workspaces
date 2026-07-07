using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Interfaces;
using Xunit;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentChatPersistenceTests
{
    private static readonly string ParentAgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "parent-agent",
          "model": {
            "id": "echo",
            "provider": "echo",
            "apiType": "Echo"
          },
          "tools": []
        }
        """;

    private static readonly string SubAgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "sub-agent",
          "model": {
            "id": "echo",
            "provider": "echo",
            "apiType": "Echo"
          },
          "tools": []
        }
        """;

    private static AgentDefinition ParentDefinition =>
        AgentDefinitionLoader.LoadAgentFromJson(ParentAgentDefinitionJson);

    private static AgentDefinition SubDefinition =>
        AgentDefinitionLoader.LoadAgentFromJson(SubAgentDefinitionJson);

    private static async Task<AgentChat> CreateParentChatAsync(
        InMemoryAgentPersistenceStore store,
        string? agentSessionId = null,
        AgentServices? services = null,
        TaskScheduler? foregroundScheduler = null) =>
        await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = ParentDefinition,
            AgentSessionId = agentSessionId,
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "parent",
            AgentServices = services,
            ForegroundScheduler = foregroundScheduler,
        });

    private static AgentChatFactory CreateFactory(InMemoryAgentPersistenceStore store) =>
        new(store, new AgentServices { ChatClientOverride = new DeterministicTestChatClient() }, TaskScheduler.Default);

    /// <summary>
    /// Queues tasks without executing them until <see cref="Drain"/> is called.
    /// </summary>
    private sealed class CapturingTaskScheduler : TaskScheduler
    {
        private readonly List<Task> _queue = [];
        // After Drain() is called, tasks are executed inline immediately when queued.
        // This prevents a deadlock during AgentChat.DisposeAsync: when the CTS is
        // cancelled, RunProcessLoopAsync's continuation is queued here; without
        // auto-drain that continuation would never run and processTask would hang.
        private volatile bool _autoDrain;

        public void Drain()
        {
            while (_queue.Count > 0)
            {
                var tasks = _queue.ToList();
                _queue.Clear();
                foreach (var task in tasks)
                    TryExecuteTask(task);
            }
            _autoDrain = true;
        }

        protected override IEnumerable<Task>? GetScheduledTasks() => _queue;
        protected override void QueueTask(Task task)
        {
            if (_autoDrain)
                TryExecuteTask(task);
            else
                _queue.Add(task);
        }
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }

    [Fact]
    public async Task GetOrCreateAsync_AddsSubAgentLink()
    {
        var store = new InMemoryAgentPersistenceStore();
        await using var parent = await CreateParentChatAsync(store);

        await parent.GetOrCreateAsync("agent-1", SubDefinition, "tool-call-1");

        var childIds = await store.ReadSubAgentChildIdsAsync(parent.AgentSessionId);
        Assert.Single(childIds);
    }

    [Fact]
    public async Task InitializeAsync_RestoresSubAgents_FromManifest()
    {
        var store = new InMemoryAgentPersistenceStore();
        string parentSessionId;

        await using (var parent = await CreateParentChatAsync(store))
        {
            var sink = (ISubAgentChat)await parent.GetOrCreateAsync("agent-1", SubDefinition, "tool-call-1");
            sink.Complete();
            await Task.Yield();
            parentSessionId = parent.AgentSessionId;
        }

        var scheduler = new CapturingTaskScheduler();
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };
        await using var restoredParent = await CreateParentChatAsync(store, parentSessionId, services, scheduler);
        scheduler.Drain();

        Assert.Single(restoredParent.SubAgents);
    }

    [Fact]
    public async Task InitializeAsync_RestoredSubAgent_HasCorrectAgentDefinition()
    {
        var store = new InMemoryAgentPersistenceStore();
        string parentSessionId;

        await using (var parent = await CreateParentChatAsync(store))
        {
            var sink = (ISubAgentChat)await parent.GetOrCreateAsync("agent-1", SubDefinition, "tool-call-1");
            sink.Complete();
            await Task.Yield();
            parentSessionId = parent.AgentSessionId;
        }

        var scheduler = new CapturingTaskScheduler();
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };
        await using var restoredParent = await CreateParentChatAsync(store, parentSessionId, services, scheduler);
        scheduler.Drain();

        var stub = Assert.IsType<SubAgent>(Assert.Single(restoredParent.SubAgents));
        await using var lease = await stub.AcquireLeaseAsync();
        Assert.Equal("sub-agent", lease.AgentChat.AgentDefinition?.Name);
    }

    [Fact]
    public async Task InitializeAsync_RestoredSubAgent_ChatHistoryLoaded()
    {
        var store = new InMemoryAgentPersistenceStore();
        string parentSessionId;
        string childSessionId;

        await using (var parent = await CreateParentChatAsync(store))
        {
            var sink = (ISubAgentChat)await parent.GetOrCreateAsync("agent-1", SubDefinition, "tool-call-1");
            childSessionId = ((AgentChat)Assert.Single(parent.SubAgents)).AgentSessionId;

            // Write a message into the child's session in the store
            await store.StoreAsync(new StoreRequestAgent
            {
                Agent = new PersistedAgent { AgentSessionId = childSessionId },
                NewMessages = [new ChatMessage(ChatRole.User, "hello from history")],
            });

            sink.Complete();
            await Task.Yield();
            parentSessionId = parent.AgentSessionId;
        }

        var scheduler = new CapturingTaskScheduler();
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };
        await using var restoredParent = await CreateParentChatAsync(store, parentSessionId, services, scheduler);
        scheduler.Drain();

        var stub = Assert.IsType<SubAgent>(Assert.Single(restoredParent.SubAgents));
        await using var lease = await stub.AcquireLeaseAsync();
        Assert.True(lease.AgentChat.History.Count > 0);
    }
}
