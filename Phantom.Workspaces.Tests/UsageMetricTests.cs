using System.Collections.Generic;
using System.ComponentModel;
using Phantom.Workspaces.Models;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class UsageMetricTests
{
    [Fact]
    public void FractionUsed_WhenTotalIsZero_ReturnsNull()
    {
        var metric = new UsageMetric { QuantityUsed = 100m, QuantityTotal = 0m };
        Assert.Null(metric.FractionUsed);
    }

    [Fact]
    public void FractionUsed_WhenTotalIsNonZero_ReturnsFraction()
    {
        var metric = new UsageMetric { QuantityUsed = 50m, QuantityTotal = 200m };
        Assert.Equal(0.25, metric.FractionUsed!.Value, 10);
    }

    [Fact]
    public void QuantityPresentation_FormatsCorrectly()
    {
        var metric = new UsageMetric
        {
            Title = "Included Usage",
            QuantityUsed = 256m,
            QuantityTotal = 755m,
            QuantityPresentationFormatString = "{0:N0} / {1:N0} {2}",
            Unit = "minutes",
        };

        Assert.Contains("256", metric.QuantityPresentation);
        Assert.Contains("755", metric.QuantityPresentation);
        Assert.Contains("minutes", metric.QuantityPresentation);
    }

    [Fact]
    public void PropertyChanged_RaisedForQuantityUsed()
    {
        var metric = new UsageMetric { QuantityTotal = 100m };
        var raised = new List<string?>();
        metric.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        metric.QuantityUsed = 50m;

        Assert.Contains(nameof(UsageMetric.QuantityUsed), raised);
        Assert.Contains(nameof(UsageMetric.FractionUsed), raised);
        Assert.Contains(nameof(UsageMetric.QuantityPresentation), raised);
    }

    [Fact]
    public void PropertyChanged_RaisedForQuantityTotal()
    {
        var metric = new UsageMetric { QuantityUsed = 50m };
        var raised = new List<string?>();
        metric.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        metric.QuantityTotal = 200m;

        Assert.Contains(nameof(UsageMetric.QuantityTotal), raised);
        Assert.Contains(nameof(UsageMetric.FractionUsed), raised);
        Assert.Contains(nameof(UsageMetric.QuantityPresentation), raised);
    }

    [Fact]
    public void PropertyChanged_NotRaisedWhenValueUnchanged()
    {
        var metric = new UsageMetric { QuantityUsed = 50m };
        var raised = new List<string?>();
        metric.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        metric.QuantityUsed = 50m; // same value

        Assert.Empty(raised);
    }
}
