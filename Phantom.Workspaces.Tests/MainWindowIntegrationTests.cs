using Avalonia.Media;
using Avalonia.Headless.XUnit;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class MainWindowIntegrationTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public void ThemeResources_UseFontFamilyType()
    {
        _ = new MainWindowViewModel(CreateInMemoryRepositorySource());

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
            sessionsView.Entities,
            static entity => string.Equals(entity.EntityType, "agent-manifest", StringComparison.Ordinal));
        Assert.DoesNotContain(
            sessionsView.Entities,
            static entity => string.Equals(entity.EntityType, "view", StringComparison.Ordinal));
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
        var selectedTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(selectedRegion.SelectedTab);
        Assert.NotNull(selectedTab.Agent);
        Assert.True(selectedTab.Entity?.IsEntityType("agent-session"));
        var names = ReadEntityNames(selectedTab.Entity!.Data);
        var createdAgentSessionId = selectedTab.Entity.Data is JsonElement selectedEntityData
            && selectedEntityData.TryGetProperty("agent-session-id", out var agentSessionIdElement)
                ? agentSessionIdElement.GetString()
                : null;
        Assert.False(string.IsNullOrWhiteSpace(createdAgentSessionId));
        var expectedSessionNamePrefix = $"session-{fixedCurrentTime:yyyy-MM-dd-HH-mm-ss}-";
        Assert.Contains(names, static name => name.Components.Length >= 5
            && string.Equals(name.Components[0], "users", StringComparison.Ordinal)
            && string.Equals(name.Components[3], "agent-sessions", StringComparison.Ordinal));
        Assert.Contains(
            names,
            name => name.Components.Length >= 5
                && string.Equals(name.Components[0], "users", StringComparison.Ordinal)
                && string.Equals(name.Components[3], "agent-sessions", StringComparison.Ordinal)
                && name.Components[^1].StartsWith(expectedSessionNamePrefix, StringComparison.Ordinal)
                && name.Components[^1].EndsWith(createdAgentSessionId, StringComparison.Ordinal));
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
        var selectedTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(selectedRegion.SelectedTab);
        Assert.NotNull(selectedTab.Agent);
        Assert.True(selectedTab.Entity?.IsEntityType("agent-session"));
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
        var selectedTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(selectedRegion.SelectedTab);
        Assert.Contains(selectedTab.Agent.Tools, static tool => string.Equals(tool.Kind, "workspace-entity", StringComparison.Ordinal));
    }

    private static RepositorySource CreateInMemoryRepositorySource()
    {
        return new UnknownRepositorySource();
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

}
