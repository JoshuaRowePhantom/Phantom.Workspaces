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
    public async Task AgentChat_HostedSubAgent_PipelineDoesNotWrapWithFunctionInvokingChatClient()
    {
        var hostedClient = new CopilotSubAgentChatClient();
        await using var chat = CreateHostedSubAgentChat(hostedClient);

        // With CopilotSubAgentChatClient marked as ISelfInvokingToolChatClient (#1174),
        // ResolveUseProvidedChatClientAsIs returns true — instructing ChatClientAgent
        // to use the sub-agent stub as-is and omit FunctionInvokingChatClient wrapping.
        Assert.Equal(true, chat.UseProvidedChatClientAsIs);
    }

    [Fact]
    public async Task AgentChat_HostedSubAgent_ParentTaskFunctionCallInjected_ChildTranscriptDoesNotThrowFunctionNotFound()
    {
        var hostedClient = new CopilotSubAgentChatClient();
        await using var chat = CreateHostedSubAgentChat(hostedClient);

        // Simulates CopilotSubAgentRouter.InjectToolCallPrompt pushing the parent's
        // FunctionCallContent(Name="task") into the child sink. Before #1174 this
        // FunctionInvokingChatClient middleware wrapping the child throws
        // "Requested function 'task' not found" because the child's ChatOptions.Tools
        // is empty. After #1174 the middleware is not installed and the content
        // flows through the receive-only stub.
        hostedClient.Push(new ChatResponseUpdate
        {
            Role = ChatRole.User,
            Contents = [new FunctionCallContent("call_task_1", "task", new Dictionary<string, object?>
            {
                ["prompt"] = "spawn sub-agent"
            })]
        });
        hostedClient.Push(new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("sub-agent response")]
        });
        hostedClient.Complete();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WaitForCompletionStateAsync(chat, AgentChatCompletionState.Succeeded, cts.Token);

        Assert.Equal(AgentChatCompletionState.Succeeded, chat.CompletionState);
        Assert.DoesNotContain(
            chat.History,
            item => GetText(item.Contents).Contains("Requested function", StringComparison.Ordinal)
                 && GetText(item.Contents).Contains("not found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentChat_HostedSubAgent_ParentTaskInvocation_TaskToolCallAttributedToParentTranscriptOnly()
    {
        var hostedClient = new CopilotSubAgentChatClient();
        await using var chat = CreateHostedSubAgentChat(hostedClient);

        // Router-injected copy of the parent's task tool call in the child sink is
        // preserved as-is in the child history but must never trigger a
        // function-registry lookup (bug #1174). It is retained purely for
        // attribution.
        hostedClient.Push(new ChatResponseUpdate
        {
            Role = ChatRole.User,
            Contents = [new FunctionCallContent("call_task_1", "task", new Dictionary<string, object?>
            {
                ["prompt"] = "spawn"
            })]
        });
        hostedClient.Push(new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("child text")]
        });
        hostedClient.Complete();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WaitForCompletionStateAsync(chat, AgentChatCompletionState.Succeeded, cts.Token);

        Assert.Equal(AgentChatCompletionState.Succeeded, chat.CompletionState);
        // The injected FunctionCallContent(Name="task") is preserved somewhere in the child's history.
        Assert.Contains(
            chat.History.SelectMany(item => item.Contents).OfType<FunctionCallContent>(),
            fc => fc.Name == "task" && fc.CallId == "call_task_1");
        // No FunctionResultContent for the parent's task call is attributed to the child.
        Assert.DoesNotContain(
            chat.History.SelectMany(item => item.Contents).OfType<FunctionResultContent>(),
            fr => fr.CallId == "call_task_1");
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

    [Fact]
    public async Task RestoreSubAgentsAsync_ThenAcquireLeaseOnEveryStub_LeavesRunningSessionsEmptyOfChildren()
    {
        // Issue #1205: after a parent restore with N persisted children, the parent's AgentViewModel
        // eagerly acquires a lease on each restored SubAgent stub. Those lease acquisitions must NOT
        // add the children to IRunningAgentChatFactory.RunningSessions — otherwise they surface as
        // "No Open Tab" rows in the running-agents flyout after every restart (issue #1205).
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
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
              "tools": []
            }
            """);
        var parentSessionId = Guid.NewGuid().ToString("n");
        var childSessionIds = new[]
        {
            Guid.NewGuid().ToString("n"),
            Guid.NewGuid().ToString("n"),
            Guid.NewGuid().ToString("n"),
        };
        foreach (var child in childSessionIds)
        {
            await store.AddSubAgentLinkAsync(parentSessionId, child);
            // Persist a definition for each child so GetAsync can load it.
            await store.StoreAsync(new StoreRequestAgent
            {
                Agent = new PersistedAgent
                {
                    AgentSessionId = child,
                    AgentDefinitionJson = MongoDB.Bson.BsonDocument.Parse(parentDefinition.ToJson()),
                }
            });
        }
        await store.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = parentSessionId,
                AgentDefinitionJson = MongoDB.Bson.BsonDocument.Parse(parentDefinition.ToJson()),
            }
        });

        await using var parentLease = await factory.GetAsync(new AgentSessionId(parentSessionId));

        await WaitForSubAgentCountAsync(parentLease.AgentChat, childSessionIds.Length, CancellationToken.None);

        var leases = new List<RunningAgentChatLease>();
        try
        {
            foreach (var stub in parentLease.AgentChat.SubAgents.OfType<SubAgent>())
            {
                leases.Add(await stub.AcquireLeaseAsync());
            }

            // Parent registered as top-level; children must not have been added.
            Assert.Single(factory.RunningSessions);
            Assert.Equal(parentSessionId, factory.RunningSessions[0].SessionId.Value);
        }
        finally
        {
            foreach (var lease in leases)
            {
                await lease.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task RepeatedRestartRestore_DoesNotAccumulateRunningSessions()
    {
        // Issue #1205: two consecutive restore cycles with the same persisted parent + child
        // must leave RunningSessions.Count == 1 (no per-restart growth / leak).
        var store = new InMemoryAgentPersistenceStore();
        var parentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "parent-agent",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
              "tools": []
            }
            """);
        var parentSessionId = Guid.NewGuid().ToString("n");
        var childSessionId = Guid.NewGuid().ToString("n");
        await store.AddSubAgentLinkAsync(parentSessionId, childSessionId);
        await store.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = parentSessionId,
                AgentDefinitionJson = MongoDB.Bson.BsonDocument.Parse(parentDefinition.ToJson()),
            }
        });
        await store.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = childSessionId,
                AgentDefinitionJson = MongoDB.Bson.BsonDocument.Parse(parentDefinition.ToJson()),
            }
        });

        for (var cycle = 0; cycle < 2; cycle++)
        {
            var factory = new AgentChatFactory(
                store,
                new AgentServices { ChatClientOverride = new DeterministicTestChatClient() },
                TaskScheduler.Default);

            await using (var parentLease = await factory.GetAsync(new AgentSessionId(parentSessionId)))
            {
                await WaitForSubAgentCountAsync(parentLease.AgentChat, 1, CancellationToken.None);
                var stub = parentLease.AgentChat.SubAgents.OfType<SubAgent>().Single();
                await using var childLease = await stub.AcquireLeaseAsync();

                Assert.Single(factory.RunningSessions);
                Assert.Equal(parentSessionId, factory.RunningSessions[0].SessionId.Value);
            }

            await factory.DisposeAsync();
        }
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

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, bool registerAsRunningAgent = true, CancellationToken ct = default)
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

    // ─── Fix #1154 tests ─────────────────────────────────────────────────────────
    //
    // These tests close the live-incrementality regression gap left by #1139: they
    // assert on the child AgentChat's transcript (RunningItems + History) BEFORE
    // calling .Complete() on the scripted hosted client, proving that a running
    // sub-agent's assistant text and tool calls are visible while the stream is
    // still open — not batched at completion.

    private static string GetText(System.Collections.Generic.IEnumerable<AIContent> contents, bool includeReasoning)
    {
        var text = string.Empty;
        foreach (var content in contents)
        {
            if (content is TextContent tc)
            {
                text += tc.Text;
            }
            else if (includeReasoning && content is TextReasoningContent rc)
            {
                text += rc.Text;
            }
        }
        return text;
    }

    private static bool TranscriptContains(AgentChat chat, string expected)
    {
        foreach (var h in chat.History)
        {
            if (GetText(h.Contents, includeReasoning: true).Contains(expected))
            {
                return true;
            }
            foreach (var c in h.Contents)
            {
                if (c is FunctionCallContent fc && (fc.Name == expected || fc.CallId == expected))
                {
                    return true;
                }
            }
        }

        foreach (var r in chat.RunningItems)
        {
            foreach (var item in r.Items)
            {
                if (GetText(item.Contents, includeReasoning: true).Contains(expected))
                {
                    return true;
                }
                foreach (var c in item.Contents)
                {
                    if (c is FunctionCallContent fc && (fc.Name == expected || fc.CallId == expected))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static async Task WaitForRunningOrHistoryContainsAsync(
        AgentChat chat,
        string expected,
        CancellationToken cancellationToken)
    {
        var running = (System.Collections.Specialized.INotifyCollectionChanged)chat.RunningItems;
        var history = (System.Collections.Specialized.INotifyCollectionChanged)chat.History;
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var innerSubscribed = new List<AgentChatRunningItem>();

        void Check()
        {
            try
            {
                if (TranscriptContains(chat, expected))
                {
                    signal.TrySetResult();
                }
            }
            catch (System.InvalidOperationException)
            {
                // Collection mutated during enumeration — the change that mutated it will
                // fire another CollectionChanged event, so we will re-check then.
            }
        }

        void OnInner(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Check();

        void SubscribeInner(AgentChatRunningItem item)
        {
            ((System.Collections.Specialized.INotifyCollectionChanged)item.Items).CollectionChanged += OnInner;
            innerSubscribed.Add(item);
        }

        void OnRunning(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is not null)
            {
                foreach (AgentChatRunningItem it in e.NewItems)
                {
                    SubscribeInner(it);
                }
            }
            Check();
        }

        void OnHistory(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Check();

        running.CollectionChanged += OnRunning;
        history.CollectionChanged += OnHistory;
        try
        {
            foreach (var it in chat.RunningItems)
            {
                SubscribeInner(it);
            }
            Check();
            if (signal.Task.IsCompleted)
            {
                return;
            }

            await signal.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            running.CollectionChanged -= OnRunning;
            history.CollectionChanged -= OnHistory;
            foreach (var it in innerSubscribed)
            {
                ((System.Collections.Specialized.INotifyCollectionChanged)it.Items).CollectionChanged -= OnInner;
            }
        }
    }

    [Fact]
    public async Task AgentChat_HostedSubAgent_RunningStream_ContentVisibleBeforeCompletion()
    {
        // Fix #1154: after a started signal + one content event, the child AgentChat's
        // transcript (RunningItems/History) must contain the text WHILE the hosted stream
        // is still open (no .Complete() called) — proving live, not batched-at-completion.
        var (router, rootChannel, factory) = await F1139_CreateRouterAsync();
        try
        {
            await router.RouteAsync(F1139_LifecycleStartWithoutAgentId("call-1"));
            await router.RouteAsync(F1139_SubAgentText("child-runtime-id", "live sub-agent text"));

            var child = Assert.Single(factory.CreatedChildren);

            // Explicitly do NOT Complete() — the assertion must be true mid-run.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await WaitForRunningOrHistoryContainsAsync(child, "live sub-agent text", cts.Token);

            Assert.True(TranscriptContains(child, "live sub-agent text"));
        }
        finally
        {
            foreach (var hosted in factory.CreatedHostedClients)
            {
                hosted.Complete();
            }
            await router.DisposeRemainingLeasesAsync();
            rootChannel.Writer.TryComplete();
        }
    }

    [Fact]
    public async Task AgentChat_HostedSubAgent_IncrementalDeltas_EachVisibleAsPushed()
    {
        // Fix #1154: two successive assistant deltas each become observable in the child
        // transcript as they are pushed. We assert visibility of the first BEFORE pushing
        // the second (and both before any .Complete()), confirming per-event incremental
        // visibility — not a single end-of-stream flush.
        var (router, rootChannel, factory) = await F1139_CreateRouterAsync();
        try
        {
            await router.RouteAsync(F1139_LifecycleStartWithoutAgentId("call-1"));
            await router.RouteAsync(F1139_SubAgentText("child-runtime-id", "partial one"));

            var child = Assert.Single(factory.CreatedChildren);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await WaitForRunningOrHistoryContainsAsync(child, "partial one", cts.Token);
            Assert.True(TranscriptContains(child, "partial one"));
            Assert.False(TranscriptContains(child, "partial two"));

            await router.RouteAsync(F1139_SubAgentText("child-runtime-id", "partial two"));
            await WaitForRunningOrHistoryContainsAsync(child, "partial two", cts.Token);

            Assert.True(TranscriptContains(child, "partial one"));
            Assert.True(TranscriptContains(child, "partial two"));
        }
        finally
        {
            foreach (var hosted in factory.CreatedHostedClients)
            {
                hosted.Complete();
            }
            await router.DisposeRemainingLeasesAsync();
            rootChannel.Writer.TryComplete();
        }
    }

    [Fact]
    public async Task AgentChat_HostedSubAgent_ToolStart_VisibleBeforeCompletion()
    {
        // Fix #1154: a sub-agent tool-start event surfaces as a running tool-call item in
        // the child transcript BEFORE the stream completes — tool progress is live, not
        // coalesced to the end.
        var (router, rootChannel, factory) = await F1139_CreateRouterAsync();
        try
        {
            await router.RouteAsync(F1139_LifecycleStartWithoutAgentId("call-1"));
            await router.RouteAsync(F1139_SubAgentToolStart("child-runtime-id", "tool-call-42", "sample_tool"));

            var child = Assert.Single(factory.CreatedChildren);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await WaitForRunningOrHistoryContainsAsync(child, "sample_tool", cts.Token);

            Assert.True(TranscriptContains(child, "sample_tool"));
            Assert.True(TranscriptContains(child, "tool-call-42"));
        }
        finally
        {
            foreach (var hosted in factory.CreatedHostedClients)
            {
                hosted.Complete();
            }
            await router.DisposeRemainingLeasesAsync();
            rootChannel.Writer.TryComplete();
        }
    }

    [Fact]
    public async Task AgentChat_HostedSubAgent_RunningItem_RaisesCollectionChanged_PerEvent()
    {
        // Fix #1154: a CollectionChanged notification on the child's RunningItems (or a
        // running item's inner Items) fires as each event arrives mid-run, not once at
        // completion. We count notifications observed between successive pushes to prove
        // per-event live signalling.
        var (router, rootChannel, factory) = await F1139_CreateRouterAsync();
        try
        {
            await router.RouteAsync(F1139_LifecycleStartWithoutAgentId("call-1"));
            var child = factory.CreatedChildren.Count > 0
                ? factory.CreatedChildren[0]
                : throw new InvalidOperationException("child not created");

            var running = (System.Collections.Specialized.INotifyCollectionChanged)child.RunningItems;
            var innerSubscribed = new List<AgentChatRunningItem>();
            int notifications = 0;
            TaskCompletionSource? incrementSignal = null;

            // Fix #1406: signal each increment so the assertions can WAIT for the
            // per-event CollectionChanged notification instead of racing its dispatch.
            void SignalIncrement()
            {
                Interlocked.Increment(ref notifications);
                Volatile.Read(ref incrementSignal)?.TrySetResult();
            }

            // Fix #1406: block until the notification counter advances past `previous`
            // (or the shared ~10s cts fires, preserving the regression timeout). Uses a
            // TCS completed by the increment handlers — no polling — and re-checks the
            // counter after registering to avoid missing an increment.
            async Task WaitForNotificationAsync(int previous, CancellationToken ct)
            {
                while (Volatile.Read(ref notifications) <= previous)
                {
                    var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    Volatile.Write(ref incrementSignal, signal);
                    if (Volatile.Read(ref notifications) > previous)
                    {
                        break;
                    }

                    await signal.Task.WaitAsync(ct);
                }

                Volatile.Write(ref incrementSignal, null);
            }

            void OnInner(object? _, System.Collections.Specialized.NotifyCollectionChangedEventArgs __) =>
                SignalIncrement();

            void SubscribeInner(AgentChatRunningItem it)
            {
                ((System.Collections.Specialized.INotifyCollectionChanged)it.Items).CollectionChanged += OnInner;
                innerSubscribed.Add(it);
            }

            void OnRunning(object? _, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
            {
                if (e.NewItems is not null)
                {
                    foreach (AgentChatRunningItem it in e.NewItems)
                    {
                        SubscribeInner(it);
                    }
                }
                SignalIncrement();
            }

            // Fix #1406: count the History collection too — the wait predicate
            // (WaitForRunningOrHistoryContainsAsync) can be satisfied via History, so the
            // notification counter must observe the same collection to avoid diverging.
            var history = (System.Collections.Specialized.INotifyCollectionChanged)child.History;
            void OnHistory(object? _, System.Collections.Specialized.NotifyCollectionChangedEventArgs __) =>
                SignalIncrement();

            running.CollectionChanged += OnRunning;
            history.CollectionChanged += OnHistory;
            foreach (var it in child.RunningItems)
            {
                SubscribeInner(it);
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                await router.RouteAsync(F1139_SubAgentText("child-runtime-id", "delta-one"));
                await WaitForRunningOrHistoryContainsAsync(child, "delta-one", cts.Token);
                // Fix #1406: wait for the notification instead of sampling immediately,
                // which raced the asynchronous CollectionChanged dispatch. A timeout on the
                // shared ~10s cts surfaces as OperationCanceledException = "no notification".
                await WaitForNotificationAsync(0, cts.Token);
                var afterFirst = Volatile.Read(ref notifications);
                Assert.True(afterFirst > 0, "Expected at least one CollectionChanged notification after the first delta.");

                await router.RouteAsync(F1139_SubAgentText("child-runtime-id", "delta-two"));
                await WaitForRunningOrHistoryContainsAsync(child, "delta-two", cts.Token);
                await WaitForNotificationAsync(afterFirst, cts.Token);
                var afterSecond = Volatile.Read(ref notifications);
                Assert.True(afterSecond > afterFirst,
                    $"Expected additional CollectionChanged notifications after the second delta; before={afterFirst}, after={afterSecond}.");
            }
            finally
            {
                running.CollectionChanged -= OnRunning;
                history.CollectionChanged -= OnHistory;
                foreach (var it in innerSubscribed)
                {
                    ((System.Collections.Specialized.INotifyCollectionChanged)it.Items).CollectionChanged -= OnInner;
                }
            }
        }
        finally
        {
            foreach (var hosted in factory.CreatedHostedClients)
            {
                hosted.Complete();
            }
            await router.DisposeRemainingLeasesAsync();
            rootChannel.Writer.TryComplete();
        }
    }

    // ─── Fix #1193: parent interrupt terminalizes running sub-agents ─────────────
    //
    // Real router → real child AgentChat pipeline (via Fix1139TestFactory) exercised
    // against the router's TerminalizeRemainingChildrenAsync entry point that
    // CopilotSdkChatClient.AbortAndInvalidateSessionAsync now calls on parent interrupt.
    // These tests assert on the child AgentChat.CompletionState / CompletionStateChanged
    // — the exact signal the running-items UI listens on to clear the pulsating-brain
    // indicator.

    private static async Task WaitForCompletionStateEventAsync(
        AgentChat chat,
        AgentChatCompletionState expected,
        CancellationToken cancellationToken)
    {
        if (chat.CompletionState == expected)
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? _, EventArgs __)
        {
            if (chat.CompletionState == expected)
            {
                signal.TrySetResult();
            }
        }

        chat.CompletionStateChanged += OnChanged;
        try
        {
            if (chat.CompletionState == expected)
            {
                return;
            }

            await signal.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            chat.CompletionStateChanged -= OnChanged;
        }
    }

    [Fact]
    public async Task AgentChat_HostedSubAgent_ParentInterrupted_ChildCompletionStateSetToFailed()
    {
        // Fix #1193: cancelling the parent's turn while a hosted sub-agent is running
        // must eventually transition the child AgentChat.CompletionState to Failed. Before
        // the fix, the aborted SDK session never delivers SubagentCompleted/SubagentFailed
        // and nothing on the cancel path sweeps the router's ChildRoutingEntry map, so the
        // child stays Running forever and the running-items indicator never clears.
        var (router, rootChannel, factory) = await F1139_CreateRouterAsync();
        try
        {
            await router.RouteAsync(F1139_LifecycleStart("child-agent-1", "call-1"));
            var child = Assert.Single(factory.CreatedChildren);
            Assert.Equal(AgentChatCompletionState.Running, child.CompletionState);

            // Simulate the parent's cancel path: after AbortAsync/InvalidateCopilotSession,
            // AbortAndInvalidateSessionAsync now calls TerminalizeRemainingChildrenAsync.
            await router.TerminalizeRemainingChildrenAsync(
                new OperationCanceledException("Parent Copilot chat was interrupted."));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await WaitForCompletionStateEventAsync(child, AgentChatCompletionState.Failed, cts.Token);
            Assert.Equal(AgentChatCompletionState.Failed, child.CompletionState);
        }
        finally
        {
            foreach (var hosted in factory.CreatedHostedClients)
            {
                hosted.Complete();
            }
            await router.DisposeRemainingLeasesAsync();
            rootChannel.Writer.TryComplete();
        }
    }

    [Fact]
    public async Task AgentChat_HostedSubAgent_ParentInterruptedThenLateCompletion_ChildStaysTerminalAndNoDuplicateEvent()
    {
        // Fix #1193 idempotency: after the parent-interrupt sweep terminalizes the child,
        // a late-arriving SubagentCompleted (queued in the event channel before the abort)
        // must NOT re-raise CompletionStateChanged. SetCompletionState's equality guard
        // guarantees this even if the router lookup were to hit; the router's dictionary
        // clear ensures the lookup misses in the first place.
        var (router, rootChannel, factory) = await F1139_CreateRouterAsync();
        try
        {
            await router.RouteAsync(F1139_LifecycleStart("child-agent-1", "call-1"));
            var child = Assert.Single(factory.CreatedChildren);

            var terminalTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int eventCount = 0;
            void OnChanged(object? _, EventArgs __)
            {
                Interlocked.Increment(ref eventCount);
                if (child.CompletionState != AgentChatCompletionState.Running)
                {
                    terminalTcs.TrySetResult();
                }
            }
            child.CompletionStateChanged += OnChanged;
            try
            {
                await router.TerminalizeRemainingChildrenAsync(
                    new OperationCanceledException("Parent Copilot chat was interrupted."));

                await terminalTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(AgentChatCompletionState.Failed, child.CompletionState);
                var countAfterTerminalize = Volatile.Read(ref eventCount);
                Assert.Equal(1, countAfterTerminalize);

                // Late queued SubagentCompleted for the same child — must be a no-op.
                await router.RouteAsync(new ChatResponseUpdate
                {
                    Contents =
                    [
                        new FunctionResultContent("child-agent-1", """{"event":"completed"}""")
                        {
                            AdditionalProperties = new()
                            {
                                [CopilotSdkStreamAdapter.ContentTypePropertyName] = CopilotSdkStreamAdapter.SubAgentLifecycleContentType,
                            },
                        },
                    ],
                });

                // Let any spurious CompletionStateChanged posted through the scheduler run.
                await Task.Yield();
                await Task.Yield();

                Assert.Equal(AgentChatCompletionState.Failed, child.CompletionState);
                Assert.Equal(countAfterTerminalize, Volatile.Read(ref eventCount));
            }
            finally
            {
                child.CompletionStateChanged -= OnChanged;
            }
        }
        finally
        {
            foreach (var hosted in factory.CreatedHostedClients)
            {
                hosted.Complete();
            }
            await router.DisposeRemainingLeasesAsync();
            rootChannel.Writer.TryComplete();
        }
    }

    [Fact]
    public async Task AgentChat_HostedSubAgent_NormalCompletion_UnaffectedByInterruptPath()
    {
        // Fix #1193 regression guard: a hosted sub-agent that completes normally (no
        // parent interrupt, SubagentCompleted flows through RouteAsync) still ends in
        // Succeeded — the new terminalize-remaining code path does not fire when it
        // should not.
        var (router, rootChannel, factory) = await F1139_CreateRouterAsync();
        try
        {
            await router.RouteAsync(F1139_LifecycleStart("child-agent-1", "call-1"));
            var child = Assert.Single(factory.CreatedChildren);

            await router.RouteAsync(new ChatResponseUpdate
            {
                Contents =
                [
                    new FunctionResultContent("child-agent-1", """{"event":"completed"}""")
                    {
                        AdditionalProperties = new()
                        {
                            [CopilotSdkStreamAdapter.ContentTypePropertyName] = CopilotSdkStreamAdapter.SubAgentLifecycleContentType,
                        },
                    },
                ],
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await WaitForCompletionStateEventAsync(child, AgentChatCompletionState.Succeeded, cts.Token);
            Assert.Equal(AgentChatCompletionState.Succeeded, child.CompletionState);
        }
        finally
        {
            foreach (var hosted in factory.CreatedHostedClients)
            {
                hosted.Complete();
            }
            await router.DisposeRemainingLeasesAsync();
            rootChannel.Writer.TryComplete();
        }
    }
}
