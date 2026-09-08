using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Transport;
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
            Assert.NotNull(spy.LastRequest?.AgentSessionEntity);
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
        var inner = CreateTestRunningAgentChatTable();
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

    // ---- Issue #1440: executor launch-parameter picker ----

    private const string ExecutorManifestEntityId = "c1440001-0000-4000-8000-000000000001";
    private const string UserComputerProfileEntityId = "c1440010-0000-4000-8000-000000000010";
    private const string TrustProfileEntityId = "c1440020-0000-4000-8000-000000000020";

    private const string ExecutorManifestEntityJson =
        """
        {
          "entity-id": "c1440001-0000-4000-8000-000000000001",
          "entity-types": ["entity", "agent-manifest"],
          "names": [["tests", "agent-manifests", "issue-1440"]],
          "display-name": { "default": "Issue 1440 Manifest" },
          "manifest": {
            "name": "issue-1440-manifest",
            "displayName": "Issue 1440 Manifest",
            "parameters": {
              "properties": [
                { "name": "worker-executor", "kind": "executor", "required": true }
              ]
            },
            "template": {
              "kind": "prompt",
              "name": "issue-1440-manifest",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
            }
          }
        }
        """;

    private const string UserComputerProfileEntityJson =
        """
        {
          "entity-id": "c1440010-0000-4000-8000-000000000010",
          "entity-types": ["entity", "user-computer-profile"],
          "names": [["computer-user-profiles", "users", "username", "issue-1440-user", "computers", "hostname", "issue-1440-machine"]],
          "display-name": { "default": "Issue 1440 Machine" },
          "computer-reference": ["computers", "hostname", "issue-1440-machine"],
          "user-reference": ["users", "username", "issue-1440-user"]
        }
        """;

    private const string TrustProfileEntityJson =
        """
        {
          "entity-id": "c1440020-0000-4000-8000-000000000020",
          "entity-types": ["entity", "llm-trust-profile"],
          "names": [["tests", "trust-profiles", "issue-1440-remote"]],
          "display-name": { "default": "Issue 1440 Remote" },
          "hosting-workspaces-client-instances": ["*"],
          "filesystem-paths": [],
          "network-capabilities": [],
          "https-proxy-policy": { "mode": "disabled" },
          "allowed-mcp-tool-call-schemas": [ {} ]
        }
        """;

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Parameters_ExecutorKind_ListsTrustProfileAndUserComputerProfileEntities()
    {
        var (viewModel, launchpad) = await OpenExecutorLaunchpadAsync();

        await using (viewModel)
        {
            var executorRow = Assert.Single(launchpad.Parameters, p => p.IsExecutorPicker);
            Assert.Equal(AgentManifestParameterKind.Executor, executorRow.ParameterKind);

            Assert.Contains(
                executorRow.ExecutorOptions,
                option => option.Kind == ExecutorParameterSelection.UserComputerProfileKind
                    && SelectionValue(option.Selection, ExecutorParameterSelection.UserComputerProfileKind)
                        == new EntityId(UserComputerProfileEntityId).ToString());

            Assert.Contains(
                executorRow.ExecutorOptions,
                option => option.Kind == ExecutorParameterSelection.TrustProfileKind
                    && SelectionValue(option.Selection, ExecutorParameterSelection.TrustProfileKind)
                        == "issue-1440-remote");
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Parameters_ExecutorSelectsTrustProfile_RecordsDisambiguatedValue()
    {
        var (viewModel, launchpad) = await OpenExecutorLaunchpadAsync();

        await using (viewModel)
        {
            var executorRow = Assert.Single(launchpad.Parameters, p => p.IsExecutorPicker);
            var trustOption = Assert.Single(
                executorRow.ExecutorOptions,
                option => option.Kind == ExecutorParameterSelection.TrustProfileKind
                    && SelectionValue(option.Selection, ExecutorParameterSelection.TrustProfileKind) == "issue-1440-remote");

            executorRow.SelectedExecutorOption = trustOption;

            Assert.True(executorRow.IsValid);
            Assert.NotNull(executorRow.Selection);
            Assert.True(ExecutorParameterSelection.TryGetTrustProfile(executorRow.Selection!.Value, out var nameOrId));
            Assert.Equal("issue-1440-remote", nameOrId);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Parameters_ExecutorSelectsUserComputerProfile_RecordsDisambiguatedValue()
    {
        var (viewModel, launchpad) = await OpenExecutorLaunchpadAsync();

        await using (viewModel)
        {
            var executorRow = Assert.Single(launchpad.Parameters, p => p.IsExecutorPicker);
            var expectedEntityId = new EntityId(UserComputerProfileEntityId).ToString();
            var profileOption = Assert.Single(
                executorRow.ExecutorOptions,
                option => option.Kind == ExecutorParameterSelection.UserComputerProfileKind
                    && SelectionValue(option.Selection, ExecutorParameterSelection.UserComputerProfileKind) == expectedEntityId);

            executorRow.SelectedExecutorOption = profileOption;

            Assert.True(executorRow.IsValid);
            Assert.NotNull(executorRow.Selection);
            Assert.True(ExecutorParameterSelection.TryGetUserComputerProfile(executorRow.Selection!.Value, out var entityId));
            Assert.Equal(expectedEntityId, entityId);
        }
    }

    private static string? SelectionValue(JsonElement selection, string kind)
        => selection.ValueKind == JsonValueKind.Object
            && selection.TryGetProperty(kind, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static async Task<(MainWindowViewModel ViewModel, AgentManifestLaunchpadViewModel Launchpad)> OpenExecutorLaunchpadAsync()
    {
        var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var broker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            broker, new EntityId(UserComputerProfileEntityId), UserComputerProfileEntityJson);
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            broker, new EntityId(TrustProfileEntityId), TrustProfileEntityJson);
        var manifestEntity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            broker, new EntityId(ExecutorManifestEntityId), ExecutorManifestEntityJson);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var inner = CreateTestRunningAgentChatTable();
        var spy = new SpyRunningAgentChatTable(inner);
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext,
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            spy);

        var launchpad = new AgentManifestLaunchpadViewModel(
            manifestEntity,
            agentSessionShortcutContext,
            openAgentSessionShortcutHandler,
            viewModel)
        {
            Id = $"launchpad-{manifestEntity.EntityId}",
            Title = manifestEntity.DisplayName,
            DockRegion = "full",
            Entity = manifestEntity,
        };

        await viewModel.OpenTabAsync(launchpad);
        await launchpad.ExecutorOptionsLoaded;
        return (viewModel, launchpad);
    }

    // ---- Issue #1463: launchpad hosts the shared ManifestParametersViewModel component ----

    private const string Issue1463RequiredManifestEntityId = "d1463101-0000-4000-8000-000000000101";
    private const string Issue1463ExecutorManifestEntityId = "d1463102-0000-4000-8000-000000000102";

    private const string Issue1463RequiredManifestEntityJson =
        """
        {
          "entity-id": "d1463101-0000-4000-8000-000000000101",
          "entity-types": ["entity", "agent-manifest"],
          "names": [["tests", "agent-manifests", "issue-1463-required"]],
          "display-name": { "default": "Issue 1463 Required" },
          "manifest": {
            "name": "issue-1463-required",
            "displayName": "Issue 1463 Required",
            "parameters": {
              "properties": [
                { "name": "alpha", "required": true }
              ]
            },
            "template": {
              "kind": "prompt",
              "name": "issue-1463-required",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
            }
          }
        }
        """;

    private const string Issue1463ExecutorManifestEntityJson =
        """
        {
          "entity-id": "d1463102-0000-4000-8000-000000000102",
          "entity-types": ["entity", "agent-manifest"],
          "names": [["tests", "agent-manifests", "issue-1463-executor"]],
          "display-name": { "default": "Issue 1463 Executor" },
          "manifest": {
            "name": "issue-1463-executor",
            "displayName": "Issue 1463 Executor",
            "parameters": {
              "properties": [
                { "name": "topic", "required": false },
                { "name": "worker-executor", "kind": "executor", "required": true }
              ]
            },
            "template": {
              "kind": "prompt",
              "name": "issue-1463-executor",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
            },
            "resources": [
              {
                "kind": "executor",
                "id": "parameter",
                "name": "worker",
                "options": { "parameter": "worker-executor" }
              }
            ]
          }
        }
        """;

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Launchpad_HostsParametersComponent_CanStartTracksIsValid()
    {
        var (viewModel, launchpad, _) = await OpenLaunchpadForAsync(
            new EntityId(Issue1463RequiredManifestEntityId),
            Issue1463RequiredManifestEntityJson);

        await using (viewModel)
        {
            var row = Assert.Single(launchpad.ManifestParameters.Parameters);

            // Required row empty → component invalid and launchpad mirrors it.
            Assert.False(launchpad.ManifestParameters.IsValid);
            Assert.Equal(launchpad.ManifestParameters.IsValid, launchpad.CanStart);
            Assert.False(launchpad.StartSessionCommand.CanExecute(null));

            row.Value = "provided";

            Assert.True(launchpad.ManifestParameters.IsValid);
            Assert.Equal(launchpad.ManifestParameters.IsValid, launchpad.CanStart);
            Assert.True(launchpad.StartSessionCommand.CanExecute(null));
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Launchpad_StartSession_UsesComponentValuesAndSelections()
    {
        var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var broker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            broker, new EntityId(UserComputerProfileEntityId), UserComputerProfileEntityJson);
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            broker, new EntityId(TrustProfileEntityId), TrustProfileEntityJson);
        var manifestEntity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            broker, new EntityId(Issue1463ExecutorManifestEntityId), Issue1463ExecutorManifestEntityJson);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var inner = CreateTestRunningAgentChatTable();
        var spy = new SpyRunningAgentChatTable(inner);
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext,
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            spy);

        var launchpad = new AgentManifestLaunchpadViewModel(
            manifestEntity,
            agentSessionShortcutContext,
            openAgentSessionShortcutHandler,
            viewModel,
            new Dictionary<string, string> { ["topic"] = "weather" })
        {
            Id = $"launchpad-{manifestEntity.EntityId}",
            Title = manifestEntity.DisplayName,
            DockRegion = "full",
            Entity = manifestEntity,
        };

        await viewModel.OpenTabAsync(launchpad);
        await launchpad.ExecutorOptionsLoaded;

        await using (viewModel)
        {
            var executorRow = Assert.Single(launchpad.ManifestParameters.Parameters, p => p.IsExecutorPicker);
            var profileOption = Assert.Single(
                executorRow.ExecutorOptions,
                option => option.Kind == ExecutorParameterSelection.UserComputerProfileKind
                    && SelectionValue(option.Selection, ExecutorParameterSelection.UserComputerProfileKind)
                        == new EntityId(UserComputerProfileEntityId).ToString());
            executorRow.SelectedExecutorOption = profileOption;

            launchpad.StartSessionCommand.Execute(null);

            var sessionTab = await MainWindowIntegrationTests.WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(
                viewModel.SelectedWorkspacePane);
            await MainWindowIntegrationTests.WaitForAgentReadyAsync(sessionTab);

            Assert.NotNull(sessionTab.Entity);
            Assert.True(sessionTab.Entity!.Data is JsonElement);
            var data = (JsonElement)sessionTab.Entity!.Data!;

            // Text value collected via component.GetValues().
            Assert.True(data.TryGetProperty("parameter-values", out var parameterValues));
            Assert.Equal("weather", parameterValues.GetProperty("topic").GetString());

            // Executor selection collected via component.GetSelections().
            Assert.True(data.TryGetProperty("parameter-selections", out var parameterSelections));
            Assert.True(parameterSelections.TryGetProperty("worker-executor", out var workerSelection));
            Assert.True(ExecutorParameterSelection.TryGetUserComputerProfile(workerSelection, out var profileId));
            Assert.Equal(new EntityId(UserComputerProfileEntityId).ToString(), profileId);

            var workerBinding = data
                .GetProperty("executor-bindings")
                .GetProperty("components")
                .GetProperty("worker");
            Assert.Equal("user-computer-profile", workerBinding.GetProperty("type").GetString());
            Assert.Equal(new EntityId(UserComputerProfileEntityId).ToString(), workerBinding.GetProperty("entity-id").GetString());
            Assert.Equal(data.GetRawText(), spy.LastRequest?.AgentSessionEntity?.GetRawText());
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Launchpad_StartSession_WithTrustProfileSelection_ResolvesTrustProfileFreshLaunchBranch()
    {
        // Regression pin for #1481 — the fresh-launch trust-profile branch in
        // AgentManifestSessionLauncher.ResolveSelectedTrustProfileAsync must resolve the referenced
        // trust profile via DataAccessLayerTrustProfileResolver so that ExecutorBindings.Build
        // authors a nonlocal worker binding for the persisted session entity. The prior test only
        // covered the user-computer-profile branch and did not exercise this code path.
        var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var broker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            broker, new EntityId(UserComputerProfileEntityId), UserComputerProfileEntityJson);
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            broker, new EntityId(TrustProfileEntityId), TrustProfileEntityJson);
        var manifestEntity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            broker, new EntityId(Issue1463ExecutorManifestEntityId), Issue1463ExecutorManifestEntityJson);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var inner = CreateTestRunningAgentChatTable();
        var spy = new SpyRunningAgentChatTable(inner);
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext,
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            spy);

        var launchpad = new AgentManifestLaunchpadViewModel(
            manifestEntity,
            agentSessionShortcutContext,
            openAgentSessionShortcutHandler,
            viewModel,
            new Dictionary<string, string> { ["topic"] = "weather" })
        {
            Id = $"launchpad-{manifestEntity.EntityId}",
            Title = manifestEntity.DisplayName,
            DockRegion = "full",
            Entity = manifestEntity,
        };

        await viewModel.OpenTabAsync(launchpad);
        await launchpad.ExecutorOptionsLoaded;

        await using (viewModel)
        {
            var executorRow = Assert.Single(launchpad.ManifestParameters.Parameters, p => p.IsExecutorPicker);
            var trustOption = Assert.Single(
                executorRow.ExecutorOptions,
                option => option.Kind == ExecutorParameterSelection.TrustProfileKind
                    && SelectionValue(option.Selection, ExecutorParameterSelection.TrustProfileKind) == "issue-1440-remote");
            executorRow.SelectedExecutorOption = trustOption;

            launchpad.StartSessionCommand.Execute(null);

            var sessionTab = await MainWindowIntegrationTests.WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(
                viewModel.SelectedWorkspacePane);
            await MainWindowIntegrationTests.WaitForAgentReadyAsync(sessionTab);

            Assert.NotNull(sessionTab.Entity);
            Assert.True(sessionTab.Entity!.Data is JsonElement);
            var data = (JsonElement)sessionTab.Entity!.Data!;

            // The trust-profile selection round-trips into the persisted parameter-selections.
            Assert.True(data.TryGetProperty("parameter-selections", out var parameterSelections));
            Assert.True(parameterSelections.TryGetProperty("worker-executor", out var workerSelection));
            Assert.True(ExecutorParameterSelection.TryGetTrustProfile(workerSelection, out var trustName));
            Assert.Equal("issue-1440-remote", trustName);

            // The trust-profile fresh-launch branch resolved the profile and Build authored a worker
            // binding (proof the ResolveSelectedTrustProfileAsync branch actually ran; the prior
            // launch test's user-computer-profile branch skipped it entirely).
            Assert.True(data.TryGetProperty("executor-bindings", out var executorBindings));
            var workerBinding = executorBindings.GetProperty("components").GetProperty("worker");
            Assert.NotEqual(JsonValueKind.Undefined, workerBinding.ValueKind);
            Assert.True(workerBinding.TryGetProperty("type", out var workerType));
            Assert.Equal(JsonValueKind.String, workerType.ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(workerType.GetString()));
            Assert.Equal(data.GetRawText(), spy.LastRequest?.AgentSessionEntity?.GetRawText());
        }
    }

    private static RunningAgentChatTable CreateTestRunningAgentChatTable()
    {
        var store = new InMemoryAgentPersistenceStore();
        var factory = new AgentChatFactory(store, new AgentServices(), SynchronizationContextTaskScheduler.FromCurrent());
        var registryProvider = new TransportFactoryRegistryProvider(new TransportFactoryRegistry());
        return new RunningAgentChatTable(factory, AgentSessionRuntimeContextFactory.FromProvider(registryProvider));
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
