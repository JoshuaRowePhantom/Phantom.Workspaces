using AgentSchema;
using System.Collections.Specialized;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentViewModelEditorTreeTests
{
    [Fact]
    public async Task ToolsNavItem_DiscoveryOverlapsInitialSnapshot_ProjectsLatestSnapshotWithoutThrowing()
    {
        var chat = new ToolProjectionChat(await CreateChatAsync());
        chat.Publish(ToolProjectionChat.Tool("pending", status: "Loading"));
        using var release = new ManualResetEventSlim();
        var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chat.SnapshotRead = () =>
        {
            chat.SnapshotRead = null;
            sampled.SetResult();
            // Only the constructor's worker is blocked; never the UI thread or a projection lock.
            release.Wait(TestContext.Current.CancellationToken);
        };
        using var logs = new ObservableLoggerFactory();
        var construction = Task.Run(() => new AgentViewModel(chat, "test", "", logs, TaskScheduler.Default),
            TestContext.Current.CancellationToken);
        await sampled.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            chat.Publish(ToolProjectionChat.Tool("pending-2", status: "Loading"));
            chat.Publish(ToolProjectionChat.Tool("complete") with
                { Children = [ToolProjectionChat.Tool("child", false, "Ready")] },
                ToolProjectionChat.Tool("disabled", false));
        }
        finally { release.Set(); }
        await using var vm = await construction;
        await vm.ToolProjectionIdle;
        AssertToolProjection(vm, chat.GetToolSnapshot());
    }

    [Fact]
    public async Task ToolsNavItem_PublicationDuringInitialization_DoesNotLoseSnapshot()
    {
        var chat = new ToolProjectionChat(await CreateChatAsync());
        chat.Subscribed = () => chat.Publish(ToolProjectionChat.Tool("at-subscription"));
        using var logs = new ObservableLoggerFactory();
        await using var vm = new AgentViewModel(chat, "test", "", logs, TaskScheduler.Default);
        await vm.ToolProjectionIdle;
        AssertToolProjection(vm, chat.GetToolSnapshot());
    }

    [Fact]
    public async Task ToolsNavItem_InitialSnapshotFault_UnsubscribesAndPropagates()
    {
        await using var chat = new ToolProjectionChat(await CreateChatAsync());
        chat.SnapshotRead = () => throw new InvalidOperationException("initial-projection-failure");
        using var logs = new ObservableLoggerFactory();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new AgentViewModel(chat, "test", "", logs, TaskScheduler.Default));
        Assert.Equal("initial-projection-failure", exception.Message);
        Assert.Equal(0, chat.SubscriberCount);
    }

    [Fact]
    public async Task ToolsNavItem_ConcurrentRefreshRequests_AppliesSnapshotsSerially()
    {
        var chat = new ToolProjectionChat(await CreateChatAsync());
        using var logs = new ObservableLoggerFactory();
        await using var vm = new AgentViewModel(chat, "test", "", logs, TaskScheduler.Default);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var depth = 0;
        var depths = new System.Collections.Concurrent.ConcurrentBag<int>();
        var first = 0;
        vm.Tools.CollectionChanged += (_, e) =>
        {
            var current = Interlocked.Increment(ref depth);
            depths.Add(current);
            try
            {
                if (e.Action == NotifyCollectionChangedAction.Add && Interlocked.Exchange(ref first, 1) == 0)
                {
                    chat.Publish(ToolProjectionChat.Tool("reentrant"));
                    entered.SetResult();
                    // This is a thread-pool projection callback, not a UI callback.
                    release.Wait(TestContext.Current.CancellationToken);
                }
            }
            finally { Interlocked.Decrement(ref depth); }
        };
        var firstPublication = Task.Run(() => chat.Publish(ToolProjectionChat.Tool("first")),
            TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
                Task.Run(() => chat.Publish(ToolProjectionChat.Tool($"concurrent-{i}")),
                    TestContext.Current.CancellationToken)));
            chat.Publish(ToolProjectionChat.Tool("last", false, "Ready"));
        }
        finally { release.Set(); }
        await firstPublication;
        await vm.ToolProjectionIdle;
        Assert.All(depths, value => Assert.Equal(1, value));
        AssertToolProjection(vm, chat.GetToolSnapshot());
    }

    [Fact]
    public async Task ToolsNavItem_QueuedOlderRefresh_DoesNotOverwriteNewerSnapshot()
    {
        var chat = new ToolProjectionChat(await CreateChatAsync());
        var scheduler = new ToolProjectionScheduler();
        using var logs = new ObservableLoggerFactory();
        await using var vm = new AgentViewModel(chat, "test", "", logs, scheduler);
        var samples = 0;
        chat.SnapshotRead = () => samples++;
        chat.Publish(ToolProjectionChat.Tool("old", status: "Loading"));
        chat.Publish(ToolProjectionChat.Tool("latest", false, "Ready"));
        Assert.Equal(0, samples);
        Assert.Equal(1, scheduler.Count);
        scheduler.Drain();
        await vm.ToolProjectionIdle;
        Assert.Equal(1, samples);
        AssertToolProjection(vm, chat.GetToolSnapshot());
        chat.Publish(ToolProjectionChat.Tool("next-generation"));
        scheduler.Drain();
        await vm.ToolProjectionIdle;
        AssertToolProjection(vm, chat.GetToolSnapshot());
    }

    [Fact]
    public async Task ToolsNavItem_DisposedWithQueuedRefresh_DoesNotMutateCollections()
    {
        var chat = new ToolProjectionChat(await CreateChatAsync());
        var scheduler = new ToolProjectionScheduler();
        using var logs = new ObservableLoggerFactory();
        var vm = new AgentViewModel(chat, "test", "", logs, scheduler);
        var lateDelivery = chat.CaptureDelivery();
        var mutations = 0;
        vm.Tools.CollectionChanged += (_, _) => mutations++;
        chat.Publish(ToolProjectionChat.Tool("queued"));
        await vm.DisposeViewResourcesAsync();
        Assert.Equal(0, chat.SubscriberCount);
        lateDelivery?.Invoke(chat, EventArgs.Empty);
        scheduler.Drain();
        chat.Publish(ToolProjectionChat.Tool("after"));
        Assert.Equal(0, mutations);
        await chat.DisposeAsync();
    }

    [Fact]
    public async Task ToolsNavItem_DisposedDuringApplication_WaitsForOwnerBeforeTransformerTeardown()
    {
        var chat = new ToolProjectionChat(await CreateChatAsync());
        using var logs = new ObservableLoggerFactory();
        var vm = new AgentViewModel(chat, "test", "", logs, TaskScheduler.Default);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.Tools.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Reset) return;
            entered.SetResult();
            // The test holds only the worker that owns this projection transaction.
            release.Wait(TestContext.Current.CancellationToken);
        };
        var publication = Task.Run(() => chat.Publish(ToolProjectionChat.Tool("active")),
            TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var disposal = vm.DisposeViewResourcesAsync().AsTask();
        var joinedDisposal = vm.DisposeViewResourcesAsync().AsTask();
        try
        {
            Assert.Same(disposal, joinedDisposal);
            Assert.False(disposal.IsCompleted);
            Assert.False(joinedDisposal.IsCompleted);
        }
        finally { release.Set(); }
        await publication;
        await disposal;
        await joinedDisposal;
        AssertToolProjection(vm, chat.GetToolSnapshot());
        var before = vm.Tools.ToArray();
        chat.Publish(ToolProjectionChat.Tool("too-late"));
        Assert.Equal(before, vm.Tools);
        await chat.DisposeAsync();
    }

    private static void AssertToolProjection(AgentViewModel vm, IReadOnlyList<AgentChatToolItem> snapshot)
    {
        var nav = vm.EditorItems.Single().Children.Single(item => item.Id == "chat-tools");
        Assert.Equal(snapshot.Select(t => t.Id), vm.Tools.Select(t => t.Id));
        Assert.Equal(snapshot.Select(t => t.Id), nav.Children.Select(t => t.Id));
        Assert.Equal(snapshot.Select(t => t.IsEnabled), vm.Tools.Select(t => t.IsEnabled));
        Assert.Equal(snapshot.Select(t => t.Status), vm.Tools.Select(t => t.Status));
        AssertToolChildren(snapshot, vm.Tools, nav.Children);
    }

    private static void AssertToolChildren(
        IReadOnlyList<AgentChatToolItem> expected,
        IReadOnlyList<AgentChatToolViewModel> tools,
        IReadOnlyList<AgentEditorNavigationItemViewModel> navigation)
    {
        Assert.Equal(expected.Select(t => t.Id), tools.Select(t => t.Id));
        Assert.Equal(expected.Select(t => t.Id), navigation.Select(t => t.Id));
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].IsEnabled, tools[i].IsEnabled);
            Assert.Equal(expected[i].Status, tools[i].Status);
            Assert.Same(tools[i], navigation[i].Tool);
            AssertToolChildren(expected[i].Children, tools[i].Children, navigation[i].Children);
        }
    }

    [Fact]
    public async Task ToolsNavItem_ScheduledProjectionFault_IsObservedAndReportedWithoutRetry()
    {
        var chat = new ToolProjectionChat(await CreateChatAsync());
        var scheduler = new ToolProjectionScheduler();
        using var logs = new ObservableLoggerFactory();
        await using var vm = new AgentViewModel(chat, "test", "", logs, scheduler);
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        logs.EntryAdded += (_, text) =>
        {
            if (text.Contains("Foreground tool projection failed.", StringComparison.Ordinal))
                reported.TrySetResult(text);
        };
        var samples = 0;
        chat.SnapshotRead = () =>
        {
            samples++;
            throw new InvalidOperationException("deterministic-projection-failure");
        };
        chat.Publish(ToolProjectionChat.Tool("failed"));
        scheduler.Drain();
        var report = await reported.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Contains("deterministic-projection-failure", report, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), report, StringComparison.Ordinal);
        chat.Publish(ToolProjectionChat.Tool("not-silently-retried"));
        scheduler.Drain();
        Assert.Equal(1, samples);
        Assert.Empty(vm.Tools);
    }

    [Fact]
    public async Task ToolsNavItem_SchedulerRejectsRefresh_PropagatesWithoutLeavingOwnerActive()
    {
        var chat = new ToolProjectionChat(await CreateChatAsync());
        var scheduler = new ToolProjectionScheduler();
        using var logs = new ObservableLoggerFactory();
        await using var vm = new AgentViewModel(chat, "test", "", logs, scheduler);
        scheduler.RejectNext = true;
        var exception = Assert.Throws<TaskSchedulerException>(() => chat.Publish(ToolProjectionChat.Tool("rejected")));
        Assert.Equal("deterministic-scheduler-rejection", exception.InnerException?.Message);
        await vm.ToolProjectionIdle;
        chat.Publish(ToolProjectionChat.Tool("not-retried"));
        Assert.Equal(0, scheduler.Count);
        Assert.Empty(vm.Tools);
    }

    [Fact]
    public async Task ToolsNavItem_DisposalFailure_PropagatesToAllJoinedCallers()
    {
        await using var chat = new ToolProjectionChat(await CreateChatAsync());
        using var logs = new ObservableLoggerFactory();
        var vm = new AgentViewModel(chat, "test", "", logs, TaskScheduler.Default);
        chat.UnsubscribingInformation = () => throw new InvalidOperationException("deterministic-disposal-failure");
        var first = vm.DisposeViewResourcesAsync().AsTask();
        var second = vm.DisposeViewResourcesAsync().AsTask();
        Assert.Same(first, second);
        var firstFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await first);
        var secondFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await second);
        Assert.Same(firstFailure, secondFailure);
        Assert.Equal("deterministic-disposal-failure", firstFailure.Message);
        Assert.Equal(0, chat.SubscriberCount);
    }

    [Fact]
    public async Task AgentViewModel_DoesNotExpose_DiagnosticsInspectorProperty()
    {
        // Issue #819: The DiagnosticsInspector property was removed.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var property = typeof(AgentViewModel).GetProperty("DiagnosticsInspector");
        Assert.Null(property);
    }

    [Fact]
    public async Task AgentViewModel_DoesNotContain_DiagnosticsNavigationItem()
    {
        // Issue #819: The Diagnostics navigation item (tab) was removed.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var root = Assert.Single(viewModel.EditorItems);
        var diagnosticsNav = root.Children.FirstOrDefault(c => c.Id == "chat-diagnostics");
        Assert.Null(diagnosticsNav);
    }

    [Fact]
    public async Task ToolsNavItem_IdentityStable_AfterToolsChanged()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var root = Assert.Single(viewModel.EditorItems);
        var toolsNavBefore = root.Children.First(c => c.Id == "chat-tools");

        chat.RaiseToolsChanged();

        var toolsNavAfter = root.Children.First(c => c.Id == "chat-tools");
        Assert.Same(toolsNavBefore, toolsNavAfter);
    }

    [Fact]
    public async Task RootNavItem_IdentityStable_AfterSubAgentAdded()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var rootBefore = Assert.Single(viewModel.EditorItems);
        var chatDetailsBefore = rootBefore.Children.First(c => c.Id == "chat-details");
        var toolsBefore = rootBefore.Children.First(c => c.Id == "chat-tools");

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var rootAfter = Assert.Single(viewModel.EditorItems);
        var chatDetailsAfter = rootAfter.Children.First(c => c.Id == "chat-details");
        var toolsAfter = rootAfter.Children.First(c => c.Id == "chat-tools");

        Assert.Same(rootBefore, rootAfter);
        Assert.Same(chatDetailsBefore, chatDetailsAfter);
        Assert.Same(toolsBefore, toolsAfter);
    }

    [Fact]
    public async Task ToolsNavItem_ChildrenUpdate_Incrementally()
    {
        Assert.True(File.Exists(TestMcpServerProcess.GetMcpExecutablePathForTests()));
        await using var server = await TestMcpServerProcess.StartAsync();
        var chat = await CreateChatWithMcpAsync(server.BoundUrl);
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var root = Assert.Single(viewModel.EditorItems);
        var toolsNav = root.Children.First(c => c.Id == "chat-tools");
        var chatDetailsNav = root.Children.First(c => c.Id == "chat-details");
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");

        await chat.Initialization.WaitAsync(TestContext.Current.CancellationToken);
        await viewModel.ToolProjectionIdle;
        AssertToolProjection(viewModel, chat.GetToolSnapshot());

        Assert.Single(toolsNav.Children);
        Assert.Empty(chatDetailsNav.Children);
        Assert.Empty(subAgentsNav.Children);
    }

    [Fact]
    public async Task SubAgentsNavItem_ChildrenUpdate_Incrementally()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var toolsNav = root.Children.First(c => c.Id == "chat-tools");

        Assert.Empty(subAgentsNav.Children);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        Assert.Single(subAgentsNav.Children);
        Assert.Empty(toolsNav.Children);
    }

    [Fact]
    public async Task SubAgentsNavItem_Name_UpdatesCount_OnSubAgentAdded()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");

        Assert.Equal("Sub-agents", subAgentsNav.Name);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        Assert.Equal("Sub-agents (1)", subAgentsNav.Name);

        await AddSubAgentAsync(chat, "sub-agent-2", "Sub Agent 2");

        Assert.Equal("Sub-agents (2)", subAgentsNav.Name);
    }

    [Fact]
    public async Task ToolsNavItem_IsExpanded_Retained_AfterToolsChange()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var root = Assert.Single(viewModel.EditorItems);
        var toolsNav = root.Children.First(c => c.Id == "chat-tools");

        Assert.True(toolsNav.IsExpanded);

        toolsNav.IsExpanded = false;
        chat.RaiseToolsChanged();

        Assert.False(toolsNav.IsExpanded);
    }

    [Fact]
    public async Task SelectedEditorItem_Retained_AfterToolsChange()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var root = Assert.Single(viewModel.EditorItems);
        // Issue #819: Use chat-details instead of removed chat-diagnostics
        var chatDetailsNav = root.Children.First(c => c.Id == "chat-details");

        viewModel.SelectedEditorItem = chatDetailsNav;

        chat.RaiseToolsChanged();

        Assert.Same(chatDetailsNav, viewModel.SelectedEditorItem);
    }

    [Fact]
    public async Task SubAgentNavItem_HasChatDetailsChild()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);

        Assert.Contains(subAgentNav.Children, c => c.Id == "chat-details");
    }

    [Fact]
    public async Task SubAgentNavItem_HasToolsChild()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);

        Assert.Contains(subAgentNav.Children, c => c.Id == "chat-tools");
    }

    [Fact]
    public async Task SubAgentNavItem_DoesNotHaveBackgroundTasksChild()
    {
        // Issue #1030: The "Background tasks" section was removed from every sub-agent too.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);

        Assert.DoesNotContain(subAgentNav.Children, c => c.Id == "chat-background-tasks");
    }

    [Fact]
    public async Task EditorItems_DoesNotContainBackgroundTasksSection()
    {
        // Issue #1030: The "Background tasks" placeholder section was removed.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var root = Assert.Single(viewModel.EditorItems);
        Assert.DoesNotContain(root.Children, c => c.Id == "chat-background-tasks");
    }

    [Fact]
    public async Task EditorItems_RootChildren_AreChatDetailsToolsSubAgentsLogs()
    {
        // Issue #1030: The root nav item's children are exactly Chat details, Tools, Sub-agents.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var root = Assert.Single(viewModel.EditorItems);
        Assert.Equal(
            new[] { "chat-details", "chat-tools", "chat-sub-agents", "chat-logs" },
            root.Children.Select(c => c.Id).ToArray());
    }

    [Fact]
    public async Task AllDetailContents_LogsNavItem_HasCachedDocument()
    {
        var chat = await CreateChatAsync();
        using var factory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", factory, TaskScheduler.Default);
        var root = Assert.Single(viewModel.EditorItems);
        var logs = root.Children.Single(child => child.Id == "chat-logs");
        var logsItem = Assert.Single(viewModel.AllDetailContents,
            item => ReferenceEquals(item.Content, logs.DetailContent));
        var document = viewModel.DetailDockFactory.GetDocument(logsItem);

        viewModel.SelectedEditorItem = logs;
        Assert.Same(document, viewModel.DetailDockFactory.ActiveDocument);
        viewModel.SelectedEditorItem = root;
        viewModel.SelectedEditorItem = logs;
        Assert.Same(document, viewModel.DetailDockFactory.ActiveDocument);
    }

    [Fact]
    public async Task AgentViewModel_LogsDetail_ConcurrentParentAndChild_IsolatesOwningSession()
    {
        var chat = await CreateChatAsync();
        using var parentMemory = new ObservableLoggerFactory();
        await using var parent = new AgentViewModel(chat, "parent", "", parentMemory, TaskScheduler.Default);
        await AddSubAgentAsync(chat, "child-log", "Child");
        var child = parent.SubAgentsContainer.Slots.Single().SubAgentViewModel;

        parentMemory.CreateLogger("SafeLifecycle").LogInformation("parent-safe-event");
        child.LoggerFactory.CreateLogger("SafeLifecycle").LogInformation("child-safe-event");
        await AgentChatLogsDetailViewModelTests.ObserveEntryAsync(parent.LogsDetail, "parent-safe-event");
        await AgentChatLogsDetailViewModelTests.ObserveEntryAsync(child.LogsDetail, "child-safe-event");

        Assert.NotSame(parent.LogsDetail, child.LogsDetail);
        Assert.Contains(parent.LogsDetail.Entries, entry => entry.Contains("parent-safe-event", StringComparison.Ordinal));
        Assert.DoesNotContain(parent.LogsDetail.Entries, entry => entry.Contains("child-safe-event", StringComparison.Ordinal));
        Assert.Contains(child.LogsDetail.Entries, entry => entry.Contains("child-safe-event", StringComparison.Ordinal));
        Assert.DoesNotContain(child.LogsDetail.Entries, entry => entry.Contains("parent-safe-event", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubAgentsNavItem_OnCreation_IsExpanded()
    {
        // Issue #1031: The Sub-agents nav item is expanded by default.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");

        Assert.True(subAgentsNav.IsExpanded);
    }

    [Fact]
    public async Task SubAgentNavItem_HasSubAgentsChild()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);

        Assert.Contains(subAgentNav.Children, c => c.Id == "chat-sub-agents");
    }

    [Fact]
    public async Task SubAgentNavItem_DoesNotHave_DiagnosticsChild()
    {
        // Issue #819: Diagnostics tab was removed.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);

        Assert.DoesNotContain(subAgentNav.Children, c => c.Id == "chat-diagnostics");
    }

    [Fact]
    public async Task SubAgentNavItem_DetailContent_IsOwnConversationDetail()
    {
        // Fix #1112: each sub-agent nav item's DetailContent is now the sub-agent's OWN
        // ConversationDetail so it resolves to a distinct AgentDetailDocumentItem — the shared
        // SubAgentsContainer is reserved for the group node's browser card only.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);
        var childVm = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "sub-agent-1").SubAgentViewModel;

        Assert.NotNull(subAgentNav.DetailContent);
        Assert.Same(childVm.ConversationDetail, subAgentNav.DetailContent);
        Assert.NotSame(viewModel.SubAgentsContainer, subAgentNav.DetailContent);
    }

    [Fact]
    public async Task SubAgentNavItem_Children_LiveUpdate_OnToolsChange()
    {
        await using var server = await TestMcpServerProcess.StartAsync();
        var subAgentDef = CreateMcpAgentDefinition(server.BoundUrl);
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        await chat.GetOrCreateAsync("sub-agent-1", subAgentDef, "tool-call-sub-agent-1", TestContext.Current.CancellationToken);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);
        var subAgentToolsNav = subAgentNav.Children.First(c => c.Id == "chat-tools");

        var subAgentChat = (AgentChat)chat.SubAgents.First(s => s.AgentId == "sub-agent-1");
        await subAgentChat.Initialization.WaitAsync(TestContext.Current.CancellationToken);
        var childVm = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "sub-agent-1").SubAgentViewModel;
        await childVm.ToolProjectionIdle;
        AssertToolProjection(childVm, subAgentChat.GetToolSnapshot());

        Assert.Single(subAgentToolsNav.Children);
    }

    [Fact]
    public async Task SubAgentNavItem_Recursive_HasGrandchildStructure()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);

        var subAgent = (AgentChat)chat.SubAgents.First();
        await AddSubAgentAsync(subAgent, "sub-sub-agent", "Sub Sub Agent");

        var subAgentSubAgentsNav = subAgentNav.Children.First(c => c.Id == "chat-sub-agents");

        var grandchildNav = Assert.Single(subAgentSubAgentsNav.Children);
        // Logs is the fourth fixed child, including in nested editors.
        Assert.Equal(4, grandchildNav.Children.Count);
    }

    [Fact]
    public async Task AllDetailContents_AllFixedItemsPresent_OnConstruction()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        // Issue #1035: the flat detail-content collection carries one item per fixed nav node
        // (conversation, chat-details, chat-tools, chat-sub-agents, chat-logs). Count is 5.
        Assert.Equal(5, viewModel.AllDetailContents.Count);
    }

    [Fact]
    public async Task EveryNavNodeDetail_HasGeneratedDocument()
    {
        // Issue #1035: each fixed nav node's DetailContent resolves to exactly one cached document.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var root = Assert.Single(viewModel.EditorItems);
        foreach (var nav in new[] { root }.Concat(root.Children))
        {
            var item = viewModel.AllDetailContents.SingleOrDefault(
                i => ReferenceEquals(i.Content, nav.DetailContent));
            Assert.NotNull(item);
            Assert.NotNull(viewModel.DetailDockFactory.GetDocument(item));
        }
    }

    [Fact]
    public async Task SelectNode_ActivatesMatchingDocument()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var root = Assert.Single(viewModel.EditorItems);
        var chatDetailsNav = root.Children.First(c => c.Id == "chat-details");

        viewModel.SelectedEditorItem = chatDetailsNav;

        Assert.NotNull(viewModel.SelectedDetailDocument);
        Assert.Same(chatDetailsNav.DetailContent, viewModel.SelectedDetailDocument!.DetailContent);
        Assert.Same(viewModel.SelectedDetailDocument, viewModel.DetailDockFactory.ActiveDocument);
    }

    [Fact]
    public async Task DetailDocument_ConversationRemainsCached_AfterNavigation()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var root = Assert.Single(viewModel.EditorItems);
        var conversationItem = viewModel.AllDetailContents
            .First(i => i.Content is AgentChatConversationDetailViewModel);
        var conversationDoc = viewModel.DetailDockFactory.GetDocument(conversationItem);
        Assert.NotNull(conversationDoc);

        viewModel.SelectedEditorItem = root.Children.First(c => c.Id == "chat-details");
        viewModel.SelectedEditorItem = root;

        // Same cached Document instance survives navigating away and back (content cached).
        Assert.Same(conversationDoc, viewModel.DetailDockFactory.GetDocument(conversationItem));
    }

    [Fact]
    public async Task DetailDocument_Reused_AcrossSelectionSwitches()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var root = Assert.Single(viewModel.EditorItems);
        var items = viewModel.AllDetailContents.ToArray();
        var docsBefore = items.Select(i => viewModel.DetailDockFactory.GetDocument(i)).ToArray();

        viewModel.SelectedEditorItem = root.Children.First(c => c.Id == "chat-details");
        viewModel.SelectedEditorItem = root.Children.First(c => c.Id == "chat-tools");
        viewModel.SelectedEditorItem = root;

        var docsAfter = items.Select(i => viewModel.DetailDockFactory.GetDocument(i)).ToArray();
        for (int i = 0; i < docsBefore.Length; i++)
        {
            Assert.Same(docsBefore[i], docsAfter[i]);
        }
    }

    [Fact]
    public async Task ConversationDocument_Active_OnConstruction()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        // The conversation node is selected by default, so its cached document is active.
        Assert.NotNull(viewModel.SelectedDetailDocument);
        Assert.IsType<AgentChatConversationDetailViewModel>(viewModel.SelectedDetailDocument!.DetailContent);
        Assert.Same(viewModel.SelectedDetailDocument, viewModel.DetailDockFactory.ActiveDocument);
    }

    [Fact]
    public async Task SubAgentsNavItem_StaysExpanded_WhenFirstSubAgentAdded()
    {
        // Issue #1031: The Sub-agents node is expanded by default and stays expanded
        // when the first sub-agent is added.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");

        Assert.True(subAgentsNav.IsExpanded);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        Assert.True(subAgentsNav.IsExpanded);
    }

    [Fact]
    public async Task SubAgentsNavItem_RemainsExpanded_WhenSecondSubAgentAdded()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");
        Assert.True(subAgentsNav.IsExpanded);

        await AddSubAgentAsync(chat, "sub-agent-2", "Sub Agent 2");
        Assert.True(subAgentsNav.IsExpanded);
    }

    [Fact]
    public async Task AgentViewModel_ConversationDetail_ReturnsSameInstanceUsedByEditorTree()
    {
        // Issue #903: Verify that the ConversationDetail property exposes the same instance
        // that is used by the editor tree, so the SubAgentSlotViewModel DataTemplate can bind
        // directly to it without instantiating a nested AgentChatEditorControl.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var conversationDetail = viewModel.ConversationDetail;
        Assert.NotNull(conversationDetail);

        var root = Assert.Single(viewModel.EditorItems);
        var rootDetailContent = root.DetailContent;

        Assert.Same(conversationDetail, rootDetailContent);
    }

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

    private static AgentDefinition CreateMcpAgentDefinition(string endpoint)
        => AgentDefinitionLoader.LoadAgentFromJson(
            $$"""
            {
              "kind": "prompt",
              "name": "test-agent",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": [
                {
                  "kind": "mcp",
                  "name": "test-mcp",
                  "serverName": "test-mcp",
                  "connection": {
                    "kind": "Anonymous",
                    "endpoint": "{{endpoint}}"
                  }
                }
              ]
            }
            """);

    private static Task<AgentChat> CreateChatAsync()
        => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = CreateAgentDefinition(),
            });

    private static Task<AgentChat> CreateChatWithMcpAsync(string mcpEndpoint)
        => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = CreateMcpAgentDefinition(mcpEndpoint),
            });

    private static async Task AddSubAgentAsync(AgentChat chat, string agentId, string displayName)
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

        await chat.GetOrCreateAsync(agentId, definition, $"tool-call-{agentId}", TestContext.Current.CancellationToken);
    }

}
