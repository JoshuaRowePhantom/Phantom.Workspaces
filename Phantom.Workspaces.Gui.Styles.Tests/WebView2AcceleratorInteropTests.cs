using Phantom.Workspaces.Gui.Styles.Controls;

namespace Phantom.Workspaces.Gui.Styles.Tests;

public sealed class WebView2AcceleratorInteropTests
{
    private const int SystemKeyDown = 2;
    private const int SystemKeyUp   = 3;
    private const int VK_MENU = 0x12; // Alt
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
}
