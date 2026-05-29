namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class MainWindowAxamlTests
{
    [Fact]
    public void MainWindow_UsesBinding_ForChildControlDataContexts()
    {
        var mainWindowContent = ReadMainWindowAxaml();

        Assert.Contains(
            "DataContext=\"{Binding Agent.InputQueue}\"",
            mainWindowContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "DataContext=\"{Binding Agent}\"",
            mainWindowContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AgentSessionIdTextBox_IsReadOnly_AndOneWay()
    {
        var mainWindowContent = ReadMainWindowAxaml();

        Assert.Contains(
            "<TextBox Text=\"{Binding Agent.AgentSessionId, Mode=OneWay}\"",
            mainWindowContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsReadOnly=\"True\"",
            mainWindowContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ChildControls_DisableCompiledBindings_ForRuntimeDataContextHandoff()
    {
        var inputQueueControlContent = ReadAxaml("AgentInputQueueControl.axaml");
        var outputControlContent = ReadAxaml("ChatAgentOutputControl.axaml");

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
