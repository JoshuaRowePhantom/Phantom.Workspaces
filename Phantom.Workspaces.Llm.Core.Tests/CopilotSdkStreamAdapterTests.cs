using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using GitHub.Copilot.SDK;
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
    public async Task TranslateCopilotSdkSessionEvents_SubagentStartedEventWithoutAgentId_UsesToolCallIdAsId()
    {
        var updates = await TranslateAsync(new SubagentStartedEvent
        {
            AgentId = string.Empty,
            Data = new SubagentStartedData { ToolCallId = "call-9", AgentName = "sub_agent", AgentDisplayName = "Sub Agent", AgentDescription = "desc" },
        });

        var update = Assert.Single(updates);
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(update.Contents));
        Assert.Equal("call-9", call.CallId);
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
    public async Task TranslateCopilotSdkSessionEvents_UnknownEvent_Dropped()
    {
        var updates = await TranslateAsync(
            new AssistantTurnStartEvent { Data = new AssistantTurnStartData { TurnId = "1", InteractionId = "i-1" } },
            DeltaEvent(string.Empty, "still works"));

        var update = Assert.Single(updates);
        var text = Assert.IsType<TextContent>(Assert.Single(update.Contents));
        Assert.Equal("still works", text.Text);
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
}
