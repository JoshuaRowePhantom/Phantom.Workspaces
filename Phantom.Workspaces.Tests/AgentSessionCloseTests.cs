using Avalonia.Headless.XUnit;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Phantom.Workspaces.ViewModels;
using MainWindowViewModel = Phantom.Workspaces.ViewModels.MainWindowViewModel;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class AgentSessionCloseTests
{
    [AvaloniaTheory(Timeout = 30_000)]
    [InlineData("sibling")]
    [InlineData("gone")]
    [InlineData("replacement")]
    [InlineData("during-dialog")]
    [InlineData("cancel")]
    public async Task CloseTab_ExactTerminatedLocalRuntime_ClosesOnlyCapturedViewer(string scenario)
    {
        await using var factory = new AgentChatFactory(new InMemoryAgentPersistenceStore(), new AgentServices(),
            TaskScheduler.FromCurrentSynchronizationContext());
        var table = new RunningAgentChatTable(factory);
        var services = new ApplicationServices(table, new AgentPersistenceStoreCache(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            Moq.Mock.Of<Phantom.Workspaces.Llm.Interfaces.Access.IAccessBroker>());
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel(applicationServices: services);
        await main.InitializeAsync();
        var firstWorkspaceId = new Phantom.Workspaces.Data.EntityId(Guid.NewGuid());
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(MainWindowIntegrationTests.GetEntityBroker(main), firstWorkspaceId,
            $$$"""{"entity-id":"{{{firstWorkspaceId}}}","entity-types":["entity","workspace"],"names":[["tests","first-terminal-pane"]]}""");
        await main.OpenWorkspaceAsync(new Phantom.Workspaces.Data.GetEntityRequest { EntityId = firstWorkspaceId });
        var id = new AgentSessionId("terminal-shared");
        var request = new AcquireAgentChatRequest
        {
            AgentSessionId = id,
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson("""
                {"kind":"prompt","name":"terminal-shared","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}
                """),
        };
        var firstLease = await table.AcquireAsync(request);
        var survivorLease = await table.AcquireAsync(request);
        Assert.Same(firstLease.AgentChat, survivorLease.AgentChat);
        var first = await OpenLeaseTabAsync(main, firstLease);
        var workspaceId = new Phantom.Workspaces.Data.EntityId(Guid.NewGuid());
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(MainWindowIntegrationTests.GetEntityBroker(main), workspaceId,
            $$$"""{"entity-id":"{{{workspaceId}}}","entity-types":["entity","workspace"],"names":[["tests","terminal-pane"]]}""");
        await main.OpenWorkspaceAsync(new Phantom.Workspaces.Data.GetEntityRequest { EntityId = workspaceId });
        var survivor = await OpenLeaseTabAsync(main, survivorLease);
        Assert.NotEqual(first.WorkspacePaneId, survivor.WorkspacePaneId);
        var resource = new Moq.Mock<IAsyncDisposable>();
        resource.Setup(value => value.DisposeAsync()).Returns(ValueTask.CompletedTask);
        firstLease.LocalAgentChat.RegisterOwnedResource(resource.Object);
        firstLease.LocalAgentChat.CreateRunningItem(new AgentChatHistoryItem { Role = ChatRole.Assistant });
        var decisions = new GatedDecisionProvider();
        main.AgentSessionCloseDecisionProvider = decisions;
        Task<bool>? closing = null;
        if (scenario is "during-dialog" or "cancel")
        {
            closing = main.RequestCloseTabAsync(survivor);
            await decisions.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        if (scenario == "sibling")
        {
            decisions.Completion.SetResult(AgentSessionCloseDecision.Terminate);
            Assert.True(await main.RequestCloseTabAsync(first));
        }
        else
            Assert.True(await table.TerminateExactAsync(id, firstLease.AgentChat));
        resource.Verify(value => value.DisposeAsync(), Moq.Times.Once);
        RunningAgentChatLease? replacement = null;
        if (scenario is "replacement" or "during-dialog")
        {
            replacement = await table.AcquireAsync(request);
            Assert.NotSame(survivorLease.AgentChat, replacement.AgentChat);
            replacement.LocalAgentChat.CreateRunningItem(new AgentChatHistoryItem { Role = ChatRole.Assistant });
        }
        if (scenario != "sibling")
            decisions.Completion.SetResult(scenario == "cancel" ? AgentSessionCloseDecision.Cancel : AgentSessionCloseDecision.Terminate);
        Assert.Equal(scenario != "cancel", await (closing ?? main.RequestCloseTabAsync(survivor)));
        Assert.Equal(scenario == "cancel", main.WorkspacePanes.SelectMany(pane => pane.Tabs).Contains(survivor));
        if (scenario is "gone" or "replacement")
            Assert.Equal(0, decisions.Calls);
        await survivorLease.DisposeAsync();
        if (replacement is not null)
        {
            var row = Assert.Single(table.RunningSessions);
            Assert.Equal(1, row.ViewerCount);
            Assert.Same(replacement.AgentChat, Assert.Single(factory.RunningSessions).AgentChat);
            Assert.Single(replacement.AgentChat.RunningItems);
            await replacement.DisposeAsync();
        }
    }

    [AvaloniaTheory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseTab_LocalCleanupPendingOrFailed_IsNotProofOfTerminalRuntime(bool fail)
    {
        await using var factory = new AgentChatFactory(new InMemoryAgentPersistenceStore(), new AgentServices(),
            TaskScheduler.FromCurrentSynchronizationContext());
        var table = new RunningAgentChatTable(factory);
        var services = new ApplicationServices(table, new AgentPersistenceStoreCache(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            Moq.Mock.Of<Phantom.Workspaces.Llm.Interfaces.Access.IAccessBroker>());
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel(applicationServices: services);
        await main.InitializeAsync();
        var lease = await table.AcquireAsync(new AcquireAgentChatRequest
        {
            AgentSessionId = new AgentSessionId("pending-cleanup"),
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson("""
                {"kind":"prompt","name":"pending-cleanup","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}
                """),
        });
        var tab = await OpenLeaseTabAsync(main, lease);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new Moq.Mock<IAsyncDisposable>();
        resource.Setup(value => value.DisposeAsync()).Returns(() =>
        {
            entered.TrySetResult();
            return new ValueTask(release.Task);
        });
        lease.LocalAgentChat.RegisterOwnedResource(resource.Object);
        var terminating = table.TerminateExactAsync(lease.SessionId, lease.AgentChat);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var decisions = new GatedDecisionProvider();
        decisions.Completion.SetResult(AgentSessionCloseDecision.Terminate);
        main.AgentSessionCloseDecisionProvider = decisions;
        var closing = main.RequestCloseTabAsync(tab);
        Assert.Contains(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));
        if (fail)
        {
            release.SetException(new IOException("Controlled cleanup failure"));
            await Assert.ThrowsAsync<AggregateException>(() => terminating);
            Assert.False(await closing);
            Assert.False(await main.RequestCloseTabAsync(tab));
            Assert.Contains(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));
        }
        else
        {
            release.SetResult();
            Assert.True(await terminating);
            Assert.True(await closing);
            Assert.DoesNotContain(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));
        }
        resource.Verify(value => value.DisposeAsync(), Moq.Times.Once);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task CloseTab_DescendantCleanupFailure_IsNotProofOfTerminalRuntime()
    {
        var store = new InMemoryAgentPersistenceStore();
        await using var factory = new AgentChatFactory(store, new AgentServices(),
            TaskScheduler.FromCurrentSynchronizationContext());
        var table = new RunningAgentChatTable(factory);
        var services = new ApplicationServices(table, new AgentPersistenceStoreCache(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            Moq.Mock.Of<Phantom.Workspaces.Llm.Interfaces.Access.IAccessBroker>());
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel(applicationServices: services);
        await main.InitializeAsync();
        const string parentId = "parent-descendant-failure";
        const string childId = "child-descendant-failure";
        var definition = AgentDefinitionLoader.LoadAgentFromJson("""
            {"kind":"prompt","name":"close-test","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}
            """);
        var seededChild = await table.AcquireAsync(new AcquireAgentChatRequest
        {
            AgentSessionId = new AgentSessionId(childId),
            AgentDefinition = definition,
        });
        await seededChild.DisposeAsync();
        await store.AddSubAgentLinkAsync(parentId, childId);
        var lease = await table.AcquireAsync(new AcquireAgentChatRequest
        {
            AgentSessionId = new AgentSessionId(parentId),
            AgentDefinition = definition,
        });
        var tab = await OpenLeaseTabAsync(main, lease);
        var restoredChild = lease.LocalAgentChat.TryGetRegisteredSubAgent(childId);
        Assert.NotNull(restoredChild);
        var childLease = await restoredChild!.AcquireLeaseAsync();
        var childResource = new Moq.Mock<IAsyncDisposable>();
        childResource.Setup(value => value.DisposeAsync())
            .Returns(ValueTask.FromException(new IOException("Controlled descendant cleanup failure")));
        childLease.LocalAgentChat.RegisterOwnedResource(childResource.Object);
        lease.LocalAgentChat.CreateRunningItem(new AgentChatHistoryItem { Role = ChatRole.Assistant });

        var firstDecision = new GatedDecisionProvider();
        firstDecision.Completion.SetResult(AgentSessionCloseDecision.Terminate);
        main.AgentSessionCloseDecisionProvider = firstDecision;
        Assert.False(await main.RequestCloseTabAsync(tab));
        Assert.Equal(1, firstDecision.Calls);
        Assert.False(lease.IsRuntimeTerminal);
        Assert.Contains(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));

        var secondDecision = new GatedDecisionProvider();
        secondDecision.Completion.SetResult(AgentSessionCloseDecision.Terminate);
        main.AgentSessionCloseDecisionProvider = secondDecision;
        Assert.False(await main.RequestCloseTabAsync(tab));
        Assert.Equal(1, secondDecision.Calls);
        Assert.False(lease.IsRuntimeTerminal);
        Assert.Contains(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));

        childResource.Verify(value => value.DisposeAsync(), Moq.Times.Once);
    }

    [AvaloniaTheory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseTab_FalseTerminationResult_RequiresPositiveCapturedTerminalEvidence(bool terminal)
    {
        var table = new Moq.Mock<IRunningAgentChatTable>();
        table.SetupGet(value => value.RunningSessions).Returns(new ObservableCollection<RunningAgentChatWithEntityInfo>());
        var services = new ApplicationServices(table.Object, new AgentPersistenceStoreCache(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            Moq.Mock.Of<Phantom.Workspaces.Llm.Interfaces.Access.IAccessBroker>());
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel(applicationServices: services);
        await main.InitializeAsync();
        var (tab, chat) = await OpenAsync(main);
        var lease = new RunningAgentChatLease(new AgentSessionId(chat.AgentSessionId), chat, () => ValueTask.CompletedTask);
        tab.SetLease(lease);
        chat.CreateRunningItem(new AgentChatHistoryItem { Role = ChatRole.Assistant });
        table.Setup(value => value.TerminateExactAsync(lease.SessionId, chat, Moq.It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (terminal) await chat.TerminateAsync();
                return false;
            });
        var decisions = new GatedDecisionProvider();
        decisions.Completion.SetResult(AgentSessionCloseDecision.Terminate);
        main.AgentSessionCloseDecisionProvider = decisions;
        Assert.False(lease.IsRuntimeTerminal);
        Assert.Equal(terminal, await main.RequestCloseTabAsync(tab));
        Assert.Equal(terminal, lease.IsRuntimeTerminal);
        Assert.Equal(!terminal, main.WorkspacePanes.SelectMany(pane => pane.Tabs).Contains(tab));
        table.Verify(value => value.TerminateExactAsync(lease.SessionId, chat, Moq.It.IsAny<CancellationToken>()), Moq.Times.Once);
        await chat.DisposeAsync();
    }

    private static async Task<AgentSessionWorkspaceTabViewModel> OpenLeaseTabAsync(
        MainWindowViewModel main, RunningAgentChatLease lease)
    {
        var logger = new ObservableLoggerFactory();
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = Guid.NewGuid().ToString(), Title = "Shared runtime", AgentSessionId = lease.SessionId.Value,
        };
        tab.SetLease(lease);
        tab.SetReady(new AgentViewModel(lease.AgentChat, "shared", "", logger,
            TaskScheduler.FromCurrentSynchronizationContext()), logger);
        await main.OpenTabAsync(tab);
        return tab;
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task CloseTab_RetentionStoreWriteFails_RollsBackPinKeepsViewerAndSurfacesError()
    {
        await using var factory = new AgentChatFactory(new InMemoryAgentPersistenceStore(), new AgentServices(),
            TaskScheduler.FromCurrentSynchronizationContext());
        var contexts = new Moq.Mock<IAgentSessionRuntimeContextFactory>();
        contexts.Setup(value => value.Create(Moq.It.IsAny<System.Text.Json.JsonElement>(),
                Moq.It.IsAny<Phantom.Workspaces.Data.EntityId?>()))
            .Returns(new AgentSessionRuntimeContext
            {
                Intent = new PersistedAgentSessionRuntimeIntent
                {
                    AgentSessionId = "retention-failure", OwningProfileEntityId = Guid.NewGuid().ToString(),
                    OwnershipGeneration = 0,
                    ExecutorBindings = new Phantom.Workspaces.Llm.Core.Manifest.ExecutorBindings
                    {
                        SessionExecutor = System.Text.Json.JsonSerializer.SerializeToElement(new { type = "local" }),
                    },
                },
            });
        var table = new RunningAgentChatTable(factory, contexts.Object);
        var persistenceEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failPersistence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new Moq.Mock<Phantom.Workspaces.Services.AgentSessions.ILocalAgentSessionRuntimeRegistry>();
        registry.Setup(value => value.SetContinueInBackgroundAsync(Moq.It.IsAny<AgentSessionId>(),
                Moq.It.IsAny<System.Text.Json.JsonElement>(), true, Moq.It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                persistenceEntered.TrySetResult();
                await failPersistence.Task;
                throw new IOException("Controlled retention write failure");
            });
        var services = new ApplicationServices(table, new AgentPersistenceStoreCache(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            Moq.Mock.Of<Phantom.Workspaces.Llm.Interfaces.Access.IAccessBroker>());
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel(applicationServices: services);
        await main.InitializeAsync();
        table.ConfigureLocalRuntimeRegistry(registry.Object);
        var entityId = new Phantom.Workspaces.Data.EntityId(Guid.NewGuid());
        var entity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            MainWindowIntegrationTests.GetEntityBroker(main), entityId,
            $$$"""{"entity-id":"{{{entityId}}}","entity-types":["entity","agent-session"],"names":[["tests","retention-failure"]],"agent-session-id":"retention-failure"}""");
        var lease = await table.AcquireAsync(new AcquireAgentChatRequest
        {
            AgentSessionId = new AgentSessionId("retention-failure"),
            AgentSessionEntity = entity.Data,
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson("""
                {"kind":"prompt","name":"retention-failure","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}
                """),
        });
        using var logger = new ObservableLoggerFactory();
        var agent = new AgentViewModel(lease.AgentChat, "failure", "", logger, TaskScheduler.FromCurrentSynchronizationContext());
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = entityId.ToString(), Title = "Retention failure", Entity = entity, AgentSessionId = lease.SessionId.Value,
        };
        tab.SetLease(lease);
        tab.SetReady(agent, logger);
        await main.OpenTabAsync(tab);
        lease.LocalAgentChat.CreateRunningItem(new AgentChatHistoryItem { Role = ChatRole.Assistant });
        var errorVisible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((INotifyCollectionChanged)lease.AgentChat.History).CollectionChanged += (_, _) =>
        {
            if (lease.AgentChat.History.Any(item => item.Contents.OfType<TextContent>()
                .Any(content => content.Text?.Contains("Unable to close session", StringComparison.Ordinal) == true)))
                errorVisible.TrySetResult();
        };
        var decisions = new GatedDecisionProvider();
        decisions.Completion.SetResult(AgentSessionCloseDecision.RunInBackground);
        main.AgentSessionCloseDecisionProvider = decisions;
        var closing = main.RequestCloseTabAsync(tab);
        await persistenceEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(closing.IsCompleted);
        Assert.Contains(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));
        failPersistence.SetResult();
        Assert.False(await closing);
        await errorVisible.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Same(lease, tab.Lease);
        Assert.False(Assert.Single(table.RunningSessions).ContinueInBackground);
        Assert.Single(factory.RunningSessions);
        await tab.DisposeAsync();
        Assert.Empty(factory.RunningSessions);
        registry.Verify(value => value.SetContinueInBackgroundAsync(Moq.It.IsAny<AgentSessionId>(),
            Moq.It.IsAny<System.Text.Json.JsonElement>(), true, Moq.It.IsAny<CancellationToken>()), Moq.Times.Once);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseTab_ModalOnlyRootOrWaitingDescendant_RequiresConsent(bool descendant)
    {
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await main.InitializeAsync();
        var (tab, chat) = await OpenAsync(main);
        var owner = chat;
        if (descendant)
        {
            await chat.GetOrCreateAsync("unmaterialized", chat.AgentDefinition!, "child-call");
            owner = Assert.IsType<AgentChat>(Assert.Single(chat.SubAgents));
        }
        var modalReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((INotifyCollectionChanged)owner.Modals).CollectionChanged += (_, _) => modalReady.TrySetResult();
        owner.PublishModal(new AgentChatModal
        {
            Id = "waiting-only", OwnerAgentId = "owner", Title = "Input",
            Body = "Required", Content = new FreeformModalContent { IsRequired = true },
        });
        await modalReady.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Empty(chat.RunningItems);
        Assert.False(tab.Agent!.IsChatRunning);
        var decisions = new GatedDecisionProvider();
        main.AgentSessionCloseDecisionProvider = decisions;
        var close = main.RequestCloseTabAsync(tab);
        Assert.Equal(1, decisions.Calls);
        decisions.Completion.SetResult(AgentSessionCloseDecision.Cancel);
        Assert.False(await close);
        Assert.Contains(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));
        Assert.Single(owner.Modals);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task CloseTab_UnmaterializedRestoredDescendant_RequiresConsentBeforeChildEditorLoads()
    {
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await main.InitializeAsync();
        var store = new InMemoryAgentPersistenceStore();
        await store.AddSubAgentLinkAsync("restored-parent", "unloaded-child");
        var childAcquisition = new TaskCompletionSource<RunningAgentChatLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        var childFactory = new Moq.Mock<Phantom.Workspaces.Llm.IRunningAgentChatFactory>();
        childFactory.Setup(value => value.GetAsync(new AgentSessionId("unloaded-child"), false,
                Moq.It.IsAny<CancellationToken>()))
            .Returns(childAcquisition.Task);
        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentSessionId = "restored-parent", ConfiguredStore = store,
            AgentServices = new AgentServices { RunningAgentChatFactory = childFactory.Object },
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson("""
                {"kind":"prompt","name":"restored-parent","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}
                """),
            ForegroundScheduler = TaskScheduler.FromCurrentSynchronizationContext(),
        });
        await chat.Initialization;
        var descendant = Assert.IsType<SubAgent>(Assert.Single(chat.SubAgents));
        descendant.SetRestoredCompletionState(AgentChatCompletionState.Running);
        using var logger = new ObservableLoggerFactory();
        var agent = new AgentViewModel(chat, "parent", "", logger, TaskScheduler.FromCurrentSynchronizationContext());
        var tab = new AgentSessionWorkspaceTabViewModel { Id = Guid.NewGuid().ToString(), Title = "Parent" };
        tab.SetReady(agent, logger);
        await main.OpenTabAsync(tab);
        try
        {
            Assert.Null(descendant.AgentChat);
            Assert.Empty(agent.SubAgentsContainer.Slots);
            Assert.Empty(chat.RunningItems);
            Assert.Empty(chat.Modals);
            Assert.False(childAcquisition.Task.IsCompleted);
            var decisions = new GatedDecisionProvider();
            main.AgentSessionCloseDecisionProvider = decisions;
            var closing = main.RequestCloseTabAsync(tab);
            await decisions.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, decisions.Calls);
            decisions.Completion.SetResult(AgentSessionCloseDecision.Cancel);
            Assert.False(await closing);
            Assert.Contains(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));
            Assert.Null(descendant.AgentChat);
            Assert.Empty(agent.SubAgentsContainer.Slots);
            Assert.False(childAcquisition.Task.IsCompleted);
        }
        finally
        {
            childAcquisition.TrySetCanceled(TestContext.Current.CancellationToken);
            await agent.RestoreSettled;
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseTab_IdlePreservesEitherRetentionPreference(bool retained)
    {
        var table = new GatedLifecycleTable();
        var services = new ApplicationServices(table, new AgentPersistenceStoreCache(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            Moq.Mock.Of<Phantom.Workspaces.Llm.Interfaces.Access.IAccessBroker>());
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel(applicationServices: services);
        await main.InitializeAsync();
        var (tab, chat) = await OpenAsync(main);
        var row = new RunningAgentChatWithEntityInfo(new AgentSessionId(chat.AgentSessionId), false,
            _ => throw new NotSupportedException(), "idle", null) { ContinueInBackground = retained };
        table.RunningSessions.Add(row);
        var decisions = new GatedDecisionProvider();
        main.AgentSessionCloseDecisionProvider = decisions;
        Assert.True(await main.RequestCloseTabAsync(tab));
        Assert.Equal(0, decisions.Calls);
        Assert.Null(table.Operation);
        Assert.Equal(retained, row.ContinueInBackground);
    }

    [AvaloniaFact]
    public async Task CloseTab_ReadyDisconnected_FailsSafeWithoutReleasingViewer()
    {
        var table = new GatedLifecycleTable();
        var services = new ApplicationServices(table, new AgentPersistenceStoreCache(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            Moq.Mock.Of<Phantom.Workspaces.Llm.Interfaces.Access.IAccessBroker>());
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel(applicationServices: services);
        await main.InitializeAsync();
        var (tab, chat) = await OpenAsync(main);
        table.RunningSessions.Add(new RunningAgentChatWithEntityInfo(new AgentSessionId(chat.AgentSessionId), false,
            _ => throw new NotSupportedException(), "disconnected", null) { IsConnected = false });
        var released = 0;
        tab.SetLease(new RunningAgentChatLease(new AgentSessionId(chat.AgentSessionId), chat,
            () => { released++; return ValueTask.CompletedTask; }));
        Assert.Equal(AgentTabState.Ready, tab.State);
        Assert.False(await main.RequestCloseTabAsync(tab));
        Assert.Equal(0, released);
        Assert.Null(table.Operation);
        Assert.Contains(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));
        await chat.DisposeAsync();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TabClose_CancelOrBackground_ReportsAwaitedOutcome(bool background)
    {
        var table = new GatedLifecycleTable();
        var services = new ApplicationServices(table, new AgentPersistenceStoreCache(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            Moq.Mock.Of<Phantom.Workspaces.Llm.Interfaces.Access.IAccessBroker>());
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel(applicationServices: services);
        await main.InitializeAsync();
        var entityId = new Phantom.Workspaces.Data.EntityId(Guid.NewGuid());
        var entity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            MainWindowIntegrationTests.GetEntityBroker(main), entityId,
            $$$"""{"entity-id":"{{{entityId}}}","entity-types":["entity","agent-session"],"names":[["tests","tool-close","{{{entityId}}}"]],"agent-session-id":"tool-close"}""");
        var (tab, chat) = await OpenAsync(main, entity);
        table.RunningSessions.Add(new RunningAgentChatWithEntityInfo(new AgentSessionId(chat.AgentSessionId), false,
            _ => throw new NotSupportedException(), "tool-close", entityId.ToString()));
        chat.CreateRunningItem(new AgentChatHistoryItem { Role = ChatRole.Assistant });
        var decisions = new GatedDecisionProvider();
        main.AgentSessionCloseDecisionProvider = decisions;
        var tool = await WorkspaceGuiContextProviderTests.GetToolAsync(main, "tab_close");
        var closing = tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["tab_id"] = System.Text.Json.JsonSerializer.SerializeToElement(tab.Id),
        }), TestContext.Current.CancellationToken).AsTask();
        await decisions.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, decisions.Calls);
        decisions.Completion.SetResult(background ? AgentSessionCloseDecision.RunInBackground : AgentSessionCloseDecision.Cancel);
        if (background)
        {
            await table.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(closing.IsCompleted);
            Assert.Contains(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));
            Assert.False(Assert.Single(table.RunningSessions).ContinueInBackground);
            table.Release.SetResult();
        }
        var result = Assert.IsType<System.Text.Json.JsonElement>(await closing);
        Assert.Equal(background, result.GetProperty("closed").GetBoolean());
        Assert.Equal(background, Assert.Single(table.RunningSessions).ContinueInBackground);
        Assert.Equal(!background, main.WorkspacePanes.SelectMany(pane => pane.Tabs).Contains(tab));
    }

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
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<AgentSessionCloseDecision> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AgentSessionCloseDecision> ChooseAsync(string sessionName, CancellationToken cancellationToken)
        {
            this.Calls++;
            this.Entered.TrySetResult();
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
