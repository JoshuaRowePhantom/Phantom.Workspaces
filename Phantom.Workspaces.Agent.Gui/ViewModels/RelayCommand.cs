using System.Windows.Input;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() =>
        this.CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
