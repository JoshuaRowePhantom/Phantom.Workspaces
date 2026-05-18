using Avalonia;
using Avalonia.Media;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class MainWindowIntegrationTests
{
    [Fact]
    public void ThemeResources_UseFontFamilyType()
    {
        EnsureAppInitialized();

        _ = new MainWindowViewModel(CreateInMemoryRepositorySource());

        Assert.True(Application.Current!.Resources.TryGetValue("Theme.FontFamily", out var fontFamilyResource));
        Assert.IsType<FontFamily>(fontFamilyResource);
    }

    [Fact]
    public async Task InMemoryRepository_InitializesWithExpectedPipeline()
    {
        EnsureAppInitialized();

        var repository = await EntityRepository.CreateAsync(CreateInMemoryRepositorySource());
        var snapshots = await repository.ExportEntitySnapshotsAsync();
        Assert.IsType<MergeProcessingDataAccessLayer>(repository.DataAccessLayer);
        Assert.NotEmpty(snapshots);
    }

    [Fact]
    public void MainWindowViewModel_ThemeSelectionIsDataDriven()
    {
        EnsureAppInitialized();

        var viewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        Assert.Contains("dark", viewModel.ThemeNames);
        Assert.Contains("light", viewModel.ThemeNames);
        viewModel.SelectedThemeName = "light";
        Assert.Equal("light", viewModel.SelectedThemeName);
    }

    private static void EnsureAppInitialized()
    {
        if (Application.Current is not null)
        {
            return;
        }

        AppBuilder.Configure<TestApplication>()
            .UsePlatformDetect()
            .SetupWithoutStarting();
    }

    private static RepositorySource CreateInMemoryRepositorySource()
    {
        return new RepositorySource(RepositorySourceType.Unknown, "(none)");
    }

    private sealed class TestApplication : Application
    {
    }
}
