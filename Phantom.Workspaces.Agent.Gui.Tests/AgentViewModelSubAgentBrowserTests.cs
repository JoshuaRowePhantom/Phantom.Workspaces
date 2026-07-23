using AgentSchema;
using System.Collections.Specialized;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentViewModelSubAgentBrowserTests
{
    // ── §10 Sub-agents group node ─────────────────────────────────────────────

    [Fact]
    public async Task SubAgentsGroup_AppearsInEditorTree_WhenSubAgentsExist()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "a1", "Agent Alpha");

        var root = Assert.Single(viewModel.EditorItems);
        Assert.Contains(root.Children, c => c.Id == "chat-sub-agents");
    }

    [Fact]
    public async Task SubAgentsGroup_Count_ReflectsSubAgentsCount()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "a1", "Alpha");
        await AddSubAgentAsync(chat, "a2", "Beta");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");
        Assert.Equal("Sub-agents (2)", subAgentsNode.Name);
    }

    // ── §10 Browser card ──────────────────────────────────────────────────────

    [Fact]
    public void BrowserCard_SortedReverseChronologically_ByLastUpdatedAt()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t1 = new DateTime(2024, 1, 1, 0, 0, 1, DateTimeKind.Utc);
        var source = new System.Collections.ObjectModel.ObservableCollection<IRunningSubAgent>
        {
            new StubSubAgentItem("first", "First Agent", AgentChatCompletionState.Running, t0),
            new StubSubAgentItem("second", "Second Agent", AgentChatCompletionState.Running, t1),
        };
        var all = new System.Collections.ObjectModel.ReadOnlyObservableCollection<IRunningSubAgent>(source);
        using var browser = new SubAgentBrowserViewModel(all);

        // Most recently updated should appear first.
        Assert.Collection(browser.VisibleItems,
            item => Assert.Equal("second", item.AgentId),
            item => Assert.Equal("first", item.AgentId));
    }

    [Fact]
    public void SubAgentBrowserViewModel_SortedByMostRecentActivity_NotLaunchOrder()
    {
        // "early-launch" was launched first but has the most recent activity timestamp.
        // "late-launch" was launched second but has an older activity timestamp.
        // The browser should show "early-launch" first (by activity, not by launch order).
        var olderTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newerTime = new DateTime(2024, 1, 1, 0, 0, 1, DateTimeKind.Utc);
        var source = new System.Collections.ObjectModel.ObservableCollection<IRunningSubAgent>
        {
            new StubSubAgentItem("early-launch", "Early Agent", AgentChatCompletionState.Running, newerTime),
            new StubSubAgentItem("late-launch", "Late Agent", AgentChatCompletionState.Running, olderTime),
        };
        var all = new System.Collections.ObjectModel.ReadOnlyObservableCollection<IRunningSubAgent>(source);
        using var browser = new SubAgentBrowserViewModel(all);

        // Despite being launched first, "early-launch" appears first because it has more recent activity.
        Assert.Collection(browser.VisibleItems,
            item => Assert.Equal("early-launch", item.AgentId),
            item => Assert.Equal("late-launch", item.AgentId));
    }

    [Fact]
    public async Task HideCompleted_True_ShowsOnlyRunningSubAgents()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "running", "Running Agent");
        await AddSubAgentAsync(chat, "done", "Done Agent");

        // Mark "done" as succeeded.
        var doneChat = (AgentChat)chat.SubAgents.Single(s => s.AgentId == "done");
        doneChat.SetCompletionState(AgentChatCompletionState.Succeeded);

        viewModel.SubAgentsContainer.Browser.HideCompleted = true;

        var items = viewModel.SubAgentsContainer.Browser.VisibleItems;
        Assert.Single(items);
        Assert.Equal("running", items[0].AgentId);
    }

    [Fact]
    public async Task HideCompleted_False_ShowsAllSubAgents()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "running", "Running");
        var subAgentChat = (AgentChat)chat.SubAgents.Single(s => s.AgentId == "running");
        subAgentChat.SetCompletionState(AgentChatCompletionState.Succeeded);

        viewModel.SubAgentsContainer.Browser.HideCompleted = false;

        Assert.Single(viewModel.SubAgentsContainer.Browser.VisibleItems);
    }

    // ── §13 NavigateToAgent ───────────────────────────────────────────────────

    [Fact]
    public async Task NavigateToAgent_OpensMatchingSubAgentView()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "a1", "Sub A");
        await AddSubAgentAsync(chat, "a2", "Sub B");

        viewModel.NavigateToAgentHandler!.Invoke("a2");

        // The container should show sub-agent "a2", not the browser.
        Assert.False(viewModel.SubAgentsContainer.IsShowingBrowser);
        var selectedSlot = viewModel.SubAgentsContainer.Slots.Single(s => s.IsSelected);
        Assert.Equal("a2", selectedSlot.AgentId);
    }

    // ── Issue #1046: ancestor navigation ──────────────────────────────────────

    [Fact]
    public async Task NavigateToAgent_AncestorId_SwitchesActiveViewToAncestor()
    {
        // Parent has a sub-agent; the sub-agent's NavigateToAgentHandler is delegated to the parent.
        // Navigating to the parent's own agent id should select the root conversation view.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "a1", "Sub A");

        // First navigate to the sub-agent (so we're "inside" it).
        viewModel.NavigateToAgentHandler!.Invoke("a1");
        Assert.False(viewModel.SubAgentsContainer.IsShowingBrowser);

        // Now invoke navigation to the parent's own id (ancestor navigation).
        // The sub-agent's handler delegates to the parent's handler via AddSubAgentSlotEager.
        viewModel.NavigateToAgentHandler.Invoke(chat.AgentId);

        // The parent should select its own root conversation node.
        Assert.NotNull(viewModel.SelectedEditorItem);
        Assert.Equal(viewModel.EditorItems[0], viewModel.SelectedEditorItem);
    }

    [Fact]
    public async Task NavigateToAgent_RootId_SwitchesToRootView()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "a1", "Sub A");

        // Navigate away from root.
        viewModel.NavigateToAgentHandler!.Invoke("a1");
        Assert.False(viewModel.SubAgentsContainer.IsShowingBrowser);

        // Navigate to root id.
        viewModel.NavigateToAgentHandler.Invoke(chat.AgentId);

        // Root conversation view should be selected.
        Assert.NotNull(viewModel.SelectedEditorItem);
        Assert.Equal(viewModel.EditorItems[0], viewModel.SelectedEditorItem);
    }

    [Fact]
    public async Task NavigateToAgent_UnloadedAncestor_DoesNotThrow()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        // Navigate to an id that is neither a child nor the current agent.
        // This simulates an unloaded ancestor. Should not throw.
        var exception = Record.Exception(() => viewModel.NavigateToAgentHandler!.Invoke("nonexistent-ancestor-id"));
        Assert.Null(exception);
    }

    // ── §5 AcceptsUserInput ───────────────────────────────────────────────────

    [Fact]
    public async Task QueueComposerControl_Hidden_WhenAcceptsUserInput_False()
    {
        // Sub-agents use IHostedAgentChatClient → AcceptsUserInput is false.
        var chat = await CreateChatAsync();
        await AddSubAgentAsync(chat, "sub1", "Sub Agent");

        var subAgentEntry = chat.SubAgents.Single(s => s.AgentId == "sub1");
        var subAgentChat = (AgentChat)subAgentEntry;

        using var loggerFactory = new ObservableLoggerFactory();
        await using var subAgentViewModel = new AgentViewModel(subAgentChat, "sub", "", loggerFactory, TaskScheduler.Default);

        Assert.False(subAgentViewModel.AcceptsUserInput);
    }

    [Fact]
    public async Task QueueComposerControl_Visible_WhenAcceptsUserInput_True()
    {
        // Root/parent agents use a real chat client → AcceptsUserInput is true.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        Assert.True(viewModel.AcceptsUserInput);
    }

    // ── §11 View retention ────────────────────────────────────────────────────

    [Fact]
    public async Task SubAgentView_ContextSwitch_DoesNotDisposeAndRecreateControl()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "a1", "Sub A");
        await AddSubAgentAsync(chat, "a2", "Sub B");

        // Navigate to sub-agent A.
        viewModel.NavigateToAgentHandler!.Invoke("a1");
        var slotA = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "a1");
        var vmA = slotA.SubAgentViewModel;

        // Navigate to sub-agent B (away from A).
        viewModel.NavigateToAgentHandler.Invoke("a2");

        // Navigate back to A.
        viewModel.NavigateToAgentHandler.Invoke("a1");
        var slotAAfter = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "a1");
        var vmAAfter = slotAAfter.SubAgentViewModel;

        // Same slot instance, same view model instance — the control is not recreated.
        Assert.Same(slotA, slotAAfter);
        Assert.Same(vmA, vmAAfter);
    }

    // ── §14 Restored sub-agents ───────────────────────────────────────────

    [Fact]
    public async Task SubAgentsGroup_AppearsInEditorTree_WhenRestoredSubAgentsExist()
    {
        // Arrange: create a parent and a completed sub-agent.
        var chat = await CreateChatAsync();
        var subAgentChat = await CreateChatAsync();
        var scheduler = new CapturingTaskScheduler();
        
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, scheduler);

        // Act: Add a lazy sub-agent via reflection, then drain all scheduled tasks.
        AddSubAgentViaReflection(chat, subAgentChat, scheduler);
        // Drain multiple times to ensure all continuations run
        for (int i = 0; i < 10; i++)
            scheduler.Drain();

        // Assert: The sub-agents group node should appear.
        var root = Assert.Single(viewModel.EditorItems);
        Assert.Contains(root.Children, c => c.Id == "chat-sub-agents");
    }

    [Fact]
    public async Task SubAgentSlot_CreatedForRestoredSubAgent_AfterLeaseAcquired()
    {
        // Arrange: create a parent and a fake lazy sub-agent.
        var chat = await CreateChatAsync();
        var subAgentChat = await CreateChatAsync();
        var scheduler = new CapturingTaskScheduler();
        
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, scheduler);

        // Act: Add a lazy sub-agent via reflection, then drain all scheduled tasks.
        AddSubAgentViaReflection(chat, subAgentChat, scheduler);
        // Drain multiple times to ensure all continuations run
        for (int i = 0; i < 10; i++)
            scheduler.Drain();

        // Assert: The slot should be created.
        var slot = Assert.Single(viewModel.SubAgentsContainer.Slots);
        Assert.Equal(subAgentChat.AgentId, slot.AgentId);
    }

    [Fact]
    public async Task SubAgentSlot_NotCreatedImmediately_ForRestoredSubAgent_BeforeLeaseAcquired()
    {
        // Arrange: create a parent and a fake lazy sub-agent.
        var chat = await CreateChatAsync();
        var subAgentChat = await CreateChatAsync();
        var scheduler = new CapturingTaskScheduler();
        
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, scheduler);

        // Act: Add a lazy sub-agent via reflection, but DON'T drain the scheduler yet.
        AddSubAgentViaReflection(chat, subAgentChat, scheduler);

        // Assert: No slot should exist yet (before draining).
        Assert.Empty(viewModel.SubAgentsContainer.Slots);
        
        // Drain all scheduled tasks to allow disposal to complete without hanging.
        for (int i = 0; i < 10; i++)
            scheduler.Drain();
    }

    [Fact]
    public async Task SubAgentsGroup_Count_IncludesRestoredSubAgents()
    {
        // Arrange: create a parent and two fake lazy sub-agents.
        var chat = await CreateChatAsync();
        var subAgentChat1 = await CreateChatAsync();
        var subAgentChat2 = await CreateChatAsync();
        var scheduler = new CapturingTaskScheduler();
        
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, scheduler);

        // Act: Add the fake lazy sub-agents via reflection, then drain all scheduled tasks.
        AddSubAgentViaReflection(chat, subAgentChat1, scheduler);
        AddSubAgentViaReflection(chat, subAgentChat2, scheduler);
        // Drain multiple times to ensure all continuations run
        for (int i = 0; i < 10; i++)
            scheduler.Drain();

        // Assert: The count should reflect restored sub-agents.
        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");
        Assert.Equal("Sub-agents (2)", subAgentsNode.Name);
    }

    // ── §796 Input queue disabled for sub-agents ──────────────────────────────

    [Fact]
    public async Task SubAgentView_FactoryPathSubAgent_AcceptsUserInput_False()
    {
        // Factory-path sub-agents use IHostedAgentChatClient → AcceptsUserInput is false.
        var chat = await CreateChatAsync();
        await AddSubAgentAsync(chat, "sub1", "Sub Agent");

        var subAgentEntry = chat.SubAgents.Single(s => s.AgentId == "sub1");
        var subAgentChat = (AgentChat)subAgentEntry;

        using var loggerFactory = new ObservableLoggerFactory();
        await using var subAgentViewModel = new AgentViewModel(subAgentChat, "sub", "", loggerFactory, TaskScheduler.Default);

        Assert.False(subAgentViewModel.AcceptsUserInput);
    }

    [Fact]
    public async Task SubAgentView_InputQueue_IsNull_WhenAcceptsUserInput_False()
    {
        // When AcceptsUserInput is false, InputQueue should not be created.
        var chat = await CreateChatAsync();
        await AddSubAgentAsync(chat, "sub1", "Sub Agent");

        var subAgentEntry = chat.SubAgents.Single(s => s.AgentId == "sub1");
        var subAgentChat = (AgentChat)subAgentEntry;

        using var loggerFactory = new ObservableLoggerFactory();
        await using var subAgentViewModel = new AgentViewModel(subAgentChat, "sub", "", loggerFactory, TaskScheduler.Default);

        Assert.Null(subAgentViewModel.InputQueue);
    }

    [Fact]
    public async Task QueueComposerControl_Hidden_WhenFactoryPathSubAgentSelected()
    {
        // Selecting a factory-path sub-agent in the nav tree should create a view with InputQueue = null.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "sub1", "Sub Agent");

        // Navigate to the sub-agent.
        viewModel.NavigateToAgentHandler!.Invoke("sub1");

        var slot = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "sub1");
        Assert.Null(slot.SubAgentViewModel.InputQueue);
        Assert.False(slot.SubAgentViewModel.AcceptsUserInput);
    }

    [Fact]
    public async Task ParentView_InputQueue_IsNotNull_WhenAcceptsUserInput_True()
    {
        // Root/parent agents should have InputQueue created.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        Assert.True(viewModel.AcceptsUserInput);
        Assert.NotNull(viewModel.InputQueue);
    }

    [Fact]
    public async Task SubAgentView_KeyboardShortcuts_QueueCommands_NotActive_WhenAcceptsUserInput_False()
    {
        // Sub-agents with AcceptsUserInput = false should have null-safe queue command wrappers
        // that can execute without throwing NullReferenceException.
        var chat = await CreateChatAsync();
        await AddSubAgentAsync(chat, "sub1", "Sub Agent");

        var subAgentEntry = chat.SubAgents.Single(s => s.AgentId == "sub1");
        var subAgentChat = (AgentChat)subAgentEntry;

        using var loggerFactory = new ObservableLoggerFactory();
        await using var subAgentViewModel = new AgentViewModel(subAgentChat, "sub", "", loggerFactory, TaskScheduler.Default);

        Assert.Null(subAgentViewModel.InputQueue);
        
        // These commands should exist and be safe to execute (no-op when InputQueue is null).
        Assert.NotNull(subAgentViewModel.ToggleHoldAllQueuesCommand);
        Assert.NotNull(subAgentViewModel.HoldAllQueuesCommand);
        Assert.NotNull(subAgentViewModel.UnholdAllQueuesCommand);
        
        // Execute should not throw.
        subAgentViewModel.ToggleHoldAllQueuesCommand.Execute(null);
        subAgentViewModel.HoldAllQueuesCommand.Execute(null);
        subAgentViewModel.UnholdAllQueuesCommand.Execute(null);
    }

    // Helpers ───────────────────────────────────────────────────────────────

    private static AgentDefinition CreateAgentDefinition()
        => AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "test-agent",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

    private static Task<AgentChat> CreateChatAsync()
        => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = CreateAgentDefinition(),
            });

    private static async Task<IRunningSubAgent> AddSubAgentAsync(
        AgentChat chat,
        string agentId,
        string displayName)
    {
        var definition = AgentDefinitionLoader.LoadAgentFromJson(
            $$"""
            {
              "kind": "prompt",
              "name": "{{displayName}}",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

        await chat.GetOrCreateAsync(agentId, definition, $"tool-call-{agentId}");
        return chat.SubAgents.Single(s => s.AgentId == agentId);
    }

    private static async Task<AgentChat> CreateParentChatAsync(
        InMemoryAgentPersistenceStore store,
        string? agentSessionId = null,
        AgentServices? services = null,
        TaskScheduler? foregroundScheduler = null) =>
        await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = CreateAgentDefinition(),
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
    /// Adds a sub-agent to AgentChat.subAgentItems via reflection and scheduler.
    /// Creates a real SubAgent with a fake factory that returns a pre-completed lease.
    /// </summary>
    private static void AddSubAgentViaReflection(AgentChat chat, AgentChat subAgentChat, TaskScheduler scheduler)
    {
        var field = typeof(AgentChat).GetField("subAgentItems", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var collection = (System.Collections.ObjectModel.ObservableCollection<IRunningSubAgent>)field!.GetValue(chat)!;
        
        // Create a fake factory that returns a completed lease immediately
        var fakeFactory = new FakeRunningAgentChatFactory(subAgentChat, scheduler);
        
        // Create a real SubAgent (lazy path) using reflection to access internal constructor
        var subAgentType = typeof(SubAgent);
        
        // Find the 2-parameter lazy constructor manually
        var allConstructors = subAgentType.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var lazyConstructor = allConstructors.FirstOrDefault(c =>
        {
            var parameters = c.GetParameters();
            return parameters.Length == 2
                && parameters[0].ParameterType == typeof(AgentSessionId)
                && parameters[1].ParameterType.Name.Contains("IRunningAgentChatFactory");
        });
        
        if (lazyConstructor == null)
        {
            throw new InvalidOperationException(
                $"Could not find lazy SubAgent constructor. Available constructors: {string.Join("; ", allConstructors.Select(c => string.Join(", ", c.GetParameters().Select(p => p.ParameterType.FullName))))}");
        }
        
        // Construct AgentSessionId from string
        var sessionId = new AgentSessionId(subAgentChat.AgentSessionId);
        var subAgent = (SubAgent)lazyConstructor.Invoke([sessionId, fakeFactory]);
        
        // Schedule the add on the foreground scheduler, just like RestoreSubAgentsAsync does
        Task.Factory.StartNew(
            () => collection.Add(subAgent),
            CancellationToken.None,
            TaskCreationOptions.None,
            scheduler);
    }

    /// <summary>
    /// Fake factory for testing that returns a pre-completed lease on the scheduler.
    /// </summary>
    private sealed class FakeRunningAgentChatFactory : Phantom.Workspaces.Llm.IRunningAgentChatFactory
    {
        private readonly AgentChat _agentChat;
        private readonly TaskScheduler _scheduler;

        public FakeRunningAgentChatFactory(AgentChat agentChat, TaskScheduler scheduler)
        {
            _agentChat = agentChat;
            _scheduler = scheduler;
        }

        public System.Collections.ObjectModel.ObservableCollection<RunningAgentChat> RunningSessions { get; } = [];

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<RunningAgentChatLease>();
            
            // Schedule the lease creation on the foreground scheduler
            Task.Factory.StartNew(
                () =>
                {
                    // Create a lease using reflection to access the internal constructor
                    var leaseType = typeof(RunningAgentChatLease);
                    var constructor = leaseType.GetConstructor(
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                        null,
                        [typeof(AgentSessionId), typeof(AgentChat), typeof(Func<ValueTask>)],
                        null);
                    var sessionIdStruct = new AgentSessionId(_agentChat.AgentSessionId);
                    var lease = (RunningAgentChatLease)constructor!.Invoke(
                        [sessionIdStruct, _agentChat, new Func<ValueTask>(() => ValueTask.CompletedTask)]);
                    tcs.SetResult(lease);
                },
                CancellationToken.None,
                TaskCreationOptions.None,
                _scheduler);
            
            return tcs.Task;
        }

        public Task<RunningAgentChatLease> CreateAsync(AgentDefinition definition, AgentSessionId sessionId, AgentServices? services = null, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<RunningAgentChatLease> GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition = null,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }
    }

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

        public bool HasPendingTasks => _queue.Count > 0;

        public void Drain()
        {
            // Keep draining until no more tasks are queued
            while (_queue.Count > 0)
            {
                var tasks = _queue.ToList();
                _queue.Clear();
                foreach (var task in tasks)
                    TryExecuteTask(task);
                // After executing, new tasks might have been queued, so loop again
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

    private sealed class StubSubAgentItem(
        string agentId,
        string displayName,
        AgentChatCompletionState completionState,
        DateTime lastUpdatedAt) : IRunningSubAgent
    {
        public string AgentId { get; } = agentId;
        public string DisplayName { get; } = displayName;
        public string Description => string.Empty;
        public AgentChatCompletionState CompletionState { get; } = completionState;
        public DateTime LastUpdatedAt { get; } = lastUpdatedAt;
        public IReadOnlyList<IRunningSubAgent> SubAgents => [];
    }
}
