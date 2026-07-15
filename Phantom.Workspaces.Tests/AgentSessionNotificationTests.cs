using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Services.Notifications;
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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
}
