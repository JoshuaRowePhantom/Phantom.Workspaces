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
        string description,
        CancellationToken cancellationToken = default)
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

            await signal.Task.WaitAsync(cancellationToken);
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
        // Use a non-empty response so History reaches [user, assistant] and the condition resolves.
        await using var chat = CreateChat(
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var image = new DataContent(new byte[] { 0x01, 0x02 }, "image/png");
        chat.EnqueueUserContents([new TextContent("hello"), image]);
        await WaitForConditionAsync(chat.History, () => chat.History.Count >= 2, "history to contain user and assistant placeholder", timeout.Token);

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

        Assert.Single(chat.History);
        Assert.Equal(ChatRole.User, chat.History[0].Role);
        var runningAssistant = Assert.Single(chat.RunningItems);
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

        Assert.Single(chat.History);
        Assert.Equal(ChatRole.User, chat.History[0].Role);
        Assert.Single(chat.RunningItems);
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
    public async Task StreamingInProgress_SurfacesToolCallAndResultInHistoryBeforeCompletion()
    {
        // Mid-turn promotion (fix #305) promotes stable items to History as soon as a role
        // boundary makes them stable. Once the tool result arrives, CoalesceAsync appends a blank
        // assistant placeholder, making both the FunctionCallContent turn and the tool-result turn
        // stable (stableCount = 2). Both are promoted to History immediately, while only the blank
        // placeholder remains as the active tail in RunningItems.
        // This test verifies that both items are observable in History before the final "Done."
        // update is released, i.e. while the run is still in progress.
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

        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Both stable items (assistant turn with FunctionCallContent, and the tool-result turn)
        // are promoted to History mid-stream once the tool result arrives. Verify they appear in
        // History before the gated final update is released.
        await WaitForConditionAsync(
            chat.History,
            () => chat.History.Any(h => h.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == "call-1"))
                && chat.History.Any(h => h.Contents.OfType<FunctionResultContent>().Any(c => c.CallId == "call-1")),
            "tool call and result to be promoted to history before final update is released",
            cts.Token);

        // The running item should hold only the blank assistant placeholder (the active tail).
        Assert.Single(chat.RunningItems);

        blockedFinal.MarkReady();
        blockedComplete.MarkReady();
        await WaitForConditionAsync(chat.RunningItems, () => chat.RunningItems.Count == 0, "run to complete", cts.Token);
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
        // CreateChat() with an empty streaming response produces no assistant item in History
        // (empty streams leave History with only the user message — see EmptyStream_HistoryEmpty_AfterTurnEnd).
        // Use a non-empty response so that History.Count >= 2 is satisfiable.
        await using var chat = CreateChat(new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] });
        var queue = chat.QueueManager.CreateInputQueue();

        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
        chat.EnqueueUserMessage("queued later", queue);
        await WaitForConditionAsync(chat.History, () => chat.History.Count >= 2, "queued message to publish to history", cts.Token);

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
        Assert.Single(chat.History);

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
    public async Task ProviderException_AppendsDiagnosticRoleErrorContent()
    {
        // Reproduces GitHub issue #267 (Bug 2): the provider error item must carry DiagnosticChatRole
        // so the renderer does not emit a second [assistant] header inside the same assistant turn.
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueException(new InvalidOperationException("budget limit"));
        await using var chat = CreateChat(client);

        chat.EnqueueUserMessage("hello");
        await WaitForConditionAsync(
            chat.History,
            () => chat.History.Any(item =>
                item.Role == AgentChatHistoryItem.DiagnosticChatRole &&
                item.Contents.OfType<ErrorContent>().Any()),
            "error content turn to be appended after provider exception");

        var diagnosticErrorTurn = Assert.Single(
            chat.History,
            item => item.Role == AgentChatHistoryItem.DiagnosticChatRole &&
                item.Contents.OfType<ErrorContent>().Any());
        var error = Assert.Single(diagnosticErrorTurn.Contents.OfType<ErrorContent>());
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
    public async Task SteeringMessage_InjectedWhileRunActive_AppearsAtInjectionPointInRunningItem()
    {
        // Arrange: a chat client that exposes a ToolResultSteeringMiddleware via GetService while
        // delegating actual streaming to a DeterministicTestChatClient. This lets the test fire
        // MessagesInjected (via a real tool-result middleware call) mid-stream, verifying that the
        // steering message ends up at the update-count boundary where it was injected (#42).
        var innerClient = new DeterministicTestChatClient();
        var stream = innerClient.EnqueueStreamingResponse();

        // "first " arrives immediately; "second" is held until after steering injection.
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "first "));
        var blockedSecond = stream.EnqueueUpdate(
            new ChatResponseUpdate(ChatRole.Assistant, "second") { FinishReason = ChatFinishReason.Stop },
            isReady: false);
        var blockedComplete = stream.Complete(isReady: false);

        var queueManager = new AgentInputQueueManager();
        await using var compositeClient = new SteeringPassthroughChatClient(innerClient, queueManager);
        await using var chat = CreateChat(compositeClient);

        // Act: start the run, wait for "first " to land in the running item, then inject steering
        // at that boundary before releasing the rest of the stream.
        chat.EnqueueUserMessage("user turn");
        await WaitForConditionAsync(
            chat.RunningItems,
            () => chat.RunningItems.Count > 0
                  && chat.RunningItems[0].Items.Count > 0
                  && GetText(chat.RunningItems[0].Items[0].Contents).Contains("first"),
            "first streaming update to appear in running item");

        queueManager.Enqueue(
            queueManager.ImmediateQueue,
            [new AgentInputItem { Messages = [new ChatMessage(ChatRole.User, "steer me")] }]);
        await compositeClient.SteeringMiddleware.GetResponseAsync(
            [new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "done")])]);

        blockedSecond.MarkReady();
        blockedComplete.MarkReady();

        await WaitForConditionAsync(chat.History, () => chat.History.Count >= 4, "history to contain user + first + steer + second");

        // Assert: user turn, then assistant "first ", then steering user message, then assistant "second".
        // The steering appears at the update boundary where it was injected, not before or after the full turn.
        Assert.Equal(4, chat.History.Count);
        Assert.Equal(ChatRole.User, chat.History[0].Role);
        Assert.Equal(ChatRole.Assistant, chat.History[1].Role);
        Assert.Equal("first ", GetText(chat.History[1].Contents));
        Assert.Equal(ChatRole.User, chat.History[2].Role);
        Assert.Equal("steer me", GetText(chat.History[2].Contents));
        Assert.Equal(ChatRole.Assistant, chat.History[3].Role);
        Assert.Equal("second", GetText(chat.History[3].Contents));
    }

    [Fact]
    public async Task CreateAsync_OnTaskRunThread_WithExplicitForegroundScheduler_UsesThatSchedulerAndProcessesMessages()
    {
        // Regression test for GitHub issue #70: before e51ec14, AgentChat was created on the UI thread
        // so SynchronizationContext.Current was the Avalonia dispatcher. After e51ec14, creation moved
        // inside Task.Run where SynchronizationContext.Current is null, causing AgentChat to fall back
        // to its internal ConcurrentExclusiveSchedulerPair rather than the UI scheduler. The fix
        // captures the UI scheduler before entering Task.Run and passes it via ForegroundScheduler so
        // the processing loop always uses the correct scheduler regardless of the construction thread.

        // Arrange: set up a response stream and capture an explicit scheduler to simulate the UI
        // scheduler that would be captured on the real UI thread before Task.Run.
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "pong")
        {
            FinishReason = ChatFinishReason.Stop,
        });
        stream.Complete();

        // Use a distinct ConcurrentExclusiveSchedulerPair as a stand-in for the UI scheduler.
        var uiSchedulerPair = new System.Threading.Tasks.ConcurrentExclusiveSchedulerPair();
        var capturedForegroundScheduler = uiSchedulerPair.ExclusiveScheduler;

        // Act: create the chat inside Task.Run (no SynchronizationContext on that thread), but
        // supply the pre-captured scheduler so the processing loop uses the right scheduler.
        var agentDefinition = AgentDefinitionLoader.LoadAgentFromJson(DefaultAgentDefinitionJson);
        var persistenceStore = new InMemoryAgentPersistenceStore();
        await using var chat = await Task.Run(async () =>
            await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
            {
                AgentDefinition = agentDefinition,
                ConfiguredStore = persistenceStore,
                ClientOverride = client,
                DisplayNameOverride = "test-chat",
                ForegroundScheduler = capturedForegroundScheduler,
            }));

        // Assert: the private foregroundScheduler field is the one we passed in, not the internal
        // fallback ExclusiveScheduler that would be chosen when SynchronizationContext.Current is null.
        var foregroundSchedulerField = typeof(AgentChat).GetField(
            "foregroundScheduler",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(foregroundSchedulerField);
        var actualScheduler = foregroundSchedulerField!.GetValue(chat);
        Assert.Same(capturedForegroundScheduler, actualScheduler);

        // Also verify messages are processed end-to-end on that scheduler.
        chat.EnqueueUserMessage("ping");
        await WaitForConditionAsync(
            chat.History,
            () => chat.History.Count == 2 && chat.History[^1].Role == ChatRole.Assistant,
            "assistant response to complete");

        Assert.Equal(2, chat.History.Count);
        Assert.Equal("pong", GetText(chat.History[1].Contents));
    }


    [Fact]
    public async Task PerServiceCallPersistence_AssistantResponsePersistedBeforeSecondServiceCallCompletes()
    {
        // Arrange: register a zero-argument tool so FunctionInvokingChatClient can execute the
        // tool call returned by the first LLM response and make a second service call.
        var simpleTool = AIFunctionFactory.Create(() => "tool result", "simple_tool", "A simple test tool.");
        var toolsetFactory = ToolsetFactory.CreateNamedToolsetFactory(
            "test_tools",
            (_, _) => Task.FromResult<Microsoft.Agents.AI.AIContextProvider?>(
                ToolsetFactory.CreateFixedToolset(simpleTool)));
        var agentServices = new AgentServices { ToolsetFactory = toolsetFactory };

        var client = new DeterministicTestChatClient();

        // First service call: the LLM invokes simple_tool.
        var firstStream = client.EnqueueStreamingResponse();
        firstStream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, [
            new FunctionCallContent("call-1", "simple_tool", new System.Collections.Generic.Dictionary<string, object?>()),
        ])
        {
            FinishReason = ChatFinishReason.ToolCalls,
        });
        firstStream.Complete();

        // Second service call: blocked until the test releases it, keeping the run in-flight.
        var secondStream = client.EnqueueStreamingResponse(isReady: false);

        var store = new SignalingPersistenceStore();
        var agentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "echo-agent",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
              "tools": [{ "kind": "test_tools", "description": "Test tool set" }]
            }
            """);

        // OverrideUseProvidedChatClientAsIs = false so the framework inserts
        // FunctionInvokingChatClient and PerServiceCallChatHistoryPersistingChatClient.
        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = agentDefinition,
            ConfiguredStore = store,
            ClientOverride = client,
            DisplayNameOverride = "test-chat",
            AgentServices = agentServices,
            OverrideUseProvidedChatClientAsIs = false,
        });

        // Act: send a user message to start the run.
        chat.EnqueueUserMessage("call the tool");

        // Wait for two StoreAsync calls:
        //   1st: ProvideChatHistoryAsync stores the user message before the first LLM call.
        //   2nd: StoreChatHistoryAsync stores the LLM's tool-call response after the first LLM call.
        await store.WaitForStoreAsync(CancellationToken.None);
        await store.WaitForStoreAsync(CancellationToken.None);

        // Assert: the run is still in-flight (second call is blocked) but the store already
        // contains the assistant's tool-call response from the first service call.
        var sessionId = chat.AgentSessionId;
        var messages = await store.ReadMessagesAsync(
            new ReadMessagesRequest { AgentSessionId = sessionId },
            CancellationToken.None);

        Assert.Contains(messages, m =>
            m.Role == ChatRole.Assistant &&
            m.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == "call-1"));

        // Release the second service call and let the run complete.
        secondStream.MarkReady();
        secondStream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "Done.")
        {
            FinishReason = ChatFinishReason.Stop,
        });
        secondStream.Complete();

        await WaitForConditionAsync(
            chat.RunningItems,
            () => chat.RunningItems.Count == 0,
            "run to complete after releasing the second stream");
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

    [Fact]
    public async Task UpdateParameterValues_SetsWorkingDirectoryInAdditionalProperties()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson("""
                {
                  "kind": "prompt",
                  "name": "test-agent",
                  "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
                }
                """),
        });

        chat.UpdateParameterValues(new Dictionary<string, string> { ["working-directory"] = @"C:\updated" });

        var chatOptions = (ChatClientAgentOptions?)typeof(AgentChat)
            .GetField("chatOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(chat);
        Assert.NotNull(chatOptions);
        var additionalProperties = chatOptions.ChatOptions!.AdditionalProperties;
        Assert.NotNull(additionalProperties);
        Assert.Equal(@"C:\updated", additionalProperties["working-directory"] as string);
    }

    [Fact]
    public async Task UpdateParameterValues_ChangesSessionSignatureForCopilotClient()
    {
        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson("""
                {
                  "kind": "prompt",
                  "name": "test-agent",
                  "model": {
                    "id": "echo",
                    "provider": "echo",
                    "apiType": "Echo",
                    "options": { "additionalProperties": { "working-directory": "C:\\original" } }
                  }
                }
                """),
        });

        var chatOptions = (ChatClientAgentOptions?)typeof(AgentChat)
            .GetField("chatOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(chat);
        var signatureBefore = CopilotSdkChatClient.ComputeSessionSignature(chatOptions!.ChatOptions);

        chat.UpdateParameterValues(new Dictionary<string, string> { ["working-directory"] = @"C:\updated" });

        var signatureAfter = CopilotSdkChatClient.ComputeSessionSignature(chatOptions.ChatOptions);
        Assert.NotEqual(signatureBefore, signatureAfter);
    }

    [Fact]
    public async Task RunSingleTurnAsync_WithDeterministicClient_StreamsUpdates()
    {
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "hello-single-turn"));
        stream.Complete();

        await using var chat = CreateChat(client);

        var updates = new System.Collections.Generic.List<ChatResponseUpdate>();
        await foreach (var update in chat.RunSingleTurnAsync(
            [new ChatMessage(ChatRole.User, "prompt")],
            CancellationToken.None))
        {
            updates.Add(update);
        }

        var text = string.Concat(updates.Select(static u => u.Text));
        Assert.Contains("hello-single-turn", text);
    }

    [Fact]
    public async Task RunSingleTurnAsync_WithNullPersistenceStore_SendsOnlyLatestMessage()
    {
        var client = new DeterministicTestChatClient();
        var agentDefinition = AgentDefinitionLoader.LoadAgentFromJson(DefaultAgentDefinitionJson);

        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = agentDefinition,
            ConfiguredStore = NullAgentPersistenceStore.Instance,
            ClientOverride = client,
            DisplayNameOverride = "null-store-test",
            OverrideUseProvidedChatClientAsIs = false,
        });

        // Turn 1
        var stream1 = client.EnqueueStreamingResponse();
        stream1.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "response-1"));
        stream1.Complete();
        await DrainSingleTurnAsync(chat.RunSingleTurnAsync(
            [new ChatMessage(ChatRole.User, "message-1")],
            CancellationToken.None));

        // Turn 2
        var stream2 = client.EnqueueStreamingResponse();
        stream2.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "response-2"));
        stream2.Complete();
        await DrainSingleTurnAsync(chat.RunSingleTurnAsync(
            [new ChatMessage(ChatRole.User, "message-2")],
            CancellationToken.None));

        // With NullAgentPersistenceStore, turn 2 should not prepend turn 1's message from storage.
        var lastMessages = client.LastRequestMessages;
        Assert.DoesNotContain(lastMessages, m => (m.Text ?? string.Empty).Contains("message-1"));
        Assert.Contains(lastMessages, m => (m.Text ?? string.Empty).Contains("message-2"));
    }

    private static async Task DrainSingleTurnAsync(IAsyncEnumerable<ChatResponseUpdate> source)
    {
        await foreach (var _ in source)
        {
        }
    }

    /// <summary>
    /// Wraps <see cref="InMemoryAgentPersistenceStore"/> and releases a semaphore after each
    /// <see cref="StoreAsync"/> call so tests can synchronise deterministically on store writes.
    /// </summary>
    private sealed class SignalingPersistenceStore : IAgentPersistenceStore
    {
        private readonly InMemoryAgentPersistenceStore inner = new();
        private readonly SemaphoreSlim storeSignal = new(0);

        public Task WaitForStoreAsync(CancellationToken cancellationToken)
            => this.storeSignal.WaitAsync(cancellationToken);

        public async ValueTask StoreAsync(
            StoreRequestAgent request,
            CancellationToken cancellationToken = default)
        {
            await this.inner.StoreAsync(request, cancellationToken).ConfigureAwait(false);
            this.storeSignal.Release();
        }

        public ValueTask<PersistedAgent?> RestoreAsync(
            RestoreRequest request,
            CancellationToken cancellationToken = default)
            => this.inner.RestoreAsync(request, cancellationToken);

        public ValueTask<Microsoft.Extensions.AI.ChatMessage[]> ReadMessagesAsync(
            ReadMessagesRequest request,
            CancellationToken cancellationToken = default)
            => this.inner.ReadMessagesAsync(request, cancellationToken);

        public ValueTask<SubAgentManifestEntry[]> ReadSubAgentManifestAsync(string parentSessionId, CancellationToken cancellationToken = default)
            => this.inner.ReadSubAgentManifestAsync(parentSessionId, cancellationToken);

        public ValueTask WriteSubAgentManifestEntryAsync(string parentSessionId, SubAgentManifestEntry entry, CancellationToken cancellationToken = default)
            => this.inner.WriteSubAgentManifestEntryAsync(parentSessionId, entry, cancellationToken);
    }

    [Fact]
    public void TryGetSubAgentIdByToolCallId_ReturnsNull_WhenNotRegistered()
    {
        // Verify that a fresh AgentChat returns null for any parentToolCallId
        // before any sub-agent has been registered.
        var chat = CreateChat();

        var result = chat.TryGetSubAgentIdByToolCallId("nonexistent-tool-call-id");

        Assert.Null(result);
    }

}
