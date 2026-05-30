using Phantom.Workspaces.Gui.Styles.Controls;

namespace Phantom.Workspaces.Gui.Styles.Tests;

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
        var result = StickyLayoutSelector.ComputePins([V("a", 0, top: 0, height: 40)]);
        var pin = Assert.Single(result);
        Assert.Equal("a", pin.Key);
        Assert.Equal(0, pin.PinY);
        Assert.Null(pin.PinX);
    }

    [Fact]
    public void ComputePins_SingleItemNotYetReachedAnchor_NotPinned()
    {
        var result = StickyLayoutSelector.ComputePins([V("a", 0, top: 100, height: 40)]);
        Assert.Empty(result);
    }

    [Fact]
    public void ComputePins_TwoItemsSameLevel_PinsHigherPositionOne()
    {
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
        var result = StickyLayoutSelector.ComputePins(
        [
            V("parent1", 0, top: 0, height: 40),
            V("child1", 1, top: 40, height: 32),
            V("parent2", 0, top: 120, height: 40),
        ]);

        var parent1Pin = result.Single(p => p.Key.Equals("parent1"));
        var child1Pin = result.Single(p => p.Key.Equals("child1"));
        Assert.Equal(0, parent1Pin.PinY);
        Assert.Equal(40, child1Pin.PinY);
        Assert.DoesNotContain(result, p => p.Key.Equals("parent2"));
    }

    [Fact]
    public void ComputePins_TwoStackedLists_SecondListTakesOverWhenItScrollsPastAnchor()
    {
        var result = StickyLayoutSelector.ComputePins(
        [
            V("parent1", 0, top: -120, height: 40),
            V("child1", 1, top: -80, height: 32),
            V("parent2", 0, top: -5, height: 40),
            V("child2", 1, top: 80, height: 32),
        ]);

        var parent2Pin = result.Single(p => p.Key.Equals("parent2"));
        Assert.Equal(0, parent2Pin.PinY);
        Assert.DoesNotContain(result, p => p.Key.Equals("parent1"));
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
