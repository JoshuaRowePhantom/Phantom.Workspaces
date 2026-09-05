using System.Collections.ObjectModel;
using System.Text.Json;

namespace Phantom.Workspaces.ViewModels;

public sealed class AgentManifestParameterRowViewModel : ViewModelBase
{
    private string value = string.Empty;
    private ExecutorOptionViewModel? selectedExecutorOption;

    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsRequired { get; init; }
    public AgentManifestParameterKind ParameterKind { get; init; } = AgentManifestParameterKind.Text;
    public bool IsDirectoryPicker => this.ParameterKind == AgentManifestParameterKind.Directory;

    /// <summary>Whether this row is the combined <c>executor</c> picker (issue #1440).</summary>
    public bool IsExecutorPicker => this.ParameterKind == AgentManifestParameterKind.Executor;

    /// <summary>
    /// The combined set of selectable executor options — both <c>trust-profile</c> and
    /// <c>user-computer-profile</c> entities — populated for an
    /// <see cref="AgentManifestParameterKind.Executor"/> row (issue #1440).
    /// </summary>
    public ObservableCollection<ExecutorOptionViewModel> ExecutorOptions { get; } = [];

    /// <summary>The option chosen in the <c>executor</c> picker, if any.</summary>
    public ExecutorOptionViewModel? SelectedExecutorOption
    {
        get => this.selectedExecutorOption;
        set
        {
            if (this.SetProperty(ref this.selectedExecutorOption, value))
            {
                this.RaisePropertyChanged(nameof(this.Selection));
                this.RaisePropertyChanged(nameof(this.IsValid));
            }
        }
    }

    /// <summary>
    /// The disambiguated selection recorded for this parameter in the session's typed
    /// <c>parameter-selections</c> map, or <see langword="null"/> when the row is not an executor picker
    /// or nothing is selected (issue #1440).
    /// </summary>
    public JsonElement? Selection => this.IsExecutorPicker ? this.selectedExecutorOption?.Selection : null;

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

    public bool IsValid => this.IsExecutorPicker
        ? !this.IsRequired || this.selectedExecutorOption is not null
        : !this.IsRequired || !string.IsNullOrWhiteSpace(this.Value);
}
