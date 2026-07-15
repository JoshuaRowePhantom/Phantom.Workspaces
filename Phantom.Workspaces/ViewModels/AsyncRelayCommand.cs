using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Phantom.Workspaces.ViewModels;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> execute;
    private readonly Func<object?, bool>? canExecute;

    public AsyncRelayCommand(
        Func<object?, Task> execute,
        Func<object?, bool>? canExecute = null)
    {
        this.execute = execute;
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public Task? LastExecutionTask { get; private set; }

    public bool CanExecute(
        object? parameter)
    {
        return this.canExecute?.Invoke(parameter) ?? true;
    }

    public void Execute(
        object? parameter)
    {
        this.LastExecutionTask = this.execute(parameter);
    }

    public void RaiseCanExecuteChanged()
    {
        this.CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
