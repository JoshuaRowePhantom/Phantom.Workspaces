using Avalonia.Media;
using Avalonia.Headless.XUnit;
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
    public void MainWindow_ConstructsWithoutTemplateCastErrors()
    {
        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        var window = new MainWindow(viewModel);

        Assert.NotNull(window);
        Assert.Empty(window.DataTemplates);
    }

    private static RepositorySource CreateInMemoryRepositorySource()
    {
        return new RepositorySource(RepositorySourceType.Unknown, "(none)");
    }

}
