using AgentSchema;
using Avalonia.Input;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentChatInputQueueControlKeyTests
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

    private static Task<AgentChat> CreateChatAsync(AgentServices? agentServices = null)
        => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = CreateAgentDefinition(),
                AgentServices = agentServices,
            });

    [AvaloniaFact]
    public async Task HoldAllQueuesCommand_HoldsAllQueues()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.InputText = "hello";
        viewModel.SubmitToNewQueue();

        viewModel.HoldAllQueuesCommand.Execute(null);
        Assert.All(chat.InputQueues, queue => Assert.True(queue.IsHeld));
    }

    [AvaloniaFact]
    public async Task UnholdAllQueuesCommand_UnholdsAllQueues()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.InputText = "hello";
        viewModel.SubmitToNewQueue();
        viewModel.HoldAllQueues();

        viewModel.UnholdAllQueuesCommand.Execute(null);
        Assert.All(chat.InputQueues, queue => Assert.False(queue.IsHeld));
    }

    [AvaloniaFact]
    public async Task ToggleHoldAllQueuesCommand_TogglesHoldState()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.InputText = "hello";
        viewModel.SubmitToNewQueue();

        viewModel.ToggleHoldAllQueuesCommand.Execute(null);
        Assert.All(chat.InputQueues, queue => Assert.True(queue.IsHeld));
    }

    [AvaloniaFact]
    public async Task HandleInputKey_CtrlShiftQ_WhenQueuesAreHeld_CreatesHeldQueue()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.InputText = "create";
        viewModel.HoldAllQueues();

        var handled = QueueComposerControl.HandleInputKey(viewModel.DefaultComposer, Key.Q, KeyModifiers.Control | KeyModifiers.Shift);

        Assert.True(handled);
        Assert.Equal(2, chat.InputQueues.Count);
        Assert.True(chat.InputQueues[1].IsHeld);
        Assert.Single(viewModel.Queues[1].Items);
    }

    [AvaloniaFact]
    public async Task HandleInputKey_Return_SubmitsToDefaultQueue()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager)
        {
            InputText = "hello from return",
        };

        var handled = QueueComposerControl.HandleInputKey(viewModel.DefaultComposer, Key.Return, KeyModifiers.None);
        await WaitForConditionAsync(chat.History, () => chat.History.Count >= 2, "return key submission to complete");

        Assert.True(handled);
        Assert.Equal(2, chat.History.Count);
        Assert.Equal("hello from return", string.Concat(chat.History[0].Contents.OfType<TextContent>().Select(static content => content.Text)));
    }

    private static async Task WaitForConditionAsync(
        System.Collections.Specialized.INotifyCollectionChanged collection,
        Func<bool> condition,
        string description)
    {
        if (condition())
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (condition())
            {
                signal.TrySetResult();
            }
        }

        collection.CollectionChanged += OnCollectionChanged;
        try
        {
            if (condition())
            {
                return;
            }

            await signal.Task;
        }
        finally
        {
            collection.CollectionChanged -= OnCollectionChanged;
        }
    }
}
