using AgentSchema;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Echo;
using Phantom.Workspaces.Llm.Interfaces;

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

    [AvaloniaFact]
    public async Task Constructor_WithLoggingFlags_DoesNotThrow()
    {
        var parseResult = new AgentDefinitionParseResult(
            CreateAgentDefinition(),
            AgentSchemaPath: null,
            AgentSessionId: null,
            LogChat: true,
            LogHttpRequests: true,
            UnmatchedArguments: []);

        var viewModel = await MainWindowViewModel.CreateAsync(parseResult);
        await viewModel.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Constructor_WithSchemaPath_AppendsSchemaFileToDisplayName()
    {
        var parseResult = new AgentDefinitionParseResult(
            CreateAgentDefinition(),
            AgentSchemaPath: @"C:\repo\docs\examples\qwen-local-chat.json",
            AgentSessionId: null,
            LogChat: false,
            LogHttpRequests: false,
            UnmatchedArguments: []);

        var viewModel = await MainWindowViewModel.CreateAsync(parseResult);
        Assert.Contains("[from qwen-local-chat.json]", viewModel.Agent.DisplayName, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.Agent.AgentSessionId));
        await viewModel.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task ToggleReasoningVisibility_UpdatesAgentState()
    {
        var parseResult = new AgentDefinitionParseResult(
            CreateAgentDefinition(),
            AgentSchemaPath: null,
            AgentSessionId: null,
            LogChat: false,
            LogHttpRequests: false,
            UnmatchedArguments: []);

        var viewModel = await MainWindowViewModel.CreateAsync(parseResult);
        Assert.False(viewModel.Agent.IsReasoningVisible);
        viewModel.Agent.ToggleReasoningVisibility();
        Assert.True(viewModel.Agent.IsReasoningVisible);
        await viewModel.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task HandleKey_CtrlT_TogglesReasoningVisibility()
    {
        var parseResult = new AgentDefinitionParseResult(
            CreateAgentDefinition(),
            AgentSchemaPath: null,
            AgentSessionId: null,
            LogChat: false,
            LogHttpRequests: false,
            UnmatchedArguments: []);

        var viewModel = await MainWindowViewModel.CreateAsync(parseResult);
        var handled = MainWindow.HandleKey(viewModel, Key.T, KeyModifiers.Control);

        Assert.True(handled);
        Assert.True(viewModel.Agent.IsReasoningVisible);
        await viewModel.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task CreateAsync_WithAgentSessionId_UsesRequestedSessionId()
    {
        var parseResult = new AgentDefinitionParseResult(
            CreateAgentDefinition(),
            AgentSchemaPath: null,
            AgentSessionId: "gui-session-id",
            LogChat: false,
            LogHttpRequests: false,
            UnmatchedArguments: []);

        var viewModel = await MainWindowViewModel.CreateAsync(parseResult);

        Assert.Equal("gui-session-id", viewModel.Agent.AgentSessionId);
        await viewModel.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task CreateAsync_WithRestoredSession_LoadsPersistedMessagesIntoAgentHistory()
    {
        var sessionId = "gui-restored-history";
        var store = new InMemoryAgentPersistenceStore();
        var services = new AgentServices
        {
            AgentPersistenceStoreOverride = store,
        };
        var serializerAgent = new ChatClientAgent(
            new EchoChatClient(),
            new ChatClientAgentOptions { UseProvidedChatClientAsIs = true });
        var serializerSession = await serializerAgent.CreateSessionAsync(CancellationToken.None);
        var serializedSession = await serializerAgent.SerializeSessionAsync(serializerSession, cancellationToken: CancellationToken.None);
        var agentDefinition = CreateAgentDefinition();

        await store.StoreAsync(
            new StoreRequestAgent
            {
                Agent = new PersistedAgent
                {
                    AgentSessionId = sessionId,
                    AgentSessionJson = BsonDocument.Parse(serializedSession.GetRawText()),
                    AgentDefinitionJson = BsonDocument.Parse(agentDefinition.ToJson()),
                },
                NewMessages =
                [
                    new ChatMessage(ChatRole.User, "restore me"),
                    new ChatMessage(ChatRole.Assistant, "restored"),
                ],
            },
            CancellationToken.None);

        var parseResult = new AgentDefinitionParseResult(
            agentDefinition,
            AgentSchemaPath: null,
            AgentSessionId: sessionId,
            LogChat: false,
            LogHttpRequests: false,
            UnmatchedArguments: []);

        await using var viewModel = await MainWindowViewModel.CreateAsync(parseResult, services);

        Assert.Contains(
            viewModel.Agent.History,
            item => item.Role == ChatRole.User
                && string.Concat(item.Contents.OfType<TextContent>().Select(static content => content.Text)).Contains("restore me", StringComparison.Ordinal));
        Assert.Contains(
            viewModel.Agent.History,
            item => item.Role == ChatRole.Assistant
                && string.Concat(item.Contents.OfType<TextContent>().Select(static content => content.Text)).Contains("restored", StringComparison.Ordinal));
    }

}
