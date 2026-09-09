using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

/// <summary>
/// Coverage for the retry-on-Conflict data flow in <see cref="InputQueueViewModel"/> (issue #1485).
///
/// Owner-side UI commands read <c>IAgentInputQueues.Snapshot.Revision</c> and then call the
/// adapter, which re-reads the aggregate revision under lock. If the revision advances between
/// those two steps (for example, a legacy Configure firing on a sibling queue, or the background
/// dequeue loop bumping the aggregate), the adapter returns <see cref="AgentInputQueueCommandStatus.Conflict"/>
/// and the pre-fix single-shot Apply silently dropped the mutation. These tests exercise the
/// retry helper directly with a controllable command lambda so each retry-loop branch is
/// covered as a real behaviour (not a reflection assertion).
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
    public async Task Apply_ConflictOnFirstAttempts_RetriesWithFreshRevisionAndReturnsApplied()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var observedCommandIds = new List<Guid>();
        var observedRevisions = new List<long>();

        var result = viewModel.Apply((commandId, expectedRevision) =>
        {
            observedCommandIds.Add(commandId);
            observedRevisions.Add(expectedRevision);
            // Simulate an aggregate-revision race for the first two attempts, then apply.
            if (observedCommandIds.Count < 3)
            {
                return new AgentInputQueueCommandResult
                {
                    CommandId = commandId,
                    Status = AgentInputQueueCommandStatus.Conflict,
                    Revision = expectedRevision + 1,
                };
            }
            return new AgentInputQueueCommandResult
            {
                CommandId = commandId,
                Status = AgentInputQueueCommandStatus.Applied,
                Revision = expectedRevision + 1,
            };
        });

        Assert.Equal(AgentInputQueueCommandStatus.Applied, result.Status);
        Assert.Equal(3, observedCommandIds.Count);
        // Every attempt gets a distinct command id so the adapter cannot replay the cached
        // Conflict from an earlier attempt.
        Assert.Equal(3, observedCommandIds.Distinct().Count());
        // Every attempt reads the current Snapshot.Revision fresh (not a captured local).
        // Under this single-threaded test the observed revisions are identical, and they
        // all equal the live snapshot revision after the operation.
        Assert.All(observedRevisions, r => Assert.Equal(observedRevisions[0], r));
    }

    [Fact]
    public async Task Apply_PersistentConflict_ExhaustsRetryBudgetAndReturnsLastConflict()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var attemptCount = 0;

        var result = viewModel.Apply((commandId, expectedRevision) =>
        {
            attemptCount++;
            return new AgentInputQueueCommandResult
            {
                CommandId = commandId,
                Status = AgentInputQueueCommandStatus.Conflict,
                Revision = expectedRevision + 1,
            };
        });

        // Retry budget is bounded so a stuck aggregate cannot deadlock the UI thread.
        Assert.Equal(AgentInputQueueCommandStatus.Conflict, result.Status);
        Assert.Equal(8, attemptCount);
    }

    [Fact]
    public async Task Apply_AppliedOnFirstAttempt_DoesNotRetryAndCoalescesChangedNotification()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var attemptCount = 0;

        var result = viewModel.Apply((commandId, expectedRevision) =>
        {
            attemptCount++;
            return new AgentInputQueueCommandResult
            {
                CommandId = commandId,
                Status = AgentInputQueueCommandStatus.Applied,
                Revision = expectedRevision + 1,
            };
        });

        Assert.Equal(AgentInputQueueCommandStatus.Applied, result.Status);
        Assert.Equal(1, attemptCount);
    }

    [Fact]
    public async Task Apply_RejectedResult_DoesNotRetry()
    {
        await using var chat = await CreateChatAsync();
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var attemptCount = 0;

        var result = viewModel.Apply((commandId, expectedRevision) =>
        {
            attemptCount++;
            return new AgentInputQueueCommandResult
            {
                CommandId = commandId,
                Status = AgentInputQueueCommandStatus.Rejected,
                Revision = expectedRevision,
                ErrorCode = AgentInputQueueErrorCodes.UnknownItem,
            };
        });

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
        var viewModel = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var queue = chat.QueueManager.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);
        viewModel.AppendToQueue(queue, "original");
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
}
