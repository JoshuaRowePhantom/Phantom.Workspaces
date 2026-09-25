using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Logging;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.ViewModels;
using IRunningAgentChatFactory = Phantom.Workspaces.Llm.IRunningAgentChatFactory;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Tests for issue #1309: the "Start Agent Session on Profile" definition path
/// (<see cref="StartAgentSessionOnProfileViewModel.CreateDefinitionSessionAsync"/>) must
/// construct root agent chats through <see cref="IRunningAgentChatTable"/> so
/// <see cref="AgentChatFactory"/> registers the chat in <c>_entries[sessionId]</c> and
/// self-injects as <see cref="AgentServices.RunningAgentChatFactory"/>. The old code called
/// <see cref="AgentFactory.CreateAgentChatAsync"/> directly, leaving the root chat
/// unregistered so a later <see cref="IRunningAgentChatFactory.GetAsync"/> would load a
/// duplicate from persistence instead of returning the live in-memory instance. This is a
/// prerequisite for #1306.
/// </summary>
public sealed class StartAgentSessionOnProfileViewModelTests
{
    [AvaloniaFact(Timeout = 30_000)]
    public async Task StartAgentSessionOnProfileViewModel_ProductionSession_ReceivesExplicitLogger()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "profile-session-logging-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            using var process = HostFileLoggerFactory.Create(directory);
            var (viewModel, vm, spy, _, _, handler) = await OpenProfileTabAsync(
                remoteOwner: true, processLoggerFactory: process);
            await using (viewModel)
            await using (handler)
            {
                var definition = await WaitForAgentSourceAsync(
                    vm, new EntityId("b1309002-0000-4000-8000-000000000002"));
                vm.SelectedAgentSource = definition;
                vm.CreateSessionCommand.Execute(null);

                var tab = await MainWindowIntegrationTests.WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(
                    viewModel.SelectedWorkspacePane);
                await MainWindowIntegrationTests.WaitForAgentReadyAsync(tab);

                var services = Assert.IsType<AgentServices>(spy.LastRequest?.AgentServices);
                var tee = Assert.IsType<SessionTeeLoggerFactory>(services.LoggerFactory);
                Assert.Same(tab.LoggerFactory, tee.SessionMemoryFactory);
                const string eventText = "Agent session profile launch; outcome acquired.";
                Assert.Single(tab.LoggerFactory!.Entries, entry =>
                    entry.Contains(eventText, StringComparison.Ordinal));
                var file = ProcessLogTestFile.ReadAll(directory);
                Assert.Equal(1, file.Split(eventText, StringSplitOptions.None).Length - 1);
                Assert.DoesNotContain(tab.AgentSessionId!, file, StringComparison.Ordinal);
                Assert.DoesNotContain("b1309001-0000-4000-8000-000000000001", file, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private const string ProfileEntityJson =
        """
        {
          "entity-id": "b1309001-0000-4000-8000-000000000001",
          "entity-types": ["entity", "git-worktree", "filesystem-path"],
          "names": [["tests", "worktrees", "issue-1309"]],
          "display-name": { "default": "Issue 1309 Profile" },
          "path": "/test/repo"
        }
        """;

    private const string DefinitionEntityJson =
        """
        {
          "entity-id": "b1309002-0000-4000-8000-000000000002",
          "entity-types": ["entity", "agent-definition"],
          "names": [["tests", "agent-definitions", "issue-1309"]],
          "display-name": { "default": "Issue 1309 Definition" },
          "definition": {
            "kind": "prompt",
            "name": "issue-1309-definition",
            "metadata": { "trust-profile": "issue-1490-direct-definition" },
            "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
            "tools": []
          }
        }
        """;

    private const string TrustProfileEntityJson =
        """
        {
          "entity-id": "b1309003-0000-4000-8000-000000000003",
          "entity-types": ["entity", "llm-trust-profile"],
          "names": [["tests", "trust-profiles", "issue-1490-direct-definition"]],
          "display-name": { "default": "Issue 1490 Direct Definition Trust" },
          "hosting-workspaces-client-instances": ["*"],
          "filesystem-paths": [],
          "network-capabilities": [],
          "https-proxy-policy": { "mode": "disabled" },
          "allowed-mcp-tool-call-schemas": [ {} ]
        }
        """;

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CreateDefinitionSession_RegistersRootChatInRunningAgentChatTable()
    {
        var (viewModel, vm, spy, inner, _, handler) = await OpenProfileTabAsync(remoteOwner: true);

        await using (viewModel)
        await using (handler)
        {
            // Wait for LoadAgentSourcesAsync to populate the definition source.
            var definition = await WaitForAgentSourceAsync(
                vm,
                new EntityId("b1309002-0000-4000-8000-000000000002"));
            vm.SelectedAgentSource = definition;

            vm.CreateSessionCommand.Execute(null);

            var sessionTab = await MainWindowIntegrationTests.WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(
                viewModel.SelectedWorkspacePane);
            await MainWindowIntegrationTests.WaitForAgentReadyAsync(sessionTab);

            // Regression pin: any future refactor that reverts to the direct
            // AgentFactory.CreateAgentChatAsync path would fail this spy assertion.
            Assert.True(spy.AcquireCallCount >= 1, "IRunningAgentChatTable.AcquireAsync was not invoked.");
            Assert.NotNull(spy.LastRequest?.AgentSessionEntity);
            Assert.NotNull(sessionTab.Lease);
            var persisted = spy.LastRequest!.AgentSessionEntity!.Value;
            Assert.Equal(
                "issue-1490-direct-definition",
                persisted.GetProperty("trust-profile-reference").GetString());
            Assert.False(string.IsNullOrWhiteSpace(
                persisted.GetProperty("expected-trust-profile-revision").GetString()));

            // The chat is registered under its session id, including when the selected profile
            // owns the new session and acquisition therefore produces a remote proxy.
            var liveChat = sessionTab.Lease!.AgentChat;
            var sessionId = new AgentSessionId(liveChat.Information.AgentSessionId);
            var runningSession = Assert.Single(inner.RunningSessions);
            Assert.Equal(sessionId, runningSession.SessionId);
            var lookupLease = await runningSession.AcquireLeaseAsync(
                TestContext.Current.CancellationToken);
            await using (lookupLease)
            {
                Assert.Same(liveChat, lookupLease.AgentChat);
            }
        }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task CreateDefinitionSession_RemoteProfile_AppliesOwnerDecisionAndAcquiresRemoteRuntime()
    {
        var (viewModel, vm, spy, _, transport, handler) = await OpenProfileTabAsync(remoteOwner: true);

        await using (viewModel)
        await using (handler)
        {
            var definition = await WaitForAgentSourceAsync(
                vm,
                new EntityId("b1309002-0000-4000-8000-000000000002"));
            vm.SelectedAgentSource = definition;

            vm.CreateSessionCommand.Execute(null);

            var sessionTab = await MainWindowIntegrationTests.WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(
                viewModel.SelectedWorkspacePane);
            await MainWindowIntegrationTests.WaitForAgentReadyAsync(sessionTab);

            Assert.True(sessionTab.State == AgentTabState.Ready, sessionTab.LoadError);
            Assert.IsType<Phantom.Workspaces.Llm.Remote.RemoteAgentChat>(sessionTab.Lease!.AgentChat);
            Assert.Equal("Issue 1309 Profile", sessionTab.RemoteProfileDisplayName);
            Assert.Equal(AgentChatAcquisitionMode.StartOrAttachRemote, spy.LastRequest!.AcquisitionMode);
            Assert.NotNull(spy.LastRequest.AgentSessionEntity);
            Assert.Equal(
                [Phantom.Workspaces.Llm.Remote.AgentSessionOpenIntent.Status,
                 Phantom.Workspaces.Llm.Remote.AgentSessionOpenIntent.StartOrAttach],
                transport!.OpenIntents);
        }
    }

    private static async Task<(
        MainWindowViewModel ViewModel,
        StartAgentSessionOnProfileViewModel Vm,
        SpyRunningAgentChatTable Spy,
        RunningAgentChatTable Inner,
        MainWindowIntegrationTests.OwnerPipelineTransport? Transport,
        OpenAgentSessionShortcutHandler Handler)> OpenProfileTabAsync(
        bool remoteOwner = false,
        ILoggerFactory? processLoggerFactory = null)
    {
        var applicationServices = processLoggerFactory is null ? null : new ApplicationServices(
            MainWindowIntegrationTests.CreateTestRunningAgentChatTable(),
            new AgentPersistenceStoreCache(), processLoggerFactory);
        var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel(
            applicationServices: applicationServices);
        await viewModel.InitializeAsync();

        var broker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var profileEntity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            broker,
            new EntityId("b1309001-0000-4000-8000-000000000001"),
            ProfileEntityJson);
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            broker,
            new EntityId("b1309002-0000-4000-8000-000000000002"),
            DefinitionEntityJson);
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            broker,
            new EntityId("b1309003-0000-4000-8000-000000000003"),
            TrustProfileEntityJson);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var store = new InMemoryAgentPersistenceStore();
        var factory = new AgentChatFactory(store, new AgentServices(), SynchronizationContextTaskScheduler.FromCurrent());
        var registryProvider = new TransportFactoryRegistryProvider(new TransportFactoryRegistry());
        var inner = new RunningAgentChatTable(factory, AgentSessionRuntimeContextFactory.FromProvider(registryProvider));
        var spy = new SpyRunningAgentChatTable(inner);
        MainWindowIntegrationTests.OwnerPipelineTransport? transport = null;
        OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler;
        if (remoteOwner)
        {
            transport = new MainWindowIntegrationTests.OwnerPipelineTransport();
            var transportRegistry = new TransportFactoryRegistry();
            transportRegistry.Register(new MainWindowIntegrationTests.SingleTransportFactory(transport));
            openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(
                agentSessionShortcutContext,
                MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
                spy,
                new MainWindowIntegrationTests.FixedOwnerDecisionProvider(
                    AgentSessionOwnerDecision.ConnectOnOwner),
                transportRegistry);
        }
        else
        {
            openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(
                agentSessionShortcutContext,
                MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
                spy);
        }

        var vm = new StartAgentSessionOnProfileViewModel(
            viewModel,
            agentSessionShortcutContext,
            openAgentSessionShortcutHandler,
            viewModel,
            profileEntity)
        {
            Id = $"start-agent-session-{profileEntity.EntityId}",
            Title = "Start Agent Session",
            DockRegion = "full",
            Entity = profileEntity,
        };

        await viewModel.OpenTabAsync(vm);
        return (viewModel, vm, spy, inner, transport, openAgentSessionShortcutHandler);
    }

    private static async Task<StartAgentSessionOnProfileViewModel.AgentSourceItem> WaitForAgentSourceAsync(
        StartAgentSessionOnProfileViewModel vm,
        EntityId entityId)
    {
        var ready = vm.AgentSources.FirstOrDefault(item => item.Entity.EntityId == entityId);
        if (ready is not null)
            return ready;

        var completion = new TaskCompletionSource<StartAgentSessionOnProfileViewModel.AgentSourceItem>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? _, NotifyCollectionChangedEventArgs __)
        {
            var matched = vm.AgentSources.FirstOrDefault(item => item.Entity.EntityId == entityId);
            if (matched is not null)
                completion.TrySetResult(matched);
        }

        vm.AgentSources.CollectionChanged += OnChanged;
        try
        {
            OnChanged(null, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            return await completion.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            vm.AgentSources.CollectionChanged -= OnChanged;
        }
    }

    private sealed class SpyRunningAgentChatTable : IRunningAgentChatTable
    {
        private readonly IRunningAgentChatTable inner;
        private int acquireCallCount;
        private AcquireAgentChatRequest? lastRequest;

        public SpyRunningAgentChatTable(IRunningAgentChatTable inner)
        {
            this.inner = inner;
        }

        public int AcquireCallCount => Volatile.Read(ref this.acquireCallCount);
        public AcquireAgentChatRequest? LastRequest => this.lastRequest;

        public ObservableCollection<RunningAgentChatWithEntityInfo> RunningSessions => this.inner.RunningSessions;

        public Task<RunningAgentChatLease> AcquireAsync(AcquireAgentChatRequest request, CancellationToken ct = default)
        {
            this.lastRequest = request;
            Interlocked.Increment(ref this.acquireCallCount);
            return this.inner.AcquireAsync(request, ct);
        }
    }
}
