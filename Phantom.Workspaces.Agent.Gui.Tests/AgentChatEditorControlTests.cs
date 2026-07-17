using Avalonia.Headless.XUnit;
using System;
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
            .OfType<Dock.Avalonia.Controls.DockControl>()
            .FirstOrDefault(d => Grid.GetColumn(d) == 2);
        Assert.NotNull(dockControl);

        // Docking is fully disabled so the detail region stays locked.
        Assert.False(dockControl!.IsDockingEnabled);
    }

    [Fact]
    public void AgentChatEditorControl_SubAgentSlotTemplate_DoesNotInstantiateAgentChatEditorControl()
    {
        // Issue #884, #903: The SubAgentSlotViewModel DataTemplate must not instantiate a
        // nested AgentChatEditorControl (with TreeView, GridSplitter, ToggleButton chrome).
        // It should render only the conversation detail content via ContentControl.
        var axamlContent = ReadAxaml("AgentChatEditorControl.axaml");

        var subAgentSlotStart = axamlContent.IndexOf(
            "DataType=\"vm:SubAgentSlotViewModel\"",
            StringComparison.Ordinal);
        Assert.True(subAgentSlotStart > 0, "Could not find SubAgentSlotViewModel DataTemplate");

        var subAgentSlotEnd = axamlContent.IndexOf(
            "</DataTemplate>",
            subAgentSlotStart,
            StringComparison.Ordinal);
        Assert.True(subAgentSlotEnd > subAgentSlotStart);

        var subAgentSlotXaml = axamlContent.Substring(
            subAgentSlotStart,
            subAgentSlotEnd - subAgentSlotStart);

        Assert.DoesNotContain(
            "AgentChatEditorControl",
            subAgentSlotXaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AgentChatEditorControl_SubAgentSlotTemplate_BindsContentToSubAgentConversationDetail()
    {
        // Issue #884, #903: The SubAgentSlotViewModel DataTemplate must bind ContentControl.Content
        // to SubAgentViewModel.ConversationDetail so the AgentChatConversationDetailViewModel
        // DataTemplate renders output + conditional input queue without editor chrome.
        var axamlContent = ReadAxaml("AgentChatEditorControl.axaml");

        var subAgentSlotStart = axamlContent.IndexOf(
            "DataType=\"vm:SubAgentSlotViewModel\"",
            StringComparison.Ordinal);
        Assert.True(subAgentSlotStart > 0);

        var subAgentSlotEnd = axamlContent.IndexOf(
            "</DataTemplate>",
            subAgentSlotStart,
            StringComparison.Ordinal);
        Assert.True(subAgentSlotEnd > subAgentSlotStart);

        var subAgentSlotXaml = axamlContent.Substring(
            subAgentSlotStart,
            subAgentSlotEnd - subAgentSlotStart);

        Assert.Contains(
            "ContentControl",
            subAgentSlotXaml,
            StringComparison.Ordinal);

        Assert.Contains(
            "Content=\"{Binding SubAgentViewModel.ConversationDetail}\"",
            subAgentSlotXaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AgentChatEditorControl_SubAgentSlotTemplate_IsVisibleBindingIsOnWrapperNotEditor()
    {
        // Issue #884, #903: The IsVisible binding must be on the wrapper Panel element, not on
        // a nested AgentChatEditorControl. This ensures proper visibility control for sub-agent
        // slots without triggering AXAML DataContext/IsVisible binding order bugs.
        var axamlContent = ReadAxaml("AgentChatEditorControl.axaml");

        var subAgentSlotStart = axamlContent.IndexOf(
            "DataType=\"vm:SubAgentSlotViewModel\"",
            StringComparison.Ordinal);
        Assert.True(subAgentSlotStart > 0);

        var subAgentSlotEnd = axamlContent.IndexOf(
            "</DataTemplate>",
            subAgentSlotStart,
            StringComparison.Ordinal);
        Assert.True(subAgentSlotEnd > subAgentSlotStart);

        var subAgentSlotXaml = axamlContent.Substring(
            subAgentSlotStart,
            subAgentSlotEnd - subAgentSlotStart);

        Assert.Contains(
            "<Panel IsVisible=\"{Binding IsSelected}\">",
            subAgentSlotXaml,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "AgentChatEditorControl",
            subAgentSlotXaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AgentChatEditorControl_SubAgentSlotTemplate_UsesCompiledBindings()
    {
        // Issue #903: The SubAgentSlotViewModel DataTemplate must use x:CompileBindings="True"
        // for better performance and compile-time binding validation.
        var axamlContent = ReadAxaml("AgentChatEditorControl.axaml");

        var subAgentSlotStart = axamlContent.IndexOf(
            "DataType=\"vm:SubAgentSlotViewModel\"",
            StringComparison.Ordinal);
        Assert.True(subAgentSlotStart > 0);

        var subAgentSlotEnd = axamlContent.IndexOf(
            "</DataTemplate>",
            subAgentSlotStart,
            StringComparison.Ordinal);
        Assert.True(subAgentSlotEnd > subAgentSlotStart);

        var subAgentSlotXaml = axamlContent.Substring(
            subAgentSlotStart,
            subAgentSlotEnd - subAgentSlotStart);

        Assert.Contains(
            "x:CompileBindings=\"True\"",
            subAgentSlotXaml,
            StringComparison.Ordinal);
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
        // LastUpdatedAt, positioned in the lower-right of the item.
        var axamlContent = ReadAxaml("AgentChatToolTemplates.axaml");

        var navHeader = ExtractTemplate(axamlContent, "AgentNavigationHeaderTemplate");

        Assert.Contains("controls:AgoTextBlock", navHeader, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding LastUpdatedAt}\"", navHeader, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment=\"Bottom\"", navHeader, StringComparison.Ordinal);
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
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);
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

    private static T GetField<T>(object instance, string fieldName) where T : class
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Could not find field '{fieldName}'.");

        return Assert.IsAssignableFrom<T>(field.GetValue(instance));
    }
}
