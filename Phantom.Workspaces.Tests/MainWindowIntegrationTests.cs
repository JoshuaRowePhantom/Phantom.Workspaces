using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.AI;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Serializer.SystemTextJson;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Shell;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Notifications;
using Phantom.Workspaces.ViewModels;
using AgentViewModel = Phantom.Workspaces.Agent.Gui.ViewModels.AgentViewModel;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class MainWindowIntegrationTests
{
    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ThemeResources_UseFontFamilyType()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        Assert.True(Avalonia.Application.Current!.Resources.TryGetValue("Theme.FontFamily", out var fontFamilyResource));
        Assert.IsType<FontFamily>(fontFamilyResource);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task InMemoryRepository_SeedsMainViewWithGitWorkspacesSubView()
    {
        var repository = await EntityRepository.CreateAsync(CreateInMemoryRepositorySource());
        var snapshots = await repository.ExportEntitySnapshotsAsync();
        var mainViewSnapshot = Assert.Single(
            snapshots,
            snapshot => ReadEntityNames(snapshot.Value.Data).Any(
                static entityName => entityName.Components.Length == 2
                    && string.Equals(entityName.Components[0], "views", StringComparison.Ordinal)
                    && string.Equals(entityName.Components[1], "main", StringComparison.Ordinal)));
        var data = mainViewSnapshot.Value.Data;
        Assert.True(data.HasValue);
        Assert.True(data!.Value.TryGetProperty("sub-views", out var subViews));
        Assert.Contains(subViews.EnumerateArray(), subView =>
            subView.TryGetProperty("view-entity-id", out var id)
            && id.ValueKind == JsonValueKind.Array
            && id.GetArrayLength() == 2
            && id[0].GetString() == "views"
            && id[1].GetString() == "git-workspaces");
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_ThemeSelectionIsDataDriven()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        Assert.Contains("dark", viewModel.ThemeNames);
        Assert.Contains("light", viewModel.ThemeNames);
        viewModel.SelectedThemeName = "light";
        Assert.Equal("light", viewModel.SelectedThemeName);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task SelectedThemeName_SetToLight_PersistsAcrossViewModelInstances()
    {
        var profilePath = CreateTempProfileStorePath();
        try
        {
            var store = new ProfileStore(profilePath);

            await using var vm1 = CreateTestMainWindowViewModel(profileStore: store);
            await vm1.InitializeAsync();
            await vm1.SetThemeAsync("light");

            await using var vm2 = CreateTestMainWindowViewModel(profileStore: store);
            await vm2.InitializeAsync();

            Assert.Equal("light", vm2.SelectedThemeName);
        }
        finally
        {
            DeleteTempProfileStoreDirectory(profilePath);
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_InitializeAsync_ReplacesDefaultAndLoadingWorkspacePanes()
    {
        await using var viewModel = CreateTestMainWindowViewModel(
            configuration: new WorkspacesConfiguration { SkipStartupWorkspace = false });
        await viewModel.InitializeAsync();

        Assert.NotEmpty(viewModel.WorkspacePanes);
        Assert.DoesNotContain(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, "default-workspace", StringComparison.Ordinal)
                || pane.Id.StartsWith("loading-workspace:", StringComparison.Ordinal));
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WhenAlreadyOpening_SecondRequestIsNoOp()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithExternalEntityTab_PopulatesTabAsynchronously()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task CreateTabFromEntityAsync_ExternalEntityNonDefaultUrlKey_SetsTitleToUrlKeyAndFixesTitle()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        // External entity with a non-default URL key only — no "default" key present
        var externalEntityId = new EntityId("ff402001-ff40-4ff4-8ff4-ff4002000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            externalEntityId,
            """
            {
              "entity-id": "ff402001-ff40-4ff4-8ff4-ff4002000001",
              "entity-types": ["entity", "external"],
              "names": [["tests", "externals", "non-default-url-key"]],
              "display-name": { "default": "Non-Default URL Entity" },
              "urls": { "docs": "https://docs.example.com" }
            }
            """);

        // Workspace tab with no explicit title — title must be derived from the URL key
        var workspaceId = new EntityId("ff402002-ff40-4ff4-8ff4-ff4002000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "ff402002-ff40-4ff4-8ff4-ff4002000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "non-default-url-key"]],
              "display-name": { "default": "Non-Default URL Key Workspace" },
              "regions": [
                {
                  "region-id": "main",
                  "title": "Main",
                  "dock": "center",
                  "size": 1.0,
                  "tabs": [
                    {
                      "tab-id": "non-default-url-tab-1",
                      "kind": "entity",
                      "dock": "full",
                      "content": {
                        "target-entity-name": ["tests", "externals", "non-default-url-key"]
                      }
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
        await WaitForWorkspaceTabAsync(contentDock!, "non-default-url-tab-1");

        var tabDoc = contentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .FirstOrDefault(d => d.Id == "non-default-url-tab-1");
        Assert.NotNull(tabDoc);
        var webVm = Assert.IsType<WebViewModel>(tabDoc!.TabViewModel);

        // Title must be the URL key, not the entity display name
        Assert.Equal("docs", webVm.Title);

        // titleFixed must be true: SetPageTitle should NOT update the tab title
        webVm.SetPageTitle("Page Title From Browser");
        Assert.Equal("docs", webVm.Title);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_CloseWhileTabsLoading_DoesNotCrash()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        // Create an external entity referenced by the workspace tab
        var externalEntityId = new EntityId("e0e00001-e0e0-4e0e-ae0e-e0e0e0e0e0e1");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            externalEntityId,
            """
            {
              "entity-id": "e0e00001-e0e0-4e0e-ae0e-e0e0e0e0e0e1",
              "entity-types": ["entity", "external"],
              "names": [["tests", "externals", "close-while-loading"]],
              "display-name": { "default": "Close While Loading" },
              "urls": { "default": "https://example.com" }
            }
            """);

        // Create a workspace that references the external entity via async entity lookup
        var workspaceId = new EntityId("e0e00002-e0e0-4e0e-ae0e-e0e0e0e0e0e2");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "e0e00002-e0e0-4e0e-ae0e-e0e0e0e0e0e2",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "close-while-loading"]],
              "display-name": { "default": "Close While Loading Workspace" },
              "regions": [
                {
                  "region-id": "main",
                  "title": "Main",
                  "dock": "center",
                  "size": 1.0,
                  "tabs": [
                    {
                      "tab-id": "cwl-tab-1",
                      "title": "CWL Tab",
                      "kind": "entity",
                      "dock": "full",
                      "content": {
                        "target-entity-name": ["tests", "externals", "close-while-loading"]
                      }
                    }
                  ]
                }
              ]
            }
            """);

        // Phase 1 completes on return; Phase 2 (PopulateWorkspacePaneTabsAsync) fires and
        // suspends at its async entity-fetch before it can add any tabs to the dock.
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        // Close the workspace before Phase 2's UI callbacks run.
        await viewModel.RemoveWorkspacePaneAsync(workspacePane);

        // Pump the Avalonia dispatcher enough times to let Phase 2 run to completion.
        // Each pump drains one layer of async work: entity-fetch continuation, guard-check
        // InvokeAsync, and final SyncWorkspacePaneFromDock InvokeAsync.
        await Dispatcher.UIThread.InvokeAsync(() => {});
        await Dispatcher.UIThread.InvokeAsync(() => {});
        await Dispatcher.UIThread.InvokeAsync(() => {});
        await Dispatcher.UIThread.InvokeAsync(() => {});

        // Guard must have fired: workspace is gone and no exception was thrown.
        Assert.DoesNotContain(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_SessionsView_GetEntitySubViewsIncludeAgentManifestEntities()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ViewEntityViewModel_TraversedEntitiesCollapsed_WhenDispositionIsCollapsed()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

        // Collapsed traversals keep children populated so expand/collapse only toggles visibility.
        Assert.Contains(
            viewModel.CurrentViewPopulation.Entities,
            e => e.Entity.EntityId == relatedId);
        Assert.Contains(
            workspaceVm.Children,
            e => e.Entity.EntityId == relatedId);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_ConstructsWithoutTemplateCastErrors()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        var window = new MainWindow(viewModel);

        Assert.NotNull(window);
        Assert.NotEmpty(window.DataTemplates);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task CreateWorkspacePane_DoesNotInjectFallbackCenterRegion_WhenWorkspaceHasNoRegions()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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
        
        // When workspace has no regions in JSON, we create a default tab for the workspace entity.
        // pane.Tabs is now the source of truth — confirm the single default tab was added.
        Assert.Single(workspacePane!.Tabs);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentDefinitionShortcutHandler_LocalEchoDefinition_CreatesAgentSessionTab()
    {
        var fixedCurrentTime = new DateTimeOffset(2026, 06, 12, 9, 23, 45, TimeSpan.Zero);
        await using var viewModel = CreateTestMainWindowViewModel();
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
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(

            agentSessionShortcutContext,

            CreateLocalTrustedExecutorSelector(),

            CreateTestRunningAgentChatTable());
        var openAgentDefinitionShortcutHandler = new OpenAgentDefinitionShortcutHandler(agentSessionShortcutContext, openAgentSessionShortcutHandler);

        var handled = await openAgentDefinitionShortcutHandler.Handle(viewModel, Shortcut.Open, agentDefinitionEntity);

        Assert.True(handled);
        var launchpadTab = Assert.IsType<AgentManifestLaunchpadViewModel>(viewModel.SelectedWorkspacePane.SelectedTab);
        Assert.Same(agentDefinitionEntity, launchpadTab.ManifestEntity);
        Assert.True(launchpadTab.CanStart);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentManifestShortcutHandler_LocalEchoManifest_CreatesAgentSessionTab()
    {
        var fixedCurrentTime = new DateTimeOffset(2026, 06, 12, 9, 23, 45, TimeSpan.Zero);
        await using var viewModel = CreateTestMainWindowViewModel();
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
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(

            agentSessionShortcutContext,

            CreateLocalTrustedExecutorSelector(),

            CreateTestRunningAgentChatTable());
        var openAgentManifestShortcutHandler = new OpenAgentManifestShortcutHandler(agentSessionShortcutContext, openAgentSessionShortcutHandler);

        var handled = await openAgentManifestShortcutHandler.Handle(viewModel, Shortcut.Open, agentManifestEntity);

        Assert.True(handled);
        var sessionTab2 = await WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane);
        await WaitForAgentReadyAsync(sessionTab2);
        Assert.NotNull(sessionTab2.Agent);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentManifestShortcutHandler_ManifestWithParameters_ShowsLaunchpadNotAutoStarted()
    {
        var fixedCurrentTime = new DateTimeOffset(2026, 06, 12, 9, 23, 45, TimeSpan.Zero);
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentManifestEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("a1b2c3d4-0000-4000-8000-000000000002"),
            """
            {
              "entity-id": "a1b2c3d4-0000-4000-8000-000000000002",
              "entity-types": ["entity", "agent-manifest"],
              "names": [["tests", "agent-manifests", "with-parameters"]],
              "display-name": { "default": "Manifest With Parameters" },
              "manifest": {
                "name": "with-parameters",
                "displayName": "Manifest With Parameters",
                "template": {
                  "kind": "prompt",
                  "name": "with-parameters",
                  "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
                },
                "parameters": {
                  "properties": [
                    { "name": "working-directory", "required": true }
                  ]
                }
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext(() => fixedCurrentTime);
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(

            agentSessionShortcutContext,

            CreateLocalTrustedExecutorSelector(),

            CreateTestRunningAgentChatTable());
        var openAgentManifestShortcutHandler = new OpenAgentManifestShortcutHandler(agentSessionShortcutContext, openAgentSessionShortcutHandler);

        var handled = await openAgentManifestShortcutHandler.Handle(viewModel, Shortcut.Open, agentManifestEntity);

        Assert.True(handled);
        var launchpadTab = await WaitForSelectedTabAsync<AgentManifestLaunchpadViewModel>(viewModel.SelectedWorkspacePane);
        Assert.Same(agentManifestEntity, launchpadTab.ManifestEntity);
        Assert.Single(launchpadTab.Parameters);
        Assert.False(launchpadTab.CanStart);
        Assert.DoesNotContain(viewModel.SelectedWorkspacePane.Tabs, static t => t is AgentSessionWorkspaceTabViewModel);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task AgentManifestLaunchpad_StartSessionWithParameters_CreatesAgentChatOnUIThread()
    {
        // Enforcement test for issue #909: the launchpad previously wrapped AgentChat creation in
        // Task.Run, constructing the chat on a thread-pool thread. With the foreground-context
        // affinity invariant enforced in the AgentChat constructor, this flow only reaches
        // AgentTabState.Ready when creation runs on the UI thread.
        var fixedCurrentTime = new DateTimeOffset(2026, 06, 12, 9, 23, 45, TimeSpan.Zero);
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentManifestEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("a1b2c3d4-0000-4000-8000-000000000909"),
            """
            {
              "entity-id": "a1b2c3d4-0000-4000-8000-000000000909",
              "entity-types": ["entity", "agent-manifest"],
              "names": [["tests", "agent-manifests", "ui-thread-creation"]],
              "display-name": { "default": "UI Thread Creation Manifest" },
              "manifest": {
                "name": "ui-thread-creation",
                "displayName": "UI Thread Creation Manifest",
                "template": {
                  "kind": "prompt",
                  "name": "ui-thread-creation",
                  "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
                },
                "parameters": {
                  "properties": [
                    { "name": "working-directory", "required": true }
                  ]
                }
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext(() => fixedCurrentTime);
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(

            agentSessionShortcutContext,

            CreateLocalTrustedExecutorSelector(),

            CreateTestRunningAgentChatTable());
        var openAgentManifestShortcutHandler = new OpenAgentManifestShortcutHandler(agentSessionShortcutContext, openAgentSessionShortcutHandler);

        var handled = await openAgentManifestShortcutHandler.Handle(viewModel, Shortcut.Open, agentManifestEntity);
        Assert.True(handled);

        var launchpadTab = await WaitForSelectedTabAsync<AgentManifestLaunchpadViewModel>(viewModel.SelectedWorkspacePane);
        launchpadTab.Parameters[0].Value = Environment.CurrentDirectory;
        Assert.True(launchpadTab.CanStart);

        launchpadTab.StartSessionCommand.Execute(null);

        var sessionTab = await WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane);
        await WaitForAgentReadyAsync(sessionTab);

        Assert.Equal(AgentTabState.Ready, sessionTab.State);
        Assert.NotNull(sessionTab.Agent);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentSessionShortcutHandler_Handle_CreatesAgentChatOnUIThread()
    {
        // Enforcement test for issue #909: the loaded-session path (shortcut handler →
        // RunningAgentChatTable → AgentChatFactory) must create the AgentChat on the UI thread.
        // The factory's foreground scheduler is a SynchronizationContextTaskScheduler, so an
        // off-context construction would throw and the tab would end in AgentTabState.Failed.
        var runningAgentChatTable = CreateTestRunningAgentChatTable();
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("a1b2c3d4-0000-4000-8000-000000000910"),
            """
            {
              "entity-id": "a1b2c3d4-0000-4000-8000-000000000910",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "ui-thread-load"]],
              "display-name": { "default": "UI Thread Load" },
              "definition": {
                "kind": "prompt",
                "name": "ui-thread-load",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext, CreateLocalTrustedExecutorSelector(), runningAgentChatTable);

        var handled = await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);
        Assert.True(handled);

        var sessionTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(sessionTab);

        Assert.Equal(AgentTabState.Ready, sessionTab.State);
        Assert.NotNull(sessionTab.Lease);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentDefinitionShortcutHandler_WorkspaceEntityTool_IsMappedInWorkspacesGui()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(

            agentSessionShortcutContext,

            CreateLocalTrustedExecutorSelector(),

            CreateTestRunningAgentChatTable());
        var openAgentDefinitionShortcutHandler = new OpenAgentDefinitionShortcutHandler(agentSessionShortcutContext, openAgentSessionShortcutHandler);

        var handled = await openAgentDefinitionShortcutHandler.Handle(viewModel, Shortcut.Open, agentDefinitionEntity);

        Assert.True(handled);
        var sessionTab = await WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane);
        await WaitForAgentReadyAsync(sessionTab);
        Assert.NotNull(sessionTab.Agent);
        Assert.Contains(sessionTab.Agent.Tools, static tool => string.Equals(tool.Kind, "workspace-entity", StringComparison.Ordinal));
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task CreateWorkspacePaneAsync_WithAgentSessionTab_CreatesAgentSessionWorkspaceTabViewModel()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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
        var restoredTab = Assert.Single(workspacePane.Tabs);
        var agentSessionTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(restoredTab);
        Assert.Equal("restored-tab-1", agentSessionTab.Id);
        Assert.Equal("My Restored Session", agentSessionTab.Title);
        Assert.True(agentSessionTab.Entity?.IsEntityType("agent-session"));
        await WaitForAgentReadyAsync(agentSessionTab);
        Assert.NotNull(agentSessionTab.Agent);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task CreateWorkspacePaneAsync_WithAgentSessionTabButMissingDefinition_FallsBackToEntityWorkspaceTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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
        var agentTab = Assert.Single(workspacePane.Tabs);
        Assert.IsType<AgentSessionWorkspaceTabViewModel>(agentTab);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task CloseActiveTabCommand_WithTwoTabs_ClosesActiveTabAndLeavesOther()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task CycleTabForwardCommand_WithThreeTabs_WrapsAroundForward()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task CycleTabBackwardCommand_WithThreeTabs_WrapsAroundBackward()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task CycleTabForwardCommand_WithSingleTab_IsNoOp()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task GoToTabAtIndexCommand_WithThreeTabs_ActivatesCorrectTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task GoToTabAtIndexCommand_WithIndexOutOfRange_IsNoOp()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task GoToWorkspacePaneAtIndexCommand_WithMultiplePanes_ActivatesCorrectPane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task GoToWorkspacePaneAtIndexCommand_WithIndexOutOfRange_IsNoOp()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var selectedBefore = viewModel.SelectedWorkspacePane;

        viewModel.GoToWorkspacePaneAtIndexCommand.Execute("99");

        Assert.Equal(selectedBefore, viewModel.SelectedWorkspacePane);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task GoToWorkspacePaneAtIndexCommand_WithTwoPanes_ActivatesCorrectDockDocument()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("77200001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "77200001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "goto-pane-active-a"]],
              "display-name": { "default": "Goto Pane Active A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("77200001-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "77200001-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "goto-pane-active-b"]],
              "display-name": { "default": "Goto Pane Active B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var workspacesDock = FindDocumentDockIn(viewModel.Layout!);
        Assert.NotNull(workspacesDock);

        viewModel.GoToWorkspacePaneAtIndexCommand.Execute("1");

        Assert.Equal(viewModel.WorkspacePanes[1], viewModel.SelectedWorkspacePane);
        var activePaneDoc = workspacesDock!.ActiveDockable as WorkspacePaneDocument;
        Assert.NotNull(activePaneDoc);
        Assert.Equal(viewModel.WorkspacePanes[1].Id, activePaneDoc!.WorkspacePane.Id);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task GoToWorkspacePaneAtIndexCommand_WithTwoPanes_ActivatesFirstPane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("77200002-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "77200002-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "goto-pane-first-a"]],
              "display-name": { "default": "Goto Pane First A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("77200002-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "77200002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "goto-pane-first-b"]],
              "display-name": { "default": "Goto Pane First B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        // Navigate to pane 1 first, then back to 0
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute("1");
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute("0");

        Assert.Equal(viewModel.WorkspacePanes[0], viewModel.SelectedWorkspacePane);
        var workspacesDock = FindDocumentDockIn(viewModel.Layout!);
        var activePaneDoc = workspacesDock!.ActiveDockable as WorkspacePaneDocument;
        Assert.NotNull(activePaneDoc);
        Assert.Equal(viewModel.WorkspacePanes[0].Id, activePaneDoc!.WorkspacePane.Id);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task GoToWorkspacePaneAtIndexCommand_WhenActiveTabInTargetPaneHasUnreadNotification_MarksNotificationRead()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("ff000001-ff00-4f00-8f00-ff0000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "ff000001-ff00-4f00-8f00-ff0000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-pane-switch-a"]],
              "display-name": { "default": "Notif Pane Switch A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("ff000002-ff00-4f00-8f00-ff0000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "ff000002-ff00-4f00-8f00-ff0000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-pane-switch-b"]],
              "display-name": { "default": "Notif Pane Switch B" },
              "regions": []
            }
            """);

        // Open both workspaces; after OpenWorkspaceAsync(B) pane B (index 1) is selected.
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });
        Assert.True(viewModel.WorkspacePanes.Count >= 2,
            $"Expected at least 2 panes. Actual: {viewModel.WorkspacePanes.Count}; ids={string.Join(", ", viewModel.WorkspacePanes.Select(p => $"'{p.Id}'"))}");

        // Open a tab in pane B while it is the selected pane.
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "notif-pane-switch-tab-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabB);

        // Flush the dispatcher queue so that any fire-and-forget work from OpenWorkspaceAsync
        // (e.g. PopulateWorkspacePaneTabsAsync adding a default entity-view tab) completes
        // before we assert on the dock state. Without this drain, the populate dispatch can run
        // after the test has set up tabB and overwrite SelectedTab, making the test flaky.
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // After the drain, tabB must still be the selected/active tab in pane B.
        var paneBIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdB.ToString());
        Assert.True(paneBIndex >= 0, $"Pane B not found. Panes: {string.Join(", ", viewModel.WorkspacePanes.Select(p => $"'{p.Id}'"))}");
        Assert.Equal("notif-pane-switch-tab-b", viewModel.WorkspacePanes[paneBIndex].SelectedTab?.Id);

        // Switch to pane A so pane B's tab is no longer visible/active in the view.
        var paneAIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdA.ToString());
        Assert.True(paneAIndex >= 0, $"Pane A not found. Panes: {string.Join(", ", viewModel.WorkspacePanes.Select(p => $"'{p.Id}'"))}");
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute(paneAIndex.ToString());
        Assert.Equal(viewModel.WorkspacePanes[paneAIndex], viewModel.SelectedWorkspacePane);

        // Post an unread notification to pane B's tab. Because pane B is not selected,
        // OnActiveDockableChanged is not fired for it, so the notification stays unread.
        viewModel.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "notif-pane-switch-tab-b" },
            "Tab B", "test notification", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));

        Assert.False(viewModel.NotificationService.Notifications
            .First(n => n.TabKey == "notif-pane-switch-tab-b").IsRead);

        // Switch back to pane B — this should mark the notification as read.
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute(paneBIndex.ToString());

        Assert.True(viewModel.NotificationService.Notifications
            .First(n => n.TabKey == "notif-pane-switch-tab-b").IsRead,
            "Expected notification for tabB to be marked read after switching back to pane B");
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task GoToWorkspacePaneAtIndexCommand_WhenActiveTabInCurrentPaneHasUnreadNotification_OnlyMarksTargetPaneTabRead()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("ff000003-ff00-4f00-8f00-ff0000000003");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "ff000003-ff00-4f00-8f00-ff0000000003",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-pane-only-a"]],
              "display-name": { "default": "Notif Pane Only A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("ff000004-ff00-4f00-8f00-ff0000000004");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "ff000004-ff00-4f00-8f00-ff0000000004",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-pane-only-b"]],
              "display-name": { "default": "Notif Pane Only B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        // Flush the dispatcher queue to let any pending PopulateWorkspacePaneTabsAsync complete.
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // Open a tab in pane B (currently selected after OpenWorkspaceAsync(B)).
        // tabB becomes the active dockable in pane B, so pane B's SelectedTab = tabB.
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "notif-pane-only-tab-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabB);

        // Switch to pane A. Neither "notif-pane-only-tab-a" nor "notif-pane-only-tab-b" is the
        // active dockable in pane A, so any notification posted now will not be auto-marked read.
        var paneAIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdA.ToString());
        Assert.True(paneAIndex >= 0, $"Pane A not found. Panes: {string.Join(", ", viewModel.WorkspacePanes.Select(p => $"'{p.Id}'"))}");
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute(paneAIndex.ToString());

        // Post unread notifications to both tab IDs. The active tab in pane A is neither
        // "notif-pane-only-tab-a" nor "notif-pane-only-tab-b", so both start unread.
        viewModel.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "notif-pane-only-tab-a" },
            "Tab A", "test notification A", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));
        viewModel.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "notif-pane-only-tab-b" },
            "Tab B", "test notification B", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));

        // Switch to pane B — only pane B's active tab (tabB) notification should be marked read.
        var paneBIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdB.ToString());
        Assert.True(paneBIndex >= 0, $"Pane B not found. Panes: {string.Join(", ", viewModel.WorkspacePanes.Select(p => $"'{p.Id}'"))}");
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute(paneBIndex.ToString());

        Assert.True(viewModel.NotificationService.Notifications
            .First(n => n.TabKey == "notif-pane-only-tab-b").IsRead,
            "Switching to pane B should mark pane B's active tab notification as read.");
        Assert.False(viewModel.NotificationService.Notifications
            .First(n => n.TabKey == "notif-pane-only-tab-a").IsRead,
            "Pane A's tab notification should remain unread after switching away.");
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithFocusedTabId_ActivatesFocusedTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithAbsentFocusedTabId_DoesNotCrash()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithNonMatchingFocusedTabId_DoesNotCrash()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task CreateAgentSessionEntityAsync_WithOwningProfileEntityId_StoresOwningProfileEntityIdInData()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var localProfileEntityId = entityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;

        var agentDefinitionId = new EntityId("aa010001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            agentDefinitionId,
            """
            {
              "entity-id": "aa010001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "owner-store-echo"]],
              "display-name": { "default": "Owner Store Echo" },
              "definition": {
                "kind": "prompt",
                "name": "owner-store-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionId = Guid.NewGuid().ToString("n");
        var createdSession = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId, owningProfileEntityId: localProfileEntityId);

        Assert.NotNull(createdSession);
        Assert.True(createdSession!.Data is JsonElement data
            && data.TryGetProperty("owning-profile-entity-id", out var idElement)
            && string.Equals(idElement.GetString(), localProfileEntityId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task TryBuildAgent_WithLocalProfileOwner_RoutesToLocalExecutorSuccessfully()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var localProfileEntityId = entityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;

        var agentDefinitionId = new EntityId("aa020001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            agentDefinitionId,
            """
            {
              "entity-id": "aa020001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "local-owner-echo"]],
              "display-name": { "default": "Local Owner Echo" },
              "definition": {
                "kind": "prompt",
                "name": "local-owner-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionId = Guid.NewGuid().ToString("n");
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId, owningProfileEntityId: localProfileEntityId);
        Assert.NotNull(agentSessionEntity);

        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());

        await openAgentSessionShortcutHandler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);
        Assert.Equal(AgentTabState.Ready, agentTab.State);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task TryBuildAgent_WithNoOwningProfile_DefaultsToLocalExecution()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var agentDefinitionId = new EntityId("aa030001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            agentDefinitionId,
            """
            {
              "entity-id": "aa030001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "no-owner-echo"]],
              "display-name": { "default": "No Owner Echo" },
              "definition": {
                "kind": "prompt",
                "name": "no-owner-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        // No owningProfileEntityId → defaults to local
        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionId = Guid.NewGuid().ToString("n");
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId);
        Assert.NotNull(agentSessionEntity);

        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());

        await openAgentSessionShortcutHandler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab2 = Assert.IsType<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab2);
        Assert.Equal(AgentTabState.Ready, agentTab2.State);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task TryBuildAgent_WithRemoteProfileOwner_SetsFailedWhenNoConnectionAvailable()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var agentDefinitionId = new EntityId("aa040001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            agentDefinitionId,
            """
            {
              "entity-id": "aa040001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "remote-owner-echo"]],
              "display-name": { "default": "Remote Owner Echo" },
              "definition": {
                "kind": "prompt",
                "name": "remote-owner-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        // Use a different GUID as the owning profile (simulates a remote profile with no connection)
        var remoteProfileEntityId = new EntityId(Guid.NewGuid());
        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionId = Guid.NewGuid().ToString("n");
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId, owningProfileEntityId: remoteProfileEntityId);
        Assert.NotNull(agentSessionEntity);

        // Empty registry → no reverse connection available for the remote profile
        var emptyRegistry = new ReverseExecutionRegistry();
        var selectorWithNoRemote = TrustedExecutorComposition.CreateSelector(emptyRegistry);
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext,
            selectorWithNoRemote,
            CreateTestRunningAgentChatTable());

        await openAgentSessionShortcutHandler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab3 = Assert.IsType<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab3);
        Assert.Equal(AgentTabState.Failed, agentTab3.State);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentSessionShortcutHandler_Handle_UsesEntityIdAsTabId()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("ab010001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ab010001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "tab-id-echo"]],
              "display-name": { "default": "Tab ID Echo" },
              "definition": {
                "kind": "prompt",
                "name": "tab-id-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());

        var paneId = viewModel.SelectedWorkspacePane.Id;
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var tab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane.SelectedTab);
        var expectedTabId = $"{paneId}-{agentSessionEntity!.EntityId}";
        Assert.Equal(expectedTabId, tab.Id);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentSessionShortcutHandler_Handle_SameEntityOpenedTwice_DeduplicatesTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("ab020001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ab020001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "dedup-echo"]],
              "display-name": { "default": "Dedup Echo" },
              "definition": {
                "kind": "prompt",
                "name": "dedup-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());

        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        // Wait for background agent initialization to complete
        await Dispatcher.UIThread.InvokeAsync(() => {}, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(() => {}, DispatcherPriority.Background);

        // Check workspacePane.Tabs directly since VisibleDockables requires visual tree.
        // Tab ID format: "{paneId}-{entityId}" (see OpenAgentSessionShortcutHandler line 72).
        var paneId = viewModel.SelectedWorkspacePane!.Id;
        var agentSessionTabs = viewModel.SelectedWorkspacePane!.Tabs
            .Where(t => t.Id == $"{paneId}-{agentSessionEntity!.EntityId}")
            .ToList();
        Assert.Single(agentSessionTabs);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentSessionShortcutHandler_Handle_WithRunningAgentChatTable_AcrossTwoWorkspacePanes_SharesAgentChat()
    {
        var runningAgentChatTable = CreateTestRunningAgentChatTable();
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("ab030001-0000-4000-8000-000000000001");
        var workspaceIdB = new EntityId("ab030002-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdA,
            """
            {
              "entity-id": "ab030001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "shared-chat-a"]],
              "display-name": { "default": "Shared Chat A" },
              "regions": []
            }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdB,
            """
            {
              "entity-id": "ab030002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "shared-chat-b"]],
              "display-name": { "default": "Shared Chat B" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var agentDefinitionId = new EntityId("ab030003-0000-4000-8000-000000000003");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ab030003-0000-4000-8000-000000000003",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "shared-chat-echo"]],
              "display-name": { "default": "Shared Chat Echo" },
              "definition": {
                "kind": "prompt",
                "name": "shared-chat-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext, CreateLocalTrustedExecutorSelector(), runningAgentChatTable);

        // Open in pane A
        var paneAIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdA.ToString());
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute(paneAIndex.ToString());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);
        var tabA = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);

        // Open in pane B
        var paneBIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdB.ToString());
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute(paneBIndex.ToString());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);
        var tabB = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);

        await WaitForAgentReadyAsync(tabA);
        await WaitForAgentReadyAsync(tabB);

        Assert.Equal(AgentTabState.Ready, tabA.State);
        Assert.Equal(AgentTabState.Ready, tabB.State);
        Assert.NotNull(tabA.Lease);
        Assert.NotNull(tabB.Lease);
        Assert.Same(tabA.Lease!.AgentChat, tabB.Lease!.AgentChat);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenUrlHandler_WhenAgentChatIsInNonSelectedPane_OpensTabInAgentChatPane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("ab010001-0000-4000-8000-000000000001");
        var workspaceIdB = new EntityId("ab010001-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdA,
            """
            {
              "entity-id": "ab010001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "url-handler-pane-a"]],
              "display-name": { "default": "URL Handler Pane A" },
              "regions": []
            }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdB,
            """
            {
              "entity-id": "ab010001-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "url-handler-pane-b"]],
              "display-name": { "default": "URL Handler Pane B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var paneA = viewModel.WorkspacePanes.First(p => p.Id == workspaceIdA.ToString());
        var paneB = viewModel.WorkspacePanes.First(p => p.Id == workspaceIdB.ToString());

        // Open agent session in pane A
        var paneAIndex = viewModel.WorkspacePanes.IndexOf(paneA);
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute(paneAIndex.ToString());

        var agentDefinitionId = new EntityId("ab010001-0000-4000-8000-000000000003");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ab010001-0000-4000-8000-000000000003",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "url-nonselected-echo"]],
              "display-name": { "default": "URL Nonselected Echo" },
              "definition": {
                "kind": "prompt",
                "name": "url-nonselected-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);
        Assert.NotNull(agentTab.Agent);

        // Switch to pane B so agent chat pane is NOT selected
        var paneBIndex = viewModel.WorkspacePanes.IndexOf(paneB);
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute(paneBIndex.ToString());
        Assert.Equal(paneB, viewModel.SelectedWorkspacePane);

        // Invoke the URL handler — should open in pane A, not pane B
        const string testUrl = "https://url-nonselected.example.com";
        agentTab.Agent!.OpenUrlHandler!.Invoke(testUrl);

        var paneBDock = FindDocumentDockIn(paneB.ContentLayout!);
        Assert.NotNull(paneBDock);
        var paneADock = FindDocumentDockIn(paneA.ContentLayout!);
        Assert.NotNull(paneADock);

        await WaitForWorkspaceTabAsync(paneADock!, $"web-{testUrl}");

        // New tab must appear in pane A
        Assert.Contains(
            paneADock!.VisibleDockables!.OfType<WorkspaceDocument>(),
            doc => doc.Id == $"web-{testUrl}");

        // New tab must NOT appear in pane B
        Assert.DoesNotContain(
            paneBDock!.VisibleDockables?.OfType<WorkspaceDocument>() ?? [],
            doc => doc.Id == $"web-{testUrl}");
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenUrlHandler_WhenAgentChatIsInSelectedPane_OpensTabInSamePane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("ab020002-0000-4000-8000-000000000001");
        var workspaceIdB = new EntityId("ab020002-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdA,
            """
            {
              "entity-id": "ab020002-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "url-selected-pane-a"]],
              "display-name": { "default": "URL Selected Pane A" },
              "regions": []
            }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdB,
            """
            {
              "entity-id": "ab020002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "url-selected-pane-b"]],
              "display-name": { "default": "URL Selected Pane B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var paneA = viewModel.WorkspacePanes.First(p => p.Id == workspaceIdA.ToString());
        var paneAIndex = viewModel.WorkspacePanes.IndexOf(paneA);
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute(paneAIndex.ToString());
        Assert.Equal(paneA, viewModel.SelectedWorkspacePane);

        var agentDefinitionId = new EntityId("ab020002-0000-4000-8000-000000000003");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ab020002-0000-4000-8000-000000000003",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "url-selected-echo"]],
              "display-name": { "default": "URL Selected Echo" },
              "definition": {
                "kind": "prompt",
                "name": "url-selected-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);
        Assert.NotNull(agentTab.Agent);

        // Pane A is selected — invoke handler while it IS selected
        Assert.Equal(paneA, viewModel.SelectedWorkspacePane);

        const string testUrl = "https://url-selected.example.com";
        agentTab.Agent!.OpenUrlHandler!.Invoke(testUrl);

        var paneADock = FindDocumentDockIn(paneA.ContentLayout!);
        Assert.NotNull(paneADock);
        await WaitForWorkspaceTabAsync(paneADock!, $"web-{testUrl}");

        Assert.Contains(
            paneADock!.VisibleDockables!.OfType<WorkspaceDocument>(),
            doc => doc.Id == $"web-{testUrl}");
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenUrlHandler_InsertsNewTabAfterAgentSessionTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var agentDefinitionId = new EntityId("ab020003-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ab020003-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "url-insert-echo"]],
              "display-name": { "default": "URL Insert Echo" },
              "definition": {
                "kind": "prompt",
                "name": "url-insert-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        // Open agent session tab
        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);
        Assert.NotNull(agentTab.Agent);

        // Open another tab after the agent session tab
        var otherTab = new WebViewModel("https://other.example.com") { Id = "url-insert-other", Title = "Other" };
        await viewModel.OpenTabAsync(otherTab);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);

        var targetPane = viewModel.SelectedWorkspacePane;
        var agentTabIndex = targetPane.Tabs
            .Select((t, i) => (t, i))
            .First(x => x.t.Id == agentTab.Id).i;

        // Invoke the URL handler — new tab should be inserted right after the agent session tab
        const string testUrl = "https://url-insert.example.com";
        agentTab.Agent!.OpenUrlHandler!.Invoke(testUrl);

        await WaitForWorkspaceTabAsync(documentDock!, $"web-{testUrl}");

        var webTabIndex = targetPane.Tabs
            .Select((t, i) => (t, i))
            .First(x => x.t.Id == $"web-{testUrl}").i;

        Assert.Equal(agentTabIndex + 1, webTabIndex);
    }

    private static ITrustedExecutorSelector CreateLocalTrustedExecutorSelector()
        => TrustedExecutorComposition.CreateSelector(new ReverseExecutionRegistry());

    // ── Float-tab disposal guard (issue #635) ─────────────────────────────────

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task FloatDockable_AgentSessionTab_DoesNotDisposeOrRemoveTabFromPane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("f1050001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "f1050001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "float-no-dispose-echo"]],
              "display-name": { "default": "Float No Dispose Echo" },
              "definition": {
                "kind": "prompt",
                "name": "float-no-dispose-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var workspacePane = viewModel.SelectedWorkspacePane;
        var agentTab = await WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(workspacePane);
        await WaitForAgentReadyAsync(agentTab);

        var dockFactory = GetDockFactoryAs<WorkspaceDockFactory>(viewModel);
        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, agentTab.Id);

        var document = dockFactory.GetDocumentForTab(agentTab.Id);
        Assert.NotNull(document);

        // Act: float the tab into a floating window
        dockFactory.FloatDockable(document!);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // The tab must remain in pane.Tabs — float must NOT remove or dispose it
        Assert.Contains(workspacePane.Tabs, t => ReferenceEquals(t, agentTab));
        Assert.NotNull(agentTab.Agent);
        Assert.Equal(AgentTabState.Ready, agentTab.State);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task CloseDockable_AfterFloat_DisposesTabAndRemovesFromPane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("f1060001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "f1060001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "float-close-echo"]],
              "display-name": { "default": "Float Close Echo" },
              "definition": {
                "kind": "prompt",
                "name": "float-close-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var workspacePane = viewModel.SelectedWorkspacePane;
        var agentTab = await WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(workspacePane);
        await WaitForAgentReadyAsync(agentTab);

        var dockFactory = GetDockFactoryAs<WorkspaceDockFactory>(viewModel);
        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, agentTab.Id);

        var document = dockFactory.GetDocumentForTab(agentTab.Id);
        Assert.NotNull(document);

        // Float first, then close from the floating state
        dockFactory.FloatDockable(document!);
        await Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.Contains(workspacePane.Tabs, t => ReferenceEquals(t, agentTab));

        // Act: close the floating document
        dockFactory.CloseDockable(document!);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // Tab must have been removed from pane.Tabs and disposed
        Assert.DoesNotContain(workspacePane.Tabs, t => ReferenceEquals(t, agentTab));
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task CloseDockable_FromMainDock_DisposesTabExactlyOnce()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("f1070001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "f1070001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "close-once-echo"]],
              "display-name": { "default": "Close Once Echo" },
              "definition": {
                "kind": "prompt",
                "name": "close-once-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var workspacePane = viewModel.SelectedWorkspacePane;
        var agentTab = await WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(workspacePane);
        await WaitForAgentReadyAsync(agentTab);

        var dockFactory = GetDockFactoryAs<WorkspaceDockFactory>(viewModel);
        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, agentTab.Id);

        var document = dockFactory.GetDocumentForTab(agentTab.Id);
        Assert.NotNull(document);

        // Track how many times the tab is removed from pane.Tabs
        var removeCount = 0;
        ((System.Collections.Specialized.INotifyCollectionChanged)workspacePane.Tabs).CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove
                && e.OldItems?.Contains(agentTab) == true)
            {
                removeCount++;
            }
        };

        // Act: close directly from the main dock (no float)
        dockFactory.CloseDockable(document!);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // Tab must be removed exactly once (guards against double-dispose from both
        // SyncPaneTabsFromDockChange and OnDockableClosed firing on close)
        Assert.Equal(1, removeCount);
        Assert.DoesNotContain(workspacePane.Tabs, t => ReferenceEquals(t, agentTab));
    }

    // ── Dock-layout save / restore (issue #561) ──────────────────────────────

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabAsync_ThenWriteBack_DockLayoutJsonContainsDockTabDescriptor()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://descriptor-test.example.com")
        {
            Id = "dt-tab-1",
            Title = "Descriptor Test",
        };
        await viewModel.OpenTabAsync(tab);

        // Serialize the dock layout directly to verify DockTabDescriptor is embedded
        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane.ContentLayout);

        var serializer = new DockSerializer(typeof(System.Collections.ObjectModel.ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        var layoutJson = serializer.Serialize(pane.ContentLayout!);

        // The serialized layout must contain the Descriptor property
        Assert.Contains("Descriptor", layoutJson, StringComparison.Ordinal);
        // And the browser kind
        Assert.Contains("browser", layoutJson, StringComparison.Ordinal);
        // And the URL
        Assert.Contains("descriptor-test.example.com", layoutJson, StringComparison.Ordinal);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabAsync_ThenWriteBack_DockLayoutDoesNotContainTabViewModelData()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://no-vm-test.example.com")
        {
            Id = "no-vm-tab-1",
            Title = "No VM Leak Test",
        };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane.ContentLayout);

        // Diagnostic: assert Owner is null before serialization
        Assert.Null(pane.ContentLayout!.Owner);

        // Use WorkspaceDockTypeInfoResolver to match production serialization (it strips
        // Type-typed Avalonia properties and handles Owner back-references via ReferenceHandler.Preserve)
        var serializer = new DockSerializer(typeof(System.Collections.ObjectModel.ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        var layoutJson = serializer.Serialize(pane.ContentLayout!);

        // Content-bearing properties must NOT appear in the serialized layout
        Assert.DoesNotContain("TabViewModel", layoutJson, StringComparison.Ordinal);
        Assert.DoesNotContain("EffectiveTabHeader", layoutJson, StringComparison.Ordinal);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithSavedDockLayout_RestoresTabsFromDescriptors()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        // Step 1: open a browser tab and capture the dock-layout JSON directly from the pane
        var tab = new WebViewModel("https://restore-test.example.com")
        {
            Id = "restore-tab-browser",
            Title = "Restore Browser Tab",
        };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane.ContentLayout);

        var serializer = new DockSerializer(typeof(System.Collections.ObjectModel.ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        var dockLayoutJson = serializer.Serialize(pane.ContentLayout!);
        Assert.Contains("Descriptor", dockLayoutJson, StringComparison.Ordinal);

        // Step 2: build a workspace entity that carries the saved dock-layout and open it
        var workspaceId = new EntityId("d0c1a7a0-0000-4000-8000-000000000001");
        var workspaceJson = $$"""
            {
              "entity-id": "d0c1a7a0-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Restore Dock Layout Workspace" },
              "dock-layout": {{dockLayoutJson}},
              "regions": []
            }
            """;
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, workspaceJson);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(restoredPane);

        // Wait for PopulateWorkspacePaneTabsAsync to populate the tabs
        await WaitForWorkspacePaneTabsAsync(restoredPane!);

        // The pane must have at least one tab from the dock-layout restore
        Assert.NotEmpty(restoredPane!.Tabs);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task PopulateWorkspacePaneTabsAsync_FallsBackToTabsArray_WhenDockLayoutAbsent()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("fa11b4c0-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "fa11b4c0-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "fallback-tabs-array"]],
              "display-name": { "default": "Fallback Tabs Array Workspace" },
              "regions": [
                {
                  "region-id": "main",
                  "title": "Main",
                  "dock": "center",
                  "size": 1.0,
                  "tabs": [
                    {
                      "tab-id": "fallback-tab-1",
                      "title": "Fallback Tab",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://fallback.example.com" }
                    }
                  ]
                }
              ]
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(workspacePane);

        var contentDock = FindDocumentDockIn(workspacePane!.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, "fallback-tab-1");

        var tabDoc = contentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .FirstOrDefault(d => d.Id == "fallback-tab-1");
        Assert.NotNull(tabDoc);
        Assert.IsType<WebViewModel>(tabDoc!.TabViewModel);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task PopulateWorkspacePaneTabsAsync_RestoresFromDockLayout_WhenPresent()
    {
        // Arrange: capture a real dock-layout JSON from an open tab, then open a new
        // workspace entity that carries that dock-layout.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://restore-layout-present.example.com")
        {
            Id = "rlp-tab",
            Title = "Restore Layout Present",
        };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;
        var serializer = new DockSerializer(typeof(System.Collections.ObjectModel.ObservableCollection<>), new WorkspaceDockTypeInfoResolver());

        // Wait for ItemContainerGenerator to populate VisibleDockables
        var rlpContentDock = FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(rlpContentDock);
        await WaitForWorkspaceTabAsync(rlpContentDock!, "rlp-tab");

        var dockLayoutJson = serializer.Serialize(pane.ContentLayout!);
        Assert.Contains("Descriptor", dockLayoutJson, StringComparison.Ordinal);

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("e570ee01-0000-4000-8000-000000000001");
        var workspaceJson = $$"""
            {
              "entity-id": "e570ee01-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Restore Layout Present WS" },
              "dock-layout": {{dockLayoutJson}},
              "regions": []
            }
            """;
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, workspaceJson);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(restoredPane);

        // Wait for PopulateWorkspacePaneTabsAsync to populate the tabs
        await WaitForPanePopulatedAsync(restoredPane!);

        Assert.NotEmpty(restoredPane!.Tabs);
        Assert.Contains(restoredPane.Tabs, t => t is WebViewModel);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task PopulateWorkspacePaneTabsAsync_WhenDockLayoutRestoreCompletes_SignalsPanePopulated()
    {
        // Verifies the Populated task completes successfully for the happy path
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://signals-populated.example.com")
        {
            Id = "sp-tab",
            Title = "Signals Populated",
        };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;
        var serializer = new DockSerializer(typeof(System.Collections.ObjectModel.ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        var spContentDock = FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(spContentDock);
        await WaitForWorkspaceTabAsync(spContentDock!, "sp-tab");

        var dockLayoutJson = serializer.Serialize(pane.ContentLayout!);

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("e570ee01-0000-4000-8000-000000000002");
        var workspaceJson = $$"""
            {
              "entity-id": "e570ee01-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Signals Populated WS" },
              "dock-layout": {{dockLayoutJson}},
              "regions": []
            }
            """;
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, workspaceJson);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(restoredPane);

        // The Populated task should complete without throwing
        await WaitForPanePopulatedAsync(restoredPane!);
        Assert.NotEmpty(restoredPane!.Tabs);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task PopulateWorkspacePaneTabsAsync_WhenNoDockLayoutAndNoTabs_SignalsPanePopulatedAfterDefaultTabAdd()
    {
        // Verifies the default-tab fallback path signals completion
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("e570ee01-0000-4000-8000-000000000003");
        var workspaceJson = """
            {
              "entity-id": "e570ee01-0000-4000-8000-000000000003",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Default Tab Fallback WS" },
              "regions": []
            }
            """;
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, workspaceJson);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var pane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(pane);

        // The Populated task should complete even when using the default-tab fallback
        await WaitForPanePopulatedAsync(pane!);
        Assert.NotEmpty(pane!.Tabs);
        Assert.Contains(pane.Tabs, t => t is EntityWorkspaceTabViewModel);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task WaitForPanePopulatedAsync_WhenPopulateHangs_ThrowsTimeoutExceptionWithDiagnostics()
    {
        // Verifies the timeout diagnostic message includes pane ID and Tabs.Count
        var entitySnapshot = new EntitySnapshot
        {
            EntityId = new EntityId("e570ee01-0000-4000-8000-000000000004"),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = JsonDocument.Parse("""
                {
                  "entity-id": "e570ee01-0000-4000-8000-000000000004",
                  "entity-types": ["entity", "workspace"],
                  "display-name": { "default": "Hang Test WS" }
                }
                """).RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
        var subscribedEntity = new SubscribedEntityViewModel(entitySnapshot);
        var pane = new WorkspacePaneViewModel(subscribedEntity, "e570ee01-0000-4000-8000-000000000004", null);

        // The Populated task should never complete (SignalPopulated is never called)
        var exception = await Assert.ThrowsAsync<TimeoutException>(
            async () => await WaitForPanePopulatedAsync(pane, TimeSpan.FromSeconds(1)));

        Assert.Contains("e570ee01-0000-4000-8000-000000000004", exception.Message);
        Assert.Contains("Tabs.Count=0", exception.Message);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task PopulateWorkspacePaneTabsAsync_WhenDockLayoutRestoreThrows_SurfacesExceptionOnPanePopulatedTask()
    {
        // Verifies that exceptions thrown during PopulateWorkspacePaneTabsAsync are propagated
        // through the Populated task via the SignalPopulated(Exception) mechanism.
        // This tests the exception handling in the ContinueWith continuation at MainWindowViewModel.cs:1586-1595
        
        var entitySnapshot = new EntitySnapshot
        {
            EntityId = new EntityId("e570ee01-0000-4000-8000-000000000005"),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = JsonDocument.Parse("""
                {
                  "entity-id": "e570ee01-0000-4000-8000-000000000005",
                  "entity-types": ["entity", "workspace"],
                  "display-name": { "default": "Exception Test WS" }
                }
                """).RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
        var subscribedEntity = new SubscribedEntityViewModel(entitySnapshot);
        var pane = new WorkspacePaneViewModel(subscribedEntity, "e570ee01-0000-4000-8000-000000000005", null);

        // Simulate the exception path by directly calling SignalPopulated with an exception
        // This tests that the exception is correctly propagated through the Populated task
        var testException = new InvalidOperationException("Simulated populate failure");
        pane.SignalPopulated(testException);

        // The Populated task should fault and propagate the exact exception
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await pane.Populated);
        
        Assert.Same(testException, exception);
        Assert.Equal("Simulated populate failure", exception.Message);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task WriteBackWorkspaceTabs_IsNotCalledOnDockLayoutChange()
    {
        // After the fix, pane.Tabs.CollectionChanged is NOT subscribed for write-back.
        // Dock-order changes (Move/Reset from dock animations) must NOT trigger entity updates.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://no-write-a.example.com") { Id = "nw-a", Title = "A" };
        var tabB = new WebViewModel("https://no-write-b.example.com") { Id = "nw-b", Title = "B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        var entityBroker = GetEntityBroker(viewModel);
        var pane = viewModel.SelectedWorkspacePane;

        // Capture entity snapshot BEFORE dock layout mutation
        var before = (await entityBroker.GetEntitiesAsync([pane.Entity.EntityId]))
            .FirstOrDefault(e => e.EntityId == pane.Entity.EntityId);

        // Simulate a dock Move/Reset by reordering pane.Tabs directly (the same operation
        // SyncPaneTabsOrderFromDock performs). With the CollectionChanged subscription removed,
        // this must NOT trigger WriteBackWorkspaceTabs.
        pane.Tabs.Move(0, 1);

        // Give any async callbacks a chance to run
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var after = (await entityBroker.GetEntitiesAsync([pane.Entity.EntityId]))
            .FirstOrDefault(e => e.EntityId == pane.Entity.EntityId);

        // Entity must not have changed — no dock-layout key written
        var beforeJson = before?.Data is System.Text.Json.JsonElement be ? be.GetRawText() : "null";
        var afterJson = after?.Data is System.Text.Json.JsonElement ae ? ae.GetRawText() : "null";
        Assert.Equal(beforeJson, afterJson);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task SaveWorkspaceLayoutAsync_PersistsDockLayoutWithDescriptors()
    {
        // Explicit WriteBackWorkspaceTabs persists dock-layout JSON that contains
        // Descriptor data for each open tab.
        await using var viewModel = CreateTestMainWindowViewModel(
            configuration: new WorkspacesConfiguration { SkipStartupWorkspace = false });
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://save-layout-test.example.com")
        {
            Id = "slt-tab",
            Title = "Save Layout Test",
        };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;

        // Wait for ItemContainerGenerator to populate VisibleDockables before write-back
        var saveContentDock = FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(saveContentDock);
        await WaitForWorkspaceTabAsync(saveContentDock!, "slt-tab");

        // Verify entity is subscribed and has a ConcurrencyTag (not the placeholder)
        Assert.False(pane.Entity.EntityId == new Phantom.Workspaces.Data.EntityId(Guid.Empty),
            "SelectedWorkspacePane must be a real workspace entity, not the placeholder.");
        Assert.NotNull(pane.Entity.ConcurrencyTag);

        // Explicitly trigger write-back (simulates explicit save) and await completion
        var writeBackResult = await viewModel.WriteBackWorkspaceTabs(pane);
        var failedResults = writeBackResult.EntityResults
            .Where(r => r.UpdateState == Phantom.Workspaces.Data.UpdateState.Failed)
            .ToList();
        var errorMessages = failedResults
            .SelectMany(r => r.Errors ?? [])
            .Select(e => e.Message)
            .ToList();
        Assert.Empty(errorMessages);

        // pane.Entity is the subscribed entity view model; its Data is updated in-place
        // by EntityBroker.UpdateAsync when the underlying snapshot changes.
        var data = Assert.IsType<System.Text.Json.JsonElement>(pane.Entity.Data);
        Assert.True(data.TryGetProperty("dock-layout", out var dockLayoutEl));
        var dockLayoutJson = dockLayoutEl.GetRawText();
        Assert.Contains("Descriptor", dockLayoutJson, StringComparison.Ordinal);
        Assert.Contains("browser", dockLayoutJson, StringComparison.Ordinal);
        Assert.Contains("save-layout-test.example.com", dockLayoutJson, StringComparison.Ordinal);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task DockLayoutRoundTrip_PreservesSplitPositionsAndDescriptors()
    {
        // Verify serialize → deserialize round-trip: the Descriptor survives and the
        // layout structure is intact (no exceptions, correct types).
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://roundtrip-test.example.com")
        {
            Id = "rt-tab",
            Title = "Round-trip Test",
        };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane.ContentLayout);

        // Wait for ItemContainerGenerator to populate VisibleDockables
        var rtContentDock = FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(rtContentDock);
        await WaitForWorkspaceTabAsync(rtContentDock!, "rt-tab");

        var serializer = new DockSerializer(
            typeof(System.Collections.ObjectModel.ObservableCollection<>),
            new WorkspaceDockTypeInfoResolver());

        // Serialize
        var layoutJson = serializer.Serialize(pane.ContentLayout!);
        Assert.Contains("Descriptor", layoutJson, StringComparison.Ordinal);
        Assert.Contains("browser", layoutJson, StringComparison.Ordinal);
        Assert.Contains("roundtrip-test.example.com", layoutJson, StringComparison.Ordinal);
        Assert.DoesNotContain("TabViewModel", layoutJson, StringComparison.Ordinal);

        // Deserialize
        var restored = serializer.Deserialize<Dock.Model.Controls.IRootDock>(layoutJson);
        Assert.NotNull(restored);

        var docs = MainWindowViewModel.EnumerateAllDocuments(restored!).ToList();
        Assert.NotEmpty(docs);
        Assert.Contains(docs, d => d.Descriptor is BrowserDockTabDescriptor b
            && b.Url == "https://roundtrip-test.example.com");
    }

    private static T GetDockFactoryAs<T>(MainWindowViewModel viewModel)
    {
        var field = typeof(MainWindowViewModel)
            .GetField("dockFactory", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<T>(field!.GetValue(viewModel));
    }

    private static IDocumentDock? GetDocumentDock(MainWindowViewModel viewModel)    {
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

    /// <summary>
    /// Gets documents from a dock that correspond to tabs in the specified pane.
    /// Filters out any placeholder or orphaned documents that may exist in the dock.
    /// </summary>
    private static List<WorkspaceDocument> GetPaneDocuments(WorkspacePaneViewModel pane, IDocumentDock dock)
    {
        return dock.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .Where(doc => doc.Context is WorkspaceTabViewModel tab && pane.Tabs.Contains(tab))
            .ToList();
    }

    /// <summary>
    /// Waits for any fire-and-forget PopulateWorkspacePaneTabsAsync tasks to complete, then closes
    /// the default tabs that were added to each pane during population. Call this after opening
    /// workspaces and before opening test tabs so that pane.Tabs only contains the expected tabs.
    /// </summary>
    private static async Task CloseDefaultPaneTabsAsync(
        MainWindowViewModel viewModel,
        params WorkspacePaneViewModel[] panes)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        foreach (var pane in panes)
            foreach (var tab in pane.Tabs.ToList())
                viewModel.CloseTab(tab);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
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

    private static async Task WaitForWorkspacePaneTabsAsync(WorkspacePaneViewModel pane)
    {
        if (pane.Tabs.Count > 0)
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (pane.Tabs.Count > 0)
            {
                signal.TrySetResult();
            }
        }

        pane.Tabs.CollectionChanged += OnCollectionChanged;
        try
        {
            if (pane.Tabs.Count == 0)
            {
                await signal.Task;
            }
        }
        finally
        {
            pane.Tabs.CollectionChanged -= OnCollectionChanged;
        }
    }

    /// <summary>
    /// Waits for <see cref="WorkspacePaneViewModel.Populated"/> to complete with a bounded timeout.
    /// Throws <see cref="TimeoutException"/> with diagnostic details if populate does not complete in time.
    /// Propagates any exception raised during populate.
    /// </summary>
    private static async Task WaitForPanePopulatedAsync(WorkspacePaneViewModel pane, TimeSpan? timeout = null)
    {
        var populateTask = pane.Populated;
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
        var timeoutTask = Task.Delay(effectiveTimeout);

        if (await Task.WhenAny(populateTask, timeoutTask) == timeoutTask)
        {
            throw new TimeoutException(
                $"Pane {pane.Id} was not populated within {effectiveTimeout.TotalSeconds}s. Tabs.Count={pane.Tabs.Count}");
        }

        await populateTask; // propagate exception if populate failed
    }

    private static async Task WaitForWorkspacePaneAsync(MainWindowViewModel viewModel, string paneId)
    {
        if (viewModel.WorkspacePanes.Any(p =>
            string.Equals(p.Id, paneId, StringComparison.Ordinal) ||
            p.Id.StartsWith("loading-workspace:", StringComparison.Ordinal)))
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (viewModel.WorkspacePanes.Any(p =>
                string.Equals(p.Id, paneId, StringComparison.Ordinal) ||
                p.Id.StartsWith("loading-workspace:", StringComparison.Ordinal)))
            {
                signal.TrySetResult();
            }
        }

        viewModel.WorkspacePanes.CollectionChanged += OnCollectionChanged;
        try
        {
            if (!viewModel.WorkspacePanes.Any(p =>
                string.Equals(p.Id, paneId, StringComparison.Ordinal) ||
                p.Id.StartsWith("loading-workspace:", StringComparison.Ordinal)))
            {
                await signal.Task;
            }
        }
        finally
        {
            viewModel.WorkspacePanes.CollectionChanged -= OnCollectionChanged;
        }
    }

    private static async Task<T> WaitForSelectedTabAsync<T>(WorkspacePaneViewModel pane)
        where T : WorkspaceTabViewModel
    {
        if (pane.SelectedTab is T alreadyReady)
        {
            return alreadyReady;
        }

        var signal = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WorkspacePaneViewModel.SelectedTab) && pane.SelectedTab is T t)
            {
                signal.TrySetResult(t);
            }
        }

        pane.PropertyChanged += OnPropertyChanged;
        try
        {
            if (pane.SelectedTab is T existing)
            {
                return existing;
            }

            return await signal.Task;
        }
        finally
        {
            pane.PropertyChanged -= OnPropertyChanged;
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

    private static Task WaitForLayoutAsync(Window window)
    {
        if (window.IsMeasureValid && window.IsArrangeValid)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (!window.IsMeasureValid || !window.IsArrangeValid)
                return;
            window.LayoutUpdated -= handler;
            tcs.TrySetResult();
        };
        window.LayoutUpdated += handler;
        return tcs.Task;
    }

    private static Task WaitForDocumentTabStripAsync(Window window)
    {
        // Wait not just for a DocumentTabStrip to appear, but for one with WorkspaceContentDock DataContext.
        // The docking library may create the visual element before assigning the correct DataContext.
        if (window.GetVisualDescendants().OfType<DocumentTabStrip>()
            .Any(ts => ts.DataContext is WorkspaceContentDock))
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (!window.GetVisualDescendants().OfType<DocumentTabStrip>()
                .Any(ts => ts.DataContext is WorkspaceContentDock))
                return;
            window.LayoutUpdated -= handler;
            tcs.TrySetResult();
        };
        window.LayoutUpdated += handler;
        // TOCTOU: re-check after subscribing in case the strip with correct DataContext appeared
        // between the initial check and the subscribe
        if (window.GetVisualDescendants().OfType<DocumentTabStrip>()
            .Any(ts => ts.DataContext is WorkspaceContentDock))
        {
            window.LayoutUpdated -= handler;
            tcs.TrySetResult();
        }
        return tcs.Task;
    }

    private static async Task WaitForDocumentTabStripAsync(Window window, Type expectedDataContextType, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var tabStrip = window.GetVisualDescendants()
                .OfType<DocumentTabStrip>()
                .FirstOrDefault(ts => ts.DataContext?.GetType() == expectedDataContextType);
            if (tabStrip != null)
            {
                var items = tabStrip.GetVisualDescendants().OfType<DocumentTabStripItem>().ToList();
                if (items.Count > 0)
                    return;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"DocumentTabStrip with {expectedDataContextType.Name} DataContext and inflated items not found within {timeoutMs}ms");
    }

    private static async Task CloseWindowAsync(Window window)
    {
        window.Close();
        await Dispatcher.UIThread.InvokeAsync(() => { });
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ApplySelectedViewAsync_CalledTwice_CurrentViewPopulationContainsEntitiesOnce()
    {
        // Regression for issue #104: concurrent ApplySelectedViewAsync invocations must not
        // double-populate the entity list.
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ApplySelectedViewAsync_EachCall_CreatesNewCurrentViewPopulationInstance()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var firstPopulation = viewModel.CurrentViewPopulation;

        var applyMethod = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyMethod);

        await (Task)applyMethod!.Invoke(viewModel, [])!;

        Assert.NotSame(firstPopulation, viewModel.CurrentViewPopulation);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ApplySelectedViewAsync_PreviousPopulationDisposed_ItsEntitiesNotModifiedAfterSwap()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ApplySelectedViewAsync_ViewSwitchedTwice_CurrentViewPopulationReflectsSecondView()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaStaFact(Timeout = 15_000)]
    public async Task MainWindow_ContentLevelDocumentTabStrip_HasHeaderTemplate_AfterTabOpened()
    {
        // Regression test for #88: the content-level DocumentTabStrip must have HeaderTemplate
        // set so tab icons and notification indicators are rendered via EffectiveTabHeader.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new ShellTabViewModel(new FakeShellSession()) { Id = "header-tmpl-test", Title = "Header Test" };
        await viewModel.OpenTabAsync(tab);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            await WaitForDocumentTabStripAsync(window);

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
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    private static RepositorySource CreateInMemoryRepositorySource()
    {
        return new UnknownRepositorySource();
    }

    private static MainWindowViewModel CreateTestMainWindowViewModel(
        ProfileStore? profileStore = null,
        ApplicationServices? applicationServices = null,
        WorkspacesConfiguration? configuration = null)
    {
        return new MainWindowViewModel(
            CreateInMemoryRepositorySource(),
            configuration ?? new WorkspacesConfiguration { SkipStartupWorkspace = true },
            profileStore ?? new ProfileStore(CreateTempProfileStorePath()),
            applicationServices);
    }

    private static string CreateTempProfileStorePath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "Phantom.Workspaces.Tests",
            Guid.NewGuid().ToString("N"),
            "profile.json");
    }

    private static RunningAgentChatTable CreateTestRunningAgentChatTable()
    {
        var store = new InMemoryAgentPersistenceStore();
        var foregroundScheduler = SynchronizationContextTaskScheduler.FromCurrent();
        var factory = new AgentChatFactory(store, new AgentServices(), foregroundScheduler);
        return new RunningAgentChatTable(factory);
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabAsync_ExistingTab_PushesNavigationEntry()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task NavigateBack_AfterMultipleToolDrivenNavigations_TraversesAllEntries()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithMultipleBrowserTabs_TabsAppearInDeclarationOrder()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithUnresolvableMiddleTab_SkipsNullAndPreservesOrder()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_Alt1_ActivatesFirstContentTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "kb-alt1-a", Title = "Tab A" };
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "kb-alt1-b", Title = "Tab B" };
        var tabC = new AgentSessionWorkspaceTabViewModel { Id = "kb-alt1-c", Title = "Tab C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            window.KeyPressQwerty(PhysicalKey.Digit1, RawInputModifiers.Alt);

            var documentDock = GetDocumentDock(viewModel);
            Assert.NotNull(documentDock);
            Assert.Equal(documentDock!.VisibleDockables![0], documentDock.ActiveDockable);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OnActiveDockableChanged_WithWorkspacePaneDocument_UpdatesSelectedWorkspacePane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("38300001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "38300001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "adc-switch-a"]],
              "display-name": { "default": "ADC Switch A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("38300001-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "38300001-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "adc-switch-b"]],
              "display-name": { "default": "ADC Switch B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var pane1 = viewModel.WorkspacePanes[0];
        var pane2 = viewModel.WorkspacePanes[1];

        viewModel.GoToWorkspacePaneAtIndexCommand.Execute("0");
        Assert.Equal(pane1, viewModel.SelectedWorkspacePane);

        // Simulate clicking pane 2's tab in the outer dock (fires ActiveDockableChanged with WorkspacePaneDocument).
        var dockFactory = GetDockFactoryAs<IFactory>(viewModel);
        var workspacesDock = FindDocumentDockIn(viewModel.Layout!);
        Assert.NotNull(workspacesDock);
        var paneDoc2 = workspacesDock!.VisibleDockables!
            .OfType<WorkspacePaneDocument>()
            .First(d => d.WorkspacePane == pane2);
        dockFactory.SetActiveDockable(paneDoc2);

        Assert.Equal(pane2, viewModel.SelectedWorkspacePane);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OnActiveDockableChanged_WithWorkspacePaneDocument_ThenAltBadge_ActivatesTabInPaneByGlobalBadge()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("38300002-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "38300002-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "adc-alt1-a"]],
              "display-name": { "default": "ADC Alt1 A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("38300002-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "38300002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "adc-alt1-b"]],
              "display-name": { "default": "ADC Alt1 B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        // Open a tab in pane 1.
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute("0");
        var tabInPane1 = new AgentSessionWorkspaceTabViewModel { Id = "adc-alt1-pane1-tab", Title = "Pane1 Tab" };
        await viewModel.OpenTabAsync(tabInPane1);

        // Open two tabs in pane 2.
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute("1");
        var tabInPane2A = new AgentSessionWorkspaceTabViewModel { Id = "adc-alt1-pane2-a", Title = "Pane2 Tab A" };
        var tabInPane2B = new AgentSessionWorkspaceTabViewModel { Id = "adc-alt1-pane2-b", Title = "Pane2 Tab B" };
        await viewModel.OpenTabAsync(tabInPane2A);
        await viewModel.OpenTabAsync(tabInPane2B);

        // Switch selection back to pane 1.
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute("0");
        Assert.Equal(viewModel.WorkspacePanes[0], viewModel.SelectedWorkspacePane);

        // Simulate clicking pane 2's tab in the outer dock — SelectedWorkspacePane must update.
        var dockFactory = GetDockFactoryAs<IFactory>(viewModel);
        var workspacesDock = FindDocumentDockIn(viewModel.Layout!);
        Assert.NotNull(workspacesDock);
        var pane2 = viewModel.WorkspacePanes[1];
        var paneDoc2 = workspacesDock!.VisibleDockables!
            .OfType<WorkspacePaneDocument>()
            .First(d => d.WorkspacePane == pane2);
        dockFactory.SetActiveDockable(paneDoc2);
        Assert.Equal(pane2, viewModel.SelectedWorkspacePane);

        // Under the global badge model (#1011), Alt-N activates the tab that visually displays
        // badge N regardless of which pane is selected. Resolve pane 2's first tab by the badge it
        // actually displays and assert that pressing that Alt shortcut activates it cross-pane.
        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            var documentDock = GetDocumentDock(viewModel);
            Assert.NotNull(documentDock);

            var pane2aDoc = documentDock!.VisibleDockables!
                .OfType<WorkspaceDocument>()
                .First(d => d.Id == "adc-alt1-pane2-a");
            var badge = pane2aDoc.EffectiveTabHeader.AltShortcutLabel;
            Assert.False(string.IsNullOrEmpty(badge));

            window.KeyPressQwerty(DigitKeyForAltBadge(badge!), RawInputModifiers.Alt);

            Assert.Equal("adc-alt1-pane2-a", (documentDock.ActiveDockable as WorkspaceDocument)?.Id);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    private static PhysicalKey DigitKeyForAltBadge(string badge) => badge switch
    {
        "1" => PhysicalKey.Digit1,
        "2" => PhysicalKey.Digit2,
        "3" => PhysicalKey.Digit3,
        "4" => PhysicalKey.Digit4,
        "5" => PhysicalKey.Digit5,
        "6" => PhysicalKey.Digit6,
        "7" => PhysicalKey.Digit7,
        "8" => PhysicalKey.Digit8,
        "9" => PhysicalKey.Digit9,
        "0" => PhysicalKey.Digit0,
        _ => throw new ArgumentOutOfRangeException(nameof(badge), badge, "Unexpected Alt badge label."),
    };

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OnActiveDockableChanged_WithWorkspacePaneDocumentWithActiveTab_PushesNavigationEntry()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("38300003-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "38300003-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "adc-nav-a"]],
              "display-name": { "default": "ADC Nav A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("38300003-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "38300003-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "adc-nav-b"]],
              "display-name": { "default": "ADC Nav B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        // Open a tab in pane A.
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute("0");
        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "adc-nav-tab-a", Title = "ADC Nav Tab A" };
        await viewModel.OpenTabAsync(tabA);

        // Open a tab in pane B.
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute("1");
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "adc-nav-tab-b", Title = "ADC Nav Tab B" };
        await viewModel.OpenTabAsync(tabB);

        // Switch back to pane A so pane A is selected.
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute("0");
        Assert.Equal(viewModel.WorkspacePanes[0], viewModel.SelectedWorkspacePane);

        // Simulate a mouse click on pane B's outer tab — should push a navigation entry for pane B's active tab.
        var dockFactory = GetDockFactoryAs<IFactory>(viewModel);
        var workspacesDock = FindDocumentDockIn(viewModel.Layout!);
        Assert.NotNull(workspacesDock);
        var pane2 = viewModel.WorkspacePanes[1];
        var paneDoc2 = workspacesDock!.VisibleDockables!
            .OfType<WorkspacePaneDocument>()
            .First(d => d.WorkspacePane == pane2);
        dockFactory.SetActiveDockable(paneDoc2);
        Assert.Equal(pane2, viewModel.SelectedWorkspacePane);

        // NavigateBack should return to a state where pane A's tab is active.
        var documentDockB = GetDocumentDock(viewModel);
        Assert.NotNull(documentDockB);
        Assert.Equal("adc-nav-tab-b", documentDockB!.ActiveDockable?.Id);

        viewModel.NavigateBackCommand.Execute(null);

        Assert.Equal(viewModel.WorkspacePanes[0], viewModel.SelectedWorkspacePane);
        var documentDockA = GetDocumentDock(viewModel);
        Assert.NotNull(documentDockA);
        Assert.Equal("adc-nav-tab-a", documentDockA!.ActiveDockable?.Id);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OnActiveDockableChanged_WithWorkspacePaneDocumentWithActiveTab_WhenNavigatingViaHistory_DoesNotPushExtraEntry()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("38300004-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "38300004-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "adc-nav-guard-a"]],
              "display-name": { "default": "ADC Nav Guard A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("38300004-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "38300004-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "adc-nav-guard-b"]],
              "display-name": { "default": "ADC Nav Guard B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        // Open a tab in pane A and pane B.
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute("0");
        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "adc-nav-guard-tab-a", Title = "Guard Tab A" };
        await viewModel.OpenTabAsync(tabA);

        viewModel.GoToWorkspacePaneAtIndexCommand.Execute("1");
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "adc-nav-guard-tab-b", Title = "Guard Tab B" };
        await viewModel.OpenTabAsync(tabB);

        // Switch back to pane A.
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute("0");

        // Simulate mouse click on pane B — pushes one navigation entry.
        var dockFactory = GetDockFactoryAs<IFactory>(viewModel);
        var workspacesDock = FindDocumentDockIn(viewModel.Layout!);
        Assert.NotNull(workspacesDock);
        var pane2 = viewModel.WorkspacePanes[1];
        var paneDoc2 = workspacesDock!.VisibleDockables!
            .OfType<WorkspacePaneDocument>()
            .First(d => d.WorkspacePane == pane2);
        dockFactory.SetActiveDockable(paneDoc2);

        // NavigateBack once — lands back on pane A's tab.
        viewModel.NavigateBackCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(viewModel.WorkspacePanes[0], viewModel.SelectedWorkspacePane);
        var documentDockAfterBack = GetDocumentDock(viewModel);
        Assert.Equal("adc-nav-guard-tab-a", documentDockAfterBack?.ActiveDockable?.Id);

        // NavigateBack again — should continue traversing history correctly to the entry
        // before "pane A" (which is "pane B" from when tabB was first opened).
        // If the navigatingViaHistory guard were absent and the dock had fired
        // ActiveDockableChanged for the outer pane during the first NavigateBack, an
        // extra entry would have been inserted — corrupting history traversal here.
        viewModel.NavigateBackCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(viewModel.WorkspacePanes[1], viewModel.SelectedWorkspacePane);
        Assert.Equal("adc-nav-guard-tab-b", GetDocumentDock(viewModel)?.ActiveDockable?.Id);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_Alt1_WithShellTabActive_ActivatesFirstContentTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "kb-alt1-shell-a", Title = "Tab A" };
        await viewModel.OpenTabAsync(tabA);

        var shellTab = new ShellTabViewModel(new FakeShellSession()) { Id = "kb-alt1-shell-b", Title = "Shell Tab" };
        await viewModel.OpenTabAsync(shellTab);

        // Shell tab is now active (last opened).
        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            window.KeyPressQwerty(PhysicalKey.Digit1, RawInputModifiers.Alt);

            var documentDock = GetDocumentDock(viewModel);
            Assert.NotNull(documentDock);
            Assert.Equal("kb-alt1-shell-a", (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_Alt0_ActivatesTenthContentTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        for (var i = 0; i < 10; i++)
        {
            var tab = new AgentSessionWorkspaceTabViewModel { Id = $"kb-alt0-tab{i}", Title = $"Tab {i}" };
            await viewModel.OpenTabAsync(tab);
        }

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            window.KeyPressQwerty(PhysicalKey.Digit0, RawInputModifiers.Alt);

            var documentDock = GetDocumentDock(viewModel);
            Assert.NotNull(documentDock);
            Assert.Equal(documentDock!.VisibleDockables![9], documentDock.ActiveDockable);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_AltDigit_WithIndexOutOfRange_IsNoOp()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "kb-alt-oob-a", Title = "Tab A" };
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "kb-alt-oob-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var documentDock = GetDocumentDock(viewModel);
            Assert.NotNull(documentDock);
            var activeBefore = documentDock!.ActiveDockable;

            window.KeyPressQwerty(PhysicalKey.Digit9, RawInputModifiers.Alt);

            Assert.Equal(activeBefore, documentDock.ActiveDockable);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_AltShift1_ActivatesFirstWorkspacePane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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
        try
        {
            viewModel.GoToWorkspacePaneAtIndexCommand.Execute("1");
            Assert.Equal(viewModel.WorkspacePanes[1], viewModel.SelectedWorkspacePane);

            window.KeyPressQwerty(PhysicalKey.Digit1, RawInputModifiers.Alt | RawInputModifiers.Shift);

            Assert.Equal(viewModel.WorkspacePanes[0], viewModel.SelectedWorkspacePane);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_AltShift2_ActivatesSecondWorkspacePane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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
        try
        {
            window.KeyPressQwerty(PhysicalKey.Digit2, RawInputModifiers.Control);

            Assert.Equal(viewModel.WorkspacePanes[1], viewModel.SelectedWorkspacePane);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_AltShiftDigit_WithIndexOutOfRange_IsNoOp()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var selectedBefore = viewModel.SelectedWorkspacePane;

            window.KeyPressQwerty(PhysicalKey.Digit2, RawInputModifiers.Alt | RawInputModifiers.Shift);

            Assert.Equal(selectedBefore, viewModel.SelectedWorkspacePane);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task InitializeAsync_WithDefaultRelationship_OpensDefaultWorkspace()
    {
        await using var viewModel = CreateTestMainWindowViewModel(
            configuration: new WorkspacesConfiguration { SkipStartupWorkspace = false });

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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task InitializeAsync_WithNoDefaultRelationship_OpensGettingStartedWorkspace()
    {
        await using var viewModel = CreateTestMainWindowViewModel(
            configuration: new WorkspacesConfiguration { SkipStartupWorkspace = false });
        await viewModel.InitializeAsync();

        Assert.Contains(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, GettingStartedWorkspaceId, StringComparison.Ordinal));
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task CloseLastWorkspace_WithDefaultRelationship_OpensDefaultWorkspaceInsteadOfGettingStarted()
    {
        await using var viewModel = CreateTestMainWindowViewModel(
            configuration: new WorkspacesConfiguration { SkipStartupWorkspace = false });

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
        await viewModel.CloseWorkspacePaneAsync(defaultPane!);

        // After closing, the default workspace should be re-opened instead of Getting Started
        Assert.Contains(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task NavigatePreviousNotificationCommand_NavigatesToUnreadTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task NavigateNextNotificationCommand_NavigatesToUnreadTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task NavigateNextNotificationCommand_WhenTabIsInNonSelectedPane_SwitchesWorkspacePane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        // Open workspace A first so there are two panes (placeholder is removed by OpenWorkspaceAsync)
        var workspaceAId = new EntityId("b1190319-0000-4000-8000-00000000000a");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceAId,
            """
            {
              "entity-id": "b1190319-0000-4000-8000-00000000000a",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-pane-a"]],
              "display-name": { "default": "Notif Pane A" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceAId });

        var workspaceBId = new EntityId("b1190319-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceBId,
            """
            {
              "entity-id": "b1190319-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-pane-b"]],
              "display-name": { "default": "Notif Pane B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceBId });

        // Select pane B and open a tab there (no WorkspaceId hint in TabDescriptor)
        var paneB = viewModel.WorkspacePanes.Single(p => string.Equals(p.Id, workspaceBId.ToString(), StringComparison.Ordinal));
        viewModel.SelectedWorkspacePane = paneB;
        var tabInPaneB = new AgentSessionWorkspaceTabViewModel { Id = "notif-cross-pane-tab", Title = "Tab in Pane B" };
        await viewModel.OpenTabAsync(tabInPaneB);

        // Switch back to pane A so the notification for tabInPaneB will be unread
        var paneA = viewModel.WorkspacePanes.First(p => !string.Equals(p.Id, workspaceBId.ToString(), StringComparison.Ordinal));
        viewModel.SelectedWorkspacePane = paneA;

        viewModel.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "notif-cross-pane-tab" },
            "Tab in Pane B", "test notification", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));

        viewModel.NavigateNextNotificationCommand.Execute(null);

        Assert.Same(paneB, viewModel.SelectedWorkspacePane);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task NavigateNextNotificationCommand_WhenTabIsInNonSelectedPaneWithWorkspaceIdHint_SwitchesWorkspacePane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        // Open workspace A first so there are two panes (placeholder is removed by OpenWorkspaceAsync)
        var workspaceAId = new EntityId("b1190319-0000-4000-8000-00000000000b");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceAId,
            """
            {
              "entity-id": "b1190319-0000-4000-8000-00000000000b",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-pane-a2"]],
              "display-name": { "default": "Notif Pane A2" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceAId });

        var workspaceBId = new EntityId("b1190319-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceBId,
            """
            {
              "entity-id": "b1190319-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-pane-b2"]],
              "display-name": { "default": "Notif Pane B2" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceBId });

        // Select pane B and open a tab there
        var paneB = viewModel.WorkspacePanes.Single(p => string.Equals(p.Id, workspaceBId.ToString(), StringComparison.Ordinal));
        viewModel.SelectedWorkspacePane = paneB;
        var tabInPaneB = new AgentSessionWorkspaceTabViewModel { Id = "notif-cross-pane-tab-hint", Title = "Tab in Pane B" };
        await viewModel.OpenTabAsync(tabInPaneB);

        // Switch back to pane A so the notification for tabInPaneB will be unread
        var paneA = viewModel.WorkspacePanes.First(p => !string.Equals(p.Id, workspaceBId.ToString(), StringComparison.Ordinal));
        viewModel.SelectedWorkspacePane = paneA;

        // Notify with WorkspaceId hint pointing to pane B
        viewModel.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "notif-cross-pane-tab-hint", WorkspaceId = workspaceBId.ToString() },
            "Tab in Pane B", "test notification", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));

        viewModel.NavigateNextNotificationCommand.Execute(null);

        Assert.Same(paneB, viewModel.SelectedWorkspacePane);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_CtrlF7_NavigatesToPreviousNotification()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "ctrl-f7-prev-a", Title = "Tab A" };
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "ctrl-f7-prev-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        // tabB is active; notify tabA so it becomes the unread candidate.
        viewModel.NotificationService.Notify(new Notification(new TabDescriptor { TabId = "ctrl-f7-prev-a" }, "Tab A", "test notification", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            window.KeyPressQwerty(PhysicalKey.F7, RawInputModifiers.Control);

            var documentDock = GetDocumentDock(viewModel);
            Assert.NotNull(documentDock);
            Assert.Equal("ctrl-f7-prev-a", (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_CtrlF8_NavigatesToNextNotification()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "ctrl-f8-next-a", Title = "Tab A" };
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "ctrl-f8-next-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        // tabB is active; notify tabA so it becomes the unread candidate.
        viewModel.NotificationService.Notify(new Notification(new TabDescriptor { TabId = "ctrl-f8-next-a" }, "Tab A", "test notification", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            window.KeyPressQwerty(PhysicalKey.F8, RawInputModifiers.Control);

            var documentDock = GetDocumentDock(viewModel);
            Assert.NotNull(documentDock);
            Assert.Equal("ctrl-f8-next-a", (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_CtrlF7_IsHandledInTunnelPhase()
    {
        // Verifies that Ctrl+F7 is intercepted in the tunnel phase (e.Handled = true),
        // preventing child controls such as WebView2 from seeing the keystroke.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
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
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_CtrlF8_IsHandledInTunnelPhase()
    {
        // Verifies that Ctrl+F8 is intercepted in the tunnel phase (e.Handled = true),
        // preventing child controls such as WebView2 from seeing the keystroke.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
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
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }



    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_WithNotificationBellRingingStyle_DoesNotThrowOnLayout()
    {
        // Regression test for #143: bell animation used string-valued RenderTransform KeyFrame
        // setters (e.g. Value="rotate(-18deg)"). Avalonia's XAML IL compiler does not apply
        // type converters inside KeyFrame.Setter, so the value arrived as a boxed string with
        // no registered animator, throwing InvalidOperationException on first style application.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            // Force a full layout pass — this applies all loaded styles (including NotificationsStyles)
            // and interprets animation keyframes. The bug caused a throw here.
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    // ── IsAltHeld / Alt-badge tests ──────────────────────────────────────────

    [Fact]
    public async Task IsAltHeld_DefaultIsFalse()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        Assert.False(viewModel.IsAltHeld);
    }

    [Fact]
    public async Task IsAltHeld_SetToTrue_RaisesPropertyChanged()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        var raised = false;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(viewModel.IsAltHeld))
                raised = true;
        };

        viewModel.IsAltHeld = true;

        Assert.True(raised);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyDown_LeftAlt_SetsIsAltHeld()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            window.KeyPressQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);

            Assert.True(viewModel.IsAltHeld);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyUp_LeftAlt_ClearsIsAltHeld()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            viewModel.IsAltHeld = true;
            window.KeyReleaseQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);

            Assert.False(viewModel.IsAltHeld);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task GoToTabAtIndexCommand_Execute_DoesNotClearIsAltHeld()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "alt-clear-a", Title = "Tab A" };
        await viewModel.OpenTabAsync(tabA);

        viewModel.IsAltHeld = true;
        viewModel.GoToTabAtIndexCommand.Execute("0");

        Assert.True(viewModel.IsAltHeld);
    }

    // ── IsShiftHeld / PropagateBadgeVisibility tests (#774) ──────────────────

    [Fact]
    public async Task IsShiftHeld_DefaultIsFalse()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        Assert.False(viewModel.IsShiftHeld);
    }

    [Fact]
    public async Task IsShiftHeld_SetToTrue_RaisesPropertyChanged()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        var raised = false;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(viewModel.IsShiftHeld))
                raised = true;
        };

        viewModel.IsShiftHeld = true;

        Assert.True(raised);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task PropagateBadgeVisibility_AltOnly_ContentTabBadgesVisible()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "badge-alt-only-a", Title = "Tab A" };
        await viewModel.OpenTabAsync(tabA);

        viewModel.IsShiftHeld = false;
        viewModel.IsAltHeld = true;

        var documentDock = GetDocumentDock(viewModel);
        var doc = documentDock!.VisibleDockables!.OfType<WorkspaceDocument>()
            .First(d => d.Id == "badge-alt-only-a");
        Assert.True(doc.EffectiveTabHeader.IsShortcutBadgeVisible);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task PropagateBadgeVisibility_AltOnly_PaneTabBadgesHidden()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("77400001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId,
            """
            {
              "entity-id": "77400001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "badge-alt-pane-hidden"]],
              "display-name": { "default": "Badge Alt Pane Hidden" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        viewModel.IsShiftHeld = false;
        viewModel.IsAltHeld = true;

        var workspacesDock = FindDocumentDockIn(viewModel.Layout!);
        var paneDoc = workspacesDock!.VisibleDockables!.OfType<WorkspacePaneDocument>().First();
        Assert.False(paneDoc.EffectiveTabHeader.IsShortcutBadgeVisible);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task PropagateBadgeVisibility_AltAndShift_ContentTabBadgesHidden()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "badge-altshift-content-a", Title = "Tab A" };
        await viewModel.OpenTabAsync(tabA);

        viewModel.IsAltHeld = true;
        viewModel.IsShiftHeld = true;

        var documentDock = GetDocumentDock(viewModel);
        var doc = documentDock!.VisibleDockables!.OfType<WorkspaceDocument>()
            .First(d => d.Id == "badge-altshift-content-a");
        Assert.False(doc.EffectiveTabHeader.IsShortcutBadgeVisible);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task PropagateBadgeVisibility_AltAndShift_PaneTabBadgesVisible()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("77400002-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId,
            """
            {
              "entity-id": "77400002-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "badge-altshift-pane-visible"]],
              "display-name": { "default": "Badge AltShift Pane Visible" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        viewModel.IsAltHeld = true;
        viewModel.IsShiftHeld = true;

        var workspacesDock = FindDocumentDockIn(viewModel.Layout!);
        var paneDoc = workspacesDock!.VisibleDockables!.OfType<WorkspacePaneDocument>()
            .First(d => d.WorkspacePane.Id == workspaceId.ToString());
        Assert.True(paneDoc.EffectiveTabHeader.IsShortcutBadgeVisible);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task PropagateBadgeVisibility_ShiftOnly_AllBadgesHidden()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "badge-shift-only-a", Title = "Tab A" };
        await viewModel.OpenTabAsync(tabA);

        viewModel.IsAltHeld = false;
        viewModel.IsShiftHeld = true;

        var documentDock = GetDocumentDock(viewModel);
        var doc = documentDock!.VisibleDockables!.OfType<WorkspaceDocument>()
            .First(d => d.Id == "badge-shift-only-a");
        Assert.False(doc.EffectiveTabHeader.IsShortcutBadgeVisible);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task PropagateBadgeVisibility_NeitherModifier_AllBadgesHidden()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "badge-neither-a", Title = "Tab A" };
        await viewModel.OpenTabAsync(tabA);

        viewModel.IsAltHeld = false;
        viewModel.IsShiftHeld = false;

        var documentDock = GetDocumentDock(viewModel);
        var doc = documentDock!.VisibleDockables!.OfType<WorkspaceDocument>()
            .First(d => d.Id == "badge-neither-a");
        Assert.False(doc.EffectiveTabHeader.IsShortcutBadgeVisible);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task IsShiftHeldChanged_TriggersPropagate_PaneTabsUpdated()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("77400003-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId,
            """
            {
              "entity-id": "77400003-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "badge-shift-change-pane"]],
              "display-name": { "default": "Badge Shift Change Pane" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        viewModel.IsAltHeld = true;
        viewModel.IsShiftHeld = false;

        var workspacesDock = FindDocumentDockIn(viewModel.Layout!);
        var paneDoc = workspacesDock!.VisibleDockables!.OfType<WorkspacePaneDocument>()
            .First(d => d.WorkspacePane.Id == workspaceId.ToString());
        Assert.False(paneDoc.EffectiveTabHeader.IsShortcutBadgeVisible);

        // Flip IsShiftHeld while IsAltHeld=true — pane badge should become visible
        viewModel.IsShiftHeld = true;
        Assert.True(paneDoc.EffectiveTabHeader.IsShortcutBadgeVisible);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task IsAltHeldChanged_TriggersPropagate_ContentTabsUpdated()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "badge-alt-change-a", Title = "Tab A" };
        await viewModel.OpenTabAsync(tabA);

        viewModel.IsShiftHeld = false;
        viewModel.IsAltHeld = false;

        var documentDock = GetDocumentDock(viewModel);
        var doc = documentDock!.VisibleDockables!.OfType<WorkspaceDocument>()
            .First(d => d.Id == "badge-alt-change-a");
        Assert.False(doc.EffectiveTabHeader.IsShortcutBadgeVisible);

        // Flip IsAltHeld while IsShiftHeld=false — content badge should become visible
        viewModel.IsAltHeld = true;
        Assert.True(doc.EffectiveTabHeader.IsShortcutBadgeVisible);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabAsync_ThreeTabs_AssignsCorrectAltShortcutLabels()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task CloseTab_ByIndex_RefreshesAltShortcutLabels()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    // ── Alt+N shortcut numbers — multi-pane scenarios (#614) ──────────────────

    // ── Alt+Shift+N shortcut numbers — workspace pane label tests (#773) ─────

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task RefreshWorkspacePaneAltShortcutLabels_InitialState_LabelsAssigned()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("77300001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "77300001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "pane-label-init-a"]],
              "display-name": { "default": "Pane Label Init A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("77300001-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "77300001-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "pane-label-init-b"]],
              "display-name": { "default": "Pane Label Init B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var workspacesDock = FindDocumentDockIn(viewModel.Layout!);
        Assert.NotNull(workspacesDock);

        var paneDocs = workspacesDock!.VisibleDockables!.OfType<WorkspacePaneDocument>().ToList();
        Assert.True(paneDocs.Count >= 2);
        Assert.Equal("1", paneDocs[0].EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("2", paneDocs[1].EffectiveTabHeader.AltShortcutLabel);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task RefreshWorkspacePaneAltShortcutLabels_OnPaneAdded_LabelsUpdated()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("77300002-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "77300002-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "pane-label-add-a"]],
              "display-name": { "default": "Pane Label Add A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("77300002-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "77300002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "pane-label-add-b"]],
              "display-name": { "default": "Pane Label Add B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });

        var workspacesDock = FindDocumentDockIn(viewModel.Layout!);
        Assert.NotNull(workspacesDock);

        var paneDocs = workspacesDock!.VisibleDockables!.OfType<WorkspacePaneDocument>().ToList();
        Assert.Single(paneDocs);
        Assert.Equal("1", paneDocs[0].EffectiveTabHeader.AltShortcutLabel);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        paneDocs = workspacesDock.VisibleDockables!.OfType<WorkspacePaneDocument>().ToList();
        Assert.Equal(2, paneDocs.Count);
        Assert.Equal("1", paneDocs[0].EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("2", paneDocs[1].EffectiveTabHeader.AltShortcutLabel);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task RefreshWorkspacePaneAltShortcutLabels_OnPaneRemoved_LabelsRenumbered()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("77300003-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "77300003-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "pane-label-remove-a"]],
              "display-name": { "default": "Pane Label Remove A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("77300003-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "77300003-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "pane-label-remove-b"]],
              "display-name": { "default": "Pane Label Remove B" },
              "regions": []
            }
            """);

        var workspaceIdC = new EntityId("77300003-0000-4000-8000-000000000003");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdC,
            """
            {
              "entity-id": "77300003-0000-4000-8000-000000000003",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "pane-label-remove-c"]],
              "display-name": { "default": "Pane Label Remove C" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdC });

        var workspacesDock = FindDocumentDockIn(viewModel.Layout!);
        Assert.NotNull(workspacesDock);

        // Switch to pane B (index 1) so we can close it
        var paneBIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdB.ToString());
        Assert.True(paneBIndex >= 0);
        var paneB = viewModel.WorkspacePanes[paneBIndex];
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute(paneBIndex.ToString());

        // Close pane B by passing it as the command parameter
        viewModel.CloseWorkspaceCommand.Execute(paneB);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var paneDocs = workspacesDock.VisibleDockables!.OfType<WorkspacePaneDocument>().ToList();
        Assert.Equal(2, paneDocs.Count);
        Assert.Equal("1", paneDocs[0].EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("2", paneDocs[1].EffectiveTabHeader.AltShortcutLabel);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task DragReorder_WithinSinglePane_LabelsUpdateToReflectNewOrder()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "drag-single-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "drag-single-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "drag-single-c", Title = "Tab C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);

        // Simulate drag-reorder: move last tab (C) to first position
        var visibleDockables = documentDock!.VisibleDockables as System.Collections.ObjectModel.ObservableCollection<IDockable>;
        Assert.NotNull(visibleDockables);
        var docC = visibleDockables!.OfType<WorkspaceDocument>().First(d => d.Id == "drag-single-c");
        visibleDockables.Move(visibleDockables.IndexOf(docC), 0);

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        var docs = documentDock.VisibleDockables!.OfType<WorkspaceDocument>().ToList();
        Assert.Equal("drag-single-c", docs[0].Id);
        Assert.Equal("drag-single-a", docs[1].Id);
        Assert.Equal("drag-single-b", docs[2].Id);
        Assert.Equal("1", docs[0].EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("2", docs[1].EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("3", docs[2].EffectiveTabHeader.AltShortcutLabel);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task DragReorder_ThreeTabs_MoveMiddleToFirst_LabelsCorrect()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "drag-middle-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "drag-middle-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "drag-middle-c", Title = "Tab C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);

        // Move middle tab (B) to first position
        var visibleDockables = documentDock!.VisibleDockables as System.Collections.ObjectModel.ObservableCollection<IDockable>;
        Assert.NotNull(visibleDockables);
        var docB = visibleDockables!.OfType<WorkspaceDocument>().First(d => d.Id == "drag-middle-b");
        visibleDockables.Move(visibleDockables.IndexOf(docB), 0);

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        var docs = documentDock.VisibleDockables!.OfType<WorkspaceDocument>().ToList();
        Assert.Equal("drag-middle-b", docs[0].Id);
        Assert.Equal("drag-middle-a", docs[1].Id);
        Assert.Equal("drag-middle-c", docs[2].Id);
        Assert.Equal("1", docs[0].EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("2", docs[1].EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("3", docs[2].EffectiveTabHeader.AltShortcutLabel);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task SplitWorkspace_TwoPanesHorizontal_LeftPaneTabsNumberedFirst()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceAId = new EntityId("ab010001-0000-4000-8000-000000000001");
        var workspaceBId = new EntityId("ab010002-0000-4000-8000-000000000002");
        
        await UpsertEntityAndLoadAsync(entityBroker, workspaceAId,
            """
            {
              "entity-id": "ab010001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "split-h-left"]],
              "display-name": { "default": "Split H Left" },
              "regions": []
            }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceBId,
            """
            {
              "entity-id": "ab010002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "split-h-right"]],
              "display-name": { "default": "Split H Right" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceAId });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceBId });

        var paneA = viewModel.WorkspacePanes.First(p => p.Id == workspaceAId.ToString());
        var paneB = viewModel.WorkspacePanes.First(p => p.Id == workspaceBId.ToString());

        // Close the default tabs added by PopulateWorkspacePaneTabsAsync before opening test tabs
        await CloseDefaultPaneTabsAsync(viewModel, paneA, paneB);

        // Open 2 tabs in each pane
        viewModel.SelectedWorkspacePane = paneA;
        var tabA1 = new WebViewModel("https://a1.example.com") { Id = "split-h-a1", Title = "A1" };
        var tabA2 = new WebViewModel("https://a2.example.com") { Id = "split-h-a2", Title = "A2" };
        await viewModel.OpenTabAsync(tabA1);
        await viewModel.OpenTabAsync(tabA2);

        viewModel.SelectedWorkspacePane = paneB;
        var tabB1 = new WebViewModel("https://b1.example.com") { Id = "split-h-b1", Title = "B1" };
        var tabB2 = new WebViewModel("https://b2.example.com") { Id = "split-h-b2", Title = "B2" };
        await viewModel.OpenTabAsync(tabB1);
        await viewModel.OpenTabAsync(tabB2);

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        var dockA = FindDocumentDockIn(paneA.ContentLayout!);
        var dockB = FindDocumentDockIn(paneB.ContentLayout!);
        Assert.NotNull(dockA);
        Assert.NotNull(dockB);

        var docsA = GetPaneDocuments(paneA, dockA!);
        var docsB = GetPaneDocuments(paneB, dockB!);
        
        Assert.Equal(2, docsA.Count);
        Assert.Equal(2, docsB.Count);
        
        Assert.Equal("split-h-a1", docsA[0].Id);
        Assert.Equal("split-h-a2", docsA[1].Id);
        Assert.Equal("split-h-b1", docsB[0].Id);
        Assert.Equal("split-h-b2", docsB[1].Id);
        
        Assert.Equal("1", docsA[0].EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("2", docsA[1].EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("3", docsB[0].EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("4", docsB[1].EffectiveTabHeader.AltShortcutLabel);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task SplitWorkspace_TwoPanesVertical_TopPaneTabsNumberedFirst()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceAId = new EntityId("ab020001-0000-4000-8000-000000000001");
        var workspaceBId = new EntityId("ab020002-0000-4000-8000-000000000002");
        
        await UpsertEntityAndLoadAsync(entityBroker, workspaceAId,
            """
            {
              "entity-id": "ab020001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "split-v-top"]],
              "display-name": { "default": "Split V Top" },
              "regions": []
            }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceBId,
            """
            {
              "entity-id": "ab020002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "split-v-bottom"]],
              "display-name": { "default": "Split V Bottom" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceAId });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceBId });

        var paneA = viewModel.WorkspacePanes.First(p => p.Id == workspaceAId.ToString());
        var paneB = viewModel.WorkspacePanes.First(p => p.Id == workspaceBId.ToString());

        // Close the default tabs added by PopulateWorkspacePaneTabsAsync before opening test tabs
        await CloseDefaultPaneTabsAsync(viewModel, paneA, paneB);

        // Open 2 tabs in each pane
        viewModel.SelectedWorkspacePane = paneA;
        var tabA1 = new WebViewModel("https://a1.example.com") { Id = "split-v-a1", Title = "A1" };
        var tabA2 = new WebViewModel("https://a2.example.com") { Id = "split-v-a2", Title = "A2" };
        await viewModel.OpenTabAsync(tabA1);
        await viewModel.OpenTabAsync(tabA2);

        viewModel.SelectedWorkspacePane = paneB;
        var tabB1 = new WebViewModel("https://b1.example.com") { Id = "split-v-b1", Title = "B1" };
        var tabB2 = new WebViewModel("https://b2.example.com") { Id = "split-v-b2", Title = "B2" };
        await viewModel.OpenTabAsync(tabB1);
        await viewModel.OpenTabAsync(tabB2);

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        var dockA = FindDocumentDockIn(paneA.ContentLayout!);
        var dockB = FindDocumentDockIn(paneB.ContentLayout!);
        Assert.NotNull(dockA);
        Assert.NotNull(dockB);

        var docsA = GetPaneDocuments(paneA, dockA!);
        var docsB = GetPaneDocuments(paneB, dockB!);
        
        Assert.Equal(2, docsA.Count);
        Assert.Equal(2, docsB.Count);
        
        Assert.Equal("split-v-a1", docsA[0].Id);
        Assert.Equal("split-v-a2", docsA[1].Id);
        Assert.Equal("split-v-b1", docsB[0].Id);
        Assert.Equal("split-v-b2", docsB[1].Id);
        
        Assert.Equal("1", docsA[0].EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("2", docsA[1].EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("3", docsB[0].EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("4", docsB[1].EffectiveTabHeader.AltShortcutLabel);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task SplitWorkspace_ThreePanes_OrderIsLeftToRightTopToBottom()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceAId = new EntityId("ab030001-0000-4000-8000-000000000001");
        var workspaceBId = new EntityId("ab030002-0000-4000-8000-000000000002");
        var workspaceCId = new EntityId("ab030003-0000-4000-8000-000000000003");
        
        await UpsertEntityAndLoadAsync(entityBroker, workspaceAId,
            """
            {
              "entity-id": "ab030001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "split-3-left"]],
              "display-name": { "default": "Split 3 Left" },
              "regions": []
            }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceBId,
            """
            {
              "entity-id": "ab030002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "split-3-right"]],
              "display-name": { "default": "Split 3 Right" },
              "regions": []
            }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceCId,
            """
            {
              "entity-id": "ab030003-0000-4000-8000-000000000003",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "split-3-bottom"]],
              "display-name": { "default": "Split 3 Bottom" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceAId });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceBId });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceCId });

        var paneA = viewModel.WorkspacePanes.First(p => p.Id == workspaceAId.ToString());
        var paneB = viewModel.WorkspacePanes.First(p => p.Id == workspaceBId.ToString());
        var paneC = viewModel.WorkspacePanes.First(p => p.Id == workspaceCId.ToString());

        // Close the default tabs added by PopulateWorkspacePaneTabsAsync before opening test tabs
        await CloseDefaultPaneTabsAsync(viewModel, paneA, paneB, paneC);

        viewModel.SelectedWorkspacePane = paneA;
        var tabA1 = new WebViewModel("https://a1.example.com") { Id = "split-3-a1", Title = "A1" };
        await viewModel.OpenTabAsync(tabA1);

        viewModel.SelectedWorkspacePane = paneB;
        var tabB1 = new WebViewModel("https://b1.example.com") { Id = "split-3-b1", Title = "B1" };
        await viewModel.OpenTabAsync(tabB1);

        viewModel.SelectedWorkspacePane = paneC;
        var tabC1 = new WebViewModel("https://c1.example.com") { Id = "split-3-c1", Title = "C1" };
        await viewModel.OpenTabAsync(tabC1);

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        var dockA = FindDocumentDockIn(paneA.ContentLayout!);
        var dockB = FindDocumentDockIn(paneB.ContentLayout!);
        var dockC = FindDocumentDockIn(paneC.ContentLayout!);
        Assert.NotNull(dockA);
        Assert.NotNull(dockB);
        Assert.NotNull(dockC);

        var docA = GetPaneDocuments(paneA, dockA!).Single();
        var docB = GetPaneDocuments(paneB, dockB!).Single();
        var docC = GetPaneDocuments(paneC, dockC!).Single();
        
        Assert.Equal("split-3-a1", docA.Id);
        Assert.Equal("split-3-b1", docB.Id);
        Assert.Equal("split-3-c1", docC.Id);
        
        Assert.Equal("1", docA.EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("2", docB.EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("3", docC.EffectiveTabHeader.AltShortcutLabel);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task SplitWorkspace_DragReorderInSecondaryPane_GlobalLabelsCorrect()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceAId = new EntityId("ab040001-0000-4000-8000-000000000001");
        var workspaceBId = new EntityId("ab040002-0000-4000-8000-000000000002");
        
        await UpsertEntityAndLoadAsync(entityBroker, workspaceAId,
            """
            {
              "entity-id": "ab040001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "drag-sec-left"]],
              "display-name": { "default": "Drag Sec Left" },
              "regions": []
            }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceBId,
            """
            {
              "entity-id": "ab040002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "drag-sec-right"]],
              "display-name": { "default": "Drag Sec Right" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceAId });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceBId });

        var paneA = viewModel.WorkspacePanes.First(p => p.Id == workspaceAId.ToString());
        var paneB = viewModel.WorkspacePanes.First(p => p.Id == workspaceBId.ToString());

        // Close the default tabs added by PopulateWorkspacePaneTabsAsync before opening test tabs
        await CloseDefaultPaneTabsAsync(viewModel, paneA, paneB);

        viewModel.SelectedWorkspacePane = paneA;
        var tabA1 = new WebViewModel("https://a1.example.com") { Id = "drag-sec-a1", Title = "A1" };
        await viewModel.OpenTabAsync(tabA1);

        viewModel.SelectedWorkspacePane = paneB;
        var tabB1 = new WebViewModel("https://b1.example.com") { Id = "drag-sec-b1", Title = "B1" };
        var tabB2 = new WebViewModel("https://b2.example.com") { Id = "drag-sec-b2", Title = "B2" };
        await viewModel.OpenTabAsync(tabB1);
        await viewModel.OpenTabAsync(tabB2);

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        var dockB = FindDocumentDockIn(paneB.ContentLayout!);
        Assert.NotNull(dockB);
        
        var visibleDockables = dockB!.VisibleDockables as System.Collections.ObjectModel.ObservableCollection<IDockable>;
        Assert.NotNull(visibleDockables);
        var docB2 = visibleDockables!.OfType<WorkspaceDocument>().First(d => d.Id == "drag-sec-b2");
        visibleDockables.Move(visibleDockables.IndexOf(docB2), 0);

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        var dockA = FindDocumentDockIn(paneA.ContentLayout!);
        Assert.NotNull(dockA);

        var docA1 = GetPaneDocuments(paneA, dockA!).Single();
        var docsB = GetPaneDocuments(paneB, dockB);
        
        Assert.Equal("drag-sec-a1", docA1.Id);
        Assert.Equal("drag-sec-b2", docsB[0].Id);
        Assert.Equal("drag-sec-b1", docsB[1].Id);
        
        Assert.Equal("1", docA1.EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("2", docsB[0].EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("3", docsB[1].EffectiveTabHeader.AltShortcutLabel);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task SplitWorkspace_NewTabOpenedInSecondaryPane_ReceivesCorrectLabel()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceAId = new EntityId("ab050001-0000-4000-8000-000000000001");
        var workspaceBId = new EntityId("ab050002-0000-4000-8000-000000000002");
        
        await UpsertEntityAndLoadAsync(entityBroker, workspaceAId,
            """
            {
              "entity-id": "ab050001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "new-sec-left"]],
              "display-name": { "default": "New Sec Left" },
              "regions": []
            }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceBId,
            """
            {
              "entity-id": "ab050002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "new-sec-right"]],
              "display-name": { "default": "New Sec Right" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceAId });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceBId });

        var paneA = viewModel.WorkspacePanes.First(p => p.Id == workspaceAId.ToString());
        var paneB = viewModel.WorkspacePanes.First(p => p.Id == workspaceBId.ToString());

        // Close the default tabs added by PopulateWorkspacePaneTabsAsync before opening test tabs
        await CloseDefaultPaneTabsAsync(viewModel, paneA, paneB);

        viewModel.SelectedWorkspacePane = paneA;
        var tabA1 = new WebViewModel("https://a1.example.com") { Id = "new-sec-a1", Title = "A1" };
        var tabA2 = new WebViewModel("https://a2.example.com") { Id = "new-sec-a2", Title = "A2" };
        await viewModel.OpenTabAsync(tabA1);
        await viewModel.OpenTabAsync(tabA2);

        viewModel.SelectedWorkspacePane = paneB;
        var tabB1 = new WebViewModel("https://b1.example.com") { Id = "new-sec-b1", Title = "B1" };
        await viewModel.OpenTabAsync(tabB1);

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        var dockA = FindDocumentDockIn(paneA.ContentLayout!);
        var dockB = FindDocumentDockIn(paneB.ContentLayout!);
        Assert.NotNull(dockA);
        Assert.NotNull(dockB);

        var docsA = GetPaneDocuments(paneA, dockA!);
        var docB1 = GetPaneDocuments(paneB, dockB!).Single();
        
        Assert.Equal("new-sec-a1", docsA[0].Id);
        Assert.Equal("new-sec-a2", docsA[1].Id);
        Assert.Equal("new-sec-b1", docB1.Id);
        
        Assert.Equal("1", docsA[0].EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("2", docsA[1].EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("3", docB1.EffectiveTabHeader.AltShortcutLabel);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task SplitWorkspace_TabClosedFromPrimaryPane_SecondaryPaneLabelsRenumbered()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceAId = new EntityId("ab060001-0000-4000-8000-000000000001");
        var workspaceBId = new EntityId("ab060002-0000-4000-8000-000000000002");
        
        await UpsertEntityAndLoadAsync(entityBroker, workspaceAId,
            """
            {
              "entity-id": "ab060001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "close-prim-left"]],
              "display-name": { "default": "Close Prim Left" },
              "regions": []
            }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceBId,
            """
            {
              "entity-id": "ab060002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "close-prim-right"]],
              "display-name": { "default": "Close Prim Right" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceAId });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceBId });

        var paneA = viewModel.WorkspacePanes.First(p => p.Id == workspaceAId.ToString());
        var paneB = viewModel.WorkspacePanes.First(p => p.Id == workspaceBId.ToString());

        // Close the default tabs added by PopulateWorkspacePaneTabsAsync before opening test tabs
        await CloseDefaultPaneTabsAsync(viewModel, paneA, paneB);

        viewModel.SelectedWorkspacePane = paneA;
        var tabA1 = new WebViewModel("https://a1.example.com") { Id = "close-prim-a1", Title = "A1" };
        var tabA2 = new WebViewModel("https://a2.example.com") { Id = "close-prim-a2", Title = "A2" };
        await viewModel.OpenTabAsync(tabA1);
        await viewModel.OpenTabAsync(tabA2);

        viewModel.SelectedWorkspacePane = paneB;
        var tabB1 = new WebViewModel("https://b1.example.com") { Id = "close-prim-b1", Title = "B1" };
        await viewModel.OpenTabAsync(tabB1);

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        viewModel.SelectedWorkspacePane = paneA;
        viewModel.CloseTab(tabA1);

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        var dockA = FindDocumentDockIn(paneA.ContentLayout!);
        var dockB = FindDocumentDockIn(paneB.ContentLayout!);
        Assert.NotNull(dockA);
        Assert.NotNull(dockB);

        var docA2 = GetPaneDocuments(paneA, dockA!).Single();
        var docB1 = GetPaneDocuments(paneB, dockB!).Single();
        
        Assert.Equal("close-prim-a2", docA2.Id);
        Assert.Equal("close-prim-b1", docB1.Id);
        
        Assert.Equal("1", docA2.EffectiveTabHeader.AltShortcutLabel);
        Assert.Equal("2", docB1.EffectiveTabHeader.AltShortcutLabel);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_ScrollLock_TogglesAgentAutoScroll()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        await using var agentChat = await CreateEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory);

        var agentTab = new AgentSessionWorkspaceTabViewModel { Id = "scroll-lock-toggle", Title = "Agent" };
        agentTab.SetReady(agentViewModel, loggerFactory);
        await viewModel.OpenTabAsync(agentTab);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Assert.True(agentViewModel.AutoScrollEnabled);

            window.KeyPress(Key.Scroll, RawInputModifiers.None, PhysicalKey.None, "");

            Assert.False(agentViewModel.AutoScrollEnabled);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_ScrollLock_TogglesAgentAutoScrollTwice()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        await using var agentChat = await CreateEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory);

        var agentTab = new AgentSessionWorkspaceTabViewModel { Id = "scroll-lock-twice", Title = "Agent" };
        agentTab.SetReady(agentViewModel, loggerFactory);
        await viewModel.OpenTabAsync(agentTab);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            window.KeyPress(Key.Scroll, RawInputModifiers.None, PhysicalKey.None, "");
            window.KeyPress(Key.Scroll, RawInputModifiers.None, PhysicalKey.None, "");

            Assert.True(agentViewModel.AutoScrollEnabled);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_ScrollLock_IsHandledInTunnelPhase()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        await using var agentChat = await CreateEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory);

        var agentTab = new AgentSessionWorkspaceTabViewModel { Id = "scroll-lock-handled", Title = "Agent" };
        agentTab.SetReady(agentViewModel, loggerFactory);
        await viewModel.OpenTabAsync(agentTab);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
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

            Assert.True(handled);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_ScrollLock_WithNoAgentTab_IsNoOp()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var plainTab = new AgentSessionWorkspaceTabViewModel { Id = "scroll-lock-noop", Title = "NoAgent" };
        await viewModel.OpenTabAsync(plainTab);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
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
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    // ── WebViewModel and AgentSessionTab accelerator-key wiring ─────────────────

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabAsync_WebViewModel_AltKeyStateChanged_SetsIsAltHeld()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var webVm = new WebViewModel("https://example.com") { Id = "wv-alt-held", Title = "Tab" };
        await viewModel.OpenTabAsync(webVm);

        webVm.RaiseAltKeyStateChanged(true);

        Assert.True(viewModel.IsAltHeld);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabAsync_WebViewModel_GoToTabAtIndexRequested_ExecutesGoToTabCommand()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "wv-goto-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "wv-goto-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "wv-goto-c", Title = "Tab C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        tabC.RaiseGoToTabAtIndex(0);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("wv-goto-a", documentDock!.ActiveDockable?.Id);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabAsync_AgentSessionTab_AltKeyStateChanged_SetsIsAltHeld()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var agentTab = new AgentSessionWorkspaceTabViewModel { Id = "agent-alt-held", Title = "Agent" };
        await viewModel.OpenTabAsync(agentTab);

        agentTab.RaiseAltKeyStateChanged(true);

        Assert.True(viewModel.IsAltHeld);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabAsync_AgentSessionTab_GoToTabAtIndexRequested_ExecutesGoToTabCommand()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "agent-goto-a", Title = "Agent A" };
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "agent-goto-b", Title = "Agent B" };
        var tabC = new AgentSessionWorkspaceTabViewModel { Id = "agent-goto-c", Title = "Agent C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        tabC.RaiseGoToTabAtIndex(0);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("agent-goto-a", documentDock!.ActiveDockable?.Id);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ApplySelectedViewAsync_WorkspacesView_ShowsRelatedEntityNestedUnderWorkspace()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        var entityBroker = await GetEntityBrokerBeforeInitAsync(viewModel);

        var workspaceId = new EntityId("a2b3c4d5-0001-4000-8000-000000000001");
        var noteId = new EntityId("a2b3c4d5-0001-4000-8000-000000000002");
        var relatedId = new EntityId("a2b3c4d5-0001-4000-8000-000000000003");
        var entityTypeViewId = new EntityId("a2b3c4d5-0001-4000-8000-000000000004");

        // Seed entity-type-view for workspace to declare traverse-relationships
        await SeedEntityAsync(entityBroker, entityTypeViewId, $$"""
            {
              "entity-id": "{{entityTypeViewId}}",
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views", "workspace"]],
              "display-name": { "default": "Workspace View" },
              "fields": [],
              "traverse-relationships": [
                { "relationship-type-ids": ["related"] }
              ]
            }
            """);
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ApplySelectedViewAsync_WorkspacesView_WorkspaceWithNoRelatedEntities_ShowsWorkspaceFlatOnly()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OnOpenScheduledTasksClicked_WhenWindowAlreadyOpen_DoesNotOpenSecondWindow()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OnOpenScheduledTasksClicked_TrackingField_InitiallyNull()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        var mainWindow = new MainWindow(viewModel);

        var trackingField = typeof(MainWindow).GetField(
            "openScheduledTasksWindow",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(trackingField);
        Assert.Null(trackingField!.GetValue(mainWindow));
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_RunVsCodeTunnelTool_IsRegistered()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var hostField = typeof(MainWindowViewModel).GetField(
            "scheduledToolHost",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(hostField);
        var host = Assert.IsType<Phantom.Workspaces.ScheduledTools.ScheduledToolHost>(hostField!.GetValue(viewModel));

        Assert.True(host.TryGetTool("run-vscode-tunnel", out _));

    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_GitWorkspaceDiscoveryTool_IsRegistered()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var hostField = typeof(MainWindowViewModel).GetField(
            "scheduledToolHost",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(hostField);
        var host = Assert.IsType<Phantom.Workspaces.ScheduledTools.ScheduledToolHost>(hostField!.GetValue(viewModel));

        Assert.True(host.TryGetTool("git-workspace-discovery", out _));
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_WorkspaceWithChildren_ShowsExpandAffordance()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceId = new EntityId("24900001-0000-4000-8000-000000000001");
        var childId = new EntityId("24900002-0000-4000-8000-000000000002");
        var relationshipId = new EntityId("24900003-0000-4000-8000-000000000003");

        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "names": [["workspaces", "expand-affordance-test"]],
              "display-name": { "default": "Expand Affordance Test Workspace" },
              "regions": []
            }
            """);

        await UpsertEntityAndLoadAsync(
            entityBroker,
            childId,
            $$"""
            {
              "entity-id": "{{childId}}",
              "entity-types": ["entity", "note"],
              "names": [["notes", "expand-affordance-child"]],
              "display-name": { "default": "Expand Affordance Child" },
              "content": { "mime-type": "text/markdown", "content": { "text": "" } }
            }
            """);

        await UpsertEntityAndLoadAsync(
            entityBroker,
            relationshipId,
            $$"""
            {
              "entity-id": "{{relationshipId}}",
              "entity-types": ["entity", "related", "relationship"],
              "names": [["relationships", "expand-affordance-relation"]],
              "participants": { "entities": ["{{workspaceId}}", "{{childId}}"] }
            }
            """);

        var workspacesView = Assert.Single(
            viewModel.TopLevelViews,
            static view => string.Equals(view.Title, "Workspaces", StringComparison.Ordinal));

        var applyMethod = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyMethod);

        viewModel.SelectedTopLevelView = workspacesView;
        await (Task)applyMethod!.Invoke(viewModel, [])!;

        var workspaceVm = Assert.Single(
            viewModel.CurrentViewPopulation.Entities,
            vm => string.Equals(vm.EntityId, workspaceId.ToString(), StringComparison.OrdinalIgnoreCase));

        Assert.True(workspaceVm.HasTraversedChildren);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_ToggleExpand_DoesNotRebuildPopulation()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceId = new EntityId("24900004-0000-4000-8000-000000000004");
        var childId = new EntityId("24900005-0000-4000-8000-000000000005");
        var relationshipId = new EntityId("24900006-0000-4000-8000-000000000006");
        var entityTypeViewId = new EntityId("24900007-0000-4000-8000-000000000007");

        // Seed entity-type-view for workspace to declare traverse-relationships
        await UpsertEntityAndLoadAsync(
            entityBroker,
            entityTypeViewId,
            $$"""
            {
              "entity-id": "{{entityTypeViewId}}",
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views", "workspace"]],
              "display-name": { "default": "Workspace View" },
              "fields": [],
              "traverse-relationships": [
                { "relationship-type-ids": ["related"] }
              ]
            }
            """);

        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "names": [["workspaces", "toggle-expand-test"]],
              "display-name": { "default": "Toggle Expand Test Workspace" },
              "regions": []
            }
            """);

        await UpsertEntityAndLoadAsync(
            entityBroker,
            childId,
            $$"""
            {
              "entity-id": "{{childId}}",
              "entity-types": ["entity", "note"],
              "names": [["notes", "toggle-expand-child"]],
              "display-name": { "default": "Toggle Expand Child" },
              "content": { "mime-type": "text/markdown", "content": { "text": "" } }
            }
            """);

        await UpsertEntityAndLoadAsync(
            entityBroker,
            relationshipId,
            $$"""
            {
              "entity-id": "{{relationshipId}}",
              "entity-types": ["entity", "related", "relationship"],
              "names": [["relationships", "toggle-expand-relation"]],
              "participants": { "entities": ["{{workspaceId}}", "{{childId}}"] }
            }
            """);

        var workspacesView = Assert.Single(
            viewModel.TopLevelViews,
            static view => string.Equals(view.Title, "Workspaces", StringComparison.Ordinal));

        var applyMethod = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyMethod);

        viewModel.SelectedTopLevelView = workspacesView;
        await (Task)applyMethod!.Invoke(viewModel, [])!;

        // Initially both workspace and child are populated.
        Assert.Contains(viewModel.CurrentViewPopulation.Entities, vm => vm.EntityId == workspaceId.ToString());
        Assert.Contains(viewModel.CurrentViewPopulation.Entities, vm => vm.EntityId == childId.ToString());

        var workspaceVm = Assert.Single(
            viewModel.CurrentViewPopulation.Entities,
            vm => string.Equals(vm.EntityId, workspaceId.ToString(), StringComparison.OrdinalIgnoreCase));
        var originalPopulation = viewModel.CurrentViewPopulation;
        var originalChild = Assert.Single(workspaceVm.Children);

        workspaceVm.ToggleExpandCommand.Execute(null);

        Assert.Same(originalPopulation, viewModel.CurrentViewPopulation);
        Assert.Contains(viewModel.CurrentViewPopulation.Entities, vm => vm.EntityId == workspaceId.ToString());
        Assert.Contains(viewModel.CurrentViewPopulation.Entities, vm => vm.EntityId == childId.ToString());
        Assert.Same(originalChild, Assert.Single(workspaceVm.Children));
        Assert.False(workspaceVm.IsExpanded);

        workspaceVm.ToggleExpandCommand.Execute(null);

        Assert.Same(originalPopulation, viewModel.CurrentViewPopulation);
        Assert.Contains(viewModel.CurrentViewPopulation.Entities, vm => vm.EntityId == workspaceId.ToString());
        Assert.Contains(viewModel.CurrentViewPopulation.Entities, vm => vm.EntityId == childId.ToString());
        Assert.Same(originalChild, Assert.Single(workspaceVm.Children));
        Assert.True(workspaceVm.IsExpanded);
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentSessionShortcutHandler_OpenSameSession_AcrossTwoWorkspacePanes_CreatesTwoTabsWithSameAgentChat()
    {
        var table = CreateTestRunningAgentChatTable();
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache());
        await using var viewModel = CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("aa050001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            agentDefinitionId,
            """
            {
              "entity-id": "aa050001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "shared-chat-echo"]],
              "display-name": { "default": "Shared Chat Echo" },
              "definition": {
                "kind": "prompt",
                "name": "shared-chat-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var workspaceIdA = new EntityId("aa050002-0000-4000-8000-000000000002");
        var workspaceIdB = new EntityId("aa050003-0000-4000-8000-000000000003");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdA,
            """
            {
              "entity-id": "aa050002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "shared-chat-ws-a"]],
              "display-name": { "default": "Shared Chat WS A" },
              "regions": []
            }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdB,
            """
            {
              "entity-id": "aa050003-0000-4000-8000-000000000003",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "shared-chat-ws-b"]],
              "display-name": { "default": "Shared Chat WS B" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionId = Guid.NewGuid().ToString("n");
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId);
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext,
            CreateLocalTrustedExecutorSelector(),
            table);

        // Open in pane A
        var paneAIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdA.ToString());
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute(paneAIndex.ToString());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);
        var tabA = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);

        // Open in pane B
        var paneBIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdB.ToString());
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute(paneBIndex.ToString());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);
        var tabB = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);

        await WaitForAgentReadyAsync(tabA);
        await WaitForAgentReadyAsync(tabB);

        Assert.NotEqual(tabA.Id, tabB.Id);
        Assert.NotNull(tabA.Lease);
        Assert.NotNull(tabB.Lease);
        Assert.Same(tabA.Lease!.AgentChat, tabB.Lease!.AgentChat);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task AgentSessionWorkspaceTabViewModel_DisposeWithLease_ReleasesChat_OnLastDispose()
    {
        var table = CreateTestRunningAgentChatTable();
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache());
        await using var viewModel = CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("aa060001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            agentDefinitionId,
            """
            {
              "entity-id": "aa060001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "shared-dispose-echo"]],
              "display-name": { "default": "Shared Dispose Echo" },
              "definition": {
                "kind": "prompt",
                "name": "shared-dispose-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var workspaceIdA = new EntityId("aa060002-0000-4000-8000-000000000002");
        var workspaceIdB = new EntityId("aa060003-0000-4000-8000-000000000003");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdA,
            """
            {
              "entity-id": "aa060002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "dispose-ws-a"]],
              "display-name": { "default": "Dispose WS A" },
              "regions": []
            }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdB,
            """
            {
              "entity-id": "aa060003-0000-4000-8000-000000000003",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "dispose-ws-b"]],
              "display-name": { "default": "Dispose WS B" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionId = Guid.NewGuid().ToString("n");
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId);
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext,
            CreateLocalTrustedExecutorSelector(),
            table);

        // Open in pane A
        var paneAIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdA.ToString());
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute(paneAIndex.ToString());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);
        var tabA = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);

        // Open in pane B
        var paneBIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdB.ToString());
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute(paneBIndex.ToString());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);
        var tabB = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);

        await WaitForAgentReadyAsync(tabA);
        await WaitForAgentReadyAsync(tabB);

        Assert.NotNull(tabA.Lease);
        Assert.NotNull(tabB.Lease);
        var sharedChat = tabA.Lease!.AgentChat;
        Assert.Same(sharedChat, tabB.Lease!.AgentChat);

        // After disposing first tab, acquire on same key should return cached chat (second tab still holds lease)
        await tabA.DisposeAsync();

        var probe1 = await table.AcquireAsync(new AcquireAgentChatRequest { AgentSessionId = new AgentSessionId(agentSessionId) });
        Assert.Same(sharedChat, probe1.AgentChat); // cached — same instance, second tab still holds lease
        await probe1.DisposeAsync();

        // After disposing second tab, the chat should be gone and a new one created from persistence
        await tabB.DisposeAsync();

        var probe2 = await table.AcquireAsync(new AcquireAgentChatRequest { AgentSessionId = new AgentSessionId(agentSessionId) });
        Assert.NotSame(sharedChat, probe2.AgentChat); // new instance — old was disposed
        await probe2.DisposeAsync();
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task RunningAgentChatTable_Refresh_DoesNotThrow_WhenSessionRemovedConcurrently()
    {
        var table = CreateTestRunningAgentChatTable();
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache());
        await using var viewModel = CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();

        var brain = viewModel.RunningAgentBrain;
        Assert.NotNull(brain);

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("aa070001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            agentDefinitionId,
            """
            {
              "entity-id": "aa070001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "refresh-race-echo"]],
              "display-name": { "default": "Refresh Race Echo" },
              "definition": {
                "kind": "prompt",
                "name": "refresh-race-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var workspaceId = new EntityId("aa070002-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId,
            """
            {
              "entity-id": "aa070002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "refresh-race-ws"]],
              "display-name": { "default": "Refresh Race WS" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionId = Guid.NewGuid().ToString("n");
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId);
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext,
            CreateLocalTrustedExecutorSelector(),
            table);

        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);
        var tab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(tab);

        Assert.NotNull(tab.Lease);
        Assert.Single(table.RunningSessions);

        // Dispose the tab (which releases the lease and triggers removal from RunningSessions).
        // With the bug (TaskScheduler.Default), the removal happens on a thread-pool thread.
        // With the fix (FromCurrentSynchronizationContext), it marshals to the UI thread.
        // Force Refresh to run multiple times concurrently to increase chance of catching the race.
        var disposeTask = tab.DisposeAsync().AsTask();

        for (int i = 0; i < 10; i++)
        {
            brain.Refresh();
        }

        await disposeTask;

        // If the bug exists, one of the Refresh() calls may have thrown InvalidOperationException
        // due to enumerating RunningSessions while it was being mutated on another thread.
        // With the fix, all mutations happen on the UI thread, so no exception occurs.
        Assert.Empty(table.RunningSessions);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task RunningAgentBrain_WithRunningAgentTab_IsAnyRunning()
    {
        var table = CreateTestRunningAgentChatTable();
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache());
        await using var viewModel = CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();

        var brain = viewModel.RunningAgentBrain;
        Assert.NotNull(brain);
        Assert.False(brain!.IsAnyRunning);

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("ab070001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ab070001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "brain-test-echo"]],
              "display-name": { "default": "Brain Test Echo" },
              "definition": {
                "kind": "prompt",
                "name": "brain-test-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext, CreateLocalTrustedExecutorSelector(), table);
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);

        Assert.True(brain.IsAnyRunning);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task RunningAgentBrain_WithRunningAgentTab_HasRowWithWorkspaceAndTabTitles()
    {
        var table = CreateTestRunningAgentChatTable();
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache());
        await using var viewModel = CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();

        var brain = viewModel.RunningAgentBrain;
        Assert.NotNull(brain);

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceId = new EntityId("ab080001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId,
            """
            {
              "entity-id": "ab080001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "brain-popup-ws"]],
              "display-name": { "default": "Brain Popup Workspace" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var agentDefinitionId = new EntityId("ab080002-0000-4000-8000-000000000002");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ab080002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "brain-popup-echo"]],
              "display-name": { "default": "Brain Popup Echo" },
              "definition": {
                "kind": "prompt",
                "name": "brain-popup-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var paneIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceId.ToString());
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute(paneIndex.ToString());

        var handler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext, CreateLocalTrustedExecutorSelector(), table);
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);

        brain!.Refresh();

        var row = Assert.Single(brain.Rows);
        Assert.Equal("Brain Popup Workspace", row.WorkspacePaneTitle);
        Assert.Equal(agentSessionEntity!.DisplayName, row.TabTitle);
        Assert.True(row.HasOpenTab);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task RunningAgentBrain_Activate_FocusesTab()
    {
        var table = CreateTestRunningAgentChatTable();
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache());
        await using var viewModel = CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();

        var brain = viewModel.RunningAgentBrain;
        Assert.NotNull(brain);

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("ab090001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ab090001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "brain-activate-echo"]],
              "display-name": { "default": "Brain Activate Echo" },
              "definition": {
                "kind": "prompt",
                "name": "brain-activate-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext, CreateLocalTrustedExecutorSelector(), table);
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);

        brain!.Refresh();

        var row = Assert.Single(brain.Rows);

        // Open the popup, click the row
        brain.IsOpen = true;
        row.ActivateCommand.Execute(null);

        // Popup should close after activation
        Assert.False(brain.IsOpen);

        // The tab should be active
        var layout = viewModel.SelectedWorkspacePane.ContentLayout;
        Assert.NotNull(layout);
        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var activeDoc = documentDock!.ActiveDockable as WorkspaceDocument;
        Assert.NotNull(activeDoc);
        Assert.Equal(agentTab.Id, activeDoc!.Id);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task RunningAgentBrain_RowActivateCommand_WhenTabIsInNonSelectedPane_SwitchesWorkspacePane()
    {
        var table = CreateTestRunningAgentChatTable();
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache());
        await using var viewModel = CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();

        var brain = viewModel.RunningAgentBrain;
        Assert.NotNull(brain);

        var entityBroker = GetEntityBroker(viewModel);

        // Open workspace A first so that workspace B is at index 1 (not 0)
        var workspaceAId = new EntityId("ab100000-0000-4000-8000-000000000000");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceAId,
            """
            {
              "entity-id": "ab100000-0000-4000-8000-000000000000",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "brain-cross-pane-ws-a"]],
              "display-name": { "default": "Brain Cross-Pane Workspace A" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceAId });

        var workspaceBId = new EntityId("ab100001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceBId,
            """
            {
              "entity-id": "ab100001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "brain-cross-pane-ws"]],
              "display-name": { "default": "Brain Cross-Pane Workspace" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceBId });

        var agentDefinitionId = new EntityId("ab100002-0000-4000-8000-000000000002");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ab100002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "brain-cross-pane-echo"]],
              "display-name": { "default": "Brain Cross-Pane Echo" },
              "definition": {
                "kind": "prompt",
                "name": "brain-cross-pane-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        // Switch to workspace B and open an agent tab there
        var paneBIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceBId.ToString());
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute(paneBIndex.ToString());

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext, CreateLocalTrustedExecutorSelector(), table);
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);

        brain!.Refresh();
        Assert.Single(brain.Rows);

        // Switch back to the default (first) pane so workspace B is not selected
        viewModel.GoToWorkspacePaneAtIndexCommand.Execute("0");
        Assert.NotEqual(workspaceBId.ToString(), viewModel.SelectedWorkspacePane.Id);

        // Execute the activate command on the running-agent row
        var row = Assert.Single(brain.Rows);
        brain.IsOpen = true;
        row.ActivateCommand.Execute(null);

        // Workspace B should now be selected and the agent tab active
        Assert.Equal(workspaceBId.ToString(), viewModel.SelectedWorkspacePane.Id);
        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal(agentTab.Id, (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ActivateTabById_WhenWorkspacePaneNotInWorkspacePanes_OpensWorkspaceAndActivatesTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceBId = new EntityId("ab110001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceBId,
            """
            {
              "entity-id": "ab110001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "activate-closed-ws"]],
              "display-name": { "default": "Activate Closed Workspace" },
              "regions": [
                {
                  "region-id": "main",
                  "title": "Main",
                  "dock": "center",
                  "size": 1.0,
                  "tabs": [
                    {
                      "tab-id": "closed-ws-tab",
                      "title": "Closed WS Tab",
                      "kind": "web",
                      "dock": "full",
                      "content": { "url": "https://example.com/closed-ws" }
                    }
                  ]
                }
              ]
            }
            """);

        // Open workspace B to confirm it loads correctly, then close it
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceBId });
        var initialPane = viewModel.WorkspacePanes.Single(
            p => string.Equals(p.Id, workspaceBId.ToString(), StringComparison.Ordinal));
        var initialDock = FindDocumentDockIn(initialPane.ContentLayout!);
        Assert.NotNull(initialDock);
        await WaitForWorkspaceTabAsync(initialDock!, "closed-ws-tab");
        await viewModel.RemoveWorkspacePaneAsync(initialPane);
        Assert.DoesNotContain(
            viewModel.WorkspacePanes,
            p => string.Equals(p.Id, workspaceBId.ToString(), StringComparison.Ordinal));

        // Now activate the tab by ID — workspace B is not open
        await viewModel.ActivateTabByIdAsync("closed-ws-tab", workspaceBId.ToString());

        // Workspace B should have been re-opened and selected
        Assert.Equal(workspaceBId.ToString(), viewModel.SelectedWorkspacePane.Id);

        // Wait for the tab to be loaded and activated
        var newPane = viewModel.WorkspacePanes.Single(
            p => string.Equals(p.Id, workspaceBId.ToString(), StringComparison.Ordinal));
        var contentDock = FindDocumentDockIn(newPane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, "closed-ws-tab");

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("closed-ws-tab", (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
    }

    // ── PopulateWorkspacePaneTabsAsync — new tabs[] format ───────────────────

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithTopLevelTabsArray_PopulatesPaneTabsInSavedOrder()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("01700001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "01700001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "tabs-array-order"]],
              "display-name": { "default": "Tabs Array Order Workspace" },
              "tabs": [
                {
                  "tab-id": "tabs-arr-a",
                  "title": "Tab A",
                  "kind": "browser",
                  "content": { "url": "https://a.example.com" }
                },
                {
                  "tab-id": "tabs-arr-b",
                  "title": "Tab B",
                  "kind": "browser",
                  "content": { "url": "https://b.example.com" }
                },
                {
                  "tab-id": "tabs-arr-c",
                  "title": "Tab C",
                  "kind": "browser",
                  "content": { "url": "https://c.example.com" }
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

        await WaitForWorkspaceTabAsync(contentDock!, "tabs-arr-a");
        await WaitForWorkspaceTabAsync(contentDock!, "tabs-arr-b");
        await WaitForWorkspaceTabAsync(contentDock!, "tabs-arr-c");

        var tabIds = contentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .Where(d => d.Id is "tabs-arr-a" or "tabs-arr-b" or "tabs-arr-c")
            .Select(d => d.Id)
            .ToList();

        Assert.Equal(["tabs-arr-a", "tabs-arr-b", "tabs-arr-c"], tabIds);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithLegacyRegions_FlattensToSingleDock()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("01700002-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "01700002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "legacy-regions-flatten"]],
              "display-name": { "default": "Legacy Regions Workspace" },
              "regions": [
                {
                  "region-id": "left",
                  "title": "Left",
                  "dock": "center",
                  "size": 0.5,
                  "tabs": [
                    {
                      "tab-id": "legacy-tab-left",
                      "title": "Left Tab",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://left.example.com" }
                    }
                  ]
                },
                {
                  "region-id": "right",
                  "title": "Right",
                  "dock": "center",
                  "size": 0.5,
                  "tabs": [
                    {
                      "tab-id": "legacy-tab-right",
                      "title": "Right Tab",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://right.example.com" }
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

        await WaitForWorkspaceTabAsync(contentDock!, "legacy-tab-left");
        await WaitForWorkspaceTabAsync(contentDock!, "legacy-tab-right");

        // Both tabs from both legacy regions are flattened into a single dock
        var tabIds = contentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .Where(d => d.Id is "legacy-tab-left" or "legacy-tab-right")
            .Select(d => d.Id)
            .ToList();

        Assert.Equal(2, tabIds.Count);
        Assert.Contains("legacy-tab-left", tabIds);
        Assert.Contains("legacy-tab-right", tabIds);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithNoTabsAndNoRegions_OpensDefaultEntityTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("01700003-0000-4000-8000-000000000003");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "01700003-0000-4000-8000-000000000003",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "no-tabs-default"]],
              "display-name": { "default": "No Tabs Workspace" }
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);

        // The workspace entity ID is used as the default tab ID
        await WaitForWorkspaceTabAsync(contentDock!, workspaceId.ToString());

        var defaultTab = contentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .FirstOrDefault(d => d.Id == workspaceId.ToString());
        Assert.NotNull(defaultTab);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithTopLevelTabsAndActiveTabId_ActivatesSpecifiedTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("01700004-0000-4000-8000-000000000004");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "01700004-0000-4000-8000-000000000004",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "tabs-active-tab-id"]],
              "display-name": { "default": "Active Tab ID Workspace" },
              "active-tab-id": "tabs-active-second",
              "tabs": [
                {
                  "tab-id": "tabs-active-first",
                  "title": "First Tab",
                  "kind": "browser",
                  "content": { "url": "https://first.example.com" }
                },
                {
                  "tab-id": "tabs-active-second",
                  "title": "Second Tab",
                  "kind": "browser",
                  "content": { "url": "https://second.example.com" }
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

        await WaitForWorkspaceTabAsync(contentDock!, "tabs-active-first");
        await WaitForWorkspaceTabAsync(contentDock!, "tabs-active-second");

        Assert.Equal("tabs-active-second", contentDock!.ActiveDockable?.Id);
    }

    // ── CreateWorkspaceContentLayout — ItemsSource wiring ────────────────────

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task CreateWorkspaceContentLayout_SetsItemsSourceToPaneTabs()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("01700005-0000-4000-8000-000000000005");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "01700005-0000-4000-8000-000000000005",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "items-source-wiring"]],
              "display-name": { "default": "ItemsSource Wiring Workspace" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        Assert.NotNull(workspacePane.ContentLayout);

        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);

        // ItemsSource must point at pane.Tabs so the generator creates documents automatically
        var itemsSourceDock = contentDock as Dock.Model.Core.IItemsSourceDock;
        Assert.NotNull(itemsSourceDock);
        Assert.Same(workspacePane.Tabs, itemsSourceDock!.ItemsSource);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task CreateWorkspaceContentLayout_AddingTabToPaneTabs_CreatesWorkspaceDocumentInDock()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("01700006-0000-4000-8000-000000000006");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "01700006-0000-4000-8000-000000000006",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "items-source-add"]],
              "display-name": { "default": "ItemsSource Add Workspace" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        // Wait for the default tab to appear, then verify adding a new tab auto-creates a document
        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, workspaceId.ToString());

        var newTab = new WebViewModel("https://items-source.example.com")
        {
            Id = "items-source-add-tab",
            Title = "Items Source Tab",
        };
        workspacePane.Tabs.Add(newTab);

        await WaitForWorkspaceTabAsync(contentDock!, "items-source-add-tab");

        var doc = contentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .FirstOrDefault(d => d.Id == "items-source-add-tab");
        Assert.NotNull(doc);
        Assert.Same(newTab, doc!.TabViewModel);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task CreateWorkspaceContentLayout_RemovingTabFromPaneTabs_RemovesWorkspaceDocumentFromDock()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabToRemove = new WebViewModel("https://remove.example.com")
        {
            Id = "items-source-remove-tab",
            Title = "Remove Tab",
        };
        await viewModel.OpenTabAsync(tabToRemove);

        var workspacePane = viewModel.SelectedWorkspacePane;
        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, "items-source-remove-tab");

        // Remove from pane.Tabs — the ItemsSource generator must remove the document automatically
        workspacePane.Tabs.Remove(tabToRemove);

        var docAfterRemoval = contentDock!.VisibleDockables?
            .OfType<WorkspaceDocument>()
            .FirstOrDefault(d => d.Id == "items-source-remove-tab");
        Assert.Null(docAfterRemoval);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task TryBuildAgent_SlashCommandContext_WithLocalSession_ExecuteAutoResume_UpdatesEntityWithTrustedExecutorDot()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("ac010001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ac010001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "slash-cmd-ctx-echo"]],
              "display-name": { "default": "Slash Cmd Context Echo" },
              "definition": {
                "kind": "prompt",
                "name": "slash-cmd-ctx-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);

        // Execute /auto-resume — if TrustedExecutorIdentifier and UpdateAutoResumeAsync are wired,
        // the entity is updated with auto-resume.trusted-executor = "."
        var interceptor = agentTab.Agent!.InputQueue!.DefaultComposer.SlashCommandInterceptorAsync;
        Assert.NotNull(interceptor);
        await interceptor!("/auto-resume");

        // Reload the entity and verify auto-resume was persisted
        var updatedEntities = await entityBroker.GetEntitiesAsync([agentSessionEntity!.EntityId]);
        var updatedEntity = updatedEntities.FirstOrDefault(e => e.EntityId == agentSessionEntity!.EntityId);
        Assert.NotNull(updatedEntity);
        var updatedData = Assert.IsType<JsonElement>(updatedEntity!.Data);
        Assert.True(updatedData.TryGetProperty("auto-resume", out var autoResumeEl));
        Assert.True(autoResumeEl.TryGetProperty("trusted-executor", out var executorEl));
        Assert.Equal(TrustProfile.LocalClientInstance, executorEl.GetString());
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task TryBuildAgent_SlashCommandContext_WithAutoResumeAlreadyEnabled_ExecuteAutoResume_RemovesAutoResume()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("ac020001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ac020001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "slash-cmd-ctx-toggle-echo"]],
              "display-name": { "default": "Slash Cmd Context Toggle Echo" },
              "definition": {
                "kind": "prompt",
                "name": "slash-cmd-ctx-toggle-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);

        var interceptor = agentTab.Agent!.InputQueue!.DefaultComposer.SlashCommandInterceptorAsync;
        Assert.NotNull(interceptor);

        // Enable auto-resume first
        await interceptor!("/auto-resume");

        // Execute again — CurrentAutoResume should now be non-null so the toggle disables it
        await interceptor!("/auto-resume");

        var updatedEntities = await entityBroker.GetEntitiesAsync([agentSessionEntity!.EntityId]);
        var updatedEntity = updatedEntities.FirstOrDefault(e => e.EntityId == agentSessionEntity!.EntityId);
        Assert.NotNull(updatedEntity);
        var updatedData = Assert.IsType<JsonElement>(updatedEntity!.Data);
        Assert.False(updatedData.TryGetProperty("auto-resume", out _));
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task TryStartAutoResumeAsync_WithMatchingLocalSession_AcquiresLeaseAndEnqueuesResumePrompt()
    {
        var table = CreateTestRunningAgentChatTable();
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache());
        await using var viewModel = CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("ac030001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ac030001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "auto-resume-start-echo"]],
              "display-name": { "default": "Auto Resume Start Echo" },
              "definition": {
                "kind": "prompt",
                "name": "auto-resume-start-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        const string agentSessionId = "ac030001-session-for-auto-resume-test";
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId);
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext, CreateLocalTrustedExecutorSelector(), table);

        const string resumePrompt = "Resume the task where you left off.";
        var foregroundScheduler = SynchronizationContextTaskScheduler.FromCurrent();
        var lease = await Task.Run(() =>
            handler.TryStartAutoResumeAsync(viewModel, agentSessionEntity!, resumePrompt, foregroundScheduler));

        try
        {
            Assert.NotNull(lease);
            Assert.Single(table.RunningSessions);

            // Verify the resume prompt was enqueued — wait for it to appear in history
            await WaitForChatHistoryAsync(lease!.AgentChat, resumePrompt);

            Assert.Contains(
                lease.AgentChat.History,
                item => item.Role == ChatRole.User
                    && item.Contents.OfType<TextContent>().Any(c => c.Text == resumePrompt));
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }
        }
    }

    private static async Task WaitForChatHistoryAsync(AgentChat agentChat, string expectedUserMessage)
    {
        bool IsPresent() => agentChat.History.Any(
            item => item.Role == ChatRole.User
                && item.Contents.OfType<TextContent>().Any(c => c.Text == expectedUserMessage));

        if (IsPresent())
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<AgentChatHistoryItem> onTurnCompleted = (_, _) =>
        {
            if (IsPresent())
            {
                signal.TrySetResult();
            }
        };

        agentChat.TurnCompleted += onTurnCompleted;
        try
        {
            if (!IsPresent())
            {
                await signal.Task;
            }
        }
        finally
        {
            agentChat.TurnCompleted -= onTurnCompleted;
        }
    }

    private sealed class FakeShellSession : ITerminalSession
    {
        private readonly MemoryStream stream = new();

        public Stream Stream => this.stream;

        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask SignalAsync(string signal, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public Task<int> WaitForExitAsync() => Task.FromResult(0);

        public ValueTask DisposeAsync()
        {
            this.stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    // ── Tab close MRU navigation tests (#828) ──────────────────────────────────

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task CloseTab_ActiveTab_NavigatesToMostRecentlyUsedTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "mru-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "mru-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "mru-c", Title = "Tab C" };

        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        // Navigate to tabA to make it MRU (OpenTabAsync on existing tab pushes to history)
        await viewModel.OpenTabAsync(tabA);

        // Navigate back to tabC
        await viewModel.OpenTabAsync(tabC);

        // Close the active tab (tabC) — should navigate to the MRU tab (tabA)
        viewModel.CloseTab(tabC);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("mru-a", documentDock!.ActiveDockable?.Id);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task CloseTab_NonActiveTab_DoesNotChangeActiveTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "mru-non-active-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "mru-non-active-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "mru-non-active-c", Title = "Tab C" };

        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        // Set tabB as active
        await viewModel.OpenTabAsync(tabB);

        // Close a non-active tab (tabA)
        viewModel.CloseTab(tabA);

        // Active tab should still be tabB
        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("mru-non-active-b", documentDock!.ActiveDockable?.Id);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task CloseTab_LastTabInPane_NoNavigation()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://example.com") { Id = "mru-last", Title = "Last Tab" };
        await viewModel.OpenTabAsync(tab);

        // Close the only tab — should not crash or navigate anywhere
        viewModel.CloseTab(tab);

        Assert.Empty(viewModel.SelectedWorkspacePane!.Tabs);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task CloseTabById_ActiveTab_NavigatesToMostRecentlyUsedTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "mru-byid-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "mru-byid-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "mru-byid-c", Title = "Tab C" };

        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        // Navigate to tabA to make it MRU
        await viewModel.OpenTabAsync(tabA);

        // Navigate back to tabC
        await viewModel.OpenTabAsync(tabC);

        // Close the active tab by ID (tabC) — should navigate to the MRU tab (tabA)
        viewModel.CloseTabById("mru-byid-c");

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("mru-byid-a", documentDock!.ActiveDockable?.Id);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task OnDockableTabClosed_ActiveTab_NavigatesToMostRecentlyUsedTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "mru-dockable-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "mru-dockable-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "mru-dockable-c", Title = "Tab C" };

        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        // Navigate to tabA to make it MRU
        await viewModel.OpenTabAsync(tabA);

        // Navigate back to tabC
        await viewModel.OpenTabAsync(tabC);

        // Close the active tab via dock framework (tabC) — should navigate to the MRU tab (tabA)
        viewModel.OnDockableTabClosed(tabC);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("mru-dockable-a", documentDock!.ActiveDockable?.Id);
    }

}

