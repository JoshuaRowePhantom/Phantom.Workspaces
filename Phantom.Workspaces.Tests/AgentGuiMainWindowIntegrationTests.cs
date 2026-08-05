using AgentSchema;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

using Phantom.Workspaces.Testing.Gui;

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

    [AvaloniaFact(Timeout = 15_000)]
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

        // Issue #1035: the detail region is a Dock.Avalonia DockControl that realizes the active
        // document's content on a dispatcher tick (the previous IsVisible deck realized it eagerly
        // during Show()). Pump the dispatcher so the conversation detail content is materialised.
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        try
        {
            var inputQueueControl = window.GetVisualDescendants().OfType<AgentChatInputQueueControl>().Single();
            var outputControl = window.GetVisualDescendants().OfType<AgentChatOutputControl>().Single();

            Assert.IsType<InputQueueViewModel>(inputQueueControl.DataContext);
            Assert.IsType<AgentViewModel>(outputControl.DataContext);
        }
        finally
        {
            window.Close();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
        }
    }

}
