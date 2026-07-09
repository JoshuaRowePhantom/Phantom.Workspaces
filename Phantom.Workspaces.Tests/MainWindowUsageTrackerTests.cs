using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class MainWindowUsageTrackerTests
{
    private static RepositorySource CreateInMemoryRepositorySource()
    {
        return new UnknownRepositorySource();
    }

    private static MainWindowViewModel CreateTestMainWindowViewModel()
    {
        return new MainWindowViewModel(
            CreateInMemoryRepositorySource(),
            new WorkspacesConfiguration { SkipStartupWorkspace = true });
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_UsageTrackerPanel_Hidden_WhenTopRightLabelNull()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        
        // UsageTracker is initialized but with no accounts, so TopRightLabel should be null
        var mainWindow = new MainWindow(viewModel);
        mainWindow.Show();
        
        try
        {
            var panels = mainWindow.GetVisualDescendants().OfType<Panel>().ToList();
            var usageTrackerPanel = panels.FirstOrDefault(p =>
            {
                var button = p.GetVisualDescendants().OfType<Button>().FirstOrDefault();
                return button?.Name == "UsageTrackerButton";
            });
            
            // Panel should not be visible when TopRightLabel is null
            if (usageTrackerPanel != null)
            {
                Assert.False(usageTrackerPanel.IsVisible);
            }
        }
        finally
        {
            mainWindow.Close();
        }
    }

    
    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_ExposesUsageTracker()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        
        // After initialization, UsageTracker should be non-null
        Assert.NotNull(viewModel.UsageTracker);
        Assert.IsType<UsageTrackerViewModel>(viewModel.UsageTracker);
    }
}
