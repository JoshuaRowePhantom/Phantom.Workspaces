using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class InputQueueViewModelTests
{
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

    [Fact]
    public async Task SubmitToDefaultQueue_QueuesTextAndClearsInput()
    {
        var created = AgentFactory.CreateAgentChat(CreateTestAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        viewModel.InputText = "hello";
        viewModel.IsFormattedMode = true;

        viewModel.SubmitToDefaultQueue();

        Assert.Empty(viewModel.InputText);
        Assert.False(viewModel.IsFormattedMode);
        Assert.Equal(2, chat.History.Count);
        var userHistory = chat.History[0];
        Assert.Equal("hello", userHistory.Text);
        Assert.Single(userHistory.Contents);
        Assert.IsType<TextContent>(userHistory.Contents[0]);
    }

    [Fact]
    public async Task SubmitToNewQueue_CreatesQueueWhenManagerIsBound()
    {
        var created = AgentFactory.CreateAgentChat(CreateTestAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.InputText = "queued";

        viewModel.SubmitToNewQueue();

        Assert.Equal(2, chat.InputQueues.Count);
        Assert.Equal(2, chat.History.Count);
        Assert.Equal("queued", chat.History[0].Text);
        Assert.Empty(viewModel.Queues[1].Items);
        Assert.Equal(2, viewModel.Queues.Count);
        Assert.Equal("queued", viewModel.Queues[1].SelectedImmediacyOption.Label);
    }

    [Fact]
    public async Task SubmitToNewQueue_ProvidesRemoveCommandForQueuedItems()
    {
        var created = AgentFactory.CreateAgentChat(CreateTestAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var queue = chat.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);
        viewModel.AppendToQueue(queue, "remove me");

        var item = Assert.Single(viewModel.Queues[1].Items);
        item.RemoveCommand.Execute(null);

        Assert.Empty(viewModel.Queues[1].Items);
    }

    [Fact]
    public async Task QueueComposer_AppendsTextToExistingQueue()
    {
        var created = AgentFactory.CreateAgentChat(CreateTestAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var queue = chat.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);
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

    [Fact]
    public async Task QueueComposer_CanAttachImageAndSubmitStructuredContent()
    {
        var created = AgentFactory.CreateAgentChat(CreateTestAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.DefaultComposer.AppendImageAttachment([0x01, 0x02, 0x03], "image/png", 640, 480, "shot.png");

        Assert.Equal("[image 640x480 shot.png]", viewModel.InputText);
        Assert.True(viewModel.DefaultComposer.HasAttachments);

        viewModel.SubmitToDefaultQueue();

        Assert.False(viewModel.DefaultComposer.HasAttachments);
        Assert.Equal(string.Empty, viewModel.InputText);
        Assert.Equal(2, chat.History.Count);
        Assert.Equal("[image/png]", chat.History[0].Text);
        Assert.Single(chat.History[0].Contents);
        Assert.IsType<DataContent>(chat.History[0].Contents[0]);
    }

    [Fact]
    public async Task SubmitToNewQueue_WhenQueuesAreHeld_CreatesHeldQueue()
    {
        var created = AgentFactory.CreateAgentChat(CreateTestAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;

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

    [Fact]
    public async Task QueueImmediacy_CanBeChangedInPlace()
    {
        var created = AgentFactory.CreateAgentChat(CreateTestAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
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
        var created = AgentFactory.CreateAgentChat(CreateTestAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);

        Assert.False(viewModel.Queues[0].ShowName);

        viewModel.InputText = "two";
        viewModel.SubmitToNewQueue();

        Assert.True(viewModel.Queues[0].ShowName);
        Assert.True(viewModel.Queues[1].ShowName);
    }
    [Fact]
    public async Task QueueItem_CanBeEditedInPlace()
    {
        var created = AgentFactory.CreateAgentChat(CreateTestAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var queue = chat.CreateInputQueue();
        chat.SetQueueHeld(queue, held: true);
        viewModel.AppendToQueue(queue, "original");

        var queueItem = Assert.Single(viewModel.Queues[1].Items);
        queueItem.EditCommand.Execute(null);
        queueItem.EditText = "edited";
        queueItem.SaveEditCommand.Execute(null);

        Assert.Equal("edited", viewModel.Queues[1].Items[0].Text);
        Assert.Equal("edited", chat.InputQueues[1].Items[0].Text);
        Assert.Empty(chat.History);
    }

    [Fact]
    public async Task ToggleHoldAllQueues_WhenAnyNotHeld_HoldsAllQueues()
    {
        var created = AgentFactory.CreateAgentChat(CreateTestAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.SubmitToNewQueue();

        viewModel.ToggleHoldAllQueues();

        Assert.All(chat.InputQueues, queue => Assert.True(queue.IsHeld));
    }

    [Fact]
    public async Task ToggleHoldAllQueues_WhenAllHeld_UnholdsAllQueues()
    {
        var created = AgentFactory.CreateAgentChat(CreateTestAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.SubmitToNewQueue();
        viewModel.ToggleHoldAllQueues();

        viewModel.ToggleHoldAllQueues();

        Assert.All(chat.InputQueues, queue => Assert.False(queue.IsHeld));
    }

    [Fact]
    public async Task HoldAllQueues_AlwaysHoldsAllQueues()
    {
        var created = AgentFactory.CreateAgentChat(CreateTestAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.SubmitToNewQueue();

        viewModel.HoldAllQueues();

        Assert.All(chat.InputQueues, queue => Assert.True(queue.IsHeld));
        Assert.All(viewModel.Queues, queue => Assert.Equal("held", queue.SelectedImmediacyOption.Label));
    }

    [Fact]
    public async Task UnholdAllQueues_AlwaysUnholdsAllQueues()
    {
        var created = AgentFactory.CreateAgentChat(CreateTestAgentDefinition());
        await using var chat = created.Chat;
        using var _ = created.Client;

        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        viewModel.SubmitToNewQueue();
        viewModel.HoldAllQueues();

        viewModel.UnholdAllQueues();

        Assert.All(chat.InputQueues, queue => Assert.False(queue.IsHeld));
    }
}
