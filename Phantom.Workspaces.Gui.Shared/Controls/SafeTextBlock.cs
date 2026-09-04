using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Phantom.Workspaces.Gui.Shared.Controls;

/// <summary>
/// A non-selectable <see cref="TextBlock"/> peer of <see cref="SafeSelectableTextBlock"/>. It carries
/// the same zero-width <see cref="MeasureOverride"/> guard (issue #394) and the same reusable
/// text-highlight capability (issue #1258) for surfaces that want search highlighting without
/// selection/copy. Both controls route through <see cref="TextHighlighter"/> so the run-building and
/// layout-invalidation logic stays DRY.
/// </summary>
public class SafeTextBlock : TextBlock
{
    /// <summary>
    /// The substring to highlight (case-insensitive, all occurrences). Null / empty / whitespace
    /// renders a single plain run with no highlight.
    /// </summary>
    public static readonly StyledProperty<string?> SearchQueryProperty =
        AvaloniaProperty.Register<SafeTextBlock, string?>(nameof(SearchQuery));

    /// <summary>The background brush painted behind matched substrings.</summary>
    public static readonly StyledProperty<IBrush> HighlightBrushProperty =
        AvaloniaProperty.Register<SafeTextBlock, IBrush>(
            nameof(HighlightBrush),
            new SolidColorBrush(Color.Parse("#FFF59D")));

    // The authoritative source text. See SafeSelectableTextBlock for why this.Text cannot be trusted
    // once the inlines are populated.
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
                inlines.Clear();
                this.SetCurrentValue(TextProperty, this.sourceText);
            }
            else
            {
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
