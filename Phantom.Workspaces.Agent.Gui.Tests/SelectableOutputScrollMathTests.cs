using Avalonia.Input;
using Phantom.Workspaces.Agent.Gui.Controls;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class SelectableOutputScrollMathTests
{
    [Fact]
    public void Home_ScrollsToTop()
    {
        Assert.Equal(0, SelectableOutputScrollMath.ComputeVerticalOffset(Key.Home, 100, 1000));
    }

    [Fact]
    public void End_ScrollsToBottom()
    {
        // viewport 100, extent 1000 => max offset 900.
        Assert.Equal(900, SelectableOutputScrollMath.ComputeVerticalOffset(Key.End, 100, 1000));
    }

    [Fact]
    public void End_ClampsToZero_WhenContentFits()
    {
        Assert.Equal(0, SelectableOutputScrollMath.ComputeVerticalOffset(Key.End, 1000, 500));
    }

    [Fact]
    public void PageKeys_AreNotHandled_LeftToTheScrollViewer()
    {
        // ScrollViewer already handles Page Up/Down natively, so the helper ignores them.
        Assert.Null(SelectableOutputScrollMath.ComputeVerticalOffset(Key.PageUp, 100, 1000));
        Assert.Null(SelectableOutputScrollMath.ComputeVerticalOffset(Key.PageDown, 100, 1000));
    }

    [Fact]
    public void NonScrollKey_ReturnsNull()
    {
        Assert.Null(SelectableOutputScrollMath.ComputeVerticalOffset(Key.A, 100, 1000));
        Assert.Null(SelectableOutputScrollMath.ComputeVerticalOffset(Key.Left, 100, 1000));
    }
}
