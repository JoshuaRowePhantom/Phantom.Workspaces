using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Phantom.Workspaces.Gui.Shared.Controls;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Gui.Shared.Tests;

public sealed class SafeSelectableTextBlockTests
{
    private static Run[] Runs(SafeSelectableTextBlock block)
        => block.Inlines!.OfType<Run>().ToArray();

    [AvaloniaFact(Timeout = 15_000)]
    public void SafeSelectableTextBlock_MeasureOverride_ZeroWidth_ReturnsSizeEmpty()
    {
        // Issue #394: measuring with Size(0,0) must not trigger catastrophic TextLineImpl allocation.
        var block = new SafeSelectableTextBlock { Text = "Hello, world!", TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        block.Measure(new Size(0, 0));

        Assert.Equal(new Size(0, 0), block.DesiredSize);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SafeSelectableTextBlock_MeasureOverride_NonZeroWidth_DelegatesToBase()
    {
        var block = new SafeSelectableTextBlock { Text = "Hello, world!", TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        block.Measure(new Size(400, double.PositiveInfinity));

        Assert.True(block.DesiredSize.Width > 0, "Expected non-zero desired width when measured with real constraint.");
        Assert.True(block.DesiredSize.Height > 0, "Expected non-zero desired height when measured with real constraint.");
    }

    [AvaloniaFact(Timeout = 15_000)]
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

    [AvaloniaFact(Timeout = 15_000)]
    public void SafeSelectableTextBlock_SearchQuerySetAfterRealize_RendersHighlightedRun()
    {
        var block = new SafeSelectableTextBlock { Text = "the foo bar" };
        block.Measure(new Size(400, double.PositiveInfinity));
        var before = block.DesiredSize;

        block.SearchQuery = "foo";
        block.Measure(new Size(400, double.PositiveInfinity));

        var highlighted = Runs(block).Where(r => r.Background is not null).ToArray();
        Assert.Single(highlighted);
        Assert.Equal("foo", highlighted[0].Text);
        Assert.Same(block.HighlightBrush, highlighted[0].Background);
        // Layout re-formatted (inlines were rebuilt) rather than staying baked at the initial value.
        Assert.True(block.DesiredSize.Height > 0);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SafeSelectableTextBlock_SearchQueryMatchesMultipleTimes_HighlightsAllOccurrences()
    {
        var block = new SafeSelectableTextBlock { Text = "foo bar foo baz foo" };
        block.Measure(new Size(400, double.PositiveInfinity));

        block.SearchQuery = "foo";

        var runs = Runs(block);
        var highlighted = runs.Where(r => r.Background is not null).ToArray();
        Assert.Equal(3, highlighted.Length);
        Assert.All(highlighted, r => Assert.Equal("foo", r.Text));
        Assert.Equal("foo bar foo baz foo", string.Concat(runs.Select(r => r.Text)));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SafeSelectableTextBlock_SearchQueryDiffersInCase_MatchesCaseInsensitively()
    {
        var block = new SafeSelectableTextBlock { Text = "the foo bar" };
        block.Measure(new Size(400, double.PositiveInfinity));

        block.SearchQuery = "FOO";

        var highlighted = Runs(block).Where(r => r.Background is not null).ToArray();
        Assert.Single(highlighted);
        Assert.Equal("foo", highlighted[0].Text);
    }

    [AvaloniaTheory(Timeout = 15_000)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SafeSelectableTextBlock_SearchQueryEmpty_RendersSinglePlainRun(string? query)
    {
        var block = new SafeSelectableTextBlock { Text = "hello world" };
        block.Measure(new Size(400, double.PositiveInfinity));

        block.SearchQuery = query;

        var runs = Runs(block);
        Assert.Single(runs);
        Assert.Equal("hello world", runs[0].Text);
        Assert.Null(runs[0].Background);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SafeSelectableTextBlock_SearchQueryNoMatch_RendersSinglePlainRun()
    {
        var block = new SafeSelectableTextBlock { Text = "hello world" };
        block.Measure(new Size(400, double.PositiveInfinity));

        block.SearchQuery = "zzz";

        var runs = Runs(block);
        Assert.Single(runs);
        Assert.Equal("hello world", runs[0].Text);
        Assert.Null(runs[0].Background);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SafeSelectableTextBlock_SearchQueryHighlightActive_SelectionAndCopyStillWork()
    {
        var block = new SafeSelectableTextBlock { Text = "foo bar foo" };
        block.Measure(new Size(400, double.PositiveInfinity));
        block.SearchQuery = "foo";

        block.SelectionStart = 0;
        block.SelectionEnd = "foo bar foo".Length;

        Assert.Equal("foo bar foo", block.SelectedText);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SafeSelectableTextBlock_TextChangedWhileSearchQueryActive_RebuildsHighlight()
    {
        var block = new SafeSelectableTextBlock { Text = "foo one" };
        block.Measure(new Size(400, double.PositiveInfinity));
        block.SearchQuery = "foo";
        Assert.Single(Runs(block), r => r.Background is not null);

        block.Text = "foo two foo";

        var highlighted = Runs(block).Where(r => r.Background is not null).ToArray();
        Assert.Equal(2, highlighted.Length);
        Assert.Equal("foo two foo", string.Concat(Runs(block).Select(r => r.Text)));
    }
}
