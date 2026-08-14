using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services;
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
            "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
            "tools": []
          }
        }
        """;

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CreateDefinitionSession_RegistersRootChatInRunningAgentChatTable()
    {
        var (viewModel, vm, spy, inner) = await OpenProfileTabAsync();

        await using (viewModel)
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
            Assert.NotNull(sessionTab.Lease);

            // The chat is registered under its session id so GetAsync returns the same live
            // in-memory instance (not a duplicate hydrated from persistence).
            var liveChat = sessionTab.Lease!.AgentChat;
            var sessionId = new AgentSessionId(liveChat.AgentSessionId);
            var lookupLease = await ((IRunningAgentChatFactory)GetFactory(inner)).GetAsync(
                sessionId,
                registerAsRunningAgent: false,
                CancellationToken.None);
            await using (lookupLease)
            {
                Assert.Same(liveChat, lookupLease.AgentChat);
            }
        }
    }

    private static async Task<(MainWindowViewModel ViewModel, StartAgentSessionOnProfileViewModel Vm, SpyRunningAgentChatTable Spy, RunningAgentChatTable Inner)> OpenProfileTabAsync()
    {
        var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
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

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var inner = MainWindowIntegrationTests.CreateTestRunningAgentChatTable();
        var spy = new SpyRunningAgentChatTable(inner);
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext,
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            spy);

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
        return (viewModel, vm, spy, inner);
    }

    private static async Task<StartAgentSessionOnProfileViewModel.AgentSourceItem> WaitForAgentSourceAsync(
        StartAgentSessionOnProfileViewModel vm,
        EntityId entityId)
    {
        var start = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - start < TimeSpan.FromSeconds(10))
        {
            foreach (var item in vm.AgentSources)
            {
                if (item.Entity.EntityId == entityId)
                {
                    return item;
                }
            }
            await Task.Yield();
        }
        throw new TimeoutException($"Agent source for {entityId} did not appear.");
    }

    private static IRunningAgentChatFactory GetFactory(RunningAgentChatTable table)
    {
        var field = typeof(RunningAgentChatTable).GetField(
            "_factory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<IRunningAgentChatFactory>(field!.GetValue(table));
    }

    private sealed class SpyRunningAgentChatTable : IRunningAgentChatTable
    {
        private readonly IRunningAgentChatTable inner;
        private int acquireCallCount;

        public SpyRunningAgentChatTable(IRunningAgentChatTable inner)
        {
            this.inner = inner;
        }

        public int AcquireCallCount => Volatile.Read(ref this.acquireCallCount);

        public ObservableCollection<RunningAgentChatWithEntityInfo> RunningSessions => this.inner.RunningSessions;

        public Task<RunningAgentChatLease> AcquireAsync(AcquireAgentChatRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref this.acquireCallCount);
            return this.inner.AcquireAsync(request, ct);
        }
    }
}
