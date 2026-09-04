using System;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Phantom.Workspaces.Gui.Shared.Controls;

/// <summary>
/// Shared helper that rebuilds an <see cref="InlineCollection"/> to represent a piece of text with
/// every case-insensitive occurrence of a search query wrapped in a highlighted <see cref="Run"/>.
/// The Safe text controls call this from their <c>Text</c>/<c>SearchQuery</c>/<c>HighlightBrush</c>
/// change handlers and then invalidate their text layout, which is precisely the step the older
/// three-bound-<c>Run</c> template omitted (see issue #1258).
/// </summary>
internal static class TextHighlighter
{
    /// <summary>
    /// Rebuilds <paramref name="inlines"/> to represent <paramref name="text"/> with every
    /// case-insensitive occurrence of <paramref name="searchQuery"/> wrapped in a <see cref="Run"/>
    /// whose <see cref="Run.Background"/> is <paramref name="highlightBrush"/>. When
    /// <paramref name="searchQuery"/> is null/empty/whitespace or does not appear in
    /// <paramref name="text"/>, <paramref name="inlines"/> receives a single plain <see cref="Run"/>.
    /// Callers must invalidate the owning control's text layout after mutation.
    /// </summary>
    public static void Apply(
        InlineCollection inlines,
        string? text,
        string? searchQuery,
        IBrush highlightBrush)
    {
        ArgumentNullException.ThrowIfNull(inlines);

        inlines.Clear();
        var body = text ?? string.Empty;

        if (string.IsNullOrWhiteSpace(searchQuery) || body.Length == 0)
        {
            inlines.Add(new Run(body));
            return;
        }

        var query = searchQuery!;
        var i = 0;
        while (i < body.Length)
        {
            var hit = body.IndexOf(query, i, StringComparison.OrdinalIgnoreCase);
            if (hit < 0)
            {
                inlines.Add(new Run(body.Substring(i)));
                break;
            }

            if (hit > i)
            {
                inlines.Add(new Run(body.Substring(i, hit - i)));
            }

            inlines.Add(new Run(body.Substring(hit, query.Length))
            {
                Background = highlightBrush,
            });

            i = hit + query.Length;
        }
    }
}
