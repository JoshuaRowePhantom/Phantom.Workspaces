using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Phantom.Workspaces.Agent.Gui.Controls;

namespace Phantom.Workspaces.Agent.Gui.Tests;

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
            "<FlowDocumentScrollViewer",
            outputControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "<TextBox x:Name=\"SelectableOutputText\"",
            outputControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Gesture=\"Cancel\" Command=\"{Binding InterruptCommand}\"",
            outputControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Gesture=\"Pause\" Command=\"{Binding InputQueue.ToggleHoldAllQueuesCommand}\"",
            outputControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Gesture=\"Shift+Pause\" Command=\"{Binding InputQueue.HoldAllQueuesCommand}\"",
            outputControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Gesture=\"Ctrl+Shift+Pause\" Command=\"{Binding InputQueue.UnholdAllQueuesCommand}\"",
            outputControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "OutputMode=\"SelectableTextBox\"",
            editorControlContent,
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
