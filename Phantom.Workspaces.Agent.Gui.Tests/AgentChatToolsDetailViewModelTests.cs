using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentChatToolsDetailViewModelTests
{
    [Fact]
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
            [child],
            isExpanded: true);

        detail.SetToolNavigationItems([root]);
        detail.SetRootItem(null);

        var renderedRoot = Assert.Single(detail.DisplayedRootItems);
        Assert.NotSame(root, renderedRoot);
        Assert.Equal(root.Id, renderedRoot.Id);
        Assert.True(renderedRoot.IsExpanded);

        var renderedChild = Assert.Single(renderedRoot.Children);
        Assert.NotSame(child, renderedChild);
        Assert.Equal(child.Id, renderedChild.Id);
    }
}
