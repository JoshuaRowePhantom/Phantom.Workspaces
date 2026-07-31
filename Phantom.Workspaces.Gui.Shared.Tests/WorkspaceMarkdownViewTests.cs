using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using ColorTextBlock.Avalonia;
using Phantom.Workspaces.Gui.Shared.Controls;

namespace Phantom.Workspaces.Gui.Shared.Tests;

public sealed class WorkspaceMarkdownViewTests
{
    [AvaloniaFact(Timeout = 30_000)]
    public void WorkspaceMarkdownView_BoldAndItalic_RendersFormattedInlines()
    {
        var view = Render("This is **bold** and *italic* text.");

        var inlines = Flatten(view).ToList();

        Assert.Contains(inlines, i => i.FontWeight == FontWeight.Bold);
        Assert.Contains(inlines, i => i.FontStyle == FontStyle.Italic);
        Assert.Contains(inlines.OfType<CRun>(), r => r.Text == "bold");
        Assert.Contains(inlines.OfType<CRun>(), r => r.Text == "italic");
    }

    [AvaloniaFact(Timeout = 30_000)]
    public void WorkspaceMarkdownView_Heading_RendersHeadingBlock()
    {
        var view = Render("# Agent Manifests\n\nBody text.");

        var headings = view.GetVisualDescendants()
            .OfType<CTextBlock>()
            .Where(b => b.Classes.Contains("Heading1"))
            .ToList();

        var heading = Assert.Single(headings);
        Assert.Contains(FlattenInlines(heading.Content).OfType<CRun>(), r => r.Text == "Agent Manifests");
    }

    [AvaloniaFact(Timeout = 30_000)]
    public void WorkspaceMarkdownView_Link_RendersClickableHyperlink()
    {
        var view = Render("See [Example](https://example.com/docs) for details.");

        var hyperlink = Assert.Single(Flatten(view).OfType<CHyperlink>());
        Assert.NotNull(hyperlink.Command);
        Assert.Contains(FlattenInlines(hyperlink.Content).OfType<CRun>(), r => r.Text == "Example");
    }

    [AvaloniaFact(Timeout = 30_000)]
    public void WorkspaceMarkdownView_FencedCodeBlock_RendersHighlightedCodeWithLanguageLabel()
    {
        var view = Render("```csharp\nvar x = 1;\n```\n");

        var codeBlock = view.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("CodeBlock"));
        Assert.NotNull(codeBlock);

        // Syntax highlighting is provided by an AvaloniaEdit editor hosted in the code block.
        Assert.NotEmpty(codeBlock!.GetVisualDescendants().OfType<TextEditor>());

        // The fenced language is rendered as a label.
        var languageLabel = view.GetVisualDescendants()
            .OfType<Label>()
            .FirstOrDefault(l => l.Classes.Contains("LangInfo"));
        Assert.NotNull(languageLabel);
        Assert.Equal("csharp", languageLabel!.Content);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public void WorkspaceMarkdownView_SelectionEnabled_AllowsTextSelection()
    {
        var view = Render("Selectable body text.");

        Assert.True(view.SelectionEnabled);
        Assert.True(view.Renderer.SelectionEnabled);
    }

    // --- Issue #1173: native markdown code styling must match the HTML chat view. ---

    [AvaloniaFact(Timeout = 30_000)]
    public void WorkspaceMarkdownView_InlineCode_UsesMonospaceCodeFontFamily()
    {
        var view = Render("This has an inline `code` span.");

        var codeInline = Flatten(view).OfType<CCode>().FirstOrDefault();
        Assert.NotNull(codeInline);

        var expected = ExpectedCodeFontFamily(view);
        Assert.Equal(expected, codeInline!.FontFamily);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public void WorkspaceMarkdownView_InlineCode_UsesLegibleForegroundNotPurple()
    {
        var view = Render("Inline `x` here.");

        var codeInline = Flatten(view).OfType<CCode>().FirstOrDefault();
        Assert.NotNull(codeInline);

        var expected = ExpectedCodeForeground(view);
        Assert.Equal(BrushColor(expected), BrushColor(codeInline!.Foreground));
    }

    [AvaloniaFact(Timeout = 30_000)]
    public void WorkspaceMarkdownView_FencedCodeBlock_UsesMonospaceCodeFontFamily()
    {
        // Plain (no language) fenced block renders as a TextBlock.CodeBlock, not the AvaloniaEdit
        // syntax-highlight variant — so the FontFamily setter on TextBlock.CodeBlock applies.
        var view = Render("```\nplain code\n```\n");

        var textBlock = view.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(t => t.Classes.Contains("CodeBlock"));
        Assert.NotNull(textBlock);

        var expected = ExpectedCodeFontFamily(view);
        Assert.Equal(expected, textBlock!.FontFamily);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public void WorkspaceMarkdownView_FencedCodeBlock_UsesMatchingBackgroundBrush()
    {
        var view = Render("```\nplain code\n```\n");

        var border = view.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("CodeBlock"));
        Assert.NotNull(border);

        var expected = ExpectedCodeBlockBackground(view);
        Assert.Equal(BrushColor(expected), BrushColor(border!.Background));
    }

    [AvaloniaFact(Timeout = 30_000)]
    public void WorkspaceMarkdownView_CodeStyleResources_MatchHtmlChatOutputTokens()
    {
        // View lives inside a window that hosts SharedStyles.axaml. Resources resolve on the tree.
        var view = Render("test");

        Assert.True(view.TryFindResource("Markdown.CodeFontFamily", null, out var fontFamilyValue));
        var fontFamily = Assert.IsType<FontFamily>(fontFamilyValue);

        // chat-font-family in chat-output-shell.html: "Cascadia Code", Consolas, "Courier New", monospace.
        // Assert every entry appears in order; FontFamily.ToString() emits the full family list.
        var source = fontFamily.ToString();
        Assert.Contains("Cascadia Code", source);
        Assert.Contains("Consolas", source);
        Assert.Contains("Courier New", source);
        Assert.Contains("monospace", source);

        Assert.True(view.TryFindResource("Markdown.CodeForeground", null, out var foregroundValue));
        Assert.IsType<SolidColorBrush>(foregroundValue);

        Assert.True(view.TryFindResource("Markdown.CodeBlockBackground", null, out var backgroundValue));
        Assert.IsType<SolidColorBrush>(backgroundValue);
    }

    private static FontFamily ExpectedCodeFontFamily(WorkspaceMarkdownView view)
    {
        Assert.True(view.TryFindResource("Markdown.CodeFontFamily", null, out var value));
        return Assert.IsType<FontFamily>(value);
    }

    private static IBrush ExpectedCodeForeground(WorkspaceMarkdownView view)
    {
        Assert.True(view.TryFindResource("Markdown.CodeForeground", null, out var value));
        return Assert.IsAssignableFrom<IBrush>(value);
    }

    private static IBrush ExpectedCodeBlockBackground(WorkspaceMarkdownView view)
    {
        Assert.True(view.TryFindResource("Markdown.CodeBlockBackground", null, out var value));
        return Assert.IsAssignableFrom<IBrush>(value);
    }

    private static Color? BrushColor(IBrush? brush) =>
        brush is ISolidColorBrush s ? s.Color : (Color?)null;

    private static IEnumerable<CInline> Flatten(WorkspaceMarkdownView view)
        => view.GetVisualDescendants().OfType<CTextBlock>().SelectMany(b => FlattenInlines(b.Content));

    private static IEnumerable<CInline> FlattenInlines(IEnumerable<CInline>? inlines)
    {
        if (inlines is null)
        {
            yield break;
        }

        foreach (var inline in inlines)
        {
            yield return inline;
            if (inline is CSpan span)
            {
                foreach (var child in FlattenInlines(span.Content))
                {
                    yield return child;
                }
            }
        }
    }

    private static WorkspaceMarkdownView Render(string markdown)
    {
        var view = new WorkspaceMarkdownView { Markdown = markdown };
        var window = new Window { Width = 600, Height = 400, Content = view };
        // Load the app's SharedStyles.axaml so the issue #1173 code-styling overrides and
        // the Markdown.CodeFontFamily / Markdown.CodeForeground / Markdown.CodeBlockBackground
        // resources are present, not just the raw upstream Markdown.Avalonia Fluent theme.
        window.Styles.Add(Load("avares://Phantom.Workspaces.Gui.Shared/Styles/SharedStyles.axaml"));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    private static IStyle Load(string uri)
        => new StyleInclude(new Uri("avares://Phantom.Workspaces.Gui.Shared.Tests/")) { Source = new Uri(uri) };
}
