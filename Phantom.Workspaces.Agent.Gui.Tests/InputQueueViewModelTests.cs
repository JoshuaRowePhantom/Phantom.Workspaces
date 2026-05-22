using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class InputQueueViewModelTests
{
    private static AgentDefinition CreateTestAgentDefinition()
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
    public async Task SubmitToDefaultQueue_QueuesTextAndClearsInput()
    {
        var created = AgentFactory.CreateAgentChat(CreateTestAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        viewModel.InputText = "hello";
        viewModel.IsFormattedMode = true;

        viewModel.SubmitToDefaultQueue();

        Assert.Empty(viewModel.InputText);
        Assert.False(viewModel.IsFormattedMode);
        Assert.Equal(2, chat.History.Count);
        var userHistory = chat.History[0];
        Assert.Equal("hello", userHistory.Text);
        Assert.Single(userHistory.Contents);
        Assert.IsType<TextContent>(userHistory.Contents[0]);
    }

    [Fact]
    public async Task SubmitToNewQueue_CreatesQueueWhenManagerIsBound()
    {
        var created = AgentFactory.CreateAgentChat(CreateTestAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.InputText = "queued";

        viewModel.SubmitToNewQueue();

        Assert.Equal(2, chat.InputQueues.Count);
        Assert.Equal("queued", chat.History[0].Text);
    }

    [Fact]
    public async Task ToggleHoldAllQueues_WhenAnyNotHeld_HoldsAllQueues()
    {
        var created = AgentFactory.CreateAgentChat(CreateTestAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.SubmitToNewQueue();

        viewModel.ToggleHoldAllQueues();

        Assert.All(chat.InputQueues, queue => Assert.True(queue.IsHeld));
    }

    [Fact]
    public async Task ToggleHoldAllQueues_WhenAllHeld_UnholdsAllQueues()
    {
        var created = AgentFactory.CreateAgentChat(CreateTestAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.SubmitToNewQueue();
        viewModel.ToggleHoldAllQueues();

        viewModel.ToggleHoldAllQueues();

        Assert.All(chat.InputQueues, queue => Assert.False(queue.IsHeld));
    }

    [Fact]
    public async Task HoldAllQueues_AlwaysHoldsAllQueues()
    {
        var created = AgentFactory.CreateAgentChat(CreateTestAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.SubmitToNewQueue();

        viewModel.HoldAllQueues();

        Assert.All(chat.InputQueues, queue => Assert.True(queue.IsHeld));
    }

    [Fact]
    public async Task UnholdAllQueues_AlwaysUnholdsAllQueues()
    {
        var created = AgentFactory.CreateAgentChat(CreateTestAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.SubmitToNewQueue();
        viewModel.HoldAllQueues();

        viewModel.UnholdAllQueues();

        Assert.All(chat.InputQueues, queue => Assert.False(queue.IsHeld));
    }
}
