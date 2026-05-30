using Phantom.Workspaces.Gui.Styles.Controls;

namespace Phantom.Workspaces.Gui.Styles.Tests;

public class TreeStickyTests
{
    [Fact]
    public void IsExpandableDataContext_ReturnsTrue_WhenHasChildrenTrue()
    {
        var value = TreeSticky.IsExpandableDataContext(new { HasChildren = true });
        Assert.True(value);
    }

    [Fact]
    public void IsExpandableDataContext_ReturnsFalse_WhenHasChildrenFalse()
    {
        var value = TreeSticky.IsExpandableDataContext(new { HasChildren = false });
        Assert.False(value);
    }

    [Fact]
    public void IsExpandableDataContext_ReturnsTrue_WhenNotHasChildrenFalse()
    {
        var value = TreeSticky.IsExpandableDataContext(new { NotHasChildren = false });
        Assert.True(value);
    }

    [Fact]
    public void IsExpandableDataContext_ReturnsFalse_WhenChildrenMissing()
    {
        var value = TreeSticky.IsExpandableDataContext(new { Name = "node" });
        Assert.False(value);
    }

    [Fact]
    public void IsExpandableDataContext_ReturnsTrue_WhenChildrenHasItems()
    {
        var value = TreeSticky.IsExpandableDataContext(new { Children = new[] { "child" } });
        Assert.True(value);
    }

    [Fact]
    public void IsExpandableDataContext_ReturnsFalse_WhenChildrenEmpty()
    {
        var value = TreeSticky.IsExpandableDataContext(new { Children = Array.Empty<string>() });
        Assert.False(value);
    }

    [Fact]
    public void IsExpandableDataContext_ReturnsTrue_WhenVisibleChildrenHasItems()
    {
        var value = TreeSticky.IsExpandableDataContext(new { VisibleChildren = new[] { "child" } });
        Assert.True(value);
    }

    [Fact]
    public void HasAny_ReturnsFalse_ForNull()
    {
        Assert.False(TreeSticky.HasAny(null));
    }

    [Fact]
    public void HasAny_ReturnsTrue_ForEnumerableWithItems()
    {
        Assert.True(TreeSticky.HasAny(new[] { 1 }));
    }
}
