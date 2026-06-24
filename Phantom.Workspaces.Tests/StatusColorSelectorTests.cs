using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tests;

public sealed class StatusColorSelectorTests
{
    private static FieldStatus FieldStatus(
        string[] good,
        string[] bad)
        => new(good, bad);

    [Fact]
    public void SelectStatusBrushKey_GoodValue_ReturnsGoodBrushKey()
    {
        var selector = new StatusColorSelector();

        var key = selector.SelectStatusBrushKey("completed", FieldStatus(["completed"], ["blocked", "cancelled"]));

        Assert.Equal("Theme.Status.Good", key);
    }

    [Fact]
    public void SelectStatusBrushKey_BadValue_ReturnsBadBrushKey()
    {
        var selector = new StatusColorSelector();

        var key = selector.SelectStatusBrushKey("blocked", FieldStatus(["completed"], ["blocked", "cancelled"]));

        Assert.Equal("Theme.Status.Bad", key);
    }

    [Fact]
    public void SelectStatusBrushKey_OtherValue_ReturnsStablePaletteKey()
    {
        var selector = new StatusColorSelector();

        // "active" is neither good nor bad. The expected palette index is fixed: it proves the hash
        // is a stable FNV-1a (FNV-1a("active") % 6 == 3), not the per-process-randomized GetHashCode.
        var key = selector.SelectStatusBrushKey("active", FieldStatus(["completed"], ["blocked"]));

        Assert.Equal("Theme.Status.Palette.3", key);
    }

    [Fact]
    public void SelectStatusBrushKey_SameValue_AlwaysYieldsSameKey()
    {
        var selector = new StatusColorSelector();
        var fieldStatus = FieldStatus(["completed"], ["blocked"]);

        var first = selector.SelectStatusBrushKey("in-progress", fieldStatus);
        var second = selector.SelectStatusBrushKey("in-progress", fieldStatus);

        Assert.Equal(first, second);
        Assert.Equal("Theme.Status.Palette.2", first);
    }

    [Fact]
    public void SelectStatusBrushKey_DifferentOtherValues_GenerallyMapToDifferentPaletteKeys()
    {
        var selector = new StatusColorSelector();
        var fieldStatus = FieldStatus([], []);

        var activeKey = selector.SelectStatusBrushKey("active", fieldStatus);
        var pendingKey = selector.SelectStatusBrushKey("pending", fieldStatus);

        Assert.NotEqual(activeKey, pendingKey);
        Assert.Equal("Theme.Status.Palette.3", activeKey);
        Assert.Equal("Theme.Status.Palette.0", pendingKey);
    }

    [Fact]
    public void SelectStatusBrushKey_Matching_IsCaseSensitive()
    {
        var selector = new StatusColorSelector();
        var fieldStatus = FieldStatus(["completed"], ["blocked"]);

        var exact = selector.SelectStatusBrushKey("completed", fieldStatus);
        var differentCase = selector.SelectStatusBrushKey("Completed", fieldStatus);

        Assert.Equal("Theme.Status.Good", exact);
        Assert.StartsWith("Theme.Status.Palette.", differentCase);
        Assert.NotEqual("Theme.Status.Good", differentCase);
    }

    [Fact]
    public void SelectStatusBrushKey_OtherValue_NeverMapsToGoodOrBadKey()
    {
        var selector = new StatusColorSelector();
        var fieldStatus = FieldStatus(["completed"], ["blocked"]);

        foreach (var value in new[] { "active", "pending", "in-progress", "draft", "review", "open", "merged" })
        {
            var key = selector.SelectStatusBrushKey(value, fieldStatus);

            Assert.NotEqual("Theme.Status.Good", key);
            Assert.NotEqual("Theme.Status.Bad", key);
            Assert.StartsWith("Theme.Status.Palette.", key);
        }
    }

    [Fact]
    public void SelectStatusBrushKey_NullFieldStatus_AlwaysHashesToPalette()
    {
        var selector = new StatusColorSelector();

        var key = selector.SelectStatusBrushKey("completed", fieldStatus: null);

        Assert.StartsWith("Theme.Status.Palette.", key);
    }
}
