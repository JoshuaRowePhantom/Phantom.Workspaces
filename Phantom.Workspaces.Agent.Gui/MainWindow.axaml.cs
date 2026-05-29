using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Agent.Gui;

public partial class MainWindow : Window
{
    private LogWindow? logWindow;

    public MainWindow(MainWindowViewModel viewModel)
    {
        this.DataContext = viewModel;
        this.InitializeComponent();
        this.AddHandler(
            InputElement.KeyDownEvent,
            this.MainWindow_KeyDown,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (this.DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (HandleKey(vm, e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }

        // Ctrl+L opens the log window (instance-only because it opens a child window).
        if (e.Key == Key.L && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            this.OpenLogWindow(vm);
            e.Handled = true;
        }
    }

    internal static bool HandleKey(MainWindowViewModel vm, Key key, KeyModifiers keyModifiers)
    {
        if (key == Key.T && keyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.Agent.ToggleReasoningVisibility();
            return true;
        }

        return false;
    }

    private void OpenLogWindow(MainWindowViewModel vm)
    {
        if (this.logWindow != null)
        {
            this.logWindow.Activate();
            return;
        }

        this.logWindow = new LogWindow(vm.LoggerFactory);
        this.logWindow.Closed += (_, _) => this.logWindow = null;
        this.logWindow.Show(this);
    }

}
