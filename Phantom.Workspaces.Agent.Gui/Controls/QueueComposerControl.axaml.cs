using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Agent.Gui.Controls;

public partial class QueueComposerControl : UserControl
{
    public QueueComposerControl()
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
        if (this.DataContext is not QueueComposerViewModel vm)
        {
            return;
        }

        if (sender is TextBox textBox)
        {
            vm.InputText = textBox.Text ?? string.Empty;
        }

        e.Handled = HandleInputKey(vm, e.Key, e.KeyModifiers);
    }

    internal static bool HandleInputKey(QueueComposerViewModel vm, Key key, KeyModifiers keyModifiers)
    {
        if (key == Key.Enter || key == Key.Return)
        {
            if (vm.IsFormattedMode)
            {
                if (keyModifiers.HasFlag(KeyModifiers.Control))
                {
                    vm.Submit();
                    return true;
                }
            }
            else
            {
                if (keyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    vm.EnterFormattedMode();
                    return true;
                }

                vm.Submit();
                return true;
            }
        }
        else if (vm.IsDefaultComposer && key == Key.Q && keyModifiers.HasFlag(KeyModifiers.Control))
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
        else if (key == Key.Escape && vm.IsFormattedMode)
        {
            vm.ExitFormattedMode();
            return true;
        }

        return false;
    }
}
