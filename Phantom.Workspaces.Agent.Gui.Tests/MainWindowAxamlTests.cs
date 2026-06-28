using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Phantom.Workspaces.Agent.Gui.Controls;

namespace Phantom.Workspaces.Agent.Gui.Tests;

[Trait("Category", "SlowLayout")]
public sealed class MainWindowAxamlTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public void MainWindow_UsesBinding_ForChildControlDataContexts()
    {
        var mainWindowContent = ReadMainWindowAxaml();

        Assert.Contains(
            "<controls:AgentChatEditorControl DataContext=\"{Binding Agent}\"/>",
            mainWindowContent,
            StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatEditorControl_UsesTreeNavigationAndSelectedDetailPane()
    {
        var editorControlContent = ReadAxaml("AgentChatEditorControl.axaml");

        Assert.Contains(
            "x:Name=\"NavigationTree\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Grid.ColumnDefinitions>",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "<ColumnDefinition Width=\"280\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "MinWidth=\"0\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "MaxWidth=\"480\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "ScrollViewer.HorizontalScrollBarVisibility=\"Disabled\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "ScrollViewer.AllowAutoHide=\"False\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"SplitterHost\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "<ToggleButton x:Name=\"TreeCollapseToggle\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "VerticalAlignment=\"Center\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "DataType=\"vm:AgentChatToolsDetailViewModel\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"Show reasoning text\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsChecked=\"{Binding IsReasoningVisible}\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "<controls:AgentChatToolsDetailControl/>",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "AgentChatToolTemplates.axaml",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Select a tool to view its details.",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"{Binding SelectedEditorDetailContent}\"",
            editorControlContent,
            StringComparison.Ordinal);

        Assert.Contains(
            "DataType=\"vm:AgentChatToolViewModel\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "ContentTemplate=\"{StaticResource AgentToolHeaderTemplate}\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemTemplate=\"{StaticResource AgentNavigationTreeHeaderItemTemplate}\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Classes=\"entity-card-tree entity-card-tree-sticky\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "ContentTemplate=\"{StaticResource AgentToolDetailTemplate}\"",
            editorControlContent,
            StringComparison.Ordinal);
        var toolTemplatesContent = ReadAxaml("AgentChatToolTemplates.axaml");
        Assert.Contains(
            "x:Key=\"AgentNavigationTreeHeaderItemTemplate\"",
            toolTemplatesContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Key=\"AgentNavigationTreeDetailItemTemplate\"",
            toolTemplatesContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsVisible=\"{Binding HasTool}\"",
            toolTemplatesContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "DataType=\"vm:AgentEditorNavigationItemViewModel\"",
            toolTemplatesContent,
            StringComparison.Ordinal);
        var toolsControlContent = ReadAxaml("AgentChatToolsDetailControl.axaml");
        Assert.Contains(
            "ItemsSource=\"{Binding DisplayedRootItems}\"",
            toolsControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "AgentChatToolTemplates.axaml",
            toolsControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Classes=\"entity-card-tree entity-card-tree-sticky\"",
            toolsControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemTemplate=\"{StaticResource AgentNavigationTreeDetailItemTemplate}\"",
            toolsControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "ScrollViewer.AllowAutoHide=\"False\"",
            toolsControlContent,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<GridSplitter",
            toolsControlContent,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SelectedTool",
            toolsControlContent,
            StringComparison.Ordinal);

        var appContent = ReadAgentGuiFile("App.axaml");
        Assert.Contains(
            "SharedStyles.axaml",
            appContent,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "RequestedThemeVariant=\"Dark\"",
            appContent,
            StringComparison.Ordinal);

        var sharedStylesContent = ReadSharedStylesFile();
        Assert.Contains(
            "TreeView.entity-card-tree TreeViewItem",
            sharedStylesContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:StickyScroll.IsEnabled",
            sharedStylesContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:TreeSticky.AutoRowLevel",
            sharedStylesContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "ContentTemplate=\"{TemplateBinding HeaderTemplate}\"",
            sharedStylesContent,
            StringComparison.Ordinal);

        // Issue #25: the entity-card-tree TreeViewItem template must bind the child
        // ItemsPresenter visibility to IsExpanded; otherwise collapsing a tool node has no
        // effect and the children stay visible.
        Assert.Contains(
            "<ItemsPresenter Margin=\"20,0,0,0\"",
            sharedStylesContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsVisible=\"{Binding IsExpanded}\" />",
            sharedStylesContent,
            StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatEditorControl_ConversationDetailIncludesStatusLine()
    {
        var editorControlContent = ReadAxaml("AgentChatEditorControl.axaml");
        var appContent = ReadAgentGuiFile("App.axaml");

        Assert.Contains(
            "Classes=\"agent-chat-status-line\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Classes.thinking=\"{Binding StatusLine.IsThinking}\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding StatusLine.ModelDisplay}\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding StatusLine.ProviderDisplay}\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding StatusLine.TokensDisplay}\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "AgentChatStatusLineStyles.axaml",
            appContent,
            StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatEditorControl_NavigationPane_StartsCollapsed()
    {
        // Issue #24: the editor navigation pane should start collapsed when an agent chat view
        // is opened, so the chat output uses the full width by default.
        var control = new AgentChatEditorControl();

        var navigationTree = GetField<TreeView>(control, "NavigationTree");
        var splitterHost = GetField<GridSplitter>(control, "SplitterHost");
        var collapseToggle = GetField<ToggleButton>(control, "TreeCollapseToggle");
        var editorGrid = GetField<Grid>(control, "EditorGrid");

        Assert.False(navigationTree.IsVisible);
        Assert.False(splitterHost.IsVisible);
        Assert.Equal("▶", collapseToggle.Content);
        Assert.True(collapseToggle.IsChecked);
        Assert.Equal(new GridLength(0), editorGrid.ColumnDefinitions[0].Width);
        Assert.Equal(new GridLength(0), editorGrid.ColumnDefinitions[1].Width);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatEditorControl_CanCollapseAndUncollapseNavigationPane()
    {
        var control = new AgentChatEditorControl();
        var setTreeCollapsed = control.GetType().GetMethod("SetTreeCollapsed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find SetTreeCollapsed method.");

        var navigationTree = GetField<TreeView>(control, "NavigationTree");
        var splitterHost = GetField<Control>(control, "SplitterHost");
        var treeSplitter = GetField<GridSplitter>(control, "SplitterHost");
        var collapseToggle = GetField<ToggleButton>(control, "TreeCollapseToggle");
        var editorGrid = GetField<Grid>(control, "EditorGrid");
        editorGrid.ColumnDefinitions[0].Width = new GridLength(318);

        setTreeCollapsed.Invoke(control, [true]);

        Assert.False(navigationTree.IsVisible);
        Assert.False(splitterHost.IsVisible);
        Assert.False(treeSplitter.IsVisible);
        Assert.Equal("▶", collapseToggle.Content);
        Assert.Equal(new GridLength(0), editorGrid.ColumnDefinitions[0].Width);
        Assert.Equal(new GridLength(0), editorGrid.ColumnDefinitions[1].Width);

        setTreeCollapsed.Invoke(control, [false]);

        Assert.True(navigationTree.IsVisible);
        Assert.True(splitterHost.IsVisible);
        Assert.True(treeSplitter.IsVisible);
        Assert.Equal("◀", collapseToggle.Content);
        Assert.Equal(new GridLength(318), editorGrid.ColumnDefinitions[0].Width);
        Assert.Equal(new GridLength(24), editorGrid.ColumnDefinitions[1].Width);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void ChildControls_DisableCompiledBindings_ForRuntimeDataContextHandoff()
    {
        var editorControlContent = ReadAxaml("AgentChatEditorControl.axaml");
        var inputQueueControlContent = ReadAxaml("AgentChatInputQueueControl.axaml");
        var outputControlContent = ReadAxaml("AgentChatOutputControl.axaml");

        Assert.Contains(
            "x:CompileBindings=\"False\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:CompileBindings=\"False\"",
            inputQueueControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:CompileBindings=\"False\"",
            outputControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"BrowserHost\"",
            outputControlContent,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "x:Name=\"AutoScrollToggle\"",
            outputControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsChecked=\"{Binding Agent.AutoScrollEnabled, Mode=TwoWay}\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IsHitTestVisible=\"{Binding Agent.AutoScrollDisabled}\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Opacity=\"{Binding Agent.AutoScrollDisabled, Converter={x:Static converters:BoolToOpacityConverter.Instance}}\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Classes.scroll-locked=\"{Binding Agent.AutoScrollEnabled}\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Gesture=\"Ctrl+Cancel\" Command=\"{Binding InterruptCommand}\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Gesture=\"Pause\" Command=\"{Binding InputQueue.ToggleHoldAllQueuesCommand}\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Gesture=\"Shift+Pause\" Command=\"{Binding InputQueue.HoldAllQueuesCommand}\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Gesture=\"Ctrl+Shift+Cancel\" Command=\"{Binding InputQueue.UnholdAllQueuesCommand}\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "<controls:AgentChatOutputControl DataContext=\"{Binding Agent}\"/>",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"Provider\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"Model\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"API type\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"Connection type\"",
            editorControlContent,
            StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EditorControl_InterruptGesture_MatchesCtrlBreak_NotPlainCancel()
    {
        // Issue #21: Windows delivers the Pause/Break key as Key.Cancel (VK_CANCEL) whenever Ctrl is
        // held, so Ctrl+Break arrives as Key.Cancel + Control. The interrupt binding must use the
        // "Ctrl+Cancel" gesture; the pre-fix "Cancel" gesture (no modifiers) never matched the real
        // event, which is why Ctrl+Break did nothing in Copilot sessions.
        var control = new AgentChatEditorControl();

        var interruptGesture = control.KeyBindings
            .Select(binding => binding.Gesture)
            .Single(gesture => gesture is { Key: Key.Cancel, KeyModifiers: KeyModifiers.Control });

        var ctrlBreak = new KeyEventArgs { Key = Key.Cancel, KeyModifiers = KeyModifiers.Control };
        Assert.True(interruptGesture.Matches(ctrlBreak));

        // The pre-fix gesture would not have matched the real Ctrl+Break event.
        Assert.False(new KeyGesture(Key.Cancel).Matches(ctrlBreak));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EditorControl_UnholdGesture_MatchesCtrlShiftBreak()
    {
        // Issue #21: Ctrl+Shift+Break likewise arrives as Key.Cancel + Control + Shift, so the unhold
        // binding must use "Ctrl+Shift+Cancel" rather than "Ctrl+Shift+Pause".
        var control = new AgentChatEditorControl();

        var unholdGesture = control.KeyBindings
            .Select(binding => binding.Gesture)
            .Single(gesture => gesture is { Key: Key.Cancel, KeyModifiers: KeyModifiers.Control | KeyModifiers.Shift });

        var ctrlShiftBreak = new KeyEventArgs
        {
            Key = Key.Cancel,
            KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift,
        };
        Assert.True(unholdGesture.Matches(ctrlShiftBreak));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatInputQueueControl_NonDefaultQueueHeader_ContainsStatusPillBoundToSetImmediacyCommand()
    {
        // Issue #127 (updated by #162): the status pill is now a ContentControl delegating to
        // QueueImmediacyPickerTemplate; the pill markup and its bindings live in QueueStyles.axaml.
        var content = ReadAxaml("AgentChatInputQueueControl.axaml");

        Assert.Contains(
            "IsVisible=\"{Binding !IsDefault}\"",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "ContentTemplate=\"{StaticResource QueueImmediacyPickerTemplate}\"",
            content,
            StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void QueueStyles_ContainsQueueImmediacyPickerTemplateWithAllBindings()
    {
        // Issue #162: QueueStyles.axaml must define the shared DataTemplate with all
        // immediacy bindings so both call sites can reference QueueImmediacyPickerTemplate.
        var content = ReadAgentGuiFile(Path.Combine("Styles", "QueueStyles.axaml"));

        Assert.Contains(
            "x:Key=\"QueueImmediacyPickerTemplate\"",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "IQueueImmediacyViewModel",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetImmediacyCommand",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedImmediacyOption.Label",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "ImmediateImmediacyOption",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "QueuedImmediacyOption",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "HeldImmediacyOption",
            content,
            StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void QueueComposerControl_StatusPill_UsesContentControlWithQueueImmediacyPickerTemplate()
    {
        // Issue #162: QueueComposerControl must delegate the pill to QueueImmediacyPickerTemplate
        // via a ContentControl rather than inlining the pill markup.
        var content = ReadAxaml("QueueComposerControl.axaml");

        Assert.Contains(
            "ContentTemplate=\"{StaticResource QueueImmediacyPickerTemplate}\"",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsVisible=\"{Binding IsDefaultComposer}\"",
            content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Classes=\"queue-status-pill dynamic\"",
            content,
            StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void QueueComposerControl_StatusPill_IsHiddenForNonDefaultComposer()
    {
        // Issue #127: the status pill inside QueueComposerControl must be hidden for
        // non-default composers because it is now rendered in the group header instead,
        // preventing a duplicate pill when the composer is expanded.
        var content = ReadAxaml("QueueComposerControl.axaml");

        Assert.Contains(
            "IsVisible=\"{Binding IsDefaultComposer}\"",
            content,
            StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatEditorControl_AutoScrollCheckbox_UsesScrollLockedClassForAnimation()
    {
        // Issue #130: the auto-scroll checkbox (and preceding separator) must animate between opacity
        // levels via a CSS .scroll-locked class rather than becoming fully invisible.
        var editorControlContent = ReadAxaml("AgentChatEditorControl.axaml");
        var statusLineStylesContent = ReadGuiStylesFile(Path.Combine("Styles", "AgentChatStatusLineStyles.axaml"));

        // New behaviour: .scroll-locked class drives the animation.
        Assert.Contains(
            "Classes.scroll-locked=\"{Binding Agent.AutoScrollEnabled}\"",
            editorControlContent,
            StringComparison.Ordinal);

        // Old behaviour must be absent — checkbox is always interactive.
        Assert.DoesNotContain(
            "IsHitTestVisible=\"{Binding Agent.AutoScrollDisabled}\"",
            editorControlContent,
            StringComparison.Ordinal);

        // Styles file must define the .scroll-locked variant and a DoubleTransition.
        Assert.Contains(
            "agent-chat-autoscroll-toggle.scroll-locked",
            statusLineStylesContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "DoubleTransition",
            statusLineStylesContent,
            StringComparison.Ordinal);
    }

    private static string ReadMainWindowAxaml()
    {
        return ReadAgentGuiFile("MainWindow.axaml");
    }

    private static string ReadAxaml(string fileName)
    {
        return ReadAgentGuiFile(Path.Combine("Controls", fileName));
    }

    private static string ReadAgentGuiFile(string relativePath)
    {
        var repositoryRoot = FindRepositoryRoot();
        var filePath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Agent.Gui",
            relativePath);

        return File.ReadAllText(filePath);
    }

    private static string ReadSharedStylesFile()
    {
        var repositoryRoot = FindRepositoryRoot();
        var filePath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Gui.Styles",
            "Styles",
            "SharedStyles.axaml");

        return File.ReadAllText(filePath);
    }

    private static string ReadGuiStylesFile(string relativePath)
    {
        var repositoryRoot = FindRepositoryRoot();
        var filePath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Gui.Styles",
            relativePath);

        return File.ReadAllText(filePath);
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

    private static T GetField<T>(object instance, string fieldName) where T : class
    {
        var field = instance.GetType().GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Could not find field '{fieldName}'.");

        return Assert.IsAssignableFrom<T>(field.GetValue(instance));
    }

}
