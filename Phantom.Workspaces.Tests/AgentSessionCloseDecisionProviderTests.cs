using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class AgentSessionCloseDecisionProviderTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dialog_EscapeOrDismissal_DefaultsToCancel(bool escape)
    {
        var (window, decision) = AgentSessionCloseDecisionProvider.CreateDialog("test");
        window.Show();
        if (escape)
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Key.Escape });
        else
            window.Close();
        Assert.Equal(AgentSessionCloseDecision.Cancel, await decision);
    }
}
