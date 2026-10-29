using System.Linq;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class MainWindowUsageTrackerTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowUsageTracker_AiCreditsDollarLink_UsesSharedFeaturesUrl()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        var opener = new CopilotCurrencyLinkTests.RecordingOpener();
        viewModel.ApplicationServices.SetUrlOpener(opener);
        var account = await CopilotCurrencyLinkTests.CreateAccountAsync("https://github.com/copilot", 12);
        await viewModel.UsageTracker!.Metrics.MutateAsync(() =>
        {
            viewModel.UsageTracker.Metrics.Accounts.Add(account);
            return Task.CompletedTask;
        });
        var mainWindow = new MainWindow(viewModel);
        mainWindow.Show();
        try
        {
            var button = mainWindow.FindControl<Button>("UsageTrackerButton")!;
            Assert.Same(viewModel.UsageTracker.ToggleOpenCommand, button.Command);
            Assert.Null(button.CommandParameter);
            button.Command!.Execute(button.CommandParameter);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(viewModel.UsageTracker.IsOpen);
            Assert.Equal(0, opener.Count);
            var popup = mainWindow.GetLogicalDescendants().OfType<Avalonia.Controls.Primitives.Popup>()
                .Single(candidate => candidate.Child is Phantom.Workspaces.Controls.UsageTrackerControl);
            var control = Assert.IsType<Phantom.Workspaces.Controls.UsageTrackerControl>(popup.Child);
            var dollarLink = control.GetVisualDescendants().OfType<HyperlinkButton>()
                .Single(link => link.Name == "MetricRowHyperlink" &&
                    link.DataContext is Phantom.Workspaces.Models.UsageMetric metric &&
                    metric.Title == "Copilot AI Credits (Additional Usage)");
            Assert.Equal(new System.Uri("https://github.com/settings/copilot/features"), dollarLink.CommandParameter);
            Assert.Same(viewModel.UsageTracker.OpenUrlCommand, dollarLink.Command);
            Assert.Null(dollarLink.NavigateUri);
            var selectedKey = viewModel.UsageTracker.SelectedUsageMetricKey;
            var popupRoot = TopLevel.GetTopLevel(dollarLink)!;
            popupRoot.UpdateLayout();
            var point = dollarLink.TranslatePoint(new Point(dollarLink.Bounds.Width / 2, dollarLink.Bounds.Height / 2), popupRoot)!.Value;
            popupRoot.MouseMove(point);
            popupRoot.MouseDown(point, MouseButton.Left);
            popupRoot.MouseUp(point, MouseButton.Left);
            Assert.Equal(1, opener.Count);
            dollarLink.Focus();
            popupRoot.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            popupRoot.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.Equal(2, opener.Count);
            Assert.Equal("https://github.com/settings/copilot/features", opener.Request!.Url);
            Assert.Equal(Phantom.Workspaces.Services.UrlOpenPreference.Auto, opener.Request.Preference);
            Assert.Equal(selectedKey, viewModel.UsageTracker.SelectedUsageMetricKey);
            Assert.True(viewModel.UsageTracker.IsOpen);
        }
        finally
        {
            mainWindow.Close();
        }
    }

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

    [AvaloniaFact(Timeout = 15_000)]
    public void MainWindow_UsageTrackerPanel_ShowsCostMetricRow_WhenCopilotAccountHasNetAmount()
    {
        System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");

        var metrics = new Phantom.Workspaces.Models.UsageMetrics();
        var account = new Phantom.Workspaces.Models.UsageAccount
        {
            Product = "github.com",
            UserName = "octocat",
            SettingsUrl = new System.Uri("https://github.com/copilot"),
        };
        account.Metrics.Add(new Phantom.Workspaces.Models.UsageMetric
        {
            Title = "Copilot AI Credits",
            QuantityUsed = 12345m,
            QuantityTotal = 0m,
            QuantityPresentationFormatString = "{0:N0} {2}",
            Unit = "AICredits",
        });
        account.Metrics.Add(new Phantom.Workspaces.Models.UsageMetric
        {
            Title = "Copilot AI Credits (Additional Usage)",
            QuantityUsed = 356.00m,
            QuantityTotal = 0m,
            QuantityPresentationFormatString = "{0:C2}",
            Unit = string.Empty,
        });
        metrics.Accounts.Add(account);

        using var vm = new UsageTrackerViewModel(metrics);
        var control = new Phantom.Workspaces.Controls.UsageTrackerControl { DataContext = vm };
        var window = new Avalonia.Controls.Window { Content = control };
        window.Show();

        try
        {
            var textBlocks = window.GetVisualDescendants()
                .OfType<Avalonia.Controls.TextBlock>()
                .ToList();
            Assert.Contains(textBlocks, tb => tb.Text == "Copilot AI Credits (Additional Usage)");
            Assert.Contains(textBlocks, tb => tb.Text == "$356.00");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void MainWindow_UsageTrackerPanel_ShowsAdditionalInformation_WhenMetricHasNote()
    {
        var metrics = new Phantom.Workspaces.Models.UsageMetrics();
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
            AdditionalInformation = "Resets in 2 days",
        });
        metrics.Accounts.Add(account);

        using var vm = new UsageTrackerViewModel(metrics);
        var control = new Phantom.Workspaces.Controls.UsageTrackerControl { DataContext = vm };
        var window = new Avalonia.Controls.Window { Content = control };
        window.Show();

        try
        {
            var noteTextBlock = window.GetVisualDescendants()
                .OfType<Avalonia.Controls.TextBlock>()
                .FirstOrDefault(tb => tb.Name == "AdditionalInformationText" && tb.Text == "Resets in 2 days");
            Assert.NotNull(noteTextBlock);
            Assert.True(noteTextBlock!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void MainWindow_UsageTrackerPanel_HidesAdditionalInformation_WhenMetricHasNoNote()
    {
        var metrics = new Phantom.Workspaces.Models.UsageMetrics();
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
            // AdditionalInformation = null (default)
        });
        metrics.Accounts.Add(account);

        using var vm = new UsageTrackerViewModel(metrics);
        var control = new Phantom.Workspaces.Controls.UsageTrackerControl { DataContext = vm };
        var window = new Avalonia.Controls.Window { Content = control };
        window.Show();

        try
        {
            var noteTextBlocks = window.GetVisualDescendants()
                .OfType<Avalonia.Controls.TextBlock>()
                .Where(tb => tb.Name == "AdditionalInformationText")
                .ToList();
            Assert.All(noteTextBlocks, tb => Assert.False(tb.IsVisible));
        }
        finally
        {
            window.Close();
        }
    }
}
