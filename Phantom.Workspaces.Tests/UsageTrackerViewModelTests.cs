using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using Phantom.Workspaces.Models;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class UsageTrackerViewModelTests
{
    public UsageTrackerViewModelTests()
    {
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
    }
    [Fact]
    public void RecomputeTopRightLabel_WhenNoAccounts_LogsHiddenReason()
    {
        var metrics = new UsageMetrics();
        var logger = new TestLogger<UsageTrackerViewModel>();
        using var vm = new UsageTrackerViewModel(metrics, logger);

        Assert.Contains(
            logger.Entries,
            e => e.Message.Contains("hidden", StringComparison.OrdinalIgnoreCase)
                && e.Message.Contains("no accounts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RecomputeTopRightLabel_WhenAccountsExist_LogsPanelShown()
    {
        var metrics = new UsageMetrics();
        var account = new UsageAccount { Product = "GitHub", UserName = "alice" };
        account.Metrics.Add(new UsageMetric
        {
            Title = "Included Usage",
            QuantityUsed = 1m,
            QuantityTotal = 5m,
            QuantityPresentationFormatString = "{0:N0} / {1:N0} {2}",
            Unit = "minutes",
        });
        metrics.Accounts.Add(account);

        var logger = new TestLogger<UsageTrackerViewModel>();
        using var vm = new UsageTrackerViewModel(metrics, logger);

        Assert.Contains(
            logger.Entries,
            e => e.Level == Microsoft.Extensions.Logging.LogLevel.Information
                && e.Message.Contains("shown", StringComparison.OrdinalIgnoreCase)
                && e.Message.Contains("1", StringComparison.Ordinal));
    }

    [Fact]
    public void TopRightLabel_IsNull_WhenNoAccounts()
    {
        var metrics = new UsageMetrics();
        using var vm = new UsageTrackerViewModel(metrics);

        Assert.Null(vm.TopRightLabel);
    }

    [Fact]
    public void TopRightLabel_ReflectsFirstMetric_WhenAccountAdded()
    {
        var metrics = new UsageMetrics();
        using var vm = new UsageTrackerViewModel(metrics);

        var account = new UsageAccount { Product = "GitHub", UserName = "alice" };
        account.Metrics.Add(new UsageMetric
        {
            Title = "Included Usage",
            QuantityUsed = 100m,
            QuantityTotal = 500m,
            QuantityPresentationFormatString = "{0:N0} / {1:N0} {2}",
            Unit = "minutes",
        });

        metrics.Accounts.Add(account);

        Assert.NotNull(vm.TopRightLabel);
        Assert.Contains("100", vm.TopRightLabel);
    }

    [Fact]
    public void TopRightLabel_CopilotAccountAdded_BecomesNonNull()
    {
        // Once a routed GitHub Copilot account surfaces metrics, the AI usage indicator label must
        // become non-null so the indicator is shown (issue #1041).
        var metrics = new UsageMetrics();
        using var vm = new UsageTrackerViewModel(metrics);

        Assert.Null(vm.TopRightLabel);

        var copilotAccount = new UsageAccount
        {
            Product = "github.com",
            UserName = "octocat",
            SettingsUrl = new Uri("https://github.com/copilot"),
        };
        copilotAccount.Metrics.Add(new UsageMetric
        {
            Title = "Included Usage",
            QuantityUsed = 42m,
            QuantityTotal = 300m,
            QuantityPresentationFormatString = "{0:N0} / {1:N0} {2}",
            Unit = "AIC",
        });

        metrics.Accounts.Add(copilotAccount);

        Assert.NotNull(vm.TopRightLabel);
        Assert.Contains("42", vm.TopRightLabel);
    }

    [Fact]
    public void TopRightLabel_BecomesNull_WhenAccountRemoved()
    {
        var metrics = new UsageMetrics();
        using var vm = new UsageTrackerViewModel(metrics);

        var account = new UsageAccount { Product = "GitHub", UserName = "alice" };
        account.Metrics.Add(new UsageMetric
        {
            Title = "Included Usage",
            QuantityUsed = 10m,
            QuantityTotal = 50m,
        });

        metrics.Accounts.Add(account);
        Assert.NotNull(vm.TopRightLabel);

        metrics.Accounts.Remove(account);
        Assert.Null(vm.TopRightLabel);
    }

    [Fact]
    public void TopRightLabel_RaisesPropertyChanged_WhenMetricUpdated()
    {
        var metrics = new UsageMetrics();
        using var vm = new UsageTrackerViewModel(metrics);

        var metric = new UsageMetric
        {
            Title = "Included Usage",
            QuantityUsed = 10m,
            QuantityTotal = 50m,
            QuantityPresentationFormatString = "{0:N0} / {1:N0} {2}",
            Unit = "minutes",
        };

        var account = new UsageAccount { Product = "GitHub", UserName = "alice" };
        account.Metrics.Add(metric);
        metrics.Accounts.Add(account);

        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        metric.QuantityUsed = 25m;

        Assert.Contains(nameof(UsageTrackerViewModel.TopRightLabel), changes);
    }

    [Fact]
    public void Accounts_UpdatesWhenAccountCollectionChanges()
    {
        var metrics = new UsageMetrics();
        using var vm = new UsageTrackerViewModel(metrics);

        Assert.Empty(vm.Accounts);

        var account = new UsageAccount { Product = "GitHub", UserName = "alice" };
        metrics.Accounts.Add(account);

        Assert.Single(vm.Accounts);
    }

    [Fact]
    public void ToggleOpenCommand_TogglesIsOpen()
    {
        var metrics = new UsageMetrics();
        using var vm = new UsageTrackerViewModel(metrics);

        Assert.False(vm.IsOpen);
        vm.ToggleOpenCommand.Execute(null);
        Assert.True(vm.IsOpen);
        vm.ToggleOpenCommand.Execute(null);
        Assert.False(vm.IsOpen);
    }

    [Fact]
    public void Dispose_DoesNotThrow_WhenCalledTwice()
    {
        var metrics = new UsageMetrics();
        var vm = new UsageTrackerViewModel(metrics);
        vm.Dispose();
        vm.Dispose(); // should not throw
    }

    [Fact]
    public void TopRightLabel_IsNull_WhenAccountHasNoMetrics()
    {
        var metrics = new UsageMetrics();
        using var vm = new UsageTrackerViewModel(metrics);

        var account = new UsageAccount { Product = "GitHub", UserName = "alice" };
        metrics.Accounts.Add(account); // no metrics added

        Assert.Null(vm.TopRightLabel);
    }

    [Fact]
    public void UsageTrackerViewModel_TopRightLabel_ShowsMostRecentlyUpdatedMetric()
    {
        var metrics = new UsageMetrics();
        using var vm = new UsageTrackerViewModel(metrics);

        var account1 = new UsageAccount { Product = "GitHub Copilot", UserName = "user1" };
        account1.Metrics.Add(new UsageMetric
        {
            Title = "Included Usage",
            QuantityUsed = 100m,
            QuantityTotal = 500m,
            QuantityPresentationFormatString = "{0:N0} / {1:N0} {2}",
            Unit = "minutes",
            LastUpdatedAt = new DateTime(2026, 1, 1, 10, 0, 0)
        });
        metrics.Accounts.Add(account1);

        var account2 = new UsageAccount { Product = "GitHub Actions", UserName = "user2" };
        account2.Metrics.Add(new UsageMetric
        {
            Title = "Additional Usage",
            QuantityUsed = 200m,
            QuantityTotal = 1000m,
            QuantityPresentationFormatString = "{0:N0} / {1:N0} {2}",
            Unit = "AIC",
            LastUpdatedAt = new DateTime(2026, 1, 1, 12, 0, 0) // Later
        });
        metrics.Accounts.Add(account2);

        Assert.Equal("200 / 1,000 AIC", vm.TopRightLabel);
    }

    [Fact]
    public void UsageTrackerViewModel_TopRightLabel_FallsBackToFirstAccount_WhenNoLastUpdatedAt()
    {
        var metrics = new UsageMetrics();
        using var vm = new UsageTrackerViewModel(metrics);

        var account1 = new UsageAccount { Product = "GitHub Copilot", UserName = "user1" };
        account1.Metrics.Add(new UsageMetric
        {
            Title = "Included Usage",
            QuantityUsed = 100m,
            QuantityTotal = 500m,
            QuantityPresentationFormatString = "{0:N0} / {1:N0} {2}",
            Unit = "minutes",
            LastUpdatedAt = null
        });
        metrics.Accounts.Add(account1);

        var account2 = new UsageAccount { Product = "GitHub Actions", UserName = "user2" };
        account2.Metrics.Add(new UsageMetric
        {
            Title = "Additional Usage",
            QuantityUsed = 200m,
            QuantityTotal = 1000m,
            QuantityPresentationFormatString = "{0:N0} / {1:N0} {2}",
            Unit = "AIC",
            LastUpdatedAt = null
        });
        metrics.Accounts.Add(account2);

        Assert.Equal("100 / 500 minutes", vm.TopRightLabel);
    }

    [Fact]
    public void UsageTrackerViewModel_TopRightLabel_UpdatesWhenNewerMetricAppears()
    {
        var metrics = new UsageMetrics();
        using var vm = new UsageTrackerViewModel(metrics);

        var account1 = new UsageAccount { Product = "GitHub Copilot", UserName = "user1" };
        account1.Metrics.Add(new UsageMetric
        {
            Title = "Included Usage",
            QuantityUsed = 100m,
            QuantityTotal = 500m,
            QuantityPresentationFormatString = "{0:N0} / {1:N0} {2}",
            Unit = "minutes",
            LastUpdatedAt = new DateTime(2026, 1, 1, 10, 0, 0)
        });
        metrics.Accounts.Add(account1);

        Assert.Equal("100 / 500 minutes", vm.TopRightLabel);

        var account2 = new UsageAccount { Product = "GitHub Actions", UserName = "user2" };
        account2.Metrics.Add(new UsageMetric
        {
            Title = "Additional Usage",
            QuantityUsed = 200m,
            QuantityTotal = 1000m,
            QuantityPresentationFormatString = "{0:N0} / {1:N0} {2}",
            Unit = "AIC",
            LastUpdatedAt = new DateTime(2026, 1, 1, 12, 0, 0) // Later
        });
        metrics.Accounts.Add(account2);

        Assert.Equal("200 / 1,000 AIC", vm.TopRightLabel);
    }

    [Fact]
    public void UsageTrackerViewModel_Dispose_UnsubscribesFromMetricEvents()
    {
        var metrics = new UsageMetrics();
        var vm = new UsageTrackerViewModel(metrics);

        var account = new UsageAccount { Product = "GitHub Copilot", UserName = "user1" };
        var metric = new UsageMetric
        {
            Title = "Included Usage",
            QuantityUsed = 100m,
            QuantityTotal = 500m,
            QuantityPresentationFormatString = "{0:N0} / {1:N0} {2}",
            Unit = "minutes",
            LastUpdatedAt = new DateTime(2026, 1, 1, 10, 0, 0)
        };
        account.Metrics.Add(metric);
        metrics.Accounts.Add(account);

        var originalLabel = vm.TopRightLabel;

        vm.Dispose();

        metric.QuantityUsed = 999m;
        
        Assert.Equal(originalLabel, vm.TopRightLabel);
    }

    [Fact]
    public void SelectedUsageMetricKey_WhenSetToMetric_TopRightLabelShowsThatMetric()
    {
        var metrics = new UsageMetrics();
        using var vm = new UsageTrackerViewModel(metrics);

        var account = new UsageAccount { Product = "github.com", UserName = "u" };
        account.Metrics.Add(new UsageMetric
        {
            Title = "Copilot AI Credits",
            QuantityUsed = 100m,
            QuantityPresentationFormatString = "{0:N0} {2}",
            Unit = "AICredits",
            LastUpdatedAt = new DateTime(2026, 1, 1, 10, 0, 0),
        });
        account.Metrics.Add(new UsageMetric
        {
            Title = "Copilot Premium Request",
            QuantityUsed = 42m,
            QuantityPresentationFormatString = "{0:N0} {2}",
            Unit = "Requests",
            LastUpdatedAt = new DateTime(2026, 1, 1, 12, 0, 0),
        });
        metrics.Accounts.Add(account);

        vm.SelectedUsageMetricKey = UsageAccount.ComposeKey("github.com", "Copilot AI Credits");

        Assert.Equal("100 AICredits", vm.TopRightLabel);
    }

    [Fact]
    public void SelectedUsageMetricKey_WhenChanged_PersistsKeyToConfiguration()
    {
        var metrics = new UsageMetrics();
        string? persistedKey = null;
        var tcs = new System.Threading.Tasks.TaskCompletionSource();
        using var vm = new UsageTrackerViewModel(
            metrics,
            logger: null,
            initialSelectedUsageMetricKey: null,
            persistSelectionAsync: k => { persistedKey = k; tcs.TrySetResult(); return System.Threading.Tasks.Task.CompletedTask; });

        var account = new UsageAccount { Product = "github.com", UserName = "u" };
        account.Metrics.Add(new UsageMetric { Title = "M", QuantityPresentationFormatString = "{0}" });
        metrics.Accounts.Add(account);

        vm.SelectedUsageMetricKey = "github.com/M";

        Assert.True(tcs.Task.IsCompletedSuccessfully);
        Assert.Equal("github.com/M", persistedKey);
    }

    [Fact]
    public void UsageTrackerViewModel_WhenSeededFromConfig_RestoresSelectedMetricAsDefault()
    {
        var metrics = new UsageMetrics();
        var account = new UsageAccount { Product = "github.com", UserName = "u" };
        account.Metrics.Add(new UsageMetric
        {
            Title = "Copilot AI Credits",
            QuantityUsed = 100m,
            QuantityPresentationFormatString = "{0:N0} {2}",
            Unit = "AICredits",
            LastUpdatedAt = new DateTime(2026, 1, 1, 10, 0, 0),
        });
        account.Metrics.Add(new UsageMetric
        {
            Title = "Copilot Premium Request",
            QuantityUsed = 42m,
            QuantityPresentationFormatString = "{0:N0} {2}",
            Unit = "Requests",
            LastUpdatedAt = new DateTime(2026, 1, 1, 12, 0, 0),
        });
        metrics.Accounts.Add(account);

        using var vm = new UsageTrackerViewModel(
            metrics,
            logger: null,
            initialSelectedUsageMetricKey: UsageAccount.ComposeKey("github.com", "Copilot AI Credits"));

        Assert.Equal("100 AICredits", vm.TopRightLabel);
    }

    [Fact]
    public void RecomputeTopRightLabel_WhenSelectedMetricAbsent_FallsBackGracefully()
    {
        var metrics = new UsageMetrics();
        var account = new UsageAccount { Product = "github.com", UserName = "u" };
        account.Metrics.Add(new UsageMetric
        {
            Title = "Copilot Premium Request",
            QuantityUsed = 42m,
            QuantityPresentationFormatString = "{0:N0} {2}",
            Unit = "Requests",
            LastUpdatedAt = new DateTime(2026, 1, 1, 12, 0, 0),
        });
        metrics.Accounts.Add(account);

        using var vm = new UsageTrackerViewModel(
            metrics,
            logger: null,
            initialSelectedUsageMetricKey: "github.com/Does Not Exist");

        // Falls back to most-recently-updated metric.
        Assert.Equal("42 Requests", vm.TopRightLabel);
        // Key is preserved so pin re-applies if the metric reappears.
        Assert.Equal("github.com/Does Not Exist", vm.SelectedUsageMetricKey);
    }

    [Fact]
    public void SelectedUsageMetric_WhenTwoMetricsExist_OnlyOneRadioIsSelected()
    {
        var metrics = new UsageMetrics();
        var account = new UsageAccount { Product = "github.com", UserName = "u" };
        var m1 = new UsageMetric { Title = "A", QuantityPresentationFormatString = "{0}" };
        var m2 = new UsageMetric { Title = "B", QuantityPresentationFormatString = "{0}" };
        account.Metrics.Add(m1);
        account.Metrics.Add(m2);
        metrics.Accounts.Add(account);

        using var vm = new UsageTrackerViewModel(metrics);

        vm.SelectedUsageMetricKey = UsageAccount.ComposeKey("github.com", "A");
        Assert.True(m1.IsSelectedAsShown);
        Assert.False(m2.IsSelectedAsShown);

        vm.SelectedUsageMetricKey = UsageAccount.ComposeKey("github.com", "B");
        Assert.False(m1.IsSelectedAsShown);
        Assert.True(m2.IsSelectedAsShown);
    }

    // ---------------------------------------------------------------------------------------------
    // #1172 — OpenUrlCommand routes through IUrlOpener.
    // ---------------------------------------------------------------------------------------------

    private sealed class RecordingUrlOpener : Phantom.Workspaces.Services.IUrlOpener
    {
        public List<Phantom.Workspaces.Services.OpenUrlRequest> Requests { get; } = new();

        public System.Threading.Tasks.Task OpenAsync(Phantom.Workspaces.Services.OpenUrlRequest request, System.Threading.CancellationToken cancellationToken = default)
        {
            this.Requests.Add(request);
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    [Fact]
    public void OpenUrlCommand_WhenMetricRowInvoked_CallsUrlOpenerWithMetricWebUrl()
    {
        var metrics = new UsageMetrics();
        var opener = new RecordingUrlOpener();
        using var vm = new UsageTrackerViewModel(
            metrics, logger: null, initialSelectedUsageMetricKey: null, persistSelectionAsync: null,
            urlOpenerProvider: () => opener);

        var webUrl = "https://github.com/settings/billing/summary?user=octocat";
        vm.OpenUrlCommand.Execute(webUrl);

        Assert.Single(opener.Requests);
        Assert.Equal(webUrl, opener.Requests[0].Url);
        Assert.Equal(Phantom.Workspaces.Services.UrlOpenPreference.Auto, opener.Requests[0].Preference);
    }

    [Fact]
    public void OpenUrlCommand_WhenAccountHeaderInvoked_CallsUrlOpenerWithSettingsUrl()
    {
        var metrics = new UsageMetrics();
        var opener = new RecordingUrlOpener();
        using var vm = new UsageTrackerViewModel(
            metrics, logger: null, initialSelectedUsageMetricKey: null, persistSelectionAsync: null,
            urlOpenerProvider: () => opener);

        vm.OpenUrlCommand.Execute("https://github.com/settings/copilot");

        Assert.Single(opener.Requests);
        Assert.Equal("https://github.com/settings/copilot", opener.Requests[0].Url);
    }

    [Fact]
    public void OpenUrlCommand_WhenUrlIsNullOrEmpty_DoesNotCallUrlOpener()
    {
        var metrics = new UsageMetrics();
        var opener = new RecordingUrlOpener();
        using var vm = new UsageTrackerViewModel(
            metrics, logger: null, initialSelectedUsageMetricKey: null, persistSelectionAsync: null,
            urlOpenerProvider: () => opener);

        vm.OpenUrlCommand.Execute(null);
        vm.OpenUrlCommand.Execute(string.Empty);

        Assert.Empty(opener.Requests);
    }

    [Fact]
    public void OpenUrlCommand_WhenInvoked_DoesNotCallExternalLauncherDirectly()
    {
        // The view model must never touch Launcher / Process.Start directly — routing is entirely
        // delegated to IUrlOpener. Verified by asserting the URL flowed through the injected opener
        // (no other observable side effect exists).
        var metrics = new UsageMetrics();
        var opener = new RecordingUrlOpener();
        using var vm = new UsageTrackerViewModel(
            metrics, logger: null, initialSelectedUsageMetricKey: null, persistSelectionAsync: null,
            urlOpenerProvider: () => opener);

        vm.OpenUrlCommand.Execute("https://example.com/");

        Assert.Single(opener.Requests);
    }

    // #1179 — long, unabbreviated metric titles round-trip through the view model without
    // any view-model-side truncation. Truncation only happens (or fails to happen) in XAML.
    [Fact]
    public void UsageTrackerViewModel_LongMetricTitle_IsExposedUntruncated()
    {
        const string longTitle = "Copilot AI Credits (Cost)";
        var metrics = new UsageMetrics();
        var account = new UsageAccount { Product = "GitHub Copilot", UserName = "testuser" };
        account.Metrics.Add(new UsageMetric
        {
            Title = longTitle,
            QuantityUsed = 1m,
            QuantityTotal = 10m,
        });
        metrics.Accounts.Add(account);

        using var vm = new UsageTrackerViewModel(metrics);

        Assert.Single(vm.Accounts);
        Assert.Single(vm.Accounts[0].Metrics);
        Assert.Equal(longTitle, vm.Accounts[0].Metrics[0].Title);
    }

    // #1160 — When no user pin is set, the ViewModel prefers a metric that the provider has
    // marked as the default budget surface (IsSelectedAsShown = true) over the credit-quantity
    // metric that would otherwise win by ordering. This is what makes the toolbar show real
    // dollar spend rather than a credit counter saturated at the included allotment.
    [Fact]
    public void TopRightLabel_PrefersCostMetric_OverQuantityMetric_WhenNoUserPin()
    {
        var metrics = new UsageMetrics();
        var account = new UsageAccount { Product = "github.com", UserName = "alice" };
        account.Metrics.Add(new UsageMetric
        {
            Title = "Copilot AI Credits",
            QuantityUsed = 20000m,
            QuantityTotal = 0m,
            QuantityPresentationFormatString = "{0:N0} {2}",
            Unit = "AICredits",
        });
        account.Metrics.Add(new UsageMetric
        {
            Title = "Copilot AI Credits (Cost)",
            QuantityUsed = 3754.58m,
            QuantityTotal = 5000m,
            QuantityPresentationFormatString = "{0:C2} / {1:C2}",
            Unit = string.Empty,
            IsSelectedAsShown = true,
        });
        metrics.Accounts.Add(account);

        using var vm = new UsageTrackerViewModel(
            metrics, logger: null, initialSelectedUsageMetricKey: null);

        Assert.Equal("$3,754.58 / $5,000.00", vm.TopRightLabel);
    }

    // #1160 — A user pin still overrides the provider-supplied default budget metric, so the
    // per-metric pinning from #1147 composes cleanly with the new default-selection behavior.
    [Fact]
    public void TopRightLabel_RespectsUserPin_OverBudgetDefault()
    {
        var metrics = new UsageMetrics();
        var account = new UsageAccount { Product = "github.com", UserName = "alice" };
        account.Metrics.Add(new UsageMetric
        {
            Title = "Copilot AI Credits",
            QuantityUsed = 20000m,
            QuantityTotal = 0m,
            QuantityPresentationFormatString = "{0:N0} {2}",
            Unit = "AICredits",
        });
        account.Metrics.Add(new UsageMetric
        {
            Title = "Copilot AI Credits (Cost)",
            QuantityUsed = 3754.58m,
            QuantityTotal = 5000m,
            QuantityPresentationFormatString = "{0:C2} / {1:C2}",
            Unit = string.Empty,
            IsSelectedAsShown = true,
        });
        metrics.Accounts.Add(account);

        using var vm = new UsageTrackerViewModel(
            metrics,
            logger: null,
            initialSelectedUsageMetricKey: UsageAccount.ComposeKey("github.com", "Copilot AI Credits"));

        Assert.Equal("20,000 AICredits", vm.TopRightLabel);
    }
}
