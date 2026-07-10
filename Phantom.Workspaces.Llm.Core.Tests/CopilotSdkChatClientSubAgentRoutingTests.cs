using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Tests for sub-agent event routing, buffering, and prompt injection in
/// <see cref="CopilotSdkTurnEventDispatcher"/>.
/// </summary>
public sealed class CopilotSdkChatClientSubAgentRoutingTests
{
    private static FakeSubAgentChatRegistry CreateRegistry() => new();

    private static FakeSubAgentChat CreateChildSink(string agentId = "agent-1") =>
        new(agentId, "Test Sub-Agent");

    private static (CopilotSdkTurnEventDispatcher dispatcher, System.Threading.Channels.Channel<ChatResponseUpdate> channel)
        CreateDispatcher(ISubAgentChatRegistry? registry = null)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<ChatResponseUpdate>();
        var dispatcher = new CopilotSdkTurnEventDispatcher(channel.Writer, registry);
        return (dispatcher, channel);
    }

    private static AssistantMessageDeltaEvent DeltaEvent(string? agentId, string text) =>
        new AssistantMessageDeltaEvent
        {
            AgentId = agentId!,
            Data = new AssistantMessageDeltaData { DeltaContent = text, MessageId = "msg-1" },
        };

    private static ToolExecutionStartEvent ToolStartEvent(string? agentId, string toolCallId, string toolName = "my_tool") =>
        new ToolExecutionStartEvent
        {
            AgentId = agentId!,
            Data = new ToolExecutionStartData { ToolCallId = toolCallId, ToolName = toolName },
        };

    private static ToolExecutionCompleteEvent ToolCompleteEvent(string? agentId, string toolCallId) =>
        new ToolExecutionCompleteEvent
        {
            AgentId = agentId!,
            Data = new ToolExecutionCompleteData { ToolCallId = toolCallId, Success = true, Result = new ToolExecutionCompleteResult { Content = "ok" } },
        };

    private static SubagentStartedEvent SubagentStartedEvent(string? agentId, string parentToolCallId, string displayName = "Sub Agent") =>
        new SubagentStartedEvent
        {
            AgentId = agentId!,
            Data = new SubagentStartedData
            {
                ToolCallId = parentToolCallId,
                AgentName = "sub_agent",
                AgentDisplayName = displayName,
                AgentDescription = "A test sub-agent",
            },
        };

    private static SubagentCompletedEvent SubagentCompletedEvent(string? agentId, string toolCallId) =>
        new SubagentCompletedEvent
        {
            AgentId = agentId!,
            Data = new SubagentCompletedData { ToolCallId = toolCallId, AgentName = "sub_agent", AgentDisplayName = "Sub Agent" },
        };

    private static SubagentFailedEvent SubagentFailedEvent(string? agentId, string toolCallId, string error = "boom") =>
        new SubagentFailedEvent
        {
            AgentId = agentId!,
            Data = new SubagentFailedData { ToolCallId = toolCallId, AgentName = "sub_agent", AgentDisplayName = "Sub Agent", Error = error },
        };

    [Fact]
    public async Task AssistantMessageDeltaEvent_NullAgentId_WrittenToRootStream()
    {
        var (dispatcher, channel) = CreateDispatcher();

        await dispatcher.DispatchAsync(DeltaEvent(null, "hello"));

        channel.Writer.Complete();
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in channel.Reader.ReadAllAsync())
            updates.Add(u);

        Assert.Single(updates);
        Assert.Equal("hello", updates[0].Text);
    }

    [Fact]
    public async Task AssistantMessageDeltaEvent_WithAgentId_WrittenToChildStream()
    {
        var childSink = CreateChildSink("agent-1");
        var registry = CreateRegistry();
        registry.Register("agent-1", childSink);

        var (dispatcher, rootChannel) = CreateDispatcher(registry);

        await dispatcher.DispatchAsync(DeltaEvent("agent-1", "child text"));

        rootChannel.Writer.Complete();
        var rootUpdates = new List<ChatResponseUpdate>();
        await foreach (var u in rootChannel.Reader.ReadAllAsync())
            rootUpdates.Add(u);

        Assert.Empty(rootUpdates);
        Assert.Single(childSink.ReceivedUpdates);
        Assert.Equal("child text", childSink.ReceivedUpdates[0].Text);
    }

    [Fact]
    public async Task SubagentStartedEvent_CallsGetOrCreateAsync_WithAgentDefinition()
    {
        var registry = CreateRegistry();
        var (dispatcher, channel) = CreateDispatcher(registry);

        await dispatcher.DispatchAsync(SubagentStartedEvent("agent-42", "tool-call-1", "My Sub Agent"));

        Assert.Single(registry.CreateCalls);
        var call = registry.CreateCalls[0];
        Assert.Equal("agent-42", call.AgentId);
        Assert.Equal("tool-call-1", call.ParentToolCallId);
        Assert.NotNull(call.Definition);
    }

    [Fact]
    public async Task SubagentCompletedEvent_CallsComplete_OnChildSink()
    {
        var childSink = CreateChildSink("agent-1");
        var registry = CreateRegistry();
        registry.Register("agent-1", childSink);

        var (dispatcher, _) = CreateDispatcher(registry);

        await dispatcher.DispatchAsync(SubagentCompletedEvent("agent-1", "tool-call-1"));

        Assert.Equal(AgentChatCompletionState.Succeeded, childSink.CompletionState);
    }

    [Fact]
    public async Task SubagentFailedEvent_CallsFail_OnChildSink()
    {
        var childSink = CreateChildSink("agent-1");
        var registry = CreateRegistry();
        registry.Register("agent-1", childSink);

        var (dispatcher, _) = CreateDispatcher(registry);

        await dispatcher.DispatchAsync(SubagentFailedEvent("agent-1", "tool-call-1", "something went wrong"));

        Assert.Equal(AgentChatCompletionState.Failed, childSink.CompletionState);
        Assert.IsType<AgentSubagentFailedException>(childSink.FailureException);
        Assert.Equal("something went wrong", childSink.FailureException!.Message);
    }

    [Fact]
    public async Task ToolExecutionStartEvent_WithAgentId_RoutedToChildSink()
    {
        var childSink = CreateChildSink("agent-1");
        var registry = CreateRegistry();
        registry.Register("agent-1", childSink);

        var (dispatcher, rootChannel) = CreateDispatcher(registry);

        await dispatcher.DispatchAsync(ToolStartEvent("agent-1", "call-1"));

        rootChannel.Writer.Complete();
        var rootUpdates = new List<ChatResponseUpdate>();
        await foreach (var u in rootChannel.Reader.ReadAllAsync())
            rootUpdates.Add(u);

        Assert.Empty(rootUpdates);
        Assert.Single(childSink.ReceivedUpdates);
        var callContent = Assert.IsType<FunctionCallContent>(childSink.ReceivedUpdates[0].Contents[0]);
        Assert.Equal("call-1", callContent.CallId);
    }

    [Fact]
    public async Task ToolExecutionCompleteEvent_WithAgentId_RoutedToChildSink()
    {
        var childSink = CreateChildSink("agent-1");
        var registry = CreateRegistry();
        registry.Register("agent-1", childSink);

        var (dispatcher, rootChannel) = CreateDispatcher(registry);

        await dispatcher.DispatchAsync(ToolCompleteEvent("agent-1", "call-1"));

        rootChannel.Writer.Complete();
        var rootUpdates = new List<ChatResponseUpdate>();
        await foreach (var u in rootChannel.Reader.ReadAllAsync())
            rootUpdates.Add(u);

        Assert.Empty(rootUpdates);
        Assert.Single(childSink.ReceivedUpdates);
        var resultContent = Assert.IsType<FunctionResultContent>(childSink.ReceivedUpdates[0].Contents[0]);
        Assert.Equal("call-1", resultContent.CallId);
    }

    [Fact]
    public async Task ToolStart_ArrivesBeforeSubagentStarted_PromptInjectedAsFirstMessage()
    {
        var registry = CreateRegistry();
        var (dispatcher, _) = CreateDispatcher(registry);

        // Tool start arrives first on root stream
        await dispatcher.DispatchAsync(ToolStartEvent(null, "call-spawn", "spawn_agent"));

        // Sub-agent then starts
        await dispatcher.DispatchAsync(SubagentStartedEvent("agent-1", "call-spawn"));

        var childSink = (FakeSubAgentChat)registry.Sinks["agent-1"];
        Assert.NotEmpty(childSink.ReceivedUpdates);
        var firstUpdate = childSink.ReceivedUpdates[0];
        var callContent = Assert.IsType<FunctionCallContent>(firstUpdate.Contents[0]);
        Assert.Equal("call-spawn", callContent.CallId);
        Assert.Equal("spawn_agent", callContent.Name);
    }

    [Fact]
    public async Task SubagentStarted_ArrivesBeforeToolStart_PromptInjectedWhenToolStartArrives()
    {
        var registry = CreateRegistry();
        var (dispatcher, _) = CreateDispatcher(registry);

        // Sub-agent starts first (before its spawning tool call arrives)
        await dispatcher.DispatchAsync(SubagentStartedEvent("agent-1", "call-spawn"));

        // Tool start arrives later on root stream
        await dispatcher.DispatchAsync(ToolStartEvent(null, "call-spawn", "spawn_agent"));

        var childSink = (FakeSubAgentChat)registry.Sinks["agent-1"];
        Assert.NotEmpty(childSink.ReceivedUpdates);
        var firstUpdate = childSink.ReceivedUpdates[0];
        var callContent = Assert.IsType<FunctionCallContent>(firstUpdate.Contents[0]);
        Assert.Equal("call-spawn", callContent.CallId);
    }

    [Fact]
    public async Task SubAgent_ToolCallPrompt_IsFirstHistoryMessage()
    {
        var registry = CreateRegistry();
        var (dispatcher, _) = CreateDispatcher(registry);

        await dispatcher.DispatchAsync(ToolStartEvent(null, "call-1", "do_work"));
        await dispatcher.DispatchAsync(SubagentStartedEvent("agent-1", "call-1"));

        // Push some child content after start
        await dispatcher.DispatchAsync(DeltaEvent("agent-1", "child output"));

        var childSink = (FakeSubAgentChat)registry.Sinks["agent-1"];
        Assert.Equal(2, childSink.ReceivedUpdates.Count);
        var firstContent = Assert.IsType<FunctionCallContent>(childSink.ReceivedUpdates[0].Contents[0]);
        Assert.Equal("call-1", firstContent.CallId);
        Assert.Equal("child output", childSink.ReceivedUpdates[1].Text);
    }

    [Fact]
    public async Task SubAgent_ToolCallPrompt_SubagentStartedBeforeToolStart_StillRecorded()
    {
        var registry = CreateRegistry();
        var (dispatcher, _) = CreateDispatcher(registry);

        // Sub-agent starts before its tool call arrives
        await dispatcher.DispatchAsync(SubagentStartedEvent("agent-1", "call-1"));

        // Some child content arrives (these still go to child, just without prompt yet)
        await dispatcher.DispatchAsync(DeltaEvent("agent-1", "early output"));

        // Tool start arrives on root
        await dispatcher.DispatchAsync(ToolStartEvent(null, "call-1", "do_work"));

        var childSink = (FakeSubAgentChat)registry.Sinks["agent-1"];

        // Prompt must be present in received updates
        var promptUpdate = childSink.ReceivedUpdates.Find(u =>
            u.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == "call-1"));
        Assert.NotNull(promptUpdate);
    }

    // ─── Fakes ───────────────────────────────────────────────────────────────────

    private sealed class FakeSubAgentChat : ISubAgentChat, IRunningSubAgent
    {
        public string AgentId { get; }
        public string DisplayName { get; }
        public string Description => string.Empty;
        public AgentChatCompletionState CompletionState { get; private set; } = AgentChatCompletionState.Running;
        public DateTime LastUpdatedAt { get; private set; } = DateTime.UtcNow;
        public Exception? FailureException { get; private set; }
        public List<ChatResponseUpdate> ReceivedUpdates { get; } = new();
        public IReadOnlyList<IRunningSubAgent> SubAgents => [];

        public FakeSubAgentChat(string agentId, string displayName)
        {
            AgentId = agentId;
            DisplayName = displayName;
        }

        public void Push(ChatResponseUpdate update) => ReceivedUpdates.Add(update);

        public void Complete()
        {
            CompletionState = AgentChatCompletionState.Succeeded;
            LastUpdatedAt = DateTime.UtcNow;
        }

        public void Fail(Exception ex)
        {
            CompletionState = AgentChatCompletionState.Failed;
            FailureException = ex;
        }
    }

    private sealed class FakeSubAgentChatRegistry : ISubAgentChatRegistry
    {
        public List<(string AgentId, AgentDefinition Definition, string ParentToolCallId)> CreateCalls { get; } = new();
        public Dictionary<string, ISubAgentChat> Sinks { get; } = new(StringComparer.Ordinal);

        public void Register(string agentId, ISubAgentChat sink) => Sinks[agentId] = sink;

        public Task<ISubAgentChat> GetOrCreateAsync(
            string agentId,
            AgentDefinition subAgentDefinition,
            string parentToolCallId,
            CancellationToken cancellationToken = default)
        {
            CreateCalls.Add((agentId, subAgentDefinition, parentToolCallId));
            if (!Sinks.TryGetValue(agentId, out var existing))
            {
                existing = new FakeSubAgentChat(agentId, "Fake Sub-Agent");
                Sinks[agentId] = existing;
            }

            return Task.FromResult(existing);
        }

        public ISubAgentChat? TryGet(string agentId) =>
            Sinks.TryGetValue(agentId, out var sink) ? sink : null;
    }
}
