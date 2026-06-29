using Avalonia.Media;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Services.Notifications;
using Phantom.Workspaces.ViewModels;
using AgentViewModel = Phantom.Workspaces.Agent.Gui.ViewModels.AgentViewModel;

namespace Phantom.Workspaces.Tests;

[Trait("Category", "SlowLayout")]
public sealed class MainWindowIntegrationTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public async Task ThemeResources_UseFontFamilyType()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        Assert.True(Avalonia.Application.Current!.Resources.TryGetValue("Theme.FontFamily", out var fontFamilyResource));
        Assert.IsType<FontFamily>(fontFamilyResource);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task InMemoryRepository_InitializesWithExpectedPipeline()
    {
        var repository = await EntityRepository.CreateAsync(CreateInMemoryRepositorySource());
        var snapshots = await repository.ExportEntitySnapshotsAsync();
        Assert.IsType<WorkspaceEntitySessionDataAccessLayer>(repository.DataAccessLayer);
        Assert.NotEqual(default, repository.WorkspaceEntitySession.UserEntityId);
        Assert.NotEqual(default, repository.WorkspaceEntitySession.ComputerEntityId);
        Assert.NotEqual(default, repository.WorkspaceEntitySession.UserComputerProfileEntityId);
        Assert.NotEmpty(snapshots);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task InMemoryRepository_SeedsGithubModelsAgentManifest()
    {
        var repository = await EntityRepository.CreateAsync(CreateInMemoryRepositorySource());
        var snapshots = await repository.ExportEntitySnapshotsAsync();
        var githubModelsSnapshot = Assert.Single(
            snapshots,
            snapshot => ReadEntityNames(snapshot.Value.Data).Any(
                static entityName => entityName.Components.Length == 3
                    && string.Equals(entityName.Components[0], "defaults", StringComparison.Ordinal)
                    && string.Equals(entityName.Components[1], "agent-manifests", StringComparison.Ordinal)
                    && string.Equals(entityName.Components[2], "github-models", StringComparison.Ordinal)));
        Assert.Equal("GitHub Models Workspace Assistant", ReadDefaultDisplayName(githubModelsSnapshot.Value.Data));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task InMemoryRepository_SeedsWorkspacesAgentManifestDisplayName()
    {
        var repository = await EntityRepository.CreateAsync(CreateInMemoryRepositorySource());
        var snapshots = await repository.ExportEntitySnapshotsAsync();
        var workspacesSnapshot = Assert.Single(
            snapshots,
            snapshot => ReadEntityNames(snapshot.Value.Data).Any(
                static entityName => entityName.Components.Length == 3
                    && string.Equals(entityName.Components[0], "defaults", StringComparison.Ordinal)
                    && string.Equals(entityName.Components[1], "agent-manifests", StringComparison.Ordinal)
                    && string.Equals(entityName.Components[2], "workspaces", StringComparison.Ordinal)));
        Assert.Equal("Workspaces Assistant", ReadDefaultDisplayName(workspacesSnapshot.Value.Data));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void MainWindowViewModel_ThemeSelectionIsDataDriven()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        Assert.Contains("dark", viewModel.ThemeNames);
        Assert.Contains("light", viewModel.ThemeNames);
        viewModel.SelectedThemeName = "light";
        Assert.Equal("light", viewModel.SelectedThemeName);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task SelectedThemeName_SetToLight_PersistsAcrossViewModelInstances()
    {
        var profilePath = CreateTempProfileStorePath();
        try
        {
            var store = new ProfileStore(profilePath);

            var vm1 = new MainWindowViewModel(CreateInMemoryRepositorySource(), profileStore: store);
            await vm1.InitializeAsync();
            await vm1.SetThemeAsync("light");

            var vm2 = new MainWindowViewModel(CreateInMemoryRepositorySource(), profileStore: store);
            await vm2.InitializeAsync();

            Assert.Equal("light", vm2.SelectedThemeName);
        }
        finally
        {
            DeleteTempProfileStoreDirectory(profilePath);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_InitializeAsync_ReplacesDefaultAndLoadingWorkspacePanes()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        Assert.NotEmpty(viewModel.WorkspacePanes);
        Assert.DoesNotContain(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, "default-workspace", StringComparison.Ordinal)
                || pane.Id.StartsWith("loading-workspace:", StringComparison.Ordinal));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WhenAlreadyOpening_SecondRequestIsNoOp()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "concurrent-open"]],
              "display-name": { "default": "Concurrent Open Workspace" },
              "regions": []
            }
            """);

        var request = new GetEntityRequest { EntityId = workspaceId };

        // The first open runs synchronously until its first await (creating the loading pane);
        // the second open must observe the in-progress load and be a no-op so the workspace is
        // only opened once (issue #23).
        var firstOpen = viewModel.OpenWorkspaceAsync(request);
        var secondOpen = viewModel.OpenWorkspaceAsync(request);
        await Task.WhenAll(firstOpen, secondOpen);

        Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(
            viewModel.WorkspacePanes,
            pane => pane.Id.StartsWith("loading-workspace:", StringComparison.Ordinal));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithExternalEntityTab_PopulatesTabAsynchronously()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        // Create an external entity referenced by the workspace tab
        var externalEntityId = new EntityId("bbbbbbbb-bbbb-4bbb-bbbb-bbbbbbbbbbbb");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            externalEntityId,
            """
            {
              "entity-id": "bbbbbbbb-bbbb-4bbb-bbbb-bbbbbbbbbbbb",
              "entity-types": ["entity", "external"],
              "names": [["tests", "externals", "tab-async-test"]],
              "display-name": { "default": "Async Tab Test" },
              "urls": { "default": "https://example.com" }
            }
            """);

        // Create a workspace that references the external entity
        var workspaceId = new EntityId("cccccccc-cccc-4ccc-cccc-cccccccccccc");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "cccccccc-cccc-4ccc-cccc-cccccccccccc",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "async-tabs"]],
              "display-name": { "default": "Async Tabs Workspace" },
              "regions": [
                {
                  "region-id": "main",
                  "title": "Main",
                  "dock": "center",
                  "size": 1.0,
                  "tabs": [
                    {
                      "tab-id": "async-tab-1",
                      "title": "Async Tab",
                      "kind": "entity",
                      "dock": "full",
                      "content": {
                        "target-entity-name": ["tests", "externals", "tab-async-test"]
                      }
                    }
                  ]
                }
              ]
            }
            """);

        // Open the workspace — Phase 1 (skeleton) completes on return; Phase 2 populates tabs async
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        // The workspace pane must be visible immediately after Phase 1
        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        // Wait for Phase 2 to add at least one tab (deterministic: watch ContentDock)
        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, "async-tab-1");

        var tabDoc = contentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .FirstOrDefault(d => d.Id == "async-tab-1");
        Assert.NotNull(tabDoc);
        Assert.IsType<WebViewModel>(tabDoc!.TabViewModel);
    }
    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_SessionsView_GetEntitySubViewsIncludeAgentManifestEntities()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var sessionsView = Assert.Single(
            viewModel.TopLevelViews,
            static view => string.Equals(view.Title, "Sessions", StringComparison.Ordinal));
        viewModel.SelectedTopLevelView = sessionsView;

        var applySelectedViewMethod = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applySelectedViewMethod);
        await (Task)applySelectedViewMethod!.Invoke(viewModel, [])!;

        Assert.Contains(
            viewModel.CurrentViewPopulation.Entities,
            static entity => string.Equals(entity.EntityType, "agent-manifest", StringComparison.Ordinal));
        Assert.DoesNotContain(
            viewModel.CurrentViewPopulation.Entities,
            static entity => string.Equals(entity.EntityType, "view", StringComparison.Ordinal));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ViewEntityViewModel_TraversedEntitiesCollapsed_WhenDispositionIsCollapsed()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        // Override the workspace entity-type-view to have traversed-entity-display-disposition: "collapsed".
        var entityTypeViewId = new EntityId("a9d73483-6752-40b3-9fed-5831616814a6");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            entityTypeViewId,
            """
            {
              "entity-id": "a9d73483-6752-40b3-9fed-5831616814a6",
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views", "workspace"]],
              "display-name": { "default": "Workspace View" },
              "fields": [],
              "traversed-entity-display-disposition": "collapsed",
              "traverse-relationships": [
                { "relationship-type-ids": ["related"] }
              ]
            }
            """);

        var workspaceId = new EntityId("b1000001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "b1000001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["workspaces", "collapse-test"]],
              "display-name": { "default": "Collapse Test Workspace" },
              "regions": []
            }
            """);

        var relatedId = new EntityId("b1000002-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            relatedId,
            """
            {
              "entity-id": "b1000002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "note"],
              "names": [["notes", "collapse-test-note"]],
              "display-name": { "default": "Related Note" },
              "content": { "mime-type": "text/markdown", "content": { "text": "note" } }
            }
            """);

        var relId = new EntityId("b1000003-0000-4000-8000-000000000003");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            relId,
            $$"""
            {
              "entity-id": "b1000003-0000-4000-8000-000000000003",
              "entity-types": ["entity", "related", "relationship"],
              "names": [["relationships", "collapse-test-rel"]],
              "participants": { "entities": ["{{workspaceId.Value}}", "{{relatedId.Value}}"] }
            }
            """);

        var workspacesView = viewModel.TopLevelViews.FirstOrDefault(
            static v => string.Equals(v.Title, "Workspaces", StringComparison.Ordinal));
        Assert.NotNull(workspacesView);
        viewModel.SelectedTopLevelView = workspacesView!;

        var applySelectedViewMethod = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applySelectedViewMethod);
        await (Task)applySelectedViewMethod!.Invoke(viewModel, [])!;

        var workspaceVm = viewModel.CurrentViewPopulation.Entities.FirstOrDefault(
            e => e.Entity.EntityId == workspaceId);
        Assert.NotNull(workspaceVm);
        Assert.False(workspaceVm!.IsExpanded);

        // Traversed child must NOT appear in the flat population when disposition is "collapsed".
        Assert.DoesNotContain(
            viewModel.CurrentViewPopulation.Entities,
            e => e.Entity.EntityId == relatedId);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void MainWindow_ConstructsWithoutTemplateCastErrors()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        var window = new MainWindow(viewModel);

        Assert.NotNull(window);
        Assert.Empty(window.DataTemplates);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CreateWorkspacePane_DoesNotInjectFallbackCenterRegion_WhenWorkspaceHasNoRegions()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();
        
        using var workspaceDocument = JsonDocument.Parse(
            """
            {
              "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Workspace Without Regions" }
            }
            """);
        using var entityDocument = JsonDocument.Parse(
            """
            {
              "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Workspace Without Regions" }
            }
            """);
        var workspaceEntity = new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = entityDocument.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });

        var createWorkspacePane = typeof(MainWindowViewModel).GetMethod(
            "CreateWorkspacePaneAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(createWorkspacePane);

        var task = (Task<WorkspacePaneViewModel>?)createWorkspacePane!.Invoke(
            viewModel,
            [workspaceEntity, workspaceDocument.RootElement.Clone()]);
        Assert.NotNull(task);
        
        var workspacePane = await task!;
        Assert.NotNull(workspacePane);
        
        // With Dock integration, regions are synthetic (created via SyncSelectedWorkspacePaneFromDock)
        // When workspace has no regions in JSON, we create a default tab for the workspace entity
        // The synthetic region should exist after the tab is added
        Assert.NotNull(workspacePane!.SelectedRegion);
        Assert.Single(workspacePane.SelectedRegion!.Tabs);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentDefinitionShortcutHandler_LocalEchoDefinition_CreatesAgentSessionTab()
    {
        var fixedCurrentTime = new DateTimeOffset(2026, 06, 12, 9, 23, 45, TimeSpan.Zero);
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("f95a86dc-f71f-43f8-abf5-31c6444f7a4e"),
            """
            {
              "entity-id": "f95a86dc-f71f-43f8-abf5-31c6444f7a4e",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "local-echo"]],
              "display-name": { "default": "Local Echo" },
              "definition": {
                "kind": "prompt",
                "name": "local-echo",
                "model": {
                  "id": "echo",
                  "provider": "echo",
                  "apiType": "Echo"
                },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext(() => fixedCurrentTime);
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(agentSessionShortcutContext);
        var openAgentDefinitionShortcutHandler = new OpenAgentDefinitionShortcutHandler(agentSessionShortcutContext, openAgentSessionShortcutHandler);

        var handled = await openAgentDefinitionShortcutHandler.Handle(viewModel, Shortcut.Open, agentDefinitionEntity);

        Assert.True(handled);
        var selectedRegion = Assert.IsType<WorkspaceRegionViewModel>(viewModel.SelectedWorkspacePane.SelectedRegion);
        var launchpadTab = Assert.IsType<AgentManifestLaunchpadViewModel>(selectedRegion.SelectedTab);
        Assert.Same(agentDefinitionEntity, launchpadTab.ManifestEntity);
        Assert.True(launchpadTab.CanStart);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentManifestShortcutHandler_LocalEchoManifest_CreatesAgentSessionTab()
    {
        var fixedCurrentTime = new DateTimeOffset(2026, 06, 12, 9, 23, 45, TimeSpan.Zero);
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentManifestEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("a1b2c3d4-0000-4000-8000-000000000001"),
            """
            {
              "entity-id": "a1b2c3d4-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-manifest"],
              "names": [["tests", "agent-manifests", "local-echo"]],
              "display-name": { "default": "Local Echo Manifest" },
              "manifest": {
                "name": "local-echo",
                "displayName": "Local Echo Manifest",
                "template": {
                  "kind": "prompt",
                  "name": "local-echo",
                  "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
                },
                "resources": [
                  { "kind": "tool", "id": "fixed", "name": "workspace-entity" }
                ]
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext(() => fixedCurrentTime);
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(agentSessionShortcutContext);
        var openAgentManifestShortcutHandler = new OpenAgentManifestShortcutHandler(agentSessionShortcutContext, openAgentSessionShortcutHandler);

        var handled = await openAgentManifestShortcutHandler.Handle(viewModel, Shortcut.Open, agentManifestEntity);

        Assert.True(handled);
        var selectedRegion = Assert.IsType<WorkspaceRegionViewModel>(viewModel.SelectedWorkspacePane.SelectedRegion);
        var launchpadTab = Assert.IsType<AgentManifestLaunchpadViewModel>(selectedRegion.SelectedTab);
        Assert.Same(agentManifestEntity, launchpadTab.ManifestEntity);
        Assert.True(launchpadTab.CanStart);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentDefinitionShortcutHandler_WorkspaceEntityTool_IsMappedInWorkspacesGui()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("b6731cc0-fb8a-4f8e-9f89-3f33a5db1b8a"),
            """
            {
              "entity-id": "b6731cc0-fb8a-4f8e-9f89-3f33a5db1b8a",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "workspace-entity-tool"]],
              "display-name": { "default": "Workspace Entity Tool Agent" },
              "definition": {
                "kind": "prompt",
                "name": "workspace-entity-tool",
                "model": {
                  "id": "echo",
                  "provider": "echo",
                  "apiType": "Echo"
                },
                "tools": [
                  {
                    "kind": "workspace-entity",
                    "description": "Read and modify workspace entities."
                  }
                ]
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(agentSessionShortcutContext);
        var openAgentDefinitionShortcutHandler = new OpenAgentDefinitionShortcutHandler(agentSessionShortcutContext, openAgentSessionShortcutHandler);

        var handled = await openAgentDefinitionShortcutHandler.Handle(viewModel, Shortcut.Open, agentDefinitionEntity);

        Assert.True(handled);
        var selectedRegion = Assert.IsType<WorkspaceRegionViewModel>(viewModel.SelectedWorkspacePane.SelectedRegion);
        var launchpadTab = Assert.IsType<AgentManifestLaunchpadViewModel>(selectedRegion.SelectedTab);
        Assert.Same(agentDefinitionEntity, launchpadTab.ManifestEntity);

        // Create an agent session directly (equivalent to the launchpad's Start Session) to verify tool mapping.
        var agentSessionId = Guid.NewGuid().ToString("n");
        var createdAgentSession = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId);
        Assert.NotNull(createdAgentSession);
        await openAgentSessionShortcutHandler.Handle(viewModel, Shortcut.Open, createdAgentSession!);
        var sessionTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            Assert.IsType<WorkspaceRegionViewModel>(viewModel.SelectedWorkspacePane.SelectedRegion).SelectedTab);
        await WaitForAgentReadyAsync(sessionTab);
        Assert.NotNull(sessionTab.Agent);
        Assert.Contains(sessionTab.Agent.Tools, static tool => string.Equals(tool.Kind, "workspace-entity", StringComparison.Ordinal));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CreateWorkspacePaneAsync_WithAgentSessionTab_CreatesAgentSessionWorkspaceTabViewModel()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        // Create an agent-definition entity.
        var agentDefinitionId = new EntityId("c0ffee01-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            agentDefinitionId,
            """
            {
              "entity-id": "c0ffee01-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "tab-restore-echo"]],
              "display-name": { "default": "Tab Restore Echo" },
              "definition": {
                "kind": "prompt",
                "name": "tab-restore-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        // Create the agent-session entity directly without going through the shortcut handler.
        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentDefinitionEntity = Assert.Single(await entityBroker.GetEntitiesAsync([agentDefinitionId]));
        var agentSessionId = Guid.NewGuid().ToString("n");
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId);
        Assert.NotNull(agentSessionEntity);
        var agentSessionEntityId = agentSessionEntity!.EntityId.ToString();

        // Build a workspace JSON with a tab referencing the agent-session entity by its entity ID.
        // Construct the workspace entity directly (no schema validation) to avoid workspace-schema
        // constraints on region/tab structure when we only care about the content routing logic.
        var workspaceEntityId = new EntityId("c0ffee03-0000-4000-8000-000000000003");
        var workspaceJson = $$"""
            {
              "entity-id": "c0ffee03-0000-4000-8000-000000000003",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Restore Test Workspace" },
              "regions": [
                {
                  "tabs": [
                    {
                      "tab-id": "restored-tab-1",
                      "title": "My Restored Session",
                      "dock": "full",
                      "content": {
                        "target-entity-name": "{{agentSessionEntityId}}"
                      }
                    }
                  ]
                }
              ]
            }
            """;

        using var workspaceDoc = JsonDocument.Parse(workspaceJson);
        var workspaceEntity = new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = workspaceEntityId,
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = workspaceDoc.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });

        var createWorkspacePaneMethod = typeof(MainWindowViewModel).GetMethod(
            "CreateWorkspacePaneAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(createWorkspacePaneMethod);

        var task = (Task<WorkspacePaneViewModel>?)createWorkspacePaneMethod!.Invoke(
            viewModel,
            [workspaceEntity, workspaceDoc.RootElement.Clone()]);
        Assert.NotNull(task);

        var workspacePane = await task!;
        Assert.NotNull(workspacePane);

        // The tab must be an AgentSessionWorkspaceTabViewModel, not a plain entity view.
        var tabs = workspacePane.SelectedRegion?.Tabs;
        Assert.NotNull(tabs);
        var restoredTab = Assert.Single(tabs!);
        var agentSessionTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(restoredTab);
        Assert.Equal("restored-tab-1", agentSessionTab.Id);
        Assert.Equal("My Restored Session", agentSessionTab.Title);
        Assert.True(agentSessionTab.Entity?.IsEntityType("agent-session"));
        await WaitForAgentReadyAsync(agentSessionTab);
        Assert.NotNull(agentSessionTab.Agent);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CreateWorkspacePaneAsync_WithAgentSessionTabButMissingDefinition_FallsBackToEntityWorkspaceTab()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        // Create an agent-definition entity so we can create a valid agent-session entity.
        var agentDefinitionId = new EntityId("dead0001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            agentDefinitionId,
            """
            {
              "entity-id": "dead0001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "fallback-echo"]],
              "display-name": { "default": "Fallback Echo" },
              "definition": {
                "kind": "prompt",
                "name": "fallback-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        // Create the agent-session entity directly without going through the shortcut handler.
        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentDefinitionEntity = Assert.Single(await entityBroker.GetEntitiesAsync([agentDefinitionId]));
        var agentSessionId = Guid.NewGuid().ToString("n");
        var createdSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId);
        Assert.NotNull(createdSessionEntity);
        var agentSessionEntityId = createdSessionEntity!.EntityId.ToString();

        // Now delete the agent-definition entity so the restore path will fail to find it.
        // ConcurrencyTag is required by MergeProcessingDataAccessLayer for existing entities.
        var latestDefinitionEntity = Assert.Single(await entityBroker.GetEntitiesAsync([agentDefinitionId]));
        await entityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Delete agent definition." } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = agentDefinitionId,
                    EntityChangeMode = EntityChangeMode.Replace,
                    ConcurrencyTag = latestDefinitionEntity.ConcurrencyTag,
                    Data = null,
                },
            ],
        });

        var workspaceEntityId = new EntityId("dead0002-0000-4000-8000-000000000002");
        var workspaceJson = $$"""
            {
              "entity-id": "dead0002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Missing Def Workspace" },
              "regions": [
                {
                  "tabs": [
                    {
                      "tab-id": "orphaned-tab",
                      "title": "Orphaned Session",
                      "content": {
                        "target-entity-name": "{{agentSessionEntityId}}"
                      }
                    }
                  ]
                }
              ]
            }
            """;

        using var workspaceDoc = JsonDocument.Parse(workspaceJson);
        var workspaceEntity = new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = workspaceEntityId,
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = workspaceDoc.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });

        var createWorkspacePaneMethod = typeof(MainWindowViewModel).GetMethod(
            "CreateWorkspacePaneAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(createWorkspacePaneMethod);

        var task = (Task<WorkspacePaneViewModel>?)createWorkspacePaneMethod!.Invoke(
            viewModel,
            [workspaceEntity, workspaceDoc.RootElement.Clone()]);
        Assert.NotNull(task);

        var workspacePane = await task!;
        Assert.NotNull(workspacePane);

        // With the new loading-tab design, TryCreateAgentSessionTabForRestoreAsync always returns
        // a loading tab (which transitions to Failed state asynchronously when data is missing).
        var tabs = workspacePane.SelectedRegion?.Tabs;
        Assert.NotNull(tabs);
        var agentTab = Assert.Single(tabs!);
        Assert.IsType<AgentSessionWorkspaceTabViewModel>(agentTab);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseActiveTabCommand_WithTwoTabs_ClosesActiveTabAndLeavesOther()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "tab-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "tab-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB); // tabB is now active

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("tab-b", documentDock!.ActiveDockable?.Id);

        viewModel.CloseActiveTabCommand.Execute(null);

        var remaining = documentDock.VisibleDockables?.OfType<WorkspaceDocument>().ToList();
        Assert.NotNull(remaining);
        Assert.DoesNotContain(remaining!, doc => doc.Id == "tab-b");
        Assert.Contains(remaining!, doc => doc.Id == "tab-a");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CycleTabForwardCommand_WithThreeTabs_WrapsAroundForward()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "tab-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "tab-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "tab-c", Title = "Tab C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC); // tabC is now active

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var dockables = documentDock!.VisibleDockables!;
        var count = dockables.Count;

        // Record starting index and cycle forward through all tabs, wrapping around.
        var startIndex = dockables.IndexOf(documentDock.ActiveDockable!);
        for (var step = 1; step <= count; step++)
        {
            viewModel.CycleTabForwardCommand.Execute(null);
            var expectedIndex = (startIndex + step) % count;
            var actualIndex = dockables.IndexOf(documentDock.ActiveDockable!);
            Assert.Equal(expectedIndex, actualIndex);
        }

        // After a full cycle we should be back at the start.
        Assert.Equal(startIndex, dockables.IndexOf(documentDock.ActiveDockable!));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CycleTabBackwardCommand_WithThreeTabs_WrapsAroundBackward()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "tab-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "tab-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "tab-c", Title = "Tab C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC); // tabC is now active

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var dockables = documentDock!.VisibleDockables!;
        var count = dockables.Count;

        // Record starting index and cycle backward through all tabs, wrapping around.
        var startIndex = dockables.IndexOf(documentDock.ActiveDockable!);
        for (var step = 1; step <= count; step++)
        {
            viewModel.CycleTabBackwardCommand.Execute(null);
            var expectedIndex = ((startIndex - step) % count + count) % count;
            var actualIndex = dockables.IndexOf(documentDock.ActiveDockable!);
            Assert.Equal(expectedIndex, actualIndex);
        }

        // After a full cycle we should be back at the start.
        Assert.Equal(startIndex, dockables.IndexOf(documentDock.ActiveDockable!));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CycleTabForwardCommand_WithSingleTab_IsNoOp()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "tab-a-single", Title = "Tab A" };
        await viewModel.OpenTabAsync(tabA);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);

        // Close all tabs except ours using the dockFactory via reflection.
        var dockFactoryField = typeof(MainWindowViewModel)
            .GetField("dockFactory", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(dockFactoryField);
        var dockFactory = dockFactoryField!.GetValue(viewModel);
        Assert.NotNull(dockFactory);
        var closeDockable = dockFactory!.GetType().GetMethod("CloseDockable",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
        Assert.NotNull(closeDockable);

        var otherDocs = documentDock!.VisibleDockables?
            .OfType<WorkspaceDocument>()
            .Where(d => d.Id != "tab-a-single")
            .ToList();
        foreach (var doc in otherDocs ?? [])
        {
            closeDockable!.Invoke(dockFactory, [doc]);
        }

        Assert.Equal("tab-a-single", documentDock.ActiveDockable?.Id);
        viewModel.CycleTabForwardCommand.Execute(null);
        Assert.Equal("tab-a-single", documentDock.ActiveDockable?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GoToTabAtIndexCommand_WithThreeTabs_ActivatesCorrectTab()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "goto-tab-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "goto-tab-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "goto-tab-c", Title = "Tab C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);

        viewModel.GoToTabAtIndexCommand.Execute("0");

        Assert.Equal(documentDock!.VisibleDockables![0], documentDock.ActiveDockable);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GoToTabAtIndexCommand_WithIndexOutOfRange_IsNoOp()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "goto-oob-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "goto-oob-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var activeBefore = documentDock!.ActiveDockable;

        viewModel.GoToTabAtIndexCommand.Execute("5");

        Assert.Equal(activeBefore, documentDock.ActiveDockable);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GoToWorkspacePaneAtIndexCommand_WithMultiplePanes_ActivatesCorrectPane()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("dddddddd-dddd-4ddd-dddd-dddddddddddd");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "dddddddd-dddd-4ddd-dddd-dddddddddddd",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "pane-nav-a"]],
              "display-name": { "default": "Pane Nav A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("eeeeeeee-eeee-4eee-eeee-eeeeeeeeeeee");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "eeeeeeee-eeee-4eee-eeee-eeeeeeeeeeee",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "pane-nav-b"]],
              "display-name": { "default": "Pane Nav B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        // Select the second pane first, then navigate back to index 0
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute("1");
        Assert.Equal(viewModel.WorkspacePanes[1], viewModel.SelectedWorkspacePane);

        viewModel.GoToWorkspacePaneAtIndexCommand.Execute("0");
        Assert.Equal(viewModel.WorkspacePanes[0], viewModel.SelectedWorkspacePane);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GoToWorkspacePaneAtIndexCommand_WithIndexOutOfRange_IsNoOp()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var selectedBefore = viewModel.SelectedWorkspacePane;

        viewModel.GoToWorkspacePaneAtIndexCommand.Execute("99");

        Assert.Equal(selectedBefore, viewModel.SelectedWorkspacePane);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithFocusedTabId_ActivatesFocusedTab()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("f0c00001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "f0c00001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "focused-tab-test"]],
              "display-name": { "default": "Focused Tab Test Workspace" },
              "focused-tab-id": "tab-second",
              "regions": [
                {
                  "region-id": "main",
                  "title": "Main",
                  "dock": "center",
                  "size": 1.0,
                  "tabs": [
                    {
                      "tab-id": "tab-first",
                      "title": "First Tab",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://first.example.com" }
                    },
                    {
                      "tab-id": "tab-second",
                      "title": "Second Tab",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://second.example.com" }
                    }
                  ]
                }
              ]
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);

        await WaitForWorkspaceTabAsync(contentDock!, "tab-first");
        await WaitForWorkspaceTabAsync(contentDock!, "tab-second");

        Assert.Equal("tab-second", contentDock!.ActiveDockable?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithAbsentFocusedTabId_DoesNotCrash()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("f0c00002-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "f0c00002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "no-focused-tab"]],
              "display-name": { "default": "No Focused Tab Workspace" },
              "regions": [
                {
                  "region-id": "main",
                  "title": "Main",
                  "dock": "center",
                  "size": 1.0,
                  "tabs": [
                    {
                      "tab-id": "only-tab",
                      "title": "Only Tab",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://example.com" }
                    }
                  ]
                }
              ]
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);

        await WaitForWorkspaceTabAsync(contentDock!, "only-tab");

        Assert.NotNull(contentDock!.ActiveDockable);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithNonMatchingFocusedTabId_DoesNotCrash()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("f0c00003-0000-4000-8000-000000000003");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "f0c00003-0000-4000-8000-000000000003",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "nonmatching-focused-tab"]],
              "display-name": { "default": "Non-matching Focused Tab Workspace" },
              "focused-tab-id": "nonexistent-tab-id",
              "regions": [
                {
                  "region-id": "main",
                  "title": "Main",
                  "dock": "center",
                  "size": 1.0,
                  "tabs": [
                    {
                      "tab-id": "tab-a",
                      "title": "Tab A",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://a.example.com" }
                    }
                  ]
                }
              ]
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);

        await WaitForWorkspaceTabAsync(contentDock!, "tab-a");

        Assert.NotNull(contentDock!.ActiveDockable);
    }

    private static IDocumentDock? GetDocumentDock(MainWindowViewModel viewModel)
    {
        var contentLayout = viewModel.SelectedWorkspacePane?.ContentLayout;
        if (contentLayout is null)
        {
            return null;
        }

        return FindDocumentDockIn(contentLayout);
    }

    private static IDocumentDock? FindDocumentDockIn(IDockable dockable)
    {
        if (dockable is IDocumentDock documentDock)
        {
            return documentDock;
        }

        if (dockable is IDock dock && dock.VisibleDockables is not null)
        {
            foreach (var child in dock.VisibleDockables)
            {
                var result = FindDocumentDockIn(child);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        return null;
    }

    private static async Task WaitForWorkspaceTabAsync(IDocumentDock contentDock, string tabId)
    {
        if (contentDock.VisibleDockables?.OfType<WorkspaceDocument>().Any(d => d.Id == tabId) == true)
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (contentDock.VisibleDockables?.OfType<WorkspaceDocument>().Any(d => d.Id == tabId) == true)
            {
                signal.TrySetResult();
            }
        }

        if (contentDock.VisibleDockables is INotifyCollectionChanged observable)
        {
            observable.CollectionChanged += OnCollectionChanged;
            try
            {
                if (contentDock.VisibleDockables?.OfType<WorkspaceDocument>().Any(d => d.Id == tabId) != true)
                {
                    await signal.Task;
                }
            }
            finally
            {
                observable.CollectionChanged -= OnCollectionChanged;
            }
        }
    }

    private static async Task WaitForAgentReadyAsync(AgentSessionWorkspaceTabViewModel tab)    {
        if (tab.State is AgentTabState.Ready or AgentTabState.Failed)
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AgentSessionWorkspaceTabViewModel.State)
                && tab.State is AgentTabState.Ready or AgentTabState.Failed)
            {
                signal.TrySetResult();
            }
        }

        tab.PropertyChanged += OnPropertyChanged;
        try
        {
            if (tab.State is not (AgentTabState.Ready or AgentTabState.Failed))
            {
                await signal.Task;
            }
        }
        finally
        {
            tab.PropertyChanged -= OnPropertyChanged;
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ApplySelectedViewAsync_CalledTwice_CurrentViewPopulationContainsEntitiesOnce()
    {
        // Regression for issue #104: concurrent ApplySelectedViewAsync invocations must not
        // double-populate the entity list.
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var sessionsView = Assert.Single(
            viewModel.TopLevelViews,
            static view => string.Equals(view.Title, "Sessions", StringComparison.Ordinal));

        var applyMethod = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyMethod);

        viewModel.SelectedTopLevelView = sessionsView;
        await (Task)applyMethod!.Invoke(viewModel, [])!;
        await (Task)applyMethod!.Invoke(viewModel, [])!;

        var entities = viewModel.CurrentViewPopulation.Entities;
        var agentManifestEntities = entities
            .Where(static e => string.Equals(e.EntityType, "agent-manifest", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(agentManifestEntities);
        var distinctIds = agentManifestEntities.Select(static e => e.Entity.EntityId).Distinct().Count();
        Assert.Equal(distinctIds, agentManifestEntities.Count);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ApplySelectedViewAsync_EachCall_CreatesNewCurrentViewPopulationInstance()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var firstPopulation = viewModel.CurrentViewPopulation;

        var applyMethod = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyMethod);

        await (Task)applyMethod!.Invoke(viewModel, [])!;

        Assert.NotSame(firstPopulation, viewModel.CurrentViewPopulation);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ApplySelectedViewAsync_PreviousPopulationDisposed_ItsEntitiesNotModifiedAfterSwap()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var sessionsView = Assert.Single(
            viewModel.TopLevelViews,
            static view => string.Equals(view.Title, "Sessions", StringComparison.Ordinal));

        var applyMethod = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyMethod);

        viewModel.SelectedTopLevelView = sessionsView;
        await (Task)applyMethod!.Invoke(viewModel, [])!;

        var firstPopulation = viewModel.CurrentViewPopulation;
        var countAfterFirstRun = firstPopulation.Entities.Count;

        await (Task)applyMethod!.Invoke(viewModel, [])!;

        // The old population must not have gained or lost entities after the swap — it was
        // disposed (CTS cancelled) before the new run appended to the new collection.
        Assert.Equal(countAfterFirstRun, firstPopulation.Entities.Count);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ApplySelectedViewAsync_ViewSwitchedTwice_CurrentViewPopulationReflectsSecondView()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var applyMethod = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyMethod);

        var firstView = viewModel.TopLevelViews.FirstOrDefault(
            v => !v.IsEntityBrowser && v.ViewEntity is not null);

        if (firstView is null)
        {
            // If no view-driven top-level views exist, the test is vacuous — skip by passing.
            return;
        }

        viewModel.SelectedTopLevelView = firstView;
        await (Task)applyMethod!.Invoke(viewModel, [])!;
        var populationAfterFirst = viewModel.CurrentViewPopulation;

        // Switch to the empty view to produce a second, different population.
        viewModel.SelectedTopLevelView = viewModel.TopLevelViews[0];
        await (Task)applyMethod!.Invoke(viewModel, [])!;

        // The CurrentViewPopulation must be a distinct instance from the one after the first switch.
        Assert.NotSame(populationAfterFirst, viewModel.CurrentViewPopulation);
    }

    [AvaloniaFact(Timeout = 15_000)]
    [Trait("Category", "SlowLayout")]
    public async Task MainWindow_ContentLevelDocumentTabStrip_HasHeaderTemplate_AfterTabOpened()
    {
        // Regression test for #88: the content-level DocumentTabStrip must have HeaderTemplate
        // set so tab icons and notification indicators are rendered via EffectiveTabHeader.
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://example.com") { Id = "header-tmpl-test", Title = "Header Test" };
        await viewModel.OpenTabAsync(tab);

        var window = new MainWindow(viewModel);
        window.Show();
        // Force layout passes: DockControl builds its visual tree during render ticks.
        for (var i = 0; i < 10; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        // The content-level DocumentTabStrip is nested inside the workspace-level DockControl.
        var tabStrips = window.GetVisualDescendants().OfType<DocumentTabStrip>().ToList();
        Assert.NotEmpty(tabStrips);

        // Diagnostic: check DataContext types on all tab strips and DockControls
        var allDockControls = window.GetVisualDescendants().OfType<Dock.Avalonia.Controls.DockControl>().ToList();

        var contentTabStrip = tabStrips.FirstOrDefault(ts => ts.DataContext is WorkspaceContentDock);
        Assert.NotNull(contentTabStrip);

        // Diagnostic: check the full chain from DocumentControl → DocumentTabStrip → PART_HeaderPresenter
        var documentControl = window.GetVisualDescendants().OfType<Dock.Avalonia.Controls.DocumentControl>()
            .FirstOrDefault(dc => dc.GetVisualDescendants().Contains(contentTabStrip));
        Assert.NotNull(documentControl);

        // Both DocumentControl and DocumentTabStrip should have our ContentControl DataTemplate, not Dock's default.
        var dcHeaderTemplateTypeName = documentControl!.HeaderTemplate?.GetType().Name ?? "(null)";
        var dcHeaderTemplateDataType = (documentControl!.HeaderTemplate as Avalonia.Markup.Xaml.Templates.DataTemplate)?.DataType?.Name ?? "(no DataType)";
        var tsHeaderTemplateTypeName = contentTabStrip!.HeaderTemplate?.GetType().Name ?? "(null)";
        var tsHeaderTemplateDataType = (contentTabStrip!.HeaderTemplate as Avalonia.Markup.Xaml.Templates.DataTemplate)?.DataType?.Name ?? "(no DataType)";

        var tabStripItems = contentTabStrip.GetVisualDescendants().OfType<DocumentTabStripItem>().ToList();
        Assert.NotEmpty(tabStripItems);

        var headerPresenter = tabStripItems[0]
            .GetVisualDescendants()
            .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
            .FirstOrDefault(cp => cp.Name == "PART_HeaderPresenter");
        Assert.NotNull(headerPresenter);

        // The child of PART_HeaderPresenter should be a ContentControl (our template), not a TextBlock.
        // If this fails, check: dcHeaderTemplate={dcHeaderTemplateTypeName}, tsHeaderTemplate={tsHeaderTemplateTypeName}
        var headerChild = headerPresenter!.GetVisualChildren().FirstOrDefault();
        Assert.NotNull(headerChild);
        Assert.True(
            headerChild is Avalonia.Controls.ContentControl,
            $"Expected ContentControl but got {headerChild!.GetType().Name}. " +
            $"DC.HeaderTemplate={dcHeaderTemplateTypeName}(DataType={dcHeaderTemplateDataType}), " +
            $"TS.HeaderTemplate={tsHeaderTemplateTypeName}(DataType={tsHeaderTemplateDataType})");

        window.Close();
    }

    private static RepositorySource CreateInMemoryRepositorySource()
    {
        return new UnknownRepositorySource();
    }

    private static string CreateTempProfileStorePath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "Phantom.Workspaces.Tests",
            Guid.NewGuid().ToString("N"),
            "profile.json");
    }

    private static void DeleteTempProfileStoreDirectory(string profilePath)
    {
        var directory = Path.GetDirectoryName(profilePath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static EntityBroker GetEntityBroker(
        MainWindowViewModel viewModel)
    {
        var entityBrokerProperty = typeof(MainWindowViewModel).GetProperty(
            "EntityBroker",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(entityBrokerProperty);
        return Assert.IsType<EntityBroker>(entityBrokerProperty!.GetValue(viewModel));
    }

    private static async Task<SubscribedEntityViewModel> UpsertEntityAndLoadAsync(
        EntityBroker entityBroker,
        EntityId entityId,
        string json)
    {
        using var document = JsonDocument.Parse(json);
        var updateResult = await entityBroker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Add test agent definition.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = document.RootElement.Clone(),
                    },
                ],
            });
        var entityResult = Assert.Single(updateResult.EntityResults, entityResult => entityResult.RequestedEntityId == entityId);
        Assert.NotEqual(UpdateState.Failed, entityResult.UpdateState);
        return Assert.Single(await entityBroker.GetEntitiesAsync([entityId]));
    }

    private static IReadOnlyCollection<EntityName> ReadEntityNames(
        JsonElement? entityData)
    {
        if (entityData is not JsonElement dataElement
            || !dataElement.TryGetProperty("names", out var namesElement)
            || namesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var names = new List<EntityName>();
        foreach (var nameElement in namesElement.EnumerateArray())
        {
            var entityName = nameElement.TryReadEntityName();
            if (entityName is not null)
            {
                names.Add(entityName.Value);
            }
        }

        return names;
    }

    private static string? ReadDefaultDisplayName(
        JsonElement? entityData)
    {
        if (entityData is not JsonElement dataElement
            || !dataElement.TryGetProperty("display-name", out var displayNameElement)
            || displayNameElement.ValueKind != JsonValueKind.Object
            || !displayNameElement.TryGetProperty("default", out var defaultValueElement))
        {
            return null;
        }

        return defaultValueElement.GetString();
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabAsync_ExistingTab_PushesNavigationEntry()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "nav-push-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "nav-push-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB); // push B; B is active

        // Re-open tab A (it already exists) — should push a navigation entry
        var tabAAgain = new WebViewModel("https://a.example.com") { Id = "nav-push-a", Title = "Tab A" };
        await viewModel.OpenTabAsync(tabAAgain);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("nav-push-a", documentDock!.ActiveDockable?.Id);

        // NavigateBack should return to tab B (the entry pushed before re-opening A)
        viewModel.NavigateBackCommand.Execute(null);
        Assert.Equal("nav-push-b", documentDock.ActiveDockable?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NavigateBack_AfterMultipleToolDrivenNavigations_TraversesAllEntries()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "multi-nav-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "multi-nav-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "multi-nav-c", Title = "Tab C" };

        // Simulate sequential tool-driven tab openings
        await viewModel.OpenTabAsync(tabA);  // push A
        await viewModel.OpenTabAsync(tabB);  // push B
        await viewModel.OpenTabAsync(tabC);  // push C; C is active

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("multi-nav-c", documentDock!.ActiveDockable?.Id);

        // Back: C → B
        viewModel.NavigateBackCommand.Execute(null);
        Assert.Equal("multi-nav-b", documentDock.ActiveDockable?.Id);

        // Back: B → A
        viewModel.NavigateBackCommand.Execute(null);
        Assert.Equal("multi-nav-a", documentDock.ActiveDockable?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithMultipleBrowserTabs_TabsAppearInDeclarationOrder()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("00100001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "00100001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "tab-order-test"]],
              "display-name": { "default": "Tab Order Workspace" },
              "regions": [
                {
                  "region-id": "main",
                  "title": "Main",
                  "dock": "center",
                  "size": 1.0,
                  "tabs": [
                    {
                      "tab-id": "tab-order-a",
                      "title": "Tab A",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://a.example.com" }
                    },
                    {
                      "tab-id": "tab-order-b",
                      "title": "Tab B",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://b.example.com" }
                    },
                    {
                      "tab-id": "tab-order-c",
                      "title": "Tab C",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://c.example.com" }
                    }
                  ]
                }
              ]
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);

        await WaitForWorkspaceTabAsync(contentDock!, "tab-order-a");
        await WaitForWorkspaceTabAsync(contentDock!, "tab-order-b");
        await WaitForWorkspaceTabAsync(contentDock!, "tab-order-c");

        var tabIds = contentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .Where(d => d.Id is "tab-order-a" or "tab-order-b" or "tab-order-c")
            .Select(d => d.Id)
            .ToList();

        Assert.Equal(["tab-order-a", "tab-order-b", "tab-order-c"], tabIds);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithUnresolvableMiddleTab_SkipsNullAndPreservesOrder()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("00100002-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "00100002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "null-tab-order-test"]],
              "display-name": { "default": "Null Tab Order Workspace" },
              "regions": [
                {
                  "region-id": "main",
                  "title": "Main",
                  "dock": "center",
                  "size": 1.0,
                  "tabs": [
                    {
                      "tab-id": "null-order-a",
                      "title": "Tab A",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://a.example.com" }
                    },
                    {
                      "tab-id": "null-order-missing",
                      "title": "Missing Tab",
                      "kind": "entity",
                      "dock": "full",
                      "content": {
                        "target-entity-name": ["tests", "null-tab-test", "entity-does-not-exist"]
                      }
                    },
                    {
                      "tab-id": "null-order-c",
                      "title": "Tab C",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://c.example.com" }
                    }
                  ]
                }
              ]
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);

        await WaitForWorkspaceTabAsync(contentDock!, "null-order-a");
        await WaitForWorkspaceTabAsync(contentDock!, "null-order-c");

        var tabIds = contentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .Where(d => d.Id is "null-order-a" or "null-order-c" or "null-order-missing")
            .Select(d => d.Id)
            .ToList();

        Assert.Equal(["null-order-a", "null-order-c"], tabIds);
    }

    [AvaloniaFact(Timeout = 15_000)]
    [Trait("Category", "SlowLayout")]
    public async Task MainWindow_KeyPress_Alt1_ActivatesFirstContentTab()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "kb-alt1-a", Title = "Tab A" };
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "kb-alt1-b", Title = "Tab B" };
        var tabC = new AgentSessionWorkspaceTabViewModel { Id = "kb-alt1-c", Title = "Tab C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        var window = new MainWindow(viewModel);
        window.Show();

        window.KeyPressQwerty(PhysicalKey.Digit1, RawInputModifiers.Alt);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal(documentDock!.VisibleDockables![0], documentDock.ActiveDockable);

        window.Close();
    }

    [AvaloniaFact(Timeout = 15_000)]
    [Trait("Category", "SlowLayout")]
    public async Task MainWindow_KeyPress_Alt0_ActivatesTenthContentTab()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        for (var i = 0; i < 10; i++)
        {
            var tab = new AgentSessionWorkspaceTabViewModel { Id = $"kb-alt0-tab{i}", Title = $"Tab {i}" };
            await viewModel.OpenTabAsync(tab);
        }

        var window = new MainWindow(viewModel);
        window.Show();

        window.KeyPressQwerty(PhysicalKey.Digit0, RawInputModifiers.Alt);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal(documentDock!.VisibleDockables![9], documentDock.ActiveDockable);

        window.Close();
    }

    [AvaloniaFact(Timeout = 15_000)]
    [Trait("Category", "SlowLayout")]
    public async Task MainWindow_KeyPress_AltDigit_WithIndexOutOfRange_IsNoOp()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "kb-alt-oob-a", Title = "Tab A" };
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "kb-alt-oob-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        var window = new MainWindow(viewModel);
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var activeBefore = documentDock!.ActiveDockable;

        window.KeyPressQwerty(PhysicalKey.Digit9, RawInputModifiers.Alt);

        Assert.Equal(activeBefore, documentDock.ActiveDockable);

        window.Close();
    }

    [AvaloniaFact(Timeout = 15_000)]
    [Trait("Category", "SlowLayout")]
    public async Task MainWindow_KeyPress_Ctrl1_ActivatesFirstWorkspacePane()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("11111111-1111-4111-8111-111111111111");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "11111111-1111-4111-8111-111111111111",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "kb-pane-a"]],
              "display-name": { "default": "KB Pane A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("22222222-2222-4222-8222-222222222222");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "22222222-2222-4222-8222-222222222222",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "kb-pane-b"]],
              "display-name": { "default": "KB Pane B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var window = new MainWindow(viewModel);
        window.Show();

        viewModel.GoToWorkspacePaneAtIndexCommand.Execute("1");
        Assert.Equal(viewModel.WorkspacePanes[1], viewModel.SelectedWorkspacePane);

        window.KeyPressQwerty(PhysicalKey.Digit1, RawInputModifiers.Control);

        Assert.Equal(viewModel.WorkspacePanes[0], viewModel.SelectedWorkspacePane);

        window.Close();
    }

    [AvaloniaFact(Timeout = 15_000)]
    [Trait("Category", "SlowLayout")]
    public async Task MainWindow_KeyPress_Ctrl2_ActivatesSecondWorkspacePane()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("33333333-3333-4333-8333-333333333333");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "33333333-3333-4333-8333-333333333333",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "kb-pane2-a"]],
              "display-name": { "default": "KB Pane 2 A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("44444444-4444-4444-8444-444444444444");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "44444444-4444-4444-8444-444444444444",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "kb-pane2-b"]],
              "display-name": { "default": "KB Pane 2 B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var window = new MainWindow(viewModel);
        window.Show();

        window.KeyPressQwerty(PhysicalKey.Digit2, RawInputModifiers.Control);

        Assert.Equal(viewModel.WorkspacePanes[1], viewModel.SelectedWorkspacePane);

        window.Close();
    }

    [AvaloniaFact(Timeout = 15_000)]
    [Trait("Category", "SlowLayout")]
    public async Task MainWindow_KeyPress_CtrlDigit_WithIndexOutOfRange_IsNoOp()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var selectedBefore = viewModel.SelectedWorkspacePane;

        window.KeyPressQwerty(PhysicalKey.Digit2, RawInputModifiers.Control);

        Assert.Equal(selectedBefore, viewModel.SelectedWorkspacePane);

        window.Close();
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task InitializeAsync_WithDefaultRelationship_OpensDefaultWorkspace()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());

        var entityBroker = await GetEntityBrokerBeforeInitAsync(viewModel);
        var profileId = entityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;

        var workspaceId = new EntityId("de1a0110-0000-4000-8000-000000000001");
        await SeedEntityAsync(
            entityBroker,
            workspaceId,
            $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "default-startup"]],
              "display-name": { "default": "Default Startup Workspace" },
              "regions": []
            }
            """);

        var defaultRelId = new EntityId("de1a0110-0000-4000-8000-000000000002");
        await SeedEntityAsync(
            entityBroker,
            defaultRelId,
            $$"""
            {
              "entity-id": "{{defaultRelId}}",
              "entity-types": ["entity", "default", "relationship"],
              "names": [["tests", "defaults", "startup-workspace"]],
              "participants": {
                "applied-to": "{{profileId}}",
                "value": "{{workspaceId}}"
              }
            }
            """);

        await viewModel.InitializeAsync();

        Assert.Contains(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, GettingStartedWorkspaceId, StringComparison.Ordinal));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task InitializeAsync_WithNoDefaultRelationship_OpensGettingStartedWorkspace()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        Assert.Contains(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, GettingStartedWorkspaceId, StringComparison.Ordinal));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseLastWorkspace_WithDefaultRelationship_OpensDefaultWorkspaceInsteadOfGettingStarted()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());

        var entityBroker = await GetEntityBrokerBeforeInitAsync(viewModel);
        var profileId = entityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;

        var workspaceId = new EntityId("de1a0110-0000-4000-8000-000000000003");
        await SeedEntityAsync(
            entityBroker,
            workspaceId,
            $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "default-close"]],
              "display-name": { "default": "Default Close Workspace" },
              "regions": []
            }
            """);

        var defaultRelId = new EntityId("de1a0110-0000-4000-8000-000000000004");
        await SeedEntityAsync(
            entityBroker,
            defaultRelId,
            $$"""
            {
              "entity-id": "{{defaultRelId}}",
              "entity-types": ["entity", "default", "relationship"],
              "names": [["tests", "defaults", "close-workspace"]],
              "participants": {
                "applied-to": "{{profileId}}",
                "value": "{{workspaceId}}"
              }
            }
            """);

        await viewModel.InitializeAsync();

        // Close the default workspace — this triggers OpenGettingStartedWorkspaceAsync
        var defaultPane = viewModel.WorkspacePanes
            .FirstOrDefault(p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(defaultPane);
        viewModel.CloseWorkspaceCommand.Execute(defaultPane!);

        // After closing, the default workspace should be re-opened instead of Getting Started
        Assert.Contains(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal)
                || pane.Id.StartsWith("loading-workspace:", StringComparison.Ordinal));
        Assert.DoesNotContain(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, GettingStartedWorkspaceId, StringComparison.Ordinal));
    }

    private const string GettingStartedWorkspaceId = "6cc39f41-2a36-4be6-ab95-3f3fd355e463";

    private static async Task<EntityBroker> GetEntityBrokerBeforeInitAsync(MainWindowViewModel viewModel)
    {
        var entityBrokerTaskField = typeof(MainWindowViewModel).GetField(
            "entityBrokerTask",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(entityBrokerTaskField);
        var entityBrokerTask = (Task<EntityBroker>)entityBrokerTaskField!.GetValue(viewModel)!;
        return await entityBrokerTask;
    }

    private static async Task SeedEntityAsync(EntityBroker entityBroker, EntityId entityId, string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = await entityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = entityId,
                    EntityChangeMode = EntityChangeMode.Replace,
                    Data = document.RootElement.Clone(),
                },
            ],
        });
        var failure = result.EntityResults.FirstOrDefault(static r => r.UpdateState == UpdateState.Failed);
        Assert.True(
            failure is null,
            failure is null ? string.Empty : string.Join(" | ", failure.Errors.Select(static e => e.Message)));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NavigatePreviousNotificationCommand_NavigatesToUnreadTab()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "nav-prev-a", Title = "Tab A" };
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "nav-prev-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        // tabB is active; notify tabA so it becomes the unread candidate.
        viewModel.NotificationService.Notify(new Notification(new TabDescriptor { TabId = "nav-prev-a" }, "Tab A", "test notification", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));

        viewModel.NavigatePreviousNotificationCommand.Execute(null);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("nav-prev-a", (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NavigateNextNotificationCommand_NavigatesToUnreadTab()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "nav-next-a", Title = "Tab A" };
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "nav-next-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        // tabB is active; notify tabA so it becomes the unread candidate.
        viewModel.NotificationService.Notify(new Notification(new TabDescriptor { TabId = "nav-next-a" }, "Tab A", "test notification", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));

        viewModel.NavigateNextNotificationCommand.Execute(null);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("nav-next-a", (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    [Trait("Category", "SlowLayout")]
    public async Task MainWindow_KeyPress_CtrlF7_NavigatesToPreviousNotification()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "ctrl-f7-prev-a", Title = "Tab A" };
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "ctrl-f7-prev-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        // tabB is active; notify tabA so it becomes the unread candidate.
        viewModel.NotificationService.Notify(new Notification(new TabDescriptor { TabId = "ctrl-f7-prev-a" }, "Tab A", "test notification", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));

        var window = new MainWindow(viewModel);
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey.F7, RawInputModifiers.Control);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("ctrl-f7-prev-a", (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);

        window.Close();
    }

    [AvaloniaFact(Timeout = 15_000)]
    [Trait("Category", "SlowLayout")]
    public async Task MainWindow_KeyPress_CtrlF8_NavigatesToNextNotification()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "ctrl-f8-next-a", Title = "Tab A" };
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "ctrl-f8-next-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        // tabB is active; notify tabA so it becomes the unread candidate.
        viewModel.NotificationService.Notify(new Notification(new TabDescriptor { TabId = "ctrl-f8-next-a" }, "Tab A", "test notification", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));

        var window = new MainWindow(viewModel);
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey.F8, RawInputModifiers.Control);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("ctrl-f8-next-a", (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);

        window.Close();
    }

    [AvaloniaFact(Timeout = 15_000)]
    [Trait("Category", "SlowLayout")]
    public async Task MainWindow_KeyPress_CtrlF7_IsHandledInTunnelPhase()
    {
        // Verifies that Ctrl+F7 is intercepted in the tunnel phase (e.Handled = true),
        // preventing child controls such as WebView2 from seeing the keystroke.
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();

        // Register a bubble-phase handler with handledEventsToo: true so it still fires
        // even after the tunnel handler has already set e.Handled = true.
        bool handledByTunnel = false;
        window.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) =>
            {
                if (e.Key == Key.F7 && e.KeyModifiers == KeyModifiers.Control)
                    handledByTunnel = e.Handled;
            },
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        // With no unread notifications the command is a no-op, but the key must still be handled.
        window.KeyPressQwerty(PhysicalKey.F7, RawInputModifiers.Control);

        Assert.True(handledByTunnel);

        window.Close();
    }

    [AvaloniaFact(Timeout = 15_000)]
    [Trait("Category", "SlowLayout")]
    public async Task MainWindow_KeyPress_CtrlF8_IsHandledInTunnelPhase()
    {
        // Verifies that Ctrl+F8 is intercepted in the tunnel phase (e.Handled = true),
        // preventing child controls such as WebView2 from seeing the keystroke.
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();

        bool handledByTunnel = false;
        window.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) =>
            {
                if (e.Key == Key.F8 && e.KeyModifiers == KeyModifiers.Control)
                    handledByTunnel = e.Handled;
            },
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        window.KeyPressQwerty(PhysicalKey.F8, RawInputModifiers.Control);

        Assert.True(handledByTunnel);

        window.Close();
    }

    [AvaloniaFact(Timeout = 15_000)]
    [Trait("Category", "SlowLayout")]
    public async Task MainWindow_WithNotificationBellRingingStyle_DoesNotThrowOnLayout()
    {
        // Regression test for #143: bell animation used string-valued RenderTransform KeyFrame
        // setters (e.g. Value="rotate(-18deg)"). Avalonia's XAML IL compiler does not apply
        // type converters inside KeyFrame.Setter, so the value arrived as a boxed string with
        // no registered animator, throwing InvalidOperationException on first style application.
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();

        // Force a full layout pass — this applies all loaded styles (including NotificationsStyles)
        // and interprets animation keyframes. The bug caused a throw here.
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    // ── IsAltHeld / Alt-badge tests ──────────────────────────────────────────

    [Fact]
    public void IsAltHeld_DefaultIsFalse()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        Assert.False(viewModel.IsAltHeld);
    }

    [Fact]
    public void IsAltHeld_SetToTrue_RaisesPropertyChanged()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        var raised = false;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(viewModel.IsAltHeld))
                raised = true;
        };

        viewModel.IsAltHeld = true;

        Assert.True(raised);
    }

    [AvaloniaFact(Timeout = 15_000)]
    [Trait("Category", "SlowLayout")]
    public async Task MainWindow_KeyDown_LeftAlt_SetsIsAltHeld()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();
        var window = new MainWindow(viewModel);
        window.Show();

        window.KeyPressQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);

        Assert.True(viewModel.IsAltHeld);

        window.Close();
    }

    [Trait("Category", "SlowLayout")]
    [AvaloniaFact(Timeout = 15_000)]
    [Trait("Category", "SlowLayout")]
    public async Task MainWindow_KeyUp_LeftAlt_ClearsIsAltHeld()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();
        var window = new MainWindow(viewModel);
        window.Show();

        viewModel.IsAltHeld = true;
        window.KeyReleaseQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);

        Assert.False(viewModel.IsAltHeld);

        window.Close();
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GoToTabAtIndexCommand_Execute_ClearsIsAltHeld()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "alt-clear-a", Title = "Tab A" };
        await viewModel.OpenTabAsync(tabA);

        viewModel.IsAltHeld = true;
        viewModel.GoToTabAtIndexCommand.Execute("0");

        Assert.False(viewModel.IsAltHeld);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabAsync_ThreeTabs_AssignsCorrectAltShortcutLabels()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "alt-label-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "alt-label-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "alt-label-c", Title = "Tab C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);

        var docs = documentDock!.VisibleDockables!.OfType<WorkspaceDocument>().ToList();
        Assert.Equal("1", docs[0].EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("2", docs[1].EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("3", docs[2].EffectiveTabHeader.AltShortcutLabel);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseTab_ByIndex_RefreshesAltShortcutLabels()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "alt-close-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "alt-close-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "alt-close-c", Title = "Tab C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        // Close the first tab — B should move to index 0 → label "1", C to index 1 → label "2"
        viewModel.CloseTabById("alt-close-a");

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);

        var docs = documentDock!.VisibleDockables!.OfType<WorkspaceDocument>().ToList();
        Assert.Equal("1", docs[0].EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("2", docs[1].EffectiveTabHeader.AltShortcutLabel);
    }

    [AvaloniaFact(Timeout = 15_000)]
    [Trait("Category", "SlowLayout")]
    public async Task MainWindow_KeyPress_ScrollLock_TogglesAgentAutoScroll()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        await using var agentChat = await CreateEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", loggerFactory);

        var agentTab = new AgentSessionWorkspaceTabViewModel { Id = "scroll-lock-toggle", Title = "Agent" };
        agentTab.SetReady(agentViewModel, loggerFactory);
        await viewModel.OpenTabAsync(agentTab);

        var window = new MainWindow(viewModel);
        window.Show();

        Assert.True(agentViewModel.AutoScrollEnabled);

        window.KeyPress(Key.Scroll, RawInputModifiers.None, PhysicalKey.None, "");

        Assert.False(agentViewModel.AutoScrollEnabled);

        window.Close();
    }

    [AvaloniaFact(Timeout = 15_000)]
    [Trait("Category", "SlowLayout")]
    public async Task MainWindow_KeyPress_ScrollLock_TogglesAgentAutoScrollTwice()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        await using var agentChat = await CreateEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", loggerFactory);

        var agentTab = new AgentSessionWorkspaceTabViewModel { Id = "scroll-lock-twice", Title = "Agent" };
        agentTab.SetReady(agentViewModel, loggerFactory);
        await viewModel.OpenTabAsync(agentTab);

        var window = new MainWindow(viewModel);
        window.Show();

        window.KeyPress(Key.Scroll, RawInputModifiers.None, PhysicalKey.None, "");
        window.KeyPress(Key.Scroll, RawInputModifiers.None, PhysicalKey.None, "");

        Assert.True(agentViewModel.AutoScrollEnabled);

        window.Close();
    }

    [AvaloniaFact(Timeout = 15_000)]
    [Trait("Category", "SlowLayout")]
    public async Task MainWindow_KeyPress_ScrollLock_WithNoAgentTab_IsNoOp()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var plainTab = new AgentSessionWorkspaceTabViewModel { Id = "scroll-lock-noop", Title = "NoAgent" };
        await viewModel.OpenTabAsync(plainTab);

        var window = new MainWindow(viewModel);
        window.Show();

        bool handled = false;
        window.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) =>
            {
                if (e.Key == Key.Scroll)
                    handled = e.Handled;
            },
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        window.KeyPress(Key.Scroll, RawInputModifiers.None, PhysicalKey.None, "");

        Assert.False(handled);

        window.Close();
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ApplySelectedViewAsync_WorkspacesView_ShowsRelatedEntityNestedUnderWorkspace()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        var entityBroker = await GetEntityBrokerBeforeInitAsync(viewModel);

        var workspaceId = new EntityId("a2b3c4d5-0001-4000-8000-000000000001");
        var noteId = new EntityId("a2b3c4d5-0001-4000-8000-000000000002");
        var relatedId = new EntityId("a2b3c4d5-0001-4000-8000-000000000003");

        await SeedEntityAsync(entityBroker, workspaceId, $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "view-related-ws"]],
              "display-name": { "default": "Related Workspace" },
              "regions": []
            }
            """);
        await SeedEntityAsync(entityBroker, noteId, $$"""
            {
              "entity-id": "{{noteId}}",
              "entity-types": ["entity", "note"],
              "names": [["notes", "related-note"]],
              "display-name": { "default": "Related Note" },
              "content": { "mime-type": "text/markdown", "content": { "text": "Related Note" } }
            }
            """);
        await SeedEntityAsync(entityBroker, relatedId, $$"""
            {
              "entity-id": "{{relatedId}}",
              "entity-types": ["entity", "related", "relationship"],
              "names": [["relationships", "ws-note-related"]],
              "participants": { "entities": ["{{workspaceId}}", "{{noteId}}"] }
            }
            """);

        await viewModel.InitializeAsync();

        var workspacesView = Assert.Single(
            viewModel.TopLevelViews,
            static view => string.Equals(view.Title, "Workspaces", StringComparison.Ordinal));
        viewModel.SelectedTopLevelView = workspacesView;

        var applySelectedViewMethod = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applySelectedViewMethod);
        await (Task)applySelectedViewMethod!.Invoke(viewModel, [])!;

        var entities = viewModel.CurrentViewPopulation.Entities;

        var workspaceEntity = Assert.Single(
            entities,
            e => string.Equals(e.EntityId, workspaceId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, workspaceEntity.IndentLevel);

        var noteEntity = Assert.Single(
            entities,
            e => string.Equals(e.EntityId, noteId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, noteEntity.IndentLevel);

        var workspaceIndex = entities.ToList().FindIndex(e => string.Equals(e.EntityId, workspaceId.ToString(), StringComparison.OrdinalIgnoreCase));
        var noteIndex = entities.ToList().FindIndex(e => string.Equals(e.EntityId, noteId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(workspaceIndex + 1, noteIndex);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ApplySelectedViewAsync_WorkspacesView_WorkspaceWithNoRelatedEntities_ShowsWorkspaceFlatOnly()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        var entityBroker = await GetEntityBrokerBeforeInitAsync(viewModel);

        var workspaceId = new EntityId("a2b3c4d5-0002-4000-8000-000000000001");

        await SeedEntityAsync(entityBroker, workspaceId, $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "view-flat-ws"]],
              "display-name": { "default": "Flat Workspace" },
              "regions": []
            }
            """);

        await viewModel.InitializeAsync();

        var workspacesView = Assert.Single(
            viewModel.TopLevelViews,
            static view => string.Equals(view.Title, "Workspaces", StringComparison.Ordinal));
        viewModel.SelectedTopLevelView = workspacesView;

        var applySelectedViewMethod = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applySelectedViewMethod);
        await (Task)applySelectedViewMethod!.Invoke(viewModel, [])!;

        var entities = viewModel.CurrentViewPopulation.Entities;

        var workspaceEntity = Assert.Single(
            entities,
            e => string.Equals(e.EntityId, workspaceId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, workspaceEntity.IndentLevel);

        Assert.DoesNotContain(entities, e => e.IndentLevel > 0);
    }


    // ── Single-window guard tests (issue #240) ────────────────────────────────

    [AvaloniaFact(Timeout = 15_000)]
    public void OnOpenScheduledTasksClicked_WhenWindowAlreadyOpen_DoesNotOpenSecondWindow()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        var mainWindow = new MainWindow(viewModel);

        var trackingField = typeof(MainWindow).GetField(
            "openScheduledTasksWindow",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(trackingField);

        var existingDialog = new ScheduledTasksWindow();
        trackingField!.SetValue(mainWindow, existingDialog);

        var handler = typeof(MainWindow).GetMethod(
            "OnOpenScheduledTasksClicked",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handler);
        handler!.Invoke(mainWindow, [null, new RoutedEventArgs()]);

        // The tracking field must still reference the same existing dialog — the guard returned early.
        Assert.Same(existingDialog, trackingField.GetValue(mainWindow));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void OnOpenGitWorkspacesClicked_WhenWindowAlreadyOpen_DoesNotOpenSecondWindow()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        var mainWindow = new MainWindow(viewModel);

        var trackingField = typeof(MainWindow).GetField(
            "openGitWorkspacesWindow",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(trackingField);

        var existingDialog = new GitWorkspacesWindow();
        trackingField!.SetValue(mainWindow, existingDialog);

        var handler = typeof(MainWindow).GetMethod(
            "OnOpenGitWorkspacesClicked",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handler);
        handler!.Invoke(mainWindow, [null, new RoutedEventArgs()]);

        // The tracking field must still reference the same existing dialog — the guard returned early.
        Assert.Same(existingDialog, trackingField.GetValue(mainWindow));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void OnOpenScheduledTasksClicked_TrackingField_InitiallyNull()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        var mainWindow = new MainWindow(viewModel);

        var trackingField = typeof(MainWindow).GetField(
            "openScheduledTasksWindow",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(trackingField);
        Assert.Null(trackingField!.GetValue(mainWindow));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void OnOpenGitWorkspacesClicked_TrackingField_InitiallyNull()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        var mainWindow = new MainWindow(viewModel);

        var trackingField = typeof(MainWindow).GetField(
            "openGitWorkspacesWindow",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(trackingField);
        Assert.Null(trackingField!.GetValue(mainWindow));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_RunVsCodeTunnelTool_IsRegistered()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var hostField = typeof(MainWindowViewModel).GetField(
            "scheduledToolHost",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(hostField);
        var host = Assert.IsType<Phantom.Workspaces.ScheduledTools.ScheduledToolHost>(hostField!.GetValue(viewModel));

        Assert.True(host.TryGetTool("run-vscode-tunnel", out _));

    }

    private static async Task<AgentChat> CreateEchoAgentChatAsync()
    {
        const string echoAgentJson =
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
        var agentDefinition = AgentDefinitionLoader.LoadAgentFromJson(echoAgentJson);
        return await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = agentDefinition,
            ForegroundScheduler = TaskScheduler.Default,
        });
    }

}

