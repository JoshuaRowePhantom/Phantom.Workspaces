using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Interfaces;
using Xunit;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentChatSubAgentRegistryTests
{
    private static readonly string DefaultAgentDefinitionJson =
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

    private static AgentChat CreateParentChat() =>
        AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(DefaultAgentDefinitionJson),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "parent-chat",
        }).GetAwaiter().GetResult();

    private static AgentDefinition CreateSubAgentDefinition(string name = "sub-agent") =>
        AgentDefinitionLoader.LoadAgentFromJson($$"""
            {
              "kind": "prompt",
              "name": "{{name}}",
              "model": {
                "id": "echo",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

    [Fact]
    public async Task GetOrCreateAsync_AddsChildToSubAgents()
    {
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();

        await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1");

        Assert.Single(parent.SubAgents);
    }

    [Fact]
    public async Task GetOrCreateAsync_SameAgentId_ReturnsSameChild()
    {
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();

        var first = await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1");
        var second = await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1");

        Assert.Same(first, second);
        Assert.Single(parent.SubAgents);
    }

    [Fact]
    public async Task TryGet_UnknownAgentId_ReturnsNull()
    {
        await using var parent = CreateParentChat();

        var result = parent.TryGet("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public async Task TryGet_KnownAgentId_ReturnsSink()
    {
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();

        var sink = await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1");

        Assert.Same(sink, parent.TryGet("agent-1"));
    }

    [Fact]
    public async Task AcceptsUserInput_True_WhenChatClientIsNormal()
    {
        await using var chat = CreateParentChat();
        Assert.True(chat.AcceptsUserInput);
    }

    [Fact]
    public async Task AcceptsUserInput_False_WhenChatClientIsIHostedAgentChatClient()
    {
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();
        await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1");

        var child = Assert.Single(parent.SubAgents);

        Assert.False(((AgentChat)child).AcceptsUserInput);
    }

    [Fact]
    public async Task CompletionState_Running_Initially()
    {
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();

        await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1");

        var child = Assert.Single(parent.SubAgents);
        Assert.Equal(AgentChatCompletionState.Running, child.CompletionState);
    }

    [Fact]
    public async Task CompletionState_Succeeded_WhenComplete_Called()
    {
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();

        var sink = await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1");
        var child = Assert.Single(parent.SubAgents);

        sink.Complete();

        Assert.Equal(AgentChatCompletionState.Succeeded, child.CompletionState);
    }

    [Fact]
    public async Task CompletionState_Failed_WhenFail_Called()
    {
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();

        var sink = await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1");
        var child = Assert.Single(parent.SubAgents);

        sink.Fail(new InvalidOperationException("test failure"));

        Assert.Equal(AgentChatCompletionState.Failed, child.CompletionState);
    }

    [Fact]
    public async Task ParentAgent_Set_OnChildAgentChat()
    {
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();

        await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1");

        var child = (AgentChat)Assert.Single(parent.SubAgents);
        Assert.Same(parent, child.ParentAgent);
    }

    [Fact]
    public async Task GetOrCreateAsync_TwiceSameId_OneChildCreated()
    {
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();

        await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1");
        await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-2");

        Assert.Single(parent.SubAgents);
    }

    [Fact]
    public async Task GetOrCreateAsync_AgentId_SetOnChild()
    {
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();

        await parent.GetOrCreateAsync("my-agent-id", subDef, "tool-call-1");

        var child = (AgentChat)Assert.Single(parent.SubAgents);
        Assert.Equal("my-agent-id", child.AgentId);
    }

    [Fact]
    public async Task GetOrCreateAsync_SubAgentChat_ReceivesParentForegroundScheduler()
    {
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();

        await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1");

        var child = (AgentChat)Assert.Single(parent.SubAgents);
        Assert.Same(parent.ForegroundSchedulerForTesting, child.ForegroundSchedulerForTesting);
    }

    [Fact]
    public async Task GetOrCreateAsync_CalledFromThreadPoolThread_ConstructsChildOnForegroundScheduler()
    {
        var scheduler = new RecordingTaskScheduler();
        await using var parent = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(DefaultAgentDefinitionJson),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "parent-chat",
            ForegroundScheduler = scheduler,
        });
        var subDef = CreateSubAgentDefinition();

        // Invoke from a thread-pool thread, mirroring the production registry path where the
        // Copilot SDK event drain loop calls GetOrCreateAsync off the UI thread (issue #913).
        await Task.Run(() => parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1"));

        var child = (AgentChat)Assert.Single(parent.SubAgents);
        Assert.Same(scheduler, child.ForegroundSchedulerForTesting);
        Assert.Contains(nameof(AgentChat), scheduler.ConstructedTypes);
    }

    [Fact]
    public async Task SubAgentSinkPush_UpdateRunningItem_ExecutesOnForegroundScheduler()
    {
        var scheduler = new RecordingTaskScheduler();
        await using var parent = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(DefaultAgentDefinitionJson),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "parent-chat",
            ForegroundScheduler = scheduler,
        });
        var subDef = CreateSubAgentDefinition();
        var sink = await Task.Run(() => parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1"));
        var child = (AgentChat)Assert.Single(parent.SubAgents);

        // Option D (issue #840) removed the outer collection's Replace notification when only
        // inner items change. Now we must subscribe to the first running item's inner Items
        // collection to observe mutations on the foreground scheduler.
        AgentChatRunningItem? firstRunningItem = null;
        var observed = new TaskCompletionSource<TaskScheduler?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outerHandler = new System.Collections.Specialized.NotifyCollectionChangedEventHandler(
            (_, e) =>
            {
                // If a new running item is added, subscribe to its inner Items collection
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add &&
                    e.NewItems?[0] is AgentChatRunningItem newItem &&
                    firstRunningItem == null)
                {
                    firstRunningItem = newItem;
                    ((System.Collections.Specialized.INotifyCollectionChanged)newItem.Items).CollectionChanged +=
                        (_, _) => observed.TrySetResult(TaskScheduler.Current);
                }
            });
        
        ((System.Collections.Specialized.INotifyCollectionChanged)child.RunningItems).CollectionChanged += outerHandler;

        // If there's already a running item (from child startup), subscribe to it immediately
        if (child.RunningItems.Count > 0)
        {
            firstRunningItem = child.RunningItems[0];
            ((System.Collections.Specialized.INotifyCollectionChanged)firstRunningItem.Items).CollectionChanged +=
                (_, _) => observed.TrySetResult(TaskScheduler.Current);
        }

        sink.Push(new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("live sub-agent output")],
        });

        var mutationScheduler = await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(scheduler, mutationScheduler);
    }

    [Fact]
    public async Task GetOrCreateAsync_HeadlessParentWithoutScheduler_ChildStillCreated()
    {
        // Headless/CLI/test parents provide no ForegroundScheduler and fall back to the parent's
        // exclusive-pair scheduler; the registry path must keep working there (issue #913).
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();

        var sink = await Task.Run(() => parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1"));

        Assert.NotNull(sink);
        var child = (AgentChat)Assert.Single(parent.SubAgents);
        Assert.Same(parent.ForegroundSchedulerForTesting, child.ForegroundSchedulerForTesting);
    }

    // Records which work executes on the scheduler so tests can assert construction and
    // foreground mutations were dispatched onto it (issue #913). Executes queued tasks on the
    // thread pool; TryExecuteTask establishes TaskScheduler.Current for the task's duration.
    private sealed class RecordingTaskScheduler : TaskScheduler
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<string> constructedTypes = [];

        public IReadOnlyCollection<string> ConstructedTypes => this.constructedTypes;

        protected override void QueueTask(Task task)
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                _ =>
                {
                    this.TryExecuteTask(task);
                    this.RecordResult(task);
                },
                null);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            var executed = this.TryExecuteTask(task);
            if (executed)
            {
                this.RecordResult(task);
            }

            return executed;
        }

        protected override IEnumerable<Task>? GetScheduledTasks() => null;

        private void RecordResult(Task task)
        {
            // GetOrCreateAsync constructs the child via StartNew(() => AgentChat.CreateAsync(...))
            // on this scheduler; the resulting Task<Task<AgentChat>> is how construction shows up.
            if (task is Task<Task<AgentChat>>)
            {
                this.constructedTypes.Add(nameof(AgentChat));
            }
        }
    }

    [Fact]
    public async Task ISubAgentTable_Add_PersistsLinkBeforeReturning()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentChat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(DefaultAgentDefinitionJson),
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "parent-chat",
        });

        var childChat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = CreateSubAgentDefinition(),
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "child-chat",
        });

        await using var parentDispose = parentChat;
        await using var childDispose = childChat;

        // Act: Add the child to the parent
        await ((ISubAgentTable)parentChat).Add(childChat);

        // Assert: The link should be immediately readable from the store
        var childIds = await store.ReadSubAgentChildIdsAsync(parentChat.AgentSessionId);
        var childId = Assert.Single(childIds);
        Assert.Equal(childChat.AgentSessionId, childId.Value);
    }
}
