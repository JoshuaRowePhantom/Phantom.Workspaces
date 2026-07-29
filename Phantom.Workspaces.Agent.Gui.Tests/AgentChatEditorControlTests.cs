using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentChatEditorControlTests
{
    [Fact]
    public void AgentChatEditorControl_AutoScrollCheckbox_HasToolTipMentioningScrollLockKey()
    {
        var axamlContent = ReadAxaml("AgentChatEditorControl.axaml");

        Assert.Contains(
            "agent-chat-autoscroll-toggle",
            axamlContent,
            StringComparison.Ordinal);

        var checkboxStart = axamlContent.IndexOf("agent-chat-autoscroll-toggle", StringComparison.Ordinal);
        var checkboxEnd = axamlContent.IndexOf("/>", checkboxStart, StringComparison.Ordinal);
        var checkboxXaml = axamlContent.Substring(checkboxStart, checkboxEnd - checkboxStart);

        Assert.Contains("ToolTip.Tip", checkboxXaml, StringComparison.Ordinal);
        Assert.Contains("Scroll Lock", checkboxXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentChatEditorControl_DetailRegion_UsesLockedDockControl()
    {
        // Issue #1035: the detail region is a locked, ItemsSource-bound Dock.Avalonia DockControl
        // (cache-N/show-one) rather than the old IsVisible deck. Docking is fully disabled.
        var axamlContent = ReadAxaml("AgentChatEditorControl.axaml");

        Assert.Contains(
            "<dock:DockControl",
            axamlContent,
            StringComparison.Ordinal);

        Assert.Contains(
            "Layout=\"{Binding DetailLayout}\"",
            axamlContent,
            StringComparison.Ordinal);

        // The old IsVisible deck (DetailContentSlots ItemsControl) must be gone.
        Assert.DoesNotContain(
            "{Binding DetailContentSlots}",
            axamlContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AgentChatEditorControl_DetailDock_LocksDockableCapabilities()
    {
        // Issue #1035: the dock must be locked so the user cannot close/float/drag the detail region.
        var axamlContent = ReadAxaml("AgentChatEditorControl.axaml");

        Assert.Contains(
            "IsDockingEnabled=\"False\"",
            axamlContent,
            StringComparison.Ordinal);

        Assert.Contains(
            "EnableManagedWindowLayer=\"False\"",
            axamlContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AgentChatEditorControl_DetailDock_HidesTabStrip()
    {
        // Issue #1035: the document tab strip is hidden via the scoped resource so only the active
        // detail content is shown (single-node detail region, no tabs).
        var axamlContent = ReadAxaml("AgentChatEditorControl.axaml");

        Assert.Contains(
            "DockDocumentControlTabStripVisible",
            axamlContent,
            StringComparison.Ordinal);

        var keyStart = axamlContent.IndexOf("DockDocumentControlTabStripVisible", StringComparison.Ordinal);
        var elementEnd = axamlContent.IndexOf("</x:Boolean>", keyStart, StringComparison.Ordinal);
        Assert.True(elementEnd > keyStart);
        var element = axamlContent.Substring(keyStart, elementEnd - keyStart);
        Assert.Contains("False", element, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentChatEditorControl_DoesNotContain_DiagnosticTabDataTemplate()
    {
        // Issue #819: The Diagnostics tab was removed from the agent edit view.
        var axamlContent = ReadAxaml("AgentChatEditorControl.axaml");

        Assert.DoesNotContain(
            "DataType=\"vm:DiagnosticInspectorViewModel\"",
            axamlContent,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "DataType=\"vm:DiagnosticItemViewModel\"",
            axamlContent,
            StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatEditorControl_DetailRegion_HostsDockControlInColumn2()
    {
        // Issue #1035: the detail region (Grid.Column=2) is a Dock.Avalonia DockControl, replacing
        // the old DetailContentSlots ItemsControl deck.
        var control = new AgentChatEditorControl();

        var editorGrid = GetField<Grid>(control, "EditorGrid");
        Assert.NotNull(editorGrid);

        var dockControl = editorGrid.Children
            .OfType<global::Dock.Avalonia.Controls.DockControl>()
            .FirstOrDefault(d => Grid.GetColumn(d) == 2);
        Assert.NotNull(dockControl);

        // Docking is fully disabled so the detail region stays locked.
        Assert.False(dockControl!.IsDockingEnabled);
    }

    [Fact]
    public void AgentChatEditorControl_SubAgentTemplate_HasNoOverlappingOutputControlStack()
    {
        // Fix #1112: the old SubAgentsContainerViewModel DataTemplate stacked one
        // ContentControl per SubAgentSlotViewModel in a Panel and toggled visibility with
        // IsSelected — this airspace-clobbered every native WebView2 transcript.
        // The template must NOT contain an ItemsControl over Slots, and must not define a
        // SubAgentSlotViewModel DataTemplate (per-sub-agent transcripts are now hosted as their
        // own DocumentDock Documents).
        var axamlContent = ReadAxaml("AgentChatEditorControl.axaml");

        Assert.DoesNotContain(
            "DataType=\"vm:SubAgentSlotViewModel\"",
            axamlContent,
            StringComparison.Ordinal);

        var containerStart = axamlContent.IndexOf(
            "DataType=\"vm:SubAgentsContainerViewModel\"",
            StringComparison.Ordinal);
        Assert.True(containerStart > 0);
        var containerEnd = axamlContent.IndexOf("</DataTemplate>", containerStart, StringComparison.Ordinal);
        var containerXaml = axamlContent.Substring(containerStart, containerEnd - containerStart);

        Assert.DoesNotContain("ItemsControl", containerXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Slots", containerXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentChatEditorControl_SubAgentsContainerTemplate_RendersBrowserCardOnly()
    {
        // Fix #1112: the SubAgentsContainer DataTemplate is reserved for the "Sub-agents (N)"
        // group node's browser card — no per-slot transcript rendering.
        var axamlContent = ReadAxaml("AgentChatEditorControl.axaml");

        var containerStart = axamlContent.IndexOf(
            "DataType=\"vm:SubAgentsContainerViewModel\"",
            StringComparison.Ordinal);
        Assert.True(containerStart > 0);
        var containerEnd = axamlContent.IndexOf("</DataTemplate>", containerStart, StringComparison.Ordinal);
        var containerXaml = axamlContent.Substring(containerStart, containerEnd - containerStart);

        Assert.Contains("Content=\"{Binding Browser}\"", containerXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentChatEditorControl_ConversationDetailTemplate_InputQueueIsVisibleBindingUsesAcceptsUserInput()
    {
        // Issue #903: The AgentChatConversationDetailViewModel DataTemplate must bind the
        // AgentChatInputQueueControl IsVisible property to Agent.AcceptsUserInput, not a
        // different property. This ensures input queue is hidden for sub-agents.
        var axamlContent = ReadAxaml("AgentChatEditorControl.axaml");

        var conversationDetailStart = axamlContent.IndexOf(
            "DataType=\"vm:AgentChatConversationDetailViewModel\"",
            StringComparison.Ordinal);
        Assert.True(conversationDetailStart > 0);

        var conversationDetailEnd = axamlContent.IndexOf(
            "</DataTemplate>",
            conversationDetailStart,
            StringComparison.Ordinal);
        Assert.True(conversationDetailEnd > conversationDetailStart);

        var conversationDetailXaml = axamlContent.Substring(
            conversationDetailStart,
            conversationDetailEnd - conversationDetailStart);

        Assert.Contains(
            "AgentChatInputQueueControl",
            conversationDetailXaml,
            StringComparison.Ordinal);

        Assert.Contains(
            "IsVisible=\"{Binding InputQueue, Converter={x:Static converters:NotNullConverter.Instance}}\"",
            conversationDetailXaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AgentChatEditor_NavigationTree_DoesNotDuplicateScrollViewerSettings()
    {
        // Issue #1045: ScrollViewer config is single-sourced from the shared entity-card-tree-view
        // style; the NavigationTree must not redeclare it inline.
        var axamlContent = ReadAxaml("AgentChatEditorControl.axaml");

        var treeStart = axamlContent.IndexOf("x:Name=\"NavigationTree\"", StringComparison.Ordinal);
        Assert.True(treeStart >= 0);
        var treeEnd = axamlContent.IndexOf(">", treeStart, StringComparison.Ordinal);
        var navigationTree = axamlContent[treeStart..treeEnd];

        Assert.DoesNotContain("ScrollViewer.HorizontalScrollBarVisibility", navigationTree, StringComparison.Ordinal);
        Assert.DoesNotContain("ScrollViewer.VerticalScrollBarVisibility", navigationTree, StringComparison.Ordinal);
        Assert.DoesNotContain("ScrollViewer.AllowAutoHide", navigationTree, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentChatEditor_NavigationTree_MinWidthIsReduced()
    {
        // Issue #1045: the NavigationTree MinWidth drops to 2/3 (240 -> 160).
        var axamlContent = ReadAxaml("AgentChatEditorControl.axaml");

        var treeStart = axamlContent.IndexOf("x:Name=\"NavigationTree\"", StringComparison.Ordinal);
        Assert.True(treeStart >= 0);
        var treeEnd = axamlContent.IndexOf(">", treeStart, StringComparison.Ordinal);
        var navigationTree = axamlContent[treeStart..treeEnd];

        Assert.Contains("MinWidth=\"160\"", navigationTree, StringComparison.Ordinal);
        Assert.DoesNotContain("MinWidth=\"240\"", navigationTree, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentNavigationHeaderTemplate_SummaryTextBlock_WrapsText()
    {
        // Issue #1045: the header/tool summary TextBlocks must wrap so long descriptions (e.g. the
        // "github" MCP server description) no longer clip off the right edge.
        var axamlContent = ReadAxaml("AgentChatToolTemplates.axaml");

        var toolHeader = ExtractTemplate(axamlContent, "AgentToolHeaderTemplate");
        var toolSummary = ExtractSummaryTextBlock(toolHeader);
        Assert.Contains("TextWrapping=\"Wrap\"", toolSummary, StringComparison.Ordinal);

        var navHeader = ExtractTemplate(axamlContent, "AgentNavigationHeaderTemplate");
        var navSummary = ExtractSummaryTextBlock(navHeader);
        Assert.Contains("TextWrapping=\"Wrap\"", navSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void SubAgentItem_LastModified_RendersAgoLabel()
    {
        // Issue #1034: the sub-agent header template renders a relative "ago" label bound to
        // LastUpdatedAt. #1131: it now lives on its own row (Grid.Row="1"), right-aligned
        // beneath the name, so it cannot overlap the name/glyph.
        var axamlContent = ReadAxaml("AgentChatToolTemplates.axaml");

        var navHeader = ExtractTemplate(axamlContent, "AgentNavigationHeaderTemplate");

        Assert.Contains("controls:AgoTextBlock", navHeader, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding LastUpdatedAt}\"", navHeader, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Right\"", navHeader, StringComparison.Ordinal);
    }

    [Fact]
    public void SubAgentItem_WhenLastUpdatedNull_AgoLabelHidden()
    {
        // Issue #1034: the "ago" label is hidden when LastUpdatedAt is null.
        var axamlContent = ReadAxaml("AgentChatToolTemplates.axaml");

        var navHeader = ExtractTemplate(axamlContent, "AgentNavigationHeaderTemplate");
        var agoStart = navHeader.IndexOf("controls:AgoTextBlock", StringComparison.Ordinal);
        Assert.True(agoStart >= 0);
        var agoEnd = navHeader.IndexOf("/>", agoStart, StringComparison.Ordinal);
        var agoLabel = navHeader[agoStart..agoEnd];

        Assert.Contains(
            "IsVisible=\"{Binding LastUpdatedAt, Converter={x:Static converters:NotNullConverter.Instance}}\"",
            agoLabel,
            StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void SubAgentItem_AgoLabel_UsesDateTimeAgoConverter()
    {
        // Issue #1034: the "ago" label uses DateTimeAgoConverter (via the reusable AgoTextBlock
        // control, whose Value setter delegates to DateTimeAgoConverter).
        var converter = typeof(Phantom.Workspaces.Agent.Gui.Controls.AgoTextBlock)
            .Assembly
            .GetType("Phantom.Workspaces.Agent.Gui.Converters.DateTimeAgoConverter");
        Assert.NotNull(converter);

        var control = new Phantom.Workspaces.Agent.Gui.Controls.AgoTextBlock
        {
            Value = DateTime.UtcNow.AddHours(-2),
        };

        Assert.Equal("2 hours ago", control.Text);
    }

    [Fact]
    public void AgentNavigationHeaderTemplate_HeaderContent_StretchesToAvailableWidth()
    {
        // Issue #1045: the header content must stretch to the available tree width so its text wraps
        // rather than overflowing.
        var axamlContent = ReadAxaml("AgentChatToolTemplates.axaml");

        var navHeader = ExtractTemplate(axamlContent, "AgentNavigationHeaderTemplate");
        Assert.Contains("HorizontalAlignment=\"Stretch\"", navHeader, StringComparison.Ordinal);

        var toolHeader = ExtractTemplate(axamlContent, "AgentToolHeaderTemplate");
        Assert.Contains("HorizontalAlignment=\"Stretch\"", toolHeader, StringComparison.Ordinal);
    }

    private static string ExtractTemplate(string axamlContent, string templateKey)
    {
        var start = axamlContent.IndexOf($"x:Key=\"{templateKey}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected template '{templateKey}' to exist.");
        var end = axamlContent.IndexOf("</DataTemplate>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Expected template '{templateKey}' to be closed.");
        return axamlContent[start..end];
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatEditorControl_TreeColumn_HasNonZeroMinWidth()
    {
        // Issue #1051: when the tree is expanded, column 0 has a MinWidth >= 160 so a drag cannot
        // collapse the tree pane behind the native output surface.
        var control = new AgentChatEditorControl();
        var editorGrid = GetField<Grid>(control, "EditorGrid");
        SetTreeCollapsed(control, false);

        Assert.True(editorGrid.ColumnDefinitions[0].MinWidth >= 160);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatEditorControl_OutputColumn_HasMinWidth()
    {
        // Issue #1051: column 2 (HTML output) declares a positive MinWidth so a drag cannot
        // squeeze the output pane to ~0.
        var control = new AgentChatEditorControl();
        var editorGrid = GetField<Grid>(control, "EditorGrid");

        Assert.True(editorGrid.ColumnDefinitions[2].MinWidth >= 240);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatEditorControl_DraggingSplitter_CannotShrinkTreeBelowMinWidth()
    {
        // Issue #1051: simulate an extreme leftward drag by forcing the tree column tiny; the
        // Grid clamps its actual width to the column MinWidth.
        var control = new AgentChatEditorControl();
        var editorGrid = GetField<Grid>(control, "EditorGrid");
        SetTreeCollapsed(control, false);
        editorGrid.ColumnDefinitions[0].Width = new GridLength(2);

        _ = ShowInWindow(control, 1000, 600);

        Assert.True(editorGrid.ColumnDefinitions[0].ActualWidth >= 159.5);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatEditorControl_DraggingSplitter_CannotShrinkOutputBelowMinWidth()
    {
        // Issue #1051: simulate an extreme rightward drag by forcing the tree column to its max;
        // the output column's actual width is clamped to its MinWidth.
        var control = new AgentChatEditorControl();
        var editorGrid = GetField<Grid>(control, "EditorGrid");
        SetTreeCollapsed(control, false);
        editorGrid.ColumnDefinitions[0].Width = new GridLength(480);

        _ = ShowInWindow(control, 600, 600);

        Assert.True(editorGrid.ColumnDefinitions[2].ActualWidth >= 239.5);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatEditorControl_SplitterHost_RemainsHitTestableAfterExtremeDrag()
    {
        // Issue #1051: after an extreme drag the splitter stays visible in its fixed 24px column
        // with non-zero bounds (proxy for "still grabbable"; airspace overlap is a manual check).
        var control = new AgentChatEditorControl();
        var editorGrid = GetField<Grid>(control, "EditorGrid");
        var splitterHost = GetField<GridSplitter>(control, "SplitterHost");
        SetTreeCollapsed(control, false);
        editorGrid.ColumnDefinitions[0].Width = new GridLength(2);

        _ = ShowInWindow(control, 1000, 600);

        Assert.True(splitterHost.IsVisible);
        Assert.True(splitterHost.Bounds.Width > 0);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatEditorControl_OutputPane_CanBeRestoredAfterShrinkToMinimum()
    {
        // Issue #1051: after shrinking the output to its minimum, a reverse drag re-enlarges it,
        // proving the collapse is reversible.
        var control = new AgentChatEditorControl();
        var editorGrid = GetField<Grid>(control, "EditorGrid");
        SetTreeCollapsed(control, false);
        editorGrid.ColumnDefinitions[0].Width = new GridLength(480);

        _ = ShowInWindow(control, 600, 600);
        var shrunkOutput = editorGrid.ColumnDefinitions[2].ActualWidth;

        editorGrid.ColumnDefinitions[0].Width = new GridLength(200);
        editorGrid.InvalidateMeasure();
        editorGrid.InvalidateArrange();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var restoredOutput = editorGrid.ColumnDefinitions[2].ActualWidth;

        Assert.True(restoredOutput > shrunkOutput);
    }

    // ── #1131: last-modified timestamp must not overlap the id/name and state glyph ──

    [Fact]
    public void SubAgentItem_Header_UsesTwoRowLayout()
    {
        // #1131: The header Grid declares RowDefinitions="Auto,Auto" so the name/glyph
        // row and the timestamp row are laid out on separate vertical bounds.
        var axamlContent = ReadAxaml("AgentChatToolTemplates.axaml");
        var navHeader = ExtractTemplate(axamlContent, "AgentNavigationHeaderTemplate");

        Assert.Contains("RowDefinitions=\"Auto,Auto\"", navHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("ColumnDefinitions=\"*,Auto\"", navHeader, StringComparison.Ordinal);
    }

    [Fact]
    public void SubAgentItem_AgoLabel_OccupiesOwnRowBelowName()
    {
        // #1131: The AgoTextBlock is assigned Grid.Row="1"; the glyph+name StackPanel
        // sits on Grid.Row="0". They live on distinct rows, so the timestamp is on its
        // own row beneath the name/glyph row.
        var axamlContent = ReadAxaml("AgentChatToolTemplates.axaml");
        var navHeader = ExtractTemplate(axamlContent, "AgentNavigationHeaderTemplate");

        var agoStart = navHeader.IndexOf("controls:AgoTextBlock", StringComparison.Ordinal);
        Assert.True(agoStart >= 0);
        var agoEnd = navHeader.IndexOf("/>", agoStart, StringComparison.Ordinal);
        var agoElement = navHeader[agoStart..agoEnd];
        Assert.Contains("Grid.Row=\"1\"", agoElement, StringComparison.Ordinal);

        // The glyph+name StackPanel is on Grid.Row="0".
        var stackStart = navHeader.IndexOf("<StackPanel Grid.Row=\"0\"", StringComparison.Ordinal);
        Assert.True(stackStart >= 0, "Expected the glyph+name StackPanel to be on Grid.Row=\"0\".");
    }

    [Fact]
    public void SubAgentItem_NameTextBlock_IsNotEllipsisTruncated()
    {
        // #1131: The Name TextBlock does not declare TextTrimming="CharacterEllipsis";
        // long ids are shown in full (may wrap) rather than truncated.
        var axamlContent = ReadAxaml("AgentChatToolTemplates.axaml");
        var navHeader = ExtractTemplate(axamlContent, "AgentNavigationHeaderTemplate");

        var nameStart = navHeader.IndexOf("Text=\"{Binding Name}\"", StringComparison.Ordinal);
        Assert.True(nameStart >= 0);
        var nameEnd = navHeader.IndexOf("/>", nameStart, StringComparison.Ordinal);
        var nameElement = navHeader[nameStart..nameEnd];

        Assert.DoesNotContain("TextTrimming=\"CharacterEllipsis\"", nameElement, StringComparison.Ordinal);
        Assert.DoesNotContain("TextTrimming=\"CharacterEllipsis\"", navHeader, StringComparison.Ordinal);
    }

    [Fact]
    public void SubAgentItem_AgoLabel_StaysRightAlignedOnItsRow()
    {
        // #1131: The AgoTextBlock keeps HorizontalAlignment="Right" within its dedicated
        // second row so the timestamp visually sits at the lower-right of the item.
        var axamlContent = ReadAxaml("AgentChatToolTemplates.axaml");
        var navHeader = ExtractTemplate(axamlContent, "AgentNavigationHeaderTemplate");

        var agoStart = navHeader.IndexOf("controls:AgoTextBlock", StringComparison.Ordinal);
        Assert.True(agoStart >= 0);
        var agoEnd = navHeader.IndexOf("/>", agoStart, StringComparison.Ordinal);
        var agoElement = navHeader[agoStart..agoEnd];

        Assert.Contains("HorizontalAlignment=\"Right\"", agoElement, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"1\"", agoElement, StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SubAgentItem_LongName_NameAndAgoLabelOccupyDifferentRows()
    {
        // #1131: With a long agent id, the Name element and the AgoTextBlock render in
        // disjoint vertical bounds (name bottom edge ≤ timestamp top edge).
        var (window, nameBlock, agoBlock) = RenderNavHeader(
            "236153f8a2a14b7f901e35a11c09a427",
            DateTime.UtcNow.AddHours(-1),
            width: 260);

        Assert.True(nameBlock.Bounds.Height > 0);
        Assert.True(agoBlock.Bounds.Height > 0);
        Assert.True(
            nameBlock.Bounds.Bottom <= agoBlock.Bounds.Top + 0.5,
            $"Expected name bottom ({nameBlock.Bounds.Bottom}) to be at or above ago top ({agoBlock.Bounds.Top}).");

        window.Close();
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SubAgentItem_LongName_FullNameIsRenderedWithoutTruncation()
    {
        // #1131: With a long id and a constrained width, the full name text is preserved
        // (wraps within row 0) rather than being replaced by an ellipsis.
        var longName = "236153f8a2a14b7f901e35a11c09a427";
        var (window, nameBlock, _) = RenderNavHeader(longName, DateTime.UtcNow.AddMinutes(-5), width: 220);

        Assert.Equal(longName, nameBlock.Text);
        Assert.NotEqual(TextTrimming.CharacterEllipsis, nameBlock.TextTrimming);

        window.Close();
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SubAgentItem_ShortName_RendersWithTimestampOnSecondRow()
    {
        // #1131: A short name renders fully on row 0 with the AgoTextBlock on row 1
        // beneath it; no overlap and no unintended ellipsis.
        var (window, nameBlock, agoBlock) = RenderNavHeader("short", DateTime.UtcNow.AddMinutes(-2), width: 400);

        Assert.Equal("short", nameBlock.Text);
        Assert.True(nameBlock.Bounds.Height > 0);
        Assert.True(agoBlock.Bounds.Height > 0);
        Assert.True(
            nameBlock.Bounds.Bottom <= agoBlock.Bounds.Top + 0.5,
            $"Expected name bottom ({nameBlock.Bounds.Bottom}) to be at or above ago top ({agoBlock.Bounds.Top}).");
        Assert.NotEqual(TextTrimming.CharacterEllipsis, nameBlock.TextTrimming);

        window.Close();
    }

    private static (Window window, TextBlock name, Phantom.Workspaces.Agent.Gui.Controls.AgoTextBlock ago) RenderNavHeader(
        string name,
        DateTime lastUpdatedAt,
        double width)
    {
        // Materialize the AgentNavigationHeaderTemplate through an AgentChatEditorControl
        // whose Resources include AgentChatToolTemplates.axaml. We host the template's
        // built content in a ContentControl inside a Window so it is measured/arranged.
        var editor = new AgentChatEditorControl();
        Assert.True(
            editor.TryFindResource("AgentNavigationHeaderTemplate", out var resource),
            "Expected AgentNavigationHeaderTemplate resource on AgentChatEditorControl.");
        var template = Assert.IsAssignableFrom<Avalonia.Controls.Templates.IDataTemplate>(resource);

        var navItem = new AgentEditorNavigationItemViewModel(
            id: "n1",
            name: name,
            toolId: null,
            summary: "s",
            tool: null,
            detailContent: new object(),
            children: Array.Empty<AgentEditorNavigationItemViewModel>(),
            runningSubAgent: new HeaderStubSubAgent(lastUpdatedAt));

        var content = new ContentControl
        {
            Content = navItem,
            ContentTemplate = template,
            Width = width,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        };

        var window = new Window
        {
            Width = width + 40,
            Height = 300,
            Content = content,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        content.Measure(new Size(width, 300));
        content.Arrange(new Rect(0, 0, width, 300));
        Dispatcher.UIThread.RunJobs();

        var name0 = content.GetVisualDescendants()
            .OfType<TextBlock>()
            .First(tb => string.Equals(tb.Text, name, StringComparison.Ordinal));
        var ago = content.GetVisualDescendants()
            .OfType<Phantom.Workspaces.Agent.Gui.Controls.AgoTextBlock>()
            .First();

        return (window, name0, ago);
    }

    private sealed class HeaderStubSubAgent : IRunningSubAgent
    {
        public HeaderStubSubAgent(DateTime lastUpdatedAt)
        {
            this.LastUpdatedAt = lastUpdatedAt;
        }

        public string AgentId => "stub";
        public string DisplayName => "stub";
        public string Description => string.Empty;
        public AgentChatCompletionState CompletionState => AgentChatCompletionState.Running;
        public DateTime LastUpdatedAt { get; }
        public IReadOnlyList<IRunningSubAgent> SubAgents => Array.Empty<IRunningSubAgent>();
    }

    private static Window ShowInWindow(Control content, double width, double height)
    {
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = content,
        };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatEditorControl_SetTreeCollapsed_StillCollapsesTreeColumnToZero()
    {
        // Issue #1051: the programmatic collapse still drives column 0 to zero width by relaxing
        // its MinWidth to 0 during collapse.
        var control = new AgentChatEditorControl();
        var editorGrid = GetField<Grid>(control, "EditorGrid");

        SetTreeCollapsed(control, true);

        Assert.Equal(new GridLength(0), editorGrid.ColumnDefinitions[0].Width);
        Assert.Equal(0, editorGrid.ColumnDefinitions[0].MinWidth);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentChatEditorControl_TreeColumn_MinWidthRestoredOnExpand()
    {
        // Issue #1051: expanding restores the 160px floor so drag-clamping is active whenever the
        // tree is shown.
        var control = new AgentChatEditorControl();
        var editorGrid = GetField<Grid>(control, "EditorGrid");

        SetTreeCollapsed(control, true);
        SetTreeCollapsed(control, false);

        Assert.True(editorGrid.ColumnDefinitions[0].MinWidth >= 160);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task AgentChatEditorControl_SubAgentChatDetailChild_RendersNonBlankDetail()
    {
        // Issue #1035 render regression: selecting a sub-agent's own chat-details child node must
        // render the sub-agent's populated AgentChatDetailsViewModel through the locked DockControl —
        // i.e. the detail region is NOT blank (the original bug), and it shows the SUB-AGENT's session.
        var chat = await CreateAgentChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);
        await AddSubAgentAsync(chat, "a1", "Sub Agent");

        var root = viewModel.EditorItems.Single();
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");
        var subAgentNavItem = subAgentsNode.Children.Single(c => c.Id == "sub-agent-a1");
        var subChatDetails = subAgentNavItem.Children.Single(c => c.Id == "chat-details");
        var details = (AgentChatDetailsViewModel)subChatDetails.DetailContent!;

        viewModel.SelectedEditorItem = subChatDetails;

        var control = new AgentChatEditorControl { DataContext = viewModel };
        _ = ShowInWindow(control, 1000, 700);
        Dispatcher.UIThread.RunJobs();

        // The chat-details template renders a read-only TextBox bound to the sub-agent's session id.
        var renderedTexts = control.GetVisualDescendants()
            .OfType<TextBox>()
            .Select(tb => tb.Text)
            .ToList();
        Assert.Contains(details.AgentSessionId, renderedTexts);
    }

    private static Task<AgentChat> CreateAgentChatAsync()
        => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
                    """
                    {
                      "kind": "prompt",
                      "name": "test-agent",
                      "model": { "id": "test", "provider": "echo", "apiType": "Echo" },
                      "tools": []
                    }
                    """),
            });

    private static async Task AddSubAgentAsync(AgentChat chat, string agentId, string displayName)
    {
        var definition = AgentDefinitionLoader.LoadAgentFromJson(
            $$"""
            {
              "kind": "prompt",
              "name": "{{displayName}}",
              "model": { "id": "test", "provider": "echo", "apiType": "Echo" },
              "tools": []
            }
            """);
        await chat.GetOrCreateAsync(agentId, definition, $"tool-call-{agentId}");
    }

    private static void SetTreeCollapsed(AgentChatEditorControl control, bool collapsed)
    {
        var method = control.GetType().GetMethod(
            "SetTreeCollapsed",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find SetTreeCollapsed method.");
        method.Invoke(control, [collapsed]);
    }

    private static string ExtractSummaryTextBlock(string templateXaml)
    {
        var start = templateXaml.IndexOf("Text=\"{Binding Summary}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected a Summary TextBlock in the template.");
        var end = templateXaml.IndexOf("/>", start, StringComparison.Ordinal);
        Assert.True(end > start, "Expected the Summary TextBlock to be closed.");
        return templateXaml[start..end];
    }

    private static string ReadAxaml(string fileName)
    {
        var repositoryRoot = FindRepositoryRoot();
        var filePath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Agent.Gui",
            "Controls",
            fileName);

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

    [Fact]
    public void AgentChatEditorControl_Collapser_ShowsChevronGlyphWhenExpanded()
    {
        // #1120: expanded (unchecked) state renders "<<" via the shared pane-collapser style.
        // The AgentChatEditorControl toggle now delegates its glyph to the shared style rather
        // than a plain single-char Content, and no longer carries the old "◀" default.
        var axaml = ReadAxaml("AgentChatEditorControl.axaml");

        Assert.Contains("x:Name=\"TreeCollapseToggle\"", axaml, StringComparison.Ordinal);
        var toggleStart = axaml.IndexOf("x:Name=\"TreeCollapseToggle\"", StringComparison.Ordinal);
        var toggleEnd = axaml.IndexOf("/>", toggleStart, StringComparison.Ordinal);
        var toggleXaml = axaml.Substring(toggleStart, toggleEnd - toggleStart);
        Assert.Contains("Classes=\"pane-collapser\"", toggleXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"◀\"", toggleXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"▶\"", toggleXaml, StringComparison.Ordinal);

        var sharedStyles = ReadSharedStyles();
        // Expanded (base) state ⇒ "<<".
        Assert.Contains("&lt;&lt;", sharedStyles, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentChatEditorControl_Collapser_ShowsReversedChevronWhenCollapsed()
    {
        // #1120: collapsed (:checked) state renders ">>". The state trigger lives in the shared
        // pane-collapser style so both collapsers switch identically.
        var sharedStyles = ReadSharedStyles();
        Assert.Contains("ToggleButton.pane-collapser:checked", sharedStyles, StringComparison.Ordinal);
        Assert.Contains("&gt;&gt;", sharedStyles, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentChatEditorControl_Collapser_GlyphForegroundIsThematicWhite()
    {
        // #1120: the pane-collapser glyph resolves to Theme.Collapser.Glyph.Foreground, and that
        // brush key is defined as white (#FFFFFF) in BOTH the Light and Dark theme dictionaries.
        var sharedStyles = ReadSharedStyles();
        Assert.Contains("Theme.Collapser.Glyph.Foreground", sharedStyles, StringComparison.Ordinal);

        foreach (var theme in new[] { "Light.axaml", "Dark.axaml" })
        {
            var themeXaml = ReadSharedTheme(theme);
            Assert.Contains("Theme.Collapser.Glyph.Foreground", themeXaml, StringComparison.Ordinal);
            var keyStart = themeXaml.IndexOf("Theme.Collapser.Glyph.Foreground", StringComparison.Ordinal);
            var elementEnd = themeXaml.IndexOf("</SolidColorBrush>", keyStart, StringComparison.Ordinal);
            Assert.True(elementEnd > keyStart, $"Missing brush closing tag in {theme}.");
            var element = themeXaml.Substring(keyStart, elementEnd - keyStart);
            Assert.Contains("#FFFFFF", element, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AgentChatEditorControl_Collapser_CodeBehind_DoesNotSwapGlyphManually()
    {
        // #1120: the "▶"/"◀" glyph swap in code-behind is replaced by the shared style's :checked
        // state trigger, so SetTreeCollapsed no longer touches TreeCollapseToggle.Content.
        var repoRoot = FindRepositoryRoot();
        var codeBehind = File.ReadAllText(Path.Combine(
            repoRoot.FullName,
            "Phantom.Workspaces.Agent.Gui",
            "Controls",
            "AgentChatEditorControl.axaml.cs"));

        Assert.DoesNotContain("TreeCollapseToggle.Content =", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("\"▶\"", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("\"◀\"", codeBehind, StringComparison.Ordinal);
    }

    private static string ReadSharedStyles()
    {
        var repositoryRoot = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Gui.Shared",
            "Styles",
            "SharedStyles.axaml"));
    }

    private static string ReadSharedTheme(string themeFileName)
    {
        var repositoryRoot = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Gui.Shared",
            "Themes",
            themeFileName));
    }

    private static T GetField<T>(object instance, string fieldName) where T : class
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Could not find field '{fieldName}'.");

        return Assert.IsAssignableFrom<T>(field.GetValue(instance));
    }

    // --- #1124 top-level dock-tab-switch adoption tests for the detail dock ---

    [Fact]
    public void AgentChatEditorControl_Root_DeclaresTabSwitchingNamespace()
    {
        // #1124 adoption: the UserControl root declares xmlns:ts pointing at the tab-switching
        // submodule so ts:DockTabSwitch.* attached properties resolve. No Window/UserControl-level
        // target property is declared — the opt-in lives on the DockControl itself.
        var axaml = ReadAxaml("AgentChatEditorControl.axaml");

        Assert.Contains(
            "xmlns:ts=\"using:Phantom.Dock.Avalonia.TabSwitching\"",
            axaml,
            StringComparison.Ordinal);

        // No Window/UserControl-level TargetDockControl-style property (rejected NameScope design).
        Assert.DoesNotContain("ts:DockTabSwitch.TargetDockControl", axaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentChatEditorControl_DetailDock_OptsIntoInstallOnTopLevelWithCtrlAltDigits()
    {
        // #1124 adoption: the detail DockControl carries Enabled=True + InstallOnTopLevel=True and
        // a Ctrl+Alt+Digits AllSwitchable binding (a distinct chord from MainWindow's Alt+Shift+
        // Digits so the two co-hosted docks do not collide).
        var axaml = ReadAxaml("AgentChatEditorControl.axaml");

        // The three flags live on the same DockControl declaration — locate the detail dock by
        // its Layout binding then assert its opening element contains all three attributes.
        var detailStart = axaml.IndexOf("Layout=\"{Binding DetailLayout}\"", StringComparison.Ordinal);
        Assert.True(detailStart >= 0, "Expected detail DockControl to bind Layout=DetailLayout.");
        var openTagEnd = axaml.IndexOf('>', detailStart);
        Assert.True(openTagEnd > detailStart);
        var openTag = axaml.Substring(detailStart, openTagEnd - detailStart);

        Assert.Contains("ts:DockTabSwitch.Enabled=\"True\"", openTag, StringComparison.Ordinal);
        Assert.Contains("ts:DockTabSwitch.InstallOnTopLevel=\"True\"", openTag, StringComparison.Ordinal);

        // The Ctrl+Alt+Digits binding is declared as a child <ts:DockTabSwitchGestures/> element.
        var bindingsStart = axaml.IndexOf("<ts:DockTabSwitchBindings>", detailStart, StringComparison.Ordinal);
        Assert.True(bindingsStart > detailStart, "Expected <ts:DockTabSwitchBindings> under the detail dock.");
        var bindingsEnd = axaml.IndexOf("</ts:DockTabSwitchBindings>", bindingsStart, StringComparison.Ordinal);
        Assert.True(bindingsEnd > bindingsStart);
        var bindingsXaml = axaml.Substring(bindingsStart, bindingsEnd - bindingsStart);

        Assert.Contains("Modifiers=\"Control,Alt\"", bindingsXaml, StringComparison.Ordinal);
        Assert.Contains("Keys=\"Digits\"", bindingsXaml, StringComparison.Ordinal);
        Assert.Contains("Scope=\"AllSwitchable\"", bindingsXaml, StringComparison.Ordinal);
    }

    private static global::Dock.Avalonia.Controls.DockControl GetDetailDockControl(AgentChatEditorControl control)
    {
        var editorGrid = GetField<Grid>(control, "EditorGrid");
        return editorGrid.Children
            .OfType<global::Dock.Avalonia.Controls.DockControl>()
            .First(d => Grid.GetColumn(d) == 2);
    }

    private static async Task<AgentViewModel> CreateAgentViewModelAsync()
    {
        var chat = await CreateAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        return new AgentViewModel(chat, "parent", string.Empty, loggerFactory, TaskScheduler.Default);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentChatEditorControl_CtrlAltDigitWithFocusOutsideDetailDock_SwitchesDetailDocument()
    {
        // #1124 adoption: with the event source on the NavigationTree (outside the detail
        // DockControl subtree), Ctrl+Alt+2 activates the second cached AgentDetailDocument. The
        // top-level-installed tunnel handler catches the chord regardless of focus.
        await using var viewModel = await CreateAgentViewModelAsync();

        var control = new AgentChatEditorControl { DataContext = viewModel };
        var window = ShowInWindow(control, 1000, 700);
        Dispatcher.UIThread.RunJobs();

        var detailDock = GetDetailDockControl(control);
        var documents = viewModel.DetailDockFactory.DetailDock.VisibleDockables!
            .OfType<Phantom.Workspaces.Agent.Gui.ViewModels.AgentDetailDocument>()
            .ToList();
        Assert.True(documents.Count >= 2);

        // Ensure the tree is outside the detail dock subtree.
        var tree = control.GetVisualDescendants()
            .OfType<TreeView>()
            .First();
        Assert.DoesNotContain(detailDock.GetVisualDescendants(), v => ReferenceEquals(v, tree));

        window.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
            Key = Avalonia.Input.Key.D2,
            KeyModifiers = Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Alt,
            Source = tree,
        });

        Assert.Same(documents[1], viewModel.DetailDockFactory.DetailDock.ActiveDockable);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentChatEditorControl_CtrlAltDigit_ActivatesDetailDocumentExactlyOnce()
    {
        // #1124 adoption: with InstallOnTopLevel the in-control tunnel handler is suppressed on
        // the detail DockControl, so a single Ctrl+Alt+1 chord causes exactly one
        // SetActiveDockable on the target detail document (no double-handling).
        await using var viewModel = await CreateAgentViewModelAsync();

        var control = new AgentChatEditorControl { DataContext = viewModel };
        var window = ShowInWindow(control, 1000, 700);
        Dispatcher.UIThread.RunJobs();

        var detailDock = viewModel.DetailDockFactory.DetailDock;
        var documents = detailDock.VisibleDockables!
            .OfType<Phantom.Workspaces.Agent.Gui.ViewModels.AgentDetailDocument>()
            .ToList();
        Assert.True(documents.Count >= 2);

        // Start on document 2 so activating document 1 is an observable transition.
        viewModel.DetailDockFactory.SetActiveDockable(documents[1]);
        Assert.Same(documents[1], detailDock.ActiveDockable);

        var activationCount = 0;
        void Handler(object? _, global::Dock.Model.Core.Events.ActiveDockableChangedEventArgs e)
        {
            if (ReferenceEquals(e.Dockable, documents[0]))
            {
                activationCount++;
            }
        }
        viewModel.DetailDockFactory.ActiveDockableChanged += Handler;
        try
        {
            var tree = control.GetVisualDescendants().OfType<TreeView>().First();
            window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
                Key = Avalonia.Input.Key.D1,
                KeyModifiers = Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Alt,
                Source = tree,
            });
        }
        finally
        {
            viewModel.DetailDockFactory.ActiveDockableChanged -= Handler;
        }

        Assert.Equal(1, activationCount);
        Assert.Same(documents[0], detailDock.ActiveDockable);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentChatEditorControl_DetailDockTransitivelyInvisible_DoesNotSwitch()
    {
        // #1124 adoption: the IsEffectivelyVisible gate blocks activation when an ancestor of
        // the detail dock is hidden. Ctrl+Alt+2 must be a no-op in that state.
        await using var viewModel = await CreateAgentViewModelAsync();

        var control = new AgentChatEditorControl { DataContext = viewModel };
        var window = ShowInWindow(control, 1000, 700);
        Dispatcher.UIThread.RunJobs();

        var detailDock = viewModel.DetailDockFactory.DetailDock;
        var documents = detailDock.VisibleDockables!
            .OfType<Phantom.Workspaces.Agent.Gui.ViewModels.AgentDetailDocument>()
            .ToList();
        Assert.True(documents.Count >= 2);

        // Start on document 1.
        viewModel.DetailDockFactory.SetActiveDockable(documents[0]);
        Assert.Same(documents[0], detailDock.ActiveDockable);

        // Hide the host control (an ancestor of the detail DockControl) → IsEffectivelyVisible=false.
        control.IsVisible = false;
        Dispatcher.UIThread.RunJobs();

        var detailControl = GetDetailDockControl(control);
        Assert.False(detailControl.IsEffectivelyVisible);

        window.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
            Key = Avalonia.Input.Key.D2,
            KeyModifiers = Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Alt,
            Source = window,
        });

        Assert.Same(documents[0], detailDock.ActiveDockable);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentChatEditorControl_ReparentedBetweenHosts_RebindsHookToCurrentTopLevel()
    {
        // #1124 adoption: because AgentChatEditorControl can move between the embedded workspace
        // host and the standalone agent window, the detail dock's controller must rebind its
        // top-level tunnel hook whenever the control's hosting TopLevel changes. Behaviourally:
        // a gesture on the old TopLevel no longer activates; a gesture on the new one does.
        await using var viewModel = await CreateAgentViewModelAsync();

        var control = new AgentChatEditorControl { DataContext = viewModel };
        var hostA = new Window { Width = 1000, Height = 700, Content = control };
        hostA.Show();
        Dispatcher.UIThread.RunJobs();

        var detailDock = viewModel.DetailDockFactory.DetailDock;
        var documents = detailDock.VisibleDockables!
            .OfType<Phantom.Workspaces.Agent.Gui.ViewModels.AgentDetailDocument>()
            .ToList();
        Assert.True(documents.Count >= 3);

        viewModel.DetailDockFactory.SetActiveDockable(documents[0]);
        Assert.Same(documents[0], detailDock.ActiveDockable);

        // Move the control to a fresh window.
        hostA.Content = null;
        Dispatcher.UIThread.RunJobs();

        var hostB = new Window { Width = 1000, Height = 700, Content = control };
        hostB.Show();
        Dispatcher.UIThread.RunJobs();

        // Gesture on the OLD TopLevel must NOT activate — the hook rebound off it.
        hostA.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
            Key = Avalonia.Input.Key.D2,
            KeyModifiers = Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Alt,
            Source = hostA,
        });
        Assert.Same(documents[0], detailDock.ActiveDockable);

        // Gesture on the NEW TopLevel activates the target document via the rebound hook.
        hostB.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
            Key = Avalonia.Input.Key.D3,
            KeyModifiers = Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Alt,
            Source = hostB,
        });
        Assert.Same(documents[2], detailDock.ActiveDockable);
    }

    // --- #1111 VM→tree selection sync tests --------------------------------

    [AvaloniaFact(Timeout = 30_000)]
    public async Task SelectedEditorItem_ProgrammaticSet_MarksMatchingTreeViewItemIsSelected()
    {
        // Issue #1111: setting AgentViewModel.SelectedEditorItem programmatically must drive
        // the realised TreeViewItem for that item to IsSelected == true (via the SelectedItem
        // two-way binding + ancestor expansion). Without the fix TreeViewItem.IsSelected stayed
        // false because the tree's selection was only ever pushed view→VM.
        var chat = await CreateAgentChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);
        await AddSubAgentAsync(chat, "a1", "Sub Agent");

        var root = viewModel.EditorItems.Single();
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");
        var subAgentNavItem = subAgentsNode.Children.Single(c => c.Id == "sub-agent-a1");

        var control = new AgentChatEditorControl { DataContext = viewModel };
        SetTreeCollapsed(control, false);
        _ = ShowInWindow(control, 1000, 700);
        Dispatcher.UIThread.RunJobs();

        viewModel.SelectedEditorItem = subAgentNavItem;
        Dispatcher.UIThread.RunJobs();

        var tree = control.GetVisualDescendants().OfType<TreeView>().First();
        var container = tree.GetVisualDescendants()
            .OfType<TreeViewItem>()
            .FirstOrDefault(tvi => ReferenceEquals(tvi.DataContext, subAgentNavItem));

        Assert.NotNull(container);
        Assert.True(container!.IsSelected,
            "Expected the TreeViewItem for the programmatically-selected sub-agent nav item to be IsSelected==true (issue #1111).");
        Assert.Same(subAgentNavItem, tree.SelectedItem);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task SelectedEditorItem_SubAgentNavigated_HighlightsSubAgentCardBlue()
    {
        // Issue #1111 headless-render variant: after a programmatic selection change, the previously
        // selected TreeViewItem must lose IsSelected and the newly selected sub-agent's realised
        // TreeViewItem must gain IsSelected. The blue recolour is scoped to
        // StackPanel.entity-card-tree-item.selected (SharedStyles.axaml:325) where Classes.selected
        // is bound to the ancestor TreeViewItem.IsSelected — SharedStylesTests already covers that
        // final styling step, so proving IsSelected transitions correctly here is exactly what
        // "the card turns blue on programmatic selection" reduces to.
        var chat = await CreateAgentChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);
        await AddSubAgentAsync(chat, "a1", "Sub Agent");

        var root = viewModel.EditorItems.Single();
        var subAgentNavItem = root.Children.Single(c => c.Id == "chat-sub-agents")
            .Children.Single(c => c.Id == "sub-agent-a1");

        // Start with the initial (root) selection so we can observe a transition.
        viewModel.SelectedEditorItem = root;

        var control = new AgentChatEditorControl { DataContext = viewModel };
        SetTreeCollapsed(control, false);
        _ = ShowInWindow(control, 1000, 700);
        Dispatcher.UIThread.RunJobs();

        var tree = control.GetVisualDescendants().OfType<TreeView>().First();
        var rootContainer = tree.GetVisualDescendants()
            .OfType<TreeViewItem>()
            .First(tvi => ReferenceEquals(tvi.DataContext, root));
        Assert.True(rootContainer.IsSelected, "Baseline: root container must start selected.");

        // Programmatic navigation to the sub-agent — the same VM state change the jump-button
        // ultimately drives via NavigateToAgentHandler → NavigateToSubAgent.
        viewModel.SelectedEditorItem = subAgentNavItem;
        Dispatcher.UIThread.RunJobs();

        var subContainer = tree.GetVisualDescendants()
            .OfType<TreeViewItem>()
            .FirstOrDefault(tvi => ReferenceEquals(tvi.DataContext, subAgentNavItem));

        Assert.NotNull(subContainer);
        Assert.True(subContainer!.IsSelected,
            "Expected the sub-agent's TreeViewItem to be IsSelected after programmatic navigation (issue #1111 blue-highlight).");
        Assert.False(rootContainer.IsSelected, "Expected the root container to lose IsSelected when selection moved.");
        Assert.Same(subAgentNavItem, tree.SelectedItem);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task SelectedEditorItem_ProgrammaticSet_ExpandsAncestorsSoContainerRealises()
    {
        // Issue #1111 edge/error variant: even when the root nav item starts collapsed
        // (isExpanded:false on construction), setting SelectedEditorItem to a deeply-nested
        // sub-agent nav item must expand every ancestor so the target's TreeViewItem is realised
        // and can carry IsSelected. Without ancestor expansion the container never materialises
        // and no highlight can be shown.
        var chat = await CreateAgentChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);
        await AddSubAgentAsync(chat, "a1", "Sub Agent");

        var root = viewModel.EditorItems.Single();
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");
        var subAgentNavItem = subAgentsNode.Children.Single(c => c.Id == "sub-agent-a1");

        // Force the root collapsed to prove ancestor expansion kicks in.
        root.IsExpanded = false;

        var control = new AgentChatEditorControl { DataContext = viewModel };
        SetTreeCollapsed(control, false);
        _ = ShowInWindow(control, 1000, 700);
        Dispatcher.UIThread.RunJobs();

        viewModel.SelectedEditorItem = subAgentNavItem;
        Dispatcher.UIThread.RunJobs();

        Assert.True(root.IsExpanded, "Root nav item ancestor should have been expanded.");
        Assert.True(subAgentsNode.IsExpanded, "Sub-agents-group ancestor should have been expanded.");

        var tree = control.GetVisualDescendants().OfType<TreeView>().First();
        var container = tree.GetVisualDescendants()
            .OfType<TreeViewItem>()
            .FirstOrDefault(tvi => ReferenceEquals(tvi.DataContext, subAgentNavItem));

        Assert.NotNull(container);
        Assert.True(container!.IsSelected);
    }
}
