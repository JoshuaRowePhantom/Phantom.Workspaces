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
        Assert.IsType<MergeProcessingDataAccessLayer>(repository.DataAccessLayer);
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

    private static RepositorySource CreateInMemoryRepositorySource()
    {
        return new RepositorySource(RepositorySourceType.Unknown, "(none)");
    }

}
