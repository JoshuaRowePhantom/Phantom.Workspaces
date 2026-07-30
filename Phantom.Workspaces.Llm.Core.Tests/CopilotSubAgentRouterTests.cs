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

    [Fact]
    public async Task SubAgentStarted_WithProvidedName_UsesProvidedNameAsAgentChatDisplayName()
    {
        // Fix #1133 (data/model side): the caller-provided sub-agent name arrives on the
        // lifecycle-start "display-name" argument. The router must read it and pass it as
        // displayNameOverride to IRunningAgentChatFactory.CreateAsync so the sub-agent's
        // AgentChat.DisplayName carries the provided name — not a fresh session GUID.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, _) = CreateRouter(factory);

        await router.RouteAsync(LifecycleStart(
            agentId: "agent-1",
            parentToolCallId: "call-1",
            displayName: "fix-reload1",
            description: "reload the workspace"));

        var (displayNameOverride, descriptionOverride) = Assert.Single(factory.CreateCallOverrides);
        Assert.Equal("fix-reload1", displayNameOverride);
        Assert.Equal("reload the workspace", descriptionOverride);
    }

    [Fact]
    public async Task SubAgentStarted_WithProvidedName_KeepsSessionGuidAsInternalIdentity()
    {
        // Fix #1133: propagating the provided name into DisplayName MUST NOT change the
        // session id / AgentId, which remain the internally-generated GUID used as the routing
        // key (start.CallId → agentId → ChildRoutingEntry) and the persisted session identity.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, _) = CreateRouter(factory);

        await router.RouteAsync(LifecycleStart(
            agentId: "agent-1",
            parentToolCallId: "call-1",
            displayName: "fix-reload1"));

        var call = Assert.Single(factory.CreateCalls);
        // Session id is a freshly generated 32-char hex GUID (Guid.NewGuid().ToString("n")),
        // NOT the provided display name. The AgentId used for routing is the lifecycle
        // callId ("agent-1"), which is likewise independent of the display name.
        Assert.NotEqual("fix-reload1", call.SessionId.Value);
        Assert.Matches("^[0-9a-f]{32}$", call.SessionId.Value!);
    }

    [Fact]
    public async Task SubAgentStarted_WithoutDisplayName_FallsBackToFactoryDefault()
    {
        // Fix #1133 fallback: when no display-name argument is provided (null OR whitespace),
        // the router must pass null so the AgentChat falls back to the client-info default —
        // never throw and never invent a fake name.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, _) = CreateRouter(factory);

        // Case 1: whitespace display-name degrades to null.
        await router.RouteAsync(LifecycleStart(
            agentId: "agent-1",
            parentToolCallId: "call-1",
            displayName: "   ",
            description: "   "));

        var overrides = Assert.Single(factory.CreateCallOverrides);
        Assert.Null(overrides.DisplayNameOverride);
        Assert.Null(overrides.DescriptionOverride);
    }

    [Fact]
    public async Task SubAgentStarted_WithAgentTypeAndName_UsesNameNotAgentType()
    {
        // Fix #1133 (field choice): the SDK provides both an agent 'name' (surfaced as the
        // "display-name" argument) and an agent-type. Only the name should become the
        // AgentChat.DisplayName; agent-type is capability, not identity, and must not leak
        // into the sub-agent card.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, _) = CreateRouter(factory);

        // The lifecycle stream only carries display-name/description arguments (per
        // CopilotSdkStreamAdapter). agent-type is not among them: the caller-provided name is
        // what the router propagates.
        await router.RouteAsync(LifecycleStart(
            agentId: "agent-1",
            parentToolCallId: "call-1",
            displayName: "fix-reload1"));

        var overrides = Assert.Single(factory.CreateCallOverrides);
        Assert.Equal("fix-reload1", overrides.DisplayNameOverride);
        Assert.DoesNotContain("general-purpose", overrides.DisplayNameOverride ?? string.Empty, StringComparison.Ordinal);
    }

    // ─── Fix #1139 tests ─────────────────────────────────────────────────────────

    private static ChatResponseUpdate LifecycleStartWithoutAgentId(
        string parentToolCallId,
        string displayName = "Sub Agent",
        string description = "desc")
    {
        // Mirrors the CopilotSdkStreamAdapter output when SubagentStartedEvent.AgentId is
        // empty: CallId is empty; the spawning tool-call id is surfaced only in the
        // ParentToolCallIdArgumentName argument for start-time correlation in the router.
        var call = new FunctionCallContent(
            string.Empty,
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

    [Fact]
    public async Task RouteAsync_StartedOmitsAgentId_RunningContentReachesChildReceiver_NotPending()
    {
        // Fix #1139 crux (router side): a SubagentStartedEvent that carries no AgentId parks
        // its ChildRoutingEntry in a pendingChildSinksByToolCall map keyed by the spawning
        // tool-call id. When the child's running content arrives tagged with its runtime
        // AgentId, the router adopts (re-keys) that pending entry under the AgentId and
        // delivers the content to the ALREADY-ATTACHED receiver — nothing is left parked in
        // ChildRoutingEntry.pending.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, channel) = CreateRouter(factory);

        await router.RouteAsync(LifecycleStartWithoutAgentId("call-1"));
        // Content is stamped with the child's runtime AgentId, distinct from any tool-call id.
        await router.RouteAsync(SubAgentText("child-runtime-id", "live text"));

        Assert.Empty(await DrainAsync(channel));
        var updates = await DrainReceiverAsync(factory.CreatedReceiver!);
        // The content must appear on the child receiver — not silently buffered in pending.
        Assert.Contains(updates, u => u.Text == "live text");
    }

    [Fact]
    public async Task RouteAsync_MismatchedSinkAndContentId_NeverWrittenToRootWriter()
    {
        // Fix #1139: prior to the fix, a mismatch between the sink key and the content's
        // AgentId caused the update to fall into an orphan sink (never receiver-attached),
        // giving the appearance of a dropped update. This test asserts the invariant on the
        // NEGATIVE face: no sub-agent-tagged update ever reaches rootWriter, regardless of
        // whether it matches an existing sink.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, channel) = CreateRouter(factory);

        await router.RouteAsync(LifecycleStart("agent-known", "call-1"));
        // Content stamped with an AgentId that doesn't match any registered sink.
        await router.RouteAsync(SubAgentText("agent-mismatch", "child-only text"));

        Assert.Empty(await DrainAsync(channel));
    }

    [Fact]
    public async Task RouteAsync_TwoConcurrentSubAgents_EachContentRoutedByOwnAgentId()
    {
        // Fix #1139 concurrent-disambiguation: with two concurrently-active children, each
        // child's content (keyed by its own AgentId) lands in its own receiver, with no
        // cross-contamination.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, channel) = CreateRouter(factory);

        await router.RouteAsync(LifecycleStart("child-a", "call-a"));
        var receiverA = factory.CreatedReceivers[^1];

        await router.RouteAsync(LifecycleStart("child-b", "call-b"));
        var receiverB = factory.CreatedReceivers[^1];
        Assert.NotSame(receiverA, receiverB);

        await router.RouteAsync(SubAgentText("child-a", "hello from A"));
        await router.RouteAsync(SubAgentText("child-b", "hello from B"));
        await router.RouteAsync(SubAgentText("child-a", "more from A"));

        Assert.Empty(await DrainAsync(channel));

        var aUpdates = await DrainReceiverAsync(receiverA);
        var bUpdates = await DrainReceiverAsync(receiverB);

        Assert.Contains(aUpdates, u => u.Text == "hello from A");
        Assert.Contains(aUpdates, u => u.Text == "more from A");
        Assert.DoesNotContain(aUpdates, u => u.Text == "hello from B");

        Assert.Contains(bUpdates, u => u.Text == "hello from B");
        Assert.DoesNotContain(bUpdates, u => u.Text == "hello from A");
        Assert.DoesNotContain(bUpdates, u => u.Text == "more from A");
    }

    [Fact]
    public async Task RouteAsync_StartedOmitsAgentId_ThenLifecycleCompleted_CompletesChild()
    {
        // Edge case complement to RouteAsync_StartedOmitsAgentId_...: if no content arrived
        // between an AgentId-less start and its completion, the pending sink is still keyed
        // by tool-call id. The SDK's completion event carries the child AgentId at event
        // level; the router adopts the oldest pending entry to complete it, so the child
        // receiver is Complete()'d rather than leaked.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, _) = CreateRouter(factory);

        await router.RouteAsync(LifecycleStartWithoutAgentId("call-1"));
        // Completion arrives with the child AgentId now known.
        await router.RouteAsync(LifecycleCompleted("child-runtime-id"));

        // Receiver's stream terminates gracefully.
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in factory.CreatedReceiver!.GetStreamingResponseAsync([]))
            updates.Add(u);
    }
}
