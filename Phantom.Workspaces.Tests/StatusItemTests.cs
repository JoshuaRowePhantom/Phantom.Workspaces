using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class StatusItemTests
{
    [Fact]
    public void StatusItem_RunningStatus_DefaultIsIdle()
    {
        var item = new StatusItem();
        Assert.Equal(RunningStatus.Idle, item.RunningStatus);
    }

    [Fact]
    public void StatusItem_ErrorStatus_DefaultIsNone()
    {
        var item = new StatusItem();
        Assert.Equal(ErrorStatus.None, item.ErrorStatus);
    }

    [Fact]
    public void StatusItem_SetRunningStatus_RaisesPropertyChanged()
    {
        var item = new StatusItem();
        var raised = false;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(item.RunningStatus))
                raised = true;
        };

        item.RunningStatus = RunningStatus.Running;

        Assert.True(raised);
    }

    [Fact]
    public void StatusItem_SetErrorStatus_RaisesPropertyChanged()
    {
        var item = new StatusItem();
        var raised = false;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(item.ErrorStatus))
                raised = true;
        };

        item.ErrorStatus = ErrorStatus.Error;

        Assert.True(raised);
    }

    [Fact]
    public void StatusItem_SetRunningStatus_ToSameValue_DoesNotRaisePropertyChanged()
    {
        var item = new StatusItem();
        var count = 0;
        item.PropertyChanged += (_, _) => count++;

        item.RunningStatus = RunningStatus.Idle; // same as default

        Assert.Equal(0, count);
    }

    [Fact]
    public void StatusItem_SetErrorStatus_ToSameValue_DoesNotRaisePropertyChanged()
    {
        var item = new StatusItem();
        var count = 0;
        item.PropertyChanged += (_, _) => count++;

        item.ErrorStatus = ErrorStatus.None; // same as default

        Assert.Equal(0, count);
    }

    [Fact]
    public void StatusItem_ImplementsIStatusItem()
    {
        var item = new StatusItem();
        Assert.IsAssignableFrom<IStatusItem>(item);
    }

    [Fact]
    public void ErrorStatus_EnumOrder_NoneIsLessThanSuccessful()
    {
        Assert.True(ErrorStatus.None < ErrorStatus.Successful);
    }

    [Fact]
    public void ErrorStatus_EnumOrder_SuccessfulIsLessThanError()
    {
        Assert.True(ErrorStatus.Successful < ErrorStatus.Error);
    }
}
