using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Pure translation layer from raw GitHub Copilot SDK session events to
/// <see cref="ChatResponseUpdate"/> items (issue #808). Routing is encoded on each content item via
/// <see cref="ParentToolCallIdPropertyName"/> in <see cref="AIContent.AdditionalProperties"/>
/// (absent = root agent), and sub-agent lifecycle transitions are expressed with the existing
/// <see cref="FunctionCallContent"/>/<see cref="FunctionResultContent"/> types so they can also
/// serve as a wire format. This class has no knowledge of <c>AgentChat</c>,
/// <c>IRunningAgentChatFactory</c>, <c>ISubAgentTable</c>, or any other AgentChat-world type;
/// interpreting the stream is <see cref="CopilotSubAgentRouter"/>'s job.
/// </summary>
public static class CopilotSdkStreamAdapter
{
    /// <summary>
    /// <see cref="AIContent.AdditionalProperties"/> key carrying the originating sub-agent ID.
    /// Absent on root-agent content.
    /// </summary>
    public const string ParentToolCallIdPropertyName = "copilot.sdk.parent_tool_call_id";

    /// <summary>
    /// <see cref="AIContent.AdditionalProperties"/> key describing special content kinds
    /// (<see cref="SystemNotificationContentType"/>, <see cref="SubAgentLifecycleContentType"/>).
    /// </summary>
    public const string ContentTypePropertyName = "copilot.sdk.content_type";

    /// <summary>Content-type value for translated <see cref="SystemNotificationEvent"/> items.</summary>
    public const string SystemNotificationContentType = "system_notification";

    /// <summary>Content-type value marking sub-agent lifecycle signals.</summary>
    public const string SubAgentLifecycleContentType = "subagent_lifecycle";

    /// <summary>
    /// Marker constant retained from #1312 for identifying the "unmapped Copilot SDK session event"
    /// classification in logs and tests. Fix #1323: the default arm no longer emits a
    /// user-visible <see cref="TextContent"/> carrying this marker; it logs at
    /// <see cref="LogLevel.Debug"/> only. Emitting the marker as transcript text spammed the
    /// user-visible chat and persisted history one placeholder per high-frequency SDK ping
    /// (e.g. <c>AssistantStreamingDeltaEvent</c>).
    /// </summary>
    public const string UnknownCopilotSdkEventContentType = "unknown-copilot-sdk-event";

    /// <summary>
    /// <see cref="FunctionCallContent.Name"/> of the sub-agent-started lifecycle signal.
    /// </summary>
    public const string SubAgentStartLifecycleName = "copilot.subagent.start";

    /// <summary>Lifecycle-start argument: the root tool call that spawned the sub-agent.</summary>
    public const string ParentToolCallIdArgumentName = "parent-tool-call-id";

    /// <summary>Lifecycle-start argument: the sub-agent's display name.</summary>
    public const string DisplayNameArgumentName = "display-name";

    /// <summary>
    /// Lifecycle-start argument: the caller-supplied sub-agent name/id (issue #1151), which
    /// maps to <c>SubagentStartedData.AgentName</c>. Distinct from <see cref="DisplayNameArgumentName"/>
    /// (which carries the type-level display name); used to surface the caller-chosen identity
    /// (e.g. <c>fix-crash1142</c>) in the UI without overwriting the type display.
    /// </summary>
    public const string AgentNameArgumentName = "agent-name";

    /// <summary>Lifecycle-start argument: the sub-agent's description.</summary>
    public const string DescriptionArgumentName = "description";

    /// <summary><see cref="UsageDetails.AdditionalCounts"/> key for reasoning tokens.</summary>
    public const string ReasoningTokensCountName = "copilot.sdk.reasoning_tokens";

    /// <summary><see cref="UsageDetails.AdditionalCounts"/> key for cache-read tokens.</summary>
    public const string CacheReadTokensCountName = "copilot.sdk.cache_read_tokens";

    /// <summary><see cref="UsageDetails.AdditionalCounts"/> key for cache-write tokens.</summary>
    public const string CacheWriteTokensCountName = "copilot.sdk.cache_write_tokens";

    /// <summary>
    /// <see cref="UsageDetails.AdditionalCounts"/> key for the provider-reported dollar cost of an
    /// assistant call, stored in micro-USD (<c>(long)Math.Round(cost * 1_000_000)</c>) because
    /// <see cref="UsageDetails.AdditionalCounts"/> is an integer dictionary. Divide by 1e6 to display.
    /// </summary>
    public const string CostMicroUsdCountName = "copilot.sdk.cost_micro_usd";

    /// <summary>
    /// Translates raw Copilot SDK session events into <see cref="ChatResponseUpdate"/> items.
    /// The stream completes normally on <see cref="SessionIdleEvent"/> and faults with
    /// <see cref="InvalidOperationException"/> on <see cref="SessionErrorEvent"/>. Fix #1312:
    /// unrecognised event types are no longer silently dropped. Fix #1323: the default arm
    /// logs at <see cref="LogLevel.Debug"/> and does NOT emit user-visible transcript content —
    /// the earlier behaviour (yielding a placeholder <see cref="TextContent"/> tagged with
    /// <see cref="UnknownCopilotSdkEventContentType"/>) polluted the chat and persisted history
    /// with per-chunk noise from high-frequency SDK pings such as
    /// <c>AssistantStreamingDeltaEvent</c>. Known-benign SDK lifecycle / metadata events are
    /// consumed silently by explicit <c>case</c> arms so they never reach the default arm.
    /// Accepting a <see cref="ChannelReader{T}"/> keeps the method testable without a live
    /// Copilot SDK session: tests write mock events to a channel and observe the translated
    /// output directly.
    /// </summary>
    public static IAsyncEnumerable<ChatResponseUpdate> TranslateCopilotSdkSessionEvents(
        ChannelReader<SessionEvent> events,
        CancellationToken cancellationToken = default)
        => TranslateCopilotSdkSessionEvents(events, logger: null, cancellationToken);

    /// <inheritdoc cref="TranslateCopilotSdkSessionEvents(ChannelReader{SessionEvent}, CancellationToken)"/>
    /// <param name="logger">
    /// Optional logger for the fix-#1312 / #1323 default arm. When <see langword="null"/>,
    /// unmapped events are still consumed silently but not logged. Callers that own a logger
    /// factory should pass a <see cref="ILogger"/> so genuinely-unknown SDK additions remain
    /// diagnosable at <see cref="LogLevel.Debug"/>.
    /// </param>
    public static async IAsyncEnumerable<ChatResponseUpdate> TranslateCopilotSdkSessionEvents(
        ChannelReader<SessionEvent> events,
        ILogger? logger,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        logger ??= NullLogger.Instance;

        await foreach (var sessionEvent in events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (sessionEvent)
            {
                case AssistantMessageDeltaEvent delta when !string.IsNullOrEmpty(delta.Data?.DeltaContent):
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        Contents = [Tag(new TextContent(delta.Data.DeltaContent), delta.AgentId)],
                    };
                    break;

                case AssistantReasoningDeltaEvent reasoningDelta when !string.IsNullOrEmpty(reasoningDelta.Data?.DeltaContent):
                    // Issue #808: reasoning deltas were previously hardcoded to the root agent;
                    // they carry the originating AgentId like every other content item.
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        Contents = [Tag(new TextReasoningContent(reasoningDelta.Data.DeltaContent), reasoningDelta.AgentId)],
                    };
                    break;

                case ToolExecutionStartEvent toolStart:
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        Contents = [Tag(CopilotToolEventMapper.MapToolStart(toolStart), toolStart.AgentId)],
                    };
                    break;

                case ToolExecutionCompleteEvent toolComplete:
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Tool,
                        Contents = [Tag(CopilotToolEventMapper.MapToolComplete(toolComplete), toolComplete.AgentId)],
                    };
                    break;

                // Fix #1139: the lifecycle CallId is the ROUTING KEY, which must always be the
                // child AgentId used to tag content — never the spawning tool-call id. When the
                // started signal omits AgentId (root-parent spawn case), emit an empty CallId and
                // rely on the spawning tool-call id (still surfaced only via the
                // ParentToolCallIdArgumentName argument) for start-time correlation in the router.
                // The event is dropped only when BOTH AgentId and ToolCallId are missing, since
                // there is nothing left to correlate against.
                case SubagentStartedEvent started when !string.IsNullOrEmpty(started.AgentId)
                                                       || !string.IsNullOrEmpty(started.Data?.ToolCallId):
                    yield return new ChatResponseUpdate
                    {
                        Contents =
                        [
                            TagLifecycle(new FunctionCallContent(
                                started.AgentId ?? string.Empty,
                                SubAgentStartLifecycleName,
                                new Dictionary<string, object?>
                                {
                                    [ParentToolCallIdArgumentName] = started.Data?.ToolCallId,
                                    [DisplayNameArgumentName] = started.Data?.AgentDisplayName,
                                    [DescriptionArgumentName] = started.Data?.AgentDescription,
                                    [AgentNameArgumentName] = started.Data?.AgentName,
                                })),
                        ],
                    };
                    break;

                case SubagentCompletedEvent completed when TryGetSubAgentId(completed.AgentId, completed.Data?.ToolCallId, out var completedId):
                    yield return new ChatResponseUpdate
                    {
                        Contents =
                        [
                            TagLifecycle(new FunctionResultContent(
                                completedId,
                                """{"event":"completed"}""")),
                        ],
                    };
                    break;

                case SubagentFailedEvent failed when TryGetSubAgentId(failed.AgentId, failed.Data?.ToolCallId, out var failedId):
                    yield return new ChatResponseUpdate
                    {
                        Contents =
                        [
                            TagLifecycle(new FunctionResultContent(
                                failedId,
                                JsonSerializer.Serialize(new Dictionary<string, string?>
                                {
                                    ["event"] = "failed",
                                    ["error"] = failed.Data?.Error,
                                }))),
                        ],
                    };
                    break;

                case AssistantUsageEvent usage when usage.Data is not null:
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        Contents = [Tag(MapUsage(usage.Data), usage.AgentId)],
                    };
                    break;

                case SystemNotificationEvent notification when !string.IsNullOrEmpty(notification.Data?.Content):
                    var content = Tag(
                        new TextContent(StripSystemNotificationTags(notification.Data.Content)),
                        notification.AgentId);
                    content.AdditionalProperties![ContentTypePropertyName] = SystemNotificationContentType;
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.System,
                        Contents = [content],
                    };
                    break;

                case SessionErrorEvent error:
                    throw new InvalidOperationException(
                        $"GitHub Copilot session error: {error.Data?.Message}");

                case SessionIdleEvent:
                    // Emit a terminal update carrying FinishReason so downstream middleware
                    // (StreamingPersistenceMiddleware) treats the response as final and persists
                    // the last message of the turn. Without this the last message of every
                    // Copilot turn is treated as unstable and never persisted (issue #1103).
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        FinishReason = ChatFinishReason.Stop,
                    };
                    yield break;

                // Known SDK event kinds whose "when" guard above failed (empty/degenerate payload)
                // are dropped explicitly here so they do not fall through to the fix-#1312 default
                // arm and surface as noisy "unknown-copilot-sdk-event" updates.
                case AssistantMessageDeltaEvent:
                case AssistantReasoningDeltaEvent:
                case SubagentStartedEvent:
                case SubagentCompletedEvent:
                case SubagentFailedEvent:
                case AssistantUsageEvent:
                case SystemNotificationEvent:
                    break;

                // Fix #1323: known-benign SDK lifecycle / metadata event types are consumed
                // silently so they do not reach the default arm (which used to yield a placeholder
                // TextContent that leaked into the user-visible transcript and persisted history).
                // The list is reconciled against the SDK's SessionEvents.cs; new types added by
                // future SDK releases will hit the default arm and be logged at Debug for triage.
                case AssistantStreamingDeltaEvent:
                case AssistantMessageStartEvent:
                case AssistantTurnStartEvent:
                case AssistantTurnEndEvent:
                case AssistantIdleEvent:
                case AssistantMessageEvent:
                case AssistantIntentEvent:
                case AssistantReasoningEvent:
                case AssistantToolCallDeltaEvent:
                case UserMessageEvent:
                case UserInputRequestedEvent:
                case UserInputCompletedEvent:
                case SystemMessageEvent:
                case PendingMessagesModifiedEvent:
                case SessionStartEvent:
                case SessionResumeEvent:
                case SessionShutdownEvent:
                case SessionInfoEvent:
                case SessionWarningEvent:
                case SessionTitleChangedEvent:
                case SessionModelChangeEvent:
                case SessionModeChangedEvent:
                case SessionRemoteSteerableChangedEvent:
                case SessionSessionLimitsChangedEvent:
                case SessionPermissionsChangedEvent:
                case SessionPlanChangedEvent:
                case SessionTodosChangedEvent:
                case SessionWorkspaceFileChangedEvent:
                case SessionHandoffEvent:
                case SessionTruncationEvent:
                case SessionSnapshotRewindEvent:
                case SessionContextChangedEvent:
                case SessionCompactionStartEvent:
                case SessionCompactionCompleteEvent:
                case SessionTaskCompleteEvent:
                case SessionCustomNotificationEvent:
                case SessionLimitsExhaustedRequestedEvent:
                case SessionLimitsExhaustedCompletedEvent:
                case SessionBackgroundTasksChangedEvent:
                case SessionUsageInfoEvent:
                case SessionUsageCheckpointEvent:
                case SessionToolsUpdatedEvent:
                case SessionSkillsLoadedEvent:
                case SessionCustomAgentsUpdatedEvent:
                case SessionMcpServersLoadedEvent:
                case SessionMcpServerStatusChangedEvent:
                case SessionExtensionsLoadedEvent:
                case SessionExtensionsAttachmentsPushedEvent:
                case SessionScheduleCreatedEvent:
                case SessionScheduleCancelledEvent:
                case SessionScheduleRearmedEvent:
                case SessionAutopilotObjectiveChangedEvent:
                    // Note: several additional SDK event types (e.g. Session canvas / binary-asset /
                    // auto-mode-resolved events) are marked [Experimental("GHCP001")] on the current
                    // SDK. They are intentionally NOT listed here to avoid a compile-time dependency
                    // on evaluation-only APIs; they fall through to the Debug-logging default arm,
                    // which is equally silent for the transcript.
                    break;

                default:
                    // Fix #1312 / #1323: never silently drop UNKNOWN event kinds — log them at
                    // Debug so future SDK additions are diagnosable — but never emit user-visible
                    // transcript content. #1312's earlier choice to yield a placeholder
                    // TextContent tagged with UnknownCopilotSdkEventContentType sprayed
                    // per-chunk noise into the chat and persisted history (issue #1323); the
                    // known-benign SDK lifecycle / metadata events are handled by explicit case
                    // arms above.
                    var runtimeTypeName = sessionEvent?.GetType().FullName ?? "<null>";
                    var runtimeAgentId = sessionEvent?.AgentId;
                    logger.LogDebug(
                        "Copilot SDK adapter received an unmapped session event of type {EventType} for AgentId {AgentId}; consumed silently.",
                        runtimeTypeName,
                        string.IsNullOrEmpty(runtimeAgentId) ? "<root>" : runtimeAgentId);
                    break;
            }
        }
    }

    /// <summary>
    /// Returns the sub-agent ID carried by <paramref name="content"/>'s
    /// <see cref="ParentToolCallIdPropertyName"/> property, or <see langword="null"/> for
    /// root-agent content.
    /// </summary>
    public static string? GetParentToolCallId(AIContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content.AdditionalProperties?.TryGetValue(ParentToolCallIdPropertyName, out var value) == true
            ? value as string
            : null;
    }

    /// <summary>
    /// Returns whether <paramref name="content"/> is a sub-agent lifecycle signal
    /// (started/completed/failed) as opposed to ordinary routed content.
    /// </summary>
    public static bool IsSubAgentLifecycleContent(AIContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content.AdditionalProperties?.TryGetValue(ContentTypePropertyName, out var value) == true
            && value as string == SubAgentLifecycleContentType;
    }

    private static bool TryGetSubAgentId(string? agentId, string? toolCallId, out string subAgentId)
    {
        // The SDK sometimes omits AgentId on lifecycle events; the spawning tool call ID is the
        // stable fallback identity in that case.
        subAgentId = (string.IsNullOrEmpty(agentId) ? toolCallId : agentId) ?? string.Empty;
        return !string.IsNullOrEmpty(subAgentId);
    }

    private static AIContent Tag(AIContent content, string? agentId)
    {
        content.AdditionalProperties ??= [];
        if (!string.IsNullOrEmpty(agentId))
        {
            content.AdditionalProperties[ParentToolCallIdPropertyName] = agentId;
        }

        return content;
    }

    private static AIContent TagLifecycle(AIContent content)
    {
        // Lifecycle signals have explicit CallId routing and never carry
        // ParentToolCallIdPropertyName; the content-type marker lets the router recognise them
        // without maintaining a table of known IDs.
        content.AdditionalProperties ??= [];
        content.AdditionalProperties[ContentTypePropertyName] = SubAgentLifecycleContentType;
        return content;
    }

    private static UsageContent MapUsage(AssistantUsageData data)
    {
        var details = new UsageDetails
        {
            InputTokenCount = (long?)data.InputTokens,
            OutputTokenCount = (long?)data.OutputTokens,
        };

        if (data.InputTokens is not null || data.OutputTokens is not null)
        {
            details.TotalTokenCount = (long)((data.InputTokens ?? 0) + (data.OutputTokens ?? 0));
        }

        AddAdditionalCount(details, ReasoningTokensCountName, data.ReasoningTokens);
        AddAdditionalCount(details, CacheReadTokensCountName, data.CacheReadTokens);
        AddAdditionalCount(details, CacheWriteTokensCountName, data.CacheWriteTokens);

#pragma warning disable GHCP001 // AssistantUsageData.Cost is evaluation-only but is the authoritative per-call $ cost.
        if (data.Cost is double cost)
#pragma warning restore GHCP001
        {
            details.AdditionalCounts ??= [];
            details.AdditionalCounts[CostMicroUsdCountName] = (long)Math.Round(cost * 1_000_000);
        }

        return new UsageContent(details);
    }

    private static void AddAdditionalCount(UsageDetails details, string name, double? value)
    {
        if (value is not null)
        {
            details.AdditionalCounts ??= [];
            details.AdditionalCounts[name] = (long)value.Value;
        }
    }

    private static string StripSystemNotificationTags(string content)
    {
        const string openTag = "<system_notification>";
        const string closeTag = "</system_notification>";

        var trimmed = content.Trim();
        if (trimmed.StartsWith(openTag, StringComparison.Ordinal)
            && trimmed.EndsWith(closeTag, StringComparison.Ordinal))
        {
            return trimmed[openTag.Length..^closeTag.Length].Trim();
        }

        return content;
    }
}
