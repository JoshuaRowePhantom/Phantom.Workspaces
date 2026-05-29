using System.Collections.ObjectModel;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentChatToolsDetailViewModel : ViewModelBase
{
    private IReadOnlyList<AgentEditorNavigationItemViewModel> toolNavigationItems = [];

    public ObservableCollection<AgentEditorNavigationItemViewModel> DisplayedRootItems { get; } = [];

    public void SetToolNavigationItems(IReadOnlyList<AgentEditorNavigationItemViewModel> toolNavigationItems)
    {
        this.toolNavigationItems = toolNavigationItems;
    }

    public void SetRootItem(AgentEditorNavigationItemViewModel? item)
    {
        this.DisplayedRootItems.Clear();
        if (item is null || string.Equals(item.Id, "chat-tools", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var tool in this.toolNavigationItems)
            {
                this.DisplayedRootItems.Add(tool);
            }
        }
        else
        {
            this.DisplayedRootItems.Add(item);
        }
    }
}
