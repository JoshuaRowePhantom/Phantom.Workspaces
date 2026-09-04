using System.Collections.ObjectModel;
using System.ComponentModel;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Secrets;

namespace Phantom.Workspaces.ViewModels;

public sealed class CredentialManagerDialogViewModel : ViewModelBase
{
    private readonly IAllowedSecretsStore allowedSecretsStore;
    private readonly IPlatformSecretStore platformSecretStore;
    private readonly ICredentialPicker credentialPicker;
    private readonly SecretMemoryEnumerator enumerator;

    public CredentialManagerDialogViewModel(
        IAllowedSecretsStore allowedSecretsStore,
        IPlatformSecretStore platformSecretStore,
        ICredentialPicker credentialPicker,
        SecretMemoryEnumerator? enumerator = null)
    {
        ArgumentNullException.ThrowIfNull(allowedSecretsStore);
        ArgumentNullException.ThrowIfNull(platformSecretStore);
        ArgumentNullException.ThrowIfNull(credentialPicker);

        this.allowedSecretsStore = allowedSecretsStore;
        this.platformSecretStore = platformSecretStore;
        this.credentialPicker = credentialPicker;
        this.enumerator = enumerator ?? new SecretMemoryEnumerator(allowedSecretsStore, platformSecretStore);
        this.DeleteSelectedCommand = new AsyncRelayCommand(
            _ => this.DeleteSelectedAsync(CancellationToken.None),
            _ => this.HasSelection);
    }

    public ObservableCollection<CredentialGroupViewModel> CredentialGroups { get; } = [];

    public ObservableCollection<UnusedSavedCredentialViewModel> UnusedSavedCredentials { get; } = [];

    public AsyncRelayCommand DeleteSelectedCommand { get; }

    public bool HasSelection
        => this.CredentialGroups.Any(static group => group.UsePlaces.Any(static use => use.IsMarkedForDelete))
           || this.UnusedSavedCredentials.Any(static credential => credential.IsMarkedForDelete);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var snapshot = await this.enumerator.EnumerateAsync(ct).ConfigureAwait(false);

        this.CredentialGroups.Clear();
        foreach (var group in snapshot.Groups)
        {
            var vm = new CredentialGroupViewModel(group, this.credentialPicker, this.OnSelectionChanged);
            this.CredentialGroups.Add(vm);
        }

        this.UnusedSavedCredentials.Clear();
        foreach (var credentialName in snapshot.UnusedSavedCredentialNames)
        {
            var vm = new UnusedSavedCredentialViewModel(credentialName, this.credentialPicker, this.OnSelectionChanged);
            this.UnusedSavedCredentials.Add(vm);
        }

        this.OnSelectionChanged();
    }

    public async Task DeleteSelectedAsync(CancellationToken ct = default)
    {
        var hashes = this.CredentialGroups
            .SelectMany(static group => group.UsePlaces)
            .Where(static use => use.IsMarkedForDelete)
            .Select(static use => use.Hash)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var credentialNames = this.UnusedSavedCredentials
            .Where(static credential => credential.IsMarkedForDelete)
            .Select(static credential => credential.CredentialName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var hash in hashes)
        {
            await this.allowedSecretsStore.DeleteAsync(hash, ct).ConfigureAwait(false);
        }

        foreach (var credentialName in credentialNames)
        {
            await this.platformSecretStore.DeleteAsync(credentialName, ct).ConfigureAwait(false);
        }

        await this.LoadAsync(ct).ConfigureAwait(false);
    }

    private void OnSelectionChanged()
    {
        this.RaisePropertyChanged(nameof(this.HasSelection));
        this.DeleteSelectedCommand.RaiseCanExecuteChanged();
    }
}

public sealed class CredentialGroupViewModel : ViewModelBase
{
    public CredentialGroupViewModel(SecretMemoryGroup group, ICredentialPicker credentialPicker, Action selectionChanged)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(credentialPicker);
        ArgumentNullException.ThrowIfNull(selectionChanged);

        this.Source = group.Source;
        this.DisplayLabel = SecretSourceDisplay.GetLabel(group.Source);
        if (group.Source is CredentialStoreSecretSource credential)
        {
            this.CredentialName = credential.CredentialName;
            this.EditCommand = new AsyncRelayCommand(_ => credentialPicker.PickAsync(credential.CredentialName, CancellationToken.None));
        }

        foreach (var use in group.UsePlaces)
        {
            var vm = new MemorizedUsePlaceViewModel(use.Hash, use.Memory);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MemorizedUsePlaceViewModel.IsMarkedForDelete))
                {
                    selectionChanged();
                }
            };
            this.UsePlaces.Add(vm);
        }
    }

    public SecretSource Source { get; }

    public string DisplayLabel { get; }

    public string? CredentialName { get; }

    public bool IsSavedCredential => this.CredentialName is not null;

    public ObservableCollection<MemorizedUsePlaceViewModel> UsePlaces { get; } = [];

    public AsyncRelayCommand? EditCommand { get; }
}

public sealed class MemorizedUsePlaceViewModel : ViewModelBase
{
    private bool isMarkedForDelete;

    public MemorizedUsePlaceViewModel(string hash, SecretUseMemory memory)
    {
        this.Hash = hash;
        this.DisplayString = memory.DisplayString;
        this.Scope = memory.Scope;
    }

    public string Hash { get; }

    public string DisplayString { get; }

    public SecretUseScope Scope { get; }

    public bool IsMarkedForDelete
    {
        get => this.isMarkedForDelete;
        set => this.SetProperty(ref this.isMarkedForDelete, value);
    }
}

public sealed class UnusedSavedCredentialViewModel : ViewModelBase
{
    private bool isMarkedForDelete;

    public UnusedSavedCredentialViewModel(string credentialName, ICredentialPicker credentialPicker, Action selectionChanged)
    {
        ArgumentException.ThrowIfNullOrEmpty(credentialName);
        ArgumentNullException.ThrowIfNull(credentialPicker);
        ArgumentNullException.ThrowIfNull(selectionChanged);

        this.CredentialName = credentialName;
        this.EditCommand = new AsyncRelayCommand(_ => credentialPicker.PickAsync(credentialName, CancellationToken.None));
        this.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(this.IsMarkedForDelete))
            {
                selectionChanged();
            }
        };
    }

    public string CredentialName { get; }

    public AsyncRelayCommand EditCommand { get; }

    public bool IsMarkedForDelete
    {
        get => this.isMarkedForDelete;
        set => this.SetProperty(ref this.isMarkedForDelete, value);
    }
}
