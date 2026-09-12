using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Phantom.Workspaces.ViewModels;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> execute;
    private readonly Func<object?, bool>? canExecute;
    private readonly bool allowConcurrentExecutions;
    private readonly object executionLock = new();
    private Task? lastExecutionTask;
    private bool isExecuting;
    private long executionGeneration;

    public AsyncRelayCommand(
        Func<object?, Task> execute,
        Func<object?, bool>? canExecute = null,
        bool allowConcurrentExecutions = true)
    {
        this.execute = execute;
        this.canExecute = canExecute;
        this.allowConcurrentExecutions = allowConcurrentExecutions;
    }

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

    public bool CanExecute(
        object? parameter)
    {
        return (this.allowConcurrentExecutions || !this.IsExecuting)
            && (this.canExecute?.Invoke(parameter) ?? true);
    }

    public void Execute(
        object? parameter)
    {
        if (this.allowConcurrentExecutions)
        {
            var concurrentTask = this.execute(parameter);
            lock (this.executionLock)
            {
                this.lastExecutionTask = concurrentTask;
            }
            return;
        }

        long generation;
        lock (this.executionLock)
        {
            if (this.isExecuting || !(this.canExecute?.Invoke(parameter) ?? true))
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

    private async Task ExecuteSingleFlightAsync(
        object? parameter,
        long generation)
    {
        try
        {
            await this.execute(parameter);
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

    public void RaiseCanExecuteChanged()
    {
        this.CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
