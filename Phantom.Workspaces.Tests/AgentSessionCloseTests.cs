using Avalonia.Headless.XUnit;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services;
using System.Collections.ObjectModel;
using Phantom.Workspaces.ViewModels;
using MainWindowViewModel = Phantom.Workspaces.ViewModels.MainWindowViewModel;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class AgentSessionCloseTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CloseTab_DockAndKeyboard_AskBeforeAnyRemoval(bool dock)
    {
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await main.InitializeAsync();
        var (tab, chat) = await OpenAsync(main);
        chat.CreateRunningItem(new AgentChatHistoryItem { Role = ChatRole.Assistant });
        var decision = new GatedDecisionProvider();
        main.AgentSessionCloseDecisionProvider = decision;
        var pane = main.WorkspacePanes.Single(p => p.Tabs.Contains(tab));
        var document = pane.GetDocumentForTab(tab.Id)!;
        var factory = MultiRegionRestoreTestSupport.GetDockFactory(main);
        factory.SetActiveDockable(document);
        if (dock)
            factory.CloseDockable(document);
        else
            main.CloseActiveTabCommand.Execute(null);
        Assert.Equal(1, decision.Calls);
        Assert.Contains(tab, pane.Tabs);
        var pending = main.RequestCloseTabAsync(tab);
        decision.Completion.SetResult(AgentSessionCloseDecision.Cancel);
        Assert.False(await pending);
        Assert.Contains(tab, pane.Tabs);
        Assert.Same(document, pane.GetDocumentForTab(tab.Id));
    }

    [AvaloniaFact]
    public async Task CloseTab_LoadingTerminate_CancelsAttemptBeforeRemoval()
    {
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await main.InitializeAsync();
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "loading-cancel", Title = "Loading" };
        var cancellation = tab.StartupCancellation;
        await main.OpenTabAsync(tab);
        var decision = new GatedDecisionProvider();
        decision.Completion.SetResult(AgentSessionCloseDecision.Terminate);
        main.AgentSessionCloseDecisionProvider = decision;
        Assert.True(await main.RequestCloseTabAsync(tab));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.DoesNotContain(tab, main.WorkspacePanes.SelectMany(p => p.Tabs));
    }

    [AvaloniaFact]
    public async Task CloseTab_IdleBecomesActiveAtLeaseBoundary_ReevaluatesConsent()
    {
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await main.InitializeAsync();
        var (tab, chat) = await OpenAsync(main);
        var disposalCount = 0;
        tab.SetLease(new RunningAgentChatLease(new AgentSessionId(chat.AgentSessionId), chat,
            () => { disposalCount++; return ValueTask.CompletedTask; },
            prepareIdleClose: _ =>
            {
                chat.CreateRunningItem(new AgentChatHistoryItem { Role = ChatRole.Assistant });
                return Task.FromResult(false);
            }));
        var decision = new GatedDecisionProvider();
        main.AgentSessionCloseDecisionProvider = decision;
        var pending = main.RequestCloseTabAsync(tab);
        Assert.Equal(1, decision.Calls);
        decision.Completion.SetResult(AgentSessionCloseDecision.Cancel);
        Assert.False(await pending);
        Assert.Equal(0, disposalCount);
        Assert.Contains(tab, main.WorkspacePanes.SelectMany(p => p.Tabs));
        await chat.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task CloseTab_IdleLeaseReplacedWhileOwnerReleaseAwaits_PreservesReplacementViewer()
    {
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await main.InitializeAsync();
        var (tab, chat) = await OpenAsync(main);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.SetLease(new RunningAgentChatLease(new AgentSessionId(chat.AgentSessionId), chat,
            () => ValueTask.CompletedTask, prepareIdleClose: _ =>
            {
                entered.TrySetResult();
                return release.Task;
            }));
        var close = main.RequestCloseTabAsync(tab);
        await entered.Task;
        var disposed = 0;
        var replacement = new RunningAgentChatLease(new AgentSessionId(chat.AgentSessionId), chat,
            () => { disposed++; return ValueTask.CompletedTask; });
        tab.SetLease(replacement);
        release.SetResult(true);
        Assert.False(await close);
        Assert.Same(replacement, tab.Lease);
        Assert.Equal(0, disposed);
        Assert.Contains(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));
    }

    [AvaloniaFact]
    public async Task CloseTab_LoadingAttemptReplacedDuringConsent_DoesNotCancelNewAttempt()
    {
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await main.InitializeAsync();
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "retry-during-consent", Title = "Loading" };
        await main.OpenTabAsync(tab);
        var decision = new GatedDecisionProvider();
        main.AgentSessionCloseDecisionProvider = decision;
        var close = main.RequestCloseTabAsync(tab);
        tab.CancelStartup();
        tab.ResetFailedLoading();
        var replacementCancellation = tab.StartupCancellation;
        decision.Completion.SetResult(AgentSessionCloseDecision.Terminate);
        Assert.False(await close);
        Assert.False(replacementCancellation.IsCancellationRequested);
        Assert.Equal(AgentTabState.Loading, tab.State);
        Assert.Contains(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));
    }

    [AvaloniaFact]
    public async Task CloseTab_CancelAndRepeatedRequests_PreservesTabAndCoalescesDecision()
    {
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await main.InitializeAsync();
        var (tab, chat) = await OpenAsync(main);
        var running = chat.CreateRunningItem(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [new TextContent("working")] });
        var decision = new GatedDecisionProvider();
        main.AgentSessionCloseDecisionProvider = decision;
        var first = main.RequestCloseTabAsync(tab);
        var second = main.CloseTabByIdAsync(tab.Id);
        Assert.Same(first, second);
        Assert.Contains(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));
        Assert.Equal(1, decision.Calls);
        chat.CompleteRunningItem(running, false);
        decision.Completion.SetResult(AgentSessionCloseDecision.Cancel);
        Assert.False(await first);
        Assert.Contains(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));
    }

    [AvaloniaFact]
    public async Task CloseTab_RuntimeRowReplacedDuringDialog_DoesNotActOnReplacement()
    {
        var table = new GatedLifecycleTable();
        var services = new ApplicationServices(table, new AgentPersistenceStoreCache(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            Moq.Mock.Of<Phantom.Workspaces.Llm.Interfaces.Access.IAccessBroker>());
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel(applicationServices: services);
        await main.InitializeAsync();
        var (tab, chat) = await OpenAsync(main);
        var id = new AgentSessionId(chat.AgentSessionId);
        table.RunningSessions.Add(new RunningAgentChatWithEntityInfo(id, false,
            _ => throw new NotSupportedException(), "original", null));
        chat.CreateRunningItem(new AgentChatHistoryItem { Role = ChatRole.Assistant });
        var decision = new GatedDecisionProvider();
        main.AgentSessionCloseDecisionProvider = decision;
        var pending = main.RequestCloseTabAsync(tab);
        table.RunningSessions.Clear();
        table.RunningSessions.Add(new RunningAgentChatWithEntityInfo(id, false,
            _ => throw new NotSupportedException(), "replacement", null));
        decision.Completion.SetResult(AgentSessionCloseDecision.Terminate);
        Assert.False(await pending);
        Assert.Null(table.Operation);
        Assert.Contains(tab, main.WorkspacePanes.SelectMany(p => p.Tabs));
    }

    [AvaloniaFact]
    public async Task CloseTab_IdleAgent_ClosesWithoutPrompt()
    {
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await main.InitializeAsync();
        var (tab, _) = await OpenAsync(main);
        var decision = new GatedDecisionProvider();
        main.AgentSessionCloseDecisionProvider = decision;
        Assert.True(await main.RequestCloseTabAsync(tab));
        Assert.Equal(0, decision.Calls);
        Assert.DoesNotContain(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));
    }

    [AvaloniaFact]
    public async Task CloseTab_UnknownLoadingActivity_DoesNotReleaseTab()
    {
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await main.InitializeAsync();
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "loading", Title = "Loading" };
        await main.OpenTabAsync(tab);
        var decision = new GatedDecisionProvider();
        decision.Completion.SetResult(AgentSessionCloseDecision.Cancel);
        main.AgentSessionCloseDecisionProvider = decision;
        Assert.False(await main.RequestCloseTabAsync(tab));
        Assert.Contains(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseTab_LifecycleActionPending_DoesNotRemoveBeforeAcknowledgement(bool background)
    {
        var table = new GatedLifecycleTable();
        var services = new ApplicationServices(
            table,
            new AgentPersistenceStoreCache(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            Moq.Mock.Of<Phantom.Workspaces.Llm.Interfaces.Access.IAccessBroker>());
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel(applicationServices: services);
        await main.InitializeAsync();
        var entityId = new Phantom.Workspaces.Data.EntityId(Guid.NewGuid());
        var entity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            MainWindowIntegrationTests.GetEntityBroker(main),
            entityId,
            $$$"""{"entity-id":"{{{entityId}}}","entity-types":["entity","agent-session"],"names":[["tests","agent-sessions","{{{entityId}}}"]],"agent-session-id":"{{{Guid.NewGuid()}}}","display-name":{"default":"Close test"}}""");
        var (tab, chat) = await OpenAsync(main, entity);
        table.RunningSessions.Add(new RunningAgentChatWithEntityInfo(
            new AgentSessionId(chat.Information.AgentSessionId),
            false,
            _ => throw new NotSupportedException(),
            "Close test",
            entityId.ToString()));
        chat.CreateRunningItem(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [new TextContent("working")] });
        var decision = new GatedDecisionProvider();
        decision.Completion.SetResult(background ? AgentSessionCloseDecision.RunInBackground : AgentSessionCloseDecision.Terminate);
        main.AgentSessionCloseDecisionProvider = decision;
        var closing = main.RequestCloseTabAsync(tab);
        await table.Entered.Task;
        Assert.False(closing.IsCompleted);
        Assert.Contains(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));
        Assert.Equal(background ? "retain" : "terminate", table.Operation);
        table.Release.SetResult();
        Assert.True(await closing);
        Assert.DoesNotContain(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));
    }

    [AvaloniaFact]
    public async Task CloseTab_HeldQueueInput_IsNotIdle()
    {
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await main.InitializeAsync();
        var (tab, chat) = await OpenAsync(main);
        var queues = ((IAgentChat)chat).InputQueues;
        var queue = await queues.CreateQueueAsync(new CreateAgentInputQueueRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRevision = queues.Snapshot.Revision,
            Configuration = new AgentInputQueueConfiguration { Name = "Held", Immediacy = AgentInputQueueImmediacy.Held, Priority = 1 },
        });
        await queues.EnqueueAsync(new EnqueueAgentInputRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRevision = queues.Snapshot.Revision,
            TargetQueueId = queue.QueueId!,
            Messages = [new ChatMessage(ChatRole.User, "pending")],
        });
        Assert.True(AgentSessionCloseActivity.HasPendingWork(chat));
        var decision = new GatedDecisionProvider();
        main.AgentSessionCloseDecisionProvider = decision;
        var closing = main.RequestCloseTabAsync(tab);
        Assert.Equal(1, decision.Calls);
        decision.Completion.SetResult(AgentSessionCloseDecision.Cancel);
        Assert.False(await closing);
    }

    [AvaloniaTheory]
    [InlineData(AgentSessionCloseDecision.Terminate)]
    [InlineData(AgentSessionCloseDecision.RunInBackground)]
    public async Task CloseTab_UnavailableLifecycleOrPersistence_LeavesTabOpen(AgentSessionCloseDecision selected)
    {
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await main.InitializeAsync();
        var (tab, chat) = await OpenAsync(main);
        chat.CreateRunningItem(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [new TextContent("working")] });
        var decision = new GatedDecisionProvider();
        decision.Completion.SetResult(selected);
        main.AgentSessionCloseDecisionProvider = decision;
        Assert.False(await main.RequestCloseTabAsync(tab));
        Assert.Contains(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));
    }

    private static async Task<(AgentSessionWorkspaceTabViewModel Tab, AgentChat Chat)> OpenAsync(
        MainWindowViewModel main,
        SubscribedEntityViewModel? entity = null)
    {
        var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson("""
                {"kind":"prompt","name":"close-test","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}
                """),
        });
        var logger = new ObservableLoggerFactory();
        var agent = new AgentViewModel(chat, "close-test", "", logger, TaskScheduler.Default);
        var tab = new AgentSessionWorkspaceTabViewModel { Id = Guid.NewGuid().ToString(), Title = "Close test", AgentSessionId = chat.Information.AgentSessionId, Entity = entity };
        tab.SetReady(agent, logger);
        await main.OpenTabAsync(tab);
        return (tab, chat);
    }

    private sealed class GatedDecisionProvider : IAgentSessionCloseDecisionProvider
    {
        internal int Calls { get; private set; }
        internal TaskCompletionSource<AgentSessionCloseDecision> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AgentSessionCloseDecision> ChooseAsync(string sessionName, CancellationToken cancellationToken)
        {
            this.Calls++;
            return this.Completion.Task;
        }
    }

    private sealed class GatedLifecycleTable : IRunningAgentChatTable
        {
            public ObservableCollection<RunningAgentChatWithEntityInfo> RunningSessions { get; } = [];
            internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal string? Operation { get; private set; }
            public Task<RunningAgentChatLease> AcquireAsync(AcquireAgentChatRequest request, CancellationToken ct = default)
                => throw new NotSupportedException();
            public async Task<bool> TerminateAsync(AgentSessionId sessionId, CancellationToken ct = default)
            {
                this.Operation = "terminate";
                this.Entered.SetResult();
                await this.Release.Task;
                return true;
            }
            public async Task RetainForViewerCloseAsync(AgentSessionId sessionId, CancellationToken ct = default)
            {
                this.Operation = "retain";
                this.Entered.SetResult();
                await this.Release.Task;
                this.RunningSessions.Single().SetContinueInBackground(true);
        }
    }
}
