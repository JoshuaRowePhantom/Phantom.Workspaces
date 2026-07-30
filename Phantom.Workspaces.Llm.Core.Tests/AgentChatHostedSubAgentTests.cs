using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
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

    // ─── Fix #1139 tests ─────────────────────────────────────────────────────────
    //
    // These tests wire the real adapter->router->child-receiver->child AgentChat pipeline
    // so assertions land on a real child AgentChat's History rather than on a mocked receiver
    // stream. They reproduce the live drop described in issue #1139 and verify that with the
    // fix in place, a running sub-agent's live assistant text, tool calls, and reasoning
    // deltas surface in the correct child's transcript.

    private sealed class Fix1139TestFactory : Phantom.Workspaces.Llm.IRunningAgentChatFactory
    {
        public List<AgentChat> CreatedChildren { get; } = new();
        public List<Phantom.Workspaces.Llm.CopilotSubAgentChatClient> CreatedHostedClients { get; } = new();

        public System.Collections.ObjectModel.ObservableCollection<Phantom.Workspaces.Llm.RunningAgentChat> RunningSessions { get; } = new();

        public Task<RunningAgentChatLease> CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            string? nameOverride = null, CancellationToken ct = default)
        {
            var hostedClient = new Phantom.Workspaces.Llm.CopilotSubAgentChatClient();
            var child = CreateHostedSubAgentChat(hostedClient);
            CreatedHostedClients.Add(hostedClient);
            CreatedChildren.Add(child);
            return Task.FromResult(new RunningAgentChatLease(sessionId, child, () => ValueTask.CompletedTask));
        }

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<RunningAgentChatLease> GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition = null,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            bool registerAsRunningAgent = true, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class Fix1139FakeSubAgentTable : ISubAgentTable
    {
        Task<SubAgent> ISubAgentTable.Add(AgentChat agentChat)
        {
            var sessionId = new AgentSessionId(agentChat.AgentSessionId);
            return Task.FromResult(new SubAgent(sessionId, agentChat, null));
        }
    }

    // Content builders that mirror what CopilotSdkStreamAdapter emits.
    private static ChatResponseUpdate F1139_LifecycleStartWithoutAgentId(string parentToolCallId)
    {
        var call = new FunctionCallContent(
            string.Empty,
            CopilotSdkStreamAdapter.SubAgentStartLifecycleName,
            new Dictionary<string, object?>
            {
                [CopilotSdkStreamAdapter.ParentToolCallIdArgumentName] = parentToolCallId,
                [CopilotSdkStreamAdapter.DisplayNameArgumentName] = "Sub Agent",
                [CopilotSdkStreamAdapter.DescriptionArgumentName] = "desc",
            })
        {
            AdditionalProperties = new()
            {
                [CopilotSdkStreamAdapter.ContentTypePropertyName] = CopilotSdkStreamAdapter.SubAgentLifecycleContentType,
            },
        };
        return new ChatResponseUpdate { Contents = [call] };
    }

    private static ChatResponseUpdate F1139_LifecycleStart(string agentId, string parentToolCallId)
    {
        var call = new FunctionCallContent(
            agentId,
            CopilotSdkStreamAdapter.SubAgentStartLifecycleName,
            new Dictionary<string, object?>
            {
                [CopilotSdkStreamAdapter.ParentToolCallIdArgumentName] = parentToolCallId,
                [CopilotSdkStreamAdapter.DisplayNameArgumentName] = "Sub Agent",
                [CopilotSdkStreamAdapter.DescriptionArgumentName] = "desc",
            })
        {
            AdditionalProperties = new()
            {
                [CopilotSdkStreamAdapter.ContentTypePropertyName] = CopilotSdkStreamAdapter.SubAgentLifecycleContentType,
            },
        };
        return new ChatResponseUpdate { Contents = [call] };
    }

    private static ChatResponseUpdate F1139_SubAgentText(string agentId, string text)
    {
        var content = new TextContent(text)
        {
            AdditionalProperties = new()
            {
                [CopilotSdkStreamAdapter.ParentToolCallIdPropertyName] = agentId,
            },
        };
        return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [content] };
    }

    private static ChatResponseUpdate F1139_SubAgentReasoning(string agentId, string text)
    {
        var content = new TextReasoningContent(text)
        {
            AdditionalProperties = new()
            {
                [CopilotSdkStreamAdapter.ParentToolCallIdPropertyName] = agentId,
            },
        };
        return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [content] };
    }

    private static ChatResponseUpdate F1139_SubAgentToolStart(string agentId, string callId, string toolName)
    {
        var content = new FunctionCallContent(callId, toolName, new Dictionary<string, object?>())
        {
            AdditionalProperties = new()
            {
                [CopilotSdkStreamAdapter.ParentToolCallIdPropertyName] = agentId,
            },
        };
        return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [content] };
    }

    private static async Task<(Phantom.Workspaces.Llm.CopilotSubAgentRouter router, System.Threading.Channels.Channel<ChatResponseUpdate> rootChannel, Fix1139TestFactory factory)> F1139_CreateRouterAsync()
    {
        var rootChannel = System.Threading.Channels.Channel.CreateUnbounded<ChatResponseUpdate>();
        var factory = new Fix1139TestFactory();
        var router = new Phantom.Workspaces.Llm.CopilotSubAgentRouter(
            rootChannel.Writer,
            factory,
            new Fix1139FakeSubAgentTable());
        await Task.CompletedTask;
        return (router, rootChannel, factory);
    }

    [Fact]
    public async Task AgentChat_HostedSubAgent_StartedWithoutAgentId_ContentWithChildAgentId_RoutedToChildHistory()
    {
        // Fix #1139 crux: scripted SDK stream emits SubagentStartedEvent with NO AgentId
        // (only Data.ToolCallId), then assistant content stamped with a distinct child
        // runtime AgentId. The child AgentChat's History must receive that content
        // (start-time tool-call correlation binds the sink; on first content arrival the
        // pending entry is re-keyed under the child AgentId and its pending list is flushed
        // into the receiver — nothing is left parked).
        var (router, rootChannel, factory) = await F1139_CreateRouterAsync();
        try
        {
            await router.RouteAsync(F1139_LifecycleStartWithoutAgentId("call-1"));
            await router.RouteAsync(F1139_SubAgentText("child-runtime-id", "live sub-agent text"));

            var child = Assert.Single(factory.CreatedChildren);
            factory.CreatedHostedClients[0].Complete();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await WaitForHistoryCountAsync(child, 1, cts.Token);

            var text = GetText(child.History[0].Contents);
            Assert.Contains("live sub-agent text", text);
        }
        finally
        {
            await router.DisposeRemainingLeasesAsync();
            rootChannel.Writer.TryComplete();
        }
    }

    [Fact]
    public async Task AgentChat_HostedSubAgent_StartedWithoutAgentId_ToolCalls_RoutedToChildHistory()
    {
        // Fix #1139 tool-call face: with the same AgentId-less start, tool start events
        // stamped with the child AgentId surface as tool-call items in the child AgentChat's
        // History (not dropped, not misrouted to the parent).
        var (router, rootChannel, factory) = await F1139_CreateRouterAsync();
        try
        {
            await router.RouteAsync(F1139_LifecycleStartWithoutAgentId("call-1"));
            await router.RouteAsync(F1139_SubAgentToolStart("child-runtime-id", "tool-call-42", "sample_tool"));

            var child = Assert.Single(factory.CreatedChildren);
            factory.CreatedHostedClients[0].Complete();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await WaitForHistoryCountAsync(child, 1, cts.Token);

            var call = Assert.Single(child.History[0].Contents.OfType<FunctionCallContent>());
            Assert.Equal("sample_tool", call.Name);
            Assert.Equal("tool-call-42", call.CallId);
        }
        finally
        {
            await router.DisposeRemainingLeasesAsync();
            rootChannel.Writer.TryComplete();
        }
    }

    [Fact]
    public async Task AgentChat_HostedSubAgent_ReasoningDelta_RoutedToChildByEventAgentId()
    {
        // Fix #1139: AssistantReasoningDeltaData has no ParentToolCallId field at all. The
        // reasoning delta routes to the child AgentChat purely via the event-level AgentId,
        // confirming no dependence on the deprecated per-Data member.
        var (router, rootChannel, factory) = await F1139_CreateRouterAsync();
        try
        {
            await router.RouteAsync(F1139_LifecycleStart("child-a", "call-1"));
            await router.RouteAsync(F1139_SubAgentReasoning("child-a", "thinking hard"));

            var child = Assert.Single(factory.CreatedChildren);
            factory.CreatedHostedClients[0].Complete();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await WaitForHistoryCountAsync(child, 1, cts.Token);

            var reasoning = Assert.Single(child.History[0].Contents.OfType<TextReasoningContent>());
            Assert.Equal("thinking hard", reasoning.Text);
        }
        finally
        {
            await router.DisposeRemainingLeasesAsync();
            rootChannel.Writer.TryComplete();
        }
    }

    [Fact]
    public async Task AgentChat_HostedSubAgent_RunningChildContent_NotAppendedToParentHistory()
    {
        // Fix #1139 negative face: the child's running content is NOT misrouted onto the
        // parent (root) transcript AND is NOT dropped. It appears only in the child's own
        // History. This is the direct inverse of the observed bug where the content ended
        // up in neither.
        var (router, rootChannel, factory) = await F1139_CreateRouterAsync();
        try
        {
            await router.RouteAsync(F1139_LifecycleStart("child-a", "call-1"));
            await router.RouteAsync(F1139_SubAgentText("child-a", "child-only content"));

            var child = Assert.Single(factory.CreatedChildren);
            factory.CreatedHostedClients[0].Complete();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await WaitForHistoryCountAsync(child, 1, cts.Token);

            Assert.Contains("child-only content", GetText(child.History[0].Contents));

            // Parent (root) channel got nothing sub-agent-tagged.
            rootChannel.Writer.TryComplete();
            var parentUpdates = new List<ChatResponseUpdate>();
            await foreach (var u in rootChannel.Reader.ReadAllAsync())
                parentUpdates.Add(u);
            Assert.All(parentUpdates, u => Assert.DoesNotContain("child-only content", GetText(u.Contents)));
        }
        finally
        {
            await router.DisposeRemainingLeasesAsync();
        }
    }

    [Fact]
    public async Task AgentChat_HostedSubAgent_TwoConcurrentSubAgents_EachRoutedToOwnChildChat()
    {
        // Fix #1139 concurrent disambiguation: two concurrently-active children, each
        // stamped with its own AgentId, land in their own respective child AgentChat's
        // History with no cross-contamination.
        var (router, rootChannel, factory) = await F1139_CreateRouterAsync();
        try
        {
            await router.RouteAsync(F1139_LifecycleStart("child-a", "call-a"));
            await router.RouteAsync(F1139_LifecycleStart("child-b", "call-b"));

            await router.RouteAsync(F1139_SubAgentText("child-a", "A speaks"));
            await router.RouteAsync(F1139_SubAgentText("child-b", "B speaks"));
            await router.RouteAsync(F1139_SubAgentText("child-a", "A again"));

            Assert.Equal(2, factory.CreatedChildren.Count);
            var childA = factory.CreatedChildren[0];
            var childB = factory.CreatedChildren[1];
            factory.CreatedHostedClients[0].Complete();
            factory.CreatedHostedClients[1].Complete();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await WaitForHistoryCountAsync(childA, 1, cts.Token);
            await WaitForHistoryCountAsync(childB, 1, cts.Token);

            var textA = GetText(childA.History[0].Contents);
            var textB = GetText(childB.History[0].Contents);

            Assert.Contains("A speaks", textA);
            Assert.Contains("A again", textA);
            Assert.DoesNotContain("B speaks", textA);

            Assert.Contains("B speaks", textB);
            Assert.DoesNotContain("A speaks", textB);
            Assert.DoesNotContain("A again", textB);
        }
        finally
        {
            await router.DisposeRemainingLeasesAsync();
            rootChannel.Writer.TryComplete();
        }
    }
}
