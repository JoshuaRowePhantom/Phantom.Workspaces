using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Agent.Gui;

public partial class MainWindow : Window
{
    public MainWindow() : this(new MainWindowViewModel(Program.ParseResult!)) { }

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

        e.Handled = HandleKey(vm, e.Key, e.KeyModifiers);
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
}
