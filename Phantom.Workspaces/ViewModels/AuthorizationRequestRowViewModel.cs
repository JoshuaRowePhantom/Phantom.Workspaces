using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ToolkitAsyncRelayCommand = CommunityToolkit.Mvvm.Input.AsyncRelayCommand;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Authorization;

namespace Phantom.Workspaces.ViewModels;

public sealed class AuthorizationRequestRowViewModel : ViewModelBase
{
    private UserAuthorizationSnapshot snapshot = null!;
    private ReleaseMemoryOptionIdentifier selectedMemory = null!;
    private CredentialSourceIdentifier selectedSource = null!;
    private bool isSubmitting;
    private bool isDetailsExpanded;
    private bool isPresent;

    public required IUserAuthorizationRegistry Registry { private get; init; }
    public UserAuthorizationRequestIdentifier Identifier => this.snapshot.Identifier;
    public long Revision => this.snapshot.Revision;
    public string UseDisplayName => this.snapshot.UseDisplayName;
    public string SafeStatus => this.snapshot.SafeStatus;
    public UserAuthorizationState State => this.snapshot.State;
    public string PrimaryActionLabel
    {
        get
        {
            if (this.State is not (UserAuthorizationState.Pending or UserAuthorizationState.Failed or UserAuthorizationState.RenewalNeeded) ||
                this.SelectedSourceOption is not { } source)
            {
                return "Unavailable";
            }

            var action = source.Usability == CredentialSourceUsability.Usable ? "Allow" : "Perform";
            return this.State == UserAuthorizationState.Failed ? $"Retry ({action})" : action;
        }
    }
    public bool IsBootstrapBlocking => this.snapshot.IsBootstrapBlocking;
    public bool CanAct => this.isPresent && !this.isSubmitting && this.snapshot.CanAct &&
        this.State is UserAuthorizationState.Pending or UserAuthorizationState.Failed or UserAuthorizationState.RenewalNeeded;
    public IReadOnlyList<ReleaseMemoryOptionDescriptor> MemoryOptions => this.snapshot.MemoryOptions;
    public IReadOnlyList<CredentialSourceChoiceDescriptor> SourceOptions => this.snapshot.SourceOptions;
    public ReleaseMemoryOptionIdentifier SelectedMemory
    {
        get => this.selectedMemory;
        set => this.SetProperty(ref this.selectedMemory, value);
    }
    public CredentialSourceIdentifier SelectedSource
    {
        get => this.selectedSource;
        set
        {
            if (this.SetProperty(ref this.selectedSource, value))
            {
                this.RaisePropertyChanged(nameof(this.SelectedSourceOption));
                this.RaisePropertyChanged(nameof(this.PrimaryActionLabel));
            }
        }
    }
    public ReleaseMemoryOptionDescriptor? SelectedMemoryOption
    {
        get => this.MemoryOptions.FirstOrDefault(option => option.Identifier == this.SelectedMemory);
        set { if (value is not null) { this.SelectedMemory = value.Identifier; this.RaisePropertyChanged(); } }
    }
    public CredentialSourceChoiceDescriptor? SelectedSourceOption
    {
        get => this.SourceOptions.FirstOrDefault(option => option.Identifier == this.SelectedSource);
        set { if (value is not null) { this.SelectedSource = value.Identifier; } }
    }
    public bool IsDetailsExpanded
    {
        get => this.isDetailsExpanded;
        set => this.SetProperty(ref this.isDetailsExpanded, value);
    }

    public IAsyncRelayCommand ApplyCommand => this.applyCommand ??= new ToolkitAsyncRelayCommand(
        () => this.SubmitAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = this.Identifier,
            ExpectedRevision = this.Revision,
            Memory = this.SelectedMemory,
            Source = this.SelectedSource,
        }), () => this.CanAct);
    public IAsyncRelayCommand DenyCommand => this.denyCommand ??= new ToolkitAsyncRelayCommand(
        () => this.SubmitAsync(new DenyUserAuthorizationAction
        {
            RequestIdentifier = this.Identifier, ExpectedRevision = this.Revision,
        }), () => this.CanAct);
    public IAsyncRelayCommand CancelCommand => this.cancelCommand ??= new ToolkitAsyncRelayCommand(
        () => this.SubmitAsync(new CancelUserAuthorizationAction
        {
            RequestIdentifier = this.Identifier, ExpectedRevision = this.Revision,
        }), () => this.CanAct);
    private ToolkitAsyncRelayCommand? applyCommand;
    private ToolkitAsyncRelayCommand? denyCommand;
    private ToolkitAsyncRelayCommand? cancelCommand;

    internal void Apply(UserAuthorizationSnapshot value)
    {
        if (this.snapshot is not null && value.Revision < this.Revision) { return; }
        this.selectedMemory = value.MemoryOptions.Any(option => option.Identifier == this.selectedMemory)
            ? this.selectedMemory : value.SelectedMemory;
        this.selectedSource = value.SourceOptions.Any(option => option.Identifier == this.selectedSource)
            ? this.selectedSource : value.SelectedSource;
        this.snapshot = value;
        this.isPresent = true;
        this.RaisePropertyChanged(string.Empty);
        this.NotifyCommands();
    }

    internal void Deactivate()
    {
        this.isPresent = false;
        this.RaisePropertyChanged(nameof(this.CanAct));
        this.NotifyCommands();
    }

    private async Task SubmitAsync(UserAuthorizationAction action)
    {
        if (!this.CanAct) { return; }
        this.isSubmitting = true;
        this.RaisePropertyChanged(nameof(this.CanAct));
        this.NotifyCommands();
        try { await this.Registry.ActAsync(action, CancellationToken.None); }
        catch (UserAuthorizationActionRejectedException) { }
        catch (ObjectDisposedException) { this.isPresent = false; }
        finally
        {
            this.isSubmitting = false;
            this.RaisePropertyChanged(nameof(this.CanAct));
            this.NotifyCommands();
        }
    }

    private void NotifyCommands()
    {
        this.applyCommand?.NotifyCanExecuteChanged();
        this.denyCommand?.NotifyCanExecuteChanged();
        this.cancelCommand?.NotifyCanExecuteChanged();
    }
}
