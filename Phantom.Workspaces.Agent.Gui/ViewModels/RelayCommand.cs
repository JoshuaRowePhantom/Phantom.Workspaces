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

public sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => parameter is T value && (canExecute?.Invoke(value) ?? true);

    public void Execute(object? parameter)
    {
        if (parameter is T value)
        {
            execute(value);
        }
    }

    public void RaiseCanExecuteChanged() =>
        this.CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Command adapter for owner-acknowledged UI operations.</summary>
public sealed class AsyncRelayCommand(
    Func<object?, Task> execute,
    Func<object?, bool>? canExecute = null,
    bool allowConcurrentExecutions = true) : ICommand
{
    private readonly object executionLock = new();
    private Task? lastExecutionTask;
    private bool isExecuting;
    private long executionGeneration;

    public event EventHandler? CanExecuteChanged;

    public Task? LastExecutionTask
    {
        get
        {
            lock (this.executionLock)
            {
                return this.lastExecutionTask;
            }
        }
    }

    public bool IsExecuting
    {
        get
        {
            lock (this.executionLock)
            {
                return this.isExecuting;
            }
        }
    }

    public bool CanExecute(object? parameter) =>
        (allowConcurrentExecutions || !this.IsExecuting)
        && (canExecute?.Invoke(parameter) ?? true);

    public void Execute(object? parameter)
    {
        if (allowConcurrentExecutions)
        {
            var concurrentTask = execute(parameter);
            lock (this.executionLock)
            {
                this.lastExecutionTask = concurrentTask;
            }
            return;
        }

        long generation;
        lock (this.executionLock)
        {
            if (this.isExecuting || !(canExecute?.Invoke(parameter) ?? true))
            {
                return;
            }

            this.isExecuting = true;
            generation = ++this.executionGeneration;
        }

        this.RaiseCanExecuteChanged();
        var executionTask = this.ExecuteSingleFlightAsync(parameter, generation);
        lock (this.executionLock)
        {
            if (this.executionGeneration == generation)
            {
                this.lastExecutionTask = executionTask;
            }
        }
    }

    private async Task ExecuteSingleFlightAsync(object? parameter, long generation)
    {
        try
        {
            await execute(parameter);
        }
        finally
        {
            lock (this.executionLock)
            {
                if (this.executionGeneration == generation)
                {
                    this.isExecuting = false;
                }
            }
            this.RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => this.CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
