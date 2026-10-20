using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ToolkitAsyncRelayCommand = CommunityToolkit.Mvvm.Input.AsyncRelayCommand;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.ViewModels;

public abstract class AdministrationViewModel<TDescriptor> : ViewModelBase where TDescriptor : class
{
    private readonly ObservableCollection<TDescriptor> items = new();
    private readonly CancellationTokenSource shutdown = new();
    private bool isBusy;
    private string safeStatus = string.Empty;
    private bool disposed;

    protected AdministrationViewModel()
    {
        this.Items = new(this.items);
        this.RefreshCommand = new ToolkitAsyncRelayCommand(() => this.RefreshAsync(), () => !this.IsBusy && !this.disposed);
        this.RemoveCommand = new AsyncRelayCommand<TDescriptor>(
            item => item is null ? Task.CompletedTask : this.RemoveAsync(item),
            item => !this.IsBusy && !this.disposed && item is not null && this.items.Contains(item));
    }

    public ReadOnlyObservableCollection<TDescriptor> Items { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand<TDescriptor> RemoveCommand { get; }
    public bool IsBusy => this.isBusy;
    public string SafeStatus => this.safeStatus;
    public abstract string ActionLabel { get; }

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        this.RunAsync(null, cancellationToken);
    public Task RemoveAsync(TDescriptor descriptor, CancellationToken cancellationToken = default) =>
        this.RunAsync(descriptor, cancellationToken);
    protected abstract ValueTask<IReadOnlyList<TDescriptor>> ListAsync(CancellationToken cancellationToken);
    protected abstract ValueTask RemoveCoreAsync(TDescriptor descriptor, CancellationToken cancellationToken);
    protected virtual void OnOperationStarting() { }
    protected virtual bool IsExpectedFailure(Exception exception) => false;
    protected virtual string GetFailureStatus(Exception exception) => "Operation unavailable. Refresh and try again.";

    private async Task RunAsync(TDescriptor? descriptor, CancellationToken cancellationToken)
    {
        if (this.disposed || this.isBusy || (descriptor is not null && !this.items.Contains(descriptor))) { return; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(this.shutdown.Token, cancellationToken);
        this.OnOperationStarting();
        this.isBusy = true;
        this.safeStatus = "Operation in progress.";
        this.NotifyState();
        try
        {
            if (descriptor is not null) { await this.RemoveCoreAsync(descriptor, linked.Token); }
            var snapshot = await this.ListAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (this.disposed) { return; }
            this.items.Clear();
            foreach (var item in snapshot) { this.items.Add(item); }
            this.safeStatus = descriptor is null ? "List updated." : "Operation completed.";
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (!this.disposed) { this.safeStatus = "Operation cancelled."; }
        }
        catch (Exception exception) when (this.IsExpectedFailure(exception))
        {
            // Never render backend exception text, identifiers, or provider responses.
            if (!this.disposed) { this.safeStatus = this.GetFailureStatus(exception); }
        }
        finally
        {
            this.isBusy = false;
            if (!this.disposed) { this.NotifyState(); }
        }
    }

    private void NotifyState()
    {
        this.RaisePropertyChanged(nameof(this.IsBusy));
        this.RaisePropertyChanged(nameof(this.SafeStatus));
        this.RefreshCommand.NotifyCanExecuteChanged();
        this.RemoveCommand.NotifyCanExecuteChanged();
    }

    public override ValueTask DisposeAsync()
    {
        if (this.disposed) { return ValueTask.CompletedTask; }
        this.disposed = true;
        this.shutdown.Cancel();
        this.shutdown.Dispose();
        this.items.Clear();
        this.NotifyState();
        return base.DisposeAsync();
    }
}

public sealed class ReleaseMemoryAdministrationViewModel : AdministrationViewModel<ReleaseMemoryDescriptor>
{
    public required IReleaseMemoryAdministration Administration { private get; init; }
    public override string ActionLabel => "Forget";
    protected override ValueTask<IReadOnlyList<ReleaseMemoryDescriptor>> ListAsync(CancellationToken cancellationToken) =>
        this.Administration.ListAsync(cancellationToken);
    protected override ValueTask RemoveCoreAsync(ReleaseMemoryDescriptor descriptor, CancellationToken cancellationToken) =>
        this.Administration.ForgetAsync(descriptor.Identifier, cancellationToken);
    protected override bool IsExpectedFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;
}
