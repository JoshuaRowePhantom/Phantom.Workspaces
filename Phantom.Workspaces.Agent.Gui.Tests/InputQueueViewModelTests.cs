using AgentSchema;
using Avalonia.Media;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Remote;

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

    [Fact]
    public async Task SubmitToDefaultQueue_QueuesTextAndClearsInput()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
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

    [Fact]
    public async Task SubmitToNewQueue_CreatesQueueWhenManagerIsBound()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        viewModel.InputText = "queued";

        viewModel.SubmitToNewQueue();

        // Ctrl+Shift+Q stages the message in a new Held queue that does not dispatch (issue #1070).
        Assert.Equal(2, chat.InputQueues.Count);
        Assert.Equal(2, viewModel.Queues.Count);
        Assert.True(viewModel.Queues[1].IsHeld);
        Assert.Equal("held", viewModel.Queues[1].SelectedImmediacyOption.Label);
        var item = Assert.Single(viewModel.Queues[1].Items);
        Assert.Equal("queued", item.Text);
        Assert.Empty(chat.History);
    }

    [Fact]
    public async Task SubmitToNewQueue_ProvidesRemoveCommandForQueuedItems()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var queue = chat.QueueManager.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);
        viewModel.AppendToQueue(queue.Queue.QueueId, "remove me");

        var item = Assert.Single(viewModel.Queues[1].Items);
        item.RemoveCommand.Execute(null);

        Assert.Empty(viewModel.Queues[1].Items);
    }

    [Fact]
    public async Task QueueComposer_AppendsTextToExistingQueue()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var queue = chat.QueueManager.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);
        viewModel.AppendToQueue(queue.Queue.QueueId, "original");

        var queueVm = viewModel.Queues[1];
        queueVm.ToggleComposerCommand.Execute(null);
        queueVm.Composer.InputText = "more";
        Assert.True(await queueVm.Composer.SubmitAsync(TestContext.Current.CancellationToken));
        var refreshedQueueVm = viewModel.Queues.Single(queueViewModel => !queueViewModel.IsDefault);

        Assert.False(refreshedQueueVm.IsComposerVisible);
        Assert.Empty(refreshedQueueVm.Composer.InputText);
        Assert.Equal(2, refreshedQueueVm.Items.Count);
        Assert.Equal("original", refreshedQueueVm.Items[0].Text);
        Assert.Equal("more", refreshedQueueVm.Items[1].Text);
        Assert.Empty(chat.History);
    }

    [Fact]
    public async Task QueueComposer_SubmitStatusOptionTracksQueueState()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        viewModel.InputText = "one";
        viewModel.SubmitToNewQueue();

        var queueVm = viewModel.Queues[1];
        // Ctrl+Shift+Q creates the queue Held (issue #1070); the composer status option tracks it.
        Assert.Equal("held", queueVm.Composer.SelectedImmediacyOption.Label);

        queueVm.SetImmediacy(QueueImmediacyOption.All.First(option => option.Value == AgentInputQueueImmediacy.Queue));

        Assert.Equal("queued", queueVm.Composer.SelectedImmediacyOption.Label);
    }

    [Fact]
    public async Task QueueComposer_CanAttachImageAndSubmitStructuredContent()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
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

    [Fact]
    public async Task QueueComposer_BackspaceRemovesImageAttachment()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
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

    [Fact]
    public async Task SubmitToNewQueue_WhenQueuesAreHeld_CreatesHeldQueue()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        viewModel.InputText = "held queue";
        viewModel.HoldAllQueues();

        viewModel.SubmitToNewQueue();

        var queue = viewModel.Queues[1];
        Assert.True(queue.IsHeld);
        Assert.Single(queue.Items);
        Assert.Equal("held queue", queue.Items[0].Text);
        Assert.Empty(chat.History);
    }

    [Fact]
    public async Task SubmitToNewQueue_WhenNoQueuesHeld_CreatesHeldQueue()
    {
        // Issue #1070: Ctrl+Shift+Q must create the queue Held even when the default queue is active.
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        Assert.False(chat.DefaultInputQueue.IsHeld);

        viewModel.InputText = "stage me";
        viewModel.SubmitToNewQueue();

        Assert.Equal(2, chat.InputQueues.Count);
        Assert.True(chat.InputQueues[1].IsHeld);
        Assert.True(viewModel.Queues[1].IsHeld);
    }

    [Fact]
    public async Task SubmitToNewQueue_CreatesHeldQueue_DoesNotBeginProcessing()
    {
        // Issue #1070: the staged message must not dispatch until the queue is released.
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        viewModel.InputText = "do not run yet";

        viewModel.SubmitToNewQueue();

        var queue = viewModel.Queues[1];
        Assert.Single(queue.Items);
        Assert.Equal("do not run yet", queue.Items[0].Text);
        Assert.Empty(chat.History);
    }

    [Fact]
    public async Task SubmitToNewQueue_AfterUnhold_QueueProcessesEnqueuedItem()
    {
        // Issue #1070: releasing the Held queue dispatches the previously staged message.
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        viewModel.InputText = "run after release";
        viewModel.SubmitToNewQueue();

        var queue = chat.InputQueues[1];
        Assert.True(queue.IsHeld);
        Assert.Empty(chat.History);

        chat.QueueManager.SetQueueHeld(queue, held: false);

        await WaitForConditionAsync(chat.History, () => chat.History.Count >= 2, "released queue to process staged item");

        Assert.False(queue.IsHeld);
        Assert.Equal("run after release", string.Concat(chat.History[0].Contents.OfType<TextContent>().Select(static content => content.Text)));
    }

    [Fact]
    public async Task SubmitToDefaultQueue_RetainsActiveImmediacy()
    {
        // Issue #1070 regression: the Held behaviour is scoped to Ctrl+Shift+Q; the default queue path
        // stays active and dispatches immediately.
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        Assert.False(chat.DefaultInputQueue.IsHeld);

        viewModel.InputText = "default active";
        viewModel.SubmitToDefaultQueue();

        await WaitForConditionAsync(chat.History, () => chat.History.Count >= 2, "default queue submission to process");

        Assert.False(chat.DefaultInputQueue.IsHeld);
        Assert.Equal("default active", string.Concat(chat.History[0].Contents.OfType<TextContent>().Select(static content => content.Text)));
    }

    [Fact]
    public async Task QueueImmediacy_CanBeChangedInPlace()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        viewModel.InputText = "one";
        viewModel.SubmitToNewQueue();

        var queue = viewModel.Queues[1];
        queue.SelectedImmediacyOption = QueueImmediacyOption.All.First(option => option.Value == AgentInputQueueImmediacy.Held);

        Assert.True(queue.IsHeld);
        Assert.Equal("held", queue.SelectedImmediacyOption.Label);
    }

    [Fact]
    public async Task SingleQueue_HidesNameUntilSecondQueueExists()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });

        Assert.False(viewModel.Queues[0].ShowName);

        viewModel.InputText = "two";
        viewModel.SubmitToNewQueue();

        Assert.True(viewModel.Queues[0].ShowName);
        Assert.True(viewModel.Queues[1].ShowName);
    }
    [Fact]
    public async Task QueueItem_CanBeEditedInPlace()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var queue = chat.QueueManager.CreateInputQueue();
        chat.QueueManager.SetQueueHeld(queue, held: true);
        viewModel.AppendToQueue(queue.Queue.QueueId, "original");

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

    [Fact]
    public async Task SaveAndSendImmediately_MovesEditedMessageToDefaultQueue()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var queue = chat.QueueManager.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);
        viewModel.AppendToQueue(queue.Queue.QueueId, "original");
        var entry = Assert.Single(viewModel.Queues[1].Items);
        entry.EditCommand.Execute(null);
        entry.EditText = "edited";

        entry.SaveAndSendImmediately();
        await WaitForConditionAsync(chat.History, () => chat.History.Count >= 2, "edited queue item submission to complete");

        Assert.Empty(queue.Items);
        Assert.False(entry.IsEditing);
        Assert.Equal("edited", string.Concat(chat.History[0].Contents.OfType<TextContent>().Select(static content => content.Text)));
    }

    [Fact]
    public async Task SaveAndSendImmediately_PreservesNonTextAttachments()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var queue = chat.QueueManager.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);
        viewModel.AppendToQueue(queue.Queue.QueueId, [new TextContent("original"), new DataContent(TinyPng, "image/png")]);
        var entry = Assert.Single(viewModel.Queues[1].Items);
        entry.EditCommand.Execute(null);
        entry.EditText = "edited with image";

        entry.SaveAndSendImmediately();
        await WaitForConditionAsync(chat.History, () => chat.History.Count >= 2, "edited attachment submission to complete");

        Assert.Empty(queue.Items);
        Assert.Equal("edited with image", Assert.IsType<TextContent>(chat.History[0].Contents[0]).Text);
        Assert.Equal("image/png", Assert.IsType<DataContent>(chat.History[0].Contents[1]).MediaType);
    }

    [Fact]
    public async Task SaveAndSendImmediately_MoveRejected_PreservesEditedSourceItem()
    {
        await using var chat = await CreateChatAsync();
        var queue = chat.QueueManager.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);
        var wrappedChat = new InputQueuesOverrideAgentChat(
            chat,
            new RejectingMoveInputQueues(((IAgentChat)chat).InputQueues));
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions
        {
            AgentChat = wrappedChat,
        });
        viewModel.AppendToQueue(queue.Queue.QueueId, "original");
        var entry = Assert.Single(viewModel.Queues.Single(group => group.QueueId == queue.Queue.QueueId).Items);
        entry.EditCommand.Execute(null);
        entry.EditText = "edited";

        await WaitForQueueFailureAsync(
            chat.History,
            () => entry.SaveAndSendImmediatelyAsync(TestContext.Current.CancellationToken));

        Assert.True(entry.IsEditing);
        Assert.Equal("edited", Assert.Single(queue.Items).Text);
        Assert.Contains(chat.History, item =>
            item.Contents.OfType<TextContent>().Any(content =>
                content.Text.Contains("queue change could not be applied", StringComparison.Ordinal)));
        viewModel.Dispose();
    }

    [Fact]
    public async Task QueueRowCommands_RemoteFailures_AreObservedAndReported()
    {
        await using var chat = await CreateChatAsync();
        var queue = chat.QueueManager.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);
        var setupViewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        await setupViewModel.AppendToQueueAsync(
            queue.Queue.QueueId,
            [new TextContent("original")],
            TestContext.Current.CancellationToken);
        setupViewModel.Dispose();
        var wrappedChat = new InputQueuesOverrideAgentChat(
            chat,
            new FaultingInputQueues(((IAgentChat)chat).InputQueues));
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions
        {
            AgentChat = wrappedChat,
        });
        var group = viewModel.Queues.Single(value => value.QueueId == queue.Queue.QueueId);
        var entry = Assert.Single(group.Items);

        await WaitForQueueFailureAsync(
            chat.History,
            () =>
            {
                entry.RemoveCommand.Execute(null);
                return Assert.IsType<AsyncRelayCommand>(entry.RemoveCommand).LastExecutionTask!;
            });
        Assert.Equal(1, QueueFailureCount(chat));

        entry.EditCommand.Execute(null);
        entry.EditText = "edited";
        await WaitForQueueFailureAsync(
            chat.History,
            () =>
            {
                entry.SaveEditCommand.Execute(null);
                return Assert.IsType<AsyncRelayCommand>(entry.SaveEditCommand).LastExecutionTask!;
            });
        Assert.True(entry.IsEditing);
        Assert.Equal(2, QueueFailureCount(chat));

        await WaitForQueueFailureAsync(
            chat.History,
            () =>
            {
                group.SetImmediacyCommand.Execute(group.QueuedImmediacyOption);
                return Assert.IsType<AsyncRelayCommand>(group.SetImmediacyCommand).LastExecutionTask!;
            });
        Assert.Equal(3, QueueFailureCount(chat));

        await WaitForQueueFailureAsync(
            chat.History,
            () =>
            {
                group.RemoveQueueCommand.Execute(null);
                return Assert.IsType<AsyncRelayCommand>(group.RemoveQueueCommand).LastExecutionTask!;
            });
        Assert.Equal(4, QueueFailureCount(chat));
        viewModel.Dispose();
    }

    [Fact]
    public async Task EditEntry_WhenEditing_ExposesShortcutHintAndVisibility()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var queue = chat.QueueManager.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);
        viewModel.AppendToQueue(queue.Queue.QueueId, "original");
        var entry = Assert.Single(viewModel.Queues[1].Items);

        Assert.False(entry.ShowEditHint);
        entry.EditCommand.Execute(null);

        Assert.True(entry.ShowEditHint);
        Assert.Contains("Enter", entry.EditShortcutHint);
        Assert.Contains("Shift+Enter", entry.EditShortcutHint);
        Assert.Contains("Ctrl+Enter", entry.EditShortcutHint);

        entry.CancelEdit();
        Assert.False(entry.ShowEditHint);
    }

    [Fact]
    public async Task SaveEdit_Enter_DoesNotSendToAgent()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var queue = chat.QueueManager.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);
        viewModel.AppendToQueue(queue.Queue.QueueId, "original");
        var entry = Assert.Single(viewModel.Queues[1].Items);
        entry.EditCommand.Execute(null);
        entry.EditText = "saved only";

        entry.SaveEdit();

        Assert.Equal("saved only", Assert.Single(queue.Items).Text);
        Assert.False(entry.IsEditing);
        Assert.Empty(chat.History);
    }

    [Fact]
    public async Task QueueItem_CanRemoveImageAttachment()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var queue = chat.QueueManager.CreateInputQueue();
        chat.QueueManager.SetQueueHeld(queue, held: true);
        viewModel.AppendToQueue(queue.Queue.QueueId, [new TextContent("hello"), new DataContent(TinyPng, "image/png")]);

        var queueItem = Assert.Single(viewModel.Queues[1].Items);
        var attachment = Assert.Single(queueItem.Attachments);
        attachment.RemoveCommand.Execute(null);

        Assert.Equal("hello", viewModel.Queues[1].Items[0].Text);
        Assert.Empty(viewModel.Queues[1].Items[0].Attachments);
        Assert.Equal("hello", chat.InputQueues[1].Items[0].Text);
        Assert.Single(chat.InputQueues[1].Items[0].Contents);
    }

    [Fact]
    public async Task CreateNewQueueCommand_CreatesQueueWithoutSending()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        viewModel.InputText = "unsent text";

        viewModel.CreateNewQueue();

        Assert.Equal(2, chat.InputQueues.Count);
        Assert.Equal("unsent text", viewModel.InputText);
        Assert.Empty(chat.History);
        Assert.Equal(2, viewModel.Queues.Count);
    }

    [Fact]
    public async Task CreateNewQueueCommand_SelectsNewQueueAsActive()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        viewModel.InputText = "first";
        viewModel.CreateNewQueue();

        viewModel.InputText = "second";
        viewModel.SubmitToMostRecentQueue();
        await WaitForConditionAsync(chat.History, () => chat.History.Count >= 2, "submission to most recent queue");

        Assert.Equal(2, chat.InputQueues.Count);
        Assert.Equal("second", string.Concat(chat.History[0].Contents.OfType<TextContent>().Select(static c => c.Text)));
        Assert.Empty(viewModel.InputText);
    }


    [Fact]
    public async Task SubmitToMostRecentQueue_WithTextAndAttachment_SendsBothAndClearsState()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        viewModel.HoldAllQueues();
        viewModel.CreateNewQueue();
        viewModel.DefaultComposer.AppendImageAttachment(TinyPng, "image/png", 640, 480, "shot.png");
        viewModel.DefaultComposer.InputText += " hello";

        viewModel.SubmitToMostRecentQueue();

        Assert.Empty(viewModel.InputText);
        Assert.False(viewModel.DefaultComposer.HasAttachments);
        Assert.Single(viewModel.Queues[1].Items);
        var item = viewModel.Queues[1].Items[0];
        Assert.Contains("hello", item.Text);
        Assert.Single(item.Attachments);
        Assert.Equal("image/png", item.Attachments[0].Label);
    }

    [Fact]
    public async Task SubmitToMostRecentQueue_WithAttachmentOnly_SubmitsAndClearsState()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        viewModel.HoldAllQueues();
        viewModel.CreateNewQueue();
        viewModel.DefaultComposer.AppendImageAttachment(TinyPng, "image/png", 640, 480, "shot.png");
        // InputText contains only the placeholder; sanitised text is empty — should still submit

        var submitted = viewModel.SubmitToMostRecentQueue();

        Assert.True(submitted);
        Assert.Empty(viewModel.InputText);
        Assert.False(viewModel.DefaultComposer.HasAttachments);
        var item = Assert.Single(viewModel.Queues[1].Items);
        Assert.Single(item.Attachments);
    }

    [Fact]
    public async Task SubmitToMostRecentQueue_WithEmptyComposer_DoesNotSubmit()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        viewModel.HoldAllQueues();
        viewModel.CreateNewQueue();

        var submitted = viewModel.SubmitToMostRecentQueue();

        Assert.False(submitted);
        Assert.Empty(viewModel.Queues[1].Items);
    }

    [Fact]
    public async Task ToggleHoldAllQueues_WhenAnyNotHeld_HoldsAllQueues()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        viewModel.InputText = "hello";
        viewModel.SubmitToNewQueue();

        viewModel.ToggleHoldAllQueues();

        Assert.All(chat.InputQueues, queue => Assert.True(queue.IsHeld));
    }

    [Fact]
    public async Task ToggleHoldAllQueues_WhenAllHeld_UnholdsAllQueues()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        viewModel.InputText = "hello";
        viewModel.SubmitToNewQueue();
        viewModel.ToggleHoldAllQueues();

        viewModel.ToggleHoldAllQueues();

        Assert.All(chat.InputQueues, queue => Assert.False(queue.IsHeld));
    }

    [Fact]
    public async Task HoldAllQueues_AlwaysHoldsAllQueues()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        viewModel.InputText = "one";
        viewModel.SubmitToNewQueue();
        var queueVm = viewModel.Queues[1];

        viewModel.HoldAllQueues();

        Assert.All(chat.InputQueues, queue => Assert.True(queue.IsHeld));
        Assert.All(viewModel.Queues, queue => Assert.Equal("held", queue.SelectedImmediacyOption.Label));
        Assert.Same(queueVm, viewModel.Queues[1]);
    }

    [Fact]
    public async Task UnholdAllQueues_AlwaysUnholdsAllQueues()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        viewModel.SubmitToNewQueue();
        viewModel.HoldAllQueues();

        viewModel.UnholdAllQueues();

        Assert.All(chat.InputQueues, queue => Assert.False(queue.IsHeld));
    }

    [Fact]
    public async Task NonDefaultQueue_SetImmediacyCommand_ChangesSelectedImmediacyOptionLabel()
    {
        // Issue #127: the header status pill binds SetImmediacyCommand on InputQueueGroupViewModel.
        // Verify the command path correctly updates SelectedImmediacyOption.Label.
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        viewModel.InputText = "one";
        viewModel.SubmitToNewQueue();

        var queueVm = viewModel.Queues[1];
        // Ctrl+Shift+Q creates the queue Held (issue #1070).
        Assert.Equal("held", queueVm.SelectedImmediacyOption.Label);

        queueVm.SetImmediacyCommand.Execute(queueVm.QueuedImmediacyOption);

        Assert.Equal("queued", queueVm.SelectedImmediacyOption.Label);
        Assert.False(queueVm.IsHeld);
    }

    [Fact]
    public async Task HoldAndUnholdAllQueues_PreserveQueueViewModels()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
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

    [Fact]
    public void QueueImmediacyOption_AllOptions_HaveGlyphAndBrushProperties()
    {
        Assert.Equal("⏩", QueueImmediacyOption.All.Single(option => option.Value == AgentInputQueueImmediacy.Immediate).GlyphText);
        Assert.Equal("▶", QueueImmediacyOption.All.Single(option => option.Value == AgentInputQueueImmediacy.Queue).GlyphText);
        Assert.Equal("⏸", QueueImmediacyOption.All.Single(option => option.Value == AgentInputQueueImmediacy.Held).GlyphText);

        var backgrounds = QueueImmediacyOption.All
            .Select(option => Assert.IsType<SolidColorBrush>(option.Background).Color)
            .ToArray();
        var borders = QueueImmediacyOption.All
            .Select(option => Assert.IsType<SolidColorBrush>(option.BorderBrush).Color)
            .ToArray();

        Assert.Equal(3, backgrounds.Distinct().Count());
        Assert.Equal(3, borders.Distinct().Count());
        Assert.All(QueueImmediacyOption.All, option => Assert.Same(Brushes.White, option.Foreground));
    }

    [Fact]
    public async Task SubmitToMostRecentQueue_AfterManualSubmitToExistingQueue_RoutesToManuallySubmittedQueue()
    {
        // Issue #302: Ctrl+Q should track which queue was most recently *used* (submitted to),
        // not just which was most recently *created*.
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });

        var q1 = chat.QueueManager.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);
        var q2 = chat.QueueManager.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);

        // Manually submit to q1 — q1 should now be the MRU even though q2 was created last.
        viewModel.AppendToQueue(q1.Queue.QueueId, "to q1");

        viewModel.InputText = "ctrl-q target";
        viewModel.SubmitToMostRecentQueue();

        Assert.Equal(2, viewModel.Queues[1].Items.Count);
        Assert.Empty(viewModel.Queues[2].Items);
    }

    [Fact]
    public async Task SubmitToMostRecentQueue_AfterDeletingMruQueue_RoutesToNextMostRecentlyUsedQueue()
    {
        // Issue #302: after deleting the MRU queue, Ctrl+Q should route to the next most recently
        // used surviving queue, not unconditionally to the default queue.
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });

        var q1 = chat.QueueManager.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);
        var q2 = chat.QueueManager.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);

        viewModel.AppendToQueue(q2.Queue.QueueId, "to q2");
        viewModel.AppendToQueue(q1.Queue.QueueId, "to q1"); // q1 is now MRU

        viewModel.RemoveInputQueue(q1.Queue.QueueId);

        viewModel.InputText = "after deletion";
        viewModel.SubmitToMostRecentQueue();

        // q2 is the only surviving non-default queue and should be the target.
        // It already has "to q2" plus the newly routed "after deletion".
        Assert.Equal(2, viewModel.Queues[1].Items.Count);
        Assert.Equal("after deletion", viewModel.Queues[1].Items[1].Text);
    }

    [Fact]
    public async Task SubmitToMostRecentQueue_WhenDefaultIsImmediate_AutoCreatesQueuedQueue()
    {
        // Issue #302 (additional behaviour): when the default queue is immediate and no non-default
        // queue exists, Ctrl+Q should auto-create a queued queue and submit to it.
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat, DefaultQueueId = ((IAgentChat)chat).InputQueues.ImmediateQueue.Snapshot.QueueId, HiddenBuiltInQueueId = ((IAgentChat)chat).InputQueues.DefaultQueue.Snapshot.QueueId });
        viewModel.InputText = "auto queued";

        var submitted = viewModel.SubmitToMostRecentQueue();

        Assert.True(submitted);
        Assert.Equal(2, chat.InputQueues.Count);
        Assert.Empty(viewModel.InputText);
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

    /// <summary>
    /// Regression test for issues #268 #449: concurrent calls to Refresh() must not throw
    /// ArgumentOutOfRangeException or InvalidOperationException from the ObservableCollection.
    /// </summary>
    [Fact]
    public async Task InputQueueGroupViewModel_Refresh_ConcurrentCalls_DoNotThrow()
    {
        await using var chat = await CreateChatAsync();

        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });

        // Hold the queue so messages stay in the queue and give Refresh() items to display.
        chat.QueueManager.SetQueueImmediacy(chat.DefaultInputQueue, AgentInputQueueImmediacy.Held);
        viewModel.AppendToQueue(chat.DefaultInputQueue.Queue.QueueId, "item1");
        viewModel.AppendToQueue(chat.DefaultInputQueue.Queue.QueueId, "item2");

        var groupViewModel = viewModel.Queues[0];
        Assert.NotNull(groupViewModel);

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                try
                {
                    groupViewModel.Refresh();
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.Empty(exceptions);

        viewModel.Dispose();
    }

    private sealed class RejectingMoveInputQueues(IAgentInputQueues inner) : IAgentInputQueues
    {
        public AgentInputQueuesSnapshot Snapshot => inner.Snapshot;
        public IReadOnlyList<IAgentInputQueue> Queues => inner.Queues;
        public IAgentInputQueue DefaultQueue => inner.DefaultQueue;
        public IAgentInputQueue ImmediateQueue => inner.ImmediateQueue;
        public event EventHandler? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public Task<AgentInputQueueCommandResult> CreateQueueAsync(CreateAgentInputQueueRequest request, CancellationToken ct = default) => inner.CreateQueueAsync(request, ct);
        public Task<AgentInputQueueCommandResult> DeleteQueueAsync(DeleteAgentInputQueueRequest request, CancellationToken ct = default) => inner.DeleteQueueAsync(request, ct);
        public Task<AgentInputQueueCommandResult> EnqueueAsync(EnqueueAgentInputRequest request, CancellationToken ct = default) => inner.EnqueueAsync(request, ct);
        public Task<AgentInputQueueCommandResult> EditAsync(EditAgentInputQueueItemRequest request, CancellationToken ct = default) => inner.EditAsync(request, ct);
        public Task<AgentInputQueueCommandResult> RemoveAsync(RemoveAgentInputQueueItemRequest request, CancellationToken ct = default) => inner.RemoveAsync(request, ct);
        public Task<AgentInputQueueCommandResult> MoveAsync(MoveAgentInputQueueItemRequest request, CancellationToken ct = default)
            => Task.FromResult(new AgentInputQueueCommandResult
            {
                CommandId = request.CommandId,
                Status = AgentInputQueueCommandStatus.Rejected,
                Revision = inner.Snapshot.Revision,
                ErrorCode = AgentInputQueueErrorCodes.UnknownQueue,
            });
        public Task<AgentInputQueueCommandResult> ConfigureAsync(ConfigureAgentInputQueueRequest request, CancellationToken ct = default) => inner.ConfigureAsync(request, ct);
        public AgentInputQueueCommandResult CreateQueue(CreateAgentInputQueueRequest request) => inner.CreateQueue(request);
        public AgentInputQueueCommandResult DeleteQueue(DeleteAgentInputQueueRequest request) => inner.DeleteQueue(request);
        public AgentInputQueueCommandResult Enqueue(EnqueueAgentInputRequest request) => inner.Enqueue(request);
        public AgentInputQueueCommandResult Edit(EditAgentInputQueueItemRequest request) => inner.Edit(request);
        public AgentInputQueueCommandResult Remove(RemoveAgentInputQueueItemRequest request) => inner.Remove(request);
        public AgentInputQueueCommandResult Move(MoveAgentInputQueueItemRequest request) => new()
        {
            CommandId = request.CommandId,
            Status = AgentInputQueueCommandStatus.Rejected,
            Revision = inner.Snapshot.Revision,
            ErrorCode = AgentInputQueueErrorCodes.UnknownQueue,
        };
        public AgentInputQueueCommandResult Configure(ConfigureAgentInputQueueRequest request) => inner.Configure(request);
    }

    private sealed class FaultingInputQueues(IAgentInputQueues inner) : IAgentInputQueues
    {
        public AgentInputQueuesSnapshot Snapshot => inner.Snapshot;
        public IReadOnlyList<IAgentInputQueue> Queues => inner.Queues;
        public IAgentInputQueue DefaultQueue => inner.DefaultQueue;
        public IAgentInputQueue ImmediateQueue => inner.ImmediateQueue;
        public event EventHandler? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public Task<AgentInputQueueCommandResult> CreateQueueAsync(
            CreateAgentInputQueueRequest request,
            CancellationToken ct = default) => Fail();
        public Task<AgentInputQueueCommandResult> DeleteQueueAsync(
            DeleteAgentInputQueueRequest request,
            CancellationToken ct = default) => Fail();
        public Task<AgentInputQueueCommandResult> EnqueueAsync(
            EnqueueAgentInputRequest request,
            CancellationToken ct = default) => inner.EnqueueAsync(request, ct);
        public Task<AgentInputQueueCommandResult> EditAsync(
            EditAgentInputQueueItemRequest request,
            CancellationToken ct = default) => Fail();
        public Task<AgentInputQueueCommandResult> RemoveAsync(
            RemoveAgentInputQueueItemRequest request,
            CancellationToken ct = default) => Fail();
        public Task<AgentInputQueueCommandResult> MoveAsync(
            MoveAgentInputQueueItemRequest request,
            CancellationToken ct = default) => Fail();
        public Task<AgentInputQueueCommandResult> ConfigureAsync(
            ConfigureAgentInputQueueRequest request,
            CancellationToken ct = default) => Fail();
        public AgentInputQueueCommandResult CreateQueue(CreateAgentInputQueueRequest request) =>
            throw Failure();
        public AgentInputQueueCommandResult DeleteQueue(DeleteAgentInputQueueRequest request) =>
            throw Failure();
        public AgentInputQueueCommandResult Enqueue(EnqueueAgentInputRequest request) =>
            inner.Enqueue(request);
        public AgentInputQueueCommandResult Edit(EditAgentInputQueueItemRequest request) =>
            throw Failure();
        public AgentInputQueueCommandResult Remove(RemoveAgentInputQueueItemRequest request) =>
            throw Failure();
        public AgentInputQueueCommandResult Move(MoveAgentInputQueueItemRequest request) =>
            throw Failure();
        public AgentInputQueueCommandResult Configure(ConfigureAgentInputQueueRequest request) =>
            throw Failure();

        private static Task<AgentInputQueueCommandResult> Fail() =>
            Task.FromException<AgentInputQueueCommandResult>(Failure());

        private static RemoteAgentProtocolException Failure() =>
            new("The remote session channel closed.");
    }

    private static int QueueFailureCount(AgentChat chat) =>
        chat.History.Count(IsQueueFailure);

    private static bool IsQueueFailure(AgentChatHistoryItem item) =>
        item.Contents
            .OfType<TextContent>()
            .Any(content => content.Text.Contains(
                "queue change could not be applied",
                StringComparison.Ordinal));

    private static async Task WaitForQueueFailureAsync(
        System.Collections.Specialized.INotifyCollectionChanged history,
        Func<Task> operation)
    {
        var added = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCollectionChanged(
            object? sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        {
            if (args.NewItems?.OfType<AgentChatHistoryItem>().Any(IsQueueFailure) is true)
                added.TrySetResult();
        }

        history.CollectionChanged += OnCollectionChanged;
        try
        {
            await operation();
            await added.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            history.CollectionChanged -= OnCollectionChanged;
        }
    }

    private sealed class InputQueuesOverrideAgentChat(
        IAgentChat inner,
        IAgentInputQueues inputQueues) : IAgentChat
    {
        public AgentInformation Information => inner.Information;
        public Usage Usage => inner.Usage;
        public bool IsBusy => inner.IsBusy;
        public AgentChatHistoryCollection History => inner.History;
        public Task HistoryPopulated => inner.HistoryPopulated;
        public AgentChatRunningItemCollection RunningItems => inner.RunningItems;
        public IAgentInputQueues InputQueues { get; } = inputQueues;
        public System.Collections.ObjectModel.ReadOnlyObservableCollection<IRunningSubAgent> SubAgents => inner.SubAgents;
        public System.Collections.ObjectModel.ReadOnlyObservableCollection<AgentChatModal> Modals => inner.Modals;
        public Phantom.Workspaces.Llm.SlashCommands.ISlashCommandRegistry SlashCommands => inner.SlashCommands;
        public event EventHandler? InformationChanged
        {
            add => inner.InformationChanged += value;
            remove => inner.InformationChanged -= value;
        }
        public event EventHandler? ToolsChanged
        {
            add => inner.ToolsChanged += value;
            remove => inner.ToolsChanged -= value;
        }
        public event EventHandler? UsageChanged
        {
            add => inner.UsageChanged += value;
            remove => inner.UsageChanged -= value;
        }
        public event EventHandler<AgentChatHistoryItem>? TurnCompleted
        {
            add => inner.TurnCompleted += value;
            remove => inner.TurnCompleted -= value;
        }
        public IReadOnlyList<AgentChatToolItem> GetToolSnapshot() => inner.GetToolSnapshot();
        public Task SetToolEnabledAsync(string toolId, bool enabled, CancellationToken ct = default) => inner.SetToolEnabledAsync(toolId, enabled, ct);
        public Task RespondToModalAsync(string modalId, System.Text.Json.JsonElement response, CancellationToken ct = default) => inner.RespondToModalAsync(modalId, response, ct);
        public void EnqueueSystemNote(string text) => inner.EnqueueSystemNote(text);
        public void EnqueueHelpNote(string text) => inner.EnqueueHelpNote(text);
        public void EnqueueTransientDiagnostic(string text) => inner.EnqueueTransientDiagnostic(text);
        public Task InterruptAsync(CancellationToken ct = default) => inner.InterruptAsync(ct);
        public object? GetService(Type serviceType) => inner.GetService(serviceType);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
