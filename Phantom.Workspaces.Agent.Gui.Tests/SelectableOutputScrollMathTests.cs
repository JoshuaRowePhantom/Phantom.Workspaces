using Avalonia.Input;
using Phantom.Workspaces.Agent.Gui.Controls;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class SelectableOutputScrollMathTests
{
    [Fact]
    public void PageDown_AdvancesByViewportLessOverlap_AndClamps()
    {
        // viewport 100, extent 1000 => max offset 900. Page step = 100 - 32 = 68.
        var offset = SelectableOutputScrollMath.ComputeVerticalOffset(Key.PageDown, 0, 100, 1000);
        Assert.Equal(68, offset);
    }

    [Fact]
    public void PageUp_MovesBack_AndClampsAtZero()
    {
        Assert.Equal(0, SelectableOutputScrollMath.ComputeVerticalOffset(Key.PageUp, 40, 100, 1000));
        Assert.Equal(32, SelectableOutputScrollMath.ComputeVerticalOffset(Key.PageUp, 100, 100, 1000));
    }

    [Fact]
    public void Home_ScrollsToTop_End_ScrollsToBottom()
    {
        Assert.Equal(0, SelectableOutputScrollMath.ComputeVerticalOffset(Key.Home, 500, 100, 1000));
        Assert.Equal(900, SelectableOutputScrollMath.ComputeVerticalOffset(Key.End, 0, 100, 1000));
    }

    [Fact]
    public void PageDown_ClampsToMaxOffset()
    {
        Assert.Equal(900, SelectableOutputScrollMath.ComputeVerticalOffset(Key.PageDown, 880, 100, 1000));
    }

    [Fact]
    public void NonScrollKey_ReturnsNull()
    {
        Assert.Null(SelectableOutputScrollMath.ComputeVerticalOffset(Key.A, 0, 100, 1000));
        Assert.Null(SelectableOutputScrollMath.ComputeVerticalOffset(Key.Left, 0, 100, 1000));
    }

    [Fact]
    public void NoScrollableContent_AllKeysClampToZero()
    {
        Assert.Equal(0, SelectableOutputScrollMath.ComputeVerticalOffset(Key.End, 0, 1000, 500));
        Assert.Equal(0, SelectableOutputScrollMath.ComputeVerticalOffset(Key.PageDown, 0, 1000, 500));
    }
}
