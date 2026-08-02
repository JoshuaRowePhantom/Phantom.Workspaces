using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Models;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class UsageTrackerControlTests
{
    [AvaloniaFact(Timeout = 15_000)]
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

    [AvaloniaFact(Timeout = 15_000)]
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
            // Account hyperlink + one hyperlink per metric row (issue #1149).
            var hyperlinkButtons = window.GetVisualDescendants().OfType<HyperlinkButton>().ToList();
            Assert.True(hyperlinkButtons.Count >= 1);
            
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

    [AvaloniaFact(Timeout = 15_000)]
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
            // #1172: URL now flows via CommandParameter (routed through IUrlOpener) instead of
            // NavigateUri (which would leak to the OS default browser).
            var hyperlinkButtons = window.GetVisualDescendants().OfType<HyperlinkButton>().ToList();
            Assert.Contains(hyperlinkButtons, hb => hb.CommandParameter is Uri u
                && u == new Uri("https://github.com/settings/copilot"));
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void UsageTrackerControl_MetricRow_RendersHyperlinkWithWebUrl()
    {
        var metrics = new UsageMetrics();
        var account = new UsageAccount
        {
            Product = "GitHub Copilot",
            UserName = "testuser",
            SettingsUrl = new Uri("https://github.com/settings/copilot"),
        };
        account.Metrics.Add(new UsageMetric
        {
            Title = "Included Usage",
            QuantityUsed = 100m,
            QuantityTotal = 200m,
            QuantityPresentationFormatString = "{0} / {1}",
            WebUrl = new Uri("https://github.com/settings/billing/summary?user=testuser"),
        });
        metrics.Accounts.Add(account);

        var viewModel = new UsageTrackerViewModel(metrics);
        var control = new UsageTrackerControl { DataContext = viewModel };
        var window = new Window { Content = control };
        window.Show();

        try
        {
            var metricHyperlink = window.GetVisualDescendants().OfType<HyperlinkButton>()
                .FirstOrDefault(hb => hb.Name == "MetricRowHyperlink");
            Assert.NotNull(metricHyperlink);
            // #1172: NavigateUri is intentionally NOT set — the URL flows via CommandParameter.
            Assert.Null(metricHyperlink!.NavigateUri);
            Assert.Equal(new Uri("https://github.com/settings/billing/summary?user=testuser"), metricHyperlink.CommandParameter as Uri);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void UsageTrackerControl_MetricRow_InvokingOpenLaunchesWebUrl()
    {
        var metrics = new UsageMetrics();
        var account = new UsageAccount
        {
            Product = "GitHub Copilot",
            UserName = "testuser",
            SettingsUrl = new Uri("https://github.com/settings/copilot"),
        };
        var expectedUrl = new Uri("https://github.com/settings/billing/summary?user=testuser");
        account.Metrics.Add(new UsageMetric
        {
            Title = "Included Usage",
            QuantityUsed = 100m,
            QuantityTotal = 200m,
            QuantityPresentationFormatString = "{0} / {1}",
            WebUrl = expectedUrl,
        });
        metrics.Accounts.Add(account);

        var viewModel = new UsageTrackerViewModel(metrics);
        var control = new UsageTrackerControl { DataContext = viewModel };
        var window = new Window { Content = control };
        window.Show();

        try
        {
            var metricHyperlink = window.GetVisualDescendants().OfType<HyperlinkButton>()
                .First(hb => hb.Name == "MetricRowHyperlink");

            // #1172: CommandParameter (not NavigateUri) carries the URL.
            Assert.Equal(expectedUrl, metricHyperlink.CommandParameter as Uri);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void UsageTrackerControl_MetricRow_FallsBackToAccountUrl_WhenMetricWebUrlNull()
    {
        var accountUrl = new Uri("https://github.com/settings/copilot");
        var metrics = new UsageMetrics();
        var account = new UsageAccount
        {
            Product = "GitHub Copilot",
            UserName = "testuser",
            SettingsUrl = accountUrl,
        };
        account.Metrics.Add(new UsageMetric
        {
            Title = "Included Usage",
            QuantityUsed = 100m,
            QuantityTotal = 200m,
            QuantityPresentationFormatString = "{0} / {1}",
            // WebUrl not set — falls back to account SettingsUrl.
        });
        metrics.Accounts.Add(account);

        var viewModel = new UsageTrackerViewModel(metrics);
        var control = new UsageTrackerControl { DataContext = viewModel };
        var window = new Window { Content = control };
        window.Show();

        try
        {
            var metricHyperlink = window.GetVisualDescendants().OfType<HyperlinkButton>()
                .First(hb => hb.Name == "MetricRowHyperlink");
            Assert.Equal(accountUrl, metricHyperlink.CommandParameter as Uri);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void UsageTrackerControl_RowWithKnownFraction_UsedBarHasProportionalWidth()
    {
        var metrics = new UsageMetrics();
        var account = new UsageAccount
        {
            Product = "GitHub Copilot",
            UserName = "testuser",
            SettingsUrl = new Uri("https://github.com/settings/copilot"),
        };
        account.Metrics.Add(new UsageMetric
        {
            Title = "Included Usage",
            QuantityUsed = 60m,
            QuantityTotal = 120m,
            QuantityPresentationFormatString = "{0} / {1}",
        });
        metrics.Accounts.Add(account);

        var viewModel = new UsageTrackerViewModel(metrics);
        var control = new UsageTrackerControl { DataContext = viewModel };
        var window = new Window { Content = control };
        window.Show();

        try
        {
            var usedBar = window.GetVisualDescendants().OfType<Border>()
                .First(b => b.Name == "UsedBar");
            // #1179: bar column width reduced to 80 so the metric-name column can grow.
            // Fraction = 0.5, ConverterParameter total width = 80 → expected width = 40.
            Assert.Equal(40.0, usedBar.Width);
            Assert.True(usedBar.IsVisible);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void UsageTrackerControl_RowWithUnknownFraction_DoesNotAppearFullyFilled()
    {
        var metrics = new UsageMetrics();
        var account = new UsageAccount
        {
            Product = "GitHub Copilot",
            UserName = "testuser",
            SettingsUrl = new Uri("https://github.com/settings/copilot"),
        };
        account.Metrics.Add(new UsageMetric
        {
            Title = "Included Usage",
            QuantityUsed = 100m,
            QuantityTotal = 0m, // FractionUsed = null → unknown-limit state
            QuantityPresentationFormatString = "{0}",
        });
        metrics.Accounts.Add(account);

        var viewModel = new UsageTrackerViewModel(metrics);
        var control = new UsageTrackerControl { DataContext = viewModel };
        var window = new Window { Content = control };
        window.Show();

        try
        {
            var remaining = window.GetVisualDescendants().OfType<Border>()
                .First(b => b.Name == "RemainingBar");
            var used = window.GetVisualDescendants().OfType<Border>()
                .First(b => b.Name == "UsedBar");
            var unknown = window.GetVisualDescendants().OfType<Border>()
                .First(b => b.Name == "UnknownLimitBar");

            // The green "remaining" bar and yellow "used" bar must not be shown when the
            // limit is unknown — otherwise the row visually reads as "100% full green".
            Assert.False(remaining.IsVisible);
            Assert.False(used.IsVisible);
            // A distinct unknown-limit indicator is shown instead, linking to the GitHub
            // Copilot features page (#1159 Fix B/C).
            Assert.True(unknown.IsVisible);

            var unknownLink = window.GetVisualDescendants().OfType<HyperlinkButton>()
                .First(hb => hb.Name == "UnknownLimitLink");
            // #1172: CommandParameter carries the URL; NavigateUri is not set.
            Assert.Equal(
                "https://github.com/settings/copilot/features",
                unknownLink.CommandParameter as string);
            Assert.Null(unknownLink.NavigateUri);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // #1172 — HyperlinkButton XAML wiring: bound to OpenUrlCommand with NavigateUri unset.
    // ---------------------------------------------------------------------------------------------

    [AvaloniaFact(Timeout = 15_000)]
    public void UsageTrackerControl_MetricRowHyperlink_IsBoundToOpenUrlCommand()
    {
        var metrics = new UsageMetrics();
        var account = new UsageAccount
        {
            Product = "GitHub Copilot",
            UserName = "testuser",
            SettingsUrl = new Uri("https://github.com/settings/copilot"),
        };
        account.Metrics.Add(new UsageMetric
        {
            Title = "Included Usage",
            QuantityUsed = 1m,
            QuantityTotal = 10m,
            WebUrl = new Uri("https://example.com/"),
        });
        metrics.Accounts.Add(account);

        var viewModel = new UsageTrackerViewModel(metrics);
        var control = new UsageTrackerControl { DataContext = viewModel };
        var window = new Window { Content = control };
        window.Show();
        try
        {
            var link = window.GetVisualDescendants().OfType<HyperlinkButton>()
                .First(hb => hb.Name == "MetricRowHyperlink");
            Assert.Same(viewModel.OpenUrlCommand, link.Command);
            Assert.Null(link.NavigateUri);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void UsageTrackerControl_ManageOnGitHubHyperlink_IsBoundToOpenUrlCommand()
    {
        var metrics = new UsageMetrics();
        var viewModel = new UsageTrackerViewModel(metrics);
        var control = new UsageTrackerControl { DataContext = viewModel };
        var window = new Window { Content = control };
        window.Show();
        try
        {
            var link = window.GetVisualDescendants().OfType<HyperlinkButton>()
                .First(hb => hb.Name == "ManageOnGitHubHyperlink");
            Assert.Same(viewModel.OpenUrlCommand, link.Command);
            Assert.Null(link.NavigateUri);
            Assert.Equal("https://github.com/settings/copilot/features", link.CommandParameter as string);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void UsageTrackerControl_UnknownLimitLink_IsBoundToOpenUrlCommand()
    {
        var metrics = new UsageMetrics();
        var account = new UsageAccount
        {
            Product = "GitHub Copilot",
            UserName = "testuser",
            SettingsUrl = new Uri("https://github.com/settings/copilot"),
        };
        account.Metrics.Add(new UsageMetric
        {
            Title = "Included Usage",
            QuantityUsed = 100m,
            QuantityTotal = 0m, // FractionUsed = null → unknown-limit
            QuantityPresentationFormatString = "{0}",
        });
        metrics.Accounts.Add(account);

        var viewModel = new UsageTrackerViewModel(metrics);
        var control = new UsageTrackerControl { DataContext = viewModel };
        var window = new Window { Content = control };
        window.Show();
        try
        {
            var link = window.GetVisualDescendants().OfType<HyperlinkButton>()
                .First(hb => hb.Name == "UnknownLimitLink");
            Assert.Same(viewModel.OpenUrlCommand, link.Command);
            Assert.Null(link.NavigateUri);
        }
        finally
        {
            window.Close();
            viewModel.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // #1179 — Metric-name column must not clip long names like "Copilot Premium Requests" or
    // "Copilot AI Credits (Cost)". The row grid uses *,Auto,80 so the name grows and the
    // progress bar (with a MinWidth floor) is the shrinker.
    // ---------------------------------------------------------------------------------------------

    private static Grid FindMetricRowGrid(Window window)
    {
        // The per-metric row grid is the direct child (visual descendant) of the
        // MetricRowHyperlink and contains three ColumnDefinitions.
        var link = window.GetVisualDescendants().OfType<HyperlinkButton>()
            .First(hb => hb.Name == "MetricRowHyperlink");
        return link.GetVisualDescendants().OfType<Grid>()
            .First(g => g.ColumnDefinitions.Count == 3);
    }

    private static (UsageTrackerViewModel vm, Window window) BuildControlWithSingleMetric(string title)
    {
        var metrics = new UsageMetrics();
        var account = new UsageAccount
        {
            Product = "GitHub Copilot",
            UserName = "testuser",
            SettingsUrl = new Uri("https://github.com/settings/copilot"),
        };
        account.Metrics.Add(new UsageMetric
        {
            Title = title,
            QuantityUsed = 10m,
            QuantityTotal = 100m,
            QuantityPresentationFormatString = "{0} / {1}",
        });
        metrics.Accounts.Add(account);

        var viewModel = new UsageTrackerViewModel(metrics);
        var control = new UsageTrackerControl { DataContext = viewModel };
        var window = new Window { Content = control };
        window.Show();
        return (viewModel, window);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void UsageTrackerControl_MetricNameColumn_IsStarSized()
    {
        var (vm, window) = BuildControlWithSingleMetric("Copilot Premium Requests");
        try
        {
            var grid = FindMetricRowGrid(window);
            var nameColumn = grid.ColumnDefinitions[0];
            Assert.True(nameColumn.Width.IsStar,
                $"Expected metric-name column to be Star-sized; got {nameColumn.Width}.");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void UsageTrackerControl_ProgressBarColumn_IsTheShrinkingColumn()
    {
        var (vm, window) = BuildControlWithSingleMetric("Copilot Premium Requests");
        try
        {
            var grid = FindMetricRowGrid(window);
            var barColumn = grid.ColumnDefinitions[2];
            // The bar column must be sized in pixels (not Star) so the name column absorbs
            // any extra width, and it must have a bounded MinWidth so it shrinks before the
            // name is starved but never collapses to zero.
            Assert.False(barColumn.Width.IsStar,
                $"Expected progress-bar column to be pixel-sized (Auto or absolute); got {barColumn.Width}.");
            Assert.True(barColumn.MinWidth > 0 && barColumn.MinWidth < 120,
                $"Expected progress-bar column MinWidth in (0,120); got {barColumn.MinWidth}.");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void UsageTrackerControl_MetricTitleTextBlock_HasNoCharacterEllipsisTrimming()
    {
        var (vm, window) = BuildControlWithSingleMetric("Copilot AI Credits (Cost)");
        try
        {
            var link = window.GetVisualDescendants().OfType<HyperlinkButton>()
                .First(hb => hb.Name == "MetricRowHyperlink");
            var titleBlock = link.GetVisualDescendants().OfType<TextBlock>()
                .First(tb => (tb.Text ?? string.Empty) == "Copilot AI Credits (Cost)");
            Assert.NotEqual(TextTrimming.CharacterEllipsis, titleBlock.TextTrimming);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void UsageTrackerControl_LongMetricName_IsNotClippedAtDefaultWidth()
    {
        var (vm, window) = BuildControlWithSingleMetric("Copilot Premium Requests");
        try
        {
            window.Measure(Size.Infinity);
            window.Arrange(new Rect(window.DesiredSize));
            window.UpdateLayout();

            var link = window.GetVisualDescendants().OfType<HyperlinkButton>()
                .First(hb => hb.Name == "MetricRowHyperlink");
            var titleBlock = link.GetVisualDescendants().OfType<TextBlock>()
                .First(tb => (tb.Text ?? string.Empty) == "Copilot Premium Requests");

            // The rendered arrange bounds must be at least the intrinsic desired size — i.e.
            // the name column gave the TextBlock all the width it needs and did not clip it.
            Assert.True(
                titleBlock.Bounds.Width + 0.5 >= titleBlock.DesiredSize.Width,
                $"Metric name was clipped: Bounds.Width={titleBlock.Bounds.Width} < DesiredSize.Width={titleBlock.DesiredSize.Width}.");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    // #1188 — When an account has a populated ResetsAt, the flyout renders a "Resets on
    // {date}" line under the account header (bound to ResetsAtDisplay).
    [AvaloniaFact(Timeout = 15_000)]
    public void UsageTrackerControl_ResetDate_IsRendered()
    {
        var metrics = new UsageMetrics();
        var account = new UsageAccount
        {
            Product = "github.com",
            UserName = "alice",
            SettingsUrl = new Uri("https://github.com/settings/billing/summary"),
            ResetsAt = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero),
        };
        account.Metrics.Add(new UsageMetric
        {
            Title = "Copilot AI Credits",
            QuantityUsed = 10769m,
            QuantityTotal = 20000m,
            QuantityPresentationFormatString = "{0:N0} / {1:N0} {2}",
            Unit = "AICredits",
        });
        metrics.Accounts.Add(account);

        using var vm = new UsageTrackerViewModel(metrics);
        var control = new UsageTrackerControl { DataContext = vm };

        var window = new Window { Content = control };
        window.Show();

        try
        {
            var resetsTextBlock = window.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(tb => tb.Name == "ResetsOnText");
            Assert.NotNull(resetsTextBlock);
            Assert.True(resetsTextBlock!.IsVisible, "ResetsOnText should be visible when ResetsAt is set.");
            Assert.Contains("Aug 31, 2026", resetsTextBlock.Text ?? string.Empty);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
