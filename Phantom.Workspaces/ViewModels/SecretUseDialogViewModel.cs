using System.Collections.ObjectModel;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.ViewModels;

public sealed class SecretUseDialogViewModel : ViewModelBase
{
    public SecretUseDialogViewModel(SecretUseDialogInput input, ICredentialPicker credentialPicker)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(credentialPicker);

        this.Rows = new ObservableCollection<SecretUseDialogRowViewModel>(
            input.Rows.Select(request => new SecretUseDialogRowViewModel(request, credentialPicker)));
        this.YesCommand = new RelayCommand(_ => this.Accept());
        this.NoCommand = new RelayCommand(_ => this.Reject());
    }

    public ObservableCollection<SecretUseDialogRowViewModel> Rows { get; }

    public RelayCommand YesCommand { get; }

    public RelayCommand NoCommand { get; }

    public bool? DialogResult { get; private set; }

    public IReadOnlyList<SecretUseDialogRow> SelectedRows { get; private set; } = [];

    private void Accept()
    {
        this.SelectedRows = this.Rows
            .Select(row => new SecretUseDialogRow(row.Request, row.SelectedMemory, row.SelectedSource))
            .ToArray();
        this.DialogResult = true;
        this.RaisePropertyChanged(nameof(this.SelectedRows));
        this.RaisePropertyChanged(nameof(this.DialogResult));
    }

    private void Reject()
    {
        this.SelectedRows = [];
        this.DialogResult = false;
        this.RaisePropertyChanged(nameof(this.SelectedRows));
        this.RaisePropertyChanged(nameof(this.DialogResult));
    }
}

public sealed class SecretUseDialogRowViewModel : ViewModelBase
{
    private readonly ICredentialPicker credentialPicker;
    private SecretUseMemory selectedMemory;
    private SecretSource selectedSource;

    public SecretUseDialogRowViewModel(SecretRequest request, ICredentialPicker credentialPicker)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credentialPicker);

        this.Request = request;
        this.credentialPicker = credentialPicker;
        this.SecretName = request.SecretName;
        this.UseDisplayString = request.UseDisplayString;
        this.AvailableMemories = request.Memories.ToArray();
        this.AvailableSources = new ObservableCollection<SecretSource>(request.CandidateSecretSources);
        this.selectedMemory = this.AvailableMemories.FirstOrDefault(memory => memory.Scope == SecretUseScope.KeyInManifestContent)
            ?? this.AvailableMemories.FirstOrDefault()
            ?? new SecretUseMemory(SecretUseScope.AlwaysAsk, "Always Ask", string.Empty);
        this.selectedSource = request.DefaultSecretSource
            ?? this.AvailableSources.FirstOrDefault()
            ?? new CredentialStoreSecretSource(string.Empty);
        this.PickCredentialCommand = new AsyncRelayCommand(
            _ => this.PickCredentialAsync(),
            _ => this.SelectedSource is CredentialStoreSecretSource && this.credentialPicker.IsSupported);
    }

    public SecretRequest Request { get; }

    public string SecretName { get; }

    public string UseDisplayString { get; }

    public IReadOnlyList<SecretUseMemory> AvailableMemories { get; }

    /// <summary>
    /// True when the row has at least one scope memory to offer. When false the scope ComboBox should
    /// be hidden rather than rendered blank.
    /// </summary>
    public bool HasMemories => this.AvailableMemories.Count > 0;

    public SecretUseMemory SelectedMemory
    {
        get => this.selectedMemory;
        set => this.SetProperty(ref this.selectedMemory, value);
    }

    public ObservableCollection<SecretSource> AvailableSources { get; }

    /// <summary>
    /// True when the row has at least one source to offer. When false the source ComboBox (and its
    /// credential-picker button) should be hidden rather than rendered blank.
    /// </summary>
    public bool HasSources => this.AvailableSources.Count > 0;

    public SecretSource SelectedSource
    {
        get => this.selectedSource;
        set
        {
            if (this.SetProperty(ref this.selectedSource, value))
            {
                this.PickCredentialCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncRelayCommand PickCredentialCommand { get; }

    private async Task PickCredentialAsync()
    {
        if (this.SelectedSource is not CredentialStoreSecretSource credentialStore)
        {
            return;
        }

        var picked = await this.credentialPicker.PickAsync(credentialStore.CredentialName, CancellationToken.None)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(picked))
        {
            return;
        }

        var source = new CredentialStoreSecretSource(picked);
        if (!this.AvailableSources.Contains(source))
        {
            this.AvailableSources.Add(source);
        }

        this.SelectedSource = source;
    }
}
