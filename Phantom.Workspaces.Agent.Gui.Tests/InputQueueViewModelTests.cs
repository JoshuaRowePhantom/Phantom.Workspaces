using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class InputQueueViewModelTests
{
    private static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO3ZfV0AAAAASUVORK5CYII=");

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

    private static Task<AgentChat> CreateChatAsync(AgentServices? agentServices = null)
        => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = CreateTestAgentDefinition(),
                AgentServices = agentServices,
            });

    [AvaloniaFact]
    public async Task SubmitToDefaultQueue_QueuesTextAndClearsInput()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        viewModel.InputText = "hello";
        viewModel.IsFormattedMode = true;

        viewModel.SubmitToDefaultQueue();
        await WaitForConditionAsync(chat.History, () => chat.History.Count >= 2, "default queue submission to complete");

        Assert.Empty(viewModel.InputText);
        Assert.False(viewModel.IsFormattedMode);
        Assert.Equal(2, chat.History.Count);
        var userHistory = chat.History[0];
        Assert.Equal("hello", string.Concat(userHistory.Contents.OfType<TextContent>().Select(static content => content.Text)));
        Assert.Single(userHistory.Contents);
        Assert.IsType<TextContent>(userHistory.Contents[0]);
    }

    [AvaloniaFact]
    public async Task SubmitToNewQueue_CreatesQueueWhenManagerIsBound()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.InputText = "queued";

        viewModel.SubmitToNewQueue();
        await WaitForConditionAsync(chat.History, () => chat.History.Count >= 2, "new queue submission to complete");

        Assert.Equal(2, chat.InputQueues.Count);
        Assert.Equal(2, chat.History.Count);
        Assert.Equal("queued", string.Concat(chat.History[0].Contents.OfType<TextContent>().Select(static content => content.Text)));
        Assert.Equal(2, viewModel.Queues.Count);
        Assert.Equal("queued", viewModel.Queues[1].SelectedImmediacyOption.Label);
    }

    [AvaloniaFact]
    public async Task SubmitToNewQueue_ProvidesRemoveCommandForQueuedItems()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var queue = chat.QueueManager.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);
        viewModel.AppendToQueue(queue, "remove me");

        var item = Assert.Single(viewModel.Queues[1].Items);
        item.RemoveCommand.Execute(null);

        Assert.Empty(viewModel.Queues[1].Items);
    }

    [AvaloniaFact]
    public async Task QueueComposer_AppendsTextToExistingQueue()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var queue = chat.QueueManager.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);
        viewModel.AppendToQueue(queue, "original");

        var queueVm = viewModel.Queues[1];
        queueVm.ToggleComposerCommand.Execute(null);
        queueVm.Composer.InputText = "more";
        queueVm.Composer.Submit();

        Assert.False(queueVm.IsComposerVisible);
        Assert.Empty(queueVm.Composer.InputText);
        Assert.Equal(2, queueVm.Items.Count);
        Assert.Equal("original", queueVm.Items[0].Text);
        Assert.Equal("more", queueVm.Items[1].Text);
        Assert.Empty(chat.History);
    }

    [AvaloniaFact]
    public async Task QueueComposer_SubmitStatusOptionTracksQueueState()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.InputText = "one";
        viewModel.SubmitToNewQueue();

        var queueVm = viewModel.Queues[1];
        Assert.Equal("queued", queueVm.Composer.SubmitStatusOption.Label);

        queueVm.SetImmediacy(QueueImmediacyOption.All.First(option => option.Value == AgentInputQueueImmediacy.Held));

        Assert.Equal("held", queueVm.Composer.SubmitStatusOption.Label);
    }

    [AvaloniaFact]
    public async Task QueueComposer_CanAttachImageAndSubmitStructuredContent()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.DefaultComposer.AppendImageAttachment(TinyPng, "image/png", 640, 480, "shot.png");

        Assert.Equal("[image 640x480 shot.png]", viewModel.InputText);
        Assert.True(viewModel.DefaultComposer.HasAttachments);
        Assert.Single(viewModel.DefaultComposer.AttachmentPreviews);

        viewModel.SubmitToDefaultQueue();
        await WaitForConditionAsync(chat.History, () => chat.History.Count >= 2, "image submission to complete");

        Assert.False(viewModel.DefaultComposer.HasAttachments);
        Assert.Equal(string.Empty, viewModel.InputText);
        Assert.Equal(2, chat.History.Count);
        Assert.Equal("[image/png]", string.Concat(chat.History[0].Contents.OfType<DataContent>().Select(static content => $"[{content.MediaType}]")));
        Assert.Single(chat.History[0].Contents);
        Assert.IsType<DataContent>(chat.History[0].Contents[0]);
    }

    [AvaloniaFact]
    public async Task QueueComposer_BackspaceRemovesImageAttachment()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.InputText = "hello";
        viewModel.DefaultComposer.AppendImageAttachment(TinyPng, "image/png", 640, 480, "shot.png");

        var removed = viewModel.DefaultComposer.TryRemoveImageAttachmentBeforeCaret(
            viewModel.InputText,
            viewModel.InputText.Length,
            out var updatedText,
            out var updatedCaretIndex);

        Assert.True(removed);
        Assert.Equal("hello", updatedText);
        Assert.Equal(5, updatedCaretIndex);
        Assert.Equal("hello", viewModel.InputText);
        Assert.False(viewModel.DefaultComposer.HasAttachments);
    }

    [AvaloniaFact]
    public async Task SubmitToNewQueue_WhenQueuesAreHeld_CreatesHeldQueue()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.InputText = "held queue";
        viewModel.HoldAllQueues();

        viewModel.SubmitToNewQueue();

        var queue = viewModel.Queues[1];
        Assert.True(queue.IsHeld);
        Assert.Single(queue.Items);
        Assert.Equal("held queue", queue.Items[0].Text);
        Assert.Empty(chat.History);
    }

    [AvaloniaFact]
    public async Task QueueImmediacy_CanBeChangedInPlace()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.InputText = "one";
        viewModel.SubmitToNewQueue();

        var queue = viewModel.Queues[1];
        queue.SelectedImmediacyOption = QueueImmediacyOption.All.First(option => option.Value == AgentInputQueueImmediacy.Held);

        Assert.True(queue.IsHeld);
        Assert.Equal("held", queue.SelectedImmediacyOption.Label);
    }

    [AvaloniaFact]
    public async Task SingleQueue_HidesNameUntilSecondQueueExists()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);

        Assert.False(viewModel.Queues[0].ShowName);

        viewModel.InputText = "two";
        viewModel.SubmitToNewQueue();

        Assert.True(viewModel.Queues[0].ShowName);
        Assert.True(viewModel.Queues[1].ShowName);
    }
    [AvaloniaFact]
    public async Task QueueItem_CanBeEditedInPlace()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var queue = chat.QueueManager.CreateInputQueue();
        chat.QueueManager.SetQueueHeld(queue, held: true);
        viewModel.AppendToQueue(queue, "original");

        var queueItem = Assert.Single(viewModel.Queues[1].Items);
        var editStarted = false;
        queueItem.EditStarted += (_, _) => editStarted = true;
        queueItem.EditCommand.Execute(null);
        queueItem.EditText = "edited";
        queueItem.SaveEditCommand.Execute(null);

        Assert.True(editStarted);
        Assert.Equal("edited", viewModel.Queues[1].Items[0].Text);
        Assert.Equal("edited", chat.InputQueues[1].Items[0].Text);
        Assert.Empty(chat.History);
    }

    [AvaloniaFact]
    public async Task QueueItem_CanRemoveImageAttachment()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var queue = chat.QueueManager.CreateInputQueue();
        chat.QueueManager.SetQueueHeld(queue, held: true);
        viewModel.AppendToQueue(queue, [new TextContent("hello"), new DataContent(TinyPng, "image/png")]);

        var queueItem = Assert.Single(viewModel.Queues[1].Items);
        var attachment = Assert.Single(queueItem.Attachments);
        attachment.RemoveCommand.Execute(null);

        Assert.Equal("hello", viewModel.Queues[1].Items[0].Text);
        Assert.Empty(viewModel.Queues[1].Items[0].Attachments);
        Assert.Equal("hello", chat.InputQueues[1].Items[0].Text);
        Assert.Single(chat.InputQueues[1].Items[0].Contents);
    }

    [AvaloniaFact]
    public async Task ToggleHoldAllQueues_WhenAnyNotHeld_HoldsAllQueues()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.SubmitToNewQueue();

        viewModel.ToggleHoldAllQueues();

        Assert.All(chat.InputQueues, queue => Assert.True(queue.IsHeld));
    }

    [AvaloniaFact]
    public async Task ToggleHoldAllQueues_WhenAllHeld_UnholdsAllQueues()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.SubmitToNewQueue();
        viewModel.ToggleHoldAllQueues();

        viewModel.ToggleHoldAllQueues();

        Assert.All(chat.InputQueues, queue => Assert.False(queue.IsHeld));
    }

    [AvaloniaFact]
    public async Task HoldAllQueues_AlwaysHoldsAllQueues()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.InputText = "one";
        viewModel.SubmitToNewQueue();
        var queueVm = viewModel.Queues[1];

        viewModel.HoldAllQueues();

        Assert.All(chat.InputQueues, queue => Assert.True(queue.IsHeld));
        Assert.All(viewModel.Queues, queue => Assert.Equal("held", queue.SelectedImmediacyOption.Label));
        Assert.Same(queueVm, viewModel.Queues[1]);
    }

    [AvaloniaFact]
    public async Task UnholdAllQueues_AlwaysUnholdsAllQueues()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.SubmitToNewQueue();
        viewModel.HoldAllQueues();

        viewModel.UnholdAllQueues();

        Assert.All(chat.InputQueues, queue => Assert.False(queue.IsHeld));
    }

    [AvaloniaFact]
    public async Task HoldAndUnholdAllQueues_PreserveQueueViewModels()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.InputText = "one";
        viewModel.SubmitToNewQueue();

        var defaultQueueVm = viewModel.Queues[0];
        var userQueueVm = viewModel.Queues[1];

        viewModel.HoldAllQueues();
        viewModel.UnholdAllQueues();

        Assert.Same(defaultQueueVm, viewModel.Queues[0]);
        Assert.Same(userQueueVm, viewModel.Queues[1]);
        Assert.All(chat.InputQueues, queue => Assert.False(queue.IsHeld));
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
