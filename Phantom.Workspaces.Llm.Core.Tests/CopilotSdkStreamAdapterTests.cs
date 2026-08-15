using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Tests for <see cref="CopilotSdkStreamAdapter"/>: pure translation of raw Copilot SDK session
/// events to routed <see cref="ChatResponseUpdate"/> items (issue #808 / #866).
/// </summary>
public sealed class CopilotSdkStreamAdapterTests
{
    private static async Task<List<ChatResponseUpdate>> TranslateAsync(params SessionEvent[] events)
    {
        var channel = Channel.CreateUnbounded<SessionEvent>();
        foreach (var sessionEvent in events)
        {
            channel.Writer.TryWrite(sessionEvent);
        }

        channel.Writer.Complete();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in CopilotSdkStreamAdapter.TranslateCopilotSdkSessionEvents(channel.Reader, CancellationToken.None))
        {
            updates.Add(update);
        }

        return updates;
    }

    private static AssistantMessageDeltaEvent DeltaEvent(string agentId, string text) =>
        new AssistantMessageDeltaEvent
        {
            AgentId = agentId,
            Data = new AssistantMessageDeltaData { DeltaContent = text, MessageId = "msg-1" },
        };

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_RootAssistantDelta_YieldsUntaggedTextContent()
    {
        var updates = await TranslateAsync(DeltaEvent(string.Empty, "hello"));

        var update = Assert.Single(updates);
        Assert.Equal(ChatRole.Assistant, update.Role);
        var text = Assert.IsType<TextContent>(Assert.Single(update.Contents));
        Assert.Equal("hello", text.Text);
        Assert.Null(CopilotSdkStreamAdapter.GetParentToolCallId(text));
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_SubAgentAssistantDelta_TaggedWithAgentId()
    {
        var updates = await TranslateAsync(DeltaEvent("agent-1", "sub hello"));

        var update = Assert.Single(updates);
        var text = Assert.IsType<TextContent>(Assert.Single(update.Contents));
        Assert.Equal("agent-1", CopilotSdkStreamAdapter.GetParentToolCallId(text));
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_EmptyAssistantDelta_Dropped()
    {
        var updates = await TranslateAsync(DeltaEvent(string.Empty, string.Empty));

        Assert.Empty(updates);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_SubAgentReasoningDelta_TaggedWithAgentId()
    {
        // Issue #808 bug fix: the old dispatcher hardcoded reasoning deltas to the root agent.
        var updates = await TranslateAsync(new AssistantReasoningDeltaEvent
        {
            AgentId = "agent-1",
            Data = new AssistantReasoningDeltaData { DeltaContent = "thinking...", ReasoningId = "r-1" },
        });

        var update = Assert.Single(updates);
        Assert.Equal(ChatRole.Assistant, update.Role);
        var reasoning = Assert.IsType<TextReasoningContent>(Assert.Single(update.Contents));
        Assert.Equal("thinking...", reasoning.Text);
        Assert.Equal("agent-1", CopilotSdkStreamAdapter.GetParentToolCallId(reasoning));
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_ToolStartEvent_YieldsAssistantFunctionCall()
    {
        var updates = await TranslateAsync(new ToolExecutionStartEvent
        {
            AgentId = string.Empty,
            Data = new ToolExecutionStartData { ToolCallId = "call-1", ToolName = "my_tool" },
        });

        var update = Assert.Single(updates);
        Assert.Equal(ChatRole.Assistant, update.Role);
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(update.Contents));
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("my_tool", call.Name);
        Assert.Null(CopilotSdkStreamAdapter.GetParentToolCallId(call));
        Assert.False(CopilotSdkStreamAdapter.IsSubAgentLifecycleContent(call));
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_SubAgentToolStartEvent_TaggedWithAgentId()
    {
        var updates = await TranslateAsync(new ToolExecutionStartEvent
        {
            AgentId = "agent-1",
            Data = new ToolExecutionStartData { ToolCallId = "call-2", ToolName = "child_tool" },
        });

        var update = Assert.Single(updates);
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(update.Contents));
        Assert.Equal("agent-1", CopilotSdkStreamAdapter.GetParentToolCallId(call));
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_ToolCompleteEvent_YieldsToolRoleFunctionResult()
    {
        var updates = await TranslateAsync(new ToolExecutionCompleteEvent
        {
            AgentId = string.Empty,
            Data = new ToolExecutionCompleteData
            {
                ToolCallId = "call-1",
                Success = true,
                Result = new ToolExecutionCompleteResult { Content = "ok" },
            },
        });

        var update = Assert.Single(updates);
        Assert.Equal(ChatRole.Tool, update.Role);
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(update.Contents));
        Assert.Equal("call-1", result.CallId);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_SubagentStartedEvent_YieldsLifecycleFunctionCall()
    {
        var updates = await TranslateAsync(new SubagentStartedEvent
        {
            AgentId = "agent-1",
            Data = new SubagentStartedData
            {
                ToolCallId = "call-1",
                AgentName = "sub_agent",
                AgentDisplayName = "Sub Agent",
                AgentDescription = "desc",
            },
        });

        var update = Assert.Single(updates);
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(update.Contents));
        Assert.True(CopilotSdkStreamAdapter.IsSubAgentLifecycleContent(call));
        Assert.Equal(CopilotSdkStreamAdapter.SubAgentStartLifecycleName, call.Name);
        Assert.Equal("agent-1", call.CallId);
        Assert.Equal("call-1", call.Arguments![CopilotSdkStreamAdapter.ParentToolCallIdArgumentName]);
        Assert.Equal("Sub Agent", call.Arguments[CopilotSdkStreamAdapter.DisplayNameArgumentName]);
        Assert.Equal("desc", call.Arguments[CopilotSdkStreamAdapter.DescriptionArgumentName]);
    }

    [Fact]
    public async Task SubagentStarted_WithAgentName_PacksAgentNameArgument()
    {
        // Fix #1151: the caller-supplied SubagentStartedData.AgentName ("fix-crash1142" etc.) must
        // ride the lifecycle-start FunctionCallContent under the new agent-name argument so the
        // router (and downstream UI) can distinguish it from the type-level display name.
        var updates = await TranslateAsync(new SubagentStartedEvent
        {
            AgentId = "agent-1",
            Data = new SubagentStartedData
            {
                ToolCallId = "call-1",
                AgentName = "fix-crash1142",
                AgentDisplayName = "General purpose",
                AgentDescription = "desc",
            },
        });

        var call = Assert.IsType<FunctionCallContent>(Assert.Single(Assert.Single(updates).Contents));
        Assert.Equal("fix-crash1142", call.Arguments![CopilotSdkStreamAdapter.AgentNameArgumentName]);
        // AgentName must be independent of the type-level display name.
        Assert.Equal("General purpose", call.Arguments[CopilotSdkStreamAdapter.DisplayNameArgumentName]);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_SubagentStartedWithoutAgentId_ExposesSpawningToolCallIdForCorrelation()
    {
        // Fix #1139: when SubagentStartedEvent lacks a child AgentId (root-parent spawn case),
        // the adapter surfaces the spawning tool-call id ONLY through the
        // ParentToolCallIdArgumentName argument (for start-time correlation in the router) —
        // it is NEVER silently promoted to the lifecycle CallId (which is the routing key)
        // because that would key the sink under the wrong identity and diverge from where the
        // child's runtime AgentId-tagged content eventually lands.
        var updates = await TranslateAsync(new SubagentStartedEvent
        {
            AgentId = string.Empty,
            Data = new SubagentStartedData { ToolCallId = "call-9", AgentName = "sub_agent", AgentDisplayName = "Sub Agent", AgentDescription = "desc" },
        });

        var update = Assert.Single(updates);
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(update.Contents));
        Assert.NotEqual("call-9", call.CallId);
        Assert.Equal(string.Empty, call.CallId);
        Assert.Equal("call-9", call.Arguments![CopilotSdkStreamAdapter.ParentToolCallIdArgumentName]);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_SubagentStartedWithoutAgentIdOrToolCallId_Dropped()
    {
        // Nothing left to correlate against: no lifecycle content emitted.
        var updates = await TranslateAsync(new SubagentStartedEvent
        {
            AgentId = string.Empty,
            Data = new SubagentStartedData { ToolCallId = string.Empty, AgentName = "sub_agent", AgentDisplayName = "Sub Agent", AgentDescription = "desc" },
        });

        Assert.Empty(updates);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_SubagentStartedWithAgentId_LifecycleKeyedByChildAgentId()
    {
        // Fix #1139: when SubagentStartedEvent carries the child AgentId, the lifecycle CallId
        // (which becomes the sink key) is the child AgentId, and the spawning tool-call id is
        // preserved only in the lifecycle argument. Content is tagged with the same AgentId,
        // so start and content resolve to the same childSinks entry by construction.
        var updates = await TranslateAsync(new SubagentStartedEvent
        {
            AgentId = "child-a",
            Data = new SubagentStartedData
            {
                ToolCallId = "call-x",
                AgentName = "sub_agent",
                AgentDisplayName = "Sub Agent",
                AgentDescription = "desc",
            },
        });

        var update = Assert.Single(updates);
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(update.Contents));
        Assert.Equal("child-a", call.CallId);
        Assert.NotEqual("call-x", call.CallId);
        Assert.Equal("call-x", call.Arguments![CopilotSdkStreamAdapter.ParentToolCallIdArgumentName]);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_SubAgentReasoningDelta_TaggedWithEventAgentId()
    {
        // Fix #1139: AssistantReasoningDeltaData has NO ParentToolCallId field at all, but the
        // event-level AgentId is populated and drives routing exactly like every other sub-
        // agent event. Content routing must not depend on the deprecated per-Data member.
        var updates = await TranslateAsync(new AssistantReasoningDeltaEvent
        {
            AgentId = "child-a",
            Data = new AssistantReasoningDeltaData { DeltaContent = "thinking...", ReasoningId = "r-1" },
        });

        var update = Assert.Single(updates);
        var reasoning = Assert.IsType<TextReasoningContent>(Assert.Single(update.Contents));
        Assert.Equal("child-a", CopilotSdkStreamAdapter.GetParentToolCallId(reasoning));
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_SubAgentContent_DoesNotReadParentToolCallId()
    {
        // Fix #1139: content tagging reads ONLY the event-level AgentId. When AgentId is
        // absent the tag is null even if the deprecated Data.ParentToolCallId would have
        // supplied a value (which we intentionally do not consult — it is [Obsolete]
        // GHCP001 and slated for removal from the SDK).
        var updatesWithAgentId = await TranslateAsync(new AssistantMessageDeltaEvent
        {
            AgentId = "child-a",
            Data = new AssistantMessageDeltaData { DeltaContent = "hi", MessageId = "msg-1" },
        });
        var withAgentId = Assert.IsType<TextContent>(Assert.Single(Assert.Single(updatesWithAgentId).Contents));
        Assert.Equal("child-a", CopilotSdkStreamAdapter.GetParentToolCallId(withAgentId));

        var updatesNoAgentId = await TranslateAsync(new AssistantMessageDeltaEvent
        {
            AgentId = string.Empty,
            Data = new AssistantMessageDeltaData { DeltaContent = "hi", MessageId = "msg-1" },
        });
        var noAgentId = Assert.IsType<TextContent>(Assert.Single(Assert.Single(updatesNoAgentId).Contents));
        Assert.Null(CopilotSdkStreamAdapter.GetParentToolCallId(noAgentId));
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_SubagentStartedEventWithoutAgentId_UsesToolCallIdAsId()
    {
        // Legacy/back-compat guard: prior to Fix #1139 the adapter promoted the spawning
        // tool-call id into the lifecycle CallId when AgentId was missing. That behaviour is
        // gone (see TranslateCopilotSdkSessionEvents_SubagentStartedWithoutAgentId_ExposesSpawningToolCallIdForCorrelation).
        // This test now asserts the CORRECTED behaviour so a future regression does not
        // silently re-introduce the sink-key/content-key mismatch that produced the drop.
        var updates = await TranslateAsync(new SubagentStartedEvent
        {
            AgentId = string.Empty,
            Data = new SubagentStartedData { ToolCallId = "call-9", AgentName = "sub_agent", AgentDisplayName = "Sub Agent", AgentDescription = "desc" },
        });

        var update = Assert.Single(updates);
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(update.Contents));
        Assert.NotEqual("call-9", call.CallId);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_SubagentCompletedEvent_YieldsCompletedLifecycleResult()
    {
        var updates = await TranslateAsync(new SubagentCompletedEvent
        {
            AgentId = "agent-1",
            Data = new SubagentCompletedData { ToolCallId = "call-1", AgentName = "sub_agent", AgentDisplayName = "Sub Agent" },
        });

        var update = Assert.Single(updates);
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(update.Contents));
        Assert.True(CopilotSdkStreamAdapter.IsSubAgentLifecycleContent(result));
        Assert.Equal("agent-1", result.CallId);
        Assert.Contains("\"completed\"", result.Result as string);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_SubagentFailedEvent_YieldsFailedLifecycleResultWithError()
    {
        var updates = await TranslateAsync(new SubagentFailedEvent
        {
            AgentId = "agent-1",
            Data = new SubagentFailedData { ToolCallId = "call-1", AgentName = "sub_agent", AgentDisplayName = "Sub Agent", Error = "boom" },
        });

        var update = Assert.Single(updates);
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(update.Contents));
        Assert.True(CopilotSdkStreamAdapter.IsSubAgentLifecycleContent(result));
        var json = Assert.IsType<string>(result.Result);
        Assert.Contains("\"failed\"", json);
        Assert.Contains("boom", json);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_UsageEvent_MapsTokenCounts()
    {
        var updates = await TranslateAsync(new AssistantUsageEvent
        {
            AgentId = string.Empty,
            Data = new AssistantUsageData
            {
                Model = "test-model",
                InputTokens = 100,
                OutputTokens = 40,
                ReasoningTokens = 7,
                CacheReadTokens = 3,
            },
        });

        var update = Assert.Single(updates);
        var usage = Assert.IsType<UsageContent>(Assert.Single(update.Contents));
        Assert.Equal(100, usage.Details.InputTokenCount);
        Assert.Equal(40, usage.Details.OutputTokenCount);
        Assert.Equal(140, usage.Details.TotalTokenCount);
        Assert.Equal(7, usage.Details.AdditionalCounts![CopilotSdkStreamAdapter.ReasoningTokensCountName]);
        Assert.Equal(3, usage.Details.AdditionalCounts[CopilotSdkStreamAdapter.CacheReadTokensCountName]);
        Assert.False(usage.Details.AdditionalCounts.ContainsKey(CopilotSdkStreamAdapter.CacheWriteTokensCountName));
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_UsageEventWithCost_PopulatesCostMicroUsdAdditionalCount()
    {
#pragma warning disable GHCP001 // AssistantUsageData.Cost is evaluation-only.
        var updates = await TranslateAsync(new AssistantUsageEvent
        {
            AgentId = string.Empty,
            Data = new AssistantUsageData
            {
                Model = "test-model",
                InputTokens = 100,
                OutputTokens = 40,
                Cost = 1.23,
            },
        });
#pragma warning restore GHCP001

        var update = Assert.Single(updates);
        var usage = Assert.IsType<UsageContent>(Assert.Single(update.Contents));
        Assert.Equal(1_230_000, usage.Details.AdditionalCounts![CopilotSdkStreamAdapter.CostMicroUsdCountName]);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_UsageEventWithoutCost_OmitsCostAdditionalCount()
    {
        var updates = await TranslateAsync(new AssistantUsageEvent
        {
            AgentId = string.Empty,
            Data = new AssistantUsageData
            {
                Model = "test-model",
                InputTokens = 100,
                OutputTokens = 40,
            },
        });

        var update = Assert.Single(updates);
        var usage = Assert.IsType<UsageContent>(Assert.Single(update.Contents));
        Assert.True(
            usage.Details.AdditionalCounts is null
            || !usage.Details.AdditionalCounts.ContainsKey(CopilotSdkStreamAdapter.CostMicroUsdCountName));
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_UsageEvent_MapsCacheReadAndWriteTokens()
    {
        var updates = await TranslateAsync(new AssistantUsageEvent
        {
            AgentId = string.Empty,
            Data = new AssistantUsageData
            {
                Model = "test-model",
                InputTokens = 100,
                OutputTokens = 40,
                CacheReadTokens = 30,
                CacheWriteTokens = 12,
            },
        });

        var update = Assert.Single(updates);
        var usage = Assert.IsType<UsageContent>(Assert.Single(update.Contents));
        Assert.Equal(30, usage.Details.AdditionalCounts![CopilotSdkStreamAdapter.CacheReadTokensCountName]);
        Assert.Equal(12, usage.Details.AdditionalCounts[CopilotSdkStreamAdapter.CacheWriteTokensCountName]);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_UsageEventWithNullCounts_YieldsEmptyDetails()
    {
        var updates = await TranslateAsync(new AssistantUsageEvent
        {
            AgentId = string.Empty,
            Data = new AssistantUsageData { Model = "test-model" },
        });

        var update = Assert.Single(updates);
        var usage = Assert.IsType<UsageContent>(Assert.Single(update.Contents));
        Assert.Null(usage.Details.InputTokenCount);
        Assert.Null(usage.Details.OutputTokenCount);
        Assert.Null(usage.Details.TotalTokenCount);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_SystemNotificationEvent_StripsTagsAndMarksContentType()
    {
        var updates = await TranslateAsync(new SystemNotificationEvent
        {
            AgentId = string.Empty,
            Data = new SystemNotificationData
            {
                Content = "<system_notification>Context is running low.</system_notification>",
                Kind = new SystemNotificationAgentIdle
                {
                    AgentId = "agent-1",
                    AgentType = "background",
                    Description = "idle",
                },
            },
        });

        var update = Assert.Single(updates);
        Assert.Equal(ChatRole.System, update.Role);
        var text = Assert.IsType<TextContent>(Assert.Single(update.Contents));
        Assert.Equal("Context is running low.", text.Text);
        Assert.Equal(
            CopilotSdkStreamAdapter.SystemNotificationContentType,
            text.AdditionalProperties![CopilotSdkStreamAdapter.ContentTypePropertyName]);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_SessionIdleEvent_CompletesStream()
    {
        var updates = await TranslateAsync(
            DeltaEvent(string.Empty, "before idle"),
            new SessionIdleEvent { Data = new SessionIdleData { Aborted = false } },
            DeltaEvent(string.Empty, "after idle"));

        Assert.Equal(2, updates.Count);
        var text = Assert.IsType<TextContent>(Assert.Single(updates[0].Contents));
        Assert.Equal("before idle", text.Text);
        Assert.Equal(ChatFinishReason.Stop, updates[1].FinishReason);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_SessionIdleEvent_EmitsTerminalFinishReason()
    {
        // Issue #1103: on SessionIdleEvent the adapter must emit a terminal ChatResponseUpdate
        // carrying FinishReason so StreamingPersistenceMiddleware treats the response as final
        // and persists every assistant message from the turn (including the last one).
        var updates = await TranslateAsync(
            new SessionIdleEvent { Data = new SessionIdleData { Aborted = false } });

        var update = Assert.Single(updates);
        Assert.Equal(ChatFinishReason.Stop, update.FinishReason);
        Assert.Equal(ChatRole.Assistant, update.Role);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_NoToolTurn_FinalDeltaFollowedByTerminalFinishReason()
    {
        // Issue #1103: a plain no-tool turn is a single assistant text delta then SessionIdle.
        // The last emitted update must carry a FinishReason so the response is treated as final
        // and the sole assistant message is persisted (previously it was dropped as "unstable").
        var updates = await TranslateAsync(
            DeltaEvent(string.Empty, "hello world"),
            new SessionIdleEvent { Data = new SessionIdleData { Aborted = false } });

        Assert.Equal(2, updates.Count);
        Assert.Null(updates[0].FinishReason);
        Assert.Equal(ChatFinishReason.Stop, updates[1].FinishReason);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_ToolTurn_TerminalFinishReasonEmittedAfterToolEvents()
    {
        // Issue #1103: tool-invoking turns must still finalize with a terminal FinishReason so
        // that once the tool executes and the model finalises, the response is persisted.
        var toolStart = new ToolExecutionStartEvent
        {
            AgentId = string.Empty,
            Data = new ToolExecutionStartData
            {
                ToolCallId = "call-1",
                ToolName = "search",
            },
        };
        var toolComplete = new ToolExecutionCompleteEvent
        {
            AgentId = string.Empty,
            Data = new ToolExecutionCompleteData
            {
                ToolCallId = "call-1",
                Success = true,
                Result = new ToolExecutionCompleteResult { Content = "ok" },
            },
        };

        var updates = await TranslateAsync(
            toolStart,
            toolComplete,
            DeltaEvent(string.Empty, "tool used"),
            new SessionIdleEvent { Data = new SessionIdleData { Aborted = false } });

        Assert.Equal(4, updates.Count);
        Assert.Equal(ChatFinishReason.Stop, updates[^1].FinishReason);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_EmptyTurn_TerminalFinishReasonStillEmitted()
    {
        // Issue #1103: even an empty/no-delta turn (SessionIdleEvent as the only event) must
        // finalize with a terminal FinishReason so downstream middleware doesn't stall.
        var updates = await TranslateAsync(
            new SessionIdleEvent { Data = new SessionIdleData { Aborted = false } });

        var update = Assert.Single(updates);
        Assert.Equal(ChatFinishReason.Stop, update.FinishReason);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_SessionErrorEvent_ThrowsInvalidOperationException()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TranslateAsync(new SessionErrorEvent
            {
                Data = new SessionErrorData { Message = "kaboom", ErrorType = "fatal" },
            }));

        Assert.Contains("kaboom", exception.Message);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_UnknownEventKind_IsSurfacedNotDropped()
    {
        // Fix #1312: previously unmapped SDK event kinds were silently dropped because the switch
        // had no default arm. Regression guard: an event kind not in the explicit case arms must
        // now be surfaced as a generic informational update tagged with
        // UnknownCopilotSdkEventContentType, and translation of subsequent events must continue.
        var updates = await TranslateAsync(
            new AssistantTurnStartEvent { Data = new AssistantTurnStartData { TurnId = "1", InteractionId = "i-1" } },
            DeltaEvent(string.Empty, "still works"));

        Assert.Equal(2, updates.Count);
        var unknown = Assert.IsType<TextContent>(Assert.Single(updates[0].Contents));
        Assert.Equal(
            CopilotSdkStreamAdapter.UnknownCopilotSdkEventContentType,
            unknown.AdditionalProperties![CopilotSdkStreamAdapter.ContentTypePropertyName]);
        Assert.Contains(nameof(AssistantTurnStartEvent), unknown.Text, StringComparison.Ordinal);

        var followingDelta = Assert.IsType<TextContent>(Assert.Single(updates[1].Contents));
        Assert.Equal("still works", followingDelta.Text);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_UnknownEventKindWithAgentId_TagsWithAgentId()
    {
        // Fix #1312: sub-agent-tagged unknown events must carry the AgentId through
        // ParentToolCallIdPropertyName so the router places them under the correct sub-agent sink.
        var updates = await TranslateAsync(
            new AssistantTurnStartEvent
            {
                AgentId = "agent-42",
                Data = new AssistantTurnStartData { TurnId = "1", InteractionId = "i-1" },
            });

        var content = Assert.IsType<TextContent>(Assert.Single(Assert.Single(updates).Contents));
        Assert.Equal("agent-42", CopilotSdkStreamAdapter.GetParentToolCallId(content));
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_UnknownEventKind_LogsWarningWithKindAndAgentId()
    {
        // Fix #1312: the default arm must log at Warning with the runtime event type name and the
        // AgentId so future SDK additions are diagnosable from logs.
        var logger = new RecordingLogger();
        var channel = Channel.CreateUnbounded<SessionEvent>();
        channel.Writer.TryWrite(new AssistantTurnStartEvent
        {
            AgentId = "agent-42",
            Data = new AssistantTurnStartData { TurnId = "1", InteractionId = "i-1" },
        });
        channel.Writer.Complete();

        await foreach (var _ in CopilotSdkStreamAdapter.TranslateCopilotSdkSessionEvents(
                           channel.Reader, logger, CancellationToken.None))
        {
        }

        var warning = Assert.Single(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Contains(nameof(AssistantTurnStartEvent), warning.Message, StringComparison.Ordinal);
        Assert.Contains("agent-42", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_UnknownEventKindRootAgent_LogsRootMarker()
    {
        // Fix #1312: root-agent unknown events (AgentId null/empty) must still be logged with a
        // stable marker so the log entry is diagnosable.
        var logger = new RecordingLogger();
        var channel = Channel.CreateUnbounded<SessionEvent>();
        channel.Writer.TryWrite(new AssistantTurnStartEvent
        {
            AgentId = string.Empty,
            Data = new AssistantTurnStartData { TurnId = "1", InteractionId = "i-1" },
        });
        channel.Writer.Complete();

        await foreach (var _ in CopilotSdkStreamAdapter.TranslateCopilotSdkSessionEvents(
                           channel.Reader, logger, CancellationToken.None))
        {
        }

        var warning = Assert.Single(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Contains("<root>", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>Records log entries for #1312 default-arm assertions.</summary>
    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            this.Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_MultipleEvents_PreservesOrder()
    {
        var updates = await TranslateAsync(
            DeltaEvent(string.Empty, "one"),
            DeltaEvent("agent-1", "two"),
            DeltaEvent(string.Empty, "three"));

        Assert.Equal(3, updates.Count);
        var texts = updates.Select(u => ((TextContent)u.Contents.Single()).Text).ToList();
        Assert.Equal(["one", "two", "three"], texts);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_ToolExecutionStartEventRootAgentId_EmitsUpdateWithoutParentToolCallIdProperty()
    {
        // Fix #1318: a ToolExecutionStartEvent with AgentId == null must map to a
        // ChatResponseUpdate whose FunctionCallContent does NOT carry
        // ParentToolCallIdPropertyName, so CopilotSubAgentRouter routes it to the root/session
        // sink where it renders as a child of the SDK session node in AgentChat history.
        var updates = await TranslateAsync(new ToolExecutionStartEvent
        {
            AgentId = null,
            Data = new ToolExecutionStartData { ToolCallId = "sh-1", ToolName = "powershell" },
        });

        var call = Assert.IsType<FunctionCallContent>(Assert.Single(Assert.Single(updates).Contents));
        Assert.Null(CopilotSdkStreamAdapter.GetParentToolCallId(call));
        Assert.Equal("powershell", call.Name);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_ToolExecutionCompleteEventRootAgentId_EmitsUpdateWithoutParentToolCallIdProperty()
    {
        // Fix #1318: mirror of the above for ToolExecutionCompleteEvent — root-AgentId completes
        // land on the root/session sink.
        var updates = await TranslateAsync(new ToolExecutionCompleteEvent
        {
            AgentId = null,
            Data = new ToolExecutionCompleteData
            {
                ToolCallId = "sh-1",
                Success = true,
                Result = new ToolExecutionCompleteResult { Content = "ok" },
            },
        });

        var result = Assert.IsType<FunctionResultContent>(Assert.Single(Assert.Single(updates).Contents));
        Assert.Null(CopilotSdkStreamAdapter.GetParentToolCallId(result));
        Assert.Equal("sh-1", result.CallId);
    }

    [Fact]
    public async Task TranslateCopilotSdkSessionEvents_ToolExecutionEventsWithAgentId_SetParentToolCallIdProperty()
    {
        // Fix #1318 regression guard: sub-agent-tagged tool events must still carry
        // ParentToolCallIdPropertyName so the router forwards them to the correct sub-agent sink.
        var updates = await TranslateAsync(
            new ToolExecutionStartEvent
            {
                AgentId = "agent-77",
                Data = new ToolExecutionStartData { ToolCallId = "call-77", ToolName = "my_tool" },
            },
            new ToolExecutionCompleteEvent
            {
                AgentId = "agent-77",
                Data = new ToolExecutionCompleteData
                {
                    ToolCallId = "call-77",
                    Success = true,
                    Result = new ToolExecutionCompleteResult { Content = "ok" },
                },
            });

        Assert.Equal(2, updates.Count);
        Assert.Equal("agent-77", CopilotSdkStreamAdapter.GetParentToolCallId(Assert.Single(updates[0].Contents)));
        Assert.Equal("agent-77", CopilotSdkStreamAdapter.GetParentToolCallId(Assert.Single(updates[1].Contents)));
    }
}
