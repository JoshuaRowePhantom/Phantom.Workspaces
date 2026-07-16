using System.Collections.ObjectModel;
using Avalonia.Media;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentEditorNavigationItemViewModel : ViewModelBase
{
    private bool isExpanded;
    private bool hideCompletedAgents = true;
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
        IRunningSubAgent? runningSubAgent = null,
        bool showHideCompletedToggle = false)
    {
        this.Id = id;
        this.name = name;
        this.ToolId = toolId;
        this.Summary = summary;
        this.Tool = tool;
        this.DetailContent = detailContent;
        this.RunningSubAgent = runningSubAgent;
        this.ShowHideCompletedToggle = showHideCompletedToggle;
        this.Children = [.. children];
        this.IsExpanded = isExpanded;
        this.ToggleExpandCommand = new RelayCommand(() => this.IsExpanded = !this.IsExpanded, () => this.HasChildren);
        this.Children.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(this.HasChildren));
            this.RaisePropertyChanged(nameof(this.NotHasChildren));
            this.RaisePropertyChanged(nameof(this.ExpandArrow));
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

    /// <summary>
    /// When <see langword="true"/>, this nav item renders a "hide completed" checkbox in its header
    /// (only the Sub-agents root sets this). See issue #1033.
    /// </summary>
    public bool ShowHideCompletedToggle { get; }

    /// <summary>
    /// When <see langword="true"/> (the default), completed (Succeeded/Failed) sub-agents are hidden
    /// from this item's children in the editor tree. Independent from the browser-panel toggle
    /// (<see cref="SubAgentBrowserViewModel.HideCompleted"/>). See issue #1033.
    /// </summary>
    public bool HideCompletedAgents
    {
        get => this.hideCompletedAgents;
        set => this.SetProperty(ref this.hideCompletedAgents, value);
    }

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

    public string ExpandArrow => this.IsExpanded ? "▴" : "▾";

    public IBrush ChildRailBrush { get; } = Brushes.Gray;

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

            this.RaisePropertyChanged(nameof(this.ExpandArrow));
        }
    }
}
