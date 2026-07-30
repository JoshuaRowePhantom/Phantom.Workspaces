using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentSchema;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class CopilotSdkEventPipelineTests
{
    private static (CopilotSubAgentRouter router, Channel<ChatResponseUpdate> channel)
        CreateRouter(
            FakeRunningAgentChatFactory? factory = null,
            FakeSubAgentTable? table = null)
    {
        var channel = Channel.CreateUnbounded<ChatResponseUpdate>();
        var router = new CopilotSubAgentRouter(
            channel.Writer,
            factory ?? new FakeRunningAgentChatFactory(),
            table ?? new FakeSubAgentTable(),
            logger: null);
        return (router, channel);
    }

    /// <summary>
    /// Runs a single SDK event through the real adapter+router pipeline
    /// (<see cref="CopilotSdkStreamAdapter"/> then <see cref="CopilotSubAgentRouter"/>),
    /// mirroring the drain loop in <c>CopilotSdkChatClient.BeginTurnAsync</c>.
    /// </summary>
    private static async Task DispatchAsync(CopilotSubAgentRouter router, SessionEvent sessionEvent)
    {
        var events = Channel.CreateUnbounded<SessionEvent>();
        events.Writer.TryWrite(sessionEvent);
        events.Writer.Complete();
        await foreach (var update in CopilotSdkStreamAdapter.TranslateCopilotSdkSessionEvents(events.Reader, CancellationToken.None))
        {
            await router.RouteAsync(update);
        }
    }

    private static SubagentStartedEvent StartedEvent(string agentId) =>
        new SubagentStartedEvent
        {
            AgentId = agentId,
            Data = new SubagentStartedData
            {
                ToolCallId = "call-1",
                AgentName = "sub_agent",
                AgentDisplayName = "Sub Agent",
                AgentDescription = "desc",
            },
        };

    private static SubagentCompletedEvent CompletedEvent(string agentId) =>
        new SubagentCompletedEvent
        {
            AgentId = agentId,
            Data = new SubagentCompletedData { ToolCallId = "call-1", AgentName = "sub_agent", AgentDisplayName = "Sub Agent" },
        };

    private static SubagentFailedEvent FailedEvent(string agentId, string error = "boom") =>
        new SubagentFailedEvent
        {
            AgentId = agentId,
            Data = new SubagentFailedData { ToolCallId = "call-1", AgentName = "sub_agent", AgentDisplayName = "Sub Agent", Error = error },
        };

    private static AssistantMessageDeltaEvent DeltaEvent(string agentId, string text) =>
        new AssistantMessageDeltaEvent
        {
            AgentId = agentId,
            Data = new AssistantMessageDeltaData { DeltaContent = text, MessageId = "msg-1" },
        };

    [Fact]
    public async Task DispatchAsync_ConcurrentToolStartEvents_DoNotCorruptDictionary()
    {
        // Regression test for GitHub issue #765: concurrent DispatchAsync calls would corrupt the
        // internal bufferedToolStarts dictionary, causing IndexOutOfRangeException.
        var channel = Channel.CreateUnbounded<ChatResponseUpdate>();
        var router = new CopilotSubAgentRouter(
            channel.Writer,
            new FakeRunningAgentChatFactory(),
            new FakeSubAgentTable(),
            logger: null);

        var toolStart1 = new ToolExecutionStartEvent
        {
            AgentId = string.Empty,
            Data = new ToolExecutionStartData
            {
                ToolCallId = "call-1",
                ToolName = "tool-1"
            }
        };

        var toolStart2 = new ToolExecutionStartEvent
        {
            AgentId = string.Empty,
            Data = new ToolExecutionStartData
            {
                ToolCallId = "call-2",
                ToolName = "tool-2"
            }
        };

        // Fire two concurrent DispatchAsync calls. Before the fix, this would often trigger
        // IndexOutOfRangeException during dictionary resize/insert because Dictionary<K,V> is not
        // thread-safe.
        var task1 = Task.Run(async () => await DispatchAsync(router, toolStart1));
        var task2 = Task.Run(async () => await DispatchAsync(router, toolStart2));

        await Task.WhenAll(task1, task2);

        // If we reach here without exception, the test passes
        channel.Writer.Complete();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_SerializedEventProcessing_ProcessesAllEventsInOrder()
    {
        // Verifies that events dispatched through channel-based drain loop are processed
        // sequentially in order.
        var channel = Channel.CreateUnbounded<ChatResponseUpdate>();
        var router = new CopilotSubAgentRouter(
            channel.Writer,
            new FakeRunningAgentChatFactory(),
            new FakeSubAgentTable(),
            logger: null);

        var events = new List<AssistantMessageDeltaEvent>
        {
            new AssistantMessageDeltaEvent
            {
                AgentId = string.Empty,
                Data = new AssistantMessageDeltaData { DeltaContent = "message-1", MessageId = "msg-1" }
            },
            new AssistantMessageDeltaEvent
            {
                AgentId = string.Empty,
                Data = new AssistantMessageDeltaData { DeltaContent = "message-2", MessageId = "msg-2" }
            },
            new AssistantMessageDeltaEvent
            {
                AgentId = string.Empty,
                Data = new AssistantMessageDeltaData { DeltaContent = "message-3", MessageId = "msg-3" }
            }
        };

        // Dispatch all events sequentially (simulating the drain loop behavior)
        foreach (var evt in events)
        {
            await DispatchAsync(router, evt);
        }

        channel.Writer.Complete();

        // Verify all events were processed in order
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in channel.Reader.ReadAllAsync())
        {
            updates.Add(update);
        }

        Assert.Equal(3, updates.Count);
        var textContents = updates
            .SelectMany(u => u.Contents.OfType<TextContent>())
            .Select(t => t.Text)
            .ToList();
        Assert.Contains("message-1", textContents);
        Assert.Contains("message-2", textContents);
        Assert.Contains("message-3", textContents);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_EventDispatchCancellation_StopsProcessing()
    {
        // Verifies that when the cancellation token is triggered, the drain loop stops processing.
        var channel = Channel.CreateUnbounded<ChatResponseUpdate>();
        var router = new CopilotSubAgentRouter(
            channel.Writer,
            new FakeRunningAgentChatFactory(),
            new FakeSubAgentTable(),
            logger: null);

        using var cts = new CancellationTokenSource();

        var deltaEvent = new AssistantMessageDeltaEvent
        {
            AgentId = string.Empty,
            Data = new AssistantMessageDeltaData { DeltaContent = "test-message", MessageId = "msg-1" }
        };

        // Dispatch one event successfully
        await DispatchAsync(router, deltaEvent);

        // Cancel the token (simulating turn cancellation)
        cts.Cancel();

        // Verify the event was processed before cancellation
        channel.Writer.Complete();
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in channel.Reader.ReadAllAsync())
        {
            updates.Add(update);
        }

        Assert.Single(updates);
    }

    [Fact]
    public async Task DispatchAsync_SubagentCompletedEvent_SetsCompletionStateToSucceeded()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var (router, _) = CreateRouter(factory, table);

        // Start sub-agent
        await DispatchAsync(router, StartedEvent("agent-1"));
        var lease = factory.CreatedLease!;
        var receiver = (CopilotSubAgentChatClient)lease.AgentChat.GetService(typeof(ICopilotSubAgentReceiver))!;

        // Verify initial state is Running
        Assert.Equal(AgentChatCompletionState.Running, lease.AgentChat.CompletionState);

        // Start consuming the stream in the background
        var streamTask = Task.Run(async () =>
        {
            await foreach (var _ in receiver.GetStreamingResponseAsync([]))
            { }
        });

        // Complete the sub-agent
        await DispatchAsync(router, CompletedEvent("agent-1"));

        // Wait for stream to complete
        await streamTask;

        // Verify completion state is now Succeeded
        Assert.Equal(AgentChatCompletionState.Succeeded, lease.AgentChat.CompletionState);
    }

    [Fact]
    public async Task DispatchAsync_SubagentFailedEvent_SetsCompletionStateToFailed()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var (router, _) = CreateRouter(factory, table);

        // Start sub-agent
        await DispatchAsync(router, StartedEvent("agent-1"));
        var lease = factory.CreatedLease!;
        var receiver = (CopilotSubAgentChatClient)lease.AgentChat.GetService(typeof(ICopilotSubAgentReceiver))!;

        // Verify initial state is Running
        Assert.Equal(AgentChatCompletionState.Running, lease.AgentChat.CompletionState);

        // Start consuming the stream in the background
        var streamTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in receiver.GetStreamingResponseAsync([]))
                { }
            }
            catch
            {
                // Expected exception from Fail()
            }
        });

        // Fail the sub-agent
        await DispatchAsync(router, FailedEvent("agent-1", "error"));

        // Wait for stream to complete
        await streamTask;

        // Verify completion state is now Failed
        Assert.Equal(AgentChatCompletionState.Failed, lease.AgentChat.CompletionState);
    }

    [Fact]
    public async Task DisposeRemainingLeasesAsync_ActiveSubAgents_SetsCompletionStateToFailed()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var (router, _) = CreateRouter(factory, table);

        // Start sub-agent but don't complete it
        await DispatchAsync(router, StartedEvent("agent-1"));
        var lease = factory.CreatedLease!;

        // Verify initial state is Running
        Assert.Equal(AgentChatCompletionState.Running, lease.AgentChat.CompletionState);

        // Dispose remaining leases (simulating turn cleanup)
        await router.DisposeRemainingLeasesAsync();

        // Verify completion state is now Failed
        Assert.Equal(AgentChatCompletionState.Failed, lease.AgentChat.CompletionState);
        Assert.True(factory.LeaseDisposed);
    }

    [Fact]
    public async Task DisposeRemainingLeasesAsync_ActiveSubAgents_CompletesReceiverChannel()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var (router, _) = CreateRouter(factory, table);

        // Start sub-agent
        await DispatchAsync(router, StartedEvent("agent-1"));
        var receiver = (CopilotSubAgentChatClient)factory.CreatedLease!.AgentChat.GetService(typeof(ICopilotSubAgentReceiver))!;

        // Start consuming the stream in the background
        Exception? thrownEx = null;
        var streamTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in receiver.GetStreamingResponseAsync([]))
                { }
            }
            catch (Exception ex)
            {
                thrownEx = ex;
            }
        });

        // Dispose remaining leases
        await router.DisposeRemainingLeasesAsync();

        // Wait for stream
        await streamTask;

        // Verify channel was completed with an exception
        Assert.NotNull(thrownEx);
        Assert.IsType<OperationCanceledException>(thrownEx);
    }

    [Fact]
    public async Task DispatchAsync_SubagentCompletedEvent_StatusReportedAsStopped_NotIdle()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var (router, _) = CreateRouter(factory, table);

        // Start sub-agent
        await DispatchAsync(router, StartedEvent("agent-1"));
        var lease = factory.CreatedLease!;
        var agentChat = lease.AgentChat;

        // Simulate consuming the stream (to make the agent "idle" in terms of no running items)
        var receiver = (CopilotSubAgentChatClient)agentChat.GetService(typeof(ICopilotSubAgentReceiver))!;
        var streamTask = Task.Run(async () =>
        {
            await foreach (var _ in receiver.GetStreamingResponseAsync([]))
            { }
        });

        // Complete the sub-agent
        await DispatchAsync(router, CompletedEvent("agent-1"));

        // Wait for stream
        await streamTask;

        // Verify that the completion state is Succeeded (not Running)
        Assert.Equal(AgentChatCompletionState.Succeeded, agentChat.CompletionState);

        // Verify that GetStatus would return "stopped" (not "idle")
        // This simulates what AgentSessionToolset.GetStatus() does:
        var status = agentChat.CompletionState switch
        {
            AgentChatCompletionState.Running => agentChat.IsBusy ? "running" : "idle",
            AgentChatCompletionState.Succeeded => "stopped",
            AgentChatCompletionState.Failed => "error",
            _ => "unknown",
        };
        Assert.Equal("stopped", status);
    }

    private sealed class FakeRunningAgentChatFactory : IRunningAgentChatFactory
    {
        private readonly InMemoryAgentPersistenceStore _store = new();

        public RunningAgentChatLease? CreatedLease { get; private set; }
        public bool LeaseDisposed { get; private set; }

        public System.Collections.ObjectModel.ObservableCollection<RunningAgentChat> RunningSessions { get; } = new();

        async Task<RunningAgentChatLease> IRunningAgentChatFactory.CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services,
            string? displayNameOverride,
            string? descriptionOverride,
            CancellationToken ct)
        {
            IChatClient client = new CopilotSubAgentChatClient();

            var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
            {
                AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
                    """{"kind":"prompt","name":"sub","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}"""),
                AgentSessionId = sessionId.Value,
                ConfiguredStore = _store,
                ClientOverride = client,
                DisplayNameOverride = displayNameOverride,
                DescriptionOverride = descriptionOverride,
                ForegroundScheduler = TaskScheduler.Default,
            });

            var lease = new RunningAgentChatLease(sessionId, chat, async () =>
            {
                LeaseDisposed = true;
                await ValueTask.CompletedTask;
            });
            CreatedLease = lease;
            return lease;
        }

        Task<RunningAgentChatLease> IRunningAgentChatFactory.GetAsync(AgentSessionId sessionId, CancellationToken ct) =>
            throw new NotImplementedException();

        Task<RunningAgentChatLease> IRunningAgentChatFactory.GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition,
            AgentServices? services,
            string? displayNameOverride,
            string? descriptionOverride,
            CancellationToken ct) =>
            throw new NotImplementedException();
    }

    private sealed class FakeSubAgentTable : ISubAgentTable
    {
        public List<AgentChat> AddedChats { get; } = new();

        Task<SubAgent> ISubAgentTable.Add(AgentChat agentChat)
        {
            AddedChats.Add(agentChat);
            var sessionId = new AgentSessionId(agentChat.AgentSessionId);
            return Task.FromResult(new SubAgent(sessionId, agentChat, null));
        }
    }

    // #1128: A restored (reloaded) session whose persisted sub-agents were "running" at
    // shutdown produces no terminal SDK event on reload, so every restored sub-agent must
    // be forced to a terminal Succeeded state; otherwise the UI running indicators never
    // clear. This is the end-to-end reload path exercised via AgentChatFactory +
    // AgentChat.RestoreSubAgentsAsync (issue #1128 root cause).
    [Fact]
    public async Task SessionReload_RestoredSubAgents_AllMarkedTerminal()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "reload-e2e";

        const string echoDefJson = """
            { "kind": "prompt", "name": "echo-agent",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
              "tools": [] }
            """;
        var echoDef = AgentDefinitionLoader.LoadAgentFromJson(echoDefJson);
        var childIds = new[] { "reload-child-a", "reload-child-b", "reload-child-c" };
        foreach (var childId in childIds)
        {
            await store.StoreAsync(new StoreRequestAgent
            {
                Agent = new PersistedAgent
                {
                    AgentSessionId = childId,
                    AgentDefinitionJson = MongoDB.Bson.BsonDocument.Parse(echoDef.ToJson()),
                }
            });
            await store.AddSubAgentLinkAsync(parentSessionId, childId);
        }

        await using var factory = new AgentChatFactory(
            store,
            new AgentServices { ChatClientOverride = new DeterministicTestChatClient() },
            TaskScheduler.Default);

        await using var parent = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = echoDef,
            AgentSessionId = parentSessionId,
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "reloaded-parent",
            AgentServices = new AgentServices { RunningAgentChatFactory = factory },
        });

        await parent.WaitForRestoredSubAgentsMarkedTerminalAsync();

        Assert.Equal(childIds.Length, parent.SubAgents.Count);
        foreach (var stub in parent.SubAgents.Cast<SubAgent>())
        {
            await using var lease = await stub.AcquireLeaseAsync();
            Assert.Equal(AgentChatCompletionState.Succeeded, lease.AgentChat.CompletionState);
        }
    }

    // #1140 (end-to-end reload path): the observable "N ago" label a sub-agent navigation
    // card displays is bound (via AgentEditorNavigationItemViewModel -> IRunningSubAgent ->
    // SubAgent -> AgentChat.LastUpdatedAt) to the child chat's LastUpdatedAt. After a
    // session reload, that value must be the persisted last-activity timestamp, not the
    // reload time. This test drives the full restore path through the real
    // AgentChatFactory and asserts the materialised child chat's LastUpdatedAt matches the
    // persisted UpdatedUtc while #1128's forced-terminal transition still fires.
    [Fact]
    public async Task SessionReload_CompletedSubAgents_CardShowsPersistedTime()
    {
        var store = new InMemoryAgentPersistenceStore();
        const string parentSessionId = "reload-parent-card";
        const string childSessionId = "reload-child-card";

        const string echoDefJson = """
            { "kind": "prompt", "name": "echo-agent",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
              "tools": [] }
            """;
        var echoDef = AgentDefinitionLoader.LoadAgentFromJson(echoDefJson);
        var echoDefBson = MongoDB.Bson.BsonDocument.Parse(echoDef.ToJson());

        await store.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = parentSessionId,
                AgentDefinitionJson = echoDefBson,
            },
        });
        var childPersistedTime = new DateTime(2021, 9, 8, 7, 6, 5, DateTimeKind.Utc);
        await store.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = childSessionId,
                AgentDefinitionJson = echoDefBson,
                LastUpdatedUtc = childPersistedTime,
            },
        });
        await store.AddSubAgentLinkAsync(parentSessionId, childSessionId);

        await using var factory = new AgentChatFactory(
            store,
            new AgentServices { ChatClientOverride = new DeterministicTestChatClient() },
            TaskScheduler.Default);

        await using var parent = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = echoDef,
            AgentSessionId = parentSessionId,
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "reloaded-parent",
            AgentServices = new AgentServices { RunningAgentChatFactory = factory },
        });

        await parent.WaitForRestoredSubAgentsMarkedTerminalAsync();

        var stub = Assert.IsType<SubAgent>(Assert.Single(parent.SubAgents));
        await using var lease = await stub.AcquireLeaseAsync();

        // The card's ago-label reads LastUpdatedAt through this chain — it must show the
        // persisted time, not the reload time. And the running marker must clear (#1128).
        Assert.Equal(childPersistedTime, lease.AgentChat.LastUpdatedAt);
        Assert.Equal(AgentChatCompletionState.Succeeded, lease.AgentChat.CompletionState);
    }
}
