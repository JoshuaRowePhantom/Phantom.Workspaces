using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Agent.Gui.Tests;

[Trait("Category", "SlowLayout")]
public sealed class AgentChatToolsDetailViewModelTests
{
    [AvaloniaFact]
    public void SetRootItem_UsesClonedNodes_InsteadOfSharedNavigationInstances()
    {
        var detail = new AgentChatToolsDetailViewModel();
        var detailContent = new object();
        var child = new AgentEditorNavigationItemViewModel(
            "tool-child",
            "Tool Child",
            "tool-child",
            "Child summary",
            tool: null,
            detailContent,
            []);
        var root = new AgentEditorNavigationItemViewModel(
            "tool-root",
            "Tool Root",
            "tool-root",
            "Root summary",
            tool: null,
            detailContent,
            [child]);

        detail.SetToolNavigationItems([root]);
        detail.SetRootItem(null);

        var renderedRoot = Assert.Single(detail.DisplayedRootItems);
        Assert.NotSame(root, renderedRoot);
        Assert.Equal(root.Id, renderedRoot.Id);

        var renderedChild = Assert.Single(renderedRoot.Children);
        Assert.NotSame(child, renderedChild);
        Assert.Equal(child.Id, renderedChild.Id);
    }

    [AvaloniaFact]
    public void CloneNavigationItem_TopLevelItems_StartCollapsed()
    {
        var detail = new AgentChatToolsDetailViewModel();
        var detailContent = new object();
        var child = new AgentEditorNavigationItemViewModel(
            "tool-child", "Tool Child", "tool-child", "Child summary", tool: null, detailContent, []);
        var root = new AgentEditorNavigationItemViewModel(
            "tool-root", "Tool Root", "tool-root", "Root summary", tool: null, detailContent, [child]);

        detail.SetToolNavigationItems([root]);
        detail.SetRootItem(null);

        var renderedRoot = Assert.Single(detail.DisplayedRootItems);
        Assert.False(renderedRoot.IsExpanded);
    }

    [AvaloniaFact]
    public void CloneNavigationItem_ChildItems_StartExpanded()
    {
        var detail = new AgentChatToolsDetailViewModel();
        var detailContent = new object();
        var child = new AgentEditorNavigationItemViewModel(
            "tool-child", "Tool Child", "tool-child", "Child summary", tool: null, detailContent, []);
        var root = new AgentEditorNavigationItemViewModel(
            "tool-root", "Tool Root", "tool-root", "Root summary", tool: null, detailContent, [child]);

        detail.SetToolNavigationItems([root]);
        detail.SetRootItem(null);

        var renderedRoot = Assert.Single(detail.DisplayedRootItems);
        var renderedChild = Assert.Single(renderedRoot.Children);
        Assert.True(renderedChild.IsExpanded);
    }
}
