using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Interfaces;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentChatHostedSubAgentTests
{
    private static AgentChat CreateHostedSubAgentChat(IChatClient hostedClient)
    {
        var agentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "hosted-subagent",
              "model": {
                "id": "github-copilot-subagent",
                "provider": "github-copilot-subagent"
              },
              "tools": []
            }
            """);
        var persistenceStore = new InMemoryAgentPersistenceStore();
        return AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = agentDefinition,
            ConfiguredStore = persistenceStore,
            ClientOverride = hostedClient,
            DisplayNameOverride = "test-hosted-chat",
        }).GetAwaiter().GetResult();
    }

    private static AgentChat CreateUserDrivenChat()
    {
        var agentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "user-driven-agent",
              "model": {
                "id": "echo",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);
        var persistenceStore = new InMemoryAgentPersistenceStore();
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("response")]
        });
        stream.Complete();

        return AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = agentDefinition,
            ConfiguredStore = persistenceStore,
            ClientOverride = client,
            DisplayNameOverride = "test-user-driven-chat",
        }).GetAwaiter().GetResult();
    }

    private static async Task WaitForHistoryCountAsync(
        AgentChat chat,
        int expectedCount,
        CancellationToken cancellationToken = default)
    {
        var collection = (System.Collections.Specialized.INotifyCollectionChanged)chat.History;
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            try
            {
                if (chat.History.Count >= expectedCount)
                {
                    signal.TrySetResult();
                }
            }
            catch (System.InvalidOperationException)
            {
            }
        }

        collection.CollectionChanged += OnCollectionChanged;
        try
        {
            if (chat.History.Count >= expectedCount)
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

    private static async Task WaitForCompletionStateAsync(
        AgentChat chat,
        AgentChatCompletionState expectedState,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        while (chat.CompletionState != expectedState)
        {
            await Task.Delay(50, cts.Token);
        }
    }

    [Fact]
    public async Task AgentChat_HostedSubAgent_ProcessLoop_ConsumesChannelData_WithoutUserInput()
    {
        var hostedClient = new CopilotSubAgentChatClient();

        await using var chat = CreateHostedSubAgentChat(hostedClient);

        hostedClient.Push(new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("hello from SDK")]
        });
        hostedClient.Complete();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WaitForHistoryCountAsync(chat, 1, cts.Token);

        Assert.Single(chat.History);
        Assert.Equal(ChatRole.Assistant, chat.History[0].Role);
        Assert.Contains("hello from SDK", GetText(chat.History[0].Contents));
    }

    [Fact]
    public async Task AgentChat_HostedSubAgent_History_PopulatedFromSdkAssistantMessages()
    {
        var hostedClient = new CopilotSubAgentChatClient();

        await using var chat = CreateHostedSubAgentChat(hostedClient);

        hostedClient.Push(new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("first ")]
        });
        hostedClient.Push(new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("second")]
        });
        hostedClient.Complete();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WaitForHistoryCountAsync(chat, 1, cts.Token);

        Assert.Single(chat.History);
        Assert.Equal(ChatRole.Assistant, chat.History[0].Role);
        var text = GetText(chat.History[0].Contents);
        Assert.Contains("first", text);
        Assert.Contains("second", text);
    }

    [Fact]
    public async Task AgentChat_HostedSubAgent_ToolCallHistory_PopulatedFromSdkToolEvents()
    {
        var hostedClient = new CopilotSubAgentChatClient();

        await using var chat = CreateHostedSubAgentChat(hostedClient);

        hostedClient.Push(new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents =
            [
                new FunctionCallContent("testCallId", "testTool", new Dictionary<string, object?>
                {
                    ["arg1"] = "value1"
                })
            ]
        });
        hostedClient.Complete();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WaitForHistoryCountAsync(chat, 1, cts.Token);

        Assert.Single(chat.History);
        Assert.Equal(ChatRole.Assistant, chat.History[0].Role);
        var functionCall = Assert.Single(chat.History[0].Contents.OfType<FunctionCallContent>());
        Assert.Equal("testTool", functionCall.Name);
    }

    [Fact]
    public async Task AgentChat_HostedSubAgent_CompletionState_SetToSucceeded_WhenStreamCompletes()
    {
        var hostedClient = new CopilotSubAgentChatClient();

        await using var chat = CreateHostedSubAgentChat(hostedClient);

        hostedClient.Push(new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("done")]
        });
        hostedClient.Complete();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WaitForCompletionStateAsync(chat, AgentChatCompletionState.Succeeded, cts.Token);

        Assert.Equal(AgentChatCompletionState.Succeeded, chat.CompletionState);
    }

    [Fact]
    public async Task AgentChat_HostedSubAgent_CompletionState_SetToFailed_WhenStreamFails()
    {
        var hostedClient = new CopilotSubAgentChatClient();

        await using var chat = CreateHostedSubAgentChat(hostedClient);

        hostedClient.Push(new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("partial")]
        });
        hostedClient.Fail(new System.InvalidOperationException("SDK error"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WaitForCompletionStateAsync(chat, AgentChatCompletionState.Failed, cts.Token);

        Assert.Equal(AgentChatCompletionState.Failed, chat.CompletionState);
    }

    [Fact]
    public async Task AgentChat_HostedSubAgent_AcceptsUserInput_IsFalse()
    {
        var hostedClient = new CopilotSubAgentChatClient();
        await using var chat = CreateHostedSubAgentChat(hostedClient);

        Assert.False(chat.AcceptsUserInput);
    }

    [Fact]
    public async Task AgentChat_UserDrivenAgent_ProcessLoop_StillRequiresUserInput()
    {
        await using var chat = CreateUserDrivenChat();

        Assert.True(chat.AcceptsUserInput);
        Assert.Empty(chat.History);

        chat.EnqueueUserMessage("test message");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WaitForHistoryCountAsync(chat, 2, cts.Token);

        Assert.Equal(2, chat.History.Count);
        Assert.Equal(ChatRole.User, chat.History[0].Role);
        Assert.Equal(ChatRole.Assistant, chat.History[1].Role);
    }

    private static string GetText(IEnumerable<AIContent> contents)
    {
        var text = string.Empty;
        foreach (var content in contents)
        {
            if (content is TextContent tc)
            {
                text += tc.Text;
            }
        }
        return text;
    }

    [Fact]
    public async Task RestoreSubAgentsAsync_WithPersistedChildren_PopulatesSubAgents()
    {
        var store = new InMemoryAgentPersistenceStore();
        var factory = new AgentChatFactory(
            store,
            new AgentServices { ChatClientOverride = new DeterministicTestChatClient() },
            TaskScheduler.Default);

        var parentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "parent-agent",
              "model": {
                "id": "echo",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

        // Create parent and persist parent→child link directly
        var parentSessionId = Guid.NewGuid().ToString("n");
        var childSessionId = Guid.NewGuid().ToString("n");
        
        await store.AddSubAgentLinkAsync(parentSessionId, childSessionId);

        // Create a parent instance that will restore subagents
        await using var restoredParent = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = parentDefinition,
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "restored-parent",
            AgentSessionId = parentSessionId,
            AgentServices = new AgentServices
            {
                RunningAgentChatFactory = factory,
                ChatClientOverride = new DeterministicTestChatClient()
            },
        });

        // Wait for the SubAgents collection to be populated
        await WaitForSubAgentCountAsync(restoredParent, 1, CancellationToken.None);

        // Assert: SubAgents should be populated with a stub
        Assert.Single(restoredParent.SubAgents);
        var restoredChild = Assert.IsType<SubAgent>(Assert.Single(restoredParent.SubAgents));
        Assert.Equal(childSessionId, restoredChild.SessionId.Value);
    }

    private static async Task WaitForSubAgentCountAsync(
        AgentChat chat,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        var collection = (System.Collections.Specialized.INotifyCollectionChanged)chat.SubAgents;
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (chat.SubAgents.Count >= expectedCount)
            {
                signal.TrySetResult();
            }
        }

        collection.CollectionChanged += OnCollectionChanged;
        try
        {
            if (chat.SubAgents.Count >= expectedCount)
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


    [Fact]
    public async Task RestoreSubAgentsAsync_NoFactory_Throws()
    {
        // Fix #1109: RunningAgentChatFactory is mandatory when there are children to restore.
        // The old warn-and-skip branch silently dropped restored sub-agents and their output
        // then leaked into the parent transcript (issue #1110). Now it throws.
        var store = new InMemoryAgentPersistenceStore();

        var parentSessionId = "parent-session-id";
        var childSessionId = "child-session-id";
        await store.AddSubAgentLinkAsync(parentSessionId, childSessionId);

        var testLoggerFactory = new TestLoggerFactory();

        var parentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "parent-agent",
              "model": {
                "id": "echo",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var _ = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
            {
                AgentDefinition = parentDefinition,
                ConfiguredStore = store,
                ClientOverride = new DeterministicTestChatClient(),
                DisplayNameOverride = "restored-parent",
                AgentSessionId = parentSessionId,
                AgentServices = new AgentServices
                {
                    // No RunningAgentChatFactory provided.
                    LoggerFactory = testLoggerFactory,
                    ChatClientOverride = new DeterministicTestChatClient()
                },
            });
        });
    }

    private sealed class TestLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
    {
        private readonly List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> _logs = new();

        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new TestLogger(_logs);

        public void Dispose() { }

        public List<string> GetLogs(Microsoft.Extensions.Logging.LogLevel level) =>
            _logs.Where(l => l.Level == level).Select(l => l.Message).ToList();

        private sealed class TestLogger : Microsoft.Extensions.Logging.ILogger
        {
            private readonly List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> _logs;

            public TestLogger(List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> logs)
            {
                _logs = logs;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(
                Microsoft.Extensions.Logging.LogLevel logLevel,
                Microsoft.Extensions.Logging.EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _logs.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
