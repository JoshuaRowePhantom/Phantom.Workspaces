using Avalonia.Headless.XUnit;
using AgentSchema;
using Avalonia.Input;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using System.Collections.Generic;

using Phantom.Workspaces.Testing.Gui;

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

    [Fact]
    public async Task HoldAllQueuesCommand_HoldsAllQueues()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.InputText = "hello";
        viewModel.SubmitToNewQueue();

        viewModel.HoldAllQueuesCommand.Execute(null);
        Assert.All(chat.InputQueues, queue => Assert.True(queue.IsHeld));
    }

    [Fact]
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

    [Fact]
    public async Task ToggleHoldAllQueuesCommand_TogglesHoldState()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.InputText = "hello";
        viewModel.SubmitToNewQueue();

        viewModel.ToggleHoldAllQueuesCommand.Execute(null);
        Assert.All(chat.InputQueues, queue => Assert.True(queue.IsHeld));
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task HandleInputKey_Escape_WhenCompletionsVisible_DismissesCompletions()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = viewModel.DefaultComposer;

        composer.Completions.SetItems([new Phantom.Workspaces.Llm.SlashCommands.SlashCommandCompletion("working-directory", "/working-directory", "desc")]);
        Assert.True(composer.Completions.IsVisible);

        var handled = QueueComposerControl.HandleInputKey(composer, Key.Escape, KeyModifiers.None);

        Assert.True(handled);
        Assert.False(composer.Completions.IsVisible);
    }

    [Fact]
    public async Task HandleInputKey_Enter_WhenCompletionsVisible_DoesNotSubmit()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = viewModel.DefaultComposer;
        composer.InputText = "hello";

        composer.Completions.SetItems([new Phantom.Workspaces.Llm.SlashCommands.SlashCommandCompletion("working-directory", "/working-directory", "desc")]);

        var handled = QueueComposerControl.HandleInputKey(composer, Key.Enter, KeyModifiers.None);

        Assert.True(handled);
        Assert.Empty(chat.DefaultInputQueue.Items);
    }

    [Fact]
    public async Task HandleInputKey_Tab_WhenCompletionsVisible_AndNothingSelected_SelectsFirst()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = viewModel.DefaultComposer;

        composer.Completions.SetItems([
            new Phantom.Workspaces.Llm.SlashCommands.SlashCommandCompletion("alpha", "/alpha", "desc"),
            new Phantom.Workspaces.Llm.SlashCommands.SlashCommandCompletion("beta", "/beta", "desc"),
        ]);

        var handled = QueueComposerControl.HandleInputKey(composer, Key.Tab, KeyModifiers.None);

        Assert.True(handled);
        Assert.Equal(0, composer.Completions.SelectedIndex);
        Assert.True(composer.Completions.IsVisible);
    }

    [Fact]
    public async Task HandleInputKey_Tab_WhenItemSelected_AcceptsCompletionAndDismisses()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = viewModel.DefaultComposer;

        composer.Completions.SetItems([
            new Phantom.Workspaces.Llm.SlashCommands.SlashCommandCompletion("working-directory", "/working-directory", "desc"),
        ]);
        composer.Completions.SelectedIndex = 0;

        var handled = QueueComposerControl.HandleInputKey(composer, Key.Tab, KeyModifiers.None);

        Assert.True(handled);
        Assert.False(composer.Completions.IsVisible);
        Assert.Equal("/working-directory", composer.InputText);
    }

    [AvaloniaFact]
    public async Task HandleInputKey_AcceptingCompletion_SetsNewText()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = viewModel.DefaultComposer;

        composer.Completions.SetItems([
            new Phantom.Workspaces.Llm.SlashCommands.SlashCommandCompletion("working-directory", "/working-directory", "desc"),
        ]);
        composer.Completions.SelectedIndex = 0;

        QueueComposerControl.HandleInputKey(composer, Key.Tab, KeyModifiers.None, caretLine: 0, out var newText, out _);

        Assert.Equal("/working-directory", newText);
    }

    [AvaloniaFact]
    public async Task HandleInputKey_AcceptingCompletion_MovesCursorToEnd()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = viewModel.DefaultComposer;

        composer.Completions.SetItems([
            new Phantom.Workspaces.Llm.SlashCommands.SlashCommandCompletion("working-directory", "/working-directory", "desc"),
        ]);
        composer.Completions.SelectedIndex = 0;

        QueueComposerControl.HandleInputKey(composer, Key.Tab, KeyModifiers.None, caretLine: 0, out var newText, out var newCaretIndex);

        Assert.NotNull(newText);
        Assert.Equal(newText!.Length, newCaretIndex);
    }

    [AvaloniaFact]
    public async Task HandleInputKey_Tab_WithTextBeforeSlashToken_PreservesPrecedingText()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = viewModel.DefaultComposer;

        composer.InputText = "hello /wo";
        composer.Completions.SetItems([
            new Phantom.Workspaces.Llm.SlashCommands.SlashCommandCompletion("working-directory", "/working-directory", "desc"),
        ]);
        composer.Completions.SelectedIndex = 0;

        QueueComposerControl.HandleInputKey(
            composer,
            Key.Tab,
            KeyModifiers.None,
            caretLine: 0,
            caretIndex: "hello /wo".Length,
            out var newText,
            out _);

        Assert.Equal("hello /working-directory", newText);
        Assert.Equal("hello /working-directory", composer.InputText);
    }

    [AvaloniaFact]
    public async Task HandleInputKey_Tab_ReplacesOnlySlashToken_NotEntireInput()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = viewModel.DefaultComposer;

        // Text both before the slash token and after the caret must be preserved.
        composer.InputText = "hello /wo world";
        composer.Completions.SetItems([
            new Phantom.Workspaces.Llm.SlashCommands.SlashCommandCompletion("working-directory", "/working-directory", "desc"),
        ]);
        composer.Completions.SelectedIndex = 0;

        QueueComposerControl.HandleInputKey(
            composer,
            Key.Tab,
            KeyModifiers.None,
            caretLine: 0,
            caretIndex: "hello /wo".Length,
            out var newText,
            out _);

        Assert.Equal("hello /working-directory world", newText);
        Assert.NotEqual("/working-directory", newText);
    }

    [AvaloniaFact]
    public async Task HandleInputKey_Tab_PlacesCaretAfterCompletedToken()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = viewModel.DefaultComposer;

        composer.InputText = "hello /wo world";
        composer.Completions.SetItems([
            new Phantom.Workspaces.Llm.SlashCommands.SlashCommandCompletion("working-directory", "/working-directory", "desc"),
        ]);
        composer.Completions.SelectedIndex = 0;

        QueueComposerControl.HandleInputKey(
            composer,
            Key.Tab,
            KeyModifiers.None,
            caretLine: 0,
            caretIndex: "hello /wo".Length,
            out var newText,
            out var newCaretIndex);

        Assert.NotNull(newText);
        Assert.Equal("hello /working-directory".Length, newCaretIndex);
        Assert.Equal("hello /working-directory", newText![..newCaretIndex]);
    }

    [Fact]
    public async Task HandleInputKey_Down_WhenCompletionsVisible_SelectsNext()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = viewModel.DefaultComposer;

        composer.Completions.SetItems([
            new Phantom.Workspaces.Llm.SlashCommands.SlashCommandCompletion("alpha", "/alpha", "desc"),
            new Phantom.Workspaces.Llm.SlashCommands.SlashCommandCompletion("beta", "/beta", "desc"),
        ]);

        var handled = QueueComposerControl.HandleInputKey(composer, Key.Down, KeyModifiers.None);

        Assert.True(handled);
        Assert.Equal(0, composer.Completions.SelectedIndex);
    }

    [Fact]
    public async Task HandleInputKey_Up_WhenCompletionsVisible_SelectsPrevious()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = viewModel.DefaultComposer;

        composer.Completions.SetItems([
            new Phantom.Workspaces.Llm.SlashCommands.SlashCommandCompletion("alpha", "/alpha", "desc"),
            new Phantom.Workspaces.Llm.SlashCommands.SlashCommandCompletion("beta", "/beta", "desc"),
        ]);
        composer.Completions.SelectedIndex = 1;

        var handled = QueueComposerControl.HandleInputKey(composer, Key.Up, KeyModifiers.None);

        Assert.True(handled);
        Assert.Equal(0, composer.Completions.SelectedIndex);
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
