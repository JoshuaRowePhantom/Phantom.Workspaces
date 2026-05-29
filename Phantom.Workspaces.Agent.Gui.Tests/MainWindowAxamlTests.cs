namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class MainWindowAxamlTests
{
    [Fact]
    public void MainWindow_UsesBinding_ForChildControlDataContexts()
    {
        var mainWindowContent = ReadMainWindowAxaml();

        Assert.Contains(
            "<controls:AgentChatEditorControl Grid.Column=\"0\"\r\n                                             DataContext=\"{Binding Agent}\"/>",
            mainWindowContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "<controls:AgentChatInputQueueControl DockPanel.Dock=\"Bottom\"\r\n                                                     DataContext=\"{Binding Agent.InputQueue}\"/>",
            mainWindowContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "<controls:AgentChatOutputControl DataContext=\"{Binding Agent}\"/>",
            mainWindowContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AgentSessionIdTextBox_IsReadOnly_AndOneWay()
    {
        var editorControlContent = ReadAxaml("AgentChatEditorControl.axaml");

        Assert.Contains(
            "<TextBox Text=\"{Binding AgentSessionId, Mode=OneWay}\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsReadOnly=\"True\"",
            editorControlContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AgentChatEditorControl_ShowsToolTreeWithEnableToggle()
    {
        var editorControlContent = ReadAxaml("AgentChatEditorControl.axaml");

        Assert.Contains(
            "<TreeView ItemsSource=\"{Binding Tools}\">",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Click=\"OnToolToggleClicked\"",
            editorControlContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "<TextBlock Text=\"{Binding Status}\"",
            editorControlContent,
            StringComparison.Ordinal);
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
}
