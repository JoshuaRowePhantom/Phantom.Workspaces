using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentSchema;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Tests for <see cref="CopilotSubAgentRouter"/> in isolation over pre-recorded
/// <see cref="ChatResponseUpdate"/> streams (issue #808 / #866 / #1109 / #1110). All tests exercise
/// the unified factory path — the registry path was removed in issue #1109.
/// </summary>
public sealed class CopilotSubAgentRouterTests
{
    private static (CopilotSubAgentRouter router, Channel<ChatResponseUpdate> channel)
        CreateRouter(
            SubAgentTestFakes.FakeRunningAgentChatFactory? factory = null,
            SubAgentTestFakes.FakeSubAgentTable? table = null)
    {
        var channel = Channel.CreateUnbounded<ChatResponseUpdate>();
        var router = new CopilotSubAgentRouter(
            channel.Writer,
            factory ?? new SubAgentTestFakes.FakeRunningAgentChatFactory(),
            table ?? new SubAgentTestFakes.FakeSubAgentTable());
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

    private static async Task<List<ChatResponseUpdate>> DrainReceiverAsync(CopilotSubAgentChatClient receiver)
    {
        receiver.Complete();
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in receiver.GetStreamingResponseAsync([]))
            updates.Add(u);
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
    public async Task RouteAsync_SubAgentUpdate_PushedToChildReceiver_NeverParent()
    {
        // Fix #1110 inverse-face: sub-agent-tagged updates route to the child receiver only.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, channel) = CreateRouter(factory);

        await router.RouteAsync(LifecycleStart("agent-1", "call-1"));
        await router.RouteAsync(SubAgentText("agent-1", "child text"));

        Assert.Empty(await DrainAsync(channel));
        var updates = await DrainReceiverAsync(factory.CreatedReceiver!);
        Assert.Contains(updates, u => u.Text == "child text");
    }

    [Fact]
    public async Task RouteAsync_LifecycleStart_CallsFactoryWithSubAgentDefinition()
    {
        // Fix #1109: sub-agent construction goes through the mandatory factory (no registry path).
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, _) = CreateRouter(factory);

        await router.RouteAsync(LifecycleStart("agent-1", "call-7", displayName: "Researcher"));

        var call = Assert.Single(factory.CreateCalls);
        Assert.Contains("github-copilot-subagent", call.Definition.ToJson());
    }

    [Fact]
    public async Task RouteAsync_LifecycleUpdate_NotForwardedToRootChannel()
    {
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, channel) = CreateRouter(factory);

        await router.RouteAsync(LifecycleStart("agent-1", "call-1"));
        await router.RouteAsync(LifecycleCompleted("agent-1"));

        Assert.Empty(await DrainAsync(channel));
    }

    [Fact]
    public async Task RouteAsync_ToolStartBeforeLifecycleStart_InjectsBufferedToolCallIntoChild()
    {
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, _) = CreateRouter(factory);

        await router.RouteAsync(RootToolStart("call-1", "spawn_agent"));
        await router.RouteAsync(LifecycleStart("agent-1", "call-1"));

        var updates = await DrainReceiverAsync(factory.CreatedReceiver!);
        var injected = Assert.Single(updates, u => u.Role == ChatRole.User);
        var call = Assert.IsType<FunctionCallContent>(injected.Contents.Single());
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("spawn_agent", call.Name);
    }

    [Fact]
    public async Task RouteAsync_LifecycleStartBeforeToolStart_InjectsToolCallWhenToolStartArrives()
    {
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, _) = CreateRouter(factory);

        await router.RouteAsync(LifecycleStart("agent-1", "call-1"));
        await router.RouteAsync(RootToolStart("call-1", "spawn_agent"));

        var updates = await DrainReceiverAsync(factory.CreatedReceiver!);
        var injected = Assert.Single(updates, u => u.Role == ChatRole.User);
        Assert.Equal("spawn_agent", ((FunctionCallContent)injected.Contents.Single()).Name);
    }

    [Fact]
    public async Task RouteAsync_LifecycleCompleted_CompletesChildReceiver()
    {
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, _) = CreateRouter(factory);

        await router.RouteAsync(LifecycleStart("agent-1", "call-1"));
        await router.RouteAsync(LifecycleCompleted("agent-1"));

        // After Complete, the receiver's stream finishes without throwing.
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in factory.CreatedReceiver!.GetStreamingResponseAsync([]))
            updates.Add(u);
        // No assertion on count — merely that draining terminates gracefully.
    }

    [Fact]
    public async Task RouteAsync_LifecycleFailed_FailsChildReceiverWithError()
    {
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, _) = CreateRouter(factory);

        await router.RouteAsync(LifecycleStart("agent-1", "call-1"));
        await router.RouteAsync(LifecycleFailed("agent-1", "engine exploded"));

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in factory.CreatedReceiver!.GetStreamingResponseAsync([]))
            { }
        });
        Assert.Contains("engine exploded", ex.ToString());
    }

    [Fact]
    public async Task RouteAsync_UnknownSubAgentId_BufferedInChildSink_NeverParent()
    {
        // Fix #1109/#1110: a delta with a non-empty agentId for which no start has arrived is
        // buffered in a per-child sink — never falls back to the parent transcript. When start
        // eventually arrives, the buffered update is flushed to the real receiver in order.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, channel) = CreateRouter(factory);

        await router.RouteAsync(SubAgentText("late-agent", "buffered text"));

        // Fix #1110: parent transcript stays empty.
        Assert.Empty(await DrainAsync(channel));
    }

    [Fact]
    public async Task RouteAsync_BufferedSubAgentDelta_FlushedToChildReceiver_OnLateStart()
    {
        // Fix #1109: pre-start deltas are held on a BufferingSubAgentChat and flushed when the
        // real receiver is attached.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, _) = CreateRouter(factory);

        await router.RouteAsync(SubAgentText("late-agent", "pre-start message"));
        await router.RouteAsync(LifecycleStart("late-agent", "call-late"));

        var updates = await DrainReceiverAsync(factory.CreatedReceiver!);
        Assert.Contains(updates, u => u.Text == "pre-start message");
    }

    [Fact]
    public void CopilotSubAgentRouter_NullFactory_Throws()
    {
        var channel = Channel.CreateUnbounded<ChatResponseUpdate>();
        Assert.Throws<ArgumentNullException>(() =>
            new CopilotSubAgentRouter(channel.Writer, factory: null!, new SubAgentTestFakes.FakeSubAgentTable()));
    }

    [Fact]
    public void CopilotSubAgentRouter_NullSubAgentTable_Throws()
    {
        var channel = Channel.CreateUnbounded<ChatResponseUpdate>();
        Assert.Throws<ArgumentNullException>(() =>
            new CopilotSubAgentRouter(channel.Writer, new SubAgentTestFakes.FakeRunningAgentChatFactory(), subAgentTable: null!));
    }
}
