using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Phantom.Workspaces.Gui.Shared.Tests;

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
    public void AltIndexBadge_DefaultOpacity_Is0Point5()
    {
        // Issue #349: badges must appear instantly at 0.50 base opacity, not fade in from 0.
        var sharedStyles = LoadSharedStyles();

        var border = new Border();
        border.Classes.Add("alt-index-badge");

        var host = new StackPanel();
        host.Styles.Add(sharedStyles);
        host.Children.Add(border);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        Assert.Equal(0.50, border.Opacity);
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
    public void SharedStyles_QueueImmediacyOptionPill_UsesThemeResourceForBackground()
    {
        // Issue #253: queue-immediacy-option-pill must use DynamicResource for Background
        // and BorderBrush, not ReflectionBinding to per-state color properties.
        var repositoryRoot = FindRepositoryRoot();
        var stylesPath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Gui.Shared",
            "Styles",
            "SharedStyles.axaml");
        var content = File.ReadAllText(stylesPath);

        var pillStyleStart = content.IndexOf(
            "queue-immediacy-option-pill",
            StringComparison.Ordinal);
        Assert.True(pillStyleStart >= 0, "queue-immediacy-option-pill selector must exist.");

        var pillStyleEnd = content.IndexOf("</Style>", pillStyleStart, StringComparison.Ordinal);
        Assert.True(pillStyleEnd > pillStyleStart, "queue-immediacy-option-pill style must be closed.");

        var pillBlock = content[pillStyleStart..pillStyleEnd];
        Assert.DoesNotContain("ReflectionBinding Background", pillBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("ReflectionBinding BorderBrush", pillBlock, StringComparison.Ordinal);
        Assert.Contains("DynamicResource", pillBlock, StringComparison.Ordinal);
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
}
