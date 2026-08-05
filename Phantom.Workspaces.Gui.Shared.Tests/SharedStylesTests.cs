using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Reflection;
using System.Text.RegularExpressions;

using Phantom.Workspaces.Gui.Shared.Controls;
using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Gui.Shared.Tests;

[Collection("Avalonia")]
public sealed class SharedStylesTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public void ThemeClassFontWeightResources_AreTypedFontWeightValues()
    {
        var sharedStyles = LoadSharedStyles();
        var keys = sharedStyles.Resources.Keys
            .OfType<string>()
            .Where(static key =>
                key.StartsWith("Theme.Class.", StringComparison.Ordinal) &&
                key.EndsWith(".FontWeight", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            Assert.True(sharedStyles.Resources.TryGetValue(key, out var value), $"Expected resource key '{key}' to exist.");
            _ = Assert.IsType<FontWeight>(value);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void TextBlockClassStyles_WithFontWeightSetters_DoNotUseStringValues()
    {
        var sharedStyles = LoadSharedStyles();
        var textBlockStyles = sharedStyles
            .OfType<Style>()
            .Where(static s => s.Selector?.ToString()?.Contains("TextBlock.", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.NotEmpty(textBlockStyles);

        foreach (var style in textBlockStyles)
        {
            foreach (var setter in style.Setters.OfType<Setter>().Where(static s => s.Property == TextBlock.FontWeightProperty))
            {
                Assert.True(
                    setter.Value is not string,
                    $"Selector '{style.Selector}' uses string FontWeight setter value, which can throw runtime cast exceptions.");
            }
        }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public void SimpleClassStyles_CanAttachToResolvedControlTypes()
    {
        var sharedStyles = LoadSharedStyles();
        var simpleCases = sharedStyles
            .OfType<Style>()
            .Select(static style => new { Style = style, SelectorText = style.Selector?.ToString() })
            .Where(static x => !string.IsNullOrWhiteSpace(x.SelectorText))
            .Select(x => new { x.Style, Parsed = TryParseSimpleSelector(x.SelectorText!) })
            .Where(static x => x.Parsed is not null)
            .Select(x => new
            {
                x.Style,
                x.Parsed!.Value.ControlTypeName,
                x.Parsed.Value.Classes
            })
            .ToArray();

        Assert.NotEmpty(simpleCases);

        var failures = new List<string>();
        var host = new StackPanel();
        host.Styles.Add(sharedStyles);

        foreach (var testCase in simpleCases)
        {
            var controlType = ResolveStyledElementType(testCase.ControlTypeName);
            if (controlType is null || !typeof(Control).IsAssignableFrom(controlType))
            {
                continue;
            }

            Control control;
            try
            {
                control = (Control)Activator.CreateInstance(controlType)!;
            }
            catch (Exception ex)
            {
                failures.Add($"Could not instantiate {controlType.FullName}: {ex.GetType().Name} {ex.Message}");
                continue;
            }

            foreach (var className in testCase.Classes)
            {
                control.Classes.Add(className);
            }

            try
            {
                if (control is TopLevel)
                {
                    continue;
                }
                else
                {
                    host.Children.Add(control);
                    host.Measure(new Size(1000, 1000));
                    host.Arrange(new Rect(0, 0, 1000, 1000));
                    host.Children.Remove(control);
                }
            }
            catch (Exception ex)
            {
                host.Children.Remove(control);
                failures.Add($"Selector '{testCase.Style.Selector}' on {controlType.Name} threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void CopyableTextBox_InnerLeftContent_UsesTemplateSetter()
    {
        var repositoryRoot = FindRepositoryRoot();
        var stylesPath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Gui.Shared",
            "Styles",
            "SharedStyles.axaml");
        var stylesContent = File.ReadAllText(stylesPath);

        var setterStartIndex = stylesContent.IndexOf(
            "<Setter Property=\"InnerLeftContent\">",
            StringComparison.Ordinal);
        Assert.True(setterStartIndex >= 0, "SharedStyles.axaml must define InnerLeftContent setter for copyable text.");

        var setterEndIndex = stylesContent.IndexOf("</Setter>", setterStartIndex, StringComparison.Ordinal);
        Assert.True(setterEndIndex > setterStartIndex, "InnerLeftContent setter must be properly closed.");

        var templateStartIndex = stylesContent.IndexOf("<Template>", setterStartIndex, StringComparison.Ordinal);
        Assert.True(
            templateStartIndex > setterStartIndex && templateStartIndex < setterEndIndex,
            "InnerLeftContent setter must wrap control content in <Template>.");
    }

    [Fact]
    public void EntityCardTree_HoveredItem_HasBorderThicknessThree()
    {
        var repositoryRoot = FindRepositoryRoot();
        var stylesPath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Gui.Shared",
            "Styles",
            "SharedStyles.axaml");
        var stylesContent = File.ReadAllText(stylesPath);

        var hoverRule = ExtractStyle(stylesContent, "Border.entity-card:pointerover");
        Assert.Contains("<Setter Property=\"BorderThickness\" Value=\"3\" />", hoverRule, StringComparison.Ordinal);

        var selectedHoverRule = ExtractStyle(stylesContent, "Border.entity-card.selected:pointerover");
        Assert.Contains("<Setter Property=\"BorderThickness\" Value=\"3\" />", selectedHoverRule, StringComparison.Ordinal);
        Assert.Contains("Theme.Surface.EntityCard.SelectedBorder", selectedHoverRule, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTree_SelectedItem_UsesSelectedThemeBorder()
    {
        var repositoryRoot = FindRepositoryRoot();
        var darkTheme = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "Phantom.Workspaces.Gui.Shared", "Themes", "Dark.axaml"));
        var lightTheme = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "Phantom.Workspaces.Gui.Shared", "Themes", "Light.axaml"));

        // Reconciled to the canonical (formerly profile-effective) selection colours per issue #1004.
        Assert.Contains("<SolidColorBrush x:Key=\"Theme.Surface.EntityCard.SelectedBorder\">#5EA0FF</SolidColorBrush>", darkTheme, StringComparison.Ordinal);
        Assert.Contains("<SolidColorBrush x:Key=\"Theme.Surface.EntityCard.SelectedBorder\">#2B67D1</SolidColorBrush>", lightTheme, StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void StatusThemeResources_AreSolidColorBrushes()
    {
        // Theme.Status.* resources were moved to theme dictionaries (Dark.axaml and Light.axaml)
        // as part of issue #905. This test now verifies they are NOT in SharedStyles anymore.
        var sharedStyles = LoadSharedStyles();

        var statusKeys = new List<string>
        {
            "Theme.Status.Good",
            "Theme.Status.Bad",
            "Theme.Status.Foreground",
        };
        for (var index = 0; index < 6; index++)
        {
            statusKeys.Add($"Theme.Status.Palette.{index}");
        }

        foreach (var key in statusKeys)
        {
            Assert.False(sharedStyles.Resources.ContainsKey(key), 
                $"Theme status resource '{key}' should be in theme dictionaries, not SharedStyles.");
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatStatusLineResources_Resolve()
    {
        var statusLineStyles = LoadAgentChatStatusLineStyles();
        var brushKeys = new[]
        {
            "AgentChat.StatusLine.ThinkingBrain.Foreground",
            "AgentChat.StatusLine.Label.Foreground",
            "AgentChat.StatusLine.Value.Foreground",
        };

        foreach (var key in brushKeys)
        {
            Assert.True(statusLineStyles.Resources.TryGetValue(key, out var value), $"Expected resource key '{key}' to exist.");
            _ = Assert.IsAssignableFrom<ISolidColorBrush>(value);
        }

        Assert.True(statusLineStyles.Resources.TryGetValue("AgentChat.StatusLine.FontSize", out var fontSize));
        _ = Assert.IsType<double>(fontSize);
        Assert.True(statusLineStyles.Resources.TryGetValue("AgentChat.StatusLine.Padding", out var padding));
        _ = Assert.IsType<Thickness>(padding);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SharedStyles_QueueStatusStyles_DoNotReferenceSubmitStatusOption()
    {
        // Issue #253: SubmitStatusOption no longer exists on any ViewModel.
        // The queue-status-pill.dynamic, queue-status-label, and queue-status-caret
        // styles must not bind to it.
        var repositoryRoot = FindRepositoryRoot();
        var stylesPath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Gui.Shared",
            "Styles",
            "SharedStyles.axaml");
        var content = File.ReadAllText(stylesPath);

        Assert.DoesNotContain("SubmitStatusOption", content, StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SharedStyles_QueueImmediacyOptionPill_UsesOptionBrushBindings()
    {
        var repositoryRoot = FindRepositoryRoot();
        var stylesPath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Gui.Shared",
            "Styles",
            "SharedStyles.axaml");
        var content = File.ReadAllText(stylesPath);

        Assert.Contains("Background=\"{ReflectionBinding Background}\"", content, StringComparison.Ordinal);
        Assert.Contains("BorderBrush=\"{ReflectionBinding BorderBrush}\"", content, StringComparison.Ordinal);
        Assert.Contains("Text=\"{ReflectionBinding GlyphText}\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardStyle_WhenRendered_StretchesAndHasNoFixedMaxWidth()
    {
        var repositoryRoot = FindRepositoryRoot();
        var stylesPath = Path.Combine(repositoryRoot.FullName, "Phantom.Workspaces.Gui.Shared", "Styles", "SharedStyles.axaml");
        var stylesContent = File.ReadAllText(stylesPath);
        var cardRule = ExtractStyle(stylesContent, "Border.entity-card");

        Assert.Contains("<Setter Property=\"HorizontalAlignment\" Value=\"Stretch\" />", cardRule, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxWidth", cardRule, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardStyle_WhenHasChildren_BottomCornersAreSquare()
    {
        var repositoryRoot = FindRepositoryRoot();
        var stylesPath = Path.Combine(repositoryRoot.FullName, "Phantom.Workspaces.Gui.Shared", "Styles", "SharedStyles.axaml");
        var stylesContent = File.ReadAllText(stylesPath);

        var branchRule = ExtractStyle(stylesContent, "Border.entity-card.branch-header");
        var leafRule = ExtractStyle(stylesContent, "Border.entity-card.leaf");

        Assert.Contains("<Setter Property=\"CornerRadius\" Value=\"6,6,0,0\" />", branchRule, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"CornerRadius\" Value=\"6\" />", leafRule, StringComparison.Ordinal);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Phantom.Workspaces.slnx")))
            {
                return current;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
    }

    private static string ExtractStyle(string stylesContent, string selector)
    {
        var start = stylesContent.IndexOf($"<Style Selector=\"{selector}\">", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected style selector '{selector}' to exist.");
        var end = stylesContent.IndexOf("</Style>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Expected style selector '{selector}' to be closed.");
        return stylesContent[start..(end + "</Style>".Length)];
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SharedStyles_WorkspaceMarkdownViewer_TargetsMarkdownRenderer()
    {
        var styles = LoadSharedStyles();

        var markdownStyle = styles
            .OfType<Style>()
            .First(s => s.Selector?.ToString() == "Control.workspace-markdown-viewer");

        var view = new WorkspaceMarkdownView();
        view.Classes.Add("workspace-markdown-viewer");

        // The shared style targets the markdown viewer class, and the shared control really is the
        // free Markdown.Avalonia renderer (not a raw-text fallback).
        Assert.NotNull(markdownStyle);
        Assert.Contains("workspace-markdown-viewer", view.Classes);
        Assert.IsAssignableFrom<Markdown.Avalonia.MarkdownScrollViewer>(view.Renderer);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SharedStyles_AcceleratorAwareWebView_EnablesBrowserAcceleratorBehavior()
    {
        var styles = LoadSharedStyles();

        var acceleratorStyle = styles
            .OfType<Style>()
            .FirstOrDefault(s => s.Selector?.ToString()?.Contains("AcceleratorAwareWebView", StringComparison.Ordinal) == true);

        Assert.NotNull(acceleratorStyle);
        var setter = acceleratorStyle!.Setters
            .OfType<Setter>()
            .FirstOrDefault(s => s.Property == BrowserAcceleratorBehavior.IsEnabledProperty);
        Assert.NotNull(setter);

        var enabled = setter!.Value is bool boolValue
            ? boolValue
            : bool.Parse(setter.Value!.ToString()!);
        Assert.True(enabled);
    }

    private static Avalonia.Styling.Styles LoadSharedStyles()
    {
        var source = new Uri("avares://Phantom.Workspaces.Gui.Shared/Styles/SharedStyles.axaml");
        var baseUri = new Uri("avares://Phantom.Workspaces.Gui.Shared/");
        var loaded = AvaloniaXamlLoader.Load(source, baseUri);
        return Assert.IsType<Avalonia.Styling.Styles>(loaded);
    }

    private static Avalonia.Styling.Styles LoadAgentChatStatusLineStyles()
    {
        var source = new Uri("avares://Phantom.Workspaces.Gui.Shared/Styles/AgentChatStatusLineStyles.axaml");
        var baseUri = new Uri("avares://Phantom.Workspaces.Gui.Shared/");
        var loaded = AvaloniaXamlLoader.Load(source, baseUri);
        return Assert.IsType<Avalonia.Styling.Styles>(loaded);
    }

    private static (string ControlTypeName, string[] Classes)? TryParseSimpleSelector(string selectorText)
    {
        if (selectorText.IndexOfAny([' ', '>', ':', '/', '[', '#', '(', ')']) >= 0)
        {
            return null;
        }

        var match = Regex.Match(
            selectorText,
            @"^(?:(?<ns>[A-Za-z_][A-Za-z0-9_]*)\|)?(?<type>[A-Za-z_][A-Za-z0-9_]*)(?<classes>(?:\.[A-Za-z0-9_-]+)*)$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var classGroup = match.Groups["classes"].Value;
        var classes = Regex.Matches(classGroup, @"\.([A-Za-z0-9_-]+)", RegexOptions.CultureInvariant)
            .Select(static m => m.Groups[1].Value)
            .ToArray();
        return (match.Groups["type"].Value, classes);
    }

    private static Type? ResolveStyledElementType(string typeName)
    {
        var candidates = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(static assembly => SafeGetTypes(assembly))
            .Where(type =>
                typeof(StyledElement).IsAssignableFrom(type) &&
                !type.IsAbstract &&
                type.Name.Equals(typeName, StringComparison.Ordinal) &&
                type.GetConstructor(Type.EmptyTypes) is not null)
            .ToArray();

        return candidates
            .OrderByDescending(static type => type.Namespace?.StartsWith("Avalonia.Controls", StringComparison.Ordinal) == true)
            .FirstOrDefault();
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(static t => t is not null)!;
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCard_Shortcuts_HiddenByDefault()
    {
        var sharedStyles = LoadSharedStyles();

        var shortcutButton = new Button();
        shortcutButton.Classes.Add("workspace-entity-shortcut-button");

        var entityCard = new Border();
        entityCard.Classes.Add("entity-card");
        entityCard.Child = shortcutButton;

        var host = new StackPanel();
        host.Styles.Add(sharedStyles);
        host.Children.Add(entityCard);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(0.0, shortcutButton.Opacity);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCard_Shortcuts_VisibleOnPointerOver()
    {
        var repositoryRoot = FindRepositoryRoot();
        var stylesPath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Gui.Shared",
            "Styles",
            "SharedStyles.axaml");
        var content = File.ReadAllText(stylesPath);

        Assert.Contains("Border.entity-card:pointerover Button.workspace-entity-shortcut-button", content, StringComparison.Ordinal);

        var selectorStart = content.IndexOf("Border.entity-card:pointerover Button.workspace-entity-shortcut-button", StringComparison.Ordinal);
        var selectorEnd = content.IndexOf("</Style>", selectorStart, StringComparison.Ordinal);
        var styleBlock = content[selectorStart..selectorEnd];

        Assert.Contains("Opacity", styleBlock, StringComparison.Ordinal);
        Assert.Contains("1", styleBlock, StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCard_Shortcuts_VisibleOnFocusWithin()
    {
        var repositoryRoot = FindRepositoryRoot();
        var stylesPath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Gui.Shared",
            "Styles",
            "SharedStyles.axaml");
        var content = File.ReadAllText(stylesPath);

        Assert.Contains("Border.entity-card:focus-within Button.workspace-entity-shortcut-button", content, StringComparison.Ordinal);

        var selectorStart = content.IndexOf("Border.entity-card:focus-within Button.workspace-entity-shortcut-button", StringComparison.Ordinal);
        var selectorEnd = content.IndexOf("</Style>", selectorStart, StringComparison.Ordinal);
        var styleBlock = content[selectorStart..selectorEnd];

        Assert.Contains("Opacity", styleBlock, StringComparison.Ordinal);
        Assert.Contains("1", styleBlock, StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCard_Shortcuts_HaveOpacityTransition()
    {
        var repositoryRoot = FindRepositoryRoot();
        var stylesPath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Gui.Shared",
            "Styles",
            "SharedStyles.axaml");
        var content = File.ReadAllText(stylesPath);

        var selectorStart = content.IndexOf("Button.workspace-entity-shortcut-button", StringComparison.Ordinal);
        Assert.True(selectorStart >= 0, "Button.workspace-entity-shortcut-button style must exist.");

        var selectorEnd = content.IndexOf("</Style>", selectorStart, StringComparison.Ordinal);
        Assert.True(selectorEnd > selectorStart, "Button.workspace-entity-shortcut-button style must be closed.");

        var styleBlock = content[selectorStart..selectorEnd];
        Assert.Contains("DoubleTransition", styleBlock, StringComparison.Ordinal);
        Assert.Contains("Property=\"Opacity\"", styleBlock, StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCard_JsonButton_DefaultOpacity_IsZero()
    {
        // Issue #810: JSON button should be hidden by default like other shortcut buttons
        var sharedStyles = LoadSharedStyles();

        var jsonButton = new Button();
        jsonButton.Classes.Add("workspace-edit-indicator-button");

        var entityCard = new Border();
        entityCard.Classes.Add("entity-card");
        entityCard.Child = jsonButton;

        var host = new StackPanel();
        host.Styles.Add(sharedStyles);
        host.Children.Add(entityCard);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(0.0, jsonButton.Opacity);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCard_JsonButton_OnPointerOver_OpacityIsOne()
    {
        // Issue #810: JSON button should fade in on pointer over
        var repositoryRoot = FindRepositoryRoot();
        var stylesPath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Gui.Shared",
            "Styles",
            "SharedStyles.axaml");
        var content = File.ReadAllText(stylesPath);

        Assert.Contains("Border.entity-card:pointerover Button.workspace-edit-indicator-button", content, StringComparison.Ordinal);

        var selectorStart = content.IndexOf("Border.entity-card:pointerover Button.workspace-edit-indicator-button", StringComparison.Ordinal);
        var selectorEnd = content.IndexOf("</Style>", selectorStart, StringComparison.Ordinal);
        var styleBlock = content[selectorStart..selectorEnd];

        Assert.Contains("Opacity", styleBlock, StringComparison.Ordinal);
        Assert.Contains("1", styleBlock, StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCard_JsonButton_OnFocusWithin_OpacityIsOne()
    {
        // Issue #810: JSON button should fade in on focus within
        var repositoryRoot = FindRepositoryRoot();
        var stylesPath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Gui.Shared",
            "Styles",
            "SharedStyles.axaml");
        var content = File.ReadAllText(stylesPath);

        Assert.Contains("Border.entity-card:focus-within Button.workspace-edit-indicator-button", content, StringComparison.Ordinal);

        var selectorStart = content.IndexOf("Border.entity-card:focus-within Button.workspace-edit-indicator-button", StringComparison.Ordinal);
        var selectorEnd = content.IndexOf("</Style>", selectorStart, StringComparison.Ordinal);
        var styleBlock = content[selectorStart..selectorEnd];

        Assert.Contains("Opacity", styleBlock, StringComparison.Ordinal);
        Assert.Contains("1", styleBlock, StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCard_JsonButton_HasOpacityTransition()
    {
        // Issue #810: JSON button should have smooth fade-in transition
        var repositoryRoot = FindRepositoryRoot();
        var stylesPath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Gui.Shared",
            "Styles",
            "SharedStyles.axaml");
        var content = File.ReadAllText(stylesPath);

        var selectorStart = content.IndexOf("Button.workspace-edit-indicator-button", StringComparison.Ordinal);
        Assert.True(selectorStart >= 0, "Button.workspace-edit-indicator-button style must exist.");

        var selectorEnd = content.IndexOf("</Style>", selectorStart, StringComparison.Ordinal);
        Assert.True(selectorEnd > selectorStart, "Button.workspace-edit-indicator-button style must be closed.");

        var styleBlock = content[selectorStart..selectorEnd];
        Assert.Contains("DoubleTransition", styleBlock, StringComparison.Ordinal);
        Assert.Contains("Property=\"Opacity\"", styleBlock, StringComparison.Ordinal);
    }

    // Issue #1029 --------------------------------------------------------------------------------

    private const string EntityCardTreeTemplateSelector =
        "TreeView.entity-card-tree TreeViewItem, TreeView.entity-card-tree-view TreeViewItem";

    private static string ReadSharedStylesText()
    {
        var repositoryRoot = FindRepositoryRoot();
        var stylesPath = Path.Combine(repositoryRoot.FullName, "Phantom.Workspaces.Gui.Shared", "Styles", "SharedStyles.axaml");
        return File.ReadAllText(stylesPath);
    }

    private static string ReadEntityCardControlText()
    {
        var repositoryRoot = FindRepositoryRoot();
        var cardPath = Path.Combine(repositoryRoot.FullName, "Phantom.Workspaces", "Controls", "EntityCardControl.axaml");
        return File.ReadAllText(cardPath);
    }

    [Fact]
    public void EntityCardTreeViewStyle_Exists_TargetsNamedTreeView()
    {
        var styles = ReadSharedStylesText();
        Assert.Contains("<Style Selector=\"TreeView.entity-card-tree-view\">", styles, StringComparison.Ordinal);
        Assert.Contains("TreeView.entity-card-tree-view TreeViewItem", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeViewStyle_ItemTemplate_UsesTwoByTwoGrid()
    {
        var styles = ReadSharedStylesText();
        var template = ExtractStyle(styles, EntityCardTreeTemplateSelector);
        Assert.Contains("RowDefinitions=\"Auto,*\"", template, StringComparison.Ordinal);
        Assert.Contains("ColumnDefinitions=\"20,*\"", template, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeViewStyle_WhenHasChildren_BorderBottomCornersAreSquare()
    {
        var styles = ReadSharedStylesText();
        var baseBorder = ExtractStyle(styles, "Border.entity-card-shell-border");
        Assert.Contains("<Setter Property=\"CornerRadius\" Value=\"6\" />", baseBorder, StringComparison.Ordinal);

        var hasChildrenBorder = ExtractStyle(styles, "Border.entity-card-shell-border.has-children");
        Assert.Contains("<Setter Property=\"CornerRadius\" Value=\"6,6,0,0\" />", hasChildrenBorder, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeViewStyle_ExpanderButton_IsBottomRoundedTopSquare()
    {
        var styles = ReadSharedStylesText();
        var footer = ExtractStyle(styles, "Button.entity-card-shell-footer");
        Assert.Contains("<Setter Property=\"CornerRadius\" Value=\"0,0,6,6\" />", footer, StringComparison.Ordinal);

        // The footer expander is only shown when the item has children.
        Assert.Contains(
            "IsVisible=\"{Binding HasChildren, FallbackValue=False}\"",
            styles,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeViewStyle_BorderThickness_MatchesInputTextBox()
    {
        var styles = ReadSharedStylesText();
        var borderThicknessSetter = "<Setter Property=\"BorderThickness\" Value=\"{DynamicResource TextControlBorderThemeThickness}\" />";

        var border = ExtractStyle(styles, "Border.entity-card-shell-border");
        Assert.Contains(borderThicknessSetter, border, StringComparison.Ordinal);

        var footer = ExtractStyle(styles, "Button.entity-card-shell-footer");
        Assert.Contains(borderThicknessSetter, footer, StringComparison.Ordinal);

        // The agent-chat input TextBox must not set its own BorderThickness so it resolves to the
        // same FluentTheme TextControlBorderThemeThickness the shell reuses.
        var repositoryRoot = FindRepositoryRoot();
        var composerPath = Path.Combine(repositoryRoot.FullName, "Phantom.Workspaces.Agent.Gui", "Controls", "QueueComposerControl.axaml");
        var composer = File.ReadAllText(composerPath);
        var inputStart = composer.IndexOf("x:Name=\"InputBox\"", StringComparison.Ordinal);
        Assert.True(inputStart >= 0);
        var inputEnd = composer.IndexOf("/>", inputStart, StringComparison.Ordinal);
        var inputBox = composer[inputStart..inputEnd];
        Assert.DoesNotContain("BorderThickness", inputBox, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeViewStyle_ChildRail_IsTwoPixelsWithChildRailBrush()
    {
        var styles = ReadSharedStylesText();
        var template = ExtractStyle(styles, EntityCardTreeTemplateSelector);
        Assert.Contains("Background=\"{Binding ChildRailBrush, FallbackValue=#808080}\"", template, StringComparison.Ordinal);
        Assert.Contains("Width=\"2\"", template, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Center\"", template, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeViewStyle_ItemsPresenter_InSecondRowSecondColumn()
    {
        var styles = ReadSharedStylesText();
        var template = ExtractStyle(styles, EntityCardTreeTemplateSelector);
        Assert.Contains("<ItemsPresenter Grid.Column=\"1\" Grid.Row=\"1\"", template, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsExpanded}\" />", template, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeViewStyle_TreeViewBackground_IsEntityPaneBackground()
    {
        var styles = ReadSharedStylesText();
        var treeViewStyle = ExtractStyle(styles, "TreeView.entity-card-tree-view");
        Assert.Contains(
            "<Setter Property=\"Background\" Value=\"{DynamicResource Theme.Surface.EntityPane.Background}\" />",
            treeViewStyle,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTree_Class_SetsAutoHScrollAndItemsPanelWrapper()
    {
        // Issue #1064: the consolidated TreeView.entity-card-tree style keeps H=Auto (so a
        // horizontal scrollbar remains available below the minimum) and provides a single
        // items-region wrapper instead of leaving the inner scroller at Avalonia's default.
        var styles = ReadSharedStylesText();
        var treeViewStyle = ExtractStyle(styles, "TreeView.entity-card-tree");
        Assert.Contains(
            "<Setter Property=\"ScrollViewer.HorizontalScrollBarVisibility\" Value=\"Auto\" />",
            treeViewStyle,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"ScrollViewer.VerticalScrollBarVisibility\" Value=\"Auto\" />",
            treeViewStyle,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"ScrollViewer.AllowAutoHide\" Value=\"False\" />",
            treeViewStyle,
            StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"ItemsPanel\">", treeViewStyle, StringComparison.Ordinal);
        Assert.Contains("<ItemsPanelTemplate>", treeViewStyle, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTree_ItemsWrapper_HasMinWidthAndViewportMaxWidth()
    {
        // Issue #1064: two-regime wrapper — floors at MinWidth="160" (below-min scroll) and
        // caps at MaxWidth bound to the inner scroller viewport (wrap + no overflow when wide).
        var styles = ReadSharedStylesText();
        var treeViewStyle = ExtractStyle(styles, "TreeView.entity-card-tree");
        Assert.Contains("MinWidth=\"160\"", treeViewStyle, StringComparison.Ordinal);
        Assert.Contains(
            "MaxWidth=\"{Binding $parent[ScrollViewer].Viewport.Width}\"",
            treeViewStyle,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeView_StillMatchesEntityCardTreeWrapper()
    {
        // Issue #1064 regression guard: #1049's entity-card-tree-view keeps its own equivalent
        // two-regime wrapper so consolidating #1064 does not regress the sibling style.
        var styles = ReadSharedStylesText();
        var treeViewStyle = ExtractStyle(styles, "TreeView.entity-card-tree-view");
        Assert.Contains("MinWidth=\"160\"", treeViewStyle, StringComparison.Ordinal);
        Assert.Contains(
            "MaxWidth=\"{Binding $parent[ScrollViewer].Viewport.Width}\"",
            treeViewStyle,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceFieldRow_LabelColumn_IsNotFixedTwoHundredWide()
    {
        // Issue #1064 secondary: the field-row grids no longer hard-code a fixed 200px label
        // column, so at the narrow regime the value column keeps room and the path wraps.
        var repositoryRoot = FindRepositoryRoot();
        var dataTemplatesPath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces",
            "Templates",
            "WorkspaceDataTemplates.axaml");
        var dataTemplates = File.ReadAllText(dataTemplatesPath);
        Assert.DoesNotContain("ColumnDefinitions=\"200,*\"", dataTemplates, StringComparison.Ordinal);
        Assert.DoesNotContain("ColumnDefinitions=\"200,*,Auto\"", dataTemplates, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeViewStyle_HorizontalScrollBar_OnlyWhenMinWidthHit_AndNotOverlapping()
    {
        var styles = ReadSharedStylesText();
        var treeViewStyle = ExtractStyle(styles, "TreeView.entity-card-tree-view");
        Assert.Contains(
            "<Setter Property=\"ScrollViewer.HorizontalScrollBarVisibility\" Value=\"Auto\" />",
            treeViewStyle,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"ScrollViewer.AllowAutoHide\" Value=\"False\" />",
            treeViewStyle,
            StringComparison.Ordinal);

        // A minimum width on the item gates when the horizontal scrollbar appears.
        var template = ExtractStyle(styles, EntityCardTreeTemplateSelector);
        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"160\" />", template, StringComparison.Ordinal);

        // Issue #1049: the ItemsPanel wrapper carries MinWidth="160" and MaxWidth bound to viewport.
        Assert.Contains("MinWidth=\"160\"", treeViewStyle, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"{Binding $parent[ScrollViewer].Viewport.Width}\"", treeViewStyle, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeViewStyle_VerticalScrollBar_DoesNotOverlapContent()
    {
        var styles = ReadSharedStylesText();
        var treeViewStyle = ExtractStyle(styles, "TreeView.entity-card-tree-view");
        Assert.Contains(
            "<Setter Property=\"ScrollViewer.VerticalScrollBarVisibility\" Value=\"Auto\" />",
            treeViewStyle,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"ScrollViewer.AllowAutoHide\" Value=\"False\" />",
            treeViewStyle,
            StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardShellTemplate_AppliedToContentControl_RendersSameBorder()
    {
        var sharedStyles = LoadSharedStyles();

        var content = new ContentControl { Content = new TextBlock { Text = "standalone" } };
        content.Classes.Add("entity-card-shell");

        var host = new StackPanel();
        host.Styles.Add(sharedStyles);
        host.Children.Add(content);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        var shellBorder = content.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(static border => border.Classes.Contains("entity-card-shell-border"));

        Assert.NotNull(shellBorder);
        Assert.Equal(new CornerRadius(6), shellBorder!.CornerRadius);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardShell_AppliedToSingleEntityHost_ProducesSameBorderAsTreeCard()
    {
        // Issue #1066: the single-entity host carries the same entity-card-shell class as the tree
        // card, so it renders the identical rounded shell border.
        var sharedStyles = LoadSharedStyles();

        var content = new ContentControl { Content = new TextBlock { Text = "single" } };
        content.Classes.Add("entity-card-shell");
        content.Classes.Add("entity-card-single-host");

        var host = new StackPanel();
        host.Styles.Add(sharedStyles);
        host.Children.Add(content);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        var shellBorder = content.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(static border => border.Classes.Contains("entity-card-shell-border"));

        Assert.NotNull(shellBorder);
        Assert.Equal(new CornerRadius(6), shellBorder!.CornerRadius);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardShell_WhenHasChildren_SingleEntityFooterIsVisible()
    {
        // Issue #1066: the shell footer expand button visibility binds to HasChildren for the
        // single-entity host, matching tree behaviour.
        var sharedStyles = LoadSharedStyles();

        var content = new ContentControl
        {
            Content = new TextBlock { Text = "single" },
            DataContext = new SingleEntityShellModel { HasChildren = true },
        };
        content.Classes.Add("entity-card-shell");
        content.Classes.Add("entity-card-single-host");

        var host = new StackPanel();
        host.Styles.Add(sharedStyles);
        host.Children.Add(content);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        var footer = content.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(static button => button.Classes.Contains("entity-card-shell-footer"));

        Assert.NotNull(footer);
        Assert.True(footer!.IsVisible);
    }

    [Fact]
    public void SingleEntityView_LongContent_WrapsToCappedWidth()
    {
        // Issue #1066 (regime 1): the single-entity host caps to the ScrollViewer viewport width
        // (and ~1/3 of the pane) so long content wraps rather than overflowing when wide.
        var dataTemplates = ReadWorkspaceDataTemplatesText();
        var template = ExtractSingleEntityTemplate(dataTemplates);
        Assert.Contains("$parent[ScrollViewer].Viewport.Width", template, StringComparison.Ordinal);
        Assert.Contains("SingleEntityMaxWidthConverter", template, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleEntityView_BelowMinimumWidth_ShowsHorizontalScrollbar()
    {
        // Issue #1066 (regime 2): the host floors at MinWidth="160" inside an H=Auto ScrollViewer,
        // so below the minimum the extent exceeds the viewport and a horizontal scrollbar appears.
        var dataTemplates = ReadWorkspaceDataTemplatesText();
        var template = ExtractSingleEntityTemplate(dataTemplates);
        Assert.Contains("MinWidth=\"160\"", template, StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollBarVisibility=\"Auto\"", template, StringComparison.Ordinal);
    }

    private sealed class SingleEntityShellModel
    {
        public bool HasChildren { get; init; }

        public string ExpandArrow => "\u25BC";

        public System.Windows.Input.ICommand? ToggleExpandCommand => null;
    }

    private static string ReadWorkspaceDataTemplatesText()
    {
        var repositoryRoot = FindRepositoryRoot();
        var path = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces",
            "Templates",
            "WorkspaceDataTemplates.axaml");
        return File.ReadAllText(path);
    }

    private static string ExtractSingleEntityTemplate(string dataTemplatesContent)
    {
        var start = dataTemplatesContent.IndexOf(
            "<DataTemplate DataType=\"vm:EntityWorkspaceTabViewModel\">",
            StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected the EntityWorkspaceTabViewModel DataTemplate to exist.");
        var end = dataTemplatesContent.IndexOf("</DataTemplate>", start, StringComparison.Ordinal);
        Assert.True(end > start, "Expected the EntityWorkspaceTabViewModel DataTemplate to be closed.");
        return dataTemplatesContent[start..(end + "</DataTemplate>".Length)];
    }

    [Fact]
    public void EntityCardControl_HasNoOuterBorderOrBackground()
    {
        var card = ReadEntityCardControlText();
        Assert.DoesNotContain("<Border Classes=\"entity-card\"", card, StringComparison.Ordinal);
        Assert.DoesNotContain("Classes=\"workspace-entity-node-root\"", card, StringComparison.Ordinal);
        Assert.Contains("<StackPanel Classes=\"workspace-entity-card-content\"", card, StringComparison.Ordinal);
        Assert.Contains("Tapped=\"OnEntityCardTapped\"", card, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceFieldColumns_MinWidth_IsSixtySeven()
    {
        // Issue #1045: property-name and property-value column min-widths drop to 2/3 (100 -> 67).
        var styles = ReadSharedStylesText();
        var label = ExtractStyle(styles, ":is(TextBlock).workspace-field-label");
        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"67\" />", label, StringComparison.Ordinal);
        var value = ExtractStyle(styles, "TextBox.workspace-field-value");
        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"67\" />", value, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardControl_HeaderAndActionsRow_MinWidthIsHundred()
    {
        // Issue #1213: header wrap layout restores the 100px min-width floor on both the
        // display-name column and the actions row so they reflow together as a unit.
        var card = ReadEntityCardControlText();

        var headerStart = card.IndexOf("Classes=\"workspace-entity-header-row\"", StringComparison.Ordinal);
        Assert.True(headerStart >= 0);
        var headerEnd = card.IndexOf(">", headerStart, StringComparison.Ordinal);
        var header = card[headerStart..headerEnd];
        Assert.Contains("MinWidth=\"100\"", header, StringComparison.Ordinal);

        var actionsStart = card.IndexOf("Classes=\"workspace-entity-actions-row\"", StringComparison.Ordinal);
        Assert.True(actionsStart >= 0, "Actions row must be a WrapPanel so action buttons wrap.");
        var actionsEnd = card.IndexOf(">", actionsStart, StringComparison.Ordinal);
        var actions = card[actionsStart..actionsEnd];
        Assert.Contains("MinWidth=\"100\"", actions, StringComparison.Ordinal);

        var styles = ReadSharedStylesText();
        Assert.Contains("<Style Selector=\"WrapPanel.workspace-entity-actions-row\">", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardHeaderWrapPanel_IsWrapPanel_NotThreeColumnGrid()
    {
        // Issue #1213: the header container must be a wrap-capable layout so the display-name
        // block and the actions row reflow together, not a fixed 3-column Grid.
        var card = ReadEntityCardControlText();
        Assert.DoesNotContain("ColumnDefinitions=\"Auto,*,Auto\"", card, StringComparison.Ordinal);
        Assert.Contains("Classes=\"workspace-entity-header-wrap\"", card, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardHeaderRow_HasMinWidthHundred_MatchingActionsRow()
    {
        // Issue #1213: the display-name column min-width floor (100) matches the actions row.
        var styles = ReadSharedStylesText();
        var headerRow = ExtractStyle(styles, "StackPanel.workspace-entity-header-row");
        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"100\" />", headerRow, StringComparison.Ordinal);
        var actionsRow = ExtractStyle(styles, "WrapPanel.workspace-entity-actions-row");
        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"100\" />", actionsRow, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardHeaderWrapPanel_StyleExists_IsHorizontalStretch()
    {
        // Issue #1213: the header wrap panel is a horizontal, stretched wrap layout.
        var styles = ReadSharedStylesText();
        var wrap = ExtractStyle(styles, "WrapPanel.workspace-entity-header-wrap");
        Assert.Contains("<Setter Property=\"Orientation\" Value=\"Horizontal\" />", wrap, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"HorizontalAlignment\" Value=\"Stretch\" />", wrap, StringComparison.Ordinal);
    }

    // Issue #1213 — behavioural/rendering coverage for the header wrap layout. These render the
    // shipped header styles (extracted verbatim from SharedStyles.axaml) around a header structure
    // that mirrors EntityCardControl.axaml, then run layout at narrow/wide widths and assert the
    // actual reflow behaviour, not merely the static style declarations.

    private static readonly string[] HeaderWrapStyleSelectors =
    {
        "WrapPanel.workspace-entity-header-wrap",
        "StackPanel.workspace-entity-header-row",
        "StackPanel.workspace-entity-header-row > :is(TextBlock)",
        ":is(TextBlock).workspace-entity-title",
        "WrapPanel.workspace-entity-actions-row",
    };

    private const string HeaderCardTitleText = "worktree, system-defined entity display name";

    private static (Window Window, WrapPanel HeaderWrap, StackPanel HeaderRow, TextBlock Title, WrapPanel ActionsRow)
        LayoutEntityCardHeader(double width, double height, string title = HeaderCardTitleText)
    {
        var styles = ReadSharedStylesText();
        var injected = string.Concat(HeaderWrapStyleSelectors.Select(s => ExtractStyle(styles, s)));
        var xaml = $$"""
            <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Window.Styles>
                {{injected}}
              </Window.Styles>
              <WrapPanel Name="HeaderWrap" Classes="workspace-entity-header-wrap"
                         Orientation="Horizontal" HorizontalAlignment="Stretch" VerticalAlignment="Top">
                <StackPanel Name="HeaderRow" Classes="workspace-entity-header-row" MinWidth="100" Margin="0,0,12,0">
                  <TextBlock Name="Title" Classes="workspace-entity-title" Text="{{title}}" />
                </StackPanel>
                <WrapPanel Name="ActionsRow" Classes="workspace-entity-actions-row" MinWidth="100">
                  <Border Width="90" Height="24" />
                </WrapPanel>
              </WrapPanel>
            </Window>
            """;
        var window = (Window)AvaloniaRuntimeXamlLoader.Load(xaml);
        window.SizeToContent = SizeToContent.Manual;
        window.Width = width;
        window.Height = height;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var headerWrap = window.GetVisualDescendants().OfType<WrapPanel>().First(p => p.Name == "HeaderWrap");
        var headerRow = window.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "HeaderRow");
        var titleBlock = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "Title");
        var actionsRow = window.GetVisualDescendants().OfType<WrapPanel>().First(p => p.Name == "ActionsRow");
        return (window, headerWrap, headerRow, titleBlock, actionsRow);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardHeader_WhenNarrow_ActionsRowWrapsBelowDisplayName()
    {
        // When the viewport is narrower than display-name + actions on one line, the actions row
        // moves to a new row of the header wrap panel (not squeezed beside a starved text column).
        var (window, _, headerRow, _, actionsRow) = LayoutEntityCardHeader(width: 180, height: 400);
        try
        {
            Assert.True(
                actionsRow.Bounds.Y >= headerRow.Bounds.Bottom - 1,
                $"Actions row (Y={actionsRow.Bounds.Y}) should wrap below the header row " +
                $"(bottom={headerRow.Bounds.Bottom}).");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardHeader_WhenNarrow_DisplayNameWrapsOnWordBoundaries()
    {
        // The title TextBlock breaks at whitespace, not mid-word, when the header wraps.
        var (window, _, _, title, _) = LayoutEntityCardHeader(width: 180, height: 400);
        try
        {
            Assert.Equal(Avalonia.Media.TextWrapping.Wrap, title.TextWrapping);

            var lines = title.TextLayout.TextLines;
            Assert.True(lines.Count >= 2, $"Expected the title to wrap; got {lines.Count} line(s).");

            // Every internal line break must occur at a whitespace boundary — no word is split.
            var text = HeaderCardTitleText;
            var position = 0;
            for (var i = 0; i < lines.Count - 1; i++)
            {
                position += lines[i].Length;
                Assert.True(position > 0 && position <= text.Length);
                // A wrap that splits a word breaks between two alphanumeric characters. Breaks at
                // whitespace, commas, or hyphens ("system-defined" → "system-" / "defined") are
                // legitimate word-boundary wraps, not the character-clipping bug.
                var splitsWord =
                    position < text.Length &&
                    char.IsLetterOrDigit(text[position - 1]) &&
                    char.IsLetterOrDigit(text[position]);
                Assert.False(
                    splitsWord,
                    $"Line break at index {position} splits a word in \"{text}\".");
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardHeader_WhenWide_DisplayNameAndActionsShareOneRow()
    {
        // With ample width the header remains a single row: display-name StackPanel and actions
        // WrapPanel are laid out side-by-side.
        var (window, _, headerRow, _, actionsRow) = LayoutEntityCardHeader(width: 1400, height: 400);
        try
        {
            Assert.True(
                Math.Abs(actionsRow.Bounds.Y - headerRow.Bounds.Y) < 5,
                $"Header row (Y={headerRow.Bounds.Y}) and actions row (Y={actionsRow.Bounds.Y}) " +
                "should share one row when wide.");
            Assert.True(
                actionsRow.Bounds.X >= headerRow.Bounds.Right - 1,
                $"Actions row (X={actionsRow.Bounds.X}) should sit to the right of the header row " +
                $"(right={headerRow.Bounds.Right}).");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardTitleTextBlock_TextWrapping_IsWrap()
    {
        // The header-row title TextBlock resolves TextWrapping=Wrap (word-level wrapping), not
        // NoWrap clipping.
        var (window, _, _, title, _) = LayoutEntityCardHeader(width: 400, height: 400);
        try
        {
            Assert.Equal(Avalonia.Media.TextWrapping.Wrap, title.TextWrapping);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void EntityCardTreeViewStyle_ItemMinWidth_IsTwoThirdsOfPrior()
    {
        // Issue #1045: the tree item MinWidth drops to 2/3 (240 -> 160).
        var styles = ReadSharedStylesText();
        var template = ExtractStyle(styles, EntityCardTreeTemplateSelector);
        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"160\" />", template, StringComparison.Ordinal);
        Assert.DoesNotContain("<Setter Property=\"MinWidth\" Value=\"240\" />", template, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeViewStyle_IndentColumn_IsFixedTwentyPixels()
    {
        // Issue #1045: the indent gutter is a fixed 20px column (was Auto) with the rail centred.
        var styles = ReadSharedStylesText();
        var template = ExtractStyle(styles, EntityCardTreeTemplateSelector);
        Assert.Contains("ColumnDefinitions=\"20,*\"", template, StringComparison.Ordinal);
        Assert.DoesNotContain("ColumnDefinitions=\"Auto,*\"", template, StringComparison.Ordinal);
        Assert.Contains("Width=\"2\"", template, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Center\"", template, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeViewStyle_Sticky_AppliesToWholeTreeViewItem()
    {
        // Issue #1045: sticky must cover the whole item (border + footer), so AutoRowLevel moves
        // from the inner ContentPresenter.branch-header to the item content root StackPanel.
        var styles = ReadSharedStylesText();

        Assert.Contains(
            "<Style Selector=\"TreeView.entity-card-tree-view.entity-card-tree-sticky StackPanel.entity-card-tree-item\">",
            styles,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Style Selector=\"TreeView.entity-card-tree.entity-card-tree-sticky StackPanel.entity-card-tree-item\">",
            styles,
            StringComparison.Ordinal);

        var stickyItemStyle = ExtractStyle(
            styles,
            "TreeView.entity-card-tree-view.entity-card-tree-sticky StackPanel.entity-card-tree-item");
        Assert.Contains(
            "<Setter Property=\"controls:TreeSticky.AutoRowLevel\" Value=\"True\"/>",
            stickyItemStyle,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "entity-card-tree-sticky ContentPresenter.branch-header",
            styles,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeViewStyle_PointerOver_RecoloursBothBorderAndFooter()
    {
        // Issue #1048: hover is now scoped to the StackPanel.entity-card-tree-item:pointerover,
        // not TreeViewItem:pointerover. Both border and footer are still recoloured (preserves #1045).
        var styles = ReadSharedStylesText();

        var borderRule = ExtractStyle(
            styles,
            "TreeView.entity-card-tree-view StackPanel.entity-card-tree-item:pointerover Border.entity-card-shell-border");
        Assert.Contains(
            "<Setter Property=\"BorderBrush\" Value=\"{DynamicResource Theme.Surface.EntityCard.ActiveBorder}\" />",
            borderRule,
            StringComparison.Ordinal);

        var footerRule = ExtractStyle(
            styles,
            "TreeView.entity-card-tree-view StackPanel.entity-card-tree-item:pointerover Button.entity-card-shell-footer");
        Assert.Contains(
            "<Setter Property=\"BorderBrush\" Value=\"{DynamicResource Theme.Surface.EntityCard.ActiveBorder}\" />",
            footerRule,
            StringComparison.Ordinal);

        // The per-element pointerover rules that caused the seam must be gone.
        Assert.DoesNotContain(
            "<Style Selector=\"Border.entity-card-shell-border:pointerover\">",
            styles,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<Style Selector=\"Button.entity-card-shell-footer:pointerover\">",
            styles,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeViewStyle_Selected_RecoloursBothBorderAndFooter()
    {
        // Issue #1048: selection is now scoped to the StackPanel.entity-card-tree-item.selected class,
        // not TreeViewItem:selected. Both border and footer are still recoloured (preserves #1045).
        var styles = ReadSharedStylesText();

        var borderRule = ExtractStyle(
            styles,
            "TreeView.entity-card-tree-view StackPanel.entity-card-tree-item.selected Border.entity-card-shell-border");
        Assert.Contains(
            "<Setter Property=\"BorderBrush\" Value=\"{DynamicResource Theme.Surface.EntityCard.SelectedBorder}\" />",
            borderRule,
            StringComparison.Ordinal);

        var footerRule = ExtractStyle(
            styles,
            "TreeView.entity-card-tree-view StackPanel.entity-card-tree-item.selected Button.entity-card-shell-footer");
        Assert.Contains(
            "<Setter Property=\"BorderBrush\" Value=\"{DynamicResource Theme.Surface.EntityCard.SelectedBorder}\" />",
            footerRule,
            StringComparison.Ordinal);
    }

    // Issue #1048 — new tests -----------------------------------------------------------------------

    [Fact]
    public void EntityCardTreeViewStyle_PointerOverHighlight_ScopedToOwnHeaderStackPanel()
    {
        // The blue ActiveBorder recolour selectors are keyed off StackPanel.entity-card-tree-item:pointerover
        // (not TreeViewItem:pointerover), so hover cannot propagate to ancestor/descendant items.
        var styles = ReadSharedStylesText();

        var borderRule = ExtractStyle(
            styles,
            "TreeView.entity-card-tree StackPanel.entity-card-tree-item:pointerover Border.entity-card-shell-border");
        Assert.Contains(
            "<Setter Property=\"BorderBrush\" Value=\"{DynamicResource Theme.Surface.EntityCard.ActiveBorder}\" />",
            borderRule,
            StringComparison.Ordinal);

        var treeViewBorderRule = ExtractStyle(
            styles,
            "TreeView.entity-card-tree-view StackPanel.entity-card-tree-item:pointerover Border.entity-card-shell-border");
        Assert.Contains(
            "<Setter Property=\"BorderBrush\" Value=\"{DynamicResource Theme.Surface.EntityCard.ActiveBorder}\" />",
            treeViewBorderRule,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeViewStyle_PointerOver_NoTreeViewItemPointerOverDescendantSelector()
    {
        // The old TreeViewItem:pointerover descendant selectors must be gone to prevent regression.
        var styles = ReadSharedStylesText();

        Assert.DoesNotContain(
            "TreeViewItem:pointerover Border.entity-card-shell-border",
            styles,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TreeViewItem:pointerover Button.entity-card-shell-footer",
            styles,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeViewStyle_PointerOver_StillRecoloursBothBorderAndFooter()
    {
        // Preserves #1045: both the shell border AND the footer are recoloured to ActiveBorder
        // under the new scoped selector (hovering header or footer recolours both).
        var styles = ReadSharedStylesText();

        // entity-card-tree variant
        _ = ExtractStyle(
            styles,
            "TreeView.entity-card-tree StackPanel.entity-card-tree-item:pointerover Border.entity-card-shell-border");
        _ = ExtractStyle(
            styles,
            "TreeView.entity-card-tree StackPanel.entity-card-tree-item:pointerover Button.entity-card-shell-footer");

        // entity-card-tree-view variant
        _ = ExtractStyle(
            styles,
            "TreeView.entity-card-tree-view StackPanel.entity-card-tree-item:pointerover Border.entity-card-shell-border");
        _ = ExtractStyle(
            styles,
            "TreeView.entity-card-tree-view StackPanel.entity-card-tree-item:pointerover Button.entity-card-shell-footer");
    }

    [Fact]
    public void EntityCardTreeViewStyle_SelectedHighlight_ScopedToOwnHeaderStackPanelClass()
    {
        // The gold SelectedBorder recolour is keyed off StackPanel.entity-card-tree-item.selected
        // and the template binds Classes.selected to the container IsSelected.
        var styles = ReadSharedStylesText();

        // Template must bind Classes.selected
        var template = ExtractStyle(styles, EntityCardTreeTemplateSelector);
        Assert.Contains("Classes.selected=", template, StringComparison.Ordinal);

        // Selection selectors for both tree class variants
        var borderRule = ExtractStyle(
            styles,
            "TreeView.entity-card-tree StackPanel.entity-card-tree-item.selected Border.entity-card-shell-border");
        Assert.Contains(
            "<Setter Property=\"BorderBrush\" Value=\"{DynamicResource Theme.Surface.EntityCard.SelectedBorder}\" />",
            borderRule,
            StringComparison.Ordinal);

        var treeViewBorderRule = ExtractStyle(
            styles,
            "TreeView.entity-card-tree-view StackPanel.entity-card-tree-item.selected Border.entity-card-shell-border");
        Assert.Contains(
            "<Setter Property=\"BorderBrush\" Value=\"{DynamicResource Theme.Surface.EntityCard.SelectedBorder}\" />",
            treeViewBorderRule,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeViewStyle_Selected_NoTreeViewItemSelectedDescendantSelector()
    {
        // The old TreeViewItem:selected descendant selectors must be gone.
        var styles = ReadSharedStylesText();

        Assert.DoesNotContain(
            "TreeViewItem:selected Border.entity-card-shell-border",
            styles,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TreeViewItem:selected Button.entity-card-shell-footer",
            styles,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeView_HoverChildItem_DoesNotHighlightParentBorder()
    {
        // Selector-scope proxy: because the hover selector is keyed off
        // StackPanel.entity-card-tree-item:pointerover (not TreeViewItem:pointerover),
        // a child's header hover cannot propagate to the parent's StackPanel — the parent's
        // StackPanel is a sibling of the ItemsPresenter containing the child, not an ancestor.
        var styles = ReadSharedStylesText();

        // The selector uses StackPanel.entity-card-tree-item:pointerover, not TreeViewItem:pointerover.
        Assert.DoesNotContain(
            "TreeViewItem:pointerover Border.entity-card-shell-border",
            styles,
            StringComparison.Ordinal);

        // And the template places ItemsPresenter as a sibling of the StackPanel, not a descendant.
        var template = ExtractStyle(styles, EntityCardTreeTemplateSelector);
        Assert.Contains("StackPanel Grid.Row=\"0\"", template, StringComparison.Ordinal);
        Assert.Contains("ItemsPresenter Grid.Column=\"1\" Grid.Row=\"1\"", template, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeView_HoverParentHeader_DoesNotHighlightChildBorders()
    {
        // Selector-scope proxy: the hover selector descends from StackPanel.entity-card-tree-item:pointerover,
        // not TreeViewItem:pointerover. The StackPanel is in grid row 0, and children are in
        // the row-1 ItemsPresenter (a sibling), so the descendant combinator cannot reach child borders.
        var styles = ReadSharedStylesText();

        Assert.DoesNotContain(
            "TreeViewItem:pointerover Border.entity-card-shell-border",
            styles,
            StringComparison.Ordinal);

        var template = ExtractStyle(styles, EntityCardTreeTemplateSelector);
        Assert.Contains("ItemsPresenter Grid.Column=\"1\" Grid.Row=\"1\"", template, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeView_SelectParent_DoesNotHighlightChildBorders()
    {
        // Selector-scope proxy: the selection selector uses StackPanel.entity-card-tree-item.selected,
        // not TreeViewItem:selected. Classes.selected is bound to the container's IsSelected via
        // the template, so only THIS item's StackPanel gets the .selected class — child StackPanels
        // in the row-1 ItemsPresenter do not inherit it.
        var styles = ReadSharedStylesText();

        Assert.DoesNotContain(
            "TreeViewItem:selected Border.entity-card-shell-border",
            styles,
            StringComparison.Ordinal);

        var template = ExtractStyle(styles, EntityCardTreeTemplateSelector);
        Assert.Contains("Classes.selected=", template, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeViewStyle_HorizontalScrollBar_ConfiguredOnceOnStyle()
    {
        // Issue #1045: the ScrollViewer configuration lives only on the shared style; the consumer
        // (AgentChatEditorControl NavigationTree) must not duplicate it.
        var styles = ReadSharedStylesText();
        var treeViewStyle = ExtractStyle(styles, "TreeView.entity-card-tree-view");
        Assert.Contains(
            "<Setter Property=\"ScrollViewer.HorizontalScrollBarVisibility\" Value=\"Auto\" />",
            treeViewStyle,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"ScrollViewer.VerticalScrollBarVisibility\" Value=\"Auto\" />",
            treeViewStyle,
            StringComparison.Ordinal);

        // Issue #1049: the ItemsPanel wrapper with MinWidth + viewport-MaxWidth is also in the shared style.
        Assert.Contains("MinWidth=\"160\"", treeViewStyle, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"{Binding $parent[ScrollViewer].Viewport.Width}\"", treeViewStyle, StringComparison.Ordinal);

        var repositoryRoot = FindRepositoryRoot();
        var editorPath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Agent.Gui",
            "Controls",
            "AgentChatEditorControl.axaml");
        var editor = File.ReadAllText(editorPath);
        var treeStart = editor.IndexOf("x:Name=\"NavigationTree\"", StringComparison.Ordinal);
        Assert.True(treeStart >= 0);
        var treeEnd = editor.IndexOf(">", treeStart, StringComparison.Ordinal);
        var navigationTree = editor[treeStart..treeEnd];
        Assert.DoesNotContain("ScrollViewer.HorizontalScrollBarVisibility", navigationTree, StringComparison.Ordinal);
        Assert.DoesNotContain("ScrollViewer.VerticalScrollBarVisibility", navigationTree, StringComparison.Ordinal);
        Assert.DoesNotContain("ScrollViewer.AllowAutoHide", navigationTree, StringComparison.Ordinal);
    }

    // Issue #1049 — new tests -----------------------------------------------------------------------

    [Fact]
    public void EntityCardTreeView_ItemsWrapper_MaxWidthBoundToViewport()
    {
        // The entity-card-tree-view ItemsPanel override declares MinWidth="160" and
        // MaxWidth bound to the tree's ScrollViewer viewport width, with HorizontalScrollBarVisibility="Auto" retained.
        var styles = ReadSharedStylesText();
        var treeViewStyle = ExtractStyle(styles, "TreeView.entity-card-tree-view");

        Assert.Contains("<Setter Property=\"ItemsPanel\">", treeViewStyle, StringComparison.Ordinal);
        Assert.Contains("<ItemsPanelTemplate>", treeViewStyle, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"160\"", treeViewStyle, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"{Binding $parent[ScrollViewer].Viewport.Width}\"", treeViewStyle, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", treeViewStyle, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"ScrollViewer.HorizontalScrollBarVisibility\" Value=\"Auto\" />", treeViewStyle, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceMarkdownFieldRow_LabelColumn_IsNotFixedTwoHundred()
    {
        // Issue #1049: the note/markdown field-row grids must not hard-code ColumnDefinitions="200,*".
        var repositoryRoot = FindRepositoryRoot();
        var templatePath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces",
            "Templates",
            "WorkspaceDataTemplates.axaml");
        var content = File.ReadAllText(templatePath);

        // Find all workspace-field-row grids inside note/markdown DataTemplates
        // (JsonSchemaFieldEditorViewModel, PlainMimeAttachmentFieldEditorViewModel,
        //  MarkdownMimeAttachmentFieldEditorViewModel). None should use 200,*.
        // The "200,*" column definition was the root of the oversized label floor.
        var fieldRowMatches = System.Text.RegularExpressions.Regex.Matches(
            content,
            @"workspace-markdown-viewer|workspace-markdown-content|workspace-markdown-editor|workspace-json-schema");

        Assert.NotEmpty(fieldRowMatches);

        // No grid in the file should still use ColumnDefinitions="200,*" after the fix
        // for the markdown/note templates — verify the total count dropped.
        var remaining200 = System.Text.RegularExpressions.Regex.Matches(
            content,
            @"ColumnDefinitions=""200,\*""");

        // The note/markdown grids (9 occurrences) should all be converted. The non-markdown grids
        // (EntityReference, EntityList, String, BooleanToggle) may still use 200,*.
        // Assert that none of the remaining 200,* grids are inside markdown/note templates.
        foreach (System.Text.RegularExpressions.Match match in remaining200)
        {
            // Check that no workspace-markdown-viewer/editor/json-schema appears within 500 chars
            // after this ColumnDefinitions (i.e. it's not a markdown grid).
            var lookAhead = content.Substring(match.Index, Math.Min(500, content.Length - match.Index));
            Assert.DoesNotContain("workspace-markdown-viewer", lookAhead, StringComparison.Ordinal);
            Assert.DoesNotContain("workspace-json-schema", lookAhead, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EntityCardTreeView_ViewportWiderThanMin_ContentFillsAndWraps_NoHScroll()
    {
        // Style/attached-property assertion proxy: when the viewport is wider than MinWidth (160),
        // the ItemsPanel wrapper's MaxWidth == Viewport.Width caps content to the viewport,
        // enabling wrap and fill. The HorizontalScrollBarVisibility="Auto" means no scrollbar
        // appears because extent == viewport.
        var styles = ReadSharedStylesText();
        var treeViewStyle = ExtractStyle(styles, "TreeView.entity-card-tree-view");

        // MaxWidth bound to viewport ensures content is capped at viewport width (finite measure).
        Assert.Contains("MaxWidth=\"{Binding $parent[ScrollViewer].Viewport.Width}\"", treeViewStyle, StringComparison.Ordinal);
        // HorizontalAlignment="Stretch" ensures content fills the viewport.
        Assert.Contains("HorizontalAlignment=\"Stretch\"", treeViewStyle, StringComparison.Ordinal);
        // Auto means scrollbar only appears when extent > viewport; with MaxWidth == viewport, it won't.
        Assert.Contains("<Setter Property=\"ScrollViewer.HorizontalScrollBarVisibility\" Value=\"Auto\" />", treeViewStyle, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeView_ViewportNarrowerThanMin_ShowsHorizontalScrollBar()
    {
        // Style assertion proxy: when viewport < MinWidth (160), Avalonia's MinMax clamp
        // resolves wrapper to MinWidth (160 > viewport), so extent > viewport → scrollbar appears.
        var styles = ReadSharedStylesText();
        var treeViewStyle = ExtractStyle(styles, "TreeView.entity-card-tree-view");

        Assert.Contains("MinWidth=\"160\"", treeViewStyle, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"ScrollViewer.HorizontalScrollBarVisibility\" Value=\"Auto\" />", treeViewStyle, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeView_NestedItem_WrapsWithinIndentedWidth()
    {
        // Style/template assertion proxy: the single ItemsPanel wrapper is constrained to
        // [MinWidth, Viewport.Width]. Indentation is handled INSIDE the wrapper by the
        // TreeViewItem template's 20,* grid, so nested items wrap at (wrapper - indent),
        // not at the full viewport width.
        var styles = ReadSharedStylesText();
        var treeViewStyle = ExtractStyle(styles, "TreeView.entity-card-tree-view");

        // Only ONE wrapper (the ItemsPanel) is bound to the viewport — not per-item.
        Assert.Contains("<ItemsPanelTemplate>", treeViewStyle, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"{Binding $parent[ScrollViewer].Viewport.Width}\"", treeViewStyle, StringComparison.Ordinal);

        // The TreeViewItem template uses 20,* grid for indentation inside the wrapper.
        var template = ExtractStyle(styles, EntityCardTreeTemplateSelector);
        Assert.Contains("ColumnDefinitions=\"20,*\"", template, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentChatEditor_NavigationTree_InheritsWrapperFromSharedStyle()
    {
        // The NavigationTree consumer does not redeclare ScrollViewer settings or the ItemsPanel
        // wrapper and therefore inherits the Auto + ItemsPanel wrapper from the shared style.
        var repositoryRoot = FindRepositoryRoot();
        var editorPath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Agent.Gui",
            "Controls",
            "AgentChatEditorControl.axaml");
        var editor = File.ReadAllText(editorPath);
        var treeStart = editor.IndexOf("x:Name=\"NavigationTree\"", StringComparison.Ordinal);
        Assert.True(treeStart >= 0);
        var treeEnd = editor.IndexOf(">", treeStart, StringComparison.Ordinal);
        var navigationTree = editor[treeStart..treeEnd];

        Assert.DoesNotContain("ItemsPanel", navigationTree, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxWidth", navigationTree, StringComparison.Ordinal);
    }

    // Issue #1064 — runtime two-regime layout proofs (ported from TwoRegimeExperiments.cs) --------
    //
    // These PhantomAvaloniaFact tests render a bare TreeView carrying the SHIPPED
    // TreeView.entity-card-tree style (extracted verbatim from SharedStyles.axaml) inside a headless
    // window, then run layout and assert the inner ScrollViewer's Viewport/Extent/Offset and the
    // scrollbar visibility across the two regimes. They prove the actual runtime behaviour
    // (wide viewport => caps to viewport, wraps, NO horizontal scrollbar; narrow viewport => floors
    // at MinWidth=160, a horizontal scrollbar appears and is reachable), not merely the static style
    // declaration that the string-AXAML tests above already cover.

    public sealed class EntityCardTreeModel
    {
        public string Branch { get; set; } = "feature/wrap-fix";

        public string HeadCommit { get; set; } = "a1b2c3d4e5f6";

        public string Path { get; set; } =
            @"C:\Users\jrowe.PHANTOM\Work Folders\My Documents\Network Settings\microsoft-extensions\modern-cmake-sample\worktrees\feature-branch\src\Phantom.Workspaces";

        public string TargetBranch { get; set; } = "main";

        public List<EntityCardTreeModel> Children { get; } = new();
    }

    private sealed record TreeProbe(
        ScrollViewer Inner,
        TextBlock Path,
        TreeViewItem Item,
        Panel? Wrapper,
        bool HScroll,
        bool VScroll);

    // Simplified entity-card TreeViewItem template (20,* indent grid + MinWidth 160), mirroring the
    // shipped item template's structure so nested items indent inside the single items-region wrapper.
    private static string ItemTemplateStyleFor(string primaryClass) => $$$"""
        <Style Selector="TreeView.{{{primaryClass}}} TreeViewItem">
          <Setter Property="HorizontalAlignment" Value="Stretch" />
          <Setter Property="MinWidth" Value="160" />
          <Setter Property="Template">
            <ControlTemplate>
              <Grid RowDefinitions="Auto,*" ColumnDefinitions="20,*" HorizontalAlignment="Stretch">
                <Border Grid.Row="0" Grid.Column="0" Grid.ColumnSpan="2" Padding="6" BorderThickness="1" HorizontalAlignment="Stretch">
                  <ContentControl HorizontalAlignment="Stretch"
                                  Content="{TemplateBinding Header}"
                                  ContentTemplate="{TemplateBinding HeaderTemplate}" />
                </Border>
                <Border Grid.Column="0" Grid.Row="1" Width="2" HorizontalAlignment="Center" />
                <ItemsPresenter Name="PART_ItemsPresenter" Grid.Column="1" Grid.Row="1"
                                IsVisible="{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}}" />
              </Grid>
            </ControlTemplate>
          </Setter>
        </Style>
        """;

    private const string GitWorktreeCardXaml = """
        <StackPanel Name="Card" HorizontalAlignment="Stretch">
          <Grid ColumnDefinitions="Auto,*,Auto">
            <Border Grid.Column="0" Width="4" />
            <StackPanel Grid.Column="1" MinWidth="67" Margin="0,0,12,0">
              <TextBlock Text="{Binding Branch}" FontWeight="Bold" />
              <TextBlock Text="git-worktree" />
            </StackPanel>
          </Grid>
          <Grid ColumnDefinitions="200,*"><TextBlock Text="branch" /><TextBlock Grid.Column="1" Text="{Binding Branch}" TextWrapping="Wrap" HorizontalAlignment="Stretch" /></Grid>
          <Grid ColumnDefinitions="200,*"><TextBlock Text="head-commit" /><TextBlock Grid.Column="1" Text="{Binding HeadCommit}" TextWrapping="Wrap" HorizontalAlignment="Stretch" /></Grid>
          <Grid ColumnDefinitions="200,*"><TextBlock Text="path" /><TextBlock Grid.Column="1" Name="PathValue" Text="{Binding Path}" TextWrapping="Wrap" HorizontalAlignment="Stretch" /></Grid>
          <Grid ColumnDefinitions="200,*"><TextBlock Text="target-branch" /><TextBlock Grid.Column="1" Text="{Binding TargetBranch}" TextWrapping="Wrap" HorizontalAlignment="Stretch" /></Grid>
        </StackPanel>
        """;

    // Soft-label variant (the #1064 secondary fix): the label column is Auto,* instead of a fixed
    // 200px floor, so the value column is not starved at the 160 min and the path wraps.
    private const string GitWorktreeSoftLabelCardXaml = """
        <StackPanel Name="Card" HorizontalAlignment="Stretch">
          <Grid ColumnDefinitions="Auto,*,Auto">
            <Border Grid.Column="0" Width="4" />
            <StackPanel Grid.Column="1" MinWidth="67" Margin="0,0,12,0">
              <TextBlock Text="{Binding Branch}" FontWeight="Bold" />
              <TextBlock Text="git-worktree" />
            </StackPanel>
          </Grid>
          <Grid ColumnDefinitions="Auto,*"><TextBlock Text="path" Margin="0,0,6,0" /><TextBlock Grid.Column="1" Name="PathValue" Text="{Binding Path}" TextWrapping="Wrap" HorizontalAlignment="Stretch" /></Grid>
        </StackPanel>
        """;

    private static string BuildTreeWindowXaml(string classAttr, string primaryClass, string? styleSelector, string cardXaml)
    {
        var itemStyle = ItemTemplateStyleFor(primaryClass);
        var injected = styleSelector is null ? string.Empty : ExtractStyle(ReadSharedStylesText(), styleSelector);
        return $$"""
        <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:controls="using:Phantom.Workspaces.Gui.Shared.Controls">
          <Window.Styles>
            {{itemStyle}}
            {{injected}}
          </Window.Styles>
          <TreeView Name="ViewTree" Classes="{{classAttr}}" HorizontalAlignment="Stretch">
            <TreeView.ItemTemplate>
              <TreeDataTemplate ItemsSource="{Binding Children}">
                {{cardXaml}}
              </TreeDataTemplate>
            </TreeView.ItemTemplate>
          </TreeView>
        </Window>
        """;
    }

    private static List<EntityCardTreeModel> BuildEntityCardItems(int count, bool nested)
    {
        var list = new List<EntityCardTreeModel>();
        for (var i = 0; i < count; i++)
        {
            var model = new EntityCardTreeModel { Branch = $"feature/wrap-fix-{i}" };
            if (nested && i == 0)
            {
                model.Children.Add(new EntityCardTreeModel { Branch = "feature/nested-child" });
            }

            list.Add(model);
        }

        return list;
    }

    private static TreeProbe LayoutTree(
        string classAttr,
        string primaryClass,
        string? styleSelector,
        string cardXaml,
        double width,
        double height,
        int count,
        bool nested = false)
    {
        var window = (Window)AvaloniaRuntimeXamlLoader.Load(
            BuildTreeWindowXaml(classAttr, primaryClass, styleSelector, cardXaml));
        window.SizeToContent = SizeToContent.Manual;
        window.Width = width;
        window.Height = height;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tree = window.GetVisualDescendants().OfType<TreeView>().First();
        tree.ItemsSource = BuildEntityCardItems(count, nested);
        Dispatcher.UIThread.RunJobs();
        foreach (var tvi in window.GetVisualDescendants().OfType<TreeViewItem>())
        {
            tvi.IsExpanded = true;
        }

        Dispatcher.UIThread.RunJobs();

        var inner = tree.GetVisualDescendants().OfType<ScrollViewer>().First();
        var path = window.GetVisualDescendants().OfType<TextBlock>().First(b => b.Name == "PathValue");
        var item = window.GetVisualDescendants().OfType<TreeViewItem>().First();
        var wrapper = window.GetVisualDescendants().OfType<Panel>().FirstOrDefault(s => s.MinWidth == 160);
        var hScroll = window.GetVisualDescendants().OfType<ScrollBar>()
            .Any(s => s.Orientation == Orientation.Horizontal && s.IsEffectivelyVisible && s.Bounds.Width > 0);
        var vScroll = window.GetVisualDescendants().OfType<ScrollBar>()
            .Any(s => s.Orientation == Orientation.Vertical && s.IsEffectivelyVisible && s.Bounds.Height > 0);
        return new TreeProbe(inner, path, item, wrapper, hScroll, vScroll);
    }

    private static TreeProbe LayoutEntityCardTree(
        string cardXaml, double width, double height, int count, bool nested = false)
        => LayoutTree(
            "entity-card-tree entity-card-tree-entity",
            "entity-card-tree",
            "TreeView.entity-card-tree",
            cardXaml,
            width,
            height,
            count,
            nested);

    [AvaloniaFact(Timeout = 30_000)]
    public void EntityCardTree_AttachedScrollProperties_FlowToInnerScrollViewer()
    {
        // The ScrollViewer.* attached properties set on TreeView.entity-card-tree flow to the
        // templated (unnamed) inner ScrollViewer.
        var p = LayoutEntityCardTree(GitWorktreeCardXaml, 800, 600, 1);
        Assert.Equal(ScrollBarVisibility.Auto, p.Inner.HorizontalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Auto, p.Inner.VerticalScrollBarVisibility);
        Assert.False(p.Inner.AllowAutoHide);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public void EntityCardTree_ViewportWiderThanMin_CapsToViewport_Wraps_NoHScroll()
    {
        // Regime 1: viewport 800 >= 160 => the wrapper/extent cap to the viewport, the path wraps
        // (height grows), and no horizontal scrollbar appears.
        var p = LayoutEntityCardTree(GitWorktreeCardXaml, 800, 600, 1);
        Assert.NotNull(p.Wrapper);
        Assert.True(
            p.Wrapper!.Bounds.Width <= p.Inner.Viewport.Width + 1 && p.Wrapper.Bounds.Width > 400,
            $"wrapper={p.Wrapper.Bounds.Width} viewport={p.Inner.Viewport.Width}");
        Assert.True(
            p.Inner.Extent.Width <= p.Inner.Viewport.Width + 1,
            $"extent={p.Inner.Extent.Width} viewport={p.Inner.Viewport.Width}");
        Assert.True(p.Path.Bounds.Height > 30, $"path wrapped, height={p.Path.Bounds.Height}");
        Assert.False(p.HScroll, "no horizontal scrollbar expected when viewport >= min");
    }

    [AvaloniaFact(Timeout = 30_000)]
    public void EntityCardTree_ViewportNarrowerThanMin_ShowsHScrollbar_AndIsReachable()
    {
        // Regime 2: viewport 120 < 160 => wrapper clamps to MinWidth=160 > viewport, extent exceeds
        // the viewport, a horizontal scrollbar appears, and Offset.X can be scrolled > 0 to reveal
        // the minimum-width content. The measure stays finite (bounded near the min, not the
        // infinite-measure overflow).
        var p = LayoutEntityCardTree(GitWorktreeCardXaml, 120, 600, 1);
        Assert.NotNull(p.Wrapper);
        Assert.True(
            p.Wrapper!.Bounds.Width >= 159 && p.Wrapper.Bounds.Width <= 161,
            $"wrapper should clamp to MinWidth 160, got {p.Wrapper.Bounds.Width}");
        Assert.True(
            p.Inner.Extent.Width > p.Inner.Viewport.Width,
            $"extent={p.Inner.Extent.Width} viewport={p.Inner.Viewport.Width}");
        Assert.True(p.HScroll, "horizontal scrollbar expected when viewport < min");
        Assert.True(p.Inner.Extent.Width < 400, $"extent bounded near min, not infinite: {p.Inner.Extent.Width}");

        var maxOffset = p.Inner.Extent.Width - p.Inner.Viewport.Width;
        Assert.True(maxOffset > 0, $"there is horizontal scrolling room: {maxOffset}");
        p.Inner.Offset = new Vector(maxOffset, p.Inner.Offset.Y);
        Dispatcher.UIThread.RunJobs();
        Assert.True(p.Inner.Offset.X > 0, $"scrolled horizontally to X={p.Inner.Offset.X}");
    }

    [AvaloniaFact(Timeout = 30_000)]
    public void EntityCardTree_NestedItem_WrapsWithinWrapperWidth()
    {
        // A nested (indented) TreeViewItem arranges within the wrapper width, confirming
        // indentation is handled inside the single items-region wrapper.
        var p = LayoutEntityCardTree(GitWorktreeCardXaml, 800, 600, 1, nested: true);
        Assert.NotNull(p.Wrapper);
        var items = p.Wrapper!.GetVisualDescendants().OfType<TreeViewItem>().ToList();
        Assert.True(items.Count >= 2, $"expected nested items, got {items.Count}");
        foreach (var it in items)
        {
            Assert.True(
                it.Bounds.Width <= p.Wrapper.Bounds.Width + 1,
                $"item width {it.Bounds.Width} exceeds wrapper {p.Wrapper.Bounds.Width}");
        }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public void EntityCardTree_ManyItems_InnerScroller_ProvidesVerticalScroll()
    {
        // With items exceeding the height and no outer scroller, the inner scroller supplies
        // vertical scrolling: extent height exceeds the viewport height, a vertical scrollbar
        // appears, the viewport width shrinks by a gutter (no overlap), and no horizontal scrollbar.
        var p = LayoutEntityCardTree(GitWorktreeCardXaml, 800, 300, 20);
        Assert.True(
            p.Inner.Extent.Height > p.Inner.Viewport.Height,
            $"content taller than viewport: extentH={p.Inner.Extent.Height} viewportH={p.Inner.Viewport.Height}");
        Assert.True(p.VScroll, "vertical scrollbar expected");
        Assert.True(p.Inner.Viewport.Width < 800, $"gutter reserved, viewportW={p.Inner.Viewport.Width}");
        Assert.False(p.HScroll, "no horizontal scrollbar in wide-viewport vertical-scroll case");
    }

    [AvaloniaFact(Timeout = 30_000)]
    public void EntityCardTree_WrapperMaxWidth_ResolvesToInnerScrollerViewport()
    {
        // The wrapper's resolved MaxWidth equals the inner scroller's Viewport.Width (gutter
        // excluded), proving $parent[ScrollViewer] resolves name-free to the unnamed inner scroller.
        var p = LayoutEntityCardTree(GitWorktreeCardXaml, 800, 300, 20);
        Assert.NotNull(p.Wrapper);
        Assert.Equal(p.Inner.Viewport.Width, p.Wrapper!.MaxWidth, 1);
        Assert.True(p.Wrapper.MaxWidth < 800, $"MaxWidth excludes gutter, ={p.Wrapper.MaxWidth}");
    }

    [AvaloniaFact(Timeout = 30_000)]
    public void EntityCardTree_SoftLabel_NarrowViewport_WrapsAtMin_AndScrolls()
    {
        // Soft-label card: at viewport 120 (< min) the value column is not starved, so the path
        // value WRAPS at the 160 min width while the horizontal scrollbar remains reachable.
        var p = LayoutEntityCardTree(GitWorktreeSoftLabelCardXaml, 120, 600, 1);
        Assert.NotNull(p.Wrapper);
        Assert.True(
            p.Wrapper!.Bounds.Width >= 159 && p.Wrapper.Bounds.Width <= 161,
            $"wrapper clamps to MinWidth 160, got {p.Wrapper.Bounds.Width}");
        Assert.True(p.Inner.Extent.Width > p.Inner.Viewport.Width, "extent exceeds viewport => scrollable");
        Assert.True(p.HScroll, "horizontal scrollbar expected");
        Assert.True(p.Path.Bounds.Height > 30, $"path wraps at 160, height={p.Path.Bounds.Height}");
    }

    [AvaloniaFact(Timeout = 30_000)]
    public void EntityCardTree_DefaultInnerAutoScroll_OverflowsAndDoesNotWrap()
    {
        // Regression guard: the legacy entity-card-tree with NO items-region wrapper (default Auto
        // inner scroller measured at infinity) grows the card far past the viewport and the path
        // stays a single line — the exact bug the two-regime wrapper fixes.
        var p = LayoutTree(
            "entity-card-tree entity-card-tree-entity",
            "entity-card-tree",
            styleSelector: null,
            GitWorktreeCardXaml,
            800,
            600,
            1);
        Assert.Null(p.Wrapper);
        Assert.True(p.Item.Bounds.Width > 900, $"card overflows viewport without wrapper: {p.Item.Bounds.Width}");
        Assert.True(p.Path.Bounds.Height < 30, $"path stays a single line (does not wrap): {p.Path.Bounds.Height}");
    }

    [AvaloniaFact(Timeout = 30_000)]
    public void EntityCardTreeView_StillMatchesEntityCardTree_TwoRegime()
    {
        // Guard that #1049's entity-card-tree-view and #1064's entity-card-tree produce the SAME
        // two-regime behaviour: wide => caps to viewport with no h-scrollbar; narrow => floors at
        // MinWidth=160 with a single h-scrollbar.
        var viewWide = LayoutTree(
            "entity-card-tree-view",
            "entity-card-tree-view",
            "TreeView.entity-card-tree-view",
            GitWorktreeCardXaml,
            800,
            600,
            1);
        Assert.NotNull(viewWide.Wrapper);
        Assert.True(viewWide.Inner.Extent.Width <= viewWide.Inner.Viewport.Width + 1, "view wide caps to viewport");
        Assert.False(viewWide.HScroll, "view wide has no h-scrollbar");

        var viewNarrow = LayoutTree(
            "entity-card-tree-view",
            "entity-card-tree-view",
            "TreeView.entity-card-tree-view",
            GitWorktreeCardXaml,
            120,
            600,
            1);
        Assert.NotNull(viewNarrow.Wrapper);
        Assert.True(
            viewNarrow.Wrapper!.Bounds.Width >= 159 && viewNarrow.Wrapper.Bounds.Width <= 161,
            $"view narrow clamps to 160, got {viewNarrow.Wrapper.Bounds.Width}");
        Assert.True(viewNarrow.HScroll, "view narrow shows h-scrollbar");

        // And the entity-card-tree class exhibits the identical pair.
        var treeWide = LayoutEntityCardTree(GitWorktreeCardXaml, 800, 600, 1);
        var treeNarrow = LayoutEntityCardTree(GitWorktreeCardXaml, 120, 600, 1);
        Assert.False(treeWide.HScroll, "tree wide has no h-scrollbar");
        Assert.True(treeNarrow.HScroll, "tree narrow shows h-scrollbar");
    }
}

