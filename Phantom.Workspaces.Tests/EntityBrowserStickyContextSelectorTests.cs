using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EntityBrowserStickyContextSelectorTests
{
    [Fact]
    public void SelectFocusedItemKey_PrefersFirstItemAtOrBelowViewportTop()
    {
        var focused = EntityBrowserStickyContextSelector.SelectFocusedItemKey(
        [
            new VisibleEntityListItemPosition("[\"entity-types\"]", Top: -120, Bottom: 20),
            new VisibleEntityListItemPosition("[\"entity-types\",\"entity-type\"]", Top: 24, Bottom: 160),
            new VisibleEntityListItemPosition("[\"entity-types\",\"json-schema\"]", Top: 180, Bottom: 300),
        ]);

        Assert.Equal("[\"entity-types\",\"entity-type\"]", focused);
    }

    [Fact]
    public void SelectFocusedItemKey_FallsBackToClosestCrossingItem_WhenNoItemStartsAtOrBelowTop()
    {
        var focused = EntityBrowserStickyContextSelector.SelectFocusedItemKey(
        [
            new VisibleEntityListItemPosition("[\"entity-types\"]", Top: -300, Bottom: 10),
            new VisibleEntityListItemPosition("[\"entity-types\",\"entity-type\"]", Top: -120, Bottom: 30),
            new VisibleEntityListItemPosition("[\"entity-types\",\"json-schema\"]", Top: -20, Bottom: 5),
        ]);

        Assert.Equal("[\"entity-types\",\"json-schema\"]", focused);
    }

    [Fact]
    public void SelectFocusedItemKey_ReturnsNull_WhenNoItemsVisible()
    {
        var focused = EntityBrowserStickyContextSelector.SelectFocusedItemKey(
        [
            new VisibleEntityListItemPosition("[\"entity-types\"]", Top: -300, Bottom: -20),
            new VisibleEntityListItemPosition("[\"entity-types\",\"entity-type\"]", Top: -120, Bottom: 0),
        ]);

        Assert.Null(focused);
    }
}
