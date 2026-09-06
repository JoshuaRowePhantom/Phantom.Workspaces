using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Tests for issue #1461: the workspace "New Agent" selection tab
/// (<see cref="NewAgentSelectionViewModel"/>). It hosts the shared
/// <see cref="ManifestParametersViewModel"/> (issue #1463), pre-selects a <c>default</c>-related
/// manifest, refreshes+persists parameter rows on manifest change, and on confirm launches the agent
/// session, dismisses itself, relates the workspace to the new agent session, and saves the workspace.
/// </summary>
public sealed class NewAgentSelectionViewModelTests
{
    private const string WorkspaceEntityId = "e1462001-0000-4000-8000-000000000001";
    private const string EchoManifestEntityId = "e1462010-0000-4000-8000-000000000010";
    private const string ManifestAEntityId = "e1462020-0000-4000-8000-000000000020";
    private const string ManifestBEntityId = "e1462021-0000-4000-8000-000000000021";

    private static readonly string WorkspaceEntityJson =
        $$"""
        {
          "entity-id": "{{WorkspaceEntityId}}",
          "entity-types": ["entity", "workspace"],
          "display-name": { "default": "New Agent WS" },
          "regions": []
        }
        """;

    // Parameter-free echo manifest: valid immediately, launches to Ready.
    private static readonly string EchoManifestJson =
        $$"""
        {
          "entity-id": "{{EchoManifestEntityId}}",
          "entity-types": ["entity", "agent-manifest"],
          "names": [["tests", "agent-manifests", "1461-echo"]],
          "display-name": { "default": "1461 Echo" },
          "manifest": {
            "name": "1461-echo",
            "displayName": "1461 Echo",
            "template": {
              "kind": "prompt",
              "name": "1461-echo",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
            }
          }
        }
        """;

    // alpha (required) + beta (optional).
    private static readonly string ManifestAJson =
        $$"""
        {
          "entity-id": "{{ManifestAEntityId}}",
          "entity-types": ["entity", "agent-manifest"],
          "names": [["tests", "agent-manifests", "1461-a"]],
          "display-name": { "default": "1461 A" },
          "manifest": {
            "name": "1461-a",
            "displayName": "1461 A",
            "parameters": {
              "properties": [
                { "name": "alpha", "required": true },
                { "name": "beta", "required": false }
              ]
            },
            "template": {
              "kind": "prompt",
              "name": "1461-a",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
            }
          }
        }
        """;

    // alpha (shared) + gamma.
    private static readonly string ManifestBJson =
        $$"""
        {
          "entity-id": "{{ManifestBEntityId}}",
          "entity-types": ["entity", "agent-manifest"],
          "names": [["tests", "agent-manifests", "1461-b"]],
          "display-name": { "default": "1461 B" },
          "manifest": {
            "name": "1461-b",
            "displayName": "1461 B",
            "parameters": {
              "properties": [
                { "name": "alpha", "required": false },
                { "name": "gamma", "required": false }
              ]
            },
            "template": {
              "kind": "prompt",
              "name": "1461-b",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
            }
          }
        }
        """;

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NewAgentSelection_WhenDefaultRelationshipExists_PreSelectsDefaultManifest()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.SeedEntityAsync(new EntityId(EchoManifestEntityId), EchoManifestJson);
        await harness.SeedEntityAsync(new EntityId(ManifestAEntityId), ManifestAJson);

        // A default relationship on the workspace points at manifest A.
        await harness.SeedEntityAsync(
            new EntityId(Guid.NewGuid()),
            $$"""
            {
              "entity-types": ["entity", "default", "relationship"],
              "names": [["relationships", "default-1461"]],
              "participants": { "applied-to": "{{WorkspaceEntityId}}", "value": "{{ManifestAEntityId}}" }
            }
            """);

        var vm = await harness.OpenSelectionTabAsync();
        await vm.Loaded;

        Assert.NotNull(vm.SelectedManifest);
        Assert.Equal(new EntityId(ManifestAEntityId), vm.SelectedManifest!.Entity.EntityId);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NewAgentSelection_WhenManifestChanges_RefreshesParametersAndPersistsByName()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.SeedEntityAsync(new EntityId(ManifestAEntityId), ManifestAJson);
        await harness.SeedEntityAsync(new EntityId(ManifestBEntityId), ManifestBJson);

        var vm = await harness.OpenSelectionTabAsync();
        await vm.Loaded;

        var manifestA = vm.Manifests.Single(m => m.Entity.EntityId == new EntityId(ManifestAEntityId));
        var manifestB = vm.Manifests.Single(m => m.Entity.EntityId == new EntityId(ManifestBEntityId));

        vm.SelectedManifest = manifestA;
        Assert.Equal(["alpha", "beta"], vm.ManifestParameters.Parameters.Select(p => p.Name).ToArray());
        Assert.Single(vm.ManifestParameters.Parameters, p => p.Name == "alpha").Value = "kept";

        vm.SelectedManifest = manifestB;
        // Rows refresh to manifest B's schema...
        Assert.Equal(["alpha", "gamma"], vm.ManifestParameters.Parameters.Select(p => p.Name).ToArray());
        // ...and the value entered for the shared "alpha" name is retained.
        Assert.Equal("kept", Assert.Single(vm.ManifestParameters.Parameters, p => p.Name == "alpha").Value);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NewAgentSelection_OnConfirm_LaunchesAgentAndClosesSelectionTab()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.SeedEntityAsync(new EntityId(EchoManifestEntityId), EchoManifestJson);

        var vm = await harness.OpenSelectionTabAsync();
        await vm.Loaded;
        vm.SelectedManifest = vm.Manifests.Single(m => m.Entity.EntityId == new EntityId(EchoManifestEntityId));

        await vm.ConfirmAsync();

        var sessionTab = await MainWindowIntegrationTests.WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(harness.Pane);
        await MainWindowIntegrationTests.WaitForAgentReadyAsync(sessionTab);

        Assert.Equal(AgentTabState.Ready, sessionTab.State);
        Assert.DoesNotContain(harness.Pane.Tabs, tab => tab is NewAgentSelectionViewModel);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NewAgentSelection_OnConfirm_AddsRelatedRelationshipBetweenWorkspaceAndAgent()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.SeedEntityAsync(new EntityId(EchoManifestEntityId), EchoManifestJson);

        var vm = await harness.OpenSelectionTabAsync();
        await vm.Loaded;
        vm.SelectedManifest = vm.Manifests.Single(m => m.Entity.EntityId == new EntityId(EchoManifestEntityId));

        await vm.ConfirmAsync();

        var sessionTab = await MainWindowIntegrationTests.WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(harness.Pane);
        await MainWindowIntegrationTests.WaitForAgentReadyAsync(sessionTab);
        var agentSessionId = new EntityId(sessionTab.Entity!.EntityId.Value);

        var workspaceSnapshot = await harness.GetWithRelatedAsync(new EntityId(WorkspaceEntityId));
        Assert.Contains(
            workspaceSnapshot.Relationships,
            r => HasEntityType(r, "related") && ParticipantEntities(r).Contains(agentSessionId.Value.ToString()));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NewAgentSelection_OnConfirm_SavesWorkspace()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.SeedEntityAsync(new EntityId(EchoManifestEntityId), EchoManifestJson);

        var vm = await harness.OpenSelectionTabAsync();
        await vm.Loaded;
        vm.SelectedManifest = vm.Manifests.Single(m => m.Entity.EntityId == new EntityId(EchoManifestEntityId));

        await vm.ConfirmAsync();

        var sessionTab = await MainWindowIntegrationTests.WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(harness.Pane);
        await MainWindowIntegrationTests.WaitForAgentReadyAsync(sessionTab);
        var agentSessionId = sessionTab.Entity!.EntityId.Value.ToString();

        // The save path persists the pane's live tabs onto the workspace entity. Reloading the
        // workspace entity must therefore show a tab descriptor referencing the new agent session,
        // which only exists if SaveCommand ran as part of confirm.
        var workspaceSnapshot = await harness.GetWithRelatedAsync(new EntityId(WorkspaceEntityId));
        Assert.True(workspaceSnapshot.Data is { } data && data.TryGetProperty("tabs", out var tabs)
            && tabs.EnumerateArray().Any(tab =>
                tab.TryGetProperty("content", out var content)
                && content.TryGetProperty("target-entity-name", out var target)
                && target.ValueKind == JsonValueKind.String
                && target.GetString() == agentSessionId),
            "Saved workspace entity did not contain a persisted tab for the launched agent session.");
    }

    private static bool HasEntityType(EntitySnapshot relationship, string entityType)
        => relationship.Data is { } data
            && data.TryGetProperty("entity-types", out var types)
            && types.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && t.GetString() == entityType);

    private static string[] ParticipantEntities(EntitySnapshot relationship)
    {
        if (relationship.Data is not { } data
            || !data.TryGetProperty("participants", out var participants)
            || !participants.TryGetProperty("entities", out var entities)
            || entities.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return entities.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToArray();
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required MainWindowViewModel ViewModel { get; init; }

        public required EntityBroker Broker { get; init; }

        public required SubscribedEntityViewModel WorkspaceEntity { get; init; }

        public required WorkspacePaneViewModel Pane { get; init; }

        public IDataAccessLayer DataAccessLayer => this.Broker.EntityRepository.DataAccessLayer;

        public static async Task<Harness> CreateAsync()
        {
            var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
            await viewModel.InitializeAsync();

            var broker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
            var workspaceId = new EntityId(WorkspaceEntityId);
            var workspaceEntity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
                broker, workspaceId, WorkspaceEntityJson);

            await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });
            var pane = Assert.Single(
                viewModel.WorkspacePanes,
                p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
            await MainWindowIntegrationTests.WaitForPanePopulatedAsync(pane);
            foreach (var tab in pane.Tabs.ToList())
            {
                viewModel.CloseTab(tab);
            }

            viewModel.SelectedWorkspacePane = pane;

            return new Harness
            {
                ViewModel = viewModel,
                Broker = broker,
                WorkspaceEntity = workspaceEntity,
                Pane = pane,
            };
        }

        public Task<SubscribedEntityViewModel> SeedEntityAsync(EntityId id, string json)
            => MainWindowIntegrationTests.UpsertEntityAndLoadAsync(this.Broker, id, json);

        public async Task<NewAgentSelectionViewModel> OpenSelectionTabAsync()
        {
            var handler = new NewAgentOnWorkspaceShortcutHandler(
                new AgentSessionShortcutContext(),
                new OpenAgentSessionShortcutHandler(
                    new AgentSessionShortcutContext(),
                    MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
                    MainWindowIntegrationTests.CreateTestRunningAgentChatTable()));

            Assert.True(await handler.Handle(this.ViewModel, Shortcut.NewAgentOnWorkspace, this.WorkspaceEntity));
            return this.Pane.Tabs.OfType<NewAgentSelectionViewModel>().Single();
        }

        public async Task<EntitySnapshot> GetWithRelatedAsync(EntityId entityId)
        {
            var result = await this.DataAccessLayer.GetAsync(
                new GetRequest
                {
                    Entities =
                    [
                        new GetEntityRequest
                        {
                            EntityId = entityId,
                            RelationshipsToReturn =
                            [
                                new GetRelationshipRequest { RelationshipTypeNames = new RelationshipTypeNameSet(["related"]) },
                            ],
                        },
                    ],
                },
                CancellationToken.None);
            return result.Batches.SelectMany(batch => batch.Entities).Single(entity => entity.EntityId == entityId);
        }

        public ValueTask DisposeAsync() => this.ViewModel.DisposeAsync();
    }
}
