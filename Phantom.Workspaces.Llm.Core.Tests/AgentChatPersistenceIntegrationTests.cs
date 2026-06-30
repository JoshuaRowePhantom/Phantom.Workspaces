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
}
