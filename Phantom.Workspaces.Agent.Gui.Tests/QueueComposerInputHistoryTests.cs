using AgentSchema;
using Avalonia.Input;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class QueueComposerInputHistoryTests
{
    private static AgentDefinition CreateAgentDefinition()
        => AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
        }
        """);

    [Fact]
    public async Task TryNavigateHistoryUp_OnFirstLine_ReturnsLastSubmittedMessage()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        var composer = inputQueue.DefaultComposer;

        composer.InputText = "first message";
        composer.Submit();
        composer.InputText = "second message";
        composer.Submit();

        var navigated = composer.TryNavigateHistoryUp(caretLine: 0, out var text, out _);

        Assert.True(navigated);
        Assert.Equal("second message", text);

        inputQueue.Dispose();
    }

    [Fact]
    public async Task TryNavigateHistoryUp_SavesDraftBeforeFirstNavigation()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        var composer = inputQueue.DefaultComposer;

        composer.InputText = "first message";
        composer.Submit();

        composer.InputText = "draft";
        composer.TryNavigateHistoryUp(caretLine: 0, out _, out _);

        // Navigate back down past the end of history to restore draft
        var navigated = composer.TryNavigateHistoryDown(out var text, out _);

        Assert.True(navigated);
        Assert.Equal("draft", text);

        inputQueue.Dispose();
    }

    [Fact]
    public async Task TryNavigateHistoryDown_PastNewestEntry_RestoresDraft()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        var composer = inputQueue.DefaultComposer;

        composer.InputText = "msg1";
        composer.Submit();
        composer.InputText = "msg2";
        composer.Submit();
        composer.InputText = "msg3";
        composer.Submit();

        composer.InputText = "my draft";

        // Navigate up 3 times (to oldest entry)
        composer.TryNavigateHistoryUp(caretLine: 0, out _, out _);
        composer.TryNavigateHistoryUp(caretLine: 0, out _, out _);
        composer.TryNavigateHistoryUp(caretLine: 0, out _, out _);

        // Navigate down 3 times (back past newest entry, restores draft)
        composer.TryNavigateHistoryDown(out _, out _);
        composer.TryNavigateHistoryDown(out _, out _);
        var navigated = composer.TryNavigateHistoryDown(out var restoredText, out var restoredCaret);

        Assert.True(navigated);
        Assert.Equal("my draft", restoredText);
        Assert.Equal("my draft".Length, restoredCaret);

        inputQueue.Dispose();
    }

    [Fact]
    public async Task TryNavigateHistoryUp_OnNonFirstLine_ReturnsFalse()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        var composer = inputQueue.DefaultComposer;

        composer.InputText = "first message";
        composer.Submit();

        // Caret on line 1 (not the first line)
        var navigated = composer.TryNavigateHistoryUp(caretLine: 1, out _, out _);

        Assert.False(navigated);

        inputQueue.Dispose();
    }

    [Fact]
    public async Task CommitToHistory_SkipsDuplicateOfLastEntry()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        var composer = inputQueue.DefaultComposer;

        composer.InputText = "duplicate";
        composer.Submit();
        composer.InputText = "duplicate";
        composer.Submit();

        // Navigate up once — should reach "duplicate"
        var first = composer.TryNavigateHistoryUp(caretLine: 0, out var text1, out _);
        // Navigate up again — should stay at "duplicate" (only one entry)
        var second = composer.TryNavigateHistoryUp(caretLine: 0, out var text2, out _);

        Assert.True(first);
        Assert.Equal("duplicate", text1);
        Assert.True(second);
        Assert.Equal("duplicate", text2);

        // Navigate down — should restore draft (not another history entry)
        var down = composer.TryNavigateHistoryDown(out var restored, out _);
        Assert.True(down);
        // Restored is the empty draft (both submits cleared InputText)
        Assert.Equal(string.Empty, restored);

        inputQueue.Dispose();
    }

    [Fact]
    public async Task HandleInputKey_Up_WithCaretOnVisualLine_InNormalMode_DoesNotNavigateHistory()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        var composer = inputQueue.DefaultComposer;

        composer.InputText = "first message";
        composer.Submit();
        composer.InputText = string.Empty;

        // caretLine: 1 represents the caret being on visual line 1 (above the first),
        // as InputBox_KeyDown will compute when the text is visually wrapped in normal mode.
        var handled = QueueComposerControl.HandleInputKey(
            composer, Key.Up, KeyModifiers.None, caretLine: 1, out var newText, out _);

        Assert.False(handled);
        Assert.Null(newText);

        inputQueue.Dispose();
    }

    [Fact]
    public async Task HandleInputKey_Up_WhenCompletionsVisible_DoesNotNavigateHistory()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        var composer = inputQueue.DefaultComposer;

        composer.InputText = "submitted message";
        composer.Submit();
        composer.InputText = string.Empty;

        // Open the completions popup
        composer.Completions.SetItems([
            new SlashCommandCompletion("alpha", "/alpha", "desc"),
            new SlashCommandCompletion("beta", "/beta", "desc"),
        ]);
        composer.Completions.SelectedIndex = 1;
        Assert.True(composer.Completions.IsVisible);

        // Press Up — should navigate completions, NOT history
        var handled = QueueComposerControl.HandleInputKey(
            composer, Key.Up, KeyModifiers.None, caretLine: 0, out var newText, out _);

        Assert.True(handled);
        Assert.Equal(0, composer.Completions.SelectedIndex);
        Assert.Null(newText); // history navigation was NOT triggered

        inputQueue.Dispose();
    }
}
