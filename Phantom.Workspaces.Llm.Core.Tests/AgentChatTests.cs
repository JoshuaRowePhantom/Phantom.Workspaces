using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;
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
        // The agent mutates its observable collections on its foreground scheduler and raises
        // CollectionChanged on that thread, so evaluating the predicate from within the handler is
        // race-free. The initial sample runs on the test thread and may observe a collection while
        // the agent is mutating it on another thread; ConditionMet swallows the resulting transient
        // enumeration error and relies on the next (race-free) CollectionChanged to complete the
        // wait. Subscribing before the post-subscription sample guarantees no notification is missed,
        // so a condition that becomes true only via a mutation is always observed.
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (ConditionMet(condition))
            {
                signal.TrySetResult();
            }
        }

        collection.CollectionChanged += OnCollectionChanged;
        try
        {
            if (ConditionMet(condition))
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

    private static bool ConditionMet(Func<bool> condition)
    {
        try
        {
            return condition();
        }
        catch (InvalidOperationException)
        {
            // Transient "Collection was modified; enumeration operation may not execute." raised when
            // the predicate samples a collection the agent is concurrently mutating on its foreground
            // thread. Treat as not-yet-satisfied; the next CollectionChanged re-evaluates safely.
            return false;
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
    public async Task InitializeTools_DisabledToolIsExcludedFromFirstLlmRequest()
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

        var requestTool = chat.Tools.Single(static tool => tool.Kind == "web_request");
        await chat.SetToolEnabledAsync(requestTool.Id, enabled: false);

        using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        chat.EnqueueUserMessage("hello");
        await client.WaitForRequestAsync(requestTimeout.Token);

        var toolNames = client.LastRequestOptions?.Tools?
            .Select(static tool => tool.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray()
            ?? [];

        Assert.Equal(["web_search"], toolNames);
    }

    [Fact]
    public async Task InitializeTools_UnmappedToolCreatesErrorNode()
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
                { "kind": "not-a-real-tool", "description": "Broken tool" }
              ]
            }
            """);

        var tool = Assert.Single(chat.Tools);
        Assert.Equal("not-a-real-tool", tool.Kind);
        Assert.False(tool.IsEnabled);
        Assert.Contains("No tool provider is mapped", tool.Status ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(tool.Children);
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
        var underlyingToolsetFactory = new Mock<IToolsetFactory>();
        underlyingToolsetFactory
            .Setup(factory => factory.CreateToolsetAsync(It.IsAny<AgentSchema.Tool>(), It.IsAny<AgentServices>()))
            .ReturnsAsync((Microsoft.Agents.AI.AIContextProvider?)null);
        var toolsetFactory = ToolsetFactory.CreateNamedToolsetFactory(
            kind: "custom_kind",
            createToolsetAsync: static (_, _) =>
            {
                return Task.FromResult<Microsoft.Agents.AI.AIContextProvider?>(ToolsetFactory.CreateFixedToolset(new WebSearchTool()));
            },
            underlyingInstance: underlyingToolsetFactory.Object);
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
    public async Task StreamingUsageContent_AccumulatesTokenTotalsAndRaisesUsageChanged()
    {
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, [
            new TextContent("usage "),
            new UsageContent(new UsageDetails
            {
                InputTokenCount = 1000,
                OutputTokenCount = 25,
            }),
        ]));
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, [
            new TextContent("tracked"),
            new UsageContent(new UsageDetails
            {
                InputTokenCount = 234,
                OutputTokenCount = 31,
            }),
        ])
        {
            FinishReason = ChatFinishReason.Stop,
        });
        stream.Complete();
        await using var chat = CreateChat(client);
        var usageChangedCount = 0;
        chat.UsageChanged += (_, _) => usageChangedCount++;

        chat.EnqueueUserMessage("hi");
        await WaitForConditionAsync(
            chat.History,
            () => chat.History.Count == 2,
            "streaming usage response to complete");

        Assert.Equal(1234, chat.TotalInputTokenCount);
        Assert.Equal(56, chat.TotalOutputTokenCount);
        Assert.Equal(2, usageChangedCount);
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
    public async Task StreamingInProgress_SurfacesToolCallAndResultInRunningItemBeforeCompletion()
    {
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "Let me check. "));
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, [
            new FunctionCallContent("call-1", "search", new Dictionary<string, object?> { ["q"] = "widgets" }),
        ]));
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Tool, [
            new FunctionResultContent("call-1", "found 3 widgets"),
        ]));
        var blockedFinal = stream.EnqueueUpdate(
            new ChatResponseUpdate(ChatRole.Assistant, "Done.")
            {
                FinishReason = ChatFinishReason.Stop,
            },
            isReady: false);
        var blockedComplete = stream.Complete(isReady: false);

        await using var chat = CreateChat(client);
        chat.EnqueueUserMessage("search please");

        // While the run is still in progress (before the final update is released), the tool call and
        // result should already have streamed into the running item.
        await WaitForConditionAsync(
            chat.RunningItems,
            () => RunningItemContents(chat).OfType<FunctionCallContent>().Any(content => content.CallId == "call-1")
                && RunningItemContents(chat).OfType<FunctionResultContent>().Any(content => content.CallId == "call-1"),
            "tool call and result to stream into the running item before completion");

        Assert.Equal(1, chat.RunningItems.Count);

        blockedFinal.MarkReady();
        blockedComplete.MarkReady();
        await WaitForConditionAsync(chat.RunningItems, () => chat.RunningItems.Count == 0, "run to complete");
    }

    [Fact]
    public async Task StreamingRun_SerializesRunningItemMutationsOnForegroundScheduler()
    {
        // The agent must mutate its running-item collections on a single serialized foreground
        // scheduler: the processing loop's lifecycle operations (create/update/complete) and the
        // partial-response conflator's population must never run concurrently, otherwise a consumer
        // enumerating RunningItems -> Items -> Contents (as the GUI data templates and the test
        // helpers do) could observe "Collection was modified; enumeration operation may not execute."
        // This asserts that CollectionChanged notifications are never raised concurrently and that a
        // foreground-thread reader (the notification handler) never observes a torn collection.
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "Let me check. "));
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "Looking it up. "));
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, [
            new FunctionCallContent("call-1", "search", new Dictionary<string, object?> { ["q"] = "widgets" }),
        ]));
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Tool, [
            new FunctionResultContent("call-1", "found 3 widgets"),
        ]));
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "There are 3 "));
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "widgets. "));
        stream.EnqueueUpdate(
            new ChatResponseUpdate(ChatRole.Assistant, "Done.")
            {
                FinishReason = ChatFinishReason.Stop,
            });
        stream.Complete();

        await using var chat = CreateChat(client);

        var activeNotifications = 0;
        var maximumConcurrentNotifications = 0;
        Exception? enumerationFailure = null;

        void OnRunningItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            var current = System.Threading.Interlocked.Increment(ref activeNotifications);
            UpdateMaximum(ref maximumConcurrentNotifications, current);
            try
            {
                // Deep-enumerate exactly as the GUI data templates and existing assertions do; on the
                // buggy code a concurrent mutation on another thread surfaces here.
                foreach (var runningItem in chat.RunningItems)
                {
                    foreach (var item in runningItem.Items)
                    {
                        foreach (var content in item.Contents)
                        {
                            _ = content;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                System.Threading.Volatile.Write(ref enumerationFailure, exception);
            }
            finally
            {
                System.Threading.Interlocked.Decrement(ref activeNotifications);
            }
        }

        var runningItemsNotifications = (System.Collections.Specialized.INotifyCollectionChanged)chat.RunningItems;
        runningItemsNotifications.CollectionChanged += OnRunningItemsChanged;
        try
        {
            chat.EnqueueUserMessage("search please");

            await WaitForConditionAsync(
                chat.RunningItems,
                () => chat.RunningItems.Count == 0
                    && chat.History.Any(item => item.Role == ChatRole.Assistant
                        && GetText(item.Contents).Contains("Done.", StringComparison.Ordinal)),
                "streaming run to complete");
        }
        finally
        {
            runningItemsNotifications.CollectionChanged -= OnRunningItemsChanged;
        }

        Assert.Null(System.Threading.Volatile.Read(ref enumerationFailure));
        Assert.Equal(1, System.Threading.Volatile.Read(ref maximumConcurrentNotifications));
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        int observed;
        do
        {
            observed = System.Threading.Volatile.Read(ref target);
            if (candidate <= observed)
            {
                return;
            }
        }
        while (System.Threading.Interlocked.CompareExchange(ref target, candidate, observed) != observed);
    }

    private static IEnumerable<AIContent> RunningItemContents(AgentChat chat)
        => chat.RunningItems
            .SelectMany(runningItem => runningItem.Items)
            .SelectMany(item => item.Contents);


    [Fact]
    public async Task Interrupt_DuringRun_RecordsInterruptedDiagnosticAndCompletes()
    {
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "thinking... "));
        // A blocked update keeps the run in progress until the interrupt cancels it.
        stream.EnqueueUpdate(
            new ChatResponseUpdate(ChatRole.Assistant, "more")
            {
                FinishReason = ChatFinishReason.Stop,
            },
            isReady: false);
        stream.Complete(isReady: false);

        await using var chat = CreateChat(client);
        chat.EnqueueUserMessage("hello");

        await WaitForConditionAsync(
            chat.RunningItems,
            () => chat.RunningItems.Count == 1
                && RunningItemContents(chat).OfType<TextContent>().Any(content => content.Text.Contains("thinking", StringComparison.Ordinal)),
            "run to start and stream initial content");

        chat.Interrupt();

        await WaitForConditionAsync(
            chat.RunningItems,
            () => chat.RunningItems.Count == 0
                && chat.History.Any(item => item.Role == AgentChatHistoryItem.DiagnosticChatRole
                    && GetText(item.Contents).Contains("Interrupted", StringComparison.Ordinal)),
            "interrupt to complete the run and record an interrupted diagnostic message");
    }

    [Fact]
    public async Task Completion_WritesFullyCoalescedStreamingResponseToHistory()
    {
        // Streaming updates are conflated (intermediate frames may be skipped while a coalesce is in
        // flight), but the run must not complete until the final accumulated frame has been fully
        // processed. Otherwise the running item would be committed to history while still partial,
        // producing duplicate/flickering content. Releasing the final fragments only after the first
        // is shown forces the final frame to arrive immediately before completion.
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "alpha "));
        var blockedSecond = stream.EnqueueUpdate(
            new ChatResponseUpdate(ChatRole.Assistant, "beta "),
            isReady: false);
        var blockedThird = stream.EnqueueUpdate(
            new ChatResponseUpdate(ChatRole.Assistant, "gamma.")
            {
                FinishReason = ChatFinishReason.Stop,
            },
            isReady: false);
        var blockedComplete = stream.Complete(isReady: false);

        await using var chat = CreateChat(client);
        chat.EnqueueUserMessage("go");

        await WaitForConditionAsync(
            chat.RunningItems,
            () => chat.RunningItems.Count == 1
                && RunningItemContents(chat).OfType<TextContent>().Any(content => content.Text.Contains("alpha", StringComparison.Ordinal)),
            "running item to appear after the first streamed fragment");

        blockedSecond.MarkReady();
        blockedThird.MarkReady();
        blockedComplete.MarkReady();

        await WaitForConditionAsync(
            chat.RunningItems,
            () => chat.RunningItems.Count == 0 && chat.History.Count == 2,
            "run to complete and commit the assistant response to history");

        // At the moment the history is updated, the full coalesced response must be present.
        Assert.Equal(2, chat.History.Count);
        Assert.Equal(ChatRole.Assistant, chat.History[1].Role);
        Assert.Equal("alpha beta gamma.", GetText(chat.History[1].Contents));
        Assert.Empty(chat.RunningItems);
    }

    [Fact]
    public async Task Interrupt_CommitsStreamedContentBeforeInterruptedDiagnostic()
    {
        // On interrupt the latest streamed frame must be drained before the diagnostic is recorded, so
        // the committed history shows the streamed content first and the "Interrupted" message last
        // (no late frame clobbering the diagnostic, no duplicate content).
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "partial answer "));
        stream.EnqueueUpdate(
            new ChatResponseUpdate(ChatRole.Assistant, "more")
            {
                FinishReason = ChatFinishReason.Stop,
            },
            isReady: false);
        stream.Complete(isReady: false);

        await using var chat = CreateChat(client);
        chat.EnqueueUserMessage("hello");

        await WaitForConditionAsync(
            chat.RunningItems,
            () => chat.RunningItems.Count == 1
                && RunningItemContents(chat).OfType<TextContent>().Any(content => content.Text.Contains("partial answer", StringComparison.Ordinal)),
            "run to start and stream initial content");

        chat.Interrupt();

        await WaitForConditionAsync(
            chat.History,
            () => chat.History.Count == 3
                && chat.History[^1].Role == AgentChatHistoryItem.DiagnosticChatRole,
            "interrupt to commit streamed content followed by the interrupted diagnostic");

        Assert.Equal(3, chat.History.Count);
        Assert.Equal(ChatRole.Assistant, chat.History[1].Role);
        Assert.Contains("partial answer", GetText(chat.History[1].Contents), StringComparison.Ordinal);
        Assert.Equal(AgentChatHistoryItem.DiagnosticChatRole, chat.History[2].Role);
        Assert.Contains("Interrupted", GetText(chat.History[2].Contents), StringComparison.Ordinal);
        Assert.Empty(chat.RunningItems);
    }

    [Fact]
    public void ResolveUseProvidedChatClientAsIs_TrueForOverride_SelfInvoking_OrServiceDiscovered()
    {
        var normalClient = new DeterministicTestChatClient();
        var selfInvokingClient = new SelfInvokingTestChatClient();
        var serviceDiscoveredClient = new ServiceDiscoveredSelfInvokingChatClient();

        // A normal resolved client (no override) needs the framework's function-invoking middleware.
        Assert.False(AgentChat.ResolveUseProvidedChatClientAsIs(hasClientOverride: false, normalClient));
        // An explicitly provided client is used as-is.
        Assert.True(AgentChat.ResolveUseProvidedChatClientAsIs(hasClientOverride: true, normalClient));
        // A self-invoking client (e.g. Copilot SDK) is used as-is so its streaming tool events aren't buffered.
        Assert.True(AgentChat.ResolveUseProvidedChatClientAsIs(hasClientOverride: false, selfInvokingClient));
        // Self-invocation is also honored when advertised via GetService (survives client wrappers).
        Assert.True(AgentChat.ResolveUseProvidedChatClientAsIs(hasClientOverride: false, serviceDiscoveredClient));
    }

    [Fact]
    public void CopilotSdkChatClient_IsSelfInvokingToolChatClient()
    {
        Assert.True(typeof(ISelfInvokingToolChatClient).IsAssignableFrom(typeof(CopilotSdkChatClient)));
    }

    private sealed class SelfInvokingTestChatClient : IChatClient, ISelfInvokingToolChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class ServiceDiscoveredSelfInvokingChatClient : IChatClient
    {
        private static readonly ISelfInvokingToolChatClient Marker = new MarkerOnly();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(ISelfInvokingToolChatClient) ? Marker : null;

        public void Dispose()
        {
        }

        private sealed class MarkerOnly : ISelfInvokingToolChatClient
        {
        }
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
    public async Task Constructor_DefaultQueueStartsImmediate_AndImmediateQueueStartsImmediate()
    {
        await using var chat = CreateChat();

        Assert.Equal(AgentInputQueueImmediacy.Immediate, chat.DefaultInputQueue.Immediacy);
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

    [Fact]
    public async Task SteeringMessage_InjectedWhileRunActive_AppearsAfterAssistantTurnInHistory()
    {
        // Arrange: a chat client that exposes a ToolResultSteeringMiddleware via GetService while
        // delegating actual streaming to a DeterministicTestChatClient. This lets the test fire
        // MessagesInjected (via a real tool-result middleware call) while a run is still streaming,
        // verifying that the steering message ends up after the assistant turn in History (#42).
        var innerClient = new DeterministicTestChatClient();
        var stream = innerClient.EnqueueStreamingResponse();
        var blockedUpdate = stream.EnqueueUpdate(
            new ChatResponseUpdate(ChatRole.Assistant, "response") { FinishReason = ChatFinishReason.Stop },
            isReady: false);
        var blockedComplete = stream.Complete(isReady: false);

        var queueManager = new AgentInputQueueManager();
        await using var compositeClient = new SteeringPassthroughChatClient(innerClient, queueManager);
        await using var chat = CreateChat(compositeClient);

        // Act: start a run that pauses mid-stream, inject a steering message, then release.
        chat.EnqueueUserMessage("user turn");
        await WaitForConditionAsync(chat.RunningItems, () => chat.RunningItems.Count > 0, "run to become active");

        queueManager.Enqueue(
            queueManager.ImmediateQueue,
            [new AgentInputItem { Messages = [new ChatMessage(ChatRole.User, "steer me")] }]);
        await compositeClient.SteeringMiddleware.GetResponseAsync(
            [new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "done")])]);

        blockedUpdate.MarkReady();
        blockedComplete.MarkReady();

        await WaitForConditionAsync(chat.History, () => chat.History.Count >= 3, "history to contain user + assistant + steering");

        // Assert: user turn first, then assistant turn, then steering message.
        Assert.Equal(3, chat.History.Count);
        Assert.Equal(ChatRole.User, chat.History[0].Role);
        Assert.Equal(ChatRole.Assistant, chat.History[1].Role);
        Assert.Equal(ChatRole.User, chat.History[2].Role);
        Assert.Equal("steer me", GetText(chat.History[2].Contents));
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

    // Delegates streaming to an inner DeterministicTestChatClient while exposing a separate
    // ToolResultSteeringMiddleware via GetService so that AgentChat.InitializeAsync subscribes
    // to MessagesInjected. This lets tests fire steering injection mid-run by calling
    // SteeringMiddleware.GetResponseAsync with a tool-result message.
    private sealed class SteeringPassthroughChatClient : IAsyncDisposable, IChatClient
    {
        private readonly DeterministicTestChatClient inner;
        private readonly ToolResultSteeringMiddleware steeringMiddleware;

        public SteeringPassthroughChatClient(DeterministicTestChatClient inner, AgentInputQueueManager queueManager)
        {
            this.inner = inner;
            var stub = new StubInnerForSteering();
            this.steeringMiddleware = new ToolResultSteeringMiddleware(stub, queueManager);
        }

        public ToolResultSteeringMiddleware SteeringMiddleware => this.steeringMiddleware;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => this.inner.GetResponseAsync(messages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => this.inner.GetStreamingResponseAsync(messages, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(ToolResultSteeringMiddleware)
                ? this.steeringMiddleware
                : this.inner.GetService(serviceType, serviceKey);

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class StubInnerForSteering : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
                => Task.FromResult(new ChatResponse());

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                yield break;
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public void Dispose() { }
        }
    }

}
