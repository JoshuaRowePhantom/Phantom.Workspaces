using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using Phantom.Workspaces.Llm.Interfaces;
using System.Linq;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentChatTests
{
    private static AgentChat CreateChat(params ChatResponseUpdate[] updates)
    {
        var agentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
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
            """);
        var persistenceStore = new InMemoryAgentPersistenceStore();
        var chatClient = new TestChatClient(updates);
        return AgentChat.CreateAsync(new AgentChat.InternalCreateAgentChatRequest
        {
            AgentDefinition = agentDefinition,
            ConfiguredStore = persistenceStore,
            ClientOverride = chatClient,
            DisplayNameOverride = "test-chat",
        }).GetAwaiter().GetResult();
    }

    private static AgentChat CreateBusyChat(IChatClient client)
    {
        var agentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
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
            """);
        var persistenceStore = new InMemoryAgentPersistenceStore();
        return AgentChat.CreateAsync(new AgentChat.InternalCreateAgentChatRequest
        {
            AgentDefinition = agentDefinition,
            ConfiguredStore = persistenceStore,
            ClientOverride = client,
            DisplayNameOverride = "test-chat",
        }).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task CreateAsync_WithRestoredSession_LoadsPersistedMessagesIntoHistory()
    {
        var agentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
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
            """);
        var store = new InMemoryAgentPersistenceStore();
        var sessionId = "restored-history-session";
        var serializerAgent = new ChatClientAgent(new TestChatClient(), new ChatClientAgentOptions { UseProvidedChatClientAsIs = true });
        var serializerSession = await serializerAgent.CreateSessionAsync(CancellationToken.None);
        var serializedSession = await serializerAgent.SerializeSessionAsync(serializerSession, cancellationToken: CancellationToken.None);

        await store.StoreAsync(
            new StoreRequestAgent
            {
                Agent = new PersistedAgent
                {
                    AgentSessionId = sessionId,
                    AgentSessionJson = BsonDocument.Parse(serializedSession.GetRawText()),
                    AgentDefinitionJson = BsonDocument.Parse(agentDefinition.ToJson()),
                },
                NewMessages =
                [
                    new ChatMessage(ChatRole.User, "hello"),
                    new ChatMessage(ChatRole.Assistant, "world"),
                ],
            },
            CancellationToken.None);

        await using var chat = await AgentChat.CreateAsync(
            new AgentChat.InternalCreateAgentChatRequest
            {
                AgentSessionId = sessionId,
                AgentDefinition = null,
                ConfiguredStore = store,
            });

        Assert.Equal(2, chat.History.Count);
        Assert.Equal("hello", chat.History[0].Text);
        Assert.Equal("world", chat.History[1].Text);
    }

    [Fact]
    public async Task EnqueueUserContents_AcceptsTextAndImageContent()
    {
        await using var chat = CreateChat();

        var image = new DataContent(new byte[] { 0x01, 0x02 }, "image/png");
        chat.EnqueueUserContents([new TextContent("hello"), image]);
        await Task.Delay(100);

        Assert.Equal(2, chat.History.Count);
        var userHistory = chat.History[0];
        Assert.Equal(ChatRole.User, userHistory.Role);
        Assert.Equal("hello[image/png]", userHistory.Text);
        Assert.Equal(2, userHistory.Contents.Count);
        Assert.IsType<TextContent>(userHistory.Contents[0]);
        Assert.IsType<DataContent>(userHistory.Contents[1]);

        var assistantPlaceholder = chat.History[1];
        Assert.Equal(ChatRole.Assistant, assistantPlaceholder.Role);
    }

    [Fact]
    public async Task EnqueueUserMessage_AddsPendingAssistantItemImmediately()
    {
        await using var chat = CreateChat();

        chat.EnqueueUserMessage("hello");
        await Task.Delay(100);

        Assert.Equal(2, chat.History.Count);
        Assert.Equal(ChatRole.User, chat.History[0].Role);
        Assert.Equal(ChatRole.Assistant, chat.History[1].Role);
    }

    [Fact]
    public async Task StreamingCompletion_UpdatesAssistantPlaceholderInPlace()
    {
        await using var chat = CreateChat(
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("An")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("swering")]),
            new ChatResponseUpdate(ChatRole.Assistant, "hello "),
            new ChatResponseUpdate(ChatRole.Assistant, "world")
            {
                FinishReason = ChatFinishReason.Stop,
            });

        chat.EnqueueUserMessage("hi");

        await Task.Delay(150);

        Assert.Equal(2, chat.History.Count);
        var assistantItem = chat.History[1];
        Assert.Equal(ChatRole.Assistant, assistantItem.Role);
        Assert.Equal("hello world", assistantItem.Text);
        Assert.Equal("Answering", assistantItem.ReasoningText);
        Assert.False(assistantItem.IsInProgress);
    }

    [Fact]
    public async Task CreateInputQueue_AddsQueueToInputQueues()
    {
        await using var chat = CreateChat();

        var created = chat.QueueManager.CreateInputQueue();

        Assert.Contains(created, chat.InputQueues);
        Assert.False(created.IsDefault);
        Assert.Equal(2, chat.InputQueues.Count);
    }

    [Fact]
    public async Task Constructor_DefaultQueueStartsQueued_AndImmediateQueueStartsImmediate()
    {
        await using var chat = CreateChat();

        Assert.Equal(AgentInputQueueImmediacy.Queue, chat.DefaultInputQueue.Immediacy);
        Assert.True(chat.ImmediateInputQueue.IsImmediate);
        Assert.Equal(AgentInputQueueImmediacy.Immediate, chat.ImmediateInputQueue.Immediacy);
    }

    [Fact]
    public async Task RemoveInputQueue_DefaultQueueCannotBeRemoved()
    {
        await using var chat = CreateChat();

        var removed = chat.QueueManager.RemoveInputQueue(chat.DefaultInputQueue);

        Assert.False(removed);
        Assert.Single(chat.InputQueues);
    }

    [Fact]
    public async Task EnqueueUserMessage_ToQueuedQueue_PublishesImmediatelyWhenIdle()
    {
        await using var chat = CreateChat();
        var queue = chat.QueueManager.CreateInputQueue();

        chat.EnqueueUserMessage("queued later", queue);
        await Task.Delay(100);

        Assert.Equal(2, chat.History.Count);
        Assert.Equal("queued later", chat.History[0].Text);
        Assert.Empty(queue.Items);
    }

    [Fact]
    public async Task EnqueueUserMessage_ToQueuedQueue_WaitsWhileBusy()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var chat = CreateBusyChat(new BusyTestChatClient(
            started,
            release,
            new ChatResponseUpdate(ChatRole.Assistant, "working "),
            new ChatResponseUpdate(ChatRole.Assistant, "done")
            {
                FinishReason = ChatFinishReason.Stop,
            }));

        var queue = chat.QueueManager.CreateInputQueue();
        chat.EnqueueUserMessage("start");

        await started.Task;

        chat.EnqueueUserMessage("queued while busy", queue);

        Assert.Single(queue.Items);
        Assert.Equal(2, chat.History.Count);

        release.SetResult();
        await Task.Delay(100);

        Assert.Empty(queue.Items);
        Assert.Equal(4, chat.History.Count);
        Assert.Equal("queued while busy", chat.History[2].Text);
    }

    [Fact]
    public async Task UpdateQueueItem_PreservesImageAttachments()
    {
        await using var chat = CreateChat();
        var queue = chat.QueueManager.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO3ZfV0AAAAASUVORK5CYII=");
        chat.EnqueueUserContents([new TextContent("hello"), new DataContent(png, "image/png")], chat.InputQueues[1]);

        var updated = chat.QueueManager.UpdateQueueItem(queue, 0, "edited");

        Assert.True(updated);
        Assert.Equal(2, queue.Items[0].Contents.Count);
        Assert.Equal("edited", queue.Items[0].Contents.OfType<TextContent>().Single().Text);
        Assert.IsType<DataContent>(queue.Items[0].Contents[1]);
    }

    [Fact]
    public void AgentInputQueue_RaisesChangedWhenItemsMutate()
    {
        var queue = new AgentInputQueue();
        var changedCount = 0;
        queue.Changed += (_, _) => changedCount++;

        queue.Enqueue([
            new AgentInputItem
            {
                Messages = [new ChatMessage(ChatRole.User, "hello")],
            },
        ]);
        var items = queue.Items;
        queue.TryRemoveAt(ref items, 0);

        Assert.Equal(2, changedCount);
    }

    [Fact]
    public async Task ProviderException_AppendsAssistantErrorContentTurn()
    {
        await using var chat = CreateBusyChat(new ThrowingTestChatClient("budget limit"));

        chat.EnqueueUserMessage("hello");
        await Task.Delay(150);

        var assistantErrorTurn = Assert.Single(
            chat.History.Where(item =>
                item.Role == ChatRole.Assistant &&
                item.Contents.OfType<ErrorContent>().Any()));
        var error = Assert.Single(assistantErrorTurn.Contents.OfType<ErrorContent>());
        Assert.Contains("budget limit", error.Message);
    }

    [Fact]
    public async Task StateChanged_UsesMonotonicVersions_AndSnapshotMatchesLatestVersion()
    {
        await using var chat = CreateChat();
        var changes = new List<AgentChatStateChangedEventArgs>();
        chat.StateChanged += (_, change) => changes.Add(change);

        var runningItem = chat.CreateRunningItem(
            new AgentChatHistoryItem
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent("working")],
                IsInProgress = true,
            });
        chat.UpdateRunningItem(
            runningItem,
            [
                new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent("done")],
                    IsInProgress = false,
                },
            ]);
        chat.CompleteRunningItem(runningItem, writeToHistory: false);
        chat.SetAgentSessionId("next-session-id");

        Assert.Equal(4, changes.Count);
        Assert.Equal(AgentChatStateChangeKind.RunningAdded, changes[0].ChangeKind);
        Assert.Equal(AgentChatStateChangeKind.RunningUpdated, changes[1].ChangeKind);
        Assert.Equal(AgentChatStateChangeKind.RunningRemoved, changes[2].ChangeKind);
        Assert.Equal(AgentChatStateChangeKind.SessionChanged, changes[3].ChangeKind);

        for (var i = 1; i < changes.Count; i++)
        {
            Assert.Equal(changes[i - 1].ToVersion, changes[i].FromVersion);
        }

        var snapshot = chat.GetStateSnapshot();
        Assert.Equal(changes[^1].ToVersion, snapshot.Version);
        Assert.Equal(chat.AgentSessionId, snapshot.AgentSessionId);
    }

    [Fact]
    public async Task EnqueueUserMessage_RaisesHistoryAddAndReplaceStateChanges()
    {
        await using var chat = CreateChat(
            new ChatResponseUpdate(ChatRole.Assistant, "hello")
            {
                FinishReason = ChatFinishReason.Stop,
            });
        var changes = new List<AgentChatStateChangedEventArgs>();
        chat.StateChanged += (_, change) => changes.Add(change);

        chat.EnqueueUserMessage("hi");
        await Task.Delay(150);

        Assert.Contains(changes, c => c.ChangeKind == AgentChatStateChangeKind.HistoryAdded && c.HistoryItem?.Role == ChatRole.User);
        Assert.Contains(changes, c => c.ChangeKind == AgentChatStateChangeKind.HistoryAdded && c.HistoryItem?.Role == ChatRole.Assistant && c.HistoryItem.IsInProgress);
        Assert.Contains(changes, c => c.ChangeKind == AgentChatStateChangeKind.HistoryReplaced && c.HistoryItem?.Role == ChatRole.Assistant && !c.HistoryItem.IsInProgress);
    }

    private sealed class BusyTestChatClient : IChatClient
    {
        private readonly TaskCompletionSource started;
        private readonly TaskCompletionSource release;
        private readonly IReadOnlyCollection<ChatResponseUpdate> updates;

        public BusyTestChatClient(
            TaskCompletionSource started,
            TaskCompletionSource release,
            params ChatResponseUpdate[] updates)
        {
            this.started = started;
            this.release = release;
            this.updates = updates;
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var content = string.Empty;
            await foreach (var update in this.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                content += update.Text;
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, content));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in this.updates.Take(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
                this.started.TrySetResult();
                await Task.Yield();
            }

            await this.release.Task.WaitAsync(cancellationToken);

            foreach (var update in this.updates.Skip(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
                await Task.Yield();
            }

        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingTestChatClient(string message) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(message);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            throw new InvalidOperationException(message);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose()
        {
        }
    }
}
