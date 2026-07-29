using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using GitHub.Copilot.SDK;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Tests for the factory-path sub-agent wiring in <see cref="CopilotSubAgentRouter"/>:
/// uses <see cref="IRunningAgentChatFactory"/> + <see cref="ISubAgentTable"/> to create and route
/// sub-agent events through <see cref="ICopilotSubAgentReceiver"/>.
/// </summary>
public sealed class CopilotSdkChatClientSubAgentFactoryTests
{
    // ─── helpers ────────────────────────────────────────────────────────────────

    private static (CopilotSubAgentRouter router, System.Threading.Channels.Channel<ChatResponseUpdate> channel)
        CreateRouter(
            FakeRunningAgentChatFactory? factory = null,
            FakeSubAgentTable? table = null,
            FakeLogger? logger = null)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<ChatResponseUpdate>();
        var router = new CopilotSubAgentRouter(
            channel.Writer,
            factory ?? new FakeRunningAgentChatFactory(),
            table ?? new FakeSubAgentTable(),
            logger);
        return (router, channel);
    }

    /// <summary>
    /// Runs a single SDK event through the real adapter+router pipeline
    /// (<see cref="CopilotSdkStreamAdapter"/> then <see cref="CopilotSubAgentRouter"/>),
    /// mirroring the drain loop in <c>CopilotSdkChatClient.BeginTurnAsync</c>.
    /// </summary>
    private static async Task DispatchAsync(CopilotSubAgentRouter router, SessionEvent sessionEvent)
    {
        var events = System.Threading.Channels.Channel.CreateUnbounded<SessionEvent>();
        events.Writer.TryWrite(sessionEvent);
        events.Writer.Complete();
        await foreach (var update in CopilotSdkStreamAdapter.TranslateCopilotSdkSessionEvents(events.Reader, CancellationToken.None))
        {
            await router.RouteAsync(update);
        }
    }

    private static SubagentStartedEvent StartedEvent(string agentId, string toolCallId = "call-1") =>
        new SubagentStartedEvent
        {
            AgentId = agentId,
            Data = new SubagentStartedData
            {
                ToolCallId = toolCallId,
                AgentName = "sub_agent",
                AgentDisplayName = "Sub Agent",
                AgentDescription = "desc",
            },
        };

    private static SubagentCompletedEvent CompletedEvent(string agentId, string toolCallId = "call-1") =>
        new SubagentCompletedEvent
        {
            AgentId = agentId,
            Data = new SubagentCompletedData { ToolCallId = toolCallId, AgentName = "sub_agent", AgentDisplayName = "Sub Agent" },
        };

    private static SubagentFailedEvent FailedEvent(string agentId, string error = "boom", string toolCallId = "call-1") =>
        new SubagentFailedEvent
        {
            AgentId = agentId,
            Data = new SubagentFailedData { ToolCallId = toolCallId, AgentName = "sub_agent", AgentDisplayName = "Sub Agent", Error = error },
        };

    private static AssistantMessageDeltaEvent DeltaEvent(string agentId, string text) =>
        new AssistantMessageDeltaEvent
        {
            AgentId = agentId,
            Data = new AssistantMessageDeltaData { DeltaContent = text, MessageId = "msg-1" },
        };

    private static ToolExecutionStartEvent ToolStartEvent(string agentId, string toolCallId) =>
        new ToolExecutionStartEvent
        {
            AgentId = agentId,
            Data = new ToolExecutionStartData { ToolCallId = toolCallId, ToolName = "do_work" },
        };

    // ─── tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SubAgentStarted_CallsFactoryCreateAsync_WithGithubCopilotSubagent()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var (router, _) = CreateRouter(factory, table);

        await DispatchAsync(router, StartedEvent("agent-1"));

        Assert.Single(factory.CreateCalls);
        var (definition, _) = factory.CreateCalls[0];
        Assert.Contains("github-copilot-subagent", definition.ToJson());
    }

    [Fact]
    public async Task SubAgentStarted_CallsSubAgentTableAdd_WithCreatedAgentChat()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var (router, _) = CreateRouter(factory, table);

        await DispatchAsync(router, StartedEvent("agent-1"));

        Assert.Single(table.AddedChats);
        Assert.Same(factory.CreatedLease!.AgentChat, table.AddedChats[0]);
    }

    [Fact]
    public async Task SubAgentStarted_AcquiresReceiverFromSubAgentChat()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var (router, _) = CreateRouter(factory, table);

        await DispatchAsync(router, StartedEvent("agent-1"));

        // Verify the factory created the chat and the receiver was extracted
        Assert.NotNull(factory.CreatedLease);
        var receiver = factory.CreatedLease.AgentChat.GetService(typeof(ICopilotSubAgentReceiver)) as ICopilotSubAgentReceiver;
        Assert.NotNull(receiver);
    }

    [Fact]
    public async Task SubAgentEvent_ForwardedToReceiver_Push()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var (router, _) = CreateRouter(factory, table);

        await DispatchAsync(router, StartedEvent("agent-1"));
        await DispatchAsync(router, DeltaEvent("agent-1", "hello from sub-agent"));

        var receiver = factory.CreatedReceiver!;
        // Drain the receiver
        receiver.Complete();
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in receiver.GetStreamingResponseAsync([]))
            updates.Add(u);

        Assert.Contains(updates, u => u.Text == "hello from sub-agent");
    }

    [Fact]
    public async Task SubAgentCompleted_ForwardedToReceiver_Complete()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var (router, _) = CreateRouter(factory, table);

        await DispatchAsync(router, StartedEvent("agent-1"));
        await DispatchAsync(router, CompletedEvent("agent-1"));

        var receiver = (CopilotSubAgentChatClient)factory.CreatedLease!.AgentChat.GetService(typeof(ICopilotSubAgentReceiver))!;
        // After Complete, reading the channel should finish with no items
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in receiver.GetStreamingResponseAsync([]))
            updates.Add(u);

        Assert.Empty(updates);
    }

    [Fact]
    public async Task SubAgentFailed_ForwardedToReceiver_Fail()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var (router, _) = CreateRouter(factory, table);

        await DispatchAsync(router, StartedEvent("agent-1"));
        await DispatchAsync(router, FailedEvent("agent-1", "test error"));

        var receiver = (CopilotSubAgentChatClient)factory.CreatedLease!.AgentChat.GetService(typeof(ICopilotSubAgentReceiver))!;
        // After Fail, reading should throw
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in receiver.GetStreamingResponseAsync([]))
            { }
        });
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task SubAgentEvent_UnknownSubAgentId_BufferedThenFlushedOnLateStart_NeverParent()
    {
        // Fix #1109/#1110: when a delta arrives for an agentId that has no lifecycle start yet,
        // the router MUST buffer it in a per-child sink (never fall back to the parent transcript).
        // When the start eventually arrives, the buffered updates are flushed in order to the
        // real receiver.
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var (router, rootChannel) = CreateRouter(factory, table);

        // Delta arrives BEFORE start.
        await DispatchAsync(router, DeltaEvent("late-agent", "early buffered"));

        // Fix #1110 inverse-face: parent transcript received nothing.
        rootChannel.Writer.Complete();
        var rootUpdates = new List<ChatResponseUpdate>();
        await foreach (var u in rootChannel.Reader.ReadAllAsync())
            rootUpdates.Add(u);
        Assert.Empty(rootUpdates);

        // Now the start arrives — the receiver should get the previously buffered update.
        await DispatchAsync(router, StartedEvent("late-agent"));

        var receiver = factory.CreatedReceiver!;
        receiver.Complete();
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in receiver.GetStreamingResponseAsync([]))
            updates.Add(u);

        Assert.Contains(updates, u => u.Text == "early buffered");
    }

    [Fact]
    public async Task SubAgentCompleted_UnknownSubAgentId_LoggedAndIgnored()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var logger = new FakeLogger();
        var (router, _) = CreateRouter(factory, table, logger);

        // No SubAgentStarted — dispatch completed for unknown ID
        await DispatchAsync(router, CompletedEvent("unknown-agent"));

        Assert.Contains(logger.Logs, l => l.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task SubAgentFailed_UnknownSubAgentId_LoggedAndIgnored()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var logger = new FakeLogger();
        var (router, _) = CreateRouter(factory, table, logger);

        // No SubAgentStarted — dispatch failed for unknown ID
        await DispatchAsync(router, FailedEvent("unknown-agent"));

        Assert.Contains(logger.Logs, l => l.Level == LogLevel.Warning);
    }

    [Fact]
    public void SubAgentRouter_ConstructedWithoutFactory_Throws()
    {
        // Fix #1109: factory is a required construction argument — the router no longer supports
        // a null-fallback path that could silently misroute sub-agent output.
        var channel = System.Threading.Channels.Channel.CreateUnbounded<ChatResponseUpdate>();
        var table = new FakeSubAgentTable();
        Assert.Throws<ArgumentNullException>(() =>
            new CopilotSubAgentRouter(channel.Writer, factory: null!, subAgentTable: table));
    }

    [Fact]
    public void SubAgentRouter_ConstructedWithoutSubAgentTable_Throws()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<ChatResponseUpdate>();
        var factory = new FakeRunningAgentChatFactory();
        Assert.Throws<ArgumentNullException>(() =>
            new CopilotSubAgentRouter(channel.Writer, factory, subAgentTable: null!));
    }

    [Fact]
    public async Task SubAgentDelta_ForRegisteredChild_RoutedToChild_NeverParent()
    {
        // Fix #1110 inverse-face: sub-agent output never leaks into the parent transcript.
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var (router, rootChannel) = CreateRouter(factory, table);

        await DispatchAsync(router, StartedEvent("agent-x"));
        await DispatchAsync(router, DeltaEvent("agent-x", "child-only text"));

        rootChannel.Writer.Complete();
        var rootUpdates = new List<ChatResponseUpdate>();
        await foreach (var u in rootChannel.Reader.ReadAllAsync())
            rootUpdates.Add(u);

        Assert.Empty(rootUpdates);
    }

    [Fact]
    public async Task SubAgentDelta_ForRegisteredChild_RoutedToChild_NeverRootStream()
    {
        // Fix #1110: named per the issue's Expected Tests table. Exercises the same
        // invariant as the sibling _NeverParent test but with an explicit assertion that
        // BOTH the child receiver saw the delta AND the root/parent channel did not.
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var (router, rootChannel) = CreateRouter(factory, table);

        await DispatchAsync(router, StartedEvent("agent-x"));
        await DispatchAsync(router, DeltaEvent("agent-x", "child-only text"));
        await DispatchAsync(router, CompletedEvent("agent-x"));

        var receiver = (CopilotSubAgentChatClient)factory.CreatedLease!.AgentChat.GetService(typeof(ICopilotSubAgentReceiver))!;
        var childUpdates = new List<ChatResponseUpdate>();
        await foreach (var u in receiver.GetStreamingResponseAsync([]))
            childUpdates.Add(u);

        rootChannel.Writer.Complete();
        var rootUpdates = new List<ChatResponseUpdate>();
        await foreach (var u in rootChannel.Reader.ReadAllAsync())
            rootUpdates.Add(u);

        Assert.Contains(childUpdates, u => u.Text == "child-only text");
        Assert.DoesNotContain(rootUpdates, u => u.Text == "child-only text");
    }

    [Fact]
    public async Task ParentTurnDelta_WhileSubAgentActive_NotRoutedToChildSink()
    {
        // Fix #1110 inverse-inverse: while a sub-agent is active, root-level (untagged)
        // parent-turn deltas must still reach the parent's root stream and must NOT be
        // duplicated into the active child's receiver.
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var (router, rootChannel) = CreateRouter(factory, table);

        await DispatchAsync(router, StartedEvent("agent-x"));
        await DispatchAsync(router, DeltaEvent(agentId: null!, "parent-turn text"));
        await DispatchAsync(router, CompletedEvent("agent-x"));

        var receiver = (CopilotSubAgentChatClient)factory.CreatedLease!.AgentChat.GetService(typeof(ICopilotSubAgentReceiver))!;
        var childUpdates = new List<ChatResponseUpdate>();
        await foreach (var u in receiver.GetStreamingResponseAsync([]))
            childUpdates.Add(u);

        rootChannel.Writer.Complete();
        var rootUpdates = new List<ChatResponseUpdate>();
        await foreach (var u in rootChannel.Reader.ReadAllAsync())
            rootUpdates.Add(u);

        Assert.Contains(rootUpdates, u => u.Text == "parent-turn text");
        Assert.DoesNotContain(childUpdates, u => u.Text == "parent-turn text");
    }

    [Fact]
    public async Task RootDelta_WithNoActiveSubAgent_WrittenToRootStream()
    {
        // Fix #1109: replacement for the deleted
        // AssistantMessageDeltaEvent_NullAgentId_WrittenToRootStream in the old registry-path
        // test file. Root-level (null/empty agentId) deltas still land on the parent channel.
        var (router, rootChannel) = CreateRouter();

        await DispatchAsync(router, DeltaEvent(agentId: null!, "root text"));

        rootChannel.Writer.Complete();
        var rootUpdates = new List<ChatResponseUpdate>();
        await foreach (var u in rootChannel.Reader.ReadAllAsync())
            rootUpdates.Add(u);

        Assert.Contains(rootUpdates, u => u.Text == "root text");
    }

    [Fact]
    public async Task SubAgentStarted_ReceiverNotExposedBySubAgentChat_Throws()
    {
        var factory = new FakeRunningAgentChatFactory(exposeReceiver: false);
        var table = new FakeSubAgentTable();
        var (router, _) = CreateRouter(factory, table);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await DispatchAsync(router, StartedEvent("agent-1")));
    }

    [Fact]
    public async Task SubAgentStarted_MultipleSubAgents_EachRoutedToCorrectReceiver()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var (router, _) = CreateRouter(factory, table);

        await DispatchAsync(router, StartedEvent("agent-1", "call-1"));
        var receiver1 = (CopilotSubAgentChatClient)factory.CreatedLease!.AgentChat.GetService(typeof(ICopilotSubAgentReceiver))!;

        factory.ResetLease();

        await DispatchAsync(router, StartedEvent("agent-2", "call-2"));
        var receiver2 = (CopilotSubAgentChatClient)factory.CreatedLease!.AgentChat.GetService(typeof(ICopilotSubAgentReceiver))!;

        Assert.NotSame(receiver1, receiver2);

        // Route events to agent-1 and agent-2 separately
        await DispatchAsync(router, DeltaEvent("agent-1", "msg-for-1"));
        await DispatchAsync(router, DeltaEvent("agent-2", "msg-for-2"));

        // Complete both
        await DispatchAsync(router, CompletedEvent("agent-1", "call-1"));
        await DispatchAsync(router, CompletedEvent("agent-2", "call-2"));

        var updates1 = new List<ChatResponseUpdate>();
        await foreach (var u in receiver1.GetStreamingResponseAsync([]))
            updates1.Add(u);

        var updates2 = new List<ChatResponseUpdate>();
        await foreach (var u in receiver2.GetStreamingResponseAsync([]))
            updates2.Add(u);

        Assert.Contains(updates1, u => u.Text == "msg-for-1");
        Assert.DoesNotContain(updates1, u => u.Text == "msg-for-2");

        Assert.Contains(updates2, u => u.Text == "msg-for-2");
        Assert.DoesNotContain(updates2, u => u.Text == "msg-for-1");
    }

    // ─── fakes ──────────────────────────────────────────────────────────────────

    private sealed class FakeRunningAgentChatFactory : IRunningAgentChatFactory
    {
        private readonly bool _exposeReceiver;

        public List<(AgentDefinition Definition, AgentSessionId SessionId)> CreateCalls { get; } = new();
        public RunningAgentChatLease? CreatedLease { get; private set; }
        public CopilotSubAgentChatClient? CreatedReceiver { get; private set; }

        public System.Collections.ObjectModel.ObservableCollection<RunningAgentChat> RunningSessions { get; } = new();

        public FakeRunningAgentChatFactory(bool exposeReceiver = true)
        {
            _exposeReceiver = exposeReceiver;
        }

        public void ResetLease()
        {
            CreatedLease = null;
            CreatedReceiver = null;
        }

        Task<RunningAgentChatLease> IRunningAgentChatFactory.CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services,
            string? displayNameOverride,
            string? descriptionOverride,
            CancellationToken ct)
        {
            CreateCalls.Add((definition, sessionId));

            IChatClient client;
            if (_exposeReceiver)
            {
                var receiver = new CopilotSubAgentChatClient();
                CreatedReceiver = receiver;
                client = receiver;
            }
            else
            {
                client = new NonReceiverChatClient();
            }

            // Create AgentChat using the internal constructor, skipping CreateAsync/InitializeAsync
            // to avoid starting the background processing loop that would consume from the channel.
            var chat = new AgentChat(new InternalCreateAgentChatRequest
            {
                AgentDefinition = null,
                ConfiguredStore = new InMemoryAgentPersistenceStore(),
                DisplayNameOverride = displayNameOverride,
                DescriptionOverride = descriptionOverride,
            });

            // Create a real ChatClientAgent and inject it via reflection.
            // ChatClientAgent itself doesn't start background tasks - those are in AgentChat.
            // Use UseProvidedChatClientAsIs = true so ChatClientAgent does not wrap the client
            // with WithDefaultAgentMiddleware. That wrapping otherwise inserts middleware that
            // (a) may initialize state lazily on first GetService call and (b) is not contractually
            // required to forward GetService for arbitrary service types. Keeping the client
            // unwrapped guarantees GetService(typeof(ICopilotSubAgentReceiver)) always returns
            // the exact CopilotSubAgentChatClient instance the router pushes updates to.
            var chatClientAgent = new ChatClientAgent(client, new ChatClientAgentOptions
            {
                UseProvidedChatClientAsIs = true,
            });
            var chatClientAgentField = typeof(AgentChat).GetField("chatClientAgent", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            chatClientAgentField!.SetValue(chat, chatClientAgent);

            var lease = new RunningAgentChatLease(sessionId, chat, () => ValueTask.CompletedTask);
            CreatedLease = lease;
            return Task.FromResult(lease);
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

    private sealed class NonReceiverChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
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

    private sealed class FakeLogger : ILogger
    {
        public record LogEntry(LogLevel Level, string Message);
        public List<LogEntry> Logs { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Logs.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
