using System.Collections.Generic;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class StatusItemAggregatorTests
{
    [Fact]
    public void UpdateFrom_NoSources_SetsIdleAndNone()
    {
        var target = new StatusItem { RunningStatus = RunningStatus.Running, ErrorStatus = ErrorStatus.Error };

        StatusItemAggregator.UpdateFrom(target, []);

        Assert.Equal(RunningStatus.Idle, target.RunningStatus);
        Assert.Equal(ErrorStatus.None, target.ErrorStatus);
    }

    [Fact]
    public void UpdateFrom_AllIdle_TargetIsIdle()
    {
        var sources = new List<IStatusItem>
        {
            new StatusItem { RunningStatus = RunningStatus.Idle },
            new StatusItem { RunningStatus = RunningStatus.Idle },
        };
        var target = new StatusItem();

        StatusItemAggregator.UpdateFrom(target, sources);

        Assert.Equal(RunningStatus.Idle, target.RunningStatus);
    }

    [Fact]
    public void UpdateFrom_AnyRunning_TargetIsRunning()
    {
        var sources = new List<IStatusItem>
        {
            new StatusItem { RunningStatus = RunningStatus.Idle },
            new StatusItem { RunningStatus = RunningStatus.Running },
        };
        var target = new StatusItem();

        StatusItemAggregator.UpdateFrom(target, sources);

        Assert.Equal(RunningStatus.Running, target.RunningStatus);
    }

    [Fact]
    public void UpdateFrom_AllNoneError_TargetIsNone()
    {
        var sources = new List<IStatusItem>
        {
            new StatusItem { ErrorStatus = ErrorStatus.None },
            new StatusItem { ErrorStatus = ErrorStatus.None },
        };
        var target = new StatusItem();

        StatusItemAggregator.UpdateFrom(target, sources);

        Assert.Equal(ErrorStatus.None, target.ErrorStatus);
    }

    [Fact]
    public void UpdateFrom_WorstIsSuccessful_TargetIsSuccessful()
    {
        var sources = new List<IStatusItem>
        {
            new StatusItem { ErrorStatus = ErrorStatus.None },
            new StatusItem { ErrorStatus = ErrorStatus.Successful },
        };
        var target = new StatusItem();

        StatusItemAggregator.UpdateFrom(target, sources);

        Assert.Equal(ErrorStatus.Successful, target.ErrorStatus);
    }

    [Fact]
    public void UpdateFrom_WorstIsError_TargetIsError()
    {
        var sources = new List<IStatusItem>
        {
            new StatusItem { ErrorStatus = ErrorStatus.Successful },
            new StatusItem { ErrorStatus = ErrorStatus.Error },
        };
        var target = new StatusItem();

        StatusItemAggregator.UpdateFrom(target, sources);

        Assert.Equal(ErrorStatus.Error, target.ErrorStatus);
    }

    [Fact]
    public void UpdateFrom_MixedRunningAndError_AggregatesBoth()
    {
        var sources = new List<IStatusItem>
        {
            new StatusItem { RunningStatus = RunningStatus.Running, ErrorStatus = ErrorStatus.None },
            new StatusItem { RunningStatus = RunningStatus.Idle, ErrorStatus = ErrorStatus.Error },
        };
        var target = new StatusItem();

        StatusItemAggregator.UpdateFrom(target, sources);

        Assert.Equal(RunningStatus.Running, target.RunningStatus);
        Assert.Equal(ErrorStatus.Error, target.ErrorStatus);
    }
}
