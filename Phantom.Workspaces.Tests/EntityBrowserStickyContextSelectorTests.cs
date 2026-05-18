using System;
using System.Collections.Generic;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EntityBrowserStickyContextSelectorTests
{
    [Fact]
    public void SelectFocusedItem_PrefersFirstItemAtOrBelowAnchor()
    {
        var focused = EntityBrowserStickyContextSelector.SelectFocusedItem(
        [
            new VisibleEntityListItemPosition("[\"entity-types\"]", Top: -120, Bottom: 20),
            new VisibleEntityListItemPosition("[\"entity-types\",\"entity-type\"]", Top: 24, Bottom: 160),
            new VisibleEntityListItemPosition("[\"entity-types\",\"json-schema\"]", Top: 180, Bottom: 300),
        ],
        anchorY: 20);

        Assert.Equal("[\"entity-types\"]", focused.ItemKey);
        Assert.Equal(-120, focused.ItemTop);
    }

    [Fact]
    public void SelectFocusedItem_FallsBackToClosestCrossingItem_WhenNoItemStartsAtOrBelowAnchor()
    {
        var focused = EntityBrowserStickyContextSelector.SelectFocusedItem(
        [
            new VisibleEntityListItemPosition("[\"entity-types\"]", Top: -300, Bottom: 10),
            new VisibleEntityListItemPosition("[\"entity-types\",\"entity-type\"]", Top: -120, Bottom: 30),
            new VisibleEntityListItemPosition("[\"entity-types\",\"json-schema\"]", Top: -20, Bottom: 5),
        ],
        anchorY: 40);

        Assert.Equal("[\"entity-types\",\"json-schema\"]", focused.ItemKey);
        Assert.Equal(-20, focused.ItemTop);
    }

    [Fact]
    public void SelectFocusedItem_UsesClosestItemAboveAnchor_WhenAllItemsAreAbove()
    {
        var focused = EntityBrowserStickyContextSelector.SelectFocusedItem(
        [
            new VisibleEntityListItemPosition("[\"entity-types\"]", Top: -300, Bottom: -20),
            new VisibleEntityListItemPosition("[\"entity-types\",\"entity-type\"]", Top: -120, Bottom: 0),
        ],
        anchorY: 0);

        Assert.Equal("[\"entity-types\",\"entity-type\"]", focused.ItemKey);
        Assert.Equal(-120, focused.ItemTop);
    }

    [Fact]
    public void SelectFocusedItem_ReturnsNull_WhenNoItemsProvided()
    {
        var focused = EntityBrowserStickyContextSelector.SelectFocusedItem(
            Array.Empty<VisibleEntityListItemPosition>(),
            anchorY: 0);

        Assert.Null(focused.ItemKey);
    }

    [Fact]
    public void SelectFocusedItem_DoesNotReplaceSiblingUntilIncomingReachesAnchor()
    {
        var beforeReplacement = EntityBrowserStickyContextSelector.SelectFocusedItem(
        [
            new VisibleEntityListItemPosition("[\"entity-types\"]", Top: -180, Bottom: -110),
            new VisibleEntityListItemPosition("[\"json-schemas\"]", Top: 80, Bottom: 150),
        ],
        anchorY: 52);
        var afterReplacement = EntityBrowserStickyContextSelector.SelectFocusedItem(
        [
            new VisibleEntityListItemPosition("[\"entity-types\"]", Top: -232, Bottom: -162),
            new VisibleEntityListItemPosition("[\"json-schemas\"]", Top: 52, Bottom: 122),
        ],
        anchorY: 52);

        Assert.Equal("[\"entity-types\"]", beforeReplacement.ItemKey);
        Assert.Equal("[\"json-schemas\"]", afterReplacement.ItemKey);
    }

    [Fact]
    public void SelectPinnedItems_WhenScrolledPastAncestors_PinsRootAndParentAtExpectedOffsets()
    {
        var layout = EntityBrowserStickyContextSelector.SelectPinnedItems(
        [
            new VisibleEntityListItemPosition("[]", Top: -120, Bottom: -20),
            new VisibleEntityListItemPosition("[\"entity-types\"]", Top: -30, Bottom: 22),
            new VisibleEntityListItemPosition("[\"entity-types\",\"workspace\"]", Top: 25, Bottom: 90),
        ],
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["[]"] = null,
            ["[\"entity-types\"]"] = "[]",
            ["[\"entity-types\",\"workspace\"]"] = "[\"entity-types\"]",
        },
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["[]"] = 52,
            ["[\"entity-types\"]"] = 52,
            ["[\"entity-types\",\"workspace\"]"] = 52,
        });

        Assert.Equal("[\"entity-types\",\"workspace\"]", layout.FocusedItemKey);
        Assert.Collection(
            layout.PinnedItems,
            root =>
            {
                Assert.Equal("[]", root.ItemKey);
                Assert.Equal(0, root.Top);
            },
            parent =>
            {
                Assert.Equal("[\"entity-types\"]", parent.ItemKey);
                Assert.Equal(52, parent.Top);
            });
    }

    [Fact]
    public void SelectPinnedItems_WhenItemsHaveNotReachedViewportTop_ReturnsNoPinnedItems()
    {
        var layout = EntityBrowserStickyContextSelector.SelectPinnedItems(
        [
            new VisibleEntityListItemPosition("[]", Top: 12, Bottom: 64),
            new VisibleEntityListItemPosition("[\"entity-types\"]", Top: 70, Bottom: 122),
            new VisibleEntityListItemPosition("[\"entity-types\",\"workspace\"]", Top: 132, Bottom: 196),
        ],
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["[]"] = null,
            ["[\"entity-types\"]"] = "[]",
            ["[\"entity-types\",\"workspace\"]"] = "[\"entity-types\"]",
        },
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["[]"] = 52,
            ["[\"entity-types\"]"] = 52,
            ["[\"entity-types\",\"workspace\"]"] = 52,
        });

        Assert.Equal("[]", layout.FocusedItemKey);
        Assert.Empty(layout.PinnedItems);
    }
}
