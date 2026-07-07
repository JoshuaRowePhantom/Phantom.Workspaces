using System.Collections.ObjectModel;

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
        bool isExpanded = false)
    {
        this.Id = id;
        this.name = name;
        this.ToolId = toolId;
        this.Summary = summary;
        this.Tool = tool;
        this.DetailContent = detailContent;
        this.Children = [.. children];
        this.IsExpanded = isExpanded;
        this.ToggleExpandCommand = new RelayCommand(() => this.IsExpanded = !this.IsExpanded, () => this.HasChildren);
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
