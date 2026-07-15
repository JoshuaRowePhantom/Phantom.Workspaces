using System.Collections.ObjectModel;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentEditorNavigationItemViewModel : ViewModelBase
{
    private bool isExpanded;
    private string name;

    public AgentEditorNavigationItemViewModel(
        string id,
        string name,
        string? toolId,
        string? summary,
        AgentChatToolViewModel? tool,
        object detailContent,
        IReadOnlyList<AgentEditorNavigationItemViewModel> children,
        bool isExpanded = false,
        IRunningSubAgent? runningSubAgent = null)
    {
        this.Id = id;
        this.name = name;
        this.ToolId = toolId;
        this.Summary = summary;
        this.Tool = tool;
        this.DetailContent = detailContent;
        this.RunningSubAgent = runningSubAgent;
        this.Children = [.. children];
        this.IsExpanded = isExpanded;
        this.ToggleExpandCommand = new RelayCommand(() => this.IsExpanded = !this.IsExpanded, () => this.HasChildren);
        this.Children.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(this.HasChildren));
            this.RaisePropertyChanged(nameof(this.NotHasChildren));
            this.ToggleExpandCommand.RaiseCanExecuteChanged();
        };
    }

    public string Id { get; }

    public string Name
    {
        get => this.name;
        set => this.SetProperty(ref this.name, value);
    }

    public string? ToolId { get; }

    public string? Summary { get; }

    public AgentChatToolViewModel? Tool { get; }

    public IRunningSubAgent? RunningSubAgent { get; }

    public AgentChatCompletionState? CompletionState => this.RunningSubAgent?.CompletionState;

    public DateTime? LastUpdatedAt => this.RunningSubAgent?.LastUpdatedAt;

    public bool HasCompletionState => this.RunningSubAgent is not null;

    public bool NotHasCompletionState => !this.HasCompletionState;

    public bool IsRunning => this.CompletionState == AgentChatCompletionState.Running;

    public bool IsSucceeded => this.CompletionState == AgentChatCompletionState.Succeeded;

    public bool IsFailed => this.CompletionState == AgentChatCompletionState.Failed;

    public bool IsIdle => this.CompletionState is null or AgentChatCompletionState.Unknown;

    public bool IsBrainVisible => this.HasCompletionState && !this.IsSucceeded && !this.IsFailed;

    public double StatusIconOpacity => this.IsIdle ? 0.25 : 1.0;

    internal void RefreshStatus()
    {
        this.RaisePropertyChanged(nameof(this.CompletionState));
        this.RaisePropertyChanged(nameof(this.LastUpdatedAt));
        this.RaisePropertyChanged(nameof(this.IsRunning));
        this.RaisePropertyChanged(nameof(this.IsSucceeded));
        this.RaisePropertyChanged(nameof(this.IsFailed));
        this.RaisePropertyChanged(nameof(this.IsIdle));
        this.RaisePropertyChanged(nameof(this.IsBrainVisible));
        this.RaisePropertyChanged(nameof(this.StatusIconOpacity));
    }

    public bool HasTool => this.Tool is not null;

    public bool NotHasTool => !this.HasTool;

    public bool HasChildren => this.Children.Count > 0;

    public bool NotHasChildren => !this.HasChildren;

    public RelayCommand ToggleExpandCommand { get; }

    public object DetailContent { get; }

    public ObservableCollection<AgentEditorNavigationItemViewModel> Children { get; }

    public bool IsExpanded
    {
        get => this.isExpanded;
        set
        {
            if (!this.SetProperty(ref this.isExpanded, value))
            {
                return;
            }

        }
    }
}
