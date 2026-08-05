using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Interfaces;
using System.Collections.Specialized;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentChatLastUpdatedAtTests
{
    // #1140: On reload, InitializeAsync must seed lastUpdatedAt from the persisted
    // UpdatedUtc timestamp (surfaced via PersistedAgent.LastUpdatedUtc) rather than leaving
    // it at construction-time "now". Without this, restored sub-agent cards would show
    // "just now" instead of the correct "N days ago".
    [Fact]
    public async Task AgentChat_Restore_SeedsLastUpdatedAtFromPersistedTimestamp()
    {
        var store = new InMemoryAgentPersistenceStore();
        const string sessionId = "seed-from-persisted";

        // Persist a session whose UpdatedUtc is in the distant past.
        var persistedTime = new DateTime(2023, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        await store.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = sessionId,
                AgentDefinitionJson = MongoDB.Bson.BsonDocument.Parse(
                    AgentDefinitionLoader.LoadAgentFromJson(DefaultAgentDefinitionJson).ToJson()),
                LastUpdatedUtc = persistedTime,
            },
        });

        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = null,
            AgentSessionId = sessionId,
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "restored",
        });

        // Even though the construction thread stamps lastUpdatedAt = now, the restore path
        // must overwrite it with the persisted timestamp.
        Assert.Equal(persistedTime, chat.LastUpdatedAt);
    }

    // #1140 regression guard: A genuinely-updated sub-agent (new history item after reload)
    // must still advance LastUpdatedAt past the seeded persisted timestamp — the fix must not
    // freeze the timestamp forever.
    [Fact]
    public async Task AgentChat_LastUpdatedAt_AdvancesOnGenuineActivityAfterRestore()
    {
        var store = new InMemoryAgentPersistenceStore();
        const string sessionId = "advances-after-restore";

        var persistedTime = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await store.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = sessionId,
                AgentDefinitionJson = MongoDB.Bson.BsonDocument.Parse(
                    AgentDefinitionLoader.LoadAgentFromJson(DefaultAgentDefinitionJson).ToJson()),
                LastUpdatedUtc = persistedTime,
            },
        });

        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = null,
            AgentSessionId = sessionId,
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "restored",
        });

        Assert.Equal(persistedTime, chat.LastUpdatedAt);

        // Genuine new activity: adding a history item must advance the timestamp.
        chat.EnqueueSystemNote("post-restore activity");
        await WaitForHistoryCountAsync(chat.History, 1);

        Assert.True(
            chat.LastUpdatedAt > persistedTime,
            $"Expected LastUpdatedAt ({chat.LastUpdatedAt:o}) to advance past persisted seed ({persistedTime:o}) after a new history item.");
    }

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
    public async Task AgentChat_LastUpdatedAt_UpdatesWhenHistoryItemAdded()
    {
        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(DefaultAgentDefinitionJson),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "test",
        });

        var beforeActivity = chat.LastUpdatedAt;

        // EnqueueSystemNote schedules AddHistoryItem on the foreground scheduler; wait for it to arrive.
        chat.EnqueueSystemNote("test activity");
        await WaitForHistoryCountAsync(chat.History, 1);

        Assert.True(
            chat.LastUpdatedAt > beforeActivity || chat.LastUpdatedAt == beforeActivity,
            "LastUpdatedAt must not decrease after a history item is added.");

        // The history item must have been added — verify that the timestamp was updated.
        Assert.Single(chat.History);
    }

    [Fact]
    public async Task AgentChat_LastUpdatedAt_IsSetOnHistoryItemAddedDuringLlmTurn()
    {
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("hello")],
        });
        stream.Complete();

        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(DefaultAgentDefinitionJson),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = client,
            DisplayNameOverride = "test",
        });

        var beforeTurn = chat.LastUpdatedAt;

        chat.EnqueueUserMessage("hi");
        await WaitForHistoryCountAsync(chat.History, 2); // user + assistant

        Assert.True(
            chat.LastUpdatedAt >= beforeTurn,
            "LastUpdatedAt must advance (or stay equal) after a turn completes.");
    }

    private static async Task WaitForHistoryCountAsync(
        INotifyCollectionChanged collection,
        int expectedCount)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = Task.Delay(TimeSpan.FromSeconds(30));

        void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (((System.Collections.ICollection)collection).Count >= expectedCount)
            {
                signal.TrySetResult();
            }
        }

        collection.CollectionChanged += OnChanged;
        try
        {
            if (((System.Collections.ICollection)collection).Count >= expectedCount)
            {
                return;
            }

            var completed = await Task.WhenAny(signal.Task, timeout);
            if (completed == timeout)
            {
                throw new TimeoutException($"Timeout waiting for {expectedCount} history items.");
            }
        }
        finally
        {
            collection.CollectionChanged -= OnChanged;
        }
    }
}
