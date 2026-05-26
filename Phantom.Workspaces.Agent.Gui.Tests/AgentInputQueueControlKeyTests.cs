using AgentSchema;
using Avalonia.Input;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentInputQueueControlKeyTests
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
    public async Task HoldAllQueuesCommand_HoldsAllQueues()
    {
        await using var chat = AgentFactory.CreateAgentChat(CreateAgentDefinition());
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.InputText = "hello";
        viewModel.SubmitToNewQueue();

        viewModel.HoldAllQueuesCommand.Execute(null);
        Assert.All(chat.InputQueues, queue => Assert.True(queue.IsHeld));
    }

    [Fact]
    public async Task UnholdAllQueuesCommand_UnholdsAllQueues()
    {
        await using var chat = AgentFactory.CreateAgentChat(CreateAgentDefinition());
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.InputText = "hello";
        viewModel.SubmitToNewQueue();
        viewModel.HoldAllQueues();

        viewModel.UnholdAllQueuesCommand.Execute(null);
        Assert.All(chat.InputQueues, queue => Assert.False(queue.IsHeld));
    }

    [Fact]
    public async Task ToggleHoldAllQueuesCommand_TogglesHoldState()
    {
        await using var chat = AgentFactory.CreateAgentChat(CreateAgentDefinition());
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.InputText = "hello";
        viewModel.SubmitToNewQueue();

        viewModel.ToggleHoldAllQueuesCommand.Execute(null);
        Assert.All(chat.InputQueues, queue => Assert.True(queue.IsHeld));
    }

    [Fact]
    public async Task HandleInputKey_CtrlShiftQ_WhenQueuesAreHeld_CreatesHeldQueue()
    {
        await using var chat = AgentFactory.CreateAgentChat(CreateAgentDefinition());
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.InputText = "create";
        viewModel.HoldAllQueues();

        var handled = QueueComposerControl.HandleInputKey(viewModel.DefaultComposer, Key.Q, KeyModifiers.Control | KeyModifiers.Shift);

        Assert.True(handled);
        Assert.Equal(2, chat.InputQueues.Count);
        Assert.True(chat.InputQueues[1].IsHeld);
        Assert.Single(viewModel.Queues[1].Items);
    }

    [Fact]
    public async Task HandleInputKey_Return_SubmitsToDefaultQueue()
    {
        await using var chat = AgentFactory.CreateAgentChat(CreateAgentDefinition());
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager)
        {
            InputText = "hello from return",
        };

        var handled = QueueComposerControl.HandleInputKey(viewModel.DefaultComposer, Key.Return, KeyModifiers.None);
        await Task.Delay(100);

        Assert.True(handled);
        Assert.Equal(2, chat.History.Count);
        Assert.Equal("hello from return", chat.History[0].Text);
    }
}
