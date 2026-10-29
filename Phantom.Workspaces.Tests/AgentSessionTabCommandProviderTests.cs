using Dock.Model.Controls;
using Dock.Model.Core;
using Moq;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services.TabCommands;
using Phantom.Workspaces.ViewModels;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace Phantom.Workspaces.Tests;

public sealed class AgentSessionTabCommandProviderTests
{
    [Fact]
    public async Task AgentSessionTabCommands_NonAgentTab_ContributesNoRestart()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity("pane-non-agent"));
        var tab = new PlainTab
        {
            Id = "plain-tab",
            Title = "Plain",
            Entity = CreateWorkspaceEntity("plain-tab"),
        };
        var context = CreateContext(vm, pane, tab);

        Assert.Empty(AgentSessionTabCommandProvider.Instance.GetCommands(context));
    }

    [Fact]
    public async Task AgentSessionTabCommands_ConsentCancelled_DoesNotCallLifecycleService()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var restartService = new RecordingRestartService();
        vm.AgentSessionRestartService = restartService;
        vm.AgentSessionRestartConsentProvider = new DelegateConsentProvider((_, _) => Task.FromResult(false));

        var pendingChat = CreateChat(hasPendingWork: true);
        var tab = CreateSessionTab("session-consent", pendingChat);
        var context = CreateContext(vm, new WorkspacePaneViewModel(CreateWorkspaceEntity("pane-consent")), tab);
        var command = Assert.Single(tab.TabCommands.GetCommands(context));
        var result = await context.Invoker.InvokeAsync(command, context, TestContext.Current.CancellationToken);

        Assert.Equal(TabCommandResultKind.Cancelled, result.Kind);
        Assert.Equal(0, restartService.RestartCallCount);
    }

    [Fact]
    public async Task AgentSessionTabCommands_RuntimeChangesDuringConsent_RejectsStaleRequest()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var restartService = new RecordingRestartService();
        var consentGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.AgentSessionRestartService = restartService;
        vm.AgentSessionRestartConsentProvider = new DelegateConsentProvider((_, _) => consentGate.Task);

        var pendingChat = CreateChat(hasPendingWork: true);
        var tab = CreateSessionTab("session-stale", pendingChat);
        var context = CreateContext(vm, new WorkspacePaneViewModel(CreateWorkspaceEntity("pane-stale")), tab);
        var command = Assert.Single(tab.TabCommands.GetCommands(context));

        var invokeTask = context.Invoker.InvokeAsync(command, context, TestContext.Current.CancellationToken);
        var replacement = new WorkspaceDocument(tab)
        {
            Owner = context.DocumentDock,
        };
        context.Pane.RegisterDocument(tab.Id, replacement);
        consentGate.TrySetResult(true);
        var result = await invokeTask;

        Assert.Equal(TabCommandResultKind.Rejected, result.Kind);
        Assert.Equal(0, restartService.RestartCallCount);
    }

    private static AgentSessionWorkspaceTabViewModel CreateSessionTab(string sessionId, IAgentChat chat)
    {
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = $"tab-{sessionId}",
            Title = sessionId,
            Entity = CreateWorkspaceEntity(sessionId),
            AgentSessionId = sessionId,
        };
        tab.SetLease(new RunningAgentChatLease(new AgentSessionId(sessionId), chat, () => ValueTask.CompletedTask));
        return tab;
    }

    private static IAgentChat CreateChat(bool hasPendingWork)
    {
        var chat = new Mock<IAgentChat>();
        chat.SetupGet(value => value.HistoryPopulated).Returns(hasPendingWork
            ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task
            : Task.CompletedTask);
        chat.SetupGet(value => value.RunningItems).Returns(new AgentChatRunningItemCollection());
        chat.SetupGet(value => value.Modals).Returns(
            new ReadOnlyObservableCollection<AgentChatModal>(new ObservableCollection<AgentChatModal>()));
        chat.SetupGet(value => value.SubAgents).Returns(
            new ReadOnlyObservableCollection<IRunningSubAgent>(new ObservableCollection<IRunningSubAgent>()));
        var queues = new Mock<IAgentInputQueues>();
        queues.SetupGet(value => value.Snapshot).Returns(new AgentInputQueuesSnapshot
        {
            Revision = 1,
            Queues = ImmutableArray<AgentInputQueueSnapshot>.Empty,
        });
        chat.SetupGet(value => value.InputQueues).Returns(queues.Object);
        return chat.Object;
    }

    private static TabCommandContext CreateContext(
        MainWindowViewModel mainWindow,
        WorkspacePaneViewModel pane,
        WorkspaceTabViewModel tab)
    {
        pane.Tabs.Add(tab);
        var document = new WorkspaceDocument(tab);
        var dock = new WorkspaceContentDock
        {
            Id = $"dock-{tab.Id}",
            VisibleDockables = new ObservableCollection<IDockable> { document },
        };
        document.Owner = dock;
        pane.RegisterDocument(tab.Id, document);
        mainWindow.WorkspacePanes.Clear();
        mainWindow.WorkspacePanes.Add(pane);
        mainWindow.SelectedWorkspacePane = pane;
        return new TabCommandContext(mainWindow, pane, dock, document, tab, mainWindow.TabCommandInvoker);
    }

    private static SubscribedEntityViewModel CreateWorkspaceEntity(string id)
        => new(new EntitySnapshot
        {
            EntityId = new EntityId(Guid.NewGuid().ToString("N")),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = JsonDocument.Parse($$"""
                {
                  "entity-id": "{{id}}",
                  "display-name": { "default": "{{id}}" },
                  "entity-types": ["entity", "workspace"]
                }
                """).RootElement.Clone(),
            Relationships = [],
        });

    private sealed class PlainTab : WorkspaceTabViewModel;

    private sealed class RecordingRestartService : IAgentSessionRestartService
    {
        public int RestartCallCount { get; private set; }

        public AgentSessionRestartAvailability GetAvailability(
            AgentSessionWorkspaceTabViewModel tab,
            TabCommandContext context)
            => new()
            {
                IsAvailable = true,
            };

        public Task<AgentSessionRestartResult> RestartAsync(
            AgentSessionRestartRequest request,
            CancellationToken cancellationToken)
        {
            this.RestartCallCount++;
            return Task.FromResult(new AgentSessionRestartResult
            {
                Outcome = AgentSessionRestartOutcome.Succeeded,
            });
        }
    }

    private sealed class DelegateConsentProvider(
        Func<string, CancellationToken, Task<bool>> confirmAsync) : IAgentSessionRestartConsentProvider
    {
        public Task<bool> ConfirmAsync(string sessionName, CancellationToken cancellationToken)
            => confirmAsync(sessionName, cancellationToken);
    }
}
