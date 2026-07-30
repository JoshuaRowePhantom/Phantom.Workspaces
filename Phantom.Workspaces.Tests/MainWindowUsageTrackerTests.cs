using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

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

    [AvaloniaFact(Timeout = 15_000)]
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

    
    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_ExposesUsageTracker()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        
        // After initialization, UsageTracker should be non-null
        Assert.NotNull(viewModel.UsageTracker);
        Assert.IsType<UsageTrackerViewModel>(viewModel.UsageTracker);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_UsageTrackerPanel_Visible_WhenAccountWithUsageExists()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        // Add an account with a metric to the initialized UsageTracker so
        // TopRightLabel becomes non-null. The MainWindow XAML binds the panel's
        // IsVisible to (UsageTracker.TopRightLabel != null).
        Assert.NotNull(viewModel.UsageTracker);
        var account = new Phantom.Workspaces.Models.UsageAccount
        {
            Product = "github.com",
            UserName = "octocat",
            SettingsUrl = new System.Uri("https://github.com/copilot"),
        };
        account.Metrics.Add(new Phantom.Workspaces.Models.UsageMetric
        {
            Title = "Copilot Premium Request",
            QuantityUsed = 42m,
            QuantityTotal = 0m,
            QuantityPresentationFormatString = "{0:N0} {2}",
            Unit = "Requests",
        });
        await viewModel.UsageTracker!.Metrics.MutateAsync(() =>
        {
            viewModel.UsageTracker.Metrics.Accounts.Add(account);
            return Task.CompletedTask;
        });

        Assert.NotNull(viewModel.UsageTracker.TopRightLabel);

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

            Assert.NotNull(usageTrackerPanel);
            Assert.True(usageTrackerPanel!.IsVisible);
        }
        finally
        {
            mainWindow.Close();
        }
    }
}
