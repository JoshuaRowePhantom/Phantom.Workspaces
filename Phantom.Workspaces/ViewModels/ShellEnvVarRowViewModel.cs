using System;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Row view-model for a single editable environment-variable in the shell settings dialog.
/// </summary>
public sealed class ShellEnvVarRowViewModel : ViewModelBase
{
    private string name = string.Empty;
    private string value = string.Empty;

    public ShellEnvVarRowViewModel(Action<ShellEnvVarRowViewModel>? removeCallback = null)
    {
        this.RemoveCommand = new RelayCommand(_ => removeCallback?.Invoke(this));
    }

    public string Name
    {
        get => this.name;
        set => this.SetProperty(ref this.name, value);
    }

    public string Value
    {
        get => this.value;
        set => this.SetProperty(ref this.value, value);
    }

    public RelayCommand RemoveCommand { get; }
}
