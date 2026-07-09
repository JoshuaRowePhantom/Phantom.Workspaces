using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Models;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class UsageTrackerControlTests
{
    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void UsageTrackerControl_Instantiates_WithEmptyAccounts()
    {
        var metrics = new UsageMetrics();
        var viewModel = new UsageTrackerViewModel(metrics);
        var control = new UsageTrackerControl { DataContext = viewModel };
        
        var window = new Window { Content = control };
        window.Show();
        
        try
        {
            Assert.NotNull(control);
            var itemsControls = window.GetVisualDescendants().OfType<ItemsControl>().ToList();
            Assert.NotEmpty(itemsControls);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void UsageTrackerControl_RendersAccountCard()
    {
        var metrics = new UsageMetrics();
        var account = new UsageAccount
        {
            Product = "GitHub Copilot",
            UserName = "testuser",
            SettingsUrl = new Uri("https://github.com/settings/copilot")
        };
        account.Metrics.Add(new UsageMetric
        {
            Title = "Included Usage",
            QuantityUsed = 10000m,
            QuantityTotal = 20000m,
            QuantityPresentationFormatString = "{0:N0} / {1:N0} {2}",
            Unit = "AIC"
        });
        account.Metrics.Add(new UsageMetric
        {
            Title = "Additional Usage",
            QuantityUsed = 14.57m,
            QuantityTotal = 75m,
            QuantityPresentationFormatString = "${0:N2} / ${1:N2}",
            Unit = string.Empty
        });
        metrics.Accounts.Add(account);

        var viewModel = new UsageTrackerViewModel(metrics);
        var control = new UsageTrackerControl { DataContext = viewModel };
        
        var window = new Window { Content = control };
        window.Show();
        
        try
        {
            // Should have one HyperlinkButton for the account
            var hyperlinkButtons = window.GetVisualDescendants().OfType<HyperlinkButton>().ToList();
            Assert.Single(hyperlinkButtons);
            
            // Should have two progress bars (one for each metric)
            var borders = window.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "UsedBar" || b.Name == "RemainingBar").ToList();
            Assert.True(borders.Count >= 2, "Expected at least 2 progress bar borders");
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void UsageTrackerControl_HyperlinkButton_HasCorrectUrl()
    {
        var metrics = new UsageMetrics();
        var account = new UsageAccount
        {
            Product = "GitHub Copilot",
            UserName = "testuser",
            SettingsUrl = new Uri("https://github.com/settings/copilot")
        };
        account.Metrics.Add(new UsageMetric
        {
            Title = "Included Usage",
            QuantityUsed = 10000m,
            QuantityTotal = 20000m
        });
        metrics.Accounts.Add(account);

        var viewModel = new UsageTrackerViewModel(metrics);
        var control = new UsageTrackerControl { DataContext = viewModel };
        
        var window = new Window { Content = control };
        window.Show();
        
        try
        {
            var hyperlinkButton = window.GetVisualDescendants().OfType<HyperlinkButton>().First();
            Assert.Equal(new Uri("https://github.com/settings/copilot"), hyperlinkButton.NavigateUri);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
        }
    }
}
