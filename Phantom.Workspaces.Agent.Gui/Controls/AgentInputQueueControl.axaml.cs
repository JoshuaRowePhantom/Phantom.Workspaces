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
        this.InputBox.AddHandler(
            InputElement.KeyDownEvent,
            this.InputBox_KeyDown,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (this.DataContext is not InputQueueViewModel vm)
        {
            return;
        }

        if (sender is TextBox textBox)
        {
            vm.InputText = textBox.Text ?? string.Empty;
        }

        e.Handled = HandleInputKey(vm, e.Key, e.KeyModifiers);
    }

    internal static bool HandleInputKey(InputQueueViewModel vm, Key key, KeyModifiers keyModifiers)
    {
        if (key == Key.Enter || key == Key.Return)
        {
            if (vm.IsFormattedMode)
            {
                if (keyModifiers.HasFlag(KeyModifiers.Control))
                {
                    vm.SubmitToDefaultQueueCommand.Execute(null);
                    return true;
                }
                // else: let TextBox handle Enter as a newline (AcceptsReturn=true)
            }
            else
            {
                if (keyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    vm.EnterFormattedMode();
                    return true;
                }
                else
                {
                    vm.SubmitToDefaultQueueCommand.Execute(null);
                    return true;
                }
            }
        }
        else if (key == Key.Q && keyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (keyModifiers.HasFlag(KeyModifiers.Shift))
            {
                vm.SubmitToNewQueue();
            }
            else
            {
                vm.SubmitToMostRecentQueue();
            }

            return true;
        }
        else if (key == Key.H
                 && keyModifiers.HasFlag(KeyModifiers.Control)
                 && keyModifiers.HasFlag(KeyModifiers.Shift))
        {
            vm.HoldAllQueues();
            return true;
        }
        else if (key == Key.U
                 && keyModifiers.HasFlag(KeyModifiers.Control)
                 && keyModifiers.HasFlag(KeyModifiers.Shift))
        {
            vm.UnholdAllQueues();
            return true;
        }
        else if (key == Key.H && keyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.ToggleHoldAllQueues();
            return true;
        }
        else if (key == Key.Escape && vm.IsFormattedMode)
        {
            vm.ExitFormattedMode();
            return true;
        }

        return false;
    }
}
