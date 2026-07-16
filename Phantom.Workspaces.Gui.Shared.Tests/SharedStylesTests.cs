using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using System.Reflection;
using System.Text.RegularExpressions;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Gui.Shared.Tests;

[Collection("Avalonia")]
public sealed class SharedStylesTests
{
    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 30_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void AltIndexBadge_DefaultOpacity_Is0()
    {
        // Issue #505: badges must be invisible (opacity 0) when Alt is not held.
        var sharedStyles = LoadSharedStyles();

        var border = new Border();
        border.Classes.Add("alt-index-badge");

        var host = new StackPanel();
        host.Styles.Add(sharedStyles);
        host.Children.Add(border);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(0.0, border.Opacity);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void AltIndexBadge_WhenAltHeld_OpacityIsOne()
    {
        // Issue #349: the alt-held override must still raise opacity to 1.
        var sharedStyles = LoadSharedStyles();

        var border = new Border();
        border.Classes.Add("alt-index-badge");
        border.Classes.Add("alt-held");

        var host = new StackPanel();
        host.Styles.Add(sharedStyles);
        host.Children.Add(border);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(1.0, border.Opacity);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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
        var label = ExtractStyle(styles, "TextBlock.workspace-field-label");
        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"67\" />", label, StringComparison.Ordinal);
        var value = ExtractStyle(styles, "TextBox.workspace-field-value");
        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"67\" />", value, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardControl_HeaderAndActionsRow_MinWidthIsSixtySeven()
    {
        // Issue #1045: display-name column and actions row min-widths drop to 2/3 (100 -> 67).
        var card = ReadEntityCardControlText();

        var headerStart = card.IndexOf("Classes=\"workspace-entity-header-row\"", StringComparison.Ordinal);
        Assert.True(headerStart >= 0);
        var headerEnd = card.IndexOf(">", headerStart, StringComparison.Ordinal);
        var header = card[headerStart..headerEnd];
        Assert.Contains("MinWidth=\"67\"", header, StringComparison.Ordinal);

        var actionsStart = card.IndexOf("<WrapPanel Grid.Column=\"2\"", StringComparison.Ordinal);
        Assert.True(actionsStart >= 0, "Actions row must be a WrapPanel so action buttons wrap.");
        var actionsEnd = card.IndexOf(">", actionsStart, StringComparison.Ordinal);
        var actions = card[actionsStart..actionsEnd];
        Assert.Contains("Classes=\"workspace-entity-actions-row\"", actions, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"67\"", actions, StringComparison.Ordinal);

        var styles = ReadSharedStylesText();
        Assert.Contains("<Style Selector=\"WrapPanel.workspace-entity-actions-row\">", styles, StringComparison.Ordinal);
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
}

