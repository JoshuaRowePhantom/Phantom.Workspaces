using AgentSchema;
using Avalonia.Input;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using System.Collections.Generic;

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

    [AvaloniaFact]
    public async Task HandleInputKey_CtrlEnter_InNormalMode_SubmitsToCurrentQueue()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager)
        {
            InputText = "hello ctrl enter",
        };

        var handled = QueueComposerControl.HandleInputKey(viewModel.DefaultComposer, Key.Enter, KeyModifiers.Control);
        await WaitForConditionAsync(chat.History, () => chat.History.Count >= 2, "ctrl+enter normal-mode submission to complete");

        Assert.True(handled);
        Assert.Single(chat.InputQueues);
        Assert.Equal(2, chat.History.Count);
        Assert.Equal("hello ctrl enter", string.Concat(chat.History[0].Contents.OfType<TextContent>().Select(static content => content.Text)));
    }

    [AvaloniaFact]
    public async Task HandleInputKey_CtrlEnter_InFormattedMode_Submits()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager)
        {
            InputText = "multi-line submit",
        };
        viewModel.DefaultComposer.EnterFormattedMode();

        var handled = QueueComposerControl.HandleInputKey(viewModel.DefaultComposer, Key.Enter, KeyModifiers.Control);
        await WaitForConditionAsync(chat.History, () => chat.History.Count >= 2, "ctrl+enter formatted submission to complete");

        Assert.True(handled);
        Assert.Equal(2, chat.History.Count);
    }

    [AvaloniaFact]
    public async Task HandleInputKey_CtrlQ_WithEmptyComposer_ReturnsFalse()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        // Composer is empty — no text, no attachments
        viewModel.InputText = string.Empty;

        var handled = QueueComposerControl.HandleInputKey(viewModel.DefaultComposer, Key.Q, KeyModifiers.Control);

        Assert.False(handled);
        Assert.Empty(chat.History);
    }

    [AvaloniaFact]
    public async Task HandleInputKey_CtrlQ_WithText_ReturnsTrue()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.InputText = "route to most recent";

        var handled = QueueComposerControl.HandleInputKey(viewModel.DefaultComposer, Key.Q, KeyModifiers.Control);
        await WaitForConditionAsync(chat.History, () => chat.History.Count >= 2, "Ctrl+Q submission to complete");

        Assert.True(handled);
    }

    [Fact]
    public async Task PlaceholderText_DefaultComposer_ShowsShortcuts()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);

        Assert.Contains("Enter", viewModel.DefaultComposer.PlaceholderText);
        Assert.Contains("Shift+Enter", viewModel.DefaultComposer.PlaceholderText);
        Assert.Contains("Ctrl+Q", viewModel.DefaultComposer.PlaceholderText);
        Assert.DoesNotContain("send to new queue", viewModel.DefaultComposer.PlaceholderText);
    }

    [Fact]
    public async Task PlaceholderText_DefaultComposer_FormattedMode_ShowsFormattedShortcuts()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.DefaultComposer.EnterFormattedMode();

        // Placeholder is simplified in formatted mode; shortcuts are in FormattedModeHint.
        Assert.Equal("Multi-line mode", viewModel.DefaultComposer.PlaceholderText);
        Assert.Contains("Ctrl+Enter", viewModel.DefaultComposer.FormattedModeHint);
        Assert.Contains("Esc", viewModel.DefaultComposer.FormattedModeHint);
        Assert.DoesNotContain("Shift+Enter", viewModel.DefaultComposer.FormattedModeHint);
    }

    [Fact]
    public async Task PlaceholderText_ChangesWhenFormattedModeChanges()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var changedProperties = new List<string?>();
        viewModel.DefaultComposer.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        viewModel.DefaultComposer.EnterFormattedMode();

        Assert.Contains(nameof(viewModel.DefaultComposer.PlaceholderText), changedProperties);
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
