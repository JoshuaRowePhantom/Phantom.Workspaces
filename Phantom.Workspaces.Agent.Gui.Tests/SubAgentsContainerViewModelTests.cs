using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class SubAgentsContainerViewModelTests
{
    [Fact]
    public async Task ShowSubAgent_MultipleSlots_OnlyCorrectSlotSelected()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        var browser = new SubAgentBrowserViewModel(chat.SubAgents);
        var container = new SubAgentsContainerViewModel(browser);

        // Create two sub-agents and add slots.
        await AddSubAgentAsync(chat, "agent-1", "Agent One");
        await AddSubAgentAsync(chat, "agent-2", "Agent Two");

        var subAgent1 = chat.SubAgents.Single(s => s.AgentId == "agent-1");
        var subAgent2 = chat.SubAgents.Single(s => s.AgentId == "agent-2");

        var chat1 = (AgentChat)subAgent1;
        var chat2 = (AgentChat)subAgent2;

        await using var viewModel1 = new AgentViewModel(chat1, "Agent One", loggerFactory);
        await using var viewModel2 = new AgentViewModel(chat2, "Agent Two", loggerFactory);

        container.AddSlot("agent-1", viewModel1);
        container.AddSlot("agent-2", viewModel2);

        // Show agent-2.
        container.ShowSubAgent("agent-2");

        // Only agent-2 should be selected.
        var slots = container.Slots.ToList();
        Assert.Equal(2, slots.Count);
        Assert.False(slots[0].IsSelected); // agent-1
        Assert.True(slots[1].IsSelected);  // agent-2

        // Now show agent-1.
        container.ShowSubAgent("agent-1");

        // Only agent-1 should be selected.
        Assert.True(slots[0].IsSelected);  // agent-1
        Assert.False(slots[1].IsSelected); // agent-2

        browser.Dispose();
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

    private static Task<AgentChat> CreateChatAsync()
        => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = CreateAgentDefinition(),
            });

    private static async Task<IRunningSubAgent> AddSubAgentAsync(
        AgentChat chat,
        string agentId,
        string displayName)
    {
        var definition = AgentDefinitionLoader.LoadAgentFromJson(
            $$"""
            {
              "kind": "prompt",
              "name": "{{displayName}}",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

        await chat.GetOrCreateAsync(agentId, definition, $"tool-call-{agentId}");
        return chat.SubAgents.Single(s => s.AgentId == agentId);
    }
}
