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
        private readonly List<(TabDescriptor Tab, string? Reason)> calls = [];

        public string? ActiveTabId { get; set; }

        public IReadOnlyList<NotificationEntry> Notifications => [];

        public event EventHandler? NotificationsChanged;

        public IReadOnlyList<(TabDescriptor Tab, string? Reason)> Calls => this.calls;

        public void Notify(TabDescriptor tab, string? reason)
        {
            lock (this.calls)
            {
                this.calls.Add((tab, reason));
            }

            this.NotifyCallReceived?.Invoke(tab, reason);
        }

        public void MarkRead(string tabId) { }

        public event Action<TabDescriptor, string?>? NotifyCallReceived;
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
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", loggerFactory);

        var notificationService = new FakeNotificationService { ActiveTabId = "other-tab" };
        var notifyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notificationService.NotifyCallReceived += (tab, reason) =>
        {
            if (reason is not null)
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
            Assert.Contains(notificationService.Calls, call => call.Reason is not null && call.Tab.TabId == "agent-tab-1");
        }
    }

    [Fact]
    public async Task AgentSessionNotification_WhenAgentIsActiveTab_NotificationReasonStillPassedToService()
    {
        // INotificationService.Notify is always called — it is the service's responsibility
        // to mark the notification read when the tab is active.  This test verifies that
        // AgentSessionWorkspaceTabViewModel passes the reason through unconditionally.
        await using var agentChat = await CreateEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", loggerFactory);

        var notificationService = new FakeNotificationService { ActiveTabId = "agent-tab-active" };
        var notifyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notificationService.NotifyCallReceived += (tab, reason) =>
        {
            if (reason is not null)
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
            Assert.Contains(notificationService.Calls, call => call.Reason is not null && call.Tab.TabId == "agent-tab-active");
        }
    }

    [Fact]
    public async Task AgentSessionNotification_WhenAgentStartsNewRun_ClearsNotification()
    {
        await using var agentChat = await CreateEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", loggerFactory);

        var notificationService = new FakeNotificationService { ActiveTabId = "other-tab" };

        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "agent-tab-2",
            Title = "Agent",
            NotificationService = notificationService,
        };
        tab.SetReady(agentViewModel, loggerFactory);

        // First run — agent becomes idle → notification posted.
        var firstIdleTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notificationService.NotifyCallReceived += (tab, reason) =>
        {
            if (reason is not null)
            {
                firstIdleTcs.TrySetResult();
            }
        };

        agentChat.EnqueueUserMessage("hello");
        await WaitForRunningItemsEmptyAsync(agentChat);

        using var cts1 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
        cts1.Token.Register(() => firstIdleTcs.TrySetCanceled());
        await firstIdleTcs.Task;

        // Second run — agent starts processing → clear (null reason) should be called.
        var clearTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notificationService.NotifyCallReceived += (tabDesc, reason) =>
        {
            if (reason is null && tabDesc.TabId == "agent-tab-2")
            {
                clearTcs.TrySetResult();
            }
        };

        agentChat.EnqueueUserMessage("hello again");

        using var cts2 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
        cts2.Token.Register(() => clearTcs.TrySetCanceled());
        await clearTcs.Task;

        lock (notificationService.Calls)
        {
            Assert.Contains(notificationService.Calls, call => call.Reason is null && call.Tab.TabId == "agent-tab-2");
        }

        await WaitForRunningItemsEmptyAsync(agentChat);
    }
}
