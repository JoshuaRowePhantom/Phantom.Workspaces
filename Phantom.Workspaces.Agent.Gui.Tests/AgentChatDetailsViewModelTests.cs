using AgentSchema;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentChatDetailsViewModelTests
{
    [Fact]
    public async Task IsReasoningVisible_Setter_UpdatesAgentState()
    {
        var chat = await CreateChatAsync();
        await using var viewModel = new AgentViewModel(chat, "test-agent");
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
        await using var viewModel = new AgentViewModel(chat, "test-agent");
        var details = new AgentChatDetailsViewModel(viewModel);

        viewModel.ToggleReasoningVisibility();

        Assert.True(details.IsReasoningVisible);
    }

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

    private static Task<AgentChat> CreateChatAsync(AgentServices? agentServices = null)
        => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = CreateAgentDefinition(),
                AgentServices = agentServices,
            });
}
