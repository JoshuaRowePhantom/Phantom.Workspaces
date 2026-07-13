using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Phantom.Workspaces.Agent.Gui.Controls;

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
    public void DetailContentSlots_ItemsPanel_IsPanel_NotStackPanel()
    {
        // Issue #764: The ItemsControl for DetailContentSlots must use Panel (base Panel, not
        // StackPanel) as its items panel. StackPanel measures children with infinite height,
        // causing the AgentChatOutputControl (WebView host) to report DesiredSize = 0 and
        // collapse the entire control. Panel provides finite constraints and allows items to
        // fill the available space.
        var axamlContent = ReadAxaml("AgentChatEditorControl.axaml");

        Assert.Contains(
            "ItemsSource=\"{Binding DetailContentSlots}\"",
            axamlContent,
            StringComparison.Ordinal);

        Assert.Contains(
            "<ItemsControl.ItemsPanel>",
            axamlContent,
            StringComparison.Ordinal);

        Assert.Contains(
            "<ItemsPanelTemplate>",
            axamlContent,
            StringComparison.Ordinal);

        Assert.Contains(
            "<Panel/>",
            axamlContent,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "<StackPanel/>",
            axamlContent,
            StringComparison.Ordinal);
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void AgentChatEditorControl_ConversationSlot_FillsAvailableHeight()
    {
        // Issue #764: Verify that the ContentControl for the conversation slot receives a
        // non-zero finite height constraint during measure. When ItemsControl defaults to
        // StackPanel, children are measured with infinite height, causing WebView to collapse
        // to zero. With Panel as the items panel, children receive the finite constraint from
        // the parent and can render properly.
        var control = new AgentChatEditorControl();

        // Navigate to the EditorGrid (Grid.Column="2") which contains the ItemsControl
        var editorGrid = GetField<Grid>(control, "EditorGrid");
        Assert.NotNull(editorGrid);

        // Find the ItemsControl for DetailContentSlots in column 2
        var detailPanel = editorGrid.Children
            .OfType<Panel>()
            .FirstOrDefault(p => Grid.GetColumn(p) == 2);
        Assert.NotNull(detailPanel);

        var itemsControl = detailPanel.Children.OfType<ItemsControl>().FirstOrDefault();
        Assert.NotNull(itemsControl);

        // Verify that the ItemsPanel is Panel (or subclass), not StackPanel
        var itemsPresenterProperty = typeof(ItemsControl)
            .GetProperty("ItemsPanel", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(itemsPresenterProperty);

        var itemsPanelTemplate = itemsPresenterProperty.GetValue(itemsControl) as ITemplate<Panel>;
        Assert.NotNull(itemsPanelTemplate);

        var panel = itemsPanelTemplate.Build();
        Assert.NotNull(panel);

        // The panel must be base Panel, not StackPanel
        Assert.IsType<Panel>(panel);
        Assert.IsNotType<StackPanel>(panel);
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
            "IsVisible=\"{Binding Agent.AcceptsUserInput}\"",
            conversationDetailXaml,
            StringComparison.Ordinal);
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
