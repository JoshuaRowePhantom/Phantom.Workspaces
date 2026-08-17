# Copilot SDK Session Event Types

> **SDK version:** `GitHub.Copilot.SDK 1.0.0-beta.2`
> **Issue:** [#148](https://github.com/JoshuaRowePhantom/Phantom.Workspaces/issues/148)

This document catalogs every `SessionEvent` subclass exposed by the GitHub Copilot SDK,
describes what data each event carries, and notes which events Phantom.Workspaces currently
handles and which are unexploited.

---

## Base class — `SessionEvent`

All events inherit these properties:

| Property    | Type     | Description |
|-------------|----------|-------------|
| `Id`        | `string` | UUID v4 uniquely identifying the event. |
| `ParentId`  | `string?` | ID of the chronologically preceding event (null for the first event). |
| `Timestamp` | `string` | ISO 8601 timestamp when the event was emitted. |
| `Type`      | `string` | String discriminator for the concrete event type. |
| `AgentId`   | `string?` | Sub-agent instance identifier; absent for root-agent and session-level events. |
| `Ephemeral` | `bool`   | When true the event is transient and not persisted to the on-disk session log. |

---

## Event catalogue

Events are grouped by area. Each entry lists the concrete class name, its `Data` payload
type, and all documented properties on that payload.

### Session lifecycle

#### `SessionStartEvent` — `SessionStartData`

Fired once when the session is first created.

| Property | Type | Description |
|----------|------|-------------|
| `SessionId` | `string` | Unique identifier for the session. |
| `StartTime` | `string` | ISO 8601 creation timestamp. |
| `Version` | `int` | Schema version for the session event format. |
| `Producer` | `string` | Software producing the events (e.g. `"copilot-agent"`). |
| `CopilotVersion` | `string` | Copilot application version string. |
| `SelectedModel` | `string?` | Model selected at creation time, if any. |
| `ReasoningEffort` | `string?` | Reasoning effort level (`"low"`, `"medium"`, `"high"`, `"xhigh"`). |
| `RemoteSteerable` | `bool` | Whether Mission Control remote-steering is supported. |
| `AlreadyInUse` | `bool` | Whether another client was already using the session. |
| `Context` | `SessionContextChangedData` | Working directory and git context. |

#### `SessionResumeEvent` — `SessionResumeData`

Fired when an existing session is resumed (e.g. after a process restart).

| Property | Type | Description |
|----------|------|-------------|
| `ResumeTime` | `string` | ISO 8601 resume timestamp. |
| `EventCount` | `int` | Total persisted events in the session at resume time. |
| `SelectedModel` | `string?` | Model currently selected at resume time. |
| `ReasoningEffort` | `string?` | Reasoning effort level at resume time. |
| `RemoteSteerable` | `bool` | Whether remote steering is supported. |
| `AlreadyInUse` | `bool` | Whether another client was already using the session. |
| `SessionWasActive` | `bool` | True when the runtime already had the session running in-memory (hot attach). |
| `ContinuePendingWork` | `bool` | When true, in-flight tool calls and permission requests from the previous lifetime are preserved. |
| `Context` | `SessionContextChangedData` | Updated working directory and git context. |

#### `SessionContextChangedEvent` — `SessionContextChangedData`

Working directory and git context. Emitted at session start and whenever the context changes.

| Property | Type | Description |
|----------|------|-------------|
| `Cwd` | `string` | Current working directory path. |
| `GitRoot` | `string?` | Git repository root directory. |
| `Branch` | `string?` | Current git branch name. |
| `Repository` | `string?` | `"owner/name"` for GitHub; `"org/project/repo"` for Azure DevOps. |
| `RepositoryHost` | `string?` | Raw host (e.g. `"github.com"`). |
| `HostType` | `string?` | Hosting platform: `"github"` or `"ado"`. |
| `BaseCommit` | `string?` | Base commit of the current branch. |
| `HeadCommit` | `string?` | Head commit of the current branch. |

#### `SessionIdleEvent` — `SessionIdleData`

Signals the session is idle with no background agents running. Marks the end of a turn.

| Property | Type | Description |
|----------|------|-------------|
| `Aborted` | `bool` | True when the preceding agentic loop was cancelled via an abort signal. |

#### `SessionErrorEvent` — `SessionErrorData`

An error condition during the session.

| Property | Type | Description |
|----------|------|-------------|
| `Message` | `string` | Human-readable error message. |
| `ErrorType` | `string` | Category: `"authentication"`, `"authorization"`, `"quota"`, `"rate_limit"`, `"context_limit"`, `"query"`. |
| `ErrorCode` | `string?` | Fine-grained provider error code (e.g. `"user_weekly_rate_limited"`). |
| `StatusCode` | `int?` | HTTP status code from the upstream request, if applicable. |
| `ProviderCallId` | `string?` | GitHub request tracing ID for server-side log correlation. |
| `Stack` | `string?` | Error stack trace, when available. |
| `Url` | `string?` | Optional URL for the user to open. |
| `EligibleForAutoSwitch` | `bool?` | On `rate_limit` errors: whether an `auto_mode_switch.requested` will follow. |

#### `SessionShutdownEvent` — `SessionShutdownData`

Emitted when the session terminates. Contains aggregate session statistics.

| Property | Type | Description |
|----------|------|-------------|
| `ShutdownType` | `string` | `"routine"` or `"error"`. |
| `ErrorReason` | `string?` | Error description when `ShutdownType` is `"error"`. |
| `CurrentModel` | `string?` | Model selected at shutdown time. |
| `CurrentTokens` | `long` | Total tokens in context window at shutdown. |
| `ConversationTokens` | `long` | Non-system message token count at shutdown. |
| `SystemTokens` | `long` | System message token count at shutdown. |
| `ToolDefinitionsTokens` | `long` | Tool definitions token count at shutdown. |
| `TotalApiDurationMs` | `long` | Cumulative time spent in API calls, in milliseconds. |
| `TotalNanoAiu` | `long` | Session-wide accumulated nano-AI-units cost. |
| `TotalPremiumRequests` | `int` | Total premium API requests used during the session. |
| `SessionStartTime` | `long` | Unix timestamp (ms) when the session started. |
| `TokenDetails` | `object` | Per-token-type accumulated counts. |
| `CodeChanges` | `object` | Aggregate code change metrics. |
| `ModelMetrics` | `object` | Per-model usage breakdown. |

#### `SessionTitleChangedEvent` — `SessionTitleChangedData`

| Property | Type | Description |
|----------|------|-------------|
| `Title` | `string` | New display title for the session. |

#### `SessionModelChangeEvent` — `SessionModelChangeData`

| Property | Type | Description |
|----------|------|-------------|
| `NewModel` | `string` | Newly selected model identifier. |
| `PreviousModel` | `string?` | Previously selected model identifier. |
| `ReasoningEffort` | `string?` | Reasoning effort after the change. |
| `PreviousReasoningEffort` | `string?` | Reasoning effort before the change. |
| `Cause` | `string?` | Why the change happened (e.g. `"rate_limit_auto_switch"`). |

#### `SessionModeChangedEvent` — `SessionModeChangedData`

| Property | Type | Description |
|----------|------|-------------|
| `NewMode` | `string` | Agent mode after change (e.g. `"interactive"`, `"plan"`, `"autopilot"`). |
| `PreviousMode` | `string` | Agent mode before change. |

#### `SessionRemoteSteerableChangedEvent` — `SessionRemoteSteerableChangedData`

| Property | Type | Description |
|----------|------|-------------|
| `RemoteSteerable` | `bool` | Whether the session now supports Mission Control remote steering. |

#### `SessionHandoffEvent` — `SessionHandoffData`

| Property | Type | Description |
|----------|------|-------------|
| `RemoteSessionId` | `string` | Session ID of the remote session being handed off. |
| `SourceType` | `string` | Origin type of the handed-off session. |
| `Host` | `string` | GitHub host URL. |
| `Repository` | `string?` | Repository context. |
| `HandoffTime` | `string` | ISO 8601 handoff timestamp. |
| `Summary` | `string?` | Summary of work done in the source session. |
| `Context` | `string?` | Additional context for the handoff. |

#### `SessionSnapshotRewindEvent` — `SessionSnapshotRewindData`

| Property | Type | Description |
|----------|------|-------------|
| `UpToEventId` | `string` | Event ID that was rewound to; all events from here onwards were removed. |
| `EventsRemoved` | `int` | Number of events removed. |

#### `SessionTruncationEvent` — `SessionTruncationData`

| Property | Type | Description |
|----------|------|-------------|
| `PerformedBy` | `string` | Component that performed truncation (e.g. `"BasicTruncator"`). |
| `TokenLimit` | `long` | Maximum tokens for the model context window. |
| `PreTruncationTokensInMessages` | `long` | Tokens before truncation. |
| `PostTruncationTokensInMessages` | `long` | Tokens after truncation. |
| `PreTruncationMessagesLength` | `int` | Message count before truncation. |
| `PostTruncationMessagesLength` | `int` | Message count after truncation. |
| `TokensRemovedDuringTruncation` | `long` | Tokens removed. |
| `MessagesRemovedDuringTruncation` | `int` | Messages removed. |

#### `SessionUsageInfoEvent` — `SessionUsageInfoData`

Periodic context-window usage snapshot.

| Property | Type | Description |
|----------|------|-------------|
| `CurrentTokens` | `long` | Current token count in the context window. |
| `TokenLimit` | `long` | Maximum token limit. |
| `ConversationTokens` | `long` | Token count from non-system messages. |
| `SystemTokens` | `long` | Token count from system messages. |
| `ToolDefinitionsTokens` | `long` | Token count from tool definitions. |
| `MessagesLength` | `int` | Current message count. |
| `IsInitial` | `bool` | True on the first usage_info event in the session. |

#### `SessionCompactionStartEvent` — `SessionCompactionStartData`

| Property | Type | Description |
|----------|------|-------------|
| `ConversationTokens` | `long` | Conversation token count at compaction start. |
| `SystemTokens` | `long` | System token count at compaction start. |
| `ToolDefinitionsTokens` | `long` | Tool definitions token count at compaction start. |

#### `SessionCompactionCompleteEvent` — `SessionCompactionCompleteData`

| Property | Type | Description |
|----------|------|-------------|
| `Success` | `bool` | Whether compaction completed successfully. |
| `Error` | `string?` | Error message if compaction failed. |
| `PreCompactionTokens` | `long` | Total tokens before compaction. |
| `PostCompactionTokens` | `long` | Total tokens after compaction. |
| `PreCompactionMessagesLength` | `int` | Message count before compaction. |
| `MessagesRemoved` | `int` | Messages removed. |
| `TokensRemoved` | `long` | Tokens removed. |
| `ConversationTokens` | `long` | Conversation tokens after compaction. |
| `SystemTokens` | `long` | System tokens after compaction. |
| `ToolDefinitionsTokens` | `long` | Tool definition tokens after compaction. |
| `SummaryContent` | `string?` | LLM-generated summary of the compacted history. |
| `CompactionTokensUsed` | `object?` | Token usage breakdown for the compaction LLM call. |
| `CheckpointNumber` | `int` | Checkpoint snapshot number. |
| `CheckpointPath` | `string` | Path where the checkpoint was stored. |
| `RequestId` | `string?` | GitHub request tracing ID for the compaction LLM call. |

#### `SessionTaskCompleteEvent` — `SessionTaskCompleteData`

| Property | Type | Description |
|----------|------|-------------|
| `Success` | `bool` | Whether the task succeeded. |
| `Summary` | `string?` | Summary of the completed task from the agent. |

#### `SessionInfoEvent` — `SessionInfoData`

Informational timeline message.

| Property | Type | Description |
|----------|------|-------------|
| `Message` | `string` | Human-readable informational message. |
| `InfoType` | `string` | Category: `"notification"`, `"timing"`, `"context_window"`, `"mcp"`, `"snapshot"`, `"configuration"`, `"authentication"`, `"model"`. |
| `Tip` | `string?` | Optional actionable tip. |
| `Url` | `string?` | Optional URL. |

#### `SessionWarningEvent` — `SessionWarningData`

| Property | Type | Description |
|----------|------|-------------|
| `Message` | `string` | Human-readable warning message. |
| `WarningType` | `string` | Category: `"subscription"`, `"policy"`, `"mcp"`. |
| `Url` | `string?` | Optional URL. |

#### `SessionScheduleCreatedEvent` — `SessionScheduleCreatedData`

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | Sequential id assigned within the session. |
| `Prompt` | `string` | Prompt text enqueued on every tick. |
| `IntervalMs` | `long` | Interval between ticks in milliseconds. |

#### `SessionScheduleCancelledEvent` — `SessionScheduleCancelledData`

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | Id of the scheduled prompt that was cancelled. |

#### `SessionPlanChangedEvent` — `SessionPlanChangedData`

| Property | Type | Description |
|----------|------|-------------|
| `Operation` | `string` | Type of operation on the plan file. |

#### `SessionWorkspaceFileChangedEvent` — `SessionWorkspaceFileChangedData`

| Property | Type | Description |
|----------|------|-------------|
| `Path` | `string` | Relative path within the session workspace files directory. |
| `Operation` | `string` | `"created"` or `"updated"`. |

#### `PendingMessagesModifiedEvent` — `PendingMessagesModifiedData`

Empty payload; signals that the pending message queue has changed.

---

### Messages

#### `UserMessageEvent` — `UserMessageData`

| Property | Type | Description |
|----------|------|-------------|
| `Content` | `string` | The user's message text as displayed in the timeline. |
| `TransformedContent` | `string?` | XML-wrapped version actually sent to the model. |
| `InteractionId` | `string?` | CAPI interaction ID. |
| `AgentMode` | `string?` | Agent mode active when the message was sent. |
| `Source` | `string?` | Origin of the message (e.g. `"skill-pdf"` for skill-injected). |
| `Attachments` | `object[]?` | Files, selections, or GitHub references attached to the message. |
| `ParentAgentTaskId` | `string?` | Parent agent task ID for telemetry correlation. |
| `SupportedNativeDocumentMimeTypes` | `string[]?` | MIME types sent natively instead of through tagged-files XML. |
| `NativeDocumentPathFallbackPaths` | `string[]?` | Paths that fell back to the tagged_files flow. |

#### `SystemMessageEvent` — `SystemMessageData`

| Property | Type | Description |
|----------|------|-------------|
| `Content` | `string` | System or developer prompt text. |
| `Role` | `string` | `"system"` or `"developer"`. |
| `Name` | `string?` | Optional name identifier for the message source. |
| `Metadata` | `SystemMessageMetadata?` | Metadata about the prompt template and its construction. |

#### `SystemNotificationEvent` — `SystemNotificationData`

| Property | Type | Description |
|----------|------|-------------|
| `Content` | `string` | Notification text (typically wrapped in `<system_notification>` XML tags). |
| `Kind` | `object?` | Structured metadata identifying what triggered this notification. |

---

### Assistant turn events

#### `AssistantTurnStartEvent` — `AssistantTurnStartData`

| Property | Type | Description |
|----------|------|-------------|
| `TurnId` | `string` | Identifier for this turn within the agentic loop. |
| `InteractionId` | `string?` | CAPI interaction ID. |

#### `AssistantIntentEvent` — `AssistantIntentData`

| Property | Type | Description |
|----------|------|-------------|
| `Intent` | `string` | Short description of what the agent is currently doing or planning to do. |

#### `AssistantReasoningEvent` — `AssistantReasoningData`

Complete extended-thinking block.

| Property | Type | Description |
|----------|------|-------------|
| `ReasoningId` | `string` | Unique identifier for this reasoning block. |
| `Content` | `string` | Complete extended thinking text. |

#### `AssistantReasoningDeltaEvent` — `AssistantReasoningDeltaData`

Streaming incremental reasoning chunk.

| Property | Type | Description |
|----------|------|-------------|
| `ReasoningId` | `string` | Reasoning block ID this delta belongs to. |
| `DeltaContent` | `string` | Incremental text chunk to append to the reasoning content. |

#### `AssistantStreamingDeltaEvent` — `AssistantStreamingDeltaData`

Streaming progress indicator (byte count only, no content).

| Property | Type | Description |
|----------|------|-------------|
| `TotalResponseSizeBytes` | `long` | Cumulative total bytes received from the streaming response so far. |

#### `AssistantMessageStartEvent` — `AssistantMessageStartData`

Marks the beginning of a streamed assistant message.

| Property | Type | Description |
|----------|------|-------------|
| `MessageId` | `string` | ID matching subsequent deltas and `AssistantMessageEvent`. |
| `Phase` | `string?` | Generation phase for phased-output models. |

#### `AssistantMessageDeltaEvent` — `AssistantMessageDeltaData`

Incremental text chunk of the assistant's response.

| Property | Type | Description |
|----------|------|-------------|
| `MessageId` | `string` | Message ID this delta belongs to. |
| `DeltaContent` | `string` | Incremental text chunk. |
| `ParentToolCallId` | `string?` | Parent tool call ID when originating from a sub-agent. |

#### `AssistantMessageEvent` — `AssistantMessageData`

The complete (non-streaming) assistant response for a turn.

| Property | Type | Description |
|----------|------|-------------|
| `MessageId` | `string` | Unique identifier for this assistant message. |
| `TurnId` | `string` | Matching `AssistantTurnStartEvent.TurnId`. |
| `InteractionId` | `string?` | CAPI interaction ID. |
| `Content` | `string?` | The assistant's text response. |
| `ReasoningText` | `string?` | Readable extended thinking text. |
| `ReasoningOpaque` | `string?` | Opaque/encrypted Anthropic thinking data (session-bound, stripped on resume). |
| `EncryptedContent` | `string?` | Encrypted reasoning from OpenAI models (session-bound, stripped on resume). |
| `OutputTokens` | `int?` | Actual output token count from the API response. |
| `Phase` | `string?` | Generation phase. |
| `RequestId` | `string?` | GitHub request tracing ID. |
| `ParentToolCallId` | `string?` | Parent tool call ID when originating from a sub-agent. |
| `ToolRequests` | `object[]?` | Tool invocations requested by the assistant. |

#### `AssistantTurnEndEvent` — `AssistantTurnEndData`

| Property | Type | Description |
|----------|------|-------------|
| `TurnId` | `string` | ID of the turn that has ended. |

#### `AssistantUsageEvent` — `AssistantUsageData`

Per-call LLM usage metrics.

| Property | Type | Description |
|----------|------|-------------|
| `ApiCallId` | `string?` | Completion ID from the model provider. |
| `Model` | `string` | Model identifier used for this API call. |
| `InputTokens` | `long` | Input tokens consumed. |
| `OutputTokens` | `long` | Output tokens produced. |
| `ReasoningTokens` | `long?` | Output tokens used for reasoning. |
| `CacheReadTokens` | `long?` | Tokens read from prompt cache. |
| `CacheWriteTokens` | `long?` | Tokens written to prompt cache. |
| `Duration` | `long` | API call duration in milliseconds. |
| `TtftMs` | `long?` | Time to first token (streaming only). |
| `InterTokenLatencyMs` | `double?` | Average inter-token latency (streaming only). |
| `Cost` | `double?` | Model multiplier cost for billing. |
| `ReasoningEffort` | `string?` | Reasoning effort level used. |
| `Initiator` | `string?` | What initiated the call (e.g. `"sub-agent"`, `"mcp-sampling"`). |
| `ParentToolCallId` | `string?` | Parent tool call ID when usage originates from a sub-agent. |
| `ProviderCallId` | `string?` | GitHub request tracing ID. |
| `QuotaSnapshots` | `object?` | Per-quota resource usage snapshots. |
| `CopilotUsage` | `object?` | Per-request cost and usage from CAPI. |

#### `AbortEvent` — `AbortData`

| Property | Type | Description |
|----------|------|-------------|
| `Reason` | `string` | Reason the current turn was aborted (e.g. `"user initiated"`). |

#### `ModelCallFailureEvent` — `ModelCallFailureData`

| Property | Type | Description |
|----------|------|-------------|
| `Model` | `string` | Model used for the failed API call. |
| `StatusCode` | `int?` | HTTP status code. |
| `ErrorMessage` | `string?` | Raw provider error message. |
| `ApiCallId` | `string?` | Completion ID from the provider. |
| `ProviderCallId` | `string?` | GitHub request tracing ID. |
| `DurationMs` | `long` | Duration of the failed call in milliseconds. |
| `Initiator` | `string?` | Initiator of the API call. |
| `Source` | `string?` | Where the failed model call originated. |

---

### Tool execution events

#### `ToolUserRequestedEvent` — `ToolUserRequestedData`

User explicitly requested a tool invocation.

| Property | Type | Description |
|----------|------|-------------|
| `ToolCallId` | `string` | Unique identifier for this tool call. |
| `ToolName` | `string` | Name of the tool. |
| `Arguments` | `object?` | Arguments for the tool invocation. |

#### `ToolExecutionStartEvent` — `ToolExecutionStartData`

Fired when a tool begins executing.

| Property | Type | Description |
|----------|------|-------------|
| `ToolCallId` | `string` | Unique identifier for this tool call. |
| `ToolName` | `string` | Name of the tool being executed. |
| `Arguments` | `object?` | Arguments passed to the tool. |
| `TurnId` | `string` | Matching `AssistantTurnStartEvent.TurnId`. |
| `McpServerName` | `string?` | MCP server hosting the tool (when applicable). |
| `McpToolName` | `string?` | Original tool name on the MCP server (when applicable). |
| `ParentToolCallId` | `string?` | Parent tool call ID when originating from a sub-agent. |

#### `ToolExecutionPartialResultEvent` — `ToolExecutionPartialResultData`

Streaming output chunk from a running tool.

| Property | Type | Description |
|----------|------|-------------|
| `ToolCallId` | `string` | Tool call ID this partial result belongs to. |
| `PartialOutput` | `string` | Incremental output chunk. |

#### `ToolExecutionProgressEvent` — `ToolExecutionProgressData`

Human-readable progress notification from a tool.

| Property | Type | Description |
|----------|------|-------------|
| `ToolCallId` | `string` | Tool call ID this notification belongs to. |
| `ProgressMessage` | `string` | Human-readable progress status message. |

#### `ToolExecutionCompleteEvent` — `ToolExecutionCompleteData`

Fired when a tool finishes executing.

| Property | Type | Description |
|----------|------|-------------|
| `ToolCallId` | `string` | Unique identifier for the completed tool call. |
| `TurnId` | `string` | Matching `AssistantTurnStartEvent.TurnId`. |
| `Success` | `bool` | Whether execution completed successfully. |
| `Result` | `object?` | Tool execution result on success. |
| `Error` | `string?` | Error details when the tool failed. |
| `IsUserRequested` | `bool` | Whether the tool call was explicitly user-requested. |
| `Model` | `string?` | Model that generated this tool call. |
| `InteractionId` | `string?` | CAPI interaction ID. |
| `ParentToolCallId` | `string?` | Parent tool call ID when originating from a sub-agent. |
| `ToolTelemetry` | `object?` | Tool-specific telemetry data. |

---

### Sub-agent events

#### `SubagentStartedEvent` — `SubagentStartedData`

| Property | Type | Description |
|----------|------|-------------|
| `ToolCallId` | `string` | Parent tool call ID that spawned this sub-agent. |
| `AgentName` | `string` | Internal name of the sub-agent. |
| `AgentDisplayName` | `string` | Human-readable display name. |
| `AgentDescription` | `string?` | Description of what the sub-agent does. |

#### `SubagentCompletedEvent` — `SubagentCompletedData`

| Property | Type | Description |
|----------|------|-------------|
| `ToolCallId` | `string` | Parent tool call ID. |
| `AgentName` | `string` | Internal name. |
| `AgentDisplayName` | `string` | Display name. |
| `Model` | `string?` | Model used by the sub-agent. |
| `DurationMs` | `long` | Wall-clock duration in milliseconds. |
| `TotalTokens` | `long` | Total tokens (input + output) consumed. |
| `TotalToolCalls` | `int` | Total tool calls made. |

#### `SubagentFailedEvent` — `SubagentFailedData`

| Property | Type | Description |
|----------|------|-------------|
| `ToolCallId` | `string` | Parent tool call ID. |
| `AgentName` | `string` | Internal name. |
| `AgentDisplayName` | `string` | Display name. |
| `Error` | `string` | Error message. |
| `Model` | `string?` | Model used (if any model calls succeeded before failure). |
| `DurationMs` | `long` | Wall-clock duration in milliseconds. |
| `TotalTokens` | `long` | Tokens consumed before failure. |
| `TotalToolCalls` | `int` | Tool calls made before failure. |

#### `SubagentSelectedEvent` — `SubagentSelectedData`

Custom agent selected.

| Property | Type | Description |
|----------|------|-------------|
| `AgentName` | `string` | Internal name. |
| `AgentDisplayName` | `string` | Display name. |
| `Tools` | `string[]?` | Tool names available to this agent, or null for all tools. |

#### `SubagentDeselectedEvent` — `SubagentDeselectedData`

Empty payload; the custom agent was deselected, returning to the default agent.

---

### Hook events

#### `HookStartEvent` — `HookStartData`

| Property | Type | Description |
|----------|------|-------------|
| `HookInvocationId` | `string` | Unique identifier for this hook invocation. |
| `HookType` | `string` | Type of hook (e.g. `"preToolUse"`, `"postToolUse"`, `"sessionStart"`). |
| `Input` | `object?` | Input data passed to the hook. |

#### `HookEndEvent` — `HookEndData`

| Property | Type | Description |
|----------|------|-------------|
| `HookInvocationId` | `string` | Matches the corresponding `HookStartEvent`. |
| `HookType` | `string` | Type of hook. |
| `Success` | `bool` | Whether the hook completed successfully. |
| `Output` | `object?` | Output data produced by the hook. |
| `Error` | `string?` | Error details when the hook failed. |

---

### Skill events

#### `SkillInvokedEvent` — `SkillInvokedData`

| Property | Type | Description |
|----------|------|-------------|
| `Name` | `string` | Name of the invoked skill. |
| `Description` | `string?` | Description from `SKILL.md` frontmatter. |
| `Path` | `string` | File path to the `SKILL.md` definition. |
| `Content` | `string` | Full content of the skill file injected into the conversation. |
| `AllowedTools` | `string[]?` | Tool names auto-approved while this skill is active. |
| `PluginName` | `string?` | Plugin this skill originated from. |
| `PluginVersion` | `string?` | Plugin version. |

---

### UI interaction events (client-handled round-trips)

These events require a client response via the corresponding `session.respondTo*()` method.

#### `PermissionRequestedEvent` — `PermissionRequestedData`

| Property | Type | Description |
|----------|------|-------------|
| `RequestId` | `string` | Used to respond via `session.respondToPermission()`. |
| `PermissionRequest` | `object` | Details of the permission being requested. |
| `PromptRequest` | `object?` | Derived user-facing permission prompt for UI consumers. |
| `ResolvedByHook` | `bool` | When true, already resolved by a hook; no client action needed. |

#### `PermissionCompletedEvent` — `PermissionCompletedData`

| Property | Type | Description |
|----------|------|-------------|
| `RequestId` | `string` | Matching `PermissionRequestedData.RequestId`. |
| `Result` | `object` | Result of the permission request. |
| `ToolCallId` | `string?` | Associated tool call ID, for UI correlation. |

#### `UserInputRequestedEvent` — `UserInputRequestedData`

| Property | Type | Description |
|----------|------|-------------|
| `RequestId` | `string` | Used to respond via `session.respondToUserInput()`. |
| `Question` | `string` | Question or prompt to present to the user. |
| `Choices` | `string[]?` | Predefined choices, if applicable. |
| `AllowFreeform` | `bool` | Whether free-form text is accepted in addition to choices. |
| `ToolCallId` | `string?` | Tool call ID for UI correlation. |

#### `UserInputCompletedEvent` — `UserInputCompletedData`

| Property | Type | Description |
|----------|------|-------------|
| `RequestId` | `string` | Matching `UserInputRequestedData.RequestId`. |
| `Answer` | `string` | The user's answer. |
| `WasFreeform` | `bool` | Whether the answer was free-form text rather than a predefined choice. |

#### `ElicitationRequestedEvent` — `ElicitationRequestedData`

| Property | Type | Description |
|----------|------|-------------|
| `RequestId` | `string` | Used to respond via `session.respondToElicitation()`. |
| `Message` | `string` | Description of information needed. |
| `Mode` | `string?` | `"form"` (default) or `"url"`. |
| `RequestedSchema` | `object?` | JSON Schema for form fields (form mode only). |
| `Url` | `string?` | URL to open in the browser (url mode only). |
| `ToolCallId` | `string?` | Tool call ID for UI correlation. |
| `ElicitationSource` | `string?` | MCP server name, or absent for agent-initiated. |

#### `ElicitationCompletedEvent` — `ElicitationCompletedData`

| Property | Type | Description |
|----------|------|-------------|
| `RequestId` | `string` | Matching request ID. |
| `Action` | `string` | `"accept"`, `"decline"`, or `"cancel"`. |
| `Content` | `object?` | Submitted form data when `Action` is `"accept"`. |

#### `ExitPlanModeRequestedEvent` — `ExitPlanModeRequestedData`

| Property | Type | Description |
|----------|------|-------------|
| `RequestId` | `string` | Used to respond via `session.respondToExitPlanMode()`. |
| `PlanContent` | `string` | Full content of the plan file. |
| `Summary` | `string?` | Summary of the plan. |
| `RecommendedAction` | `string?` | Recommended action for the user. |
| `Actions` | `string[]?` | Available actions (e.g. `"approve"`, `"edit"`, `"reject"`). |

#### `ExitPlanModeCompletedEvent` — `ExitPlanModeCompletedData`

| Property | Type | Description |
|----------|------|-------------|
| `RequestId` | `string` | Matching request ID. |
| `Approved` | `bool` | Whether the plan was approved. |
| `SelectedAction` | `string?` | Which action was taken (e.g. `"autopilot"`, `"interactive"`, `"exit_only"`). |
| `AutoApproveEdits` | `bool` | Whether edits should be auto-approved. |
| `Feedback` | `string?` | Free-form feedback if changes were requested. |

#### `AutoModeSwitchRequestedEvent` — `AutoModeSwitchRequestedData`

| Property | Type | Description |
|----------|------|-------------|
| `RequestId` | `string` | Used to respond via `session.respondToAutoModeSwitch()`. |
| `ErrorCode` | `string` | Rate limit error code that triggered this request. |
| `RetryAfterSeconds` | `int?` | Seconds until the rate limit resets. |

#### `AutoModeSwitchCompletedEvent` — `AutoModeSwitchCompletedData`

| Property | Type | Description |
|----------|------|-------------|
| `RequestId` | `string` | Matching request ID. |
| `Response` | `string` | User's choice: `"yes"`, `"yes_always"`, or `"no"`. |

#### `SamplingRequestedEvent` — `SamplingRequestedData`

| Property | Type | Description |
|----------|------|-------------|
| `RequestId` | `string` | Used to respond via `session.respondToSampling()`. |
| `ServerName` | `string` | MCP server that initiated the sampling request. |
| `McpRequestId` | `object` | JSON-RPC request ID from the MCP protocol. |

#### `SamplingCompletedEvent` — `SamplingCompletedData`

| Property | Type | Description |
|----------|------|-------------|
| `RequestId` | `string` | Matching request ID. |

#### `McpOauthRequiredEvent` — `McpOauthRequiredData`

| Property | Type | Description |
|----------|------|-------------|
| `RequestId` | `string` | Used to respond via `session.respondToMcpOAuth()`. |
| `ServerName` | `string` | MCP server display name. |
| `ServerUrl` | `string` | MCP server URL. |
| `StaticClientConfig` | `object?` | Static OAuth client configuration, if the server specifies one. |

#### `McpOauthCompletedEvent` — `McpOauthCompletedData`

| Property | Type | Description |
|----------|------|-------------|
| `RequestId` | `string` | Matching request ID. |

#### `ExternalToolRequestedEvent` — `ExternalToolRequestedData`

Client-side tool execution request.

| Property | Type | Description |
|----------|------|-------------|
| `RequestId` | `string` | Used to respond via `session.respondToExternalTool()`. |
| `ToolCallId` | `string` | Tool call ID for this invocation. |
| `ToolName` | `string` | Name of the external tool. |
| `Arguments` | `object?` | Arguments to pass. |
| `SessionId` | `string` | Session ID this request belongs to. |
| `Traceparent` | `string?` | W3C Trace Context traceparent header. |
| `Tracestate` | `string?` | W3C Trace Context tracestate header. |

#### `ExternalToolCompletedEvent` — `ExternalToolCompletedData`

| Property | Type | Description |
|----------|------|-------------|
| `RequestId` | `string` | Matching request ID; clients should dismiss any UI for this request. |

---

### Command events

#### `CommandQueuedEvent` — `CommandQueuedData`

| Property | Type | Description |
|----------|------|-------------|
| `RequestId` | `string` | Used to respond via `session.respondToQueuedCommand()`. |
| `Command` | `string` | Slash command text (e.g. `/help`). |

#### `CommandExecuteEvent` — `CommandExecuteData`

| Property | Type | Description |
|----------|------|-------------|
| `RequestId` | `string` | Used to respond via `session.commands.handlePendingCommand()`. |
| `Command` | `string` | Full command text. |
| `CommandName` | `string` | Command name without leading `/`. |
| `Args` | `string?` | Raw argument string after the command name. |

#### `CommandCompletedEvent` — `CommandCompletedData`

| Property | Type | Description |
|----------|------|-------------|
| `RequestId` | `string` | Matching request ID. |

---

### Capability/tool registry events

#### `CommandsChangedEvent` — `CommandsChangedData`

| Property | Type | Description |
|----------|------|-------------|
| `Commands` | `object[]` | Current list of registered SDK commands. |

#### `CapabilitiesChangedEvent` — `CapabilitiesChangedData`

| Property | Type | Description |
|----------|------|-------------|
| `Ui` | `object?` | UI capability changes. |

#### `SessionToolsUpdatedEvent` — `SessionToolsUpdatedData`

No documented properties (payload data schema not published in the XML docs).

#### `SessionSkillsLoadedEvent` — `SessionSkillsLoadedData`

No documented properties.

#### `SessionCustomAgentsUpdatedEvent` — `SessionCustomAgentsUpdatedData`

No documented properties.

#### `SessionMcpServersLoadedEvent` — `SessionMcpServersLoadedData`

No documented properties.

#### `SessionMcpServerStatusChangedEvent` — `SessionMcpServerStatusChangedData`

No documented properties.

#### `SessionExtensionsLoadedEvent` — `SessionExtensionsLoadedData`

No documented properties.

#### `SessionBackgroundTasksChangedEvent` — `SessionBackgroundTasksChangedData`

No documented properties.

---

## Current Phantom.Workspaces usage

### `CopilotSdkChatClient` (`Phantom.Workspaces.Llm.Core`)

The event handler is installed via `session.On(sessionEvent => …)`. Two handler scopes exist:

**Non-streaming turn (`GetResponseAsync`)**

| Event | Handling |
|-------|----------|
| `ToolExecutionStartEvent` | Mapped via `CopilotToolEventMapper.MapToolStart()` → `FunctionCallContent` accumulated for the response. |
| `ToolExecutionCompleteEvent` | Mapped via `CopilotToolEventMapper.MapToolComplete()` → `FunctionResultContent` accumulated for the response. |
| All others | Ignored. |

**Streaming turn (`GetStreamingResponseAsync`)**

| Event | Handling |
|-------|----------|
| `AssistantMessageDeltaEvent` | `delta.Data.DeltaContent` emitted as a `ChatResponseUpdate(Role.Assistant, text)`. |
| `AssistantReasoningDeltaEvent` | `reasoningDelta.Data.DeltaContent` emitted as a `ChatResponseUpdate` carrying a `TextReasoningContent`. |
| `ToolExecutionStartEvent` | Emitted as a `ChatResponseUpdate(Role.Assistant, FunctionCallContent)`. |
| `ToolExecutionCompleteEvent` | Emitted as a `ChatResponseUpdate(Role.Tool, FunctionResultContent)`. |
| `SessionErrorEvent` | `channel.Writer.TryComplete(new InvalidOperationException(error.Data.Message))` — terminates the stream with an error. |
| `SessionIdleEvent` | `channel.Writer.TryComplete()` — terminates the stream successfully. |
| All others | Ignored. |

**Final response assembly** uses `AssistantMessageEvent.Data.ReasoningText` and
`AssistantMessageEvent.Data.Content` to build the non-streaming `ChatResponse`.

### Unmapped / benign event handling (#1312 → #1323)

`CopilotSdkStreamAdapter.TranslateCopilotSdkSessionEvents` and the non-streaming
`CopilotSdkChatClient.GetResponseAsync` handler must never emit user-visible transcript content
for events they do not explicitly translate. #1312 added a `default:` arm that yielded a
placeholder `TextContent` reading `[unknown-copilot-sdk-event: <TypeName>]`; because the SDK
emits many high-frequency lifecycle / metadata events per turn (most notably
`AssistantStreamingDeltaEvent`, a per-chunk byte-count progress ping), that placeholder sprayed
noise char-by-char into the chat and persisted history. #1323 reconciles the switch against the
live SDK event set and enforces:

- **Known-benign lifecycle / metadata events are consumed silently** via explicit `case` arms
  (they never reach the default arm). These carry no assistant-visible content and are
  intentionally dropped from the transcript:

  - Assistant lifecycle: `AssistantStreamingDeltaEvent`, `AssistantMessageStartEvent`,
    `AssistantTurnStartEvent`, `AssistantTurnEndEvent`, `AssistantIdleEvent`,
    `AssistantMessageEvent`, `AssistantIntentEvent`, `AssistantReasoningEvent`,
    `AssistantToolCallDeltaEvent`.
  - User / system bookkeeping: `UserMessageEvent`, `UserInputRequestedEvent`,
    `UserInputCompletedEvent`, `SystemMessageEvent`, `PendingMessagesModifiedEvent`.
  - Session lifecycle / metadata: `SessionStartEvent`, `SessionResumeEvent`,
    `SessionShutdownEvent`, `SessionInfoEvent`, `SessionWarningEvent`,
    `SessionTitleChangedEvent`, `SessionModelChangeEvent`, `SessionModeChangedEvent`,
    `SessionRemoteSteerableChangedEvent`, `SessionSessionLimitsChangedEvent`,
    `SessionPermissionsChangedEvent`, `SessionPlanChangedEvent`, `SessionTodosChangedEvent`,
    `SessionWorkspaceFileChangedEvent`, `SessionHandoffEvent`, `SessionTruncationEvent`,
    `SessionSnapshotRewindEvent`, `SessionContextChangedEvent`, `SessionCompactionStartEvent`,
    `SessionCompactionCompleteEvent`, `SessionTaskCompleteEvent`, `SessionBinaryAssetEvent`,
    `SessionCustomNotificationEvent`, `SessionLimitsExhaustedRequestedEvent`,
    `SessionLimitsExhaustedCompletedEvent`, `SessionAutoModeResolvedEvent`,
    `SessionBackgroundTasksChangedEvent`, `SessionUsageInfoEvent`,
    `SessionUsageCheckpointEvent`, `SessionToolsUpdatedEvent`, `SessionSkillsLoadedEvent`,
    `SessionCustomAgentsUpdatedEvent`, `SessionMcpServersLoadedEvent`,
    `SessionMcpServerStatusChangedEvent`, `SessionExtensionsLoadedEvent`,
    `SessionExtensionsAttachmentsPushedEvent`, `SessionScheduleCreatedEvent`,
    `SessionScheduleCancelledEvent`, `SessionScheduleRearmedEvent`,
    `SessionAutopilotObjectiveChangedEvent`, `SessionCanvasOpenedEvent`,
    `SessionCanvasClosedEvent`, `SessionCanvasRegistryChangedEvent`,
    `SessionCanvasUnavailableEvent`, `SessionCanvasRecordedEvent`, `SessionCanvasRemovedEvent`.

- **Genuinely-unknown events** (an SDK type not covered above and not otherwise mapped) hit the
  `default:` arm, which logs at `LogLevel.Debug` with the runtime type name and originating
  `AgentId` (`<root>` for the main agent) and yields **nothing** to the transcript. The
  `UnknownCopilotSdkEventContentType` marker constant is retained only for log/test
  identification; it is never emitted as `TextContent`.


### `AgentViewModel` (`Phantom.Workspaces.Agent.Gui`)

`AgentViewModel` does not subscribe to SDK events directly — it consumes the already-abstracted
`AgentChat` events (`AgentSessionIdChanged`, `ToolsChanged`, `UsageChanged`) which are
translated upstream before reaching the view model. The following properties are surfaced in
the UI and are populated by SDK events further up the pipeline:

| UI property | Upstream SDK source |
|-------------|---------------------|
| `AgentSessionId` | `CopilotSession.SessionId` (via `SessionEstablished` internal event → `AgentChat.AgentSessionIdChanged`) |
| `TotalInputTokenCount` / `TotalOutputTokenCount` | `AssistantUsageEvent` accumulated in `AgentChat` → `UsageChanged` |
| Tools list | `SessionToolsUpdatedEvent` (or equivalent) → `AgentChat.ToolsChanged` |

---

## Unhandled events and exploitation opportunities

The following events are currently received but silently ignored. Each represents a concrete
opportunity to enrich the Phantom.Workspaces UI or agent pipeline.

### High value

| Event | Opportunity |
|-------|-------------|
| `AssistantIntentEvent` | Surface the agent's current stated intent in the UI sidebar or running-items list. |
| `AssistantReasoningEvent` | Show the complete (non-streaming) reasoning block in the existing "reasoning" collapsible panel when streaming is not active. |
| `SessionUsageInfoEvent` | Display a live token-usage gauge / progress bar in the chat details panel. The data (`CurrentTokens`, `TokenLimit`) are already needed for context-window warnings. |
| `SessionCompactionCompleteEvent` | Notify the user that history was compacted and how many tokens were freed; store `SummaryContent` for inspection. |
| `SessionTaskCompleteEvent` | Show a task-complete badge or notification with the agent's `Summary`. |
| `SubagentStartedEvent` | Populate the placeholder "Sub-agents" panel with a live sub-agent entry. `AgentId` on subsequent events from the sub-agent already identifies its stream. |
| `SubagentCompletedEvent` / `SubagentFailedEvent` | Update the sub-agent entry with completion status, duration, and token cost. |
| `SessionErrorEvent.Url` | Render a clickable link alongside the error message rather than plain text only. |

### Medium value

| Event | Opportunity |
|-------|-------------|
| `ToolExecutionPartialResultEvent` | Stream partial tool output incrementally instead of waiting for `ToolExecutionCompleteEvent`. |
| `ToolExecutionProgressEvent` | Show real-time progress messages for long-running tools in the running-items list. |
| `SessionModelChangeEvent` | React to auto model-switch events by updating `ModelId`/`ModelProvider` properties and showing a notification. |
| `SessionModeChangedEvent` | Update a mode indicator in the UI (interactive / plan / autopilot). |
| `SessionTruncationEvent` | Warn the user that conversation history was truncated and by how much. |
| `SessionHandoffEvent` | Display handoff provenance metadata (source session, summary) when resuming a handed-off session. |
| `AssistantUsageEvent` (per-turn) | Persist per-turn token breakdowns (cache hits, reasoning tokens) for cost visibility beyond session totals. |
| `SessionInfoEvent` / `SessionWarningEvent` | Surface advisory messages (MCP connectivity, subscription warnings) in the chat timeline. |
| `HookStartEvent` / `HookEndEvent` | Show hook invocation status in the running-items list. |
| `SkillInvokedEvent` | Display which skill was activated and its description in the chat details panel. |

### Lower value / complex integration

| Event | Opportunity |
|-------|-------------|
| `UserInputRequestedEvent` | Show an inline input widget in the chat when the agent needs to ask the user a structured question. |
| `ElicitationRequestedEvent` | Render a form or open a browser tab for MCP elicitation requests. |
| `ExitPlanModeRequestedEvent` | Present the plan review UI (approve/edit/reject) in the chat. |
| `AutoModeSwitchRequestedEvent` | Show a prompt asking whether to switch models due to rate limiting. |
| `CommandExecuteEvent` | Allow the SDK to dispatch slash commands to be executed by Phantom.Workspaces's own slash-command registry. |
| `SessionShutdownEvent` | Log or display end-of-session cost/change summaries. |
| `SessionScheduleCreatedEvent` / `SessionScheduleCancelledEvent` | Show scheduled prompt status in the background tasks panel. |
| `SubagentSelectedEvent` / `SubagentDeselectedEvent` | Show which custom agent is active in the UI. |
