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
}
