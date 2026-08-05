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
        string description = "desc",
        string? agentName = null)
    {
        var call = new FunctionCallContent(
            agentId,
            CopilotSdkStreamAdapter.SubAgentStartLifecycleName,
            new Dictionary<string, object?>
            {
                [CopilotSdkStreamAdapter.ParentToolCallIdArgumentName] = parentToolCallId,
                [CopilotSdkStreamAdapter.DisplayNameArgumentName] = displayName,
                [CopilotSdkStreamAdapter.DescriptionArgumentName] = description,
                [CopilotSdkStreamAdapter.AgentNameArgumentName] = agentName,
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

        var overrides302 = Assert.Single(factory.CreateCallOverrides);
        Assert.Equal("fix-reload1", overrides302.DisplayNameOverride);
        Assert.Equal("reload the workspace", overrides302.DescriptionOverride);
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

    // ─── Fix #1151 tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SubAgentStarted_WithCallerName_CapturesNameOntoAgentChat()
    {
        // Fix #1151: the caller-supplied AgentName arrives on the lifecycle-start
        // "agent-name" argument and must reach IRunningAgentChatFactory.CreateAsync as the
        // separate nameOverride so it can be stamped onto AgentChat.Name.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, _) = CreateRouter(factory);

        await router.RouteAsync(LifecycleStart(
            agentId: "agent-1",
            parentToolCallId: "call-1",
            displayName: "General purpose",
            description: "desc",
            agentName: "fix-crash1142"));

        var overrides = Assert.Single(factory.CreateCallOverrides);
        Assert.Equal("fix-crash1142", overrides.NameOverride);
    }

    [Fact]
    public async Task SubAgentStarted_WithAgentNameAndDisplayName_KeepsBothDistinct()
    {
        // Fix #1151: the caller-supplied name is orthogonal to the type-level display name;
        // both must flow through independently to preserve the type label (Fix #1133) and the
        // invoker-chosen identity.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, _) = CreateRouter(factory);

        await router.RouteAsync(LifecycleStart(
            agentId: "agent-1",
            parentToolCallId: "call-1",
            displayName: "General purpose",
            description: "desc",
            agentName: "fix-crash1142"));

        var overrides = Assert.Single(factory.CreateCallOverrides);
        Assert.Equal("General purpose", overrides.DisplayNameOverride);
        Assert.Equal("fix-crash1142", overrides.NameOverride);
        Assert.NotEqual(overrides.DisplayNameOverride, overrides.NameOverride);
    }

    [Fact]
    public async Task SubAgentStarted_WithoutAgentName_FallsBackGracefully()
    {
        // Fix #1151 fallback: when the agent-name argument is absent or whitespace, the router
        // must pass null (not throw, not synthesize a fake value) so downstream UI can fall back
        // to DisplayName / session id.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, _) = CreateRouter(factory);

        await router.RouteAsync(LifecycleStart(
            agentId: "agent-1",
            parentToolCallId: "call-1",
            displayName: "General purpose",
            description: "desc",
            agentName: "   "));

        var overrides = Assert.Single(factory.CreateCallOverrides);
        Assert.Null(overrides.NameOverride);
    }

    // ─── Fix #1139 tests ─────────────────────────────────────────────────────────

    private static ChatResponseUpdate LifecycleStartWithoutAgentId(
        string parentToolCallId,
        string displayName = "Sub Agent",
        string description = "desc",
        string? agentName = null)
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
                [CopilotSdkStreamAdapter.AgentNameArgumentName] = agentName,
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

    // ─── Fix #1154 tests ─────────────────────────────────────────────────────────
    //
    // Router-level live-incrementality regression coverage: prove that content routed
    // to an attached child receiver reaches the receiver at PUSH time, not only when
    // a terminal/complete signal drains a pending buffer. We consume the receiver's
    // stream via an IAsyncEnumerator so we can observe items WITHOUT calling
    // .Complete() on the receiver — a mid-run visibility test that the #1139 suite
    // (which always DrainReceiverAsync-then-Complete's first) does not perform.

    private static async Task<ChatResponseUpdate> ReadOneFromReceiverAsync(
        CopilotSubAgentChatClient receiver,
        CancellationToken cancellationToken)
    {
        await foreach (var update in receiver.GetStreamingResponseAsync([], cancellationToken: cancellationToken))
        {
            return update;
        }

        throw new InvalidOperationException("Receiver stream completed without emitting any update.");
    }

    [Fact]
    public async Task RouteAsync_StartedOmitsAgentId_RunningContent_ForwardedLive_PendingEmpty()
    {
        // Fix #1154 router-level crux: with an AgentId-less start correlated at start
        // time, a content update tagged with the child AgentId reaches the ALREADY-
        // ATTACHED receiver immediately — never held in ChildRoutingEntry.pending until
        // some terminal signal drains it. We observe the receiver's stream WITHOUT
        // calling Complete(), so if the router were still buffering content until
        // completion, the read would time out.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, channel) = CreateRouter(factory);

        await router.RouteAsync(LifecycleStartWithoutAgentId("call-1"));

        var receiver = factory.CreatedReceiver!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var readTask = ReadOneFromReceiverAsync(receiver, cts.Token);

        // Content stamped with the child's runtime AgentId, distinct from any tool-call id.
        await router.RouteAsync(SubAgentText("child-runtime-id", "live text"));

        var update = await readTask;
        Assert.Equal("live text", update.Text);

        // Parent transcript stays empty.
        Assert.Empty(await DrainAsync(channel));
    }

    [Fact]
    public async Task RouteAsync_RunningContent_NotHeldUntilCompleteSignal()
    {
        // Fix #1154: content pushed before ANY terminal/complete signal reaches the child
        // receiver at push time; the receiver observes it prior to Complete(), confirming
        // updates are not released only when the sub-agent completes.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, channel) = CreateRouter(factory);

        await router.RouteAsync(LifecycleStart("agent-1", "call-1"));

        var receiver = factory.CreatedReceiver!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var readTask = ReadOneFromReceiverAsync(receiver, cts.Token);

        // No LifecycleCompleted, no Complete() on the receiver — content must still arrive
        // live because ChildRoutingEntry.Push forwards straight to the attached receiver.
        await router.RouteAsync(SubAgentText("agent-1", "live before complete"));

        var update = await readTask;
        Assert.Equal("live before complete", update.Text);

        // Parent transcript is untouched.
        Assert.Empty(await DrainAsync(channel));
    }

    // ─── Fix #1187: full, per-sub-agent AgentDefinition ─────────────────────────

    [Fact]
    public async Task CopilotSubAgentRouter_SubAgentDefinition_HasGithubCopilotSubagentProvider()
    {
        // Fix #1187: the definition the router hands to the factory always resolves to the
        // github-copilot-subagent provider so AgentFactory's provider fast-path fires
        // (composes with #912).
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, _) = CreateRouter(factory);

        await router.RouteAsync(LifecycleStart("agent-1187a", "call-1187a"));

        var call = Assert.Single(factory.CreateCalls);
        var promptAgent = Assert.IsType<PromptAgent>(call.Definition);
        Assert.Equal("github-copilot-subagent", promptAgent.Model?.Provider);
    }

    [Fact]
    public async Task CopilotSubAgentRouter_SubAgentDefinition_IsFullyPopulated()
    {
        // Fix #1187: hosted sub-agents are constructed from a full canonical definition —
        // non-null Model with a stable id, and non-empty Name/DisplayName — rather than the
        // pre-#1187 two-field synthetic that had no model.id, no name, no displayName.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, _) = CreateRouter(factory);

        await router.RouteAsync(LifecycleStart(
            agentId: "agent-1187b",
            parentToolCallId: "call-1187b",
            displayName: "Researcher",
            description: "Does research",
            agentName: "research-bot"));

        var call = Assert.Single(factory.CreateCalls);
        var promptAgent = Assert.IsType<PromptAgent>(call.Definition);
        Assert.NotNull(promptAgent.Model);
        Assert.False(string.IsNullOrEmpty(promptAgent.Model!.Id));
        Assert.False(string.IsNullOrEmpty(promptAgent.Name));
        Assert.False(string.IsNullOrEmpty(promptAgent.DisplayName));
    }

    [Fact]
    public async Task CopilotSubAgentRouter_SubAgentStarted_PassesPerSubAgentDefinition()
    {
        // Fix #1187: distinct SubAgentStarted events must produce distinct AgentDefinition
        // instances (per-sub-agent identity) — the previous implementation shared a single
        // static synthetic definition across every hosted sub-agent.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, _) = CreateRouter(factory);

        await router.RouteAsync(LifecycleStart(
            agentId: "agent-1187c1", parentToolCallId: "call-1187c1",
            displayName: "A", description: "a", agentName: "a"));
        await router.RouteAsync(LifecycleStart(
            agentId: "agent-1187c2", parentToolCallId: "call-1187c2",
            displayName: "B", description: "b", agentName: "b"));

        Assert.Equal(2, factory.CreateCalls.Count);
        Assert.NotSame(factory.CreateCalls[0].Definition, factory.CreateCalls[1].Definition);
    }

    // ─── Fix #1193: parent-interrupt terminalize-remaining-children ─────────────
    //
    // When the parent Copilot chat is interrupted, its SDK session is aborted before the
    // pending SubagentCompleted/SubagentFailed events are ever delivered. Nothing else on
    // the cancel path enumerates the router's ChildRoutingEntry bookkeeping, so running
    // sub-agent AgentChats stay non-terminal and the running-items UI never clears.
    // TerminalizeRemainingChildrenAsync sweeps both dictionaries and forces each entry to
    // Failed via ChildRoutingEntry.CompleteAsFailedAsync, which flips the child's
    // AgentChat.CompletionState through SetCompletionState.

    [Fact]
    public async Task TerminalizeRemainingChildrenAsync_WithRunningChildren_MarksEachAgentChatFailed()
    {
        // Fix #1193: after two ChildRoutingEntrys are registered via SubagentStarted lifecycles,
        // calling TerminalizeRemainingChildrenAsync must transition every associated child
        // AgentChat.CompletionState to Failed — mirroring the terminal state a real
        // SubagentFailed event would have produced.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var table = new SubAgentTestFakes.FakeSubAgentTable();
        var (router, _) = CreateRouter(factory, table);

        await router.RouteAsync(LifecycleStart("agent-1", "call-1"));
        await router.RouteAsync(LifecycleStart("agent-2", "call-2"));

        Assert.Equal(2, table.AddedChats.Count);
        Assert.All(table.AddedChats, chat => Assert.Equal(AgentChatCompletionState.Running, chat.CompletionState));

        await router.TerminalizeRemainingChildrenAsync(new OperationCanceledException("parent interrupted"));

        Assert.All(table.AddedChats, chat => Assert.Equal(AgentChatCompletionState.Failed, chat.CompletionState));
    }

    [Fact]
    public async Task TerminalizeRemainingChildrenAsync_ClearsChildSinksAndPending()
    {
        // Fix #1193: after termination, both childSinks and pendingChildSinksByToolCall are
        // empty so a subsequent RouteAsync for the same agent id misses cleanly (the router
        // logs a warning and returns without throwing). This guarantees a late queued
        // SubagentCompleted arriving after the abort does not re-attach state.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var (router, channel) = CreateRouter(factory);

        await router.RouteAsync(LifecycleStart("agent-1", "call-1"));
        // AgentId-less start parks the entry in pendingChildSinksByToolCall.
        await router.RouteAsync(LifecycleStartWithoutAgentId("call-2"));

        await router.TerminalizeRemainingChildrenAsync(new OperationCanceledException("parent interrupted"));

        // Late completion for agent-1 finds no entry and returns without throwing.
        await router.RouteAsync(LifecycleCompleted("agent-1"));
        // Late completion for the AgentId-less start also finds nothing — the pending entry
        // was terminalized and cleared, and pendingChildSinksToolCallOrder is empty so
        // TryAdoptPendingSinkForAgentId returns false.
        await router.RouteAsync(LifecycleCompleted("child-runtime-id"));

        // Parent transcript is untouched by any of this.
        Assert.Empty(await DrainAsync(channel));
    }

    [Fact]
    public async Task RouteAsync_LateCompletionAfterTerminalize_DoesNotDoubleTerminalize()
    {
        // Fix #1193 idempotency: after TerminalizeRemainingChildrenAsync flips the child's
        // AgentChat to Failed and raises CompletionStateChanged once, a late queued
        // SubagentCompleted for the same agent id must not fire CompletionStateChanged
        // again — SetCompletionState's equality guard already handles this, and the router's
        // dictionary clear makes the late event a lookup-miss no-op.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var table = new SubAgentTestFakes.FakeSubAgentTable();
        var (router, _) = CreateRouter(factory, table);

        await router.RouteAsync(LifecycleStart("agent-1", "call-1"));
        var childChat = Assert.Single(table.AddedChats);

        var terminalTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventCount = 0;
        childChat.CompletionStateChanged += (_, _) =>
        {
            Interlocked.Increment(ref eventCount);
            if (childChat.CompletionState != AgentChatCompletionState.Running)
            {
                terminalTcs.TrySetResult();
            }
        };

        await router.TerminalizeRemainingChildrenAsync(new OperationCanceledException("parent interrupted"));

        // Wait until the CompletionStateChanged for the terminalize sweep has been marshaled
        // onto the child's foreground scheduler.
        await terminalTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AgentChatCompletionState.Failed, childChat.CompletionState);
        var terminalCount = Volatile.Read(ref eventCount);
        Assert.Equal(1, terminalCount);

        // A late queued SubagentCompleted for the same agent must not throw and must not
        // re-fire CompletionStateChanged.
        await router.RouteAsync(LifecycleCompleted("agent-1"));

        // Give the scheduler a chance to raise any event that would have been raised.
        await Task.Yield();
        await Task.Yield();
        Assert.Equal(AgentChatCompletionState.Failed, childChat.CompletionState);
        Assert.Equal(terminalCount, Volatile.Read(ref eventCount));
    }

    [Fact]
    public async Task RouteAsync_LifecycleCompleted_WithoutInterrupt_StillCompletesChildReceiver()
    {
        // Fix #1193 regression guard: the normal (non-interrupted) path still runs
        // HandleSubAgentResultAsync → ChildRoutingEntry.CompleteAsync → child
        // AgentChat.SetCompletionState(Succeeded). Adding TerminalizeRemainingChildrenAsync
        // must not disturb this path when no cancellation occurs.
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        var table = new SubAgentTestFakes.FakeSubAgentTable();
        var (router, channel) = CreateRouter(factory, table);

        await router.RouteAsync(LifecycleStart("agent-1", "call-1"));
        await router.RouteAsync(LifecycleCompleted("agent-1"));

        var childChat = Assert.Single(table.AddedChats);
        Assert.Equal(AgentChatCompletionState.Succeeded, childChat.CompletionState);

        // Child receiver's stream terminates gracefully and the parent transcript is empty.
        Assert.Empty(await DrainAsync(channel));
        var updates = await DrainReceiverAsync(factory.CreatedReceiver!);
    }
}
