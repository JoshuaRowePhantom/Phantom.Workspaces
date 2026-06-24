# Subagent toolset design

## Purpose

Define how an agent can create, manage, and communicate with subordinate agent sessions
(subagents). Covers agent definition schema extensions, the `IToolsetFactory` surface,
persistence, trust integration, GUI touch-points, and tool visualization.

Related prior art in this repository:
- [`workspace-entity-toolset-factory.md`](workspace-entity-toolset-factory.md) — toolset factory pattern + DAL concurrency rules
- [`tool-entity-implementation-design.md`](tool-entity-implementation-design.md) — `IWorkspaceTool`, temporary subagent sessions via `EntityClassifierTool`
- [`trust-models.md`](trust-models.md) — trust profile composition + enforcement layers
- [`llm-session.md`](llm-session.md) — session/conversation model, `AgentInputQueueManager`, MCP `agent` server
- [`agent-gui.md`](agent-gui.md) — `AgentRuntimeBrowserControl` (`"sub-agent"` node kind), two-zone layout
- [`tool-visualization.md`](tool-visualization.md) — `IToolVisualizerFactory`, `ToolVisualizationContext`
- [`session-context-tools.md`](session-context-tools.md) — `CurrentSessionContext`, session resume pattern
- [`llm-trust-profile.md`](llm-trust-profile.md) — `LlmTrustProfileEntity` / `LlmTrustProfile` split

---

## Agent definition schema extensions

### `sub-agents` array on `PromptAgent`

Each entry declares one subagent the parent agent is allowed to create. The entry
carries the subagent's definition (inline or by reference) and the trust profile it
will be executed under.

```json
{
  "kind": "prompt",
  "name": "orchestrator",
  "model": { "id": "gpt-4o", "provider": "github-models" },
  "instructions": "...",
  "tools": [...],
  "sub-agents": [
    {
      "name": "research-agent",
      "definition": {
        "kind": "prompt",
        "model": { "id": "gpt-4o-mini", "provider": "github-models" },
        "instructions": "You are a focused research assistant.",
        "tools": [{ "kind": "web-search" }]
      },
      "trust-profile": {
        "$ref": { "entity-name": ["trust-profiles", "web-read-only"] }
      }
    },
    {
      "name": "code-agent",
      "definition": {
        "$ref": { "entity-name": ["agent-definitions", "my-code-agent"] }
      },
      "trust-profile": {
        "hosting-workspaces-client-instances": ["."],
        "network-access-policy": "no-network",
        "allowed-mcp-tool-call-schemas": [
          { "properties": { "toolName": { "enum": ["view", "edit", "create", "glob", "grep"] } } }
        ]
      }
    }
  ]
}
```

### `SubagentDefinition` schema shape

```json
{
  "name": "string (required) — local alias used as tool parameter; unique within parent definition",
  "definition": "inline PromptAgent OR { \"$ref\": entity-name-array }",
  "trust-profile": "inline LlmTrustProfile object OR { \"$ref\": entity-name-array } OR omitted"
}
```

**Trust profile resolution rules:**

1. If `trust-profile` is a `$ref` → resolved via `ITrustProfileProvider` at session creation time.
2. If `trust-profile` is an inline object → used directly; no entity lookup.
3. If `trust-profile` is omitted → the subagent uses the parent's effective trust profile
   directly. No composition step; the subagent runs under exactly the same restrictions
   as the parent.
4. When a `trust-profile` **is** specified (inline or `$ref`), the resolved profile is
   composed with the parent's effective profile using restrictive intersection
   (`TrustProfileComposer`). A subagent can never exceed the parent's permissions.

### Agent manifest support

In an `AgentManifest`, subagent definitions are expressed as `SubagentResource` entries
alongside `ToolResource`. The manifest's template `PromptAgent` references them by name.
`AgentFactory.CreateAgentDefinitionAsync` resolves each `SubagentResource` by loading and
validating the referenced definition entity, then populating `PromptAgent.SubAgents`.

```json
{
  "kind": "agent-manifest",
  "template": { "kind": "prompt", "name": "orchestrator", "sub-agents": [] },
  "resources": [
    { "kind": "tool-resource", ... },
    {
      "kind": "sub-agent-resource",
      "name": "research-agent",
      "definition": { "$ref": { "entity-name": ["agent-definitions", "research-agent"] } },
      "trust-profile": { "$ref": { "entity-name": ["trust-profiles", "web-read-only"] } }
    }
  ]
}
```

---

## Toolset factory

### Tool kind string

`"agent-session"` — matched by `AgentSessionToolsetFactory`.

### `AgentSessionToolsetFactory`

```csharp
public sealed class AgentSessionToolsetFactory : IToolsetFactory
{
    // Closed-over context: the parent's SubagentManager and the parent's session context.
    // Created by AgentFactory.CreateAgentChatAsync when the definition has sub-agents.
    public AgentSessionToolsetFactory(
        SubagentManager subagentManager,
        CurrentSessionContext currentSessionContext,
        IToolsetFactory? underlyingToolsetFactory = null)
    { ... }

    public Task<IToolset?> CreateToolsetAsync(
        AgentSchema.Tool tool,
        AgentServices agentServices)
    { ... }
}
```

The toolset is injected into the parent `AgentChat`'s tool chain the same way as
`WorkspaceEntityToolsetFactory` — composed via `Combine(...)` in `AgentFactory`.

### Tool surface

All tools accept a `session_id` parameter whose value is extensible (see
[Session ID resolution](#session-id-resolution) below).

#### `agent_session_create`

Creates and starts a new subagent session from a named definition declared in
`sub-agents`.

| Parameter | Type | Description |
|---|---|---|
| `definition_name` | `string` (required) | Local alias from `sub-agents[*].name` |
| `initial_message` | `string` (optional) | First user message to enqueue after creation |

Returns:
```json
{
  "session_id": "string",
  "status": "running | idle | error",
  "created_at": "ISO 8601"
}
```

Errors if `definition_name` is not in the parent's `sub-agents` list, or if the trust
profile cannot be resolved or is incompatible with the current host.

#### `agent_session_list`

Lists all subagent sessions owned by the current agent session.

| Parameter | Type | Description |
|---|---|---|
| `definition_name` | `string` (optional) | Filter to sessions from a specific definition |
| `status` | `string` (optional) | `"running"`, `"idle"`, `"stopped"`, `"error"` |

Returns an array of session descriptors:
```json
[
  {
    "session_id": "string",
    "definition_name": "string",
    "status": "running | idle | stopped | error",
    "created_at": "ISO 8601",
    "last_activity_at": "ISO 8601 | null"
  }
]
```

#### `agent_session_get`

Gets the current status and running items of a session.

| Parameter | Type | Description |
|---|---|---|
| `session_id` | `string` (required) | See [Session ID resolution](#session-id-resolution) |

Returns:
```json
{
  "session_id": "string",
  "definition_name": "string",
  "status": "running | idle | stopped | error",
  "is_busy": true,
  "running_items": [
    { "role": "assistant | tool", "preview": "first 200 chars..." }
  ],
  "last_activity_at": "ISO 8601 | null"
}
```

#### `agent_session_send`

Injects text into a subagent's input queue.

| Parameter | Type | Description |
|---|---|---|
| `session_id` | `string` (required) | Target session |
| `text` | `string` (required) | Text to enqueue |
| `immediacy` | `string` (optional) | `"immediate"` or `"queue"` (default: `"queue"`) |

Equivalent to the parent calling `EnqueueUserMessage` on the target `AgentChat`.
Returns `{ "ok": true }` or an error object.

#### `agent_session_stop`

Stops a running session by calling `Interrupt()` and optionally disposing the session.

| Parameter | Type | Description |
|---|---|---|
| `session_id` | `string` (required) | Session to stop |
| `dispose` | `bool` (optional) | If `true`, disposes and removes the session (default: `false` = interrupt only) |

Returns `{ "ok": true }` or an error.

#### `agent_session_read_events`

Reads events from a session's history with optional filtering and pagination.

| Parameter | Type | Description |
|---|---|---|
| `session_id` | `string` (required) | Session to read from; `"."` = current session |
| `after_timestamp` | `string` (optional) | ISO 8601 cursor; only events after this time |
| `event_types` | `string[]` (optional) | Filter: `"user"`, `"assistant"`, `"tool_call"`, `"tool_result"`, `"diagnostic"` |
| `search` | `string` (optional) | Full-text substring match against event content |
| `limit` | `int` (optional) | Max events to return (default 20, max 200) |

Returns:
```json
{
  "events": [
    {
      "timestamp": "ISO 8601",
      "event_type": "user | assistant | tool_call | tool_result | diagnostic",
      "role": "user | assistant | tool",
      "content_preview": "first 500 chars",
      "has_more_content": true
    }
  ],
  "total_matching": 42,
  "next_cursor": "ISO 8601 | null"
}
```

`session_id: "."` enables an agent to introspect its own session history — useful for
summarization, context recovery, and self-correction tools.

#### `agent_session_wait`

Waits until a session produces new output or a timeout elapses. Designed for a parent
agent that dispatches work to a subagent and wants to poll for completion without tight
busy-waiting.

| Parameter | Type | Description |
|---|---|---|
| `session_id` | `string` (required) | Session to wait on |
| `timeout_seconds` | `int` (optional) | Max wait time (default 30, max 300) |
| `wait_for_idle` | `bool` (optional) | If `true`, returns only when `is_busy` becomes `false` |

Returns the same shape as `agent_session_get` plus new events since the last call:
```json
{
  "session_id": "string",
  "status": "idle | running | stopped | error | timeout",
  "new_events": [ ... ]
}
```

### Session ID resolution

The `session_id` string in all tools is resolved in the following order:

1. **`"."` or omitted** → the current agent's own session (self-introspection).
2. **Named alias** (matches a `sub-agents[*].name`) → the most recently created live
   session for that definition name.
3. **Full session ID string** (UUID or opaque string) → direct lookup in the
   `SubagentManager`. Subject to ownership check: the agent may only query sessions it
   created, unless the trust profile explicitly grants cross-session access.
4. **Future: entity-name array** (serialized as `"[\"agent-sessions\", \"some-id\"]"`) →
   looked up in the persistence store, enabling inspection of other agents' archived
   session histories.

---

## `SubagentManager`

`SubagentManager` is a component owned by the parent `AgentChat`. It tracks live
subagent `AgentChat` instances, manages their lifecycle, and is passed to the
`AgentSessionToolsetFactory` closure.

```csharp
public sealed class SubagentManager : IAsyncDisposable
{
    // Maps definition_name → list of sessions (most recent first)
    // All AgentChats are registered as owned resources on the parent AgentChat.

    public Task<SubagentSession> CreateAsync(
        string definitionName,
        SubagentDefinition definition,
        AgentServices services,
        CancellationToken ct);

    public IReadOnlyList<SubagentSession> GetAll();
    public IReadOnlyList<SubagentSession> GetByDefinitionName(string name);
    public SubagentSession? GetById(string sessionId);
    public SubagentSession? ResolveSessionId(string sessionIdParam); // applies resolution order above
}

public sealed class SubagentSession
{
    public string SessionId { get; }
    public string DefinitionName { get; }
    public AgentChat Chat { get; }
    public DateTimeOffset CreatedAt { get; }
}
```

The parent `AgentChat` calls `SubagentManager.DisposeAsync()` in its own `DisposeAsync`,
which in turn disposes all child `AgentChat` instances. This guarantees that all
subagent CLI processes are cleaned up when the parent is torn down.

---

## Persistence

### Session hierarchy

Subagent sessions are persisted as children of the parent session in the
`IAgentPersistenceStore`. A new relationship entity links them:

```json
{
  "entity-types": ["relationship", "agent-subagent-session-relationship"],
  "participants": {
    "parent-session": "<parent-agent-session-id>",
    "child-session": "<child-agent-session-id>",
    "child-definition-name": "<local alias string>"
  }
}
```

Alternatively, the child `agent-session` entity gains a `parent-session-id` field
(simpler; no separate relationship entity needed for a pure parent→child hierarchy
with no many-to-many requirement).

### `agent-session.json` additions

```json
{
  "parent-session-id": {
    "type": "string",
    "description": "Session ID of the parent agent that created this subagent session."
  },
  "definition-name": {
    "type": "string",
    "description": "Local alias from the parent's sub-agents list."
  }
}
```

### Resume behavior

When a parent session is resumed:
1. The `AgentPersistenceStoreFactory` loads child session records for the parent's
   session ID (querying `parent-session-id`).
2. The `SubagentManager` is pre-populated with metadata about prior subagent sessions.
3. Child sessions are NOT automatically restarted — they are surfaced in the
   `AgentRuntimeBrowserControl` as `"stopped"` nodes and can be restarted via
   `agent_session_create` with the same `definition_name` (which picks up persisted
   history) or via a GUI action.

---

## Trust model integration

The trust enforcement follows the existing three-layer model from `trust-models.md`:

1. **Computer-set enforcement** — `TrustedExecutorSelector` checks that the resolved
   subagent trust profile permits execution on the current host. If the profile requires
   a remote host, `RemoteTrustedExecutor` is used.
2. **Tool-call validation** — the subagent's `AgentChat` is given an
   `AllowedMcpToolCallSchemas` derived from the resolved (composed) trust profile.
   `TrustToolCallAuthorizer` rejects tool calls not matching the schema.
3. **Container enforcement** — if the trust profile specifies container isolation,
   `DockerContainerTrustProfileMaterializer` enforces mounts/network/proxy at subagent
   session start. This is immutable for the session's lifetime.

**No privilege escalation:** when a subagent specifies a trust profile, the effective
profile is always the restrictive intersection of (a) the declared subagent trust profile
and (b) the parent's own effective trust profile. When no trust profile is declared, the
parent's effective profile is used directly. Either way, a subagent can never exceed the
parent's permissions. This is enforced in `SubagentManager.CreateAsync` before any
`AgentChat` is constructed.

```csharp
// In SubagentManager.CreateAsync:
LlmTrustProfile effectiveProfile;
if (definition.TrustProfile is null)
{
    // No trust profile declared — use parent's directly.
    effectiveProfile = parentEffectiveProfile;
}
else
{
    var subagentRawProfile = await trustProfileProvider.ResolveAsync(
        definition.TrustProfile, ct);
    effectiveProfile = TrustProfileComposer.Compose(
        parentEffectiveProfile,
        subagentRawProfile,
        mode: InheritanceMode.Restrictive);
}
```

---

## GUI touch-points

### `AgentRuntimeBrowserControl` — subagent nodes

The runtime tree already reserves kind `"sub-agent"`. Concrete expansion:

```
AgentRuntimeNode (kind="sub-agent")
  DisplayName: "<definition_name> (<status>)"
  StatusIndicator: spinner (running) | idle circle | red X (error) | grey dot (stopped)
  Children:
    → running-items sub-nodes (if is_busy)
    → "View history" leaf → opens subagent chat panel
    → "Stop" action leaf (if running)
    → "Restart" action leaf (if stopped)
```

Status is live-bound to the subagent's `AgentChat.IsBusy` and last error.

### Subagent chat panel

When a subagent node is focused (single-click or "View history"), a panel opens showing:

- **Output area** — `AgentChatOutputControl` bound to the subagent's `AgentChat`.
  Read-only by default; the `AgentChatInputQueueControl` can be optionally shown for
  manual steering.
- **Definition-name breadcrumb** — `"orchestrator › research-agent"` indicating the
  subagent's lineage.
- **Status badge** — live `IsBusy` indicator.

The panel is hosted in the dock layout alongside the parent agent's panel; the user can
tile them side-by-side.

### Active items zone — subagent cards

When the parent agent is executing and a subagent is active, the parent's active-items
zone shows a subagent invocation card:

```
┌─ research-agent ──────────────────────────── [running] ─┐
│  Last: "Searching for OAuth 2.0 RFC references..."       │
│  [View] [Stop]                                           │
└──────────────────────────────────────────────────────────┘
```

Bound to `SubagentSession.Chat.RunningItems` and `IsBusy`. "View" focuses the subagent
panel; "Stop" calls `Interrupt()`.

### Tool visualization — `AgentSessionVisualizerFactory`

Following the `IToolVisualizerFactory` pattern from `tool-visualization.md`, a new
`AgentSessionVisualizerFactory` handles rendering for all `agent_session_*` tool calls.

| Tool call | Visualization |
|---|---|
| `agent_session_create` | Badge: `+ research-agent` with session ID and link to subagent panel |
| `agent_session_list` | Compact table of sessions with status badges |
| `agent_session_get` | Status card: definition name, is_busy, running-item previews |
| `agent_session_send` | `→ research-agent: "<text>"` inline with queue info |
| `agent_session_stop` | `■ research-agent stopped` |
| `agent_session_read_events` | Expandable event list with type-colored rows and search highlight |
| `agent_session_wait` | `⏳ waiting for research-agent` → `✓ idle after 4.2 s` |

`AgentSessionVisualizerFactory` is added to the `CompositeToolVisualizerFactory` chain
alongside `WorkspaceVisualizerFactory` and `CopilotToolVisualizerFactory`.

---

## Factory and wiring

### `AgentFactory.CreateAgentChatAsync` additions

```csharp
// After resolving the agent definition and before constructing AgentChat:
var subagentDefinitions = (resolvedAgentDefinition as PromptAgent)?.SubAgents ?? [];

SubagentManager? subagentManager = null;
if (subagentDefinitions.Count > 0)
{
    subagentManager = new SubagentManager(
        subagentDefinitions,
        resolvedEffectiveTrustProfile,
        services);
    // SubagentManager is registered as an owned resource on the parent AgentChat.
}

// Build toolset factory chain:
IToolsetFactory toolsetFactory = services.ToolResourceFactory ?? NullToolsetFactory.Instance;
if (subagentManager is not null)
{
    toolsetFactory = new AgentSessionToolsetFactory(
        subagentManager,
        currentSessionContext,
        underlyingToolsetFactory: toolsetFactory);
}
```

The `"agent-session"` tool kind must be declared in the parent's `tools` array to be
active. This follows the same opt-in pattern as `"workspace-entity"` and
`"current-session"` tools:

```json
{
  "tools": [
    { "kind": "agent-session" }
  ]
}
```

---

## Implementation order

1. **Schema** — add `sub-agents` to `agent-definition.json` / `PromptAgent`; add
   `parent-session-id` and `definition-name` to `agent-session.json`.
2. **`SubagentManager.cs`** — lifecycle management; no toolset yet.
3. **`AgentFactory.cs`** — wire `SubagentManager` construction when definition has
   sub-agents; register as owned resource.
4. **`AgentSessionToolsetFactory.cs` + `AgentSessionToolset.cs`** — implement the 7
   tools; unit-testable with an in-memory `SubagentManager` stub.
5. **Tests** — `AgentSessionToolsetTests.cs` covering each tool against a mock
   `SubagentManager`; `SubagentManagerTests.cs` for session ID resolution and lifecycle.
6. **Persistence** — `parent-session-id` query in `AgentPersistenceStoreFactory`; resume
   pre-population.
7. **GUI — runtime browser** — expand `AgentRuntimeNode` for `"sub-agent"` kind with
   status binding and context menu.
8. **GUI — subagent panel** — dock panel; `AgentChatOutputControl` bound to subagent
   `AgentChat`.
9. **GUI — active items cards** — subagent invocation card in parent's active-items zone.
10. **GUI — tool visualization** — `AgentSessionVisualizerFactory`; wire into
    `CompositeToolVisualizerFactory`.
11. **Trust integration** — `TrustProfileComposer` call in `SubagentManager.CreateAsync`;
    `TrustedExecutorSelector` for remote subagent support.

---

## Open questions

1. **Concurrency limit** — should there be a maximum number of live subagent sessions per
   parent? (Avoids runaway agent spawning.) Suggested default: 10, configurable in the
   parent's agent definition or trust profile.

2. **Subagent tools** — should `agent_session_create` also be gated by
   `allowed-mcp-tool-call-schemas` in the parent's trust profile, or is it implicitly
   allowed when the `"agent-session"` tool kind is present in the definition?

3. **Cross-session read access** — the current design limits full-session-id reads to
   subagents the current agent created. A future extension could allow a trust profile to
   grant read access to other agents' sessions for monitoring/orchestration scenarios.

4. **Notification push vs. pull** — `agent_session_wait` is a pull mechanism. A future
   push mechanism (where a subagent completion automatically enqueues a message on the
   parent's `ImmediateQueue`) might reduce round-trips for long-running subagents.

5. **Subagent-of-subagent** — the design supports arbitrary nesting (a subagent's
   definition can itself contain `sub-agents`). Trust profiles compose correctly through
   the hierarchy. No depth limit is imposed by the design; operators may add one via
   trust policy.
