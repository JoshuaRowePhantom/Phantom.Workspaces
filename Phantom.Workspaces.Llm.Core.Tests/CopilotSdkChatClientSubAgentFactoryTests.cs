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
/// Tests for the factory-path sub-agent wiring in <see cref="CopilotSdkTurnEventDispatcher"/>:
/// uses <see cref="IRunningAgentChatFactory"/> + <see cref="ISubAgentTable"/> to create and route
/// sub-agent events through <see cref="ICopilotSubAgentReceiver"/>.
/// </summary>
public sealed class CopilotSdkChatClientSubAgentFactoryTests
{
    // ─── helpers ────────────────────────────────────────────────────────────────

    private static (CopilotSdkTurnEventDispatcher dispatcher, System.Threading.Channels.Channel<ChatResponseUpdate> channel)
        CreateDispatcher(
            FakeRunningAgentChatFactory? factory = null,
            FakeSubAgentTable? table = null,
            FakeLogger? logger = null)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<ChatResponseUpdate>();
        var dispatcher = new CopilotSdkTurnEventDispatcher(
            channel.Writer,
            registry: null,
            factory: factory,
            subAgentTable: table,
            logger: logger);
        return (dispatcher, channel);
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
        var (dispatcher, _) = CreateDispatcher(factory, table);

        await dispatcher.DispatchAsync(StartedEvent("agent-1"));

        Assert.Single(factory.CreateCalls);
        var (definition, _) = factory.CreateCalls[0];
        Assert.Contains("github-copilot-subagent", definition.ToJson());
    }

    [Fact]
    public async Task SubAgentStarted_CallsSubAgentTableAdd_WithCreatedAgentChat()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var (dispatcher, _) = CreateDispatcher(factory, table);

        await dispatcher.DispatchAsync(StartedEvent("agent-1"));

        Assert.Single(table.AddedChats);
        Assert.Same(factory.CreatedLease!.AgentChat, table.AddedChats[0]);
    }

    [Fact]
    public async Task SubAgentStarted_AcquiresReceiverFromSubAgentChat()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var (dispatcher, _) = CreateDispatcher(factory, table);

        await dispatcher.DispatchAsync(StartedEvent("agent-1"));

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
        var (dispatcher, _) = CreateDispatcher(factory, table);

        await dispatcher.DispatchAsync(StartedEvent("agent-1"));
        await dispatcher.DispatchAsync(DeltaEvent("agent-1", "hello from sub-agent"));

        var receiver = (CopilotSubAgentChatClient)factory.CreatedLease!.AgentChat.GetService(typeof(ICopilotSubAgentReceiver))!;
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
        var (dispatcher, _) = CreateDispatcher(factory, table);

        await dispatcher.DispatchAsync(StartedEvent("agent-1"));
        await dispatcher.DispatchAsync(CompletedEvent("agent-1"));

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
        var (dispatcher, _) = CreateDispatcher(factory, table);

        await dispatcher.DispatchAsync(StartedEvent("agent-1"));
        await dispatcher.DispatchAsync(FailedEvent("agent-1", "test error"));

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
    public async Task SubAgentEvent_UnknownSubAgentId_LoggedAndIgnored()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var logger = new FakeLogger();
        var (dispatcher, _) = CreateDispatcher(factory, table, logger);

        // No SubAgentStarted — dispatch event for unknown ID
        await dispatcher.DispatchAsync(DeltaEvent("unknown-agent", "text"));

        Assert.Contains(logger.Logs, l => l.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task SubAgentCompleted_UnknownSubAgentId_LoggedAndIgnored()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var logger = new FakeLogger();
        var (dispatcher, _) = CreateDispatcher(factory, table, logger);

        // No SubAgentStarted — dispatch completed for unknown ID
        await dispatcher.DispatchAsync(CompletedEvent("unknown-agent"));

        Assert.Contains(logger.Logs, l => l.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task SubAgentFailed_UnknownSubAgentId_LoggedAndIgnored()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var logger = new FakeLogger();
        var (dispatcher, _) = CreateDispatcher(factory, table, logger);

        // No SubAgentStarted — dispatch failed for unknown ID
        await dispatcher.DispatchAsync(FailedEvent("unknown-agent"));

        Assert.Contains(logger.Logs, l => l.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task SubAgentStarted_ReceiverNotExposedBySubAgentChat_Throws()
    {
        var factory = new FakeRunningAgentChatFactory(exposeReceiver: false);
        var table = new FakeSubAgentTable();
        var (dispatcher, _) = CreateDispatcher(factory, table);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await dispatcher.DispatchAsync(StartedEvent("agent-1")));
    }

    [Fact]
    public async Task SubAgentStarted_MultipleSubAgents_EachRoutedToCorrectReceiver()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        var (dispatcher, _) = CreateDispatcher(factory, table);

        await dispatcher.DispatchAsync(StartedEvent("agent-1", "call-1"));
        var receiver1 = (CopilotSubAgentChatClient)factory.CreatedLease!.AgentChat.GetService(typeof(ICopilotSubAgentReceiver))!;

        factory.ResetLease();

        await dispatcher.DispatchAsync(StartedEvent("agent-2", "call-2"));
        var receiver2 = (CopilotSubAgentChatClient)factory.CreatedLease!.AgentChat.GetService(typeof(ICopilotSubAgentReceiver))!;

        Assert.NotSame(receiver1, receiver2);

        // Route events to agent-1 and agent-2 separately
        await dispatcher.DispatchAsync(DeltaEvent("agent-1", "msg-for-1"));
        await dispatcher.DispatchAsync(DeltaEvent("agent-2", "msg-for-2"));

        // Complete both
        await dispatcher.DispatchAsync(CompletedEvent("agent-1", "call-1"));
        await dispatcher.DispatchAsync(CompletedEvent("agent-2", "call-2"));

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

        public System.Collections.ObjectModel.ObservableCollection<RunningAgentChat> RunningSessions { get; } = new();

        public FakeRunningAgentChatFactory(bool exposeReceiver = true)
        {
            _exposeReceiver = exposeReceiver;
        }

        public void ResetLease() => CreatedLease = null;

        Task<RunningAgentChatLease> IRunningAgentChatFactory.CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services,
            CancellationToken ct)
        {
            CreateCalls.Add((definition, sessionId));

            IChatClient client = _exposeReceiver
                ? new CopilotSubAgentChatClient()
                : new NonReceiverChatClient();

            // Create AgentChat using the internal constructor, skipping CreateAsync/InitializeAsync
            // to avoid starting the background processing loop that would consume from the channel.
            var chat = new AgentChat(new InternalCreateAgentChatRequest
            {
                AgentDefinition = null,
                ConfiguredStore = new InMemoryAgentPersistenceStore(),
            });

            // Create a real ChatClientAgent and inject it via reflection.
            // ChatClientAgent itself doesn't start background tasks - those are in AgentChat.
            var chatClientAgent = new ChatClientAgent(client, new ChatClientAgentOptions());
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

        SubAgent ISubAgentTable.Add(AgentChat agentChat)
        {
            AddedChats.Add(agentChat);
            var sessionId = new AgentSessionId(agentChat.AgentSessionId);
            return new SubAgent(sessionId, agentChat, null);
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
