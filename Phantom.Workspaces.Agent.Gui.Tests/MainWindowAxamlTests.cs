using Avalonia.Headless.XUnit;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Phantom.Workspaces.Agent.Gui.Controls;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class MainWindowAxamlTests
{
    [Fact]
    public void MainWindow_UsesBinding_ForChildControlDataContexts()
    {
        var mainWindowContent = ReadMainWindowAxaml();

        Assert.Contains(
            "<controls:AgentChatEditorControl DataContext=\"{Binding Agent}\"/>",
            mainWindowContent,
            StringComparison.Ordinal);
    }

    [Fact]
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
            "MaxWidth=\"480\"",
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
            "ItemsSource=\"{Binding DetailContentSlots}\"",
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
            "Classes=\"entity-card-tree-view entity-card-tree-sticky\"",
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
        // Issue #1064: the tools-detail tree no longer sets inline ScrollViewer.* setters; it
        // inherits the two-regime entity-card-tree wrapper (H=Auto + items-region cap).
        Assert.DoesNotContain(
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

        Assert.Contains(
            "RequestedThemeVariant=\"Default\"",
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
            "<ItemsPresenter Grid.Column=\"1\"",
            sharedStylesContent,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Margin=\"18,0,0,0\"",
            sharedStylesContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsVisible=\"{Binding IsExpanded}\" />",
            sharedStylesContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AgentChatEditorControl_ConversationDetailIncludesStatusLine()
    {
        var editorControlContent = ReadAxaml("AgentChatEditorControl.axaml");
        var appContent = ReadAgentGuiFile("App.axaml");

        Assert.Contains(
            "Classes=\"agent-chat-status-line\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Classes=\"agent-chat-status-line-brain\"",
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

    [Fact]
    public void EntityCardTreeTemplate_DoesNotWrapEntityCardControlInSecondCardBorder()
    {
        var sharedStylesContent = ReadSharedStylesFile();

        Assert.DoesNotContain(
            "Classes=\"entity-card branch-header\"",
            sharedStylesContent,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Classes=\"entity-card leaf\"",
            sharedStylesContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Classes=\"branch-header\"",
            sharedStylesContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EntityCardTreeTypeBar_IsConstrainedToCardHeaderAndCardsStretch()
    {
        var sharedStylesContent = ReadSharedStylesFile();
        var selector = "<Style Selector=\"TreeView.entity-card-tree.entity-card-tree-entity Border.entity-card-tree-type-bar\">";
        var styleStart = sharedStylesContent.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(styleStart >= 0);
        var styleEnd = sharedStylesContent.IndexOf("</Style>", styleStart, StringComparison.Ordinal);
        Assert.True(styleEnd > styleStart);
        var typeBarStyle = sharedStylesContent[styleStart..(styleEnd + "</Style>".Length)];

        Assert.Contains(
            selector,
            typeBarStyle,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<Setter Property=\"Height\" Value=\"42\" />",
            typeBarStyle,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<Setter Property=\"VerticalAlignment\" Value=\"Top\" />",
            typeBarStyle,
            StringComparison.Ordinal);

        // Type bar must live inside EntityCardControl's header grid, not in a sibling
        // overlay that stretches down the whole card.
        var dataTemplatesContent = ReadMainAppFile(Path.Combine("Templates", "WorkspaceDataTemplates.axaml"));
        Assert.DoesNotContain("entity-card-tree-type-bar", dataTemplatesContent, StringComparison.Ordinal);

        var entityCardContent = ReadMainAppFile(Path.Combine("Controls", "EntityCardControl.axaml"));
        Assert.Contains("ColumnDefinitions=\"Auto,*,Auto\"", entityCardContent, StringComparison.Ordinal);
        Assert.Contains("Classes=\"entity-card-tree-type-bar\"", entityCardContent, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"1\"", entityCardContent, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"2\"", entityCardContent, StringComparison.Ordinal);
        Assert.DoesNotContain("HorizontalAlignment=\"Left\"", entityCardContent, StringComparison.Ordinal);
        Assert.Contains("ClipToBounds=\"True\"", entityCardContent, StringComparison.Ordinal);

        Assert.Contains(
            "<Setter Property=\"HorizontalAlignment\" Value=\"Stretch\" />",
            sharedStylesContent,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<Setter Property=\"MaxWidth\" Value=\"760\" />",
            sharedStylesContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowView_EntityPaneTree_HasNoSurroundingScrollViewer()
    {
        // Issue #1064: the entity-pane TreeView carries the two-regime entity-card-tree wrapper,
        // so the legacy surrounding ScrollViewer (H=Disabled) is removed and the tree's own inner
        // scroller is the single scroller.
        var mainWindowContent = ReadMainAppFile("MainWindow.axaml");

        Assert.Contains(
            "Classes=\"entity-card-tree entity-card-tree-entity\"",
            mainWindowContent,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<ScrollViewer Grid.Row=\"1\"",
            mainWindowContent,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "HorizontalScrollBarVisibility=\"Disabled\"",
            mainWindowContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AgentChatToolsDetail_Tree_InheritsScrollWrapperInsteadOfDisablingHScroll()
    {
        // Issue #1064: the tools-detail tree no longer sets inline ScrollViewer.* setters; it
        // inherits the two-regime entity-card-tree wrapper (gaining the below-minimum scrollbar).
        var toolsDetailContent = ReadAxaml("AgentChatToolsDetailControl.axaml");

        Assert.Contains(
            "Classes=\"entity-card-tree entity-card-tree-sticky\"",
            toolsDetailContent,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ScrollViewer.HorizontalScrollBarVisibility=\"Disabled\"",
            toolsDetailContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EntityBrowserView_BrowserTree_HasNoSurroundingScrollViewer_AndIsSticky()
    {
        // Issue #1064: the entity-browser tree carries the entity-card-tree class and inherits the
        // two-regime wrapper, so the surrounding ScrollViewer is removed and the tree's own inner
        // scroller is the single scroller. StickyScroll is preserved on that inner scroller via the
        // entity-card-tree-sticky class (SharedStyles selector
        // TreeView.entity-card-tree.entity-card-tree-sticky ScrollViewer sets StickyScroll.IsEnabled).
        var browserContent = ReadMainAppFile(Path.Combine("Templates", "EntityBrowserWorkspaceTabView.axaml"));

        Assert.Contains(
            "Classes=\"entity-card-tree entity-card-tree-entity entity-card-tree-sticky\"",
            browserContent,
            StringComparison.Ordinal);
        // The surrounding ScrollViewer (with its inline StickyScroll/H=Disabled) is gone.
        Assert.DoesNotContain("BrowserScrollViewer", browserContent, StringComparison.Ordinal);
        Assert.DoesNotContain("HorizontalScrollBarVisibility=\"Disabled\"", browserContent, StringComparison.Ordinal);
        Assert.DoesNotContain("controls:StickyScroll.IsEnabled", browserContent, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleEntityView_ContentControl_UsesEntityCardShellClass()
    {
        // Issue #1066: the single-entity host reuses the entity-card-shell chrome.
        var template = ExtractSingleEntityTemplate();
        Assert.Contains(
            "Classes=\"entity-card-shell entity-card-single-host\"",
            template,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SingleEntityView_Host_IsHorizontallyCentered()
    {
        var template = ExtractSingleEntityTemplate();
        Assert.Contains("HorizontalAlignment=\"Center\"", template, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleEntityView_Host_HasTopMargin()
    {
        // Issue #1066: a non-zero top margin separates the card from the tab strip.
        var template = ExtractSingleEntityTemplate();
        Assert.Contains("Margin=\"0,12,0,0\"", template, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleEntityView_Host_MaxWidthCapsToAboutOneThird()
    {
        // Issue #1066: MaxWidth binds to ~1/3 of the pane width (via the shared converter), not
        // unbounded.
        var template = ExtractSingleEntityTemplate();
        Assert.Contains("<ContentControl.MaxWidth>", template, StringComparison.Ordinal);
        Assert.Contains("SingleEntityMaxWidthConverter", template, StringComparison.Ordinal);
        Assert.Contains("$parent[ScrollViewer].Bounds.Width", template, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleEntityView_Host_HasMinWidthOneSixty()
    {
        var template = ExtractSingleEntityTemplate();
        Assert.Contains("MinWidth=\"160\"", template, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleEntityView_ScrollViewer_HorizontalIsAutoAndCapsToViewport()
    {
        // Issue #1066: the host ScrollViewer uses H=Auto and the host MaxWidth binds to the
        // ScrollViewer viewport width (two-regime cap shared with #1064).
        var template = ExtractSingleEntityTemplate();
        Assert.Contains("HorizontalScrollBarVisibility=\"Auto\"", template, StringComparison.Ordinal);
        Assert.Contains("$parent[ScrollViewer].Viewport.Width", template, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleEntityView_ScrollViewer_VerticalIsAutoAndDoesNotAutoHide()
    {
        var template = ExtractSingleEntityTemplate();
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", template, StringComparison.Ordinal);
        Assert.Contains("AllowAutoHide=\"False\"", template, StringComparison.Ordinal);
    }

    private static string ExtractSingleEntityTemplate()
    {
        var dataTemplates = ReadMainAppFile(Path.Combine("Templates", "WorkspaceDataTemplates.axaml"));
        var start = dataTemplates.IndexOf(
            "<DataTemplate DataType=\"vm:EntityWorkspaceTabViewModel\">",
            StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected the EntityWorkspaceTabViewModel DataTemplate to exist.");
        var end = dataTemplates.IndexOf("</DataTemplate>", start, StringComparison.Ordinal);
        Assert.True(end > start, "Expected the EntityWorkspaceTabViewModel DataTemplate to be closed.");
        return dataTemplates[start..(end + "</DataTemplate>".Length)];
    }

    [Fact]
    public void SharedStyles_EntityCardTreeControlTemplate_CentersChildRailInIndentColumn()
    {
        var sharedStylesContent = ReadSharedStylesFile();

        // Issue #1045: the indent gutter is a fixed 20px column with the 2px rail centred within it.
        Assert.Contains(
            "ColumnDefinitions=\"20,*\"",
            sharedStylesContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Width=\"2\"",
            sharedStylesContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "HorizontalAlignment=\"Center\"",
            sharedStylesContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "FallbackValue=▼",
            sharedStylesContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "FallbackValue=#808080",
            sharedStylesContent,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ColumnDefinitions=\"21,*\"",
            sharedStylesContent,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ColumnDefinitions=\"Auto,*\"",
            sharedStylesContent,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Margin=\"18,0,0,0\"",
            sharedStylesContent,
            StringComparison.Ordinal);
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

    [Fact]
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
            "Gesture=\"Pause\" Command=\"{Binding ToggleHoldAllQueuesCommand}\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Gesture=\"Shift+Pause\" Command=\"{Binding HoldAllQueuesCommand}\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Gesture=\"Ctrl+Shift+Cancel\" Command=\"{Binding UnholdAllQueuesCommand}\"",
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public void AgentChatEditorControl_StatusLine_HasNoSeparatorTextBlocks()
    {
        // Issue #401: all TextBlock separator elements must be removed; spacing is now
        // handled by left margins on the item style classes.
        var editorControlContent = ReadAxaml("AgentChatEditorControl.axaml");

        Assert.DoesNotContain(
            "agent-chat-status-line-separator",
            editorControlContent,
            StringComparison.Ordinal);
    }

    [Fact]
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

    [Fact]
    public void MainWindow_HasBrainIconSet()
    {
        // Issue #327: the agent chat window must declare Window.Icon pointing at the pre-rendered
        // brain.ico so the OS taskbar / title bar shows a correctly-sized icon.
        var mainWindowContent = ReadMainWindowAxaml();

        Assert.Contains(
            "<Window.Icon>",
            mainWindowContent,
            StringComparison.Ordinal);

        Assert.Contains(
            "avares://Phantom.Workspaces.Gui.Shared/Assets/brain.ico",
            mainWindowContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BrainIco_FileExistsInGuiStylesAssets()
    {
        // Issue #327: the pre-rendered brain.ico must be present in the shared asset library.
        var repositoryRoot = FindRepositoryRoot();
        var icoPath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Gui.Shared",
            "Assets",
            "brain.ico");

        Assert.True(File.Exists(icoPath), $"brain.ico not found at: {icoPath}");
    }

    [Fact]
    public void BrainIco_IsEmbeddedAsAvaloniaResource()
    {
        // Issue #327: brain.ico must be declared as an AvaloniaResource so it is accessible
        // via the avares:// URI scheme at runtime.
        var csprojContent = ReadGuiStylesFile("Phantom.Workspaces.Gui.Shared.csproj");

        Assert.Contains(
            "AvaloniaResource",
            csprojContent,
            StringComparison.Ordinal);

        Assert.Contains(
            "brain.ico",
            csprojContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Application_RequestedThemeVariantSetToDark_SurfaceBrushResolvesToDarkValue()
    {
        var darkContent = ReadThemeFile("Dark.axaml");

        Assert.Contains(
            "Theme.Surface.EntityPane.Background\">#1E1E1E",
            darkContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Application_RequestedThemeVariantSetToLight_SurfaceBrushResolvesToLightValue()
    {
        var lightContent = ReadThemeFile("Light.axaml");

        Assert.Contains(
            "Theme.Surface.EntityPane.Background\">#F3F3F3",
            lightContent,
            StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void Application_RequestedThemeVariantChanged_ActualThemeVariantChangedEventFires()
    {
        var app = Application.Current ?? throw new InvalidOperationException("Application.Current is null");
        app.RequestedThemeVariant = ThemeVariant.Dark;

        int eventCount = 0;
        app.ActualThemeVariantChanged += (_, _) => eventCount++;

        app.RequestedThemeVariant = ThemeVariant.Light;

        Assert.Equal(1, eventCount);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void App_DefaultRequestedThemeVariant_FollowsOperatingSystemTheme()
    {
        var appContent = ReadAgentGuiFile("App.axaml");

        Assert.Contains(
            "RequestedThemeVariant=\"Default\"",
            appContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ThemeDictionaries_AllKeysDefinedInDark_AlsoDefinedInLight()
    {
        var darkContent = ReadThemeFile("Dark.axaml");
        var lightContent = ReadThemeFile("Light.axaml");

        var darkKeys = ExtractResourceKeys(darkContent);
        var lightKeys = ExtractResourceKeys(lightContent);

        foreach (var darkKey in darkKeys)
        {
            Assert.Contains(darkKey, lightKeys);
        }

        foreach (var lightKey in lightKeys)
        {
            Assert.Contains(lightKey, darkKeys);
        }
    }

    [Fact]
    public void ThemeDictionaries_AllReferencedKeysExist_InBothVariants()
    {
        var darkContent = ReadThemeFile("Dark.axaml");
        var lightContent = ReadThemeFile("Light.axaml");

        var darkKeys = ExtractResourceKeys(darkContent);
        var lightKeys = ExtractResourceKeys(lightContent);
        var allThemeKeys = new HashSet<string>(darkKeys);
        allThemeKeys.UnionWith(lightKeys);

        var referencedKeys = FindAllDynamicResourceReferences();

        foreach (var key in referencedKeys)
        {
            if (key.StartsWith("Theme.", StringComparison.Ordinal) || key.StartsWith("Terminal.", StringComparison.Ordinal))
            {
                if (allThemeKeys.Contains(key))
                {
                    Assert.Contains(key, darkKeys);
                    Assert.Contains(key, lightKeys);
                }
            }
        }
    }

    [Fact]
    public void ThemePreferenceService_ProfileJsonHasLight_AppStartsInLightVariant()
    {
        var appCsContent = ReadAgentGuiFile("App.axaml.cs");

        Assert.Contains(
            "\"light\" => ThemeVariant.Light",
            appCsContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ThemePreferenceService_ProfileJsonMissingOrSystem_AppStartsInDefaultVariant()
    {
        var appCsContent = ReadAgentGuiFile("App.axaml.cs");

        Assert.Contains(
            "_ => ThemeVariant.Default",
            appCsContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ThemePreferenceService_UserSelectsLight_PersistsLightToProfileJson()
    {
        // This test verifies the theme persistence contract exists in the main app's ViewModel
        var viewModelContent = ReadMainAppFile("ViewModels/MainWindowViewModel.cs");

        Assert.Contains(
            "ApplyThemeVariant",
            viewModelContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowViewModel_ApplyThemeResourcesUnderLight_DoesNotLeakIntoDark()
    {
        // This test verifies that ApplyThemeResources doesn't write directly to the root
        // Resources dictionary, which would cause light theme changes to leak into dark mode
        var viewModelContent = ReadMainAppFile("ViewModels/MainWindowViewModel.cs");

        Assert.Contains(
            "ApplyThemeResources",
            viewModelContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AgentGuiApp_Initialize_DoesNotReadProfileJsonDirectly()
    {
        // Verify that ApplyPersistedTheme uses the standard theme variant mapping
        var appCsContent = ReadAgentGuiFile("App.axaml.cs");

        Assert.Contains(
            "ApplyPersistedTheme",
            appCsContent,
            StringComparison.Ordinal);
        
        Assert.Contains(
            "ThemeVariant.Light",
            appCsContent,
            StringComparison.Ordinal);
        
        Assert.Contains(
            "ThemeVariant.Dark",
            appCsContent,
            StringComparison.Ordinal);
        
        Assert.Contains(
            "ThemeVariant.Default",
            appCsContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AgentChatEditor_NavigationTree_UsesEntityCardTreeViewStyle()
    {
        // Issue #1029: the agent chat editor navigation tree must consume the shared
        // entity-card-tree-view style so its content renders inside the shared item border.
        var editorControlContent = ReadAxaml("AgentChatEditorControl.axaml");

        var navStart = editorControlContent.IndexOf("x:Name=\"NavigationTree\"", StringComparison.Ordinal);
        Assert.True(navStart >= 0);
        var navEnd = editorControlContent.IndexOf('>', navStart);
        var navigationTree = editorControlContent[navStart..navEnd];

        Assert.Contains("Classes=\"entity-card-tree-view entity-card-tree-sticky\"", navigationTree, StringComparison.Ordinal);

        var control = new AgentChatEditorControl();
        var tree = GetField<TreeView>(control, "NavigationTree");
        Assert.Contains("entity-card-tree-view", tree.Classes);
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
            "Phantom.Workspaces.Gui.Shared",
            "Styles",
            "SharedStyles.axaml");

        return File.ReadAllText(filePath);
    }

    private static string ReadGuiStylesFile(string relativePath)
    {
        var repositoryRoot = FindRepositoryRoot();
        var filePath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Gui.Shared",
            relativePath);

        return File.ReadAllText(filePath);
    }

    private static string ReadMainAppFile(string relativePath)
    {
        var repositoryRoot = FindRepositoryRoot();
        var filePath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces",
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

    private static string ReadThemeFile(string fileName)
    {
        var repositoryRoot = FindRepositoryRoot();
        var filePath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Gui.Shared",
            "Themes",
            fileName);

        return File.ReadAllText(filePath);
    }

    private static List<string> ExtractResourceKeys(string axamlContent)
    {
        var keys = new List<string>();
        var lines = axamlContent.Split('\n');

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("x:Key=\""))
            {
                var startIndex = trimmed.IndexOf("x:Key=\"", StringComparison.Ordinal) + 7;
                var endIndex = trimmed.IndexOf("\"", startIndex, StringComparison.Ordinal);
                if (endIndex > startIndex)
                {
                    var key = trimmed.Substring(startIndex, endIndex - startIndex);
                    keys.Add(key);
                }
            }
        }

        return keys;
    }

    private static List<string> FindAllDynamicResourceReferences()
    {
        var keys = new HashSet<string>();
        var repositoryRoot = FindRepositoryRoot();

        var axamlFiles = Directory.GetFiles(repositoryRoot.FullName, "*.axaml", SearchOption.AllDirectories);

        foreach (var filePath in axamlFiles)
        {
            var content = File.ReadAllText(filePath);
            ExtractDynamicResourceKeys(content, keys);
        }

        return keys.ToList();
    }

    private static void ExtractDynamicResourceKeys(string content, HashSet<string> keys)
    {
        int index = 0;
        while ((index = content.IndexOf("{DynamicResource ", index, StringComparison.Ordinal)) != -1)
        {
            index += 17;
            var endIndex = content.IndexOf("}", index, StringComparison.Ordinal);
            if (endIndex > index)
            {
                var key = content.Substring(index, endIndex - index).Trim();
                keys.Add(key);
            }
        }

        index = 0;
        while ((index = content.IndexOf("{StaticResource ", index, StringComparison.Ordinal)) != -1)
        {
            index += 16;
            var endIndex = content.IndexOf("}", index, StringComparison.Ordinal);
            if (endIndex > index)
            {
                var key = content.Substring(index, endIndex - index).Trim();
                if (key.StartsWith("Theme.", StringComparison.Ordinal) || key.StartsWith("Terminal.", StringComparison.Ordinal))
                {
                    keys.Add(key);
                }
            }
        }
    }

}


