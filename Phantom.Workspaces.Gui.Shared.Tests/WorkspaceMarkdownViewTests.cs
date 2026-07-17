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
        window.Styles.Add(Load("avares://Markdown.Avalonia/StyleCollections/MarkdownStyleFluentTheme.axaml"));
        window.Styles.Add(Load("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"));
        window.Styles.Add(Load("avares://Markdown.Avalonia.SyntaxHigh/StyleCollections/AppendixOfFluentTheme.axaml"));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    private static IStyle Load(string uri)
        => new StyleInclude(new Uri("avares://Phantom.Workspaces.Gui.Shared.Tests/")) { Source = new Uri(uri) };
}
