using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Phantom.Workspaces.Gui.Shared.Controls;

/// <summary>
/// A <see cref="SelectableTextBlock"/> subclass that short-circuits MeasureOverride when
/// availableSize.Width is zero, preventing TextFormatterImpl.PerformTextWrapping from creating
/// one TextLineImpl per character (6 million lines → 5.8 GB allocation) when TextWrapping=Wrap
/// and the control is first-measured with Size(0,0) by Avalonia's layout manager.
/// </summary>
/// <remarks>
/// Also carries a reusable text-highlight capability (issue #1258): setting <see cref="SearchQuery"/>
/// re-formats the control so every case-insensitive occurrence of the query is drawn with a
/// <see cref="HighlightBrush"/> background. Unlike a template of three bound <c>Run</c> children,
/// rebuilding the inlines here re-formats the cached text layout, so the highlight actually
/// re-renders when either the text or the query changes after realize.
/// </remarks>
public class SafeSelectableTextBlock : SelectableTextBlock
{
    /// <summary>
    /// The substring to highlight (case-insensitive, all occurrences). Null / empty / whitespace
    /// renders a single plain run with no highlight.
    /// </summary>
    public static readonly StyledProperty<string?> SearchQueryProperty =
        AvaloniaProperty.Register<SafeSelectableTextBlock, string?>(nameof(SearchQuery));

    /// <summary>The background brush painted behind matched substrings.</summary>
    public static readonly StyledProperty<IBrush> HighlightBrushProperty =
        AvaloniaProperty.Register<SafeSelectableTextBlock, IBrush>(
            nameof(HighlightBrush),
            new SolidColorBrush(Color.Parse("#FFF59D")));

    // The authoritative source text. TextBlock/InlineCollection mutate the Text property as a side
    // effect of populating Inlines (see InlineCollection.Add + TextBlock.OnPropertyChanged), so we
    // cannot read this.Text after the first rebuild — we track the source ourselves.
    private string? sourceText;
    private bool rebuilding;

    public string? SearchQuery
    {
        get => this.GetValue(SearchQueryProperty);
        set => this.SetValue(SearchQueryProperty, value);
    }

    public IBrush HighlightBrush
    {
        get => this.GetValue(HighlightBrushProperty);
        set => this.SetValue(HighlightBrushProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty)
        {
            if (this.rebuilding)
                return;
            this.sourceText = change.GetNewValue<string?>();
            this.RebuildInlines();
        }
        else if (change.Property == SearchQueryProperty || change.Property == HighlightBrushProperty)
        {
            this.RebuildInlines();
        }
    }

    private void RebuildInlines()
    {
        var inlines = this.Inlines;
        if (inlines is null)
            return;

        this.rebuilding = true;
        try
        {
            var query = this.SearchQuery;
            var hasHighlight = !string.IsNullOrWhiteSpace(query)
                && !string.IsNullOrEmpty(this.sourceText)
                && this.sourceText!.IndexOf(query!, System.StringComparison.OrdinalIgnoreCase) >= 0;

            if (!hasHighlight)
            {
                // No highlight to render — keep the control in plain Text mode so consumers can
                // read Text and layout follows the normal fast path.
                inlines.Clear();
                this.SetCurrentValue(TextProperty, this.sourceText);
            }
            else
            {
                // Clear Text first so InlineCollection.Add does not re-inject a Run from the old Text.
                if (!string.IsNullOrEmpty(this.Text))
                    this.SetCurrentValue(TextProperty, null);

                TextHighlighter.Apply(inlines, this.sourceText, query, this.HighlightBrush);
            }
        }
        finally
        {
            this.rebuilding = false;
        }

        this.InvalidateTextLayout();
        this.InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (availableSize.Width == 0)
            return default;
        return base.MeasureOverride(availableSize);
    }
}
