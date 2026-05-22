using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Agent.Gui.Controls;

public partial class AgentInputQueueControl : UserControl
{
    public AgentInputQueueControl()
    {
        this.InitializeComponent();
        this.AddHandler(
            InputElement.KeyDownEvent,
            this.OnKeyDown,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    internal static bool HandleInputKey(InputQueueViewModel vm, Key key, KeyModifiers keyModifiers)
    {
        if (key == Key.H
            && keyModifiers.HasFlag(KeyModifiers.Control)
            && keyModifiers.HasFlag(KeyModifiers.Shift))
        {
            vm.HoldAllQueues();
            return true;
        }

        if (key == Key.U
            && keyModifiers.HasFlag(KeyModifiers.Control)
            && keyModifiers.HasFlag(KeyModifiers.Shift))
        {
            vm.UnholdAllQueues();
            return true;
        }

        if (key == Key.H && keyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.ToggleHoldAllQueues();
            return true;
        }

        return false;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (this.DataContext is not InputQueueViewModel vm)
        {
            return;
        }

        e.Handled = HandleInputKey(vm, e.Key, e.KeyModifiers);
    }

}
