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
    public async Task HandleInputKey_CtrlShiftH_HoldsAllQueues()
    {
        var created = AgentFactory.CreateAgentChat(CreateAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.SubmitToNewQueue();

        var handled = AgentInputQueueControl.HandleInputKey(viewModel, Key.H, KeyModifiers.Control | KeyModifiers.Shift);

        Assert.True(handled);
        Assert.All(chat.InputQueues, queue => Assert.True(queue.IsHeld));
    }

    [Fact]
    public async Task HandleInputKey_CtrlShiftU_UnholdsAllQueues()
    {
        var created = AgentFactory.CreateAgentChat(CreateAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.SubmitToNewQueue();
        viewModel.HoldAllQueues();

        var handled = AgentInputQueueControl.HandleInputKey(viewModel, Key.U, KeyModifiers.Control | KeyModifiers.Shift);

        Assert.True(handled);
        Assert.All(chat.InputQueues, queue => Assert.False(queue.IsHeld));
    }

    [Fact]
    public async Task HandleInputKey_CtrlH_TogglesHoldState()
    {
        var created = AgentFactory.CreateAgentChat(CreateAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.SubmitToNewQueue();

        var handled = AgentInputQueueControl.HandleInputKey(viewModel, Key.H, KeyModifiers.Control);

        Assert.True(handled);
        Assert.All(chat.InputQueues, queue => Assert.True(queue.IsHeld));
    }

    [Fact]
    public async Task HandleInputKey_Return_SubmitsToDefaultQueue()
    {
        var created = AgentFactory.CreateAgentChat(CreateAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager)
        {
            InputText = "hello from return",
        };

        var handled = AgentInputQueueControl.HandleInputKey(viewModel, Key.Return, KeyModifiers.None);

        Assert.True(handled);
        Assert.Equal(2, chat.History.Count);
        Assert.Equal("hello from return", chat.History[0].Text);
    }
}
