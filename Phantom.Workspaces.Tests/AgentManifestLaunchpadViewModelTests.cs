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

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Tests for issue #1180: the manifest-open path in <see cref="AgentManifestLaunchpadViewModel"/>
/// must construct the <see cref="AgentChat"/> through <see cref="IRunningAgentChatTable"/> (which
/// routes to <c>AgentChatFactory.GetOrCreateAsync</c> → <c>WithSelfAsFactory</c>) so
/// <see cref="AgentServices.RunningAgentChatFactory"/> is populated on the request that reaches
/// <see cref="AgentChat"/>. The old code path called <c>AgentFactory.CreateAgentChatAsync</c>
/// directly, which bypassed the factory, tripped the #1109 guard when a Copilot SDK client was
/// resolved, and surfaced as "Failed to load agent session from manifest".
/// </summary>
public sealed class AgentManifestLaunchpadViewModelTests
{
    private const string ManifestEntityJson =
        """
        {
          "entity-id": "b1180001-0000-4000-8000-000000000001",
          "entity-types": ["entity", "agent-manifest"],
          "names": [["tests", "agent-manifests", "issue-1180"]],
          "display-name": { "default": "Issue 1180 Manifest" },
          "manifest": {
            "name": "issue-1180-manifest",
            "displayName": "Issue 1180 Manifest",
            "template": {
              "kind": "prompt",
              "name": "issue-1180-manifest",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
            }
          }
        }
        """;

    private const string DefinitionEntityJson =
        """
        {
          "entity-id": "b1180002-0000-4000-8000-000000000002",
          "entity-types": ["entity", "agent-definition"],
          "names": [["tests", "agent-definitions", "issue-1180"]],
          "display-name": { "default": "Issue 1180 Definition" },
          "definition": {
            "kind": "prompt",
            "name": "issue-1180-definition",
            "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
            "tools": []
          }
        }
        """;

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_WhenManifestEntity_CreatesAgentChatThroughFactory_DoesNotThrow()
    {
        var (viewModel, launchpad, spy) = await OpenLaunchpadForAsync(
            new EntityId("b1180001-0000-4000-8000-000000000001"),
            ManifestEntityJson);

        await using (viewModel)
        {
            launchpad.StartSessionCommand.Execute(null);

            var sessionTab = await MainWindowIntegrationTests.WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(
                viewModel.SelectedWorkspacePane);
            await MainWindowIntegrationTests.WaitForAgentReadyAsync(sessionTab);

            // The #1109 guard would have set the tab to Failed with the "must be supplied at
            // construction time" message. Reaching Ready proves the guard did not fire and
            // therefore that RunningAgentChatFactory was injected before AgentChat.CreateAsync.
            Assert.Equal(AgentTabState.Ready, sessionTab.State);
            Assert.NotNull(sessionTab.Lease);
            var services = GetRequestServices(sessionTab.Lease!.AgentChat);
            Assert.NotNull(services.RunningAgentChatFactory);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_WhenDefinitionEntity_CreatesAgentChatThroughFactory_DoesNotThrow()
    {
        var (viewModel, launchpad, spy) = await OpenLaunchpadForAsync(
            new EntityId("b1180002-0000-4000-8000-000000000002"),
            DefinitionEntityJson);

        await using (viewModel)
        {
            // The definition-branch launchpad auto-starts (no parameters) — no explicit Execute.
            var sessionTab = await MainWindowIntegrationTests.WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(
                viewModel.SelectedWorkspacePane);
            await MainWindowIntegrationTests.WaitForAgentReadyAsync(sessionTab);

            Assert.Equal(AgentTabState.Ready, sessionTab.State);
            Assert.NotNull(sessionTab.Lease);
            var services = GetRequestServices(sessionTab.Lease!.AgentChat);
            Assert.NotNull(services.RunningAgentChatFactory);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_WhenManifestEntity_UsesRunningAgentChatTable_NotAgentFactoryStatic()
    {
        var (viewModel, launchpad, spy) = await OpenLaunchpadForAsync(
            new EntityId("b1180001-0000-4000-8000-000000000001"),
            ManifestEntityJson);

        await using (viewModel)
        {
            launchpad.StartSessionCommand.Execute(null);

            var sessionTab = await MainWindowIntegrationTests.WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(
                viewModel.SelectedWorkspacePane);
            await MainWindowIntegrationTests.WaitForAgentReadyAsync(sessionTab);

            // Regression pin: any future refactor that silently reverts to the direct
            // AgentFactory.CreateAgentChatAsync path would fail this spy assertion.
            Assert.True(spy.AcquireCallCount >= 1, "IRunningAgentChatTable.AcquireAsync was not invoked.");
        }
    }

    private static async Task<(MainWindowViewModel ViewModel, AgentManifestLaunchpadViewModel Launchpad, SpyRunningAgentChatTable Spy)> OpenLaunchpadForAsync(
        EntityId entityId,
        string entityJson)
    {
        var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var broker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var entity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(broker, entityId, entityJson);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var inner = MainWindowIntegrationTests.CreateTestRunningAgentChatTable();
        var spy = new SpyRunningAgentChatTable(inner);
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext,
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            spy);

        var launchpad = new AgentManifestLaunchpadViewModel(
            entity,
            agentSessionShortcutContext,
            openAgentSessionShortcutHandler,
            viewModel)
        {
            Id = $"launchpad-{entity.EntityId}",
            Title = entity.DisplayName,
            DockRegion = "full",
            Entity = entity,
        };

        await viewModel.OpenTabAsync(launchpad);
        return (viewModel, launchpad, spy);
    }

    private static AgentServices GetRequestServices(AgentChat chat)
    {
        var requestField = typeof(AgentChat).GetField(
            "request",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(requestField);
        var request = requestField!.GetValue(chat);
        Assert.NotNull(request);
        var servicesProperty = request!.GetType().GetProperty("AgentServices");
        Assert.NotNull(servicesProperty);
        var services = (AgentServices?)servicesProperty!.GetValue(request);
        Assert.NotNull(services);
        return services!;
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
