using Phantom.Workspaces.Gui.Shared.Controls;

namespace Phantom.Workspaces.Gui.Shared.Tests;

public sealed class WebView2AcceleratorInteropTests
{
    private const int SystemKeyDown = 2;
    private const int SystemKeyUp   = 3;
    private const int VK_MENU = 0x12; // Alt
    private const int VK_CONTROL = 0x11;
    private const int VK_SHIFT = 0x10;
    private const int VK_W = 0x57;
    private const int VK_1    = 0x31;
    private const int VK_2    = 0x32;
    private const int VK_0    = 0x30;

    [Fact]
    public void Dispatch_SystemKeyDownAlt_CallsOnAltKeyStateWithTrue()
    {
        bool? received = null;
        WebView2AcceleratorInterop.Dispatch(SystemKeyDown, VK_MENU,
            onAltKeyState: v => received = v,
            onGoToTab:     _ => { });

        Assert.True(received);
    }

    [Fact]
    public void Dispatch_SystemKeyDownAlt1_CallsOnGoToTabWithIndex0()
    {
        int? received = null;
        WebView2AcceleratorInterop.Dispatch(SystemKeyDown, VK_1,
            onAltKeyState: _ => { },
            onGoToTab:     v => received = v);

        Assert.Equal(0, received);
    }

    [Fact]
    public void Dispatch_SystemKeyUpAlt_CallsOnAltKeyStateWithFalse()
    {
        bool? received = null;
        WebView2AcceleratorInterop.Dispatch(SystemKeyUp, VK_MENU,
            onAltKeyState: v => received = v,
            onGoToTab:     _ => { });

        Assert.False(received);
    }

    [Fact]
    public void Dispatch_SystemKeyDownAlt2_CallsOnGoToTabWithIndex1()
    {
        int? received = null;
        WebView2AcceleratorInterop.Dispatch(SystemKeyDown, VK_2,
            onAltKeyState: _ => { },
            onGoToTab:     v => received = v);

        Assert.Equal(1, received);
    }

    [Fact]
    public void Dispatch_SystemKeyDownAlt0_CallsOnGoToTabWithIndex9()
    {
        int? received = null;
        WebView2AcceleratorInterop.Dispatch(SystemKeyDown, VK_0,
            onAltKeyState: _ => { },
            onGoToTab:     v => received = v);

        Assert.Equal(9, received);
    }

    [Fact]
    public void Dispatch_KeyDownCtrlW_CallsOnCloseTab()
    {
        var called = false;
        WebView2AcceleratorInterop.Dispatch(0, VK_W,
            onAltKeyState: _ => { },
            onGoToTab: _ => { },
            onCloseTab: () => called = true,
            isKeyDown: key => key == VK_CONTROL);

        Assert.True(called);
    }

    [Fact]
    public void Dispatch_KeyDownCtrlW_WithoutCtrl_DoesNotCallOnCloseTab()
    {
        var called = false;
        WebView2AcceleratorInterop.Dispatch(0, VK_W,
            onAltKeyState: _ => { },
            onGoToTab: _ => { },
            onCloseTab: () => called = true,
            isKeyDown: _ => false);

        Assert.False(called);
    }

    [Fact]
    public void Dispatch_SystemKeyDownShiftAlt2_CallsOnGoToWorkspacePaneWithIndex1()
    {
        int? tab = null;
        int? pane = null;
        WebView2AcceleratorInterop.Dispatch(SystemKeyDown, VK_2,
            onAltKeyState: _ => { },
            onGoToTab: v => tab = v,
            onGoToWorkspacePane: v => pane = v,
            isKeyDown: key => key == VK_SHIFT);

        Assert.Null(tab);
        Assert.Equal(1, pane);
    }

    [Fact]
    public void Dispatch_SystemKeyDownShiftAlt0_CallsOnGoToWorkspacePaneWithIndex9()
    {
        int? pane = null;
        WebView2AcceleratorInterop.Dispatch(SystemKeyDown, VK_0,
            onAltKeyState: _ => { },
            onGoToTab: _ => { },
            onGoToWorkspacePane: v => pane = v,
            isKeyDown: key => key == VK_SHIFT);

        Assert.Equal(9, pane);
    }
}
