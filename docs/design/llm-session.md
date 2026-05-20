# LLM Session and Conversation Design

## Purpose

Define the core domain model for integrating LLM capabilities into Phantom.Workspaces with clear abstraction boundaries, auditable conversation state, and trust-profile-scoped tool execution.

## Core abstractions

### `ILlmProvider`

`ILlmProvider` is the provider abstraction used to communicate with an LLM model endpoint and retrieve responses.

Responsibilities:

1. Submit conversation context (turns + tool outputs) to a model.
2. Return assistant output and any model-requested tool invocations.
3. Surface provider metadata (model ID, usage, finish reason, errors).
4. Remain transport/provider agnostic (OpenAI-compatible, native vendor SDKs, local runtimes, etc.).

Non-goals:

1. Rights enforcement.
2. Direct tool execution.
3. Container orchestration.

### `ILlmProvider` shape (core)

```text
ILlmProvider
  StreamAsync(
    conversation: LlmConversation,
    cancellationToken?: CancellationToken)
    -> IAsyncEnumerable<LlmStreamEvent>
```

Notes:

1. Provider output is streamed as `IAsyncEnumerable<LlmStreamEvent>`.
2. `LlmStreamEvent` can carry a normal `LlmEvent`, a `LlmReplaceEvent`, or a `LlmCheckpointEvent`.
3. Conversation state construction is handled by `LlmConversationBuilder`, not by provider implementations.
4. `ILlmProvider` is part of `Phantom.Workspaces.Llm.Core`.

### `PreProvidedContentLlmProvider` (decorator implementation)

`PreProvidedContentLlmProvider` wraps an underlying `ILlmProvider` and guarantees a configured pre-provided content prefix is retained on every generated conversation.

Behavior:

1. For each generated `LlmConversation`, ensure pre-provided content is present at the conversation prefix.
2. Prefixing is idempotent: if the underlying conversation already starts with the same pre-provided content, do not duplicate it.
3. When pre-provided content changes, start streaming a new prefixed `LlmConversation` that reflects the updated prefix.
4. When the underlying provider produces a new conversation, apply prefix normalization before exposing it.

Suggested shape:

```text
PreProvidedContentLlmProvider : ILlmProvider
  UnderlyingProvider: ILlmProvider
  PreProvidedContent: ImmutableList<LlmEvent>
  UpdatePreProvidedContent(content: ImmutableList<LlmEvent>)
  StreamAsync(conversation, cancellationToken?) -> IAsyncEnumerable<LlmStreamEvent>  // ILlmProvider contract
  StreamConversationsAsync(conversation, cancellationToken?) -> IAsyncEnumerable<LlmConversation>
```

## Conversation model

### `LlmConversation`

A single `LlmConversation` represents one coherent LLM dialogue and its full execution trace.

It contains:

1. All user/assistant/system turns.
2. All tool requests emitted by the model.
3. All tool results returned to the model.
4. Timing/usage metadata and terminal state.

`LlmConversation` is the canonical record for replay, debugging, and auditing.

Ordering requirement:

Conversation history must be represented as a single ordered event stream. Separate collections for turns/tool calls/tool results are insufficient because ordering between them is semantically significant.

Tool-call records should remain provider/tool focused; execution policy selection belongs to the Agent layer:

```text
LlmToolCall
  Id
  ToolName
  Input
```

### Suggested structure

```text
LlmConversation
  Events: IReadOnlyList<LlmEvent>  // strictly ordered canonical log
  CreatedAt / UpdatedAt
```

```text
LlmEvent
  Timestamp
  EventKind
  Role
  Content
  ExternalContent
  ExternalContentName
  Thinking
  ToolCalls
  ToolName
  CorrelationId
  Done
  DoneReason
```

```text
LlmStreamEvent
  Event?
  Replace?
  Checkpoint?
```

```text
LlmReplaceEvent
  RemoveCount
  Events: IReadOnlyList<LlmEvent>
```

```text
LlmCheckpointEvent
  Conversation: LlmConversation
```

```text
LlmToolCall
  Name
  Arguments (JSON object)
  Index
  CorrelationId  // guaranteed after normalization
```

### Low-level compatibility notes (Ollama/MCP)

1. `role`, `content`, `thinking`, and `tool_calls` map directly to Ollama chat stream/message fields.
2. Tool result messages from Ollama use `role=tool`, `tool_name`, and string `content` (often JSON-encoded payload).
3. `ExternalContent` + `ExternalContentName` allow conversation persistence of tool-returned artifacts that are not directly delivered to the LLM prompt (for example large payloads, files, or side-channel output).
4. Native Ollama tool calls do not guarantee a stable call id; MCP/JSON-RPC does require request/response correlation ids.
5. Core normalization must guarantee internal `CorrelationId` on tool call/result/notification events:
   - preserve upstream id when provided;
   - otherwise synthesize deterministic ids (for example `${turnSequence}:${indexOrOrdinal}:${toolName}`).
6. `done` and `done_reason` should be preserved on low-level events for provider-compatible terminal semantics (for example `stop`, `length`).

```text
Stream event types
  LlmStreamEvent
  LlmReplaceEvent
  LlmCheckpointEvent
```

```text
MCP-related events
  LlmEvent                 // low-level conversation event
  LlmStreamEvent           // provider stream event wrapper
```

### Core projections from low-level event stream

1. **Content projection**: coalesce contiguous assistant `Content` chunks into one logical turn payload.
2. **Thinking projection**: coalesce contiguous assistant `Thinking` chunks into one logical thinking payload.
3. **Tool-call projection**: emit one logical tool call per normalized call (`CorrelationId`).
4. **Tool-result projection**: match by `CorrelationId` when available; otherwise use normalized fallback matching (`tool_name` + call order). Include `ExternalContent`/`ExternalContentName` in conversation projections so persisted results remain visible even when not injected into the LLM context.

### Provider decorators

`ProjectorLlmProvider` is a decorator `ILlmProvider` that coalesces assistant content/thinking chunks by emitting `LlmReplaceEvent` updates for the active logical turn.

### `LlmConversationBuilder` (immutable builder)

Use immutable collections for safe composition and cloning:

```text
LlmConversationBuilder
  Events: ImmutableList<LlmEvent>
  AddEvent(event: LlmEvent): LlmConversationBuilder
  AddEvents(events: IEnumerable<LlmEvent>): LlmConversationBuilder
  AddStreamEvent(streamEvent: LlmStreamEvent): LlmConversationBuilder
  ReplaceTail(removeCount: int, replacementEvents: IEnumerable<LlmEvent>): LlmConversationBuilder
  Build(): LlmConversation
```

```text
Factory helpers
  LlmConversationBuilder.Create()
  LlmConversationBuilder.FromConversation(conversation)
```

Builder behavior:

1. `AddEvent(LlmEvent)` accepts low-level provider events (including token-level).
2. `AddStreamEvent(LlmStreamEvent)` applies event, replace, and checkpoint stream items.
3. `ReplaceTail(...)` supports stream coalescing projections by replacing trailing events.
4. Every append produces a new builder instance (persistent immutable semantics).
5. `Build()` returns the canonical `Events` list; projections are derived by consumers.
6. `LlmConversationBuilder` is constructible from empty state (`Create`) or existing conversation (`FromConversation`).

## Session model

### `LlmSession`

An `LlmSession` owns a series of related `LlmConversation` instances.

It contains:

1. Session identity and lifecycle state.
2. One-to-many relationship to `LlmConversation`.

### Suggested structure

```text
LlmSession
  Conversations: IReadOnlyList<LlmConversation>  // ordered series
  CreatedAt / UpdatedAt
```

### `LlmSessionBuilder` (immutable builder)

Use immutable collections for composing session conversation series through event writes:

```text
LlmSessionBuilder
  Conversations: ImmutableList<LlmConversationBuilder>
  AddEvent(event: LlmEvent): LlmSessionBuilder
  AddStreamEvent(streamEvent: LlmStreamEvent): LlmSessionBuilder
  AddEvents(events: IEnumerable<LlmEvent>): LlmSessionBuilder
  AddStreamEvents(streamEvents: IEnumerable<LlmStreamEvent>): LlmSessionBuilder
  Build(): LlmSession
```

```text
Factory helpers
  LlmSessionBuilder.Create()
  LlmSessionBuilder.FromSession(session)
```

Session builder behavior:

1. Events append to the active/latest conversation by default.
2. `LlmReplaceEvent` coalesces the active/latest conversation by replacing its trailing events.
3. `LlmCheckpointEvent` appends a brand new `LlmConversation` to the series.
4. Every append produces a new builder instance (persistent immutable semantics).

## Agent layer model

### `AgentSession`

`AgentSession` is the next semantic layer above `LlmSession`. It wraps one `LlmSession` and owns execution policy/environment concerns.

It contains:

1. Agent-session identity and lifecycle state.
2. A wrapped `LlmSession`.
3. `IAgentExecutionEnvironment` (embodies MCP tool execution and trust-profile enforcement).
4. Sets of available tools exposed to the model.
5. Ordered input queue serviced at the end of every turn.
6. Constructible from `LlmSession` + `IAgentExecutionEnvironment`.

### `Agent`

`Agent` owns multiple `AgentSession` instances and provides named trust-profile management.

It contains:

1. Agent identity and lifecycle state.
2. Agent sessions.
3. Trust profiles indexed by name.
4. A method to start a new `AgentSession`.

### Suggested structure

```text
Agent
  Id
  Sessions: IReadOnlyList<AgentSession>
  TrustProfilesByName: IReadOnlyDictionary<string, AgentTrustProfileEntity>
  StartSession(options): AgentSession
  CreatedAt / UpdatedAt
```

```text
AgentSession
  LlmSession: LlmSession
  ExecutionEnvironment: IAgentExecutionEnvironment
  LlmProvider: ILlmProvider
  Process(input: IAsyncEnumerable<SessionInputEvent>, cancellationToken?): IAsyncEnumerable<AgentSessionUpdate>
```

```text
AgentSession constructors/factories
  AgentSession(llmSession: LlmSession, executionEnvironment: IAgentExecutionEnvironment, llmProvider: ILlmProvider)
  AgentSession.Create(llmSession, executionEnvironment, llmProvider)
```

```text
IAgentExecutionEnvironment
  ExecuteToolCallAsync(toolCall: LlmEvent, cancellationToken?): Task<LlmEvent>
```

```text
IMcpServer
  GetDescription(): string
  GetAgentExecutionEnvironment(): IAgentExecutionEnvironment
```

```text
AgentToolSet
  Name
  Tools: IReadOnlyList<AgentToolDefinition>
```

```text
AgentInputQueue
  Items: ImmutableList<LlmEvent>
  Priority
  Immediacy (Immediate | Queue | Held)
  CoalescingKey?
  Update(existingItems: ImmutableList<LlmEvent>, newItems: ImmutableList<LlmEvent>): bool
```

```text
AgentInputQueueManager
  ImmediateQueue: AgentInputQueue
  InputQueue: IReadOnlyList<AgentInputQueue>
  Process(cancellationToken?): IAsyncEnumerable<AgentSessionUpdate>
  RegisterInputQueue(queue: AgentInputQueue)
  Enqueue(queue: AgentInputQueue, events: IEnumerable<LlmEvent>, interrupt?: bool)
  ServiceQueues(modelTurnIncludedToolCalls: bool)
  Interrupt(queue: AgentInputQueue)
  RequestInterrupt()  // interrupt without queue payload (e.g. ESC)
```

```text
SessionInputEvent
  LlmEvents: LlmEvent[]
  InterruptCurrentResponse: bool
```

```text
AgentSessionUpdate
  LlmSession: LlmSession
  LlmStreamingEvent?: LlmStreamEvent
```

## Tool execution architecture

Tool execution is performed through an RPC model into Docker containers configured from `AgentSession.ExecutionEnvironment`.

The session runtime provides an HTTP proxy server between `LlmConversation` and the container, so HTTP requests flow through a controlled boundary before reaching MCP host processes.

Flow:

1. Model response (via `ILlmProvider`) requests a tool invocation.
2. `AgentSession.ExecutionEnvironment` executes tool calls asynchronously (including policy validation/enforcement) and returns `LlmToolResult`.
3. HTTP request is sent to the session proxy server.
4. Proxy dispatches the HTTP request/RPC call to the appropriate containerized tool runtime.
5. Tool result is returned through the proxy and appended to the conversation.
6. Provider is called again with updated conversation state.

### MCP `agent` server for session collaboration

Keep the MCP surface minimal and primitive-oriented:

1. `list_agent_sessions`
2. `get_agent_session_events`
3. `enqueue_agent_action`
4. `list_agent_session_queues` (includes queue metadata and current queue items)

`enqueue_agent_action` accepts optional `queueName`, `priority`, and `immediacy`.

1. If `queueName` is omitted, `ImmediateQueue` is used by default.
2. If enqueue uses `interrupt` and `queueName` is omitted, a temporary queue is created and `AgentSession.Interrupt(tempQueue)` is called, so existing named queues are not cleared/consumed.
3. If `queueName` or `priority` is provided, a queue with that MCP-associated name is created (if needed) with the provided/default priority.
4. If `immediacy` is provided, the target queue is updated with that immediacy value.
5. If enqueue is called with no content items, it performs queue metadata update only (priority/immediacy) on the target queue.
6. If enqueue uses `interrupt`, `AgentSession.Interrupt(targetQueue)` is called for that queue; queue immediacy is not mutated by this operation.
7. Queue items own immutable `Items` lists updated via compare-and-swap so multiple producers can safely enqueue/coalesce items for a single turn.

Queue semantics:

1. Each `AgentSession` has named queues plus a built-in `ImmediateQueue`.
2. At queue-service time, higher priority queues are delivered first.
3. `Immediacy=Immediate` queues are delivered on the next model `done`, even when that turn includes tool calls.
4. `Immediacy=Queue` queues are delivered only when the model turn is `done` without tool calls.
5. `Immediacy=Held` queues are not delivered.
6. `AgentSession.Interrupt(targetQueue)` atomically interrupts the active stream and inserts that queue's current items into conversation flow, but does not change queue immediacy.
7. `AgentSession.Interrupt(targetQueue)` is the single interrupt entrypoint for queue interruption/insertion behavior.
8. `AgentSession` services its input queues at the end of every turn.
9. Queue containers are persistent and are not removed when serviced; their `Items` are emptied after delivery so producers can continue appending to the same queue.
10. Multiple queued items may be coalesced into one delivered turn input.
11. Queue mutation/consumption uses CAS via `Update(existingItems, newItems)`; the update succeeds only when `existingItems` matches the current immutable list.
12. Temporary interrupt queues created for unnamed interrupt enqueue are one-shot and do not replace or clear existing named queues.

### MCP `meta` server for tool management

Expose minimal host-level tool lifecycle operations:

1. `list_tools`
2. `stop_tool`

### MCP command-execution tool set

Commands are allowlisted by the effective trust profile in the concrete `AgentExecutionEnvironment` implementation. LLM requests execution by `commandId` only.

1. `list_commands` (returns command ids, descriptions, trust-profile availability, interactivity flags)
2. `start_command` (starts by `commandId`, returns `commandExecutionId`)
3. `read_command_output` (pull output with sequence/cursor)
4. `send_command_input` (only when command allows input redirection)
5. `control_command` (only when command allows TTY control)
6. `get_command_status`
7. `stop_command`

### Command event model (event-driven MCP)

Use streaming notifications/events as primary mechanism:

1. `command.started`
2. `command.stdout`
3. `command.stderr`
4. `command.status_changed`
5. `command.exited` (terminal event, includes `exitCode`)

No `command.prompt`, `command.completed`, or `command.failed` events are required.

### Host-environment responsibilities (not MCP tools)

1. `create_checkpoint`
2. `list_checkpoints`
3. Ordered event persistence and sequencing
4. `IAgentExecutionEnvironment` policy resolution and enforcement
5. HTTP proxy routing to container runtime
6. Meta MCP tool lifecycle coordination (`list_tools`, `stop_tool`)

## Agent execution environment and isolation principles

1. **Deny by default**: no tool/resource access unless explicitly granted by `AgentSession.ExecutionEnvironment`.
2. **Execution-context-scoped enforcement**: `IAgentExecutionEnvironment` is attached to `AgentSession` and applied while processing `LlmSession` conversation events.
3. **Container isolation**: tools run in execution-environment-configured Docker containers.
4. **Auditability**: all tool requests/results are persisted in `LlmConversation`.
5. **Least privilege**: assign the minimum trust profile capabilities needed per session.

## Detailed Agent trust profile model (proposed)

### Goals

1. Express what actions the LLM may perform.
2. Scope those actions to explicit resources.
3. Produce a deterministic artifact that Docker/container startup can enforce.

### Core types

```text
AgentTrustProfileEntity (user-semantic entity)
  Name
  BaseTrustProfileNames: IReadOnlyList<string>
  HostingWorkspacesClientInstances: IReadOnlyList<string>  // "." means local client
  MountPoints: IReadOnlyList<TrustMountPoint>
  NetworkAccessPolicy: TrustNetworkAccessPolicy
  HttpsProxyPolicy: TrustHttpsProxyPolicy
  AllowedMcpToolCallSchemas: IReadOnlyList<JsonObject>  // combined via anyOf
  AllowedCommands: IReadOnlyList<TrustedCommand>
```

```text
AgentTrustProfile (runtime/composed form)
  HostingWorkspacesClientInstances: IReadOnlyList<string>
  MountPoints: IReadOnlyList<TrustMountPoint>
  NetworkAccessPolicy: TrustNetworkAccessPolicy
  HttpsProxyPolicy: TrustHttpsProxyPolicy
  AllowedMcpToolCallSchema: JsonObject  // fully composed anyOf schema
  AllowedCommands: IReadOnlyList<TrustedCommand>
```

```text
TrustedCommand
  CommandId
  Description
  Program
  Args: IReadOnlyList<string>
  AllowInputRedirection: bool
  AllowTtyControl: bool
```

```text
TrustMountPoint
  SourcePath
  TargetPath
  AccessMode (ReadOnly | ReadWrite)
  Type (Bind | Volume | Tmpfs)
```

```text
TrustNetworkAccessPolicy
  None
  LocalNetwork
  NattedNetwork
  HostNetwork
```

```text
TrustHttpsProxyPolicy
  Disabled
  Required(ProxyUrl, OptionalCredentialsReference)
  Optional(ProxyUrl, OptionalCredentialsReference)
```

### Inheritance and composition

1. Trust profiles may inherit from zero or more base trust profiles (`BaseTrustProfileNames`).
2. Effective mount points, network policy, proxy policy, and MCP schema are produced by deterministic merge rules.
3. MCP schema composition uses `anyOf` for inherited profile schemas plus local profile schema constraints.
4. Cycles in base-profile inheritance are invalid.
5. Runtime `AgentTrustProfile` strips user semantics (no `Name`, no `BaseTrustProfileNames`).

### MCP tool-call schema note

MCP tools expose input schemas via JSON Schema, and tool calls pass arguments as JSON values.  
Trust profiles should store and enforce real JSON schema objects (not identifiers only), and reject calls whose payload does not validate against the configured composed schema policy.

For effective `AllowedMcpToolCallSchemas`, compose a single `anyOf` schema at runtime to validate the tool-call envelope:

```json
{
  "type": "object",
  "required": ["toolName", "input"],
  "anyOf": [
    {
      "properties": {
        "toolName": { "const": "read_file" },
        "input": { "$ref": "#/$defs/readFileInput" }
      }
    },
    {
      "properties": {
        "toolName": { "const": "write_file" },
        "input": { "$ref": "#/$defs/writeFileInput" }
      }
    }
  ]
}
```

Equivalent `if/then` composition is also valid if preferred.

### Docker file-permission mapping constraints

1. Docker enforces file access primarily via **what is mounted** and whether the mount is **read-only**.
2. Docker does not natively enforce per-file-extension or subpath deny rules inside a mounted path; those remain host/tool-layer checks.
3. To represent denies, prefer composing narrower allow mounts and avoid mounting denied host paths at all.
4. Read-only trust mounts should materialize to read-only mounts (`:ro` / equivalent).
5. Read-write trust mounts should materialize to read-write mounts only for explicitly granted targets.
6. `Tmpfs` mounts should be used for scratch paths that must not persist to host storage.

### Agent-layer integration

`LlmSession` remains conversation-only.  
`AgentSession` carries `IAgentExecutionEnvironment` used for tool execution.

### `LlmMcpHostConfig` (execution-environment implementation configuration)

`LlmMcpHostConfig` defines what MCP servers are available in the runtime environment and how they are started.

```text
LlmMcpHostConfig
  Servers: IReadOnlyList<LlmMcpServerConfig>
```

```text
LlmMcpServerConfig
  ServerName
  ImageOrCommand
  StartupArguments
  EnvironmentVariables
  WorkingDirectory?
  StartupTimeout?
  HealthCheck?
```

Separation rule:

The concrete `AgentExecutionEnvironment` implementation composes `LlmMcpHostConfig` + effective `AgentTrustProfile` into one runtime contract for tool execution.

### Docker materialization

`DockerContainerTrustProfileMaterializer` converts the effective trust profile from the concrete execution-environment implementation into container-enforced settings:

1. Allowed MCP server/tool map (and optional schema policy) for invocation enforcement.
2. OS-specific Docker mount definitions (bind/volume/tmpfs) for file scopes, with read-only/read-write access modes.
3. Environment variable allowlist injection.
4. Egress/network policy inputs for outbound HTTP scopes.
5. Process allowlist arguments for tool host enforcement.

`DockerContainerBuilder` materializes MCP host configuration from the concrete execution-environment implementation into container startup configuration (images/commands/args/env) and `DockerContainerTrustProfileMaterializer` materializes its effective trust profile into enforcement policy.

The materialized trust-profile artifact should be written once at container start and treated as immutable for the lifetime of that container instance.

## Relationships summary

```text
Agent 1 --- * AgentSession 1 --- 1 LlmSession 1 --- * LlmConversation
LlmConversation 1 --- * LlmEvent (strict global order)
LlmConversation -> ILlmProvider (for model interaction)
Agent -> AgentTrustProfile (indexed by name)
AgentSession -> IAgentExecutionEnvironment (runtime)
AgentSession/LlmSession/LlmConversation -> RPC Tool Runtime (Docker, policy-scoped)
AgentSession 1 --- 1 AgentInputQueueManager 1 --- * AgentInputQueue
AgentInputQueueManager -> SessionInputEvent -> AgentSession.Process(...)
```

## Initial implementation notes

1. Start with immutable `LlmEvent` and `LlmStreamEvent` records even if backed by mutable aggregates.
2. Keep `ILlmProvider` minimal and capability-oriented to support multiple providers safely.
3. Treat RPC tool contracts as versioned APIs.
4. Persist enough metadata to reconstruct exact model/tool execution paths.
5. Implement provider composition/decorators (for example `PreProvidedContentLlmProvider`) instead of embedding cross-cutting prefix logic in concrete transport providers.

## Proposed assemblies and starter classes (review before implementation)

### `Phantom.Workspaces.Llm` (core abstractions)

Primary responsibility: provider/session/conversation contracts and event model.

Proposed classes and interfaces:

1. `ILlmProvider`
2. `PreProvidedContentLlmProvider`
3. `LlmEvent`
4. `LlmStreamEvent`
5. `LlmReplaceEvent`
6. `LlmCheckpointEvent`
7. `LlmConversation`
8. `LlmConversationBuilder`
9. `LlmSession`
10. `LlmSessionBuilder`
11. `Agent`
12. `AgentSession`
13. `IAgentExecutionEnvironment`
14. `AgentTrustProfileEntity`
15. `AgentTrustProfile`
16. `AgentInputQueue`
17. `AgentInputQueueManager`
18. `SessionInputEvent`
19. `AgentSessionUpdate`
20. `AgentToolSet`
21. `LlmMcpServerConfig`
22. `LlmMcpHostConfig`
23. `ILlmToolRuntime`
24. `ILlmContainerBuilder`
25. `LlmContainerBuildRequest`
26. `LlmContainerDefinition`
27. `LlmContainerInstance`
28. `IMcpServer`

### `Phantom.Workspaces.Llm.Docker` (Docker implementation)

Primary responsibility: Docker-specific construction and lifecycle of trust-profile-scoped tool runtime containers.

Proposed classes:

1. `ILlmContainerBuilderFactory`
2. `DockerLlmToolRuntime : ILlmToolRuntime`
3. `DockerContainerTrustProfileMaterializer`
4. `WindowsLlmContainerBuilder : ILlmContainerBuilder`
5. `LinuxLlmContainerBuilder : ILlmContainerBuilder`
6. `MacOsLlmContainerBuilder : ILlmContainerBuilder`

Rationale:

1. Docker mount semantics, shell invocation, user mapping, and path handling differ across Windows, Linux, and macOS hosts.
2. Each OS builder should produce the same `LlmContainerDefinition` contract, but with OS-correct wiring.
3. `ILlmContainerBuilderFactory` selects the appropriate builder based on host runtime.

### `Phantom.Workspaces.Llm.Mcp.Host` (host process for MCP servers)

Primary responsibility: run inside container, start configured MCP servers, expose RPC surface for tool invocation.

Proposed classes:

1. `McpServerHost`
2. `McpServerHostOptions`
3. `McpServerRegistration`
4. `IMcpServerProcessLauncher`
5. `McpServerProcessLauncher`
6. `AgentMcpServer` (agent-session manipulation tools)
7. `MetaMcpServer` (`list_tools`, `stop_tool`)

### `Phantom.Workspaces.Agent.Cli` (interactive terminal agent)

Primary responsibility: local interactive host for agent sessions using `AgentSession` + `AgentInputQueueManager`.

Command-line shape:

1. `--provider ollama --model <name> --think <value> --endpoint <url>`
2. `--provider echo`

Behavior:

1. Uses `AgentExecutionEnvironmentDispatcher.Empty` as execution environment.
2. Uses `AgentInputQueueManager.Process()` to drive `AgentSession.Process(...)`.
3. Reads one user line per prompt (` > `), enqueues as `LlmEvent` turn input.
4. SIGINT/Ctrl+C requests interruption via queue-manager interrupt signal.
5. Renders stream updates live; handles `LlmReplaceEvent` by replacing previously rendered assistant text in-place.

## Proposed runtime flow

1. `Agent` starts an `AgentSession`; `LlmSession` is created as its wrapped conversation container.
2. `AgentSession` is constructed from `LlmSession` + `IAgentExecutionEnvironment`; concrete execution-environment implementations may include trust profile + MCP host/runtime capabilities beyond the interface.
3. `ILlmContainerBuilderFactory` resolves an OS-specific builder (`WindowsLlmContainerBuilder`, `LinuxLlmContainerBuilder`, `MacOsLlmContainerBuilder`).
4. The selected `ILlmContainerBuilder` builds `LlmContainerDefinition` from `AgentSession.ExecutionEnvironment`.
5. `ILlmToolRuntime` starts a container instance from the definition.
6. Session HTTP proxy server is initialized for container-bound tool requests.
7. Container launches `Phantom.Workspaces.Llm.Mcp.Host`, which starts configured MCP servers including agent-session and meta-tool-management servers.
8. `LlmConversation` tool calls are dispatched as HTTP requests to the proxy, which forwards them to the host.
9. Tool results return through the proxy to conversation state and are submitted to `ILlmProvider`.
10. `AgentInputQueueManager` services queues and feeds `SessionInputEvent` batches into `AgentSession.Process(...)`.
