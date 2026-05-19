using Phantom.Workspaces.Controls;

namespace Phantom.Workspaces.Tests;

public class StickyLayoutSelectorTests
{
    private static StickyItemMeasurement V(string key, int level, double top, double height) =>
        new(key, Top: top, Left: 0, Height: height, Width: 0, VerticalLevel: level, HorizontalLevel: null);

    private static StickyItemMeasurement H(string key, int level, double left, double width) =>
        new(key, Top: 0, Left: left, Height: 0, Width: width, VerticalLevel: null, HorizontalLevel: level);

    private static StickyItemMeasurement VH(string key, int vLevel, int hLevel, double top, double left, double height, double width) =>
        new(key, Top: top, Left: left, Height: height, Width: width, VerticalLevel: vLevel, HorizontalLevel: hLevel);

    [Fact]
    public void ComputePins_NoItems_ReturnsEmpty()
    {
        var result = StickyLayoutSelector.ComputePins([]);
        Assert.Empty(result);
    }

    [Fact]
    public void ComputePins_SingleItemAtTopScrolledPast_PinsAtZero()
    {
        // Item at top=0, height=40 — at scroll offset 0 it should pin at 0
        var result = StickyLayoutSelector.ComputePins([V("a", 0, top: 0, height: 40)]);
        var pin = Assert.Single(result);
        Assert.Equal("a", pin.Key);
        Assert.Equal(0, pin.PinY);
        Assert.Null(pin.PinX);
    }

    [Fact]
    public void ComputePins_SingleItemNotYetReachedAnchor_NotPinned()
    {
        // Item at top=100, accumulated starts at 0 — 100 > 0, so not pinned
        var result = StickyLayoutSelector.ComputePins([V("a", 0, top: 100, height: 40)]);
        Assert.Empty(result);
    }

    [Fact]
    public void ComputePins_TwoItemsSameLevel_PinsHigherPositionOne()
    {
        // Two items at level 0: a at top=0, b at top=50
        // Accumulated=0: highest position <= 0 is "a" (top=0)
        var result = StickyLayoutSelector.ComputePins(
        [
            V("a", 0, top: 0, height: 40),
            V("b", 0, top: 50, height: 40),
        ]);
        var pin = Assert.Single(result);
        Assert.Equal("a", pin.Key);
        Assert.Equal(0, pin.PinY);
    }

    [Fact]
    public void ComputePins_TwoItemsSameLevel_PinsLaterWhenItReachesAnchor()
    {
        // Item b has scrolled to top <= 0 (both have top <= 0 now)
        // Highest position wins → b at top=-10 vs a at top=-60: b wins (closer to anchor)
        var result = StickyLayoutSelector.ComputePins(
        [
            V("a", 0, top: -60, height: 40),
            V("b", 0, top: -10, height: 40),
        ]);
        var pin = Assert.Single(result);
        Assert.Equal("b", pin.Key);
        Assert.Equal(0, pin.PinY);
    }

    [Fact]
    public void ComputePins_TwoLevels_AccumulatesCorrectly()
    {
        // Level 0: "parent" at top=0, height=40 → pins at 0, accumulated becomes 40
        // Level 1: "child" at top=100 (has scrolled past 40 now) → pins at 40
        var result = StickyLayoutSelector.ComputePins(
        [
            V("parent", 0, top: 0, height: 40),
            V("child", 1, top: 40, height: 32),
        ]);
        Assert.Equal(2, result.Count);
        var parentPin = result.Single(p => p.Key.Equals("parent"));
        var childPin = result.Single(p => p.Key.Equals("child"));
        Assert.Equal(0, parentPin.PinY);
        Assert.Equal(40, childPin.PinY);
    }

    [Fact]
    public void ComputePins_TwoLevels_ChildNotYetReachedAccumulated_OnlyParentPinned()
    {
        // Level 0: parent at top=-10 → pins at 0, accumulated = 40
        // Level 1: child at top=100 (> 40, not yet scrolled to accumulated) → not pinned
        var result = StickyLayoutSelector.ComputePins(
        [
            V("parent", 0, top: -10, height: 40),
            V("child", 1, top: 100, height: 32),
        ]);
        var pin = Assert.Single(result);
        Assert.Equal("parent", pin.Key);
        Assert.Equal(0, pin.PinY);
    }

    [Fact]
    public void ComputePins_TwoStackedLists_EachListPinsItsOwnParent()
    {
        // List 1: parent1 at top=0/height=40, child1 at top=40/height=32
        // List 2: parent2 at top=120/height=40 — NOT yet in view (top=120 > accumulated=0)
        // After scrolling, parent1 pins at 0; child1 pins at 40
        var result = StickyLayoutSelector.ComputePins(
        [
            V("parent1", 0, top: 0, height: 40),
            V("child1", 1, top: 40, height: 32),
            V("parent2", 0, top: 120, height: 40),
        ]);

        // At accumulated=0: parent1 (top=0 ≤ 0) wins over parent2 (top=120 > 0)
        var parent1Pin = result.Single(p => p.Key.Equals("parent1"));
        var child1Pin = result.Single(p => p.Key.Equals("child1"));
        Assert.Equal(0, parent1Pin.PinY);
        Assert.Equal(40, child1Pin.PinY);
        Assert.DoesNotContain(result, p => p.Key.Equals("parent2"));
    }

    [Fact]
    public void ComputePins_TwoStackedLists_SecondListTakesOverWhenItScrollsPastAnchor()
    {
        // After scrolling: parent1 at top=-120, parent2 at top=-5 (it's closer to the anchor)
        // parent2 should now be pinned at 0 instead of parent1
        var result = StickyLayoutSelector.ComputePins(
        [
            V("parent1", 0, top: -120, height: 40),
            V("child1", 1, top: -80, height: 32),
            V("parent2", 0, top: -5, height: 40),
            V("child2", 1, top: 80, height: 32),
        ]);

        var parent2Pin = result.Single(p => p.Key.Equals("parent2"));
        Assert.Equal(0, parent2Pin.PinY);
        // parent1 should not be pinned (it was overtaken by parent2)
        Assert.DoesNotContain(result, p => p.Key.Equals("parent1"));
        // child2 at top=80 > accumulated(40), so not pinned
        Assert.DoesNotContain(result, p => p.Key.Equals("child2"));
    }

    [Fact]
    public void ComputePins_HorizontalOnly_PinsCorrectly()
    {
        var result = StickyLayoutSelector.ComputePins(
        [
            H("col0", 0, left: 0, width: 60),
            H("col1", 1, left: 60, width: 80),
        ]);
        var col0 = result.Single(p => p.Key.Equals("col0"));
        var col1 = result.Single(p => p.Key.Equals("col1"));
        Assert.Equal(0, col0.PinX);
        Assert.Null(col0.PinY);
        Assert.Equal(60, col1.PinX);
        Assert.Null(col1.PinY);
    }

    [Fact]
    public void ComputePins_SkipsMissingLevels()
    {
        var result = StickyLayoutSelector.ComputePins(
        [
            V("parent", 0, top: -5, height: 40),
            V("grandchild", 2, top: 35, height: 24),
        ]);

        var parent = result.Single(p => p.Key.Equals("parent"));
        var grandchild = result.Single(p => p.Key.Equals("grandchild"));
        Assert.Equal(0, parent.PinY);
        Assert.Equal(40, grandchild.PinY);
    }

    [Fact]
    public void ComputePins_CanPinBothAxesForSameItem()
    {
        var result = StickyLayoutSelector.ComputePins(
        [
            VH("corner", vLevel: 0, hLevel: 0, top: -8, left: -12, height: 32, width: 48),
            VH("child", vLevel: 1, hLevel: 1, top: 32, left: 48, height: 24, width: 40),
        ]);

        var corner = result.Single(p => p.Key.Equals("corner"));
        var child = result.Single(p => p.Key.Equals("child"));
        Assert.Equal(0, corner.PinY);
        Assert.Equal(0, corner.PinX);
        Assert.Equal(32, child.PinY);
        Assert.Equal(48, child.PinX);
    }
}
