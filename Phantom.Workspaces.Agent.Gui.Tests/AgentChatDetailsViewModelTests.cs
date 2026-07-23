using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentChatDetailsViewModelTests
{
    [Fact]
    public async Task IsReasoningVisible_Setter_UpdatesAgentState()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);
        var details = new AgentChatDetailsViewModel(viewModel);

        Assert.False(viewModel.IsReasoningVisible);
        details.IsReasoningVisible = true;

        Assert.True(viewModel.IsReasoningVisible);
        Assert.True(details.IsReasoningVisible);
    }

    [Fact]
    public async Task IsReasoningVisible_ReflectsAgentToggle()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);
        var details = new AgentChatDetailsViewModel(viewModel);

        viewModel.ToggleReasoningVisibility();

        Assert.True(details.IsReasoningVisible);
    }

    [Fact]
    public async Task ModelMetadata_ExposesProviderModelAndConnectionTypeWithoutSecrets()
    {
        var chat = await CreateChatAsync(
            """
            {
              "kind": "prompt",
              "name": "test-agent",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo",
                "connection": {
                  "kind": "key",
                  "endpoint": "https://example.invalid",
                  "apiKey": "do-not-show-this"
                }
              },
              "tools": []
            }
            """);
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);
        var details = new AgentChatDetailsViewModel(viewModel);

        Assert.Equal("echo", details.ModelProvider);
        Assert.Equal("test", details.ModelId);
        Assert.Equal("Echo", details.ModelApiType);
        Assert.Equal("API key", details.ModelConnectionType);
        Assert.DoesNotContain("do-not-show-this", details.ModelConnectionType, StringComparison.Ordinal);
    }

    private static AgentDefinition CreateAgentDefinition(string? definitionJson = null)
        => AgentDefinitionLoader.LoadAgentFromJson(
            definitionJson ??
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

    private static Task<AgentChat> CreateChatAsync(
        string? definitionJson = null,
        AgentServices? agentServices = null)
        => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = CreateAgentDefinition(definitionJson),
                AgentServices = agentServices,
            });
}
