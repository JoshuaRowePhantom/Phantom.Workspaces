using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Controls;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class TransientPopupControlFocusTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public void TransientPopupControl_Focusable_IsFalse()
    {
        var control = new TransientPopupControl();
        Assert.False(control.Focusable);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void NotificationsControl_Focusable_IsFalse()
    {
        var control = new NotificationsControl();
        Assert.False(control.Focusable);
    }
}
