using AgentSchema;
using Microsoft.Extensions.AI;
using System.Collections.Specialized;

namespace Phantom.Workspaces.Llm.Tests;

public class AgentChatHistoryPromotionTests
{
    private const string DefaultAgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "echo-agent",
          "model": {
            "id": "echo",
            "provider": "echo",
            "apiType": "Echo"
          },
          "tools": []
        }
        """;

    [Fact]
    public async Task SingleMessageStream_NotInHistory_UntilTurnEnd()
    {
        // With only one streaming message no items are promoted during streaming
        // (stableCount = max(0, 1-1) = 0). The single item should only reach History at DrainAsync.
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse(isReady: false);

        await using var chat = await CreateChatAsync(client);

        var update1Holder = stream.EnqueueUpdate(
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("partial")] },
            isReady: false);
        var terminalHolder = stream.Complete(isReady: false);

        chat.EnqueueUserMessage("hi");

        // User message appears in history quickly.
        await WaitForHistoryCountAsync(chat.History, 1, "user message");

        // No assistant items in History yet while only one update is in flight.
        Assert.Single(chat.History);

        // Now allow the stream to complete.
        update1Holder.MarkReady();
        stream.MarkReady();
        terminalHolder.MarkReady();

        // After the turn completes, the assistant item should be in History.
        await WaitForHistoryCountAsync(chat.History, 2, "assistant item after turn end");
        Assert.Equal(2, chat.History.Count);
        Assert.Equal(ChatRole.Assistant, chat.History[1].Role);
    }

    [Fact]
    public async Task TwoMessageStream_FirstInHistory_BeforeTurnEnd()
    {
        // A three-update sequence produces three distinct messages via role transitions:
        //   update[0]: assistant text "msg-one"    → message[0]
        //   update[1]: tool FunctionResultContent  → message[1]   (creates role boundary)
        //   update[2]: assistant text "msg-two"    → message[2]   (finish reason, GATED)
        //
        // After update[0]+update[1] arrive (both ready), CoalesceAsync sees a tool-result as the
        // last update, so it appends a blank assistant placeholder. This produces three items:
        //   [assistant "msg-one", tool-result, blank-placeholder]
        //   stableCount = max(0, 3-1) = 2 → both message[0] and message[1] are promoted to History
        //   while the blank placeholder remains the active tail in the running item.
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse(isReady: false);

        await using var chat = await CreateChatAsync(client);

        // update[0] and update[1] are immediately ready; update[2] is gated.
        stream.EnqueueUpdate(
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("msg-one")] },
            isReady: true);
        stream.EnqueueUpdate(
            new ChatResponseUpdate { Role = ChatRole.Tool, Contents = [new FunctionResultContent("c1", "r1")] },
            isReady: true);
        var update3 = stream.EnqueueUpdate(
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("msg-two")], FinishReason = new ChatFinishReason("stop") },
            isReady: false);
        var terminal = stream.Complete(isReady: false);

        stream.MarkReady();
        chat.EnqueueUserMessage("hi");

        // Wait for user + "msg-one" + tool-result to appear in History.
        // Both are stable (the blank placeholder is the active tail), so they are promoted
        // together mid-stream before update[2] (gated) has arrived.
        await WaitForHistoryCountAsync(chat.History, 3, "user + first two messages in history");

        Assert.Equal(3, chat.History.Count);
        Assert.Equal(ChatRole.Assistant, chat.History[1].Role);
        Assert.Equal("msg-one", GetText(chat.History[1]));
        Assert.Equal(ChatRole.Tool, chat.History[2].Role);

        // Now let the second and third messages arrive and complete.
        update3.MarkReady();
        terminal.MarkReady();

        // After the turn ends: user + msg-one + tool-result + msg-two = 4 items.
        await WaitForHistoryCountAsync(chat.History, 4, "all items in history");
        Assert.Equal(4, chat.History.Count);
    }

    [Fact]
    public async Task PromotedItems_NotDuplicated_AfterTurnEnd()
    {
        // Three updates forming three distinct messages via role transitions:
        //   u0: assistant FunctionCallContent → message[0]
        //   u1: tool    FunctionResultContent → message[1]
        //   u2: assistant text "done"         → message[2]  (finish reason)
        //
        // Promotions happen mid-stream and in DrainAsync. After the turn ends, History must
        // contain exactly four items (user + 3 response messages) with no duplicates.
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new FunctionCallContent("c1", "tool", null)] });
        stream.EnqueueUpdate(new ChatResponseUpdate { Role = ChatRole.Tool,      Contents = [new FunctionResultContent("c1", "ok")] });
        stream.EnqueueUpdate(new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("done")], FinishReason = new ChatFinishReason("stop") });
        stream.Complete();

        await using var chat = await CreateChatAsync(client);

        chat.EnqueueUserMessage("hi");
        await WaitForHistoryCountAsync(chat.History, 4, "user + three response items");

        // Must be exactly 4 items — no duplicates from mid-stream promotions.
        Assert.Equal(4, chat.History.Count);
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant],
            chat.History.Select(h => h.Role).ToArray());
    }

    [Fact]
    public async Task CompleteRunningItem_SkipsAlreadyPromoted()
    {
        // All items should be promoted via PromoteItemsToHistory before CompleteRunningItem runs.
        // CompleteRunningItem should not add them again.
        // Use three role-alternating updates to produce three separate messages:
        //   u0: assistant FunctionCallContent → message[0]
        //   u1: tool    FunctionResultContent → message[1]
        //   u2: assistant text "done"         → message[2]  (finish reason)
        // The conflator pre-promotes items mid-stream; DrainAsync promotes the remainder.
        // CompleteRunningItem finds an empty running item and adds nothing to History.
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new FunctionCallContent("c1", "tool", null)] });
        stream.EnqueueUpdate(new ChatResponseUpdate { Role = ChatRole.Tool,      Contents = [new FunctionResultContent("c1", "ok")] });
        stream.EnqueueUpdate(new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("done")], FinishReason = new ChatFinishReason("stop") });
        stream.Complete();

        await using var chat = await CreateChatAsync(client);

        chat.EnqueueUserMessage("hi");
        await WaitForHistoryCountAsync(chat.History, 4, "turn complete");

        // Exactly 4 items: user + 3 response messages — CompleteRunningItem must not have re-added any.
        Assert.Equal(4, chat.History.Count);
        Assert.Equal(ChatRole.User,      chat.History[0].Role);
        Assert.Equal(ChatRole.Assistant, chat.History[1].Role);
        Assert.Equal(ChatRole.Tool,      chat.History[2].Role);
        Assert.Equal(ChatRole.Assistant, chat.History[3].Role);
        Assert.Equal("done", GetText(chat.History[3]));
    }

    [Fact]
    public async Task MultiMessageStream_IncrementalPromotion()
    {
        // Two pairs of role-alternating updates are promoted in two separate incremental steps,
        // followed by a gated final message at turn end.
        //
        // Pair 1 (u0+u1 immediately ready):
        //   u0: assistant FC("c0"), u1: tool FR("c0")
        //   → CoalesceAsync produces [FC, FR, blank] (3 items), stableCount=2
        //   → FC and FR promoted to History → History: user + FC + FR = 3
        //
        // Pair 2 (u2+u3 released after step-1 verified):
        //   u2: assistant FC("c1"), u3: tool FR("c1")
        //   → CoalesceAsync produces [FC, FR, FC2, FR2, blank], stableCount=4
        //   → FC2 and FR2 also promoted → History: user + FC + FR + FC2 + FR2 = 5
        //
        // Final (u4 released after step-2 verified):
        //   u4: assistant "done" (finishReason=stop)
        //   → DrainAsync promotes last item → History: 6
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse(isReady: false);

        await using var chat = await CreateChatAsync(client);

        stream.EnqueueUpdate(
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new FunctionCallContent("c0", "tool", null)] },
            isReady: true);
        stream.EnqueueUpdate(
            new ChatResponseUpdate { Role = ChatRole.Tool, Contents = [new FunctionResultContent("c0", "r0")] },
            isReady: true);

        var u2 = stream.EnqueueUpdate(
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new FunctionCallContent("c1", "tool", null)] },
            isReady: false);
        var u3 = stream.EnqueueUpdate(
            new ChatResponseUpdate { Role = ChatRole.Tool, Contents = [new FunctionResultContent("c1", "r1")] },
            isReady: false);

        var u4 = stream.EnqueueUpdate(
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("done")], FinishReason = new ChatFinishReason("stop") },
            isReady: false);
        var terminal = stream.Complete(isReady: false);

        stream.MarkReady();
        chat.EnqueueUserMessage("hi");

        // Step 1: user + FC + FR = 3 items after pair 1 is promoted mid-stream.
        await WaitForHistoryCountAsync(chat.History, 3, "step 1: user + FC + FR promoted");
        Assert.Equal(3, chat.History.Count);

        u2.MarkReady();
        u3.MarkReady();

        // Step 2: + FC2 + FR2 = 5 items after pair 2 is promoted mid-stream.
        await WaitForHistoryCountAsync(chat.History, 5, "step 2: + FC2 + FR2 promoted");
        Assert.Equal(5, chat.History.Count);

        u4.MarkReady();
        terminal.MarkReady();

        // Step 3: + "done" = 6 items after turn end.
        await WaitForHistoryCountAsync(chat.History, 6, "step 3: + done after turn end");
        Assert.Equal(6, chat.History.Count);
    }

    [Fact]
    public async Task EmptyStream_HistoryEmpty_AfterTurnEnd()
    {
        // A streaming response with zero updates must complete without throwing
        // and must not add any assistant items to History.
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.Complete();

        await using var chat = await CreateChatAsync(client);
        chat.EnqueueUserMessage("hi");

        // Wait for the user message to appear in History.
        await WaitForHistoryCountAsync(chat.History, 1, "user message");

        // Wait for the running item to disappear, confirming the turn has fully completed.
        await WaitForRunningItemsEmptyAsync(chat.RunningItems);

        // No assistant items were added — History has only the user message.
        Assert.Single(chat.History);
        Assert.Equal(ChatRole.User, chat.History[0].Role);
    }

    [Fact]
    public async Task RunningItem_ContainsOnlyActiveTail_AfterPromotion()
    {
        // After the first two updates arrive (func-call and tool-result), CoalesceAsync appends a
        // blank assistant placeholder because the snapshot ends with a tool result. This produces:
        //   [assistant(func-call), tool-result, blank-placeholder]
        //   stableCount = 2 → [assistant, tool] are promoted to History
        //   running item is updated to [blank-placeholder] only
        //
        // While the third update (assistant text) is still gated, the running item must expose
        // only the blank placeholder (the active tail).
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse(isReady: false);

        await using var chat = await CreateChatAsync(client);

        stream.EnqueueUpdate(
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new FunctionCallContent("c1", "tool")] },
            isReady: true);
        stream.EnqueueUpdate(
            new ChatResponseUpdate { Role = ChatRole.Tool, Contents = [new FunctionResultContent("c1", "result")] },
            isReady: true);
        var third = stream.EnqueueUpdate(
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("active")], FinishReason = new ChatFinishReason("stop") },
            isReady: false);
        var terminal = stream.Complete(isReady: false);

        stream.MarkReady();
        chat.EnqueueUserMessage("hi");

        // Wait until assistant(func-call) and tool-result have been promoted to History
        // (user + func-call + tool-result = 3 items).
        await WaitForHistoryCountAsync(chat.History, 3, "stable items promoted");

        // The running item should hold only the blank placeholder (active tail).
        Assert.Single(chat.RunningItems);
        var runningItem = chat.RunningItems[0];
        Assert.Single(runningItem.Items);
        Assert.Equal(ChatRole.Assistant, runningItem.Items[0].Role);
        // Blank placeholder has no text content.
        Assert.Empty(string.Concat(runningItem.Items[0].Contents.OfType<TextContent>().Select(c => c.Text)));

        third.MarkReady();
        terminal.MarkReady();
    }

    private static async Task<AgentChat> CreateChatAsync(IChatClient client)
    {
        return await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(DefaultAgentDefinitionJson),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = client,
            DisplayNameOverride = "test",
        });
    }

    private static async Task WaitForRunningItemsEmptyAsync(
        System.Collections.Specialized.INotifyCollectionChanged collection)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = Task.Delay(TimeSpan.FromSeconds(30));

        void OnChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (((System.Collections.ICollection)collection).Count == 0)
            {
                signal.TrySetResult();
            }
        }

        collection.CollectionChanged += OnChanged;
        try
        {
            if (((System.Collections.ICollection)collection).Count == 0)
            {
                return;
            }

            var completed = await Task.WhenAny(signal.Task, timeout);
            if (completed == timeout)
            {
                throw new TimeoutException("Timeout waiting for running items to become empty.");
            }
        }
        finally
        {
            collection.CollectionChanged -= OnChanged;
        }
    }

    private static async Task WaitForHistoryCountAsync(
        INotifyCollectionChanged collection,
        int expectedCount,
        string description)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = Task.Delay(TimeSpan.FromSeconds(30));

        void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (TryCheckCount())
            {
                signal.TrySetResult();
            }
        }

        collection.CollectionChanged += OnChanged;
        try
        {
            if (TryCheckCount())
            {
                return;
            }

            var completed = await Task.WhenAny(signal.Task, timeout);
            if (completed == timeout)
            {
                throw new TimeoutException($"Timeout waiting for {description}.");
            }
        }
        finally
        {
            collection.CollectionChanged -= OnChanged;
        }

        bool TryCheckCount()
        {
            try
            {
                return ((System.Collections.ICollection)collection).Count >= expectedCount;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    private static string GetText(AgentChatHistoryItem item)
        => string.Concat(item.Contents.OfType<TextContent>().Select(c => c.Text));
}
