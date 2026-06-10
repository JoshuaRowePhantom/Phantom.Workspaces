using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using Phantom.Workspaces.Llm.Interfaces;
using System.Linq;
using System.Reflection;

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
        return AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = agentDefinition,
            ConfiguredStore = persistenceStore,
            ClientOverride = chatClient,
            DisplayNameOverride = "test-chat",
        }).GetAwaiter().GetResult();
    }

    private static AgentChat CreateChatFromJson(
        string agentDefinitionJson,
        IChatClient? client = null,
        AgentServices? agentServices = null)
    {
        var agentDefinition = AgentDefinitionLoader.LoadAgentFromJson(agentDefinitionJson);
        var persistenceStore = new InMemoryAgentPersistenceStore();
        return AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = agentDefinition,
            ConfiguredStore = persistenceStore,
            ClientOverride = client ?? new DeterministicTestChatClient(),
            DisplayNameOverride = "test-chat",
            AgentServices = agentServices,
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
        return AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = agentDefinition,
            ConfiguredStore = persistenceStore,
            ClientOverride = client,
            DisplayNameOverride = "test-chat",
        }).GetAwaiter().GetResult();
    }

    private static async Task WaitForConditionAsync(
        System.Collections.Specialized.INotifyCollectionChanged collection,
        Func<bool> condition,
        string description)
    {
        if (condition())
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (condition())
            {
                signal.TrySetResult();
            }
        }

        collection.CollectionChanged += OnCollectionChanged;
        try
        {
            if (condition())
            {
                return;
            }

            await signal.Task;
        }
        finally
        {
            collection.CollectionChanged -= OnCollectionChanged;
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
            new InternalCreateAgentChatRequest
            {
                AgentSessionId = sessionId,
                AgentDefinition = null,
                ConfiguredStore = store,
            });

        Assert.Equal(2, chat.History.Count);
        Assert.Equal("hello", GetText(chat.History[0].Contents));
        Assert.Equal("world", GetText(chat.History[1].Contents));
    }

    [Fact]
    public async Task EnqueueUserContents_AcceptsTextAndImageContent()
    {
        await using var chat = CreateChat();

        var image = new DataContent(new byte[] { 0x01, 0x02 }, "image/png");
        chat.EnqueueUserContents([new TextContent("hello"), image]);
        await WaitForConditionAsync(chat.History, () => chat.History.Count >= 2, "history to contain user and assistant placeholder");

        Assert.Equal(2, chat.History.Count);
        var userHistory = chat.History[0];
        Assert.Equal(ChatRole.User, userHistory.Role);
        Assert.Equal("hello[image/png]", GetDisplayText(userHistory.Contents));
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
                { "kind": "web_search", "description": "Search docs" },
                { "kind": "web_request", "description": "Fetch pages" }
              ]
            }
            """);

        Assert.Collection(
            chat.Tools.OrderBy(static tool => tool.Kind),
            item =>
            {
                Assert.Equal("web_request", item.Kind);
                Assert.Equal("web_request", item.Name);
                Assert.True(item.IsEnabled);
                Assert.Empty(item.Children);
            },
            item =>
            {
                Assert.Equal("web_search", item.Kind);
                Assert.Equal("web_search", item.Name);
                Assert.True(item.IsEnabled);
                Assert.Empty(item.Children);
            });
        var requestTool = chat.Tools.Single(static tool => tool.Kind == "web_request");
        await chat.SetToolEnabledAsync(requestTool.Id, enabled: false);
        await chat.SetToolEnabledAsync(requestTool.Id, enabled: false);
        Assert.False(chat.Tools.Single(static tool => tool.Kind == "web_request").IsEnabled);
    }

    [Fact]
    public async Task InitializeTools_IncludesConfiguredToolsInFirstLlmRequest()
    {
        var client = new DeterministicTestChatClient();
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
                { "kind": "web_search", "description": "Search docs" },
                { "kind": "web_request", "description": "Fetch pages" }
              ]
            }
            """,
            client);

        Assert.Equal(2, chat.Tools.Count);

        using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        chat.EnqueueUserMessage("hello");
        await client.WaitForRequestAsync(requestTimeout.Token);

        var toolNames = client.LastRequestOptions?.Tools?
            .Select(static tool => tool.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray()
            ?? [];

        Assert.Equal(["web_request", "web_search"], toolNames);
    }

    [Fact]
    public async Task EnqueueUserMessage_DoesNotDuplicateCurrentUserMessageInRequestHistory()
    {
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "ok")
        {
            FinishReason = ChatFinishReason.Stop,
        });
        stream.Complete();
        await using var chat = CreateChat(client);

        chat.EnqueueUserMessage("world");
        using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await client.WaitForRequestAsync(requestTimeout.Token);

        var matchingUserMessages = client.LastRequestMessages
            .Where(static message => message.Role == ChatRole.User)
            .SelectMany(static message => message.Contents.OfType<TextContent>())
            .Count(static content => string.Equals(content.Text, "world", StringComparison.Ordinal));

        Assert.Equal(1, matchingUserMessages);
    }

    [Fact]
    public async Task InitializeTools_FilesystemServiceToolset_UsesFilesystemRootAndChildTools()
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
                { "kind": "filesystem", "description": "Workspace files" }
              ]
            }
            """);

        var root = Assert.Single(chat.Tools);
        Assert.Equal("filesystem", root.Kind);
        Assert.Equal("filesystem", root.Name);
        Assert.True(root.IsEnabled);
        Assert.Contains(root.Children, static child => child.Name == "read");
        Assert.Contains(root.Children, static child => child.Name == "search");
        Assert.Contains(root.Children, static child => child.Name == "edit");
    }

    [Fact]
    public async Task InitializeTools_FilesystemServiceToolset_IncludesFilesystemToolsInFirstLlmRequest()
    {
        var client = new DeterministicTestChatClient();
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
                { "kind": "filesystem", "description": "Workspace files" }
              ]
            }
            """,
            client);

        chat.EnqueueUserMessage("hello");
        using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await client.WaitForRequestAsync(requestTimeout.Token);

        var toolNames = client.LastRequestOptions?.Tools?
            .Select(static tool => tool.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray()
            ?? [];

        Assert.Equal(
            ["describe_edit", "edit", "edit_apply", "make_directory", "move_item", "read", "remove_item", "search"],
            toolNames);
    }

    [Fact]
    public async Task InitializeTools_UsesAgentServicesToolsetFactory()
    {
        var toolsetFactory = ToolsetFactory.CreateNamedToolsetFactory(
            name: "custom_kind",
            createToolsetAsync: static (_, _, _) =>
            {
                IToolset toolset = new SingleToolset(new WebSearchTool());
                return Task.FromResult(toolset);
            },
            underlyingInstance: NoOpToolsetFactory.Instance);
        var services = new AgentServices
        {
            ToolsetFactory = toolsetFactory,
        };

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
                { "kind": "custom_kind", "description": "Custom tools" }
              ]
            }
            """,
            agentServices: services);

        var root = Assert.Single(chat.Tools);
        Assert.Equal("custom_kind", root.Kind);
        Assert.Single(root.Children);
        Assert.Equal("web_search", root.Children[0].Name);
    }

    [Fact]
    public async Task EnqueueUserMessage_AddsPendingAssistantItemImmediately()
    {
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "partial"));
        await using var chat = CreateChat(client);

        chat.EnqueueUserMessage("hello");
        await WaitForConditionAsync(chat.RunningItems, () => chat.History.Count == 1 && chat.RunningItems.Count == 1, "history to contain user and running assistant items");

        Assert.Equal(1, chat.History.Count);
        Assert.Equal(ChatRole.User, chat.History[0].Role);
        Assert.Equal(1, chat.RunningItems.Count);
        var runningAssistant = chat.RunningItems[0];
        Assert.Equal(ChatRole.Assistant, runningAssistant.Items[0].Role);
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
        await WaitForConditionAsync(chat.RunningItems, () =>
            chat.History.Count == 2
            && chat.RunningItems.Count == 0,
            "assistant running item to complete and move into history");

        Assert.Equal(2, chat.History.Count);
        var assistantItem = chat.History[1];
        Assert.Equal(ChatRole.Assistant, assistantItem.Role);
        Assert.Equal("hello world", GetText(assistantItem.Contents));
        Assert.Equal("Answering", GetReasoningText(assistantItem.Contents));
        Assert.Empty(chat.RunningItems);
    }

    [Fact]
    public async Task StreamingInProgress_UsesRunningItemBeforeCompletion()
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
            chat.RunningItems,
            () => chat.History.Count == 1
                && chat.RunningItems.Count == 1,
            "running item to appear after first streamed token");

        Assert.Equal(1, chat.History.Count);
        Assert.Equal(ChatRole.User, chat.History[0].Role);
        Assert.Equal(1, chat.RunningItems.Count);
        blockedSecond.MarkReady();
        blockedComplete.MarkReady();
        await WaitForConditionAsync(
            chat.RunningItems,
            () => chat.RunningItems.Count == 0,
            "running item to complete after stream release");
        await WaitForConditionAsync(
            chat.History,
            () => chat.History.Count == 2
                && chat.History[^1].Role == ChatRole.Assistant
                && GetText(chat.History[^1].Contents).Contains("2+2 equals 4.", StringComparison.Ordinal),
            "completed assistant response after stream release");
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
        await WaitForConditionAsync(chat.History, () => chat.History.Count >= 2, "queued message to publish to history");

        Assert.Equal(2, chat.History.Count);
        Assert.Equal("queued later", GetText(chat.History[0].Contents));
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
        await WaitForConditionAsync(chat.RunningItems, () => chat.RunningItems.Count > 0, "first queued run to start");

        chat.EnqueueUserMessage("queued while busy", queue);
        Assert.Single(queue.Items);
        Assert.Equal(1, chat.History.Count);

        blockedSecond.MarkReady();
        blockedComplete.MarkReady();
        await WaitForConditionAsync(chat.RunningItems, () => chat.RunningItems.Count == 0, "busy run to finish");
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
            chat.History,
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
    public async Task CreateUpdateCompleteRunningItem_ChangesCollectionsAndSessionId()
    {
        await using var chat = CreateChat();

        var runningItem = chat.CreateRunningItem(
            new AgentChatHistoryItem
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent("working")],
            });
        chat.UpdateRunningItem(
            runningItem,
            [
                new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent("done")],
                },
            ]);
        chat.CompleteRunningItem(runningItem, writeToHistory: false);
        chat.SetAgentSessionId("next-session-id");

        Assert.Empty(chat.RunningItems);
        Assert.Equal("next-session-id", chat.AgentSessionId);
    }

    [Fact]
    public async Task EnqueueUserMessage_UpdatesHistoryAndRunningCollections()
    {
        await using var chat = CreateChat(
            new ChatResponseUpdate(ChatRole.Assistant, "hello")
            {
                FinishReason = ChatFinishReason.Stop,
            });

        chat.EnqueueUserMessage("hi");
        await WaitForConditionAsync(
            chat.RunningItems,
            () => chat.History.Count == 2 && chat.RunningItems.Count == 0,
            "user message to complete and move the assistant response into history");

        Assert.Equal(2, chat.History.Count);
        Assert.Equal(ChatRole.User, chat.History[0].Role);
        Assert.Equal(ChatRole.Assistant, chat.History[1].Role);
        Assert.Equal("hello", GetText(chat.History[1].Contents));
        Assert.Empty(chat.RunningItems);
    }

    [Fact]
    public async Task EnqueueUserMessage_StartsProcessingLoopWhenItHasNotBegun()
    {
        await using var chat = await CreateUnstartedChatAsync();

        chat.EnqueueUserMessage("hi");

        await WaitForConditionAsync(
            chat.History,
            () => chat.History.Any(item => item.Role == ChatRole.Assistant && GetText(item.Contents).Contains("hello", StringComparison.Ordinal)),
            "queued user message to start processing and produce assistant output");
    }

    private static string GetText(IReadOnlyList<AIContent> contents)
        => string.Concat(contents.OfType<TextContent>().Select(static content => content.Text));

    private static string GetReasoningText(IReadOnlyList<AIContent> contents)
        => string.Concat(contents.OfType<TextReasoningContent>().Select(static content => content.Text));

    private static string GetDisplayText(IReadOnlyList<AIContent> contents)
        => string.Concat(contents.Select(static content => content switch
        {
            TextContent textContent => textContent.Text,
            TextReasoningContent => string.Empty,
            ToolCallContent => string.Empty,
            ToolResultContent => string.Empty,
            DataContent dataContent when !string.IsNullOrWhiteSpace(dataContent.MediaType) => $"[{dataContent.MediaType}]",
            DataContent => "[data]",
            UriContent uriContent when !string.IsNullOrWhiteSpace(uriContent.MediaType) => $"[{uriContent.MediaType}] {uriContent.Uri}",
            UriContent uriContent => uriContent.Uri.ToString(),
            _ => $"[{content.GetType().Name}]",
        }));

    private static async Task<AgentChat> CreateUnstartedChatAsync()
    {
        var testClient = new DeterministicTestChatClient();
        var stream = testClient.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "hello")
        {
            FinishReason = ChatFinishReason.Stop,
        });
        stream.Complete();

        var chatClientAgent = new ChatClientAgent(
            testClient,
            new ChatClientAgentOptions
            {
                UseProvidedChatClientAsIs = true,
            });

        var session = await chatClientAgent.CreateSessionAsync(CancellationToken.None);
        var agentChatSession = new AgentChatSession(chatClientAgent, session);

        var agentDefinition = AgentDefinitionLoader.LoadAgentFromJson(DefaultAgentDefinitionJson);
        var persistenceStore = new InMemoryAgentPersistenceStore();
        var request = new InternalCreateAgentChatRequest
        {
            AgentDefinition = agentDefinition,
            ConfiguredStore = persistenceStore,
            DisplayNameOverride = "test-chat",
        };

        var constructor = typeof(AgentChat).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(InternalCreateAgentChatRequest) },
            modifiers: null);
        if (constructor is null)
        {
            throw new InvalidOperationException("AgentChat constructor not found.");
        }

        var chat = (AgentChat)constructor.Invoke(new object[] { request });
        var sessionField = typeof(AgentChat).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("session field not found.");
        sessionField.SetValue(chat, agentChatSession);
        return chat;
    }

    private sealed class SingleToolset : IToolset
    {
        private readonly IList<AITool> tools;

        public SingleToolset(AITool tool)
        {
            this.tools = [tool];
        }

        public Task<IList<AITool>> ListToolsAsync()
        {
            return Task.FromResult(this.tools);
        }
    }

    private sealed class NoOpToolsetFactory : IToolsetFactory
    {
        public static readonly NoOpToolsetFactory Instance = new();

        public Task<IToolset> CreateToolsetAsync(
            string name,
            Dictionary<string, object> properties,
            AgentServices agentServices)
        {
            _ = name;
            _ = properties;
            _ = agentServices;
            throw new InvalidOperationException("No-op test factory should not be invoked.");
        }
    }
}
