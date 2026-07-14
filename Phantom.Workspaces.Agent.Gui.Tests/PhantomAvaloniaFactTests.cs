using Avalonia.Threading;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class PhantomAvaloniaFactTests
{
    [PhantomAvaloniaFact]
    public void PhantomAvaloniaFact_DelegatesToAvaloniaTestCase()
    {
        // PhantomAvaloniaFact delegates to AvaloniaTestCase which schedules the test body on
        // the Avalonia UI thread -- verifying that proves the delegation is working correctly.
        Assert.True(Dispatcher.UIThread.CheckAccess(), "Test body should be running on the Avalonia UI thread.");
    }
}
