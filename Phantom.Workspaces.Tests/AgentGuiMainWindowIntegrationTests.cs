using AgentSchema;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Tests;

public sealed class AgentGuiMainWindowIntegrationTests
{
    private static AgentDefinition CreateAgentDefinition()
        => AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "test-agent",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task AgentGuiMainWindow_Constructs_WithExpectedChildDataContexts()
    {
        var parseResult = new AgentDefinitionParseResult(
            CreateAgentDefinition(),
            AgentSchemaPath: null,
            AgentSessionId: null,
            LogChat: false,
            LogHttpRequests: false,
            UnmatchedArguments: []);

        await using var viewModel = await MainWindowViewModel.CreateAsync(parseResult);
        var window = new global::Phantom.Workspaces.Agent.Gui.MainWindow(viewModel);
        window.Show();

        var inputQueueControl = window.GetVisualDescendants().OfType<AgentChatInputQueueControl>().Single();
        var outputControl = window.GetVisualDescendants().OfType<AgentChatOutputControl>().Single();

        Assert.IsType<InputQueueViewModel>(inputQueueControl.DataContext);
        Assert.IsType<AgentViewModel>(outputControl.DataContext);

        window.Close();
    }

}
