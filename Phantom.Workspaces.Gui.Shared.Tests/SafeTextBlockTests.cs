using System.Linq;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Phantom.Workspaces.Gui.Shared.Controls;

namespace Phantom.Workspaces.Gui.Shared.Tests;

public sealed class SafeTextBlockTests
{
    private static Run[] Runs(SafeTextBlock block)
        => block.Inlines!.OfType<Run>().ToArray();

    [AvaloniaFact(Timeout = 15_000)]
    public void SafeTextBlock_MeasureOverride_ZeroWidth_ReturnsSizeEmpty()
    {
        // Issue #394 peer: measuring with Size(0,0) must not trigger catastrophic TextLineImpl allocation.
        var block = new SafeTextBlock { Text = "Hello, world!", TextWrapping = TextWrapping.Wrap };

        block.Measure(new Size(0, 0));

        Assert.Equal(new Size(0, 0), block.DesiredSize);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SafeTextBlock_SearchQuerySetAfterRealize_RendersHighlightedRun()
    {
        var block = new SafeTextBlock { Text = "the foo bar" };
        block.Measure(new Size(400, double.PositiveInfinity));

        block.SearchQuery = "foo";

        var highlighted = Runs(block).Where(r => r.Background is not null).ToArray();
        Assert.Single(highlighted);
        Assert.Equal("foo", highlighted[0].Text);
        Assert.Same(block.HighlightBrush, highlighted[0].Background);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SafeTextBlock_SearchQueryMatchesMultipleTimes_HighlightsAllOccurrences()
    {
        var block = new SafeTextBlock { Text = "foo bar foo baz foo" };
        block.Measure(new Size(400, double.PositiveInfinity));

        block.SearchQuery = "foo";

        var highlighted = Runs(block).Where(r => r.Background is not null).ToArray();
        Assert.Equal(3, highlighted.Length);
        Assert.All(highlighted, r => Assert.Equal("foo", r.Text));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SafeTextBlock_SearchQueryEmpty_RendersSinglePlainRun()
    {
        var block = new SafeTextBlock { Text = "hello world" };
        block.Measure(new Size(400, double.PositiveInfinity));

        block.SearchQuery = "";

        var runs = Runs(block);
        Assert.Single(runs);
        Assert.Equal("hello world", runs[0].Text);
        Assert.Null(runs[0].Background);
    }
}
