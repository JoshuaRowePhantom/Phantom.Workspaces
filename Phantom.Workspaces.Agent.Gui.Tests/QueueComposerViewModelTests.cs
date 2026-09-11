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
        var inputQueue = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var composer = inputQueue.DefaultComposer;

        composer.InputText = "hello world";

        // Caret sits just after "hello " (index 6): submit "hello", keep "world".
        var submitted = await composer.SubmitBeforeCursorAsync(
            caretIndex: 6,
            TestContext.Current.CancellationToken);

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
        var inputQueue = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var composer = inputQueue.DefaultComposer;

        composer.InputText = "complete message";

        var submitted = await composer.SubmitBeforeCursorAsync(
            caretIndex: composer.InputText.Length,
            TestContext.Current.CancellationToken);

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
        var inputQueue = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var composer = inputQueue.DefaultComposer;

        composer.InputText = "keep me";

        var submitted = await composer.SubmitBeforeCursorAsync(
            caretIndex: 0,
            TestContext.Current.CancellationToken);

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
        var inputQueue = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
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
        var submitted = await composer.SubmitBeforeCursorAsync(
            caretIndex: "/model gpt".Length,
            TestContext.Current.CancellationToken);

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
        var inputQueue = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var composer = inputQueue.DefaultComposer;

        composer.InputText = "hello world";

        var submitted = await composer.SubmitBeforeCursorAsync(
            caretIndex: 6,
            TestContext.Current.CancellationToken);
        var newCaretIndex = 0;

        Assert.True(submitted);
        Assert.Equal("world", composer.InputText);
        Assert.Equal(0, newCaretIndex);

        inputQueue.Dispose();
    }

    [Fact]
    public async Task SubmitBeforeCursorAsync_CancelledMutation_PreservesEntireDraft()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var inputQueue = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var composer = inputQueue.DefaultComposer;
        composer.InputText = "hello world";
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            composer.SubmitBeforeCursorAsync(caretIndex: 6, cancellation.Token));

        Assert.Equal("hello world", composer.InputText);
        inputQueue.Dispose();
    }

    [Fact]
    public async Task SubmitBeforeCursorAsync_DraftChangesWhilePending_AreNotOverwritten()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var inputQueue = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var composer = inputQueue.DefaultComposer;
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        composer.SlashCommandInterceptorAsync = _ =>
        {
            invoked.TrySetResult();
            return release.Task;
        };
        composer.InputText = "/help keep";

        var submission = composer.SubmitBeforeCursorAsync(
            caretIndex: "/help".Length,
            TestContext.Current.CancellationToken);
        await invoked.Task.WaitAsync(TestContext.Current.CancellationToken);
        composer.AppendImageAttachment([0], "image/png", 1, 1, "new.png");
        var editedDraft = composer.InputText;
        release.TrySetResult();

        Assert.True(await submission);
        Assert.Equal(editedDraft, composer.InputText);
        Assert.True(composer.HasAttachments);
        inputQueue.Dispose();
    }

    [Fact]
    public async Task SubmitCommand_RejectedQueue_PreservesDraftAndReportsFailure()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var inputQueue = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var queue = chat.QueueManager.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);
        var composer = new QueueComposerViewModel(inputQueue, queue.Queue.QueueId, isDefaultComposer: false);
        inputQueue.RemoveInputQueue(queue.Queue.QueueId);
        composer.InputText = "preserve me";

        composer.SubmitCommand.Execute(null);
        await Assert.IsType<AsyncRelayCommand>(composer.SubmitCommand).LastExecutionTask!;

        Assert.Equal("preserve me", composer.InputText);
        Assert.Contains(chat.History, item =>
            item.Contents.OfType<TextContent>().Any(content =>
                content.Text.Contains("could not be submitted", StringComparison.Ordinal)));
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
