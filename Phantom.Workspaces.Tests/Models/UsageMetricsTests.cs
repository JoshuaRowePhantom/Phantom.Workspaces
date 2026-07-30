using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using Phantom.Workspaces.Models;
using Xunit;

namespace Phantom.Workspaces.Tests.Models;

public sealed class UsageMetricsTests
{
    public UsageMetricsTests()
    {
        // Ensure consistent culture for currency/number formatting in tests
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
    }

    [Fact]
    public void UsageMetric_FractionUsed_ReturnsCorrectRatio()
    {
        var metric = new UsageMetric
        {
            Title = "Test",
            QuantityUsed = 1000,
            QuantityTotal = 20000,
            QuantityPresentationFormatString = "{0} / {1}",
            Unit = ""
        };

        Assert.Equal(0.05, metric.FractionUsed);
    }

    [Fact]
    public void UsageMetric_FractionUsed_Null_WhenTotalIsZero()
    {
        var metric = new UsageMetric
        {
            Title = "Test",
            QuantityUsed = 100,
            QuantityTotal = 0,
            QuantityPresentationFormatString = "{0} / {1}",
            Unit = ""
        };

        Assert.Null(metric.FractionUsed);
    }

    [Fact]
    public void UsageMetric_FractionUsed_One_WhenFullyConsumed()
    {
        var metric = new UsageMetric
        {
            Title = "Test",
            QuantityUsed = 500,
            QuantityTotal = 500,
            QuantityPresentationFormatString = "{0} / {1}",
            Unit = ""
        };

        Assert.Equal(1.0, metric.FractionUsed);
    }

    [Fact]
    public void UsageMetric_FractionUsed_UsedLessThanTotal_ReturnsPartialFraction()
    {
        var metric = new UsageMetric
        {
            Title = "Test",
            QuantityUsed = 25,
            QuantityTotal = 100,
            QuantityPresentationFormatString = "{0} / {1}",
            Unit = ""
        };

        Assert.Equal(0.25, metric.FractionUsed);
    }

    [Fact]
    public void UsageMetric_FractionUsed_UsedEqualsTotal_ReturnsOne()
    {
        var metric = new UsageMetric
        {
            Title = "Test",
            QuantityUsed = 500,
            QuantityTotal = 500,
            QuantityPresentationFormatString = "{0} / {1}",
            Unit = ""
        };

        Assert.Equal(1.0, metric.FractionUsed);
    }

    [Fact]
    public void UsageMetric_FractionUsed_TotalIsZero_ReturnsNull()
    {
        var metric = new UsageMetric
        {
            Title = "Test",
            QuantityUsed = 100,
            QuantityTotal = 0,
            QuantityPresentationFormatString = "{0} / {1}",
            Unit = ""
        };

        Assert.Null(metric.FractionUsed);
    }

    [Fact]
    public void UsageMetric_QuantityPresentation_Minutes()
    {
        var metric = new UsageMetric
        {
            Title = "Included Usage",
            QuantityPresentationFormatString = "{0:N0} / {1:N0} {2}",
            Unit = "minutes",
            QuantityUsed = 256,
            QuantityTotal = 755
        };

        Assert.Equal("256 / 755 minutes", metric.QuantityPresentation);
    }

    [Fact]
    public void UsageMetric_QuantityPresentation_AIC()
    {
        var metric = new UsageMetric
        {
            Title = "Additional Usage",
            QuantityPresentationFormatString = "{0:N0} / {1:N0} {2}",
            Unit = "AIC",
            QuantityUsed = 10000,
            QuantityTotal = 20000
        };

        Assert.Equal("10,000 / 20,000 AIC", metric.QuantityPresentation);
    }

    [Fact]
    public void UsageMetric_QuantityPresentation_Dollars()
    {
        var metric = new UsageMetric
        {
            Title = "Additional Usage",
            QuantityPresentationFormatString = "{0:C2} / {1:C2}",
            Unit = "",
            QuantityUsed = 356.00m,
            QuantityTotal = 3000.00m
        };

        Assert.Equal("$356.00 / $3,000.00", metric.QuantityPresentation);
    }

    [Fact]
    public void UsageMetric_RaisesPropertyChanged_OnQuantityUpdate()
    {
        var metric = new UsageMetric
        {
            Title = "Test",
            QuantityPresentationFormatString = "{0:N0} / {1:N0} {2}",
            Unit = "minutes",
            QuantityUsed = 100,
            QuantityTotal = 500
        };

        var changedProperties = new System.Collections.Generic.List<string?>();
        metric.PropertyChanged += (sender, e) => changedProperties.Add(e.PropertyName);

        metric.QuantityUsed = 200;

        Assert.Contains("QuantityUsed", changedProperties);
        Assert.Contains("FractionUsed", changedProperties);
        Assert.Contains("QuantityPresentation", changedProperties);
    }

    [Fact]
    public void UsageMetric_RaisesPropertyChanged_OnQuantityTotalUpdate()
    {
        var metric = new UsageMetric
        {
            Title = "Test",
            QuantityPresentationFormatString = "{0:N0} / {1:N0} {2}",
            Unit = "minutes",
            QuantityUsed = 100,
            QuantityTotal = 500
        };

        var changedProperties = new System.Collections.Generic.List<string?>();
        metric.PropertyChanged += (sender, e) => changedProperties.Add(e.PropertyName);

        metric.QuantityTotal = 600;

        Assert.Contains("QuantityTotal", changedProperties);
        Assert.Contains("FractionUsed", changedProperties);
        Assert.Contains("QuantityPresentation", changedProperties);
    }

    [Fact]
    public void UsageAccount_HasCorrectProperties()
    {
        var account = new UsageAccount
        {
            Product = "GitHub Copilot",
            UserName = "jrowe",
            SettingsUrl = new Uri("https://example.com/settings")
        };

        Assert.Equal("GitHub Copilot", account.Product);
        Assert.Equal("jrowe", account.UserName);
        Assert.Equal("https://example.com/settings", account.SettingsUrl.ToString());
        Assert.NotNull(account.Metrics);
        Assert.Empty(account.Metrics);
    }

    [Fact]
    public void UsageMetrics_HasAccountsCollection()
    {
        var metrics = new UsageMetrics();

        Assert.NotNull(metrics.Accounts);
        Assert.Empty(metrics.Accounts);
    }

    [Fact]
    public void UsageAccount_CanAddMetrics()
    {
        var account = new UsageAccount
        {
            Product = "Test",
            UserName = "user",
            SettingsUrl = new Uri("https://example.com")
        };

        var metric = new UsageMetric
        {
            Title = "Test Metric",
            QuantityPresentationFormatString = "{0} / {1}",
            Unit = "",
            QuantityUsed = 10,
            QuantityTotal = 100
        };

        account.Metrics.Add(metric);

        Assert.Single(account.Metrics);
        Assert.Same(metric, account.Metrics[0]);
    }

    [Fact]
    public void UsageMetrics_CanAddAccounts()
    {
        var metrics = new UsageMetrics();
        var account = new UsageAccount
        {
            Product = "Test",
            UserName = "user",
            SettingsUrl = new Uri("https://example.com")
        };

        metrics.Accounts.Add(account);

        Assert.Single(metrics.Accounts);
        Assert.Same(account, metrics.Accounts[0]);
    }
}
