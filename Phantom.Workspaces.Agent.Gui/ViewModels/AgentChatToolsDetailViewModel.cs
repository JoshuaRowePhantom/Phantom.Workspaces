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
                this.DisplayedRootItems.Add(CloneNavigationItem(tool));
            }
        }
        else
        {
            var sourceItem = FindById(this.toolNavigationItems, item.Id) ?? item;
            this.DisplayedRootItems.Add(CloneNavigationItem(sourceItem));
        }
    }

    private static AgentEditorNavigationItemViewModel CloneNavigationItem(AgentEditorNavigationItemViewModel source)
        => new(
            source.Id,
            source.Name,
            source.ToolId,
            source.Summary,
            source.Tool,
            source.DetailContent,
            source.Children.Select(CloneNavigationItem).ToArray(),
            source.IsExpanded);

    private static AgentEditorNavigationItemViewModel? FindById(IEnumerable<AgentEditorNavigationItemViewModel> roots, string id)
    {
        foreach (var root in roots)
        {
            if (string.Equals(root.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            var match = FindById(root.Children, id);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }
}
