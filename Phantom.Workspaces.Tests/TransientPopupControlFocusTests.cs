using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Controls;

namespace Phantom.Workspaces.Tests;

public sealed class TransientPopupControlFocusTests
{
    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void TransientPopupControl_Focusable_IsFalse()
    {
        var control = new TransientPopupControl();
        Assert.False(control.Focusable);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void NotificationsControl_Focusable_IsFalse()
    {
        var control = new NotificationsControl();
        Assert.False(control.Focusable);
    }
}
