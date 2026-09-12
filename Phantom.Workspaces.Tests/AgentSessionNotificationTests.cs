using Avalonia.Headless.XUnit;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Services.Notifications;
using Phantom.Workspaces.Testing.Gui;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class AgentSessionNotificationTests
{
    private const string EchoAgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "test-agent",
          "model": {
            "id": "echo",
            "provider": "echo",
            "apiType": "Echo"
          },
          "tools": []
        }
        """;

    private sealed class FakeNotificationService : INotificationService
    {
        private readonly List<Notification> calls = [];

        public string? ActiveTabId { get; set; }

        public IReadOnlyList<NotificationEntry> Notifications => [];

        public bool HasActiveRun { get; private set; }

#pragma warning disable CS0067 // Required by INotificationService but never raised in this fake
        public event EventHandler? NotificationsChanged;
#pragma warning restore CS0067

        public IReadOnlyList<Notification> Calls => this.calls;

        public void Notify(Notification notification)
        {
            lock (this.calls)
            {
                this.calls.Add(notification);
            }

            this.HasActiveRun = notification.RunningState == RunningState.Running;
            this.NotifyCallReceived?.Invoke(notification);
        }

        public void Remove(string tabId) { }

        public void Remove(NotificationTargetRequest request) { }

        public void MarkRead(string tabId) { }

        public void MarkRead(NotificationTargetRequest request) { }

        public event Action<Notification>? NotifyCallReceived;
    }

    private static async Task<AgentChat> CreateEchoAgentChatAsync()
    {
        var agentDefinition = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentDefinitionJson);
        return await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = agentDefinition,
        });
    }

    private static async Task WaitForRunningItemsEmptyAsync(AgentChat chat)
    {
        var runningItems = chat.RunningItems;
        var cts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (runningItems.Count == 0)
            {
                cts.TrySetResult();
            }
        }

        ((INotifyCollectionChanged)runningItems).CollectionChanged += OnCollectionChanged;
        try
        {
            if (runningItems.Count == 0)
            {
                return;
            }

            using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            timeoutCts.Token.Register(() => cts.TrySetCanceled());
            await cts.Task;
        }
        finally
        {
            ((INotifyCollectionChanged)runningItems).CollectionChanged -= OnCollectionChanged;
        }
    }

    [AvaloniaFact]
    public async Task AgentSessionNotification_WhenAgentGoesIdle_PostsNotification()
    {
        await using var agentChat = await CreateEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var notificationService = new FakeNotificationService { ActiveTabId = "other-tab" };
        var notifyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notificationService.NotifyCallReceived += notification =>
        {
            if (notification.RunningState == RunningState.Idle)
            {
                notifyTcs.TrySetResult();
            }
        };

        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "agent-tab-1",
            Title = "Agent",
            NotificationService = notificationService,
        };
        tab.SetReady(agentViewModel, loggerFactory);

        agentChat.EnqueueUserMessage("hello");
        await WaitForRunningItemsEmptyAsync(agentChat);

        using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
        timeoutCts.Token.Register(() => notifyTcs.TrySetCanceled());
        await notifyTcs.Task;

        lock (notificationService.Calls)
        {
            Assert.Contains(notificationService.Calls, call =>
                call.RunningState == RunningState.Idle && call.TabDescriptor.TabId == "agent-tab-1");
        }
    }

    [AvaloniaFact]
    public async Task AgentSessionNotification_WhenAgentIsActiveTab_NotificationStillPassedToService()
    {
        // INotificationService.Notify is always called — it is the service's responsibility
        // to mark the notification read when the tab is active.  This test verifies that
        // AgentSessionWorkspaceTabViewModel passes the notification through unconditionally.
        await using var agentChat = await CreateEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var notificationService = new FakeNotificationService { ActiveTabId = "agent-tab-active" };
        var notifyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notificationService.NotifyCallReceived += notification =>
        {
            if (notification.RunningState == RunningState.Idle)
            {
                notifyTcs.TrySetResult();
            }
        };

        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "agent-tab-active",
            Title = "Agent",
            NotificationService = notificationService,
        };
        tab.SetReady(agentViewModel, loggerFactory);

        agentChat.EnqueueUserMessage("hello");
        await WaitForRunningItemsEmptyAsync(agentChat);

        using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
        timeoutCts.Token.Register(() => notifyTcs.TrySetCanceled());
        await notifyTcs.Task;

        lock (notificationService.Calls)
        {
            Assert.Contains(notificationService.Calls, call =>
                call.RunningState == RunningState.Idle && call.TabDescriptor.TabId == "agent-tab-active");
        }
    }

    [AvaloniaFact]
    public async Task AgentSessionNotification_WhenAgentStartsNewRun_PostsRunningNotification()
    {
        await using var agentChat = await CreateEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var notificationService = new FakeNotificationService { ActiveTabId = "other-tab" };

        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "agent-tab-2",
            Title = "Agent",
            NotificationService = notificationService,
        };
        tab.SetReady(agentViewModel, loggerFactory);

        // First run — agent becomes idle → idle notification posted.
        var firstIdleTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notificationService.NotifyCallReceived += notification =>
        {
            if (notification.RunningState == RunningState.Idle)
            {
                firstIdleTcs.TrySetResult();
            }
        };

        agentChat.EnqueueUserMessage("hello");
        await WaitForRunningItemsEmptyAsync(agentChat);

        using var cts1 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
        cts1.Token.Register(() => firstIdleTcs.TrySetCanceled());
        await firstIdleTcs.Task;

        // Second run — agent starts → running notification should be posted.
        var runningTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notificationService.NotifyCallReceived += notification =>
        {
            if (notification.RunningState == RunningState.Running
                && notification.NotificationState == NotificationState.Interesting
                && notification.TabDescriptor.TabId == "agent-tab-2")
            {
                runningTcs.TrySetResult();
            }
        };

        agentChat.EnqueueUserMessage("hello again");

        using var cts2 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
        cts2.Token.Register(() => runningTcs.TrySetCanceled());
        await runningTcs.Task;

        lock (notificationService.Calls)
        {
            Assert.Contains(notificationService.Calls, call =>
                call.RunningState == RunningState.Running
                && call.NotificationState == NotificationState.Interesting
                && call.TabDescriptor.TabId == "agent-tab-2");
        }

        await WaitForRunningItemsEmptyAsync(agentChat);
    }

    [AvaloniaFact]
    public async Task AgentSessionNotification_WhenAgentGoesIdle_TabDescriptorHasTabTitleFromViewModelTitle()
    {
        await using var agentChat = await CreateEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var notificationService = new FakeNotificationService { ActiveTabId = "other-tab" };
        var notifyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notificationService.NotifyCallReceived += notification =>
        {
            if (notification.RunningState == RunningState.Idle)
            {
                notifyTcs.TrySetResult();
            }
        };

        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "agent-tab-title-test",
            Title = "My Full Agent Title",
            NotificationService = notificationService,
        };
        tab.SetReady(agentViewModel, loggerFactory);

        agentChat.EnqueueUserMessage("hello");
        await WaitForRunningItemsEmptyAsync(agentChat);

        using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
        timeoutCts.Token.Register(() => notifyTcs.TrySetCanceled());
        await notifyTcs.Task;

        lock (notificationService.Calls)
        {
            Assert.Contains(notificationService.Calls,
                call => call.RunningState == RunningState.Idle && call.TabDescriptor.TabTitle == "My Full Agent Title");
        }
    }

    [AvaloniaFact]
    public async Task Notify_SetsWorkspaceId_FromWorkspacePaneId()
    {
        await using var agentChat = await CreateEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var notificationService = new FakeNotificationService { ActiveTabId = "other-tab" };
        var notifyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notificationService.NotifyCallReceived += notification =>
        {
            if (notification.RunningState == RunningState.Idle)
            {
                notifyTcs.TrySetResult();
            }
        };

        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "agent-tab-workspace-test",
            Title = "Agent",
            WorkspacePaneId = "workspace-pane-1",
            NotificationService = notificationService,
        };
        tab.SetReady(agentViewModel, loggerFactory);

        agentChat.EnqueueUserMessage("hello");
        await WaitForRunningItemsEmptyAsync(agentChat);

        using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
        timeoutCts.Token.Register(() => notifyTcs.TrySetCanceled());
        await notifyTcs.Task;

        lock (notificationService.Calls)
        {
            Assert.Contains(notificationService.Calls,
                call => call.RunningState == RunningState.Idle
                    && call.TabDescriptor.WorkspaceId == "workspace-pane-1");
        }
    }

    [AvaloniaFact]
    public async Task AgentSessionWorkspaceTabViewModel_RecordsEvent_StampsTimestampFromInjectedTimeProvider()
    {
        await using var agentChat = await CreateEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var start = new DateTimeOffset(2024, 7, 4, 10, 0, 0, TimeSpan.Zero);
        var fake = new FakeTimeProvider(start);

        var notificationService = new FakeNotificationService { ActiveTabId = "other-tab" };

        // Construct via the internal TimeProvider ctor so all three event-stamp sites
        // (running / idle / streaming transitions in OnAgentPropertyChanged) read the fake clock.
        var tab = new AgentSessionWorkspaceTabViewModel(fake)
        {
            Id = "agent-tab-stamp",
            Title = "Agent",
            NotificationService = notificationService,
        };
        tab.SetReady(agentViewModel, loggerFactory);

        // First run: fires a Running notification then, on going idle, an Idle notification —
        // both stamped from the (un-advanced) fake clock.
        var firstIdleTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void FirstIdleHandler(Notification notification)
        {
            if (notification.RunningState == RunningState.Idle)
            {
                firstIdleTcs.TrySetResult();
            }
        }

        notificationService.NotifyCallReceived += FirstIdleHandler;

        agentChat.EnqueueUserMessage("hello");
        await WaitForRunningItemsEmptyAsync(agentChat);

        using (var cts1 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            cts1.Token.Register(() => firstIdleTcs.TrySetCanceled());
            await firstIdleTcs.Task;
        }

        notificationService.NotifyCallReceived -= FirstIdleHandler;

        lock (notificationService.Calls)
        {
            Assert.Contains(notificationService.Calls, call =>
                call.RunningState == RunningState.Running && call.When == start.UtcDateTime);
            Assert.Contains(notificationService.Calls, call =>
                call.RunningState == RunningState.Idle && call.When == start.UtcDateTime);

            // Every event so far was stamped from the injected provider, never wall-clock.
            Assert.All(notificationService.Calls, call => Assert.Equal(start.UtcDateTime, call.When));
        }

        // Advance the fake clock to prove the stamps read the injected provider, not wall-clock time.
        fake.Advance(TimeSpan.FromHours(3));
        var advanced = fake.GetUtcNow().UtcDateTime;

        var runningTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notificationService.NotifyCallReceived += notification =>
        {
            if (notification.RunningState == RunningState.Running
                && notification.NotificationState == NotificationState.Interesting
                && notification.TabDescriptor.TabId == "agent-tab-stamp")
            {
                runningTcs.TrySetResult();
            }
        };

        agentChat.EnqueueUserMessage("hello again");

        using (var cts2 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            cts2.Token.Register(() => runningTcs.TrySetCanceled());
            await runningTcs.Task;
        }

        lock (notificationService.Calls)
        {
            Assert.Contains(notificationService.Calls, call =>
                call.RunningState == RunningState.Running
                && call.NotificationState == NotificationState.Interesting
                && call.TabDescriptor.TabId == "agent-tab-stamp"
                && call.When == advanced);
        }

        await WaitForRunningItemsEmptyAsync(agentChat);
    }

    [AvaloniaFact]
    public async Task Notify_SetsWorkspaceId_FromOwningPane()
    {
        // #1135: When an agent-session tab is added to a WorkspacePaneViewModel, the pane
        // authoritatively stamps the tab's WorkspacePaneId with its own Id — overwriting any
        // stale value captured from SelectedWorkspacePane?.Id at construction time (e.g. during
        // workspace restore where the tab is created before it lands in its actual pane).
        // The subsequently posted notification's TabDescriptor.WorkspaceId reflects the pane
        // the tab actually lives in, so cross-workspace notification navigation resolves the
        // right owning workspace.
        await using var agentChat = await CreateEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var notificationService = new FakeNotificationService { ActiveTabId = "other-tab" };
        var notifyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notificationService.NotifyCallReceived += notification =>
        {
            if (notification.RunningState == RunningState.Idle)
            {
                notifyTcs.TrySetResult();
            }
        };

        // The tab is created with a stale/wrong workspace-pane id (mirroring the buggy
        // pre-#1135 behaviour where restore paths captured SelectedWorkspacePane?.Id at
        // construction time — a different pane than the one the tab was actually restored into).
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "agent-tab-owning-pane",
            Title = "Agent",
            WorkspacePaneId = "stale-active-pane",
            NotificationService = notificationService,
        };

        var owningPane = new WorkspacePaneViewModel(
            CreateWorkspaceEntity(),
            id: "owning-pane-1");
        owningPane.Tabs.Add(tab);

        // Adding the tab to the pane must stamp WorkspacePaneId with the pane's actual id.
        Assert.Equal("owning-pane-1", tab.WorkspacePaneId);

        tab.SetReady(agentViewModel, loggerFactory);
        agentChat.EnqueueUserMessage("hello");
        await WaitForRunningItemsEmptyAsync(agentChat);

        using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
        timeoutCts.Token.Register(() => notifyTcs.TrySetCanceled());
        await notifyTcs.Task;

        lock (notificationService.Calls)
        {
            Assert.Contains(notificationService.Calls, call =>
                call.TabDescriptor.TabId == "agent-tab-owning-pane"
                && call.TabDescriptor.WorkspaceId == "owning-pane-1");
            Assert.DoesNotContain(notificationService.Calls, call =>
                call.TabDescriptor.WorkspaceId == "stale-active-pane");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // #1451: Resume-notification suppression tests. These drive the restore window deterministically
    // via a controllable HistoryPopulated gate (SetHistoryPopulatedForTest) and control running
    // state via DeterministicTestChatClient — no timing/sleeps.
    // ---------------------------------------------------------------------------------------------

    private static async Task<(AgentChat Chat, DeterministicTestChatClient Client)> CreateControllableAgentChatAsync()
    {
        var client = new DeterministicTestChatClient();
        var agentDefinition = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentDefinitionJson);
        var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = agentDefinition,
            AgentServices = new AgentServices { ChatClientOverride = client },
        });
        return (chat, client);
    }

    private static async Task WaitForRunningItemsNonEmptyAsync(AgentChat chat)
    {
        var runningItems = chat.RunningItems;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (runningItems.Count > 0)
            {
                tcs.TrySetResult();
            }
        }

        ((INotifyCollectionChanged)runningItems).CollectionChanged += OnCollectionChanged;
        try
        {
            if (runningItems.Count > 0)
            {
                return;
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            timeoutCts.Token.Register(() => tcs.TrySetCanceled());
            await tcs.Task;
        }
        finally
        {
            ((INotifyCollectionChanged)runningItems).CollectionChanged -= OnCollectionChanged;
        }
    }

    // Completes once the agent view model has raised (and thus its earlier-subscribed tab handler has
    // processed) an IsChatRunning transition to the desired value. Because the tab subscribes to
    // PropertyChanged before this test handler, when this completes the tab has already observed the
    // same edge.
    private static async Task WaitForChatRunningAsync(AgentViewModel vm, bool desired)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AgentViewModel.IsChatRunning) && vm.IsChatRunning == desired)
            {
                tcs.TrySetResult();
            }
        }

        vm.PropertyChanged += Handler;
        try
        {
            if (vm.IsChatRunning == desired)
            {
                return;
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            timeoutCts.Token.Register(() => tcs.TrySetCanceled());
            await tcs.Task;
        }
        finally
        {
            vm.PropertyChanged -= Handler;
        }
    }

    [AvaloniaFact]
    public async Task ResumedSession_RestoredIdle_DoesNotRaiseNotification()
    {
        // A persisted session that rehydrates to idle must raise no notification on open.
        await using var agentChat = await CreateEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var notificationService = new FakeNotificationService { ActiveTabId = "other-tab" };
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "resume-idle",
            Title = "Agent",
            NotificationService = notificationService,
        };

        tab.SetReady(agentViewModel, loggerFactory);
        await tab.RestoreSettledTask;

        lock (notificationService.Calls)
        {
            Assert.Empty(notificationService.Calls);
        }
    }

    [AvaloniaFact]
    public async Task ResumedSession_RestoreTimeRunningTransition_IsSuppressed()
    {
        // A 0 -> N -> 0 running-state change occurring while the session is still restoring must
        // produce no Running/Completed/Interrupted notification.
        var (agentChat, client) = await CreateControllableAgentChatAsync();
        await using var chatScope = agentChat;
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        // Hold the restore window open by gating HistoryPopulated on a task we control.
        var historyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agentViewModel.SetHistoryPopulatedForTest(historyGate.Task);

        var notificationService = new FakeNotificationService { ActiveTabId = "other-tab" };
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "resume-transition",
            Title = "Agent",
            NotificationService = notificationService,
        };
        tab.SetReady(agentViewModel, loggerFactory);

        // Drive a genuine 0 -> N -> 0 while the window is still open (isRestoring == true).
        var stream = client.EnqueueStreamingResponse();
        agentChat.EnqueueUserMessage("restore-run");
        await WaitForChatRunningAsync(agentViewModel, desired: true);

        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "done"));
        stream.Complete();
        await WaitForChatRunningAsync(agentViewModel, desired: false);

        // Now close the restore window and let it settle.
        historyGate.SetResult();
        await tab.RestoreSettledTask;

        lock (notificationService.Calls)
        {
            Assert.Empty(notificationService.Calls);
        }
    }

    [AvaloniaFact]
    public async Task ResumedSession_PersistedWhileRunning_SettlesWithoutInterruptedNotification()
    {
        // A session restored with running items already present, settling to idle during restore,
        // must not emit an Interrupted/Completed Interesting notification.
        var (agentChat, client) = await CreateControllableAgentChatAsync();
        await using var chatScope = agentChat;
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        // Session is mid-run when rehydrated: start (and hold) a run BEFORE SetReady so the tab seeds
        // from a running state. Keep the restore window open via the history gate.
        var historyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agentViewModel.SetHistoryPopulatedForTest(historyGate.Task);

        var stream = client.EnqueueStreamingResponse();
        agentChat.EnqueueUserMessage("mid-run");
        await WaitForRunningItemsNonEmptyAsync(agentChat);
        Assert.True(agentViewModel.IsChatRunning);

        var notificationService = new FakeNotificationService { ActiveTabId = "other-tab" };
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "resume-mid-run",
            Title = "Agent",
            NotificationService = notificationService,
        };
        tab.SetReady(agentViewModel, loggerFactory);

        // The persisted run finishes while still restoring: N -> 0 must be suppressed.
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "done"));
        stream.Complete();
        await WaitForChatRunningAsync(agentViewModel, desired: false);

        historyGate.SetResult();
        await tab.RestoreSettledTask;

        lock (notificationService.Calls)
        {
            Assert.DoesNotContain(notificationService.Calls, call =>
                call.NotificationState == NotificationState.Interesting);
        }
    }

    [AvaloniaFact]
    public async Task ResumedSession_AfterRestoreCompletes_NewUserRunRaisesNotification()
    {
        // Guard against over-suppression: once restore completes, a genuinely new user-initiated run
        // must still raise the expected Running notification.
        var (agentChat, client) = await CreateControllableAgentChatAsync();
        await using var chatScope = agentChat;
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var notificationService = new FakeNotificationService { ActiveTabId = "other-tab" };
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "post-restore-run",
            Title = "Agent",
            NotificationService = notificationService,
        };
        tab.SetReady(agentViewModel, loggerFactory);
        await tab.RestoreSettledTask;

        // A brand-new user run after restore settles.
        var runningTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notificationService.NotifyCallReceived += notification =>
        {
            if (notification.RunningState == RunningState.Running
                && notification.NotificationState == NotificationState.Interesting
                && notification.TabDescriptor.TabId == "post-restore-run")
            {
                runningTcs.TrySetResult();
            }
        };

        var stream = client.EnqueueStreamingResponse();
        agentChat.EnqueueUserMessage("new-run");

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            cts.Token.Register(() => runningTcs.TrySetCanceled());
            await runningTcs.Task;
        }

        lock (notificationService.Calls)
        {
            Assert.Contains(notificationService.Calls, call =>
                call.RunningState == RunningState.Running
                && call.NotificationState == NotificationState.Interesting
                && call.TabDescriptor.TabId == "post-restore-run");
        }

        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "done"));
        stream.Complete();
        await WaitForRunningItemsEmptyAsync(agentChat);
    }

    [AvaloniaFact]
    public async Task SetReady_SeedsWasRunningFromSettledState_NoInitialTransitionNotification()
    {
        // A session seeded running at SetReady but settling to idle must re-baseline wasRunning from
        // the settled (idle) state: the phantom N -> 0 restore edge raises nothing, and a subsequent
        // genuine run is correctly detected as a 0 -> N edge (proving the baseline is idle).
        var (agentChat, client) = await CreateControllableAgentChatAsync();
        await using var chatScope = agentChat;
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var historyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agentViewModel.SetHistoryPopulatedForTest(historyGate.Task);

        // Running at SetReady (transient restore-time running state).
        var stream1 = client.EnqueueStreamingResponse();
        agentChat.EnqueueUserMessage("mid-run");
        await WaitForRunningItemsNonEmptyAsync(agentChat);
        Assert.True(agentViewModel.IsChatRunning);

        var notificationService = new FakeNotificationService { ActiveTabId = "other-tab" };
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "seed-settled",
            Title = "Agent",
            NotificationService = notificationService,
        };
        tab.SetReady(agentViewModel, loggerFactory);

        // The transient run settles to idle while restoring (phantom N -> 0 — must be suppressed).
        stream1.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "done"));
        stream1.Complete();
        await WaitForChatRunningAsync(agentViewModel, desired: false);

        historyGate.SetResult();
        await tab.RestoreSettledTask;

        // No phantom restore edge produced a notification.
        lock (notificationService.Calls)
        {
            Assert.Empty(notificationService.Calls);
        }

        // A subsequent genuine run must be detected as a fresh 0 -> N edge (baseline is idle).
        var runningTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notificationService.NotifyCallReceived += notification =>
        {
            if (notification.RunningState == RunningState.Running
                && notification.NotificationState == NotificationState.Interesting
                && notification.TabDescriptor.TabId == "seed-settled")
            {
                runningTcs.TrySetResult();
            }
        };

        var stream2 = client.EnqueueStreamingResponse();
        agentChat.EnqueueUserMessage("new-run");

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            cts.Token.Register(() => runningTcs.TrySetCanceled());
            await runningTcs.Task;
        }

        lock (notificationService.Calls)
        {
            Assert.Contains(notificationService.Calls, call =>
                call.RunningState == RunningState.Running
                && call.NotificationState == NotificationState.Interesting
                && call.TabDescriptor.TabId == "seed-settled");
        }

        stream2.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "done"));
        stream2.Complete();
        await WaitForRunningItemsEmptyAsync(agentChat);
    }

    private static SubscribedEntityViewModel CreateWorkspaceEntity()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """
            {
              "entity-id": "22222222-2222-2222-2222-222222222222",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Test Owning Pane" }
            }
            """);
        return new SubscribedEntityViewModel(
            new Phantom.Workspaces.Data.EntitySnapshot
            {
                EntityId = new Phantom.Workspaces.Data.EntityId("22222222-2222-2222-2222-222222222222"),
                ConcurrencyTag = new Phantom.Workspaces.Data.ConcurrencyTag("1"),
                ModifiedTime = new Phantom.Workspaces.Data.Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = Array.Empty<Phantom.Workspaces.Data.EntitySnapshot>(),
            });
    }
}
