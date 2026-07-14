using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Tests for <see cref="CopilotSubAgentRouter"/> in isolation: routing, lifecycle interpretation,
/// and tool-start buffering driven by pre-recorded <see cref="ChatResponseUpdate"/> streams with
/// no raw Copilot SDK event types (issue #808 / #866).
/// </summary>
public sealed class CopilotSubAgentRouterTests
{
    private static (CopilotSubAgentRouter router, Channel<ChatResponseUpdate> channel)
        CreateRouter(ISubAgentChatRegistry? registry = null)
    {
        var channel = Channel.CreateUnbounded<ChatResponseUpdate>();
        var router = new CopilotSubAgentRouter(channel.Writer, registry);
        return (router, channel);
    }

    private static async Task<List<ChatResponseUpdate>> DrainAsync(Channel<ChatResponseUpdate> channel)
    {
        channel.Writer.Complete();
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in channel.Reader.ReadAllAsync())
        {
            updates.Add(update);
        }

        return updates;
    }

    // ─── pre-recorded update builders ───────────────────────────────────────────

    private static ChatResponseUpdate RootText(string text) =>
        new() { Role = ChatRole.Assistant, Contents = [new TextContent(text)] };

    private static ChatResponseUpdate SubAgentText(string agentId, string text)
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

    private static ChatResponseUpdate RootToolStart(string callId, string toolName = "my_tool") =>
        new()
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent(callId, toolName, new Dictionary<string, object?>())],
        };

    private static ChatResponseUpdate LifecycleStart(
        string agentId,
        string parentToolCallId,
        string displayName = "Sub Agent",
        string description = "desc")
    {
        var call = new FunctionCallContent(
            agentId,
            CopilotSdkStreamAdapter.SubAgentStartLifecycleName,
            new Dictionary<string, object?>
            {
                [CopilotSdkStreamAdapter.ParentToolCallIdArgumentName] = parentToolCallId,
                [CopilotSdkStreamAdapter.DisplayNameArgumentName] = displayName,
                [CopilotSdkStreamAdapter.DescriptionArgumentName] = description,
            })
        {
            AdditionalProperties = new()
            {
                [CopilotSdkStreamAdapter.ContentTypePropertyName] = CopilotSdkStreamAdapter.SubAgentLifecycleContentType,
            },
        };
        return new ChatResponseUpdate { Contents = [call] };
    }

    private static ChatResponseUpdate LifecycleResult(string agentId, string json)
    {
        var result = new FunctionResultContent(agentId, json)
        {
            AdditionalProperties = new()
            {
                [CopilotSdkStreamAdapter.ContentTypePropertyName] = CopilotSdkStreamAdapter.SubAgentLifecycleContentType,
            },
        };
        return new ChatResponseUpdate { Contents = [result] };
    }

    private static ChatResponseUpdate LifecycleCompleted(string agentId) =>
        LifecycleResult(agentId, """{"event":"completed"}""");

    private static ChatResponseUpdate LifecycleFailed(string agentId, string error = "boom") =>
        LifecycleResult(agentId, $$"""{"event":"failed","error":"{{error}}"}""");

    // ─── tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RouteAsync_RootUpdate_PushedToRootChannel()
    {
        var (router, channel) = CreateRouter();

        await router.RouteAsync(RootText("hello"));

        var updates = await DrainAsync(channel);
        var update = Assert.Single(updates);
        Assert.Equal("hello", ((TextContent)update.Contents.Single()).Text);
    }

    [Fact]
    public async Task RouteAsync_SubAgentUpdate_PushedToChildSink()
    {
        var registry = new FakeSubAgentChatRegistry();
        var (router, channel) = CreateRouter(registry);

        await router.RouteAsync(LifecycleStart("agent-1", "call-1"));
        await router.RouteAsync(SubAgentText("agent-1", "child text"));

        var childSink = (FakeSubAgentChat)registry.Sinks["agent-1"];
        var childUpdate = Assert.Single(childSink.ReceivedUpdates);
        Assert.Equal("child text", ((TextContent)childUpdate.Contents.Single()).Text);
        Assert.Empty(await DrainAsync(channel));
    }

    [Fact]
    public async Task RouteAsync_LifecycleStart_CreatesChildViaRegistryWithParentToolCallId()
    {
        var registry = new FakeSubAgentChatRegistry();
        var (router, _) = CreateRouter(registry);

        await router.RouteAsync(LifecycleStart("agent-1", "call-7", displayName: "Researcher"));

        var call = Assert.Single(registry.CreateCalls);
        Assert.Equal("agent-1", call.AgentId);
        Assert.Equal("call-7", call.ParentToolCallId);
        Assert.Contains("Researcher", call.Definition.ToJson());
    }

    [Fact]
    public async Task RouteAsync_LifecycleUpdate_NotForwardedToRootChannel()
    {
        var registry = new FakeSubAgentChatRegistry();
        var (router, channel) = CreateRouter(registry);

        await router.RouteAsync(LifecycleStart("agent-1", "call-1"));
        await router.RouteAsync(LifecycleCompleted("agent-1"));

        Assert.Empty(await DrainAsync(channel));
    }

    [Fact]
    public async Task RouteAsync_ToolStartBeforeLifecycleStart_InjectsBufferedToolCallIntoChild()
    {
        var registry = new FakeSubAgentChatRegistry();
        var (router, _) = CreateRouter(registry);

        await router.RouteAsync(RootToolStart("call-1", "spawn_agent"));
        await router.RouteAsync(LifecycleStart("agent-1", "call-1"));

        var childSink = (FakeSubAgentChat)registry.Sinks["agent-1"];
        var injected = Assert.Single(childSink.ReceivedUpdates);
        Assert.Equal(ChatRole.User, injected.Role);
        var call = Assert.IsType<FunctionCallContent>(injected.Contents.Single());
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("spawn_agent", call.Name);
    }

    [Fact]
    public async Task RouteAsync_LifecycleStartBeforeToolStart_InjectsToolCallWhenToolStartArrives()
    {
        var registry = new FakeSubAgentChatRegistry();
        var (router, _) = CreateRouter(registry);

        await router.RouteAsync(LifecycleStart("agent-1", "call-1"));
        await router.RouteAsync(RootToolStart("call-1", "spawn_agent"));

        var childSink = (FakeSubAgentChat)registry.Sinks["agent-1"];
        var injected = Assert.Single(childSink.ReceivedUpdates);
        Assert.Equal(ChatRole.User, injected.Role);
        Assert.Equal("spawn_agent", ((FunctionCallContent)injected.Contents.Single()).Name);
    }

    [Fact]
    public async Task RouteAsync_LifecycleCompleted_CompletesChildSink()
    {
        var registry = new FakeSubAgentChatRegistry();
        var (router, _) = CreateRouter(registry);

        await router.RouteAsync(LifecycleStart("agent-1", "call-1"));
        await router.RouteAsync(LifecycleCompleted("agent-1"));

        var childSink = (FakeSubAgentChat)registry.Sinks["agent-1"];
        Assert.Equal(AgentChatCompletionState.Succeeded, childSink.CompletionState);
    }

    [Fact]
    public async Task RouteAsync_LifecycleFailed_FailsChildSinkWithError()
    {
        var registry = new FakeSubAgentChatRegistry();
        var (router, _) = CreateRouter(registry);

        await router.RouteAsync(LifecycleStart("agent-1", "call-1"));
        await router.RouteAsync(LifecycleFailed("agent-1", "engine exploded"));

        var childSink = (FakeSubAgentChat)registry.Sinks["agent-1"];
        Assert.Equal(AgentChatCompletionState.Failed, childSink.CompletionState);
        Assert.NotNull(childSink.FailureException);
        Assert.Contains("engine exploded", childSink.FailureException!.Message);
    }

    [Fact]
    public async Task RouteAsync_UnknownSubAgentIdWithRegistry_FallsBackToRootChannel()
    {
        var registry = new FakeSubAgentChatRegistry();
        var (router, channel) = CreateRouter(registry);

        await router.RouteAsync(SubAgentText("never-started", "orphan text"));

        var updates = await DrainAsync(channel);
        var update = Assert.Single(updates);
        Assert.Equal("orphan text", ((TextContent)update.Contents.Single()).Text);
    }

    // ─── Fakes ───────────────────────────────────────────────────────────────────

    private sealed class FakeSubAgentChat : ISubAgentChat
    {
        public AgentChatCompletionState CompletionState { get; private set; } = AgentChatCompletionState.Running;
        public Exception? FailureException { get; private set; }
        public List<ChatResponseUpdate> ReceivedUpdates { get; } = new();

        public void Push(ChatResponseUpdate update) => ReceivedUpdates.Add(update);

        public void Complete() => CompletionState = AgentChatCompletionState.Succeeded;

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

        public Task<ISubAgentChat> GetOrCreateAsync(
            string agentId,
            AgentDefinition subAgentDefinition,
            string parentToolCallId,
            CancellationToken cancellationToken = default)
        {
            CreateCalls.Add((agentId, subAgentDefinition, parentToolCallId));
            if (!Sinks.TryGetValue(agentId, out var existing))
            {
                existing = new FakeSubAgentChat();
                Sinks[agentId] = existing;
            }

            return Task.FromResult(existing);
        }

        public ISubAgentChat? TryGet(string agentId) =>
            Sinks.TryGetValue(agentId, out var sink) ? sink : null;
    }
}
