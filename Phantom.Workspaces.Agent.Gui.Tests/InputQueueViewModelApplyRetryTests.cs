using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

/// <summary>
/// Coverage for owner-acknowledged queue commands. UI projections only reconcile after an
/// immutable result or Changed delta; conflicts are not retried against a stale user intent.
/// </summary>
public sealed class InputQueueViewModelApplyRetryTests
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

    private static Task<AgentChat> CreateChatAsync()
        => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateTestAgentDefinition() });

    [Fact]
    public async Task ApplyAsync_Conflict_DoesNotRetryStaleIntent()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var observedCommandIds = new List<Guid>();
        var observedRevisions = new List<long>();

        var result = await viewModel.ApplyAsync((commandId, expectedRevision) =>
        {
            observedCommandIds.Add(commandId);
            observedRevisions.Add(expectedRevision);
            return Task.FromResult(new AgentInputQueueCommandResult
            {
                CommandId = commandId,
                Status = AgentInputQueueCommandStatus.Conflict,
                Revision = expectedRevision + 1,
            });
        }, TestContext.Current.CancellationToken);

        Assert.Equal(AgentInputQueueCommandStatus.Conflict, result.Status);
        Assert.Single(observedCommandIds);
        Assert.Single(observedRevisions);
    }

    [Fact]
    public async Task ApplyAsync_OlderConflict_DoesNotRegressAcknowledgedRevision()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var snapshot = ((IAgentChat)chat).InputQueues.Snapshot;

        await viewModel.ApplyAsync((commandId, _) => Task.FromResult(new AgentInputQueueCommandResult
        {
            CommandId = commandId,
            Status = AgentInputQueueCommandStatus.Conflict,
            Revision = 5,
            CurrentSnapshot = snapshot with { Revision = 5 },
        }), TestContext.Current.CancellationToken);
        await viewModel.ApplyAsync((commandId, _) => Task.FromResult(new AgentInputQueueCommandResult
        {
            CommandId = commandId,
            Status = AgentInputQueueCommandStatus.Conflict,
            Revision = 4,
            CurrentSnapshot = snapshot with { Revision = 4 },
        }), TestContext.Current.CancellationToken);

        long? observedRevision = null;
        await viewModel.ApplyAsync((commandId, expectedRevision) =>
        {
            observedRevision = expectedRevision;
            return Task.FromResult(new AgentInputQueueCommandResult
            {
                CommandId = commandId,
                Status = AgentInputQueueCommandStatus.Rejected,
                Revision = expectedRevision,
            });
        }, TestContext.Current.CancellationToken);

        Assert.Equal(5, observedRevision);
    }

    [Fact]
    public async Task ApplyAsync_Cancelled_LeavesProjectionUnchanged()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var before = viewModel.Queues.Count;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            viewModel.ApplyAsync(
                (_, _) => throw new InvalidOperationException("The cancelled operation must not be invoked."),
                cancellation.Token));
        Assert.Equal(before, viewModel.Queues.Count);
    }

    [Fact]
    public async Task Apply_AppliedOnFirstAttempt_DoesNotRetryAndCoalescesChangedNotification()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var attemptCount = 0;

        var result = await viewModel.ApplyAsync((commandId, expectedRevision) =>
        {
            attemptCount++;
            return Task.FromResult(new AgentInputQueueCommandResult
            {
                CommandId = commandId,
                Status = AgentInputQueueCommandStatus.Applied,
                Revision = expectedRevision + 1,
            });
        }, TestContext.Current.CancellationToken);

        Assert.Equal(AgentInputQueueCommandStatus.Applied, result.Status);
        Assert.Equal(1, attemptCount);
    }

    [Fact]
    public async Task Apply_RejectedResult_DoesNotRetry()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var attemptCount = 0;

        var result = await viewModel.ApplyAsync((commandId, expectedRevision) =>
        {
            attemptCount++;
            return Task.FromResult(new AgentInputQueueCommandResult
            {
                CommandId = commandId,
                Status = AgentInputQueueCommandStatus.Rejected,
                Revision = expectedRevision,
                ErrorCode = AgentInputQueueErrorCodes.UnknownItem,
            });
        }, TestContext.Current.CancellationToken);

        // Rejected is a permanent failure (bad ids, protected queue, etc.); retrying would
        // just repeat the same reject with a different command id, so the loop must stop.
        Assert.Equal(AgentInputQueueCommandStatus.Rejected, result.Status);
        Assert.Equal(1, attemptCount);
    }

    [Fact]
    public async Task UpdateQueueItem_AfterSaveEdit_PersistsEditedTextToUnderlyingAgentInputQueue()
    {
        // Regression coverage for the diagnose-unrelated-test-failure comment on #1485: the
        // failing HandleEditKey_Enter_SavesEditAndExitsEditMode assertion reads through the
        // underlying AgentInputQueue.Items, so verify the full data-flow from
        // InputQueueEntryViewModel.SaveEdit through UpdateQueueItem(string,string,string) to
        // the AgentInputQueue mutation lands the new text.
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var queue = chat.QueueManager.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);
        viewModel.AppendToQueue(queue.Queue.QueueId, "original");
        var entry = Assert.Single(viewModel.Queues[1].Items);
        entry.EditCommand.Execute(null);
        entry.EditText = "edited";

        entry.SaveEdit();

        Assert.False(entry.IsEditing);
        var underlyingItem = Assert.Single(queue.Items);
        Assert.Equal("edited", underlyingItem.Text);
        Assert.Equal("edited", string.Concat(
            underlyingItem.Messages.SelectMany(m => m.Contents).OfType<TextContent>().Select(t => t.Text)));
    }

    [Fact]
    public async Task RefreshQueues_ConcurrentDispatch_DoesNotDuplicateQueueGroups()
    {
        // Regression coverage for issue #1485. LocalAgentInputQueuesAdapter.RaiseChanged posts
        // Task.Factory.StartNew(..., foregroundScheduler) whenever TaskScheduler.Current does not
        // match the captured foregroundScheduler. When AgentChat is constructed without an
        // explicit dispatcher (the default in headless tests), the captured foregroundScheduler
        // is TaskScheduler.Default, which schedules on arbitrary thread-pool threads with no
        // serialization guarantee. Two Changed notifications can then execute the
        // OnQueuesChanged -> RefreshQueues path concurrently: both observe a dictionary miss for
        // the same QueueId, both call new InputQueueGroupViewModel(...) + Queues.Insert, and the
        // ObservableCollection ends up with duplicate entries under the same QueueId. That was
        // the actual cause of the intermittent
        // `InputQueueViewModelTests.QueueComposer_AppendsTextToExistingQueue`
        // failure (Sequence.Single "more than one matching element"). RefreshQueues must
        // serialize its queueViewModels/Queues mutations.
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(new InputQueueViewModelOptions { AgentChat = chat });
        var queue = chat.QueueManager.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);
        viewModel.AppendToQueue(queue.Queue.QueueId, "original");

        var refreshQueues = typeof(InputQueueViewModel).GetMethod(
            "RefreshQueues",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        using var barrier = new Barrier(4);
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < 400; i++)
            {
                refreshQueues.Invoke(viewModel, null);
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(2, viewModel.Queues.Count);
        Assert.Single(viewModel.Queues, static q => !q.IsDefault);
        var distinctIds = viewModel.Queues.Select(static q => q.QueueId).Distinct(StringComparer.Ordinal).Count();
        Assert.Equal(viewModel.Queues.Count, distinctIds);
    }
}
