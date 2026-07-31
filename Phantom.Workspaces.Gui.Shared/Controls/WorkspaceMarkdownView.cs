using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Markdown.Avalonia;
using Markdown.Avalonia.SyntaxHigh;

namespace Phantom.Workspaces.Gui.Shared.Controls;

/// <summary>
/// Shared markdown control for in-entity markdown surfaces (note content, entity display items,
/// markdown MIME attachments, JSON-schema field docs). It hosts the free, MIT-licensed
/// <see cref="MarkdownScrollViewer"/> renderer so every surface shares one control and one policy:
/// text selection stays enabled and fenced code blocks are syntax-highlighted with language labels.
/// </summary>
/// <remarks>
/// The renderer is composed (not inherited) so consuming assemblies bind against this control's own
/// <see cref="Markdown"/> string property without referencing the Markdown.Avalonia package, whose
/// root <c>Markdown</c> namespace would otherwise collide with the domain <c>Markdown</c> content
/// type.
/// </remarks>
public sealed class WorkspaceMarkdownView : Decorator
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<WorkspaceMarkdownView, string?>(nameof(Markdown));

    private static readonly Uri s_baseUri = new("avares://Phantom.Workspaces.Gui.Shared/");
    private static readonly Uri s_codeStylesUri =
        new("avares://Phantom.Workspaces.Gui.Shared/Styles/MarkdownCodeStyles.axaml");

    private readonly MarkdownScrollViewer _viewer;

    public WorkspaceMarkdownView()
    {
        _viewer = new MarkdownScrollViewer
        {
            SelectionEnabled = true,
        };
        _viewer.Plugins.Plugins.Add(new SyntaxHighlight());
        // Issue #1173: Markdown.Avalonia installs its Fluent theme into the viewer's local Styles
        // collection at construction time (see MarkdownScrollViewer.MarkdownStyle). External
        // ancestor styles cannot outrank those local setters, so we attach our code-styling
        // overrides to the same local Styles collection to guarantee they win.
        _viewer.Styles.Add(new StyleInclude(s_baseUri) { Source = s_codeStylesUri });
        Child = _viewer;
    }

    /// <summary>Markdown source text to render.</summary>
    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>Whether rendered markdown text can be selected. Enabled by default.</summary>
    public bool SelectionEnabled
    {
        get => _viewer.SelectionEnabled;
        set => _viewer.SelectionEnabled = value;
    }

    /// <summary>The hosted Markdown.Avalonia renderer.</summary>
    internal MarkdownScrollViewer Renderer => _viewer;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
        {
            _viewer.Markdown = Markdown;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Mirror SafeSelectableTextBlock: a zero-width first measure of wrapping text can trigger a
        // catastrophic per-character line allocation inside the renderer.
        if (availableSize.Width == 0)
            return default;
        return base.MeasureOverride(availableSize);
    }
}
