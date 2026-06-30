namespace Phantom.Workspaces.ViewModels;

public sealed class AgentManifestParameterRowViewModel : ViewModelBase
{
    private string value = string.Empty;

    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsRequired { get; init; }
    public AgentManifestParameterKind ParameterKind { get; init; } = AgentManifestParameterKind.Text;
    public bool IsDirectoryPicker => this.ParameterKind == AgentManifestParameterKind.Directory;

    public string Value
    {
        get => this.value;
        set
        {
            if (this.SetProperty(ref this.value, value))
            {
                this.RaisePropertyChanged(nameof(this.IsValid));
            }
        }
    }

    public bool IsValid => !this.IsRequired || !string.IsNullOrWhiteSpace(this.Value);
}
