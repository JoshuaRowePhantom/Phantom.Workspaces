using Avalonia.Headless.XUnit;
using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using AgentSchema;
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

        public void MarkRead(string tabId) { }

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
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory);

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
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory);

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
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory);

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
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory);

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
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory);

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
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory);

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
}
