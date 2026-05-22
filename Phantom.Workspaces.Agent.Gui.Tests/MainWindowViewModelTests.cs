using AgentSchema;
using Avalonia.Input;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class MainWindowViewModelTests
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

    [Fact]
    public async Task Constructor_WithLoggingFlags_DoesNotThrow()
    {
        var parseResult = new AgentDefinitionParseResult(
            CreateAgentDefinition(),
            AgentSchemaPath: null,
            LogChat: true,
            LogHttpRequests: true,
            UnmatchedArguments: []);

        var viewModel = new MainWindowViewModel(parseResult);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task Constructor_WithSchemaPath_AppendsSchemaFileToDisplayName()
    {
        var parseResult = new AgentDefinitionParseResult(
            CreateAgentDefinition(),
            AgentSchemaPath: @"C:\repo\docs\examples\qwen-local-chat.json",
            LogChat: false,
            LogHttpRequests: false,
            UnmatchedArguments: []);

        var viewModel = new MainWindowViewModel(parseResult);
        Assert.Contains("[from qwen-local-chat.json]", viewModel.Agent.DisplayName, StringComparison.Ordinal);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task ToggleReasoningVisibility_UpdatesAgentState()
    {
        var parseResult = new AgentDefinitionParseResult(
            CreateAgentDefinition(),
            AgentSchemaPath: null,
            LogChat: false,
            LogHttpRequests: false,
            UnmatchedArguments: []);

        var viewModel = new MainWindowViewModel(parseResult);
        Assert.False(viewModel.Agent.IsReasoningVisible);
        viewModel.Agent.ToggleReasoningVisibility();
        Assert.True(viewModel.Agent.IsReasoningVisible);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task HandleKey_CtrlT_TogglesReasoningVisibility()
    {
        var parseResult = new AgentDefinitionParseResult(
            CreateAgentDefinition(),
            AgentSchemaPath: null,
            LogChat: false,
            LogHttpRequests: false,
            UnmatchedArguments: []);

        var viewModel = new MainWindowViewModel(parseResult);
        var handled = MainWindow.HandleKey(viewModel, Key.T, KeyModifiers.Control);

        Assert.True(handled);
        Assert.True(viewModel.Agent.IsReasoningVisible);
        await viewModel.DisposeAsync();
    }
}
