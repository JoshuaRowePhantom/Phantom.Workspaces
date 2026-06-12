using Avalonia.Media;
using Avalonia.Headless.XUnit;
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
    public async Task MainWindowViewModel_SessionsView_GetEntitySubViewsIncludeAgentDefinitionEntities()
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
            static entity => string.Equals(entity.EntityType, "agent-definition", StringComparison.Ordinal));
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
    public void CreateWorkspacePane_DoesNotInjectFallbackCenterRegion_WhenWorkspaceHasNoRegions()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        using var workspaceDocument = JsonDocument.Parse(
            """
            {
              "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "entity-types": ["workspace"],
              "display-name": { "default": "Workspace Without Regions" }
            }
            """);
        using var entityDocument = JsonDocument.Parse(
            """
            {
              "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "entity-types": ["workspace"],
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
            "CreateWorkspacePane",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(createWorkspacePane);

        var workspacePane = (WorkspacePaneViewModel?)createWorkspacePane!.Invoke(
            viewModel,
            [workspaceEntity, workspaceDocument.RootElement.Clone()]);

        Assert.NotNull(workspacePane);
        Assert.Empty(workspacePane!.Regions);
        Assert.Null(workspacePane.SelectedRegion);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentDefinitionShortcutHandler_LocalEchoDefinition_CreatesAgentSessionTab()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("f95a86dc-f71f-43f8-abf5-31c6444f7a4e"),
            """
            {
              "entity-id": "f95a86dc-f71f-43f8-abf5-31c6444f7a4e",
              "entity-types": ["agent-definition"],
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

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(agentSessionShortcutContext);
        var openAgentDefinitionShortcutHandler = new OpenAgentDefinitionShortcutHandler(agentSessionShortcutContext, openAgentSessionShortcutHandler);

        var handled = await openAgentDefinitionShortcutHandler.Handle(viewModel, Shortcut.Open, agentDefinitionEntity);

        Assert.True(handled);
        var selectedRegion = Assert.IsType<WorkspaceRegionViewModel>(viewModel.SelectedWorkspacePane.SelectedRegion);
        var selectedTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(selectedRegion.SelectedTab);
        Assert.NotNull(selectedTab.Agent);
        Assert.True(selectedTab.Entity?.IsEntityType("agent-session"));
        var names = ReadEntityNames(selectedTab.Entity!.Data);
        Assert.Contains(names, static name => name.Components.Length >= 5
            && string.Equals(name.Components[0], "users", StringComparison.Ordinal)
            && string.Equals(name.Components[3], "agent-sessions", StringComparison.Ordinal));
    }

    private static RepositorySource CreateInMemoryRepositorySource()
    {
        return new RepositorySource(RepositorySourceType.Unknown, "(none)");
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

}
