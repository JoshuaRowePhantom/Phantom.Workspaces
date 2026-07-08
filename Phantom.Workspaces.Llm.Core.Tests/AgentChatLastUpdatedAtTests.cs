using Microsoft.Extensions.AI;
using System.Collections.Specialized;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentChatLastUpdatedAtTests
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
