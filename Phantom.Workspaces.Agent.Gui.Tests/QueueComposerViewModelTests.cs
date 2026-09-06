using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using System.Collections.Specialized;
using System.Linq;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class QueueComposerViewModelTests
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
    public async Task SubmitBeforeCursor_WithCaretMidText_SubmitsPrefixAndKeepsSuffix()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = inputQueue.DefaultComposer;

        composer.InputText = "hello world";

        // Caret sits just after "hello " (index 6): submit "hello", keep "world".
        var submitted = composer.SubmitBeforeCursor(caretIndex: 6);

        Assert.True(submitted);
        Assert.Equal("world", composer.InputText);

        await WaitForConditionAsync(chat.History, () => chat.History.Count >= 2,
            "submit-before-cursor prefix to be queued and processed");
        Assert.Equal("hello",
            string.Concat(chat.History[0].Contents.OfType<TextContent>().Select(static c => c.Text)));

        inputQueue.Dispose();
    }

    [Fact]
    public async Task SubmitBeforeCursor_CaretAtEnd_SubmitsAllAndClearsInput()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = inputQueue.DefaultComposer;

        composer.InputText = "complete message";

        var submitted = composer.SubmitBeforeCursor(caretIndex: composer.InputText.Length);

        Assert.True(submitted);
        Assert.Equal(string.Empty, composer.InputText);

        await WaitForConditionAsync(chat.History, () => chat.History.Count >= 2,
            "caret-at-end submission to be queued and processed");
        Assert.Equal("complete message",
            string.Concat(chat.History[0].Contents.OfType<TextContent>().Select(static c => c.Text)));

        inputQueue.Dispose();
    }

    [Fact]
    public async Task SubmitBeforeCursor_CaretAtStart_DoesNotSubmitAndKeepsInput()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = inputQueue.DefaultComposer;

        composer.InputText = "keep me";

        var submitted = composer.SubmitBeforeCursor(caretIndex: 0);

        Assert.False(submitted);
        Assert.Equal("keep me", composer.InputText);
        Assert.Empty(chat.DefaultInputQueue.Items);

        inputQueue.Dispose();
    }

    [Fact]
    public async Task SubmitBeforeCursor_PrefixIsSlashCommand_RoutesThroughInterceptor()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = inputQueue.DefaultComposer;

        string? intercepted = null;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        composer.SlashCommandInterceptorAsync = text =>
        {
            intercepted = text;
            tcs.TrySetResult();
            return Task.CompletedTask;
        };

        // "/model gpt" before the caret, " keep this" retained after.
        composer.InputText = "/model gpt keep this";
        var submitted = composer.SubmitBeforeCursor(caretIndex: "/model gpt".Length);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(submitted);
        Assert.Equal("/model gpt", intercepted);
        Assert.Equal(" keep this", composer.InputText);
        Assert.Empty(chat.DefaultInputQueue.Items);

        inputQueue.Dispose();
    }

    [Fact]
    public async Task SubmitBeforeCursor_PlacesCaretAtStartOfRemainingText()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = inputQueue.DefaultComposer;

        composer.InputText = "hello world";

        var submitted = composer.SubmitBeforeCursor(caretIndex: 6, out var newCaretIndex);

        Assert.True(submitted);
        Assert.Equal("world", composer.InputText);
        Assert.Equal(0, newCaretIndex);

        inputQueue.Dispose();
    }

    private static async Task WaitForConditionAsync(
        INotifyCollectionChanged collection,
        Func<bool> condition,
        string description)
    {
        if (condition())
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
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
