using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Gui.Shared.Controls;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Gui.Shared.Tests;

public sealed class SafeSelectableTextBlockTests
{
    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void SafeSelectableTextBlock_MeasureOverride_ZeroWidth_ReturnsSizeEmpty()
    {
        // Issue #394: measuring with Size(0,0) must not trigger catastrophic TextLineImpl allocation.
        var block = new SafeSelectableTextBlock { Text = "Hello, world!", TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        block.Measure(new Size(0, 0));

        Assert.Equal(new Size(0, 0), block.DesiredSize);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void SafeSelectableTextBlock_MeasureOverride_NonZeroWidth_DelegatesToBase()
    {
        var block = new SafeSelectableTextBlock { Text = "Hello, world!", TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        block.Measure(new Size(400, double.PositiveInfinity));

        Assert.True(block.DesiredSize.Width > 0, "Expected non-zero desired width when measured with real constraint.");
        Assert.True(block.DesiredSize.Height > 0, "Expected non-zero desired height when measured with real constraint.");
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void SafeSelectableTextBlock_MeasureOverride_ZeroWidth_ThenRealWidth_ProducesCorrectLayout()
    {
        var block = new SafeSelectableTextBlock { Text = "Hello, world!", TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        // Simulate Avalonia's first-measure probe with Size(0,0)
        block.Measure(new Size(0, 0));
        Assert.Equal(new Size(0, 0), block.DesiredSize);

        // Re-measure with a real constraint — must produce correct layout
        block.InvalidateMeasure();
        block.Measure(new Size(400, double.PositiveInfinity));

        Assert.True(block.DesiredSize.Width > 0, "Expected non-zero desired width after re-measure with real constraint.");
        Assert.True(block.DesiredSize.Height > 0, "Expected non-zero desired height after re-measure with real constraint.");
    }
}
