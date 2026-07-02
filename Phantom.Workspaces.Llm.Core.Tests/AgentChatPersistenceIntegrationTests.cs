using AgentSchema;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Tests;

public class AgentChatPersistenceIntegrationTests
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
    public async Task AfterTurn_StoreContainsAllStreamedMessages()
    {
        var store = new InMemoryAgentPersistenceStore();
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("hello")] });
        stream.EnqueueUpdate(new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent(" world")], FinishReason = new ChatFinishReason("stop") });
        stream.Complete();

        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(DefaultAgentDefinitionJson),
            ConfiguredStore = store,
            ClientOverride = client,
            DisplayNameOverride = "test",
        });

        var stream2 = client.EnqueueStreamingResponse();
        stream2.Complete();

        chat.EnqueueUserMessage("hi");
        await WaitForHistoryCountAsync(chat.History, 2, "two history items (user + assistant)");

        var messages = await store.ReadMessagesAsync(
            new ReadMessagesRequest { AgentSessionId = chat.AgentSessionId },
            CancellationToken.None);

        var assistantMessages = messages.Where(m => m.Role == ChatRole.Assistant).ToArray();
        Assert.NotEmpty(assistantMessages);
    }

    [Fact]
    public async Task TwoTurns_StoreContainsBothTurns()
    {
        var store = new InMemoryAgentPersistenceStore();
        var client = new DeterministicTestChatClient();

        var stream1 = client.EnqueueStreamingResponse();
        stream1.EnqueueUpdate(new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("turn-one")],
            FinishReason = new ChatFinishReason("stop"),
        });
        stream1.Complete();

        var stream2 = client.EnqueueStreamingResponse();
        stream2.EnqueueUpdate(new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("turn-two")],
            FinishReason = new ChatFinishReason("stop"),
        });
        stream2.Complete();

        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(DefaultAgentDefinitionJson),
            ConfiguredStore = store,
            ClientOverride = client,
            DisplayNameOverride = "test",
        });

        chat.EnqueueUserMessage("first");
        await WaitForHistoryCountAsync(chat.History, 2, "first turn complete");

        chat.EnqueueUserMessage("second");
        await WaitForHistoryCountAsync(chat.History, 4, "second turn complete");

        var messages = await store.ReadMessagesAsync(
            new ReadMessagesRequest { AgentSessionId = chat.AgentSessionId },
            CancellationToken.None);

        var assistantMessages = messages.Where(m => m.Role == ChatRole.Assistant).ToArray();
        Assert.Equal(2, assistantMessages.Length);
        Assert.Equal("turn-one", GetText(assistantMessages[0]));
        Assert.Equal("turn-two", GetText(assistantMessages[1]));
    }

    [Fact]
    public async Task TurnCancelled_StoreContainsMessagesCheckpointedBeforeCancellation()
    {
        // Two role-alternating updates (FC + FR, immediately ready) form two messages.
        // After FR arrives, the middleware computes stableCount=1 and persists FC with
        // CancellationToken.None. A gated third update keeps the stream open so the turn
        // is still in progress when Interrupt() is called. FC must survive in the store.
        var store = new InMemoryAgentPersistenceStore();
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new FunctionCallContent("c0", "tool", null)] });
        stream.EnqueueUpdate(
            new ChatResponseUpdate { Role = ChatRole.Tool, Contents = [new FunctionResultContent("c0", "r0")] });
        stream.EnqueueUpdate(
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("after")], FinishReason = ChatFinishReason.Stop },
            isReady: false);
        stream.Complete(isReady: false);

        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(DefaultAgentDefinitionJson),
            ConfiguredStore = store,
            ClientOverride = client,
            DisplayNameOverride = "test",
        });

        chat.EnqueueUserMessage("hi");

        // Wait until FC and FR are both promoted to History (user + FC + FR = 3 items).
        await WaitForHistoryCountAsync(chat.History, 3, "user + FC + FR promoted");

        chat.Interrupt();

        // Wait for the interrupted turn to finish (running items cleared).
        await WaitForRunningItemsEmptyAsync(chat.RunningItems);

        var messages = await store.ReadMessagesAsync(
            new ReadMessagesRequest { AgentSessionId = chat.AgentSessionId },
            CancellationToken.None);

        // FC was persisted by the middleware with CancellationToken.None before the interrupt,
        // so it must still be present in the store even though the turn was cancelled.
        Assert.Contains(messages, m =>
            m.Role == ChatRole.Assistant
            && m.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == "c0"));
    }

    [Fact]
    public async Task StoreChatHistoryAsync_NotCalledOnStore()
    {
        // IncrementalPersistenceChatHistoryProvider.StoreChatHistoryAsync is a no-op,
        // so the only StoreAsync calls that reach the store come from:
        //   1. ProvideChatHistoryAsync — persists the request (user) message.
        //   2. StreamingPersistenceMiddleware — persists each stable response message.
        // If StoreChatHistoryAsync were not a no-op it would call StoreAsync a third time
        // with all response messages (the old bulk-write path). This test asserts exactly 2 calls.
        var store = new CountingAgentPersistenceStore();
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("hello")],
            FinishReason = ChatFinishReason.Stop,
        });
        stream.Complete();

        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(DefaultAgentDefinitionJson),
            ConfiguredStore = store,
            ClientOverride = client,
            DisplayNameOverride = "test",
        });

        chat.EnqueueUserMessage("hi");
        await WaitForHistoryCountAsync(chat.History, 2, "user + assistant in history");

        // Exactly 2 StoreAsync calls: one for the user message (ProvideChatHistoryAsync),
        // one for the assistant message (StreamingPersistenceMiddleware). The no-op
        // StoreChatHistoryAsync must not have produced a third call.
        Assert.Equal(2, store.StoreCallCount);
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
        System.Collections.Specialized.INotifyCollectionChanged collection,
        int expectedCount,
        string description)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = Task.Delay(TimeSpan.FromSeconds(30));

        void OnChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
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

    private static string GetText(ChatMessage message)
        => string.Concat(message.Contents.OfType<TextContent>().Select(c => c.Text));

    private sealed class CountingAgentPersistenceStore : IAgentPersistenceStore
    {
        private readonly InMemoryAgentPersistenceStore inner = new();

        public int StoreCallCount { get; private set; }

        public ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
        {
            this.StoreCallCount++;
            return this.inner.StoreAsync(request, cancellationToken);
        }

        public ValueTask<PersistedAgent?> RestoreAsync(RestoreRequest request, CancellationToken cancellationToken = default)
            => this.inner.RestoreAsync(request, cancellationToken);

        public ValueTask<ChatMessage[]> ReadMessagesAsync(ReadMessagesRequest request, CancellationToken cancellationToken = default)
            => this.inner.ReadMessagesAsync(request, cancellationToken);

        public ValueTask<SubAgentManifestEntry[]> ReadSubAgentManifestAsync(string parentSessionId, CancellationToken cancellationToken = default)
            => this.inner.ReadSubAgentManifestAsync(parentSessionId, cancellationToken);

        public ValueTask WriteSubAgentManifestEntryAsync(string parentSessionId, SubAgentManifestEntry entry, CancellationToken cancellationToken = default)
            => this.inner.WriteSubAgentManifestEntryAsync(parentSessionId, entry, cancellationToken);
    }
}
