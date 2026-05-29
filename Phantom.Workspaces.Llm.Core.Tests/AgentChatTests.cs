using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using Phantom.Workspaces.Llm.Interfaces;
using System.Linq;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentChatTests
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

    private static AgentChat CreateChat(params ChatResponseUpdate[] updates)
    {
        var agentDefinition = AgentDefinitionLoader.LoadAgentFromJson(DefaultAgentDefinitionJson);
        var persistenceStore = new InMemoryAgentPersistenceStore();
        var chatClient = new DeterministicTestChatClient();
        var stream = chatClient.EnqueueStreamingResponse();
        foreach (var update in updates)
        {
            stream.EnqueueUpdate(update);
        }

        stream.Complete();
        return AgentChat.CreateAsync(new AgentChat.InternalCreateAgentChatRequest
        {
            AgentDefinition = agentDefinition,
            ConfiguredStore = persistenceStore,
            ClientOverride = chatClient,
            DisplayNameOverride = "test-chat",
        }).GetAwaiter().GetResult();
    }

    private static AgentChat CreateChatFromJson(string agentDefinitionJson, IChatClient? client = null)
    {
        var agentDefinition = AgentDefinitionLoader.LoadAgentFromJson(agentDefinitionJson);
        var persistenceStore = new InMemoryAgentPersistenceStore();
        return AgentChat.CreateAsync(new AgentChat.InternalCreateAgentChatRequest
        {
            AgentDefinition = agentDefinition,
            ConfiguredStore = persistenceStore,
            ClientOverride = client ?? new DeterministicTestChatClient(),
            DisplayNameOverride = "test-chat",
        }).GetAwaiter().GetResult();
    }

    private static AgentChat CreateChat(IChatClient client)
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

    private static async Task WaitForConditionAsync(
        AgentChat chat,
        Func<bool> condition,
        string description)
    {
        if (condition())
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStateChanged(object? sender, AgentChatStateChangedEventArgs e)
        {
            if (condition())
            {
                signal.TrySetResult();
            }
        }

        chat.StateChanged += OnStateChanged;
        try
        {
            if (condition())
            {
                return;
            }

            await signal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Timed out waiting for condition: {description}", ex);
        }
        finally
        {
            chat.StateChanged -= OnStateChanged;
        }
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
        var serializerAgent = new ChatClientAgent(new DeterministicTestChatClient(), new ChatClientAgentOptions { UseProvidedChatClientAsIs = true });
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
        await WaitForConditionAsync(chat, () => chat.History.Count >= 2, "history to contain user and assistant placeholder");

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
    public async Task InitializeTools_CreatesToggleableToolModels()
    {
        await using var chat = CreateChatFromJson(
            """
            {
              "kind": "prompt",
              "name": "echo-agent",
              "model": {
                "id": "echo",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": [
                { "kind": "web_search", "name": "search", "description": "Search docs" },
                { "kind": "web_request", "name": "request", "description": "Fetch pages" }
              ]
            }
            """);

        await WaitForConditionAsync(chat, () => chat.Tools.Count == 2, "tool model initialization");
        Assert.Collection(
            chat.Tools.OrderBy(static tool => tool.Kind),
            item =>
            {
                Assert.Equal("web_request", item.Kind);
                Assert.True(item.IsEnabled);
                Assert.Empty(item.Children);
            },
            item =>
            {
                Assert.Equal("web_search", item.Kind);
                Assert.True(item.IsEnabled);
                Assert.Empty(item.Children);
            });

        var requestTool = chat.Tools.Single(static tool => tool.Kind == "web_request");
        await chat.SetToolEnabledAsync(requestTool.Id, enabled: false);
        await WaitForConditionAsync(chat, () => !chat.Tools.Any(static tool => tool.Kind == "web_request" && tool.IsEnabled), "tool disable state");
        Assert.False(chat.Tools.Single(static tool => tool.Kind == "web_request").IsEnabled);
    }

    [Fact]
    public async Task EnqueueUserMessage_AddsPendingAssistantItemImmediately()
    {
        await using var chat = CreateChat();

        chat.EnqueueUserMessage("hello");
        await WaitForConditionAsync(chat, () => chat.History.Count >= 2, "history to contain user and assistant placeholder");

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
        await WaitForConditionAsync(chat, () =>
            chat.History.Count == 2
            && chat.History[1].Role == ChatRole.Assistant
            && !chat.History[1].IsInProgress,
            "assistant placeholder to be replaced by completed streaming response");

        Assert.Equal(2, chat.History.Count);
        var assistantItem = chat.History[1];
        Assert.Equal(ChatRole.Assistant, assistantItem.Role);
        Assert.Equal("hello world", assistantItem.Text);
        Assert.Equal("Answering", assistantItem.ReasoningText);
        Assert.False(assistantItem.IsInProgress);
    }

    [Fact]
    public async Task StreamingInProgress_UsesPlaceholderAndRunningItemBeforeCompletion()
    {
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "2+2 "));
        var blockedSecond = stream.EnqueueUpdate(
            new ChatResponseUpdate(ChatRole.Assistant, "equals 4.")
            {
                FinishReason = ChatFinishReason.Stop,
            },
            isReady: false);
        var blockedComplete = stream.Complete(isReady: false);
        await using var chat = CreateChat(client);

        chat.EnqueueUserMessage("What is 2+2?");
        await WaitForConditionAsync(
            chat,
            () => chat.History.Count >= 2
                && chat.History[^1].Role == ChatRole.Assistant
                && chat.History[^1].IsInProgress
                && chat.RunningItems.Count == 1,
            "in-progress placeholder and running item to appear after first streamed token");

        Assert.True(chat.History[^1].IsInProgress);

        blockedSecond.MarkReady();
        blockedComplete.MarkReady();
        await WaitForConditionAsync(
            chat,
            () => chat.History.Count >= 2
                && chat.History[^1].Role == ChatRole.Assistant
                && !chat.History[^1].IsInProgress
                && chat.History[^1].Text.Contains("2+2 equals 4.", StringComparison.Ordinal),
            "completed assistant response to replace placeholder after stream release");
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
        await WaitForConditionAsync(chat, () => chat.History.Count >= 2, "queued message to publish to history");

        Assert.Equal(2, chat.History.Count);
        Assert.Equal("queued later", chat.History[0].Text);
        Assert.Empty(queue.Items);
    }

    [Fact]
    public async Task EnqueueUserMessage_ToQueuedQueue_WaitsWhileBusy()
    {
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "working "));
        var blockedSecond = stream.EnqueueUpdate(
            new ChatResponseUpdate(ChatRole.Assistant, "done")
            {
                FinishReason = ChatFinishReason.Stop,
            },
            isReady: false);
        var blockedComplete = stream.Complete(isReady: false);
        await using var chat = CreateChat(client);

        var queue = chat.QueueManager.CreateInputQueue();
        chat.EnqueueUserMessage("start");
        await WaitForConditionAsync(chat, () => chat.RunningItems.Count > 0, "first queued run to start");

        chat.EnqueueUserMessage("queued while busy", queue);
        Assert.Single(queue.Items);
        Assert.Equal(2, chat.History.Count);

        blockedSecond.MarkReady();
        blockedComplete.MarkReady();
        await WaitForConditionAsync(chat, () => queue.Items.Count == 0 && chat.History.Count >= 4, "busy run to finish and queued message to flush");

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
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueException(new InvalidOperationException("budget limit"));
        await using var chat = CreateChat(client);

        chat.EnqueueUserMessage("hello");
        await WaitForConditionAsync(
            chat,
            () => chat.History.Any(item =>
                item.Role == ChatRole.Assistant &&
                item.Contents.OfType<ErrorContent>().Any()),
            "error content turn to be appended after provider exception");

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
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chat.StateChanged += (_, change) =>
        {
            changes.Add(change);
            if (change.ChangeKind == AgentChatStateChangeKind.HistoryReplaced
                && change.HistoryItem?.Role == ChatRole.Assistant
                && !change.HistoryItem.IsInProgress)
            {
                completed.TrySetResult();
            }
        };

        chat.EnqueueUserMessage("hi");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains(changes, c => c.ChangeKind == AgentChatStateChangeKind.HistoryAdded && c.HistoryItem?.Role == ChatRole.User);
        Assert.Contains(changes, c => c.ChangeKind == AgentChatStateChangeKind.HistoryAdded && c.HistoryItem?.Role == ChatRole.Assistant && c.HistoryItem.IsInProgress);
        Assert.Contains(changes, c => c.ChangeKind == AgentChatStateChangeKind.HistoryReplaced && c.HistoryItem?.Role == ChatRole.Assistant && !c.HistoryItem.IsInProgress);
    }
}
