# Remote Agent Sessions

## Problem statement

An agent session currently runs in the Phantom.Workspaces process that opens it. The product has lower-level remote chat-client and component-routing designs, but it has no implemented protocol for a UI to attach to an already-running remote `AgentChat`. It also lacks a coherent rule for combining remote ownership with MXC process containment.

This design makes a session belong to one user-computer-profile, permits an authorized client to start or attach to the owning host, mirrors the remote session into the local UI, and supports safe ownership takeover. It also defines how the full-remote topology composes with per-component executor bindings and the approved MXC design in #1471–#1477.

The central distinction is:

- the remote-agent protocol decides **which host owns and runs `AgentChat`**;
- executor bindings decide **where each component or tool runs**; and
- the effective `TrustProfile` plus MXC decide **how each launched process is confined on that selected host**.

These are orthogonal decisions. A proxy connection is not a sandbox, and an MXC-contained process does not authorize a client to attach to its session.

## Requirements

### Status and compatibility

- Full-remote `AgentChat` ownership and the `attach-agent-session` proxy protocol are **designed here but are not implemented**.
- `ChatClientOverTransport`, `ChatClientTransportSession`, and the split-client design in [`remote-chat-client-session.md`](remote-chat-client-session.md) are reusable lower-level mechanisms. They remote an `IChatClient`/Copilot session, not a complete running `AgentChat`, and must not be described as an existing full-session attach implementation.
- Existing local sessions and legacy session entities must remain usable.
- The new protocol must use the existing `ITransport` / message-channel infrastructure rather than creating a second network stack.

### Session/profile association and persistence

- Every newly created agent-session entity must persist exactly one owning user-computer-profile reference.
- A legacy session with no owner is associated with the current user-computer-profile when it is next started successfully. That write is the only legacy migration.
- Persist the session owner, an ownership generation, executor/profile bindings, the selected trust-profile reference plus expected revision needed to reconstruct effective trust intent, and the explicit per-session `continue-in-background` preference. The preference defaults to `false`; a missing value on a legacy entity is read as `false`.
- Do not persist an MXC SDK object, `SandboxPolicy`, `SandboxRequest`, process handle, policy-file path, or other host-specific runtime state.
- `MxcProcessPolicy`, approved by #1475 as a versioned local execution DTO, may cross only the protected same-machine wrapper handoff defined by #1476. It must not cross a machine transport or be persisted as session authority.
- The agent-session entity-type-view groups sessions by the persisted owning-profile field using the existing `group-by-parent` mechanism. Unstarted legacy sessions remain ungrouped.

### Starting, attaching, and taking over

- Opening a session owned by the current profile starts or reuses the local owning runtime.
- Opening a session owned by another profile offers:
   1. **Connect on owning profile** — start there if absent, otherwise attach to the existing runtime.
   2. **Resume locally** — perform an ownership takeover, terminate the old owning runtime, persist the new owner/generation, and reconstruct locally.
- Before showing that choice, the GUI performs an authorized status query and displays **Running**,
  **Not running**, or **Unavailable** for the owning profile. Unauthorized/not-found are both
  **Unavailable** and reveal no session metadata.
- Every session-opening and restore path must enforce the same decision.
- At most one owning runtime may accept mutations for an ownership generation. Takeover must use an ownership-generation compare/exchange or equivalent lease check.
- If the old owner cannot confirm termination and its ownership lease has not expired, takeover fails closed rather than starting a concurrent local `AgentChat`.
- Takeover rebuilds `AgentServices`, resolves the effective `TrustProfile`, and recompiles any required process confinement on the new host. Runtime MXC state is never migrated.

### Full-remote `AgentChat`

- The owning host constructs the real `AgentChat`, its `AgentServices`, persistence services, tool providers, and current-session identity from that host's `WorkspaceEntitySession` / `CurrentSessionContext`.
- The owning host resolves executor bindings and routes each component to its selected execution host.
- The host that actually launches a process resolves the referenced effective `TrustProfile`, checks the expected revision, compiles MXC locally, and launches through the shared process executor.
- A local proxy mirrors history, queue state, streaming state, busy state, tools, subagents, modals, and errors. It submits user input through the common queue API and forwards modal responses and interrupt/terminate requests.
- The proxy does not launch remote Copilot, stdio MCP, or tool subprocesses and must never claim to enforce their containment.
- Opening a remote subagent creates another authorized proxy to the remote subagent; it does not re-parent the subagent locally.

### Split-client topology

- The topology in [`remote-chat-client-session.md`](remote-chat-client-session.md) remains distinct: the local host owns `AgentChat`, while a remote model host owns the Copilot client/process.
- In split-client mode, the remote model host resolves and compiles MXC for the Copilot process. Each tool's selected executor host independently resolves and compiles MXC for subprocesses it launches.
- Component executor bindings from [`per-component-executor-binding.md`](per-component-executor-binding.md) remain the source of execution placement. Agent ownership does not override an explicit component binding.

### Process and non-process tools

- Copilot CLI containment uses the wrapper and protected one-use local policy handoff from #1476.
- Every stdio MCP launch uses `ProcessExecutorBackedClientTransport` from #1477. A null policy selects the ordinary-process branch; a required policy selects MXC.
- GUI-only and entity-only tools remain in-process application-authorization surfaces. They are not process launches and must not be represented as MXC-contained.
- Constrained HTTP/SSE MCP remains unsupported as specified by #1477 because Phantom does not own the remote server process.
- Tool-schema authorization, client-instance authorization, and MXC confinement are cumulative and remain separate.

### Attach protocol and UI

- `attach-agent-session` is a dedicated bidirectional session protocol, separate from the `chat-client` request/response protocol.
- Initial attach returns an authoritative snapshot followed by ordered deltas. Each frame carries an owning-runtime epoch and monotonically increasing sequence.
- Reconnect supplies the last applied epoch/sequence. The host resumes from retained deltas when possible and otherwise sends a fresh snapshot. Reconnect does not reconstruct `AgentChat` or relaunch children.
- The owning `AgentChat` is authoritative for every input queue. Local and remote GUIs submit acknowledged commands with stable queue/item ids, a command id, and an expected revision; the owner applies each command once and broadcasts the resulting ordered delta. GUI code never receives a mutable owner collection or mutates queue projections optimistically.
- A session snapshot contains every default, immediate, held, and custom queue. Queue deltas share the session epoch/global sequence, so reconnect and multiple attached GUIs converge on the same owner state.
- The proxy is registered in `IRunningAgentChatTable`; the existing `RunningAgentBrainViewModel.Rows` collection and `RunningAgentBrainControl` popup show local owned sessions and remote sessions to which this instance is attached, but do not discover unrelated remote sessions.
- Tools and subagents are mirrored for display and navigation; execution remains on the owning/routed host.
- Each top-level running row exposes interrupt and terminate. A remote row also exposes a **Continue in background** checkbox that updates the persisted session preference through the owner; subagents remain filtered out.
- A session can raise multiple modal requests. Each agent editor displays its own modal stack above its input and gates only that editor. The root editor aggregates descendant modal presence for the tab notification.
- Notifications are keyed by `(tab id, kind)` so future kinds coexist. `chat-idle` clears on tab
  activation; `modal-pending` clears only when all relevant modals are dismissed.
  `NotificationsViewModel.HasUnread` remains the application-wide logical OR of every unread kind.

### Lifecycle

- An owning runtime has a host-owned `RemoteAgentSessionLease`; each attached GUI has an independent `RemoteAgentAttachmentLease`. The runtime serializes attachment-count, retention-preference, and termination transitions under one lifecycle gate.
- `continue-in-background` is an explicit persisted per-session preference, defaults to `false`, and is copied into every runtime snapshot. When the last viewer detaches and the value is `false`, the owner gracefully stops the runtime. When it is `true`, the runtime may continue with zero viewers until explicit termination, takeover, owner-host shutdown, or runtime-lease expiry.
- Explicit detach, proxy disposal, UI tab close, and graceful viewer-application shutdown release that viewer immediately. Unexpected channel/transport loss instead reserves that logical attachment for a five-second reconnect grace period; reconnect with the same attachment token cancels expiry without changing the logical viewer count. When the grace expires, the attachment is released and the last-viewer rule runs.
- A new attach racing with last-viewer stop is serialized by the lifecycle gate. If attachment reservation wins, stop is cancelled and attach receives the existing epoch. If fencing wins, that epoch accepts no attach or mutation; `Attach` returns the indistinguishable not-found result and `StartOrAttach` may create a fresh epoch only after terminal persistence and registry removal complete.
- Multiple viewers are independent: removing any non-final viewer never stops the runtime. Setting `continue-in-background` to `true` preserves a zero-viewer runtime; setting it to `false` while viewers remain changes persistence and the snapshot but does not interrupt the run; setting it to `false` when the viewer count is already zero starts graceful stop immediately.
- Owner-host application shutdown and runtime-lease expiry stop every runtime regardless of the preference. Viewer-application shutdown is only a graceful detach. A host crash cannot emit a terminal frame, but process containment kills owned children and recovery records the interrupted epoch as stopped before any new epoch starts; the persisted preference remains unchanged and does not itself imply auto-resume.
- Graceful stop first fences the epoch so it accepts no new mutations, cancels/interrupts an active turn if needed, disposes `AgentChat`, component transports, MXC/process-executor leases, wrappers, stdio MCP transports, and contained child sessions/trees, persists the terminal completion and stopped state, then removes the registry entry. It emits exactly one `session-terminal` event after persistence and before closing attachment channels where the transport remains writable.
- Interrupt cancels only the active run; terminate ends the owning runtime. These are distinct protocol verbs and UI actions.

### Security and failure semantics

- `attach-agent-session` authenticates the transport peer and authorizes that peer for the requested owning profile and session before revealing whether the session exists.
- Attach authorization is evaluated on every initial attach, reconnect, child-subagent attach, mutation verb, and takeover request. Existing execution-target reachability alone is not sufficient proof of session access.
- MXC does not authorize attach, and transport authorization does not sandbox processes.
- A required containment compile, handoff, wrapper, or launch failure fails closed. No layer retries with a null policy or direct uncontained launch.
- Host-local details remain in protected logs. Wire errors contain only a stable error code, safe operation category, retryability, user-safe message, and correlation id. They exclude policy JSON, grants, local paths, environment values, command arguments, stderr containing secrets, native handles, and credentials.
- `operation-error` terminates the affected operation without necessarily terminating the owning session. A fatal runtime error additionally emits `session-terminal`.
- MXC is a preview dependency and must not be described as a production-grade security boundary.

### Non-goals

- Discovering every running session on every profile.
- Migrating live `AgentChat`, process handles, wrapper handoffs, or compiled MXC state between hosts.
- Sending compiled MXC policy across transport.
- Replacing transport authentication with containment or replacing application authorization with MXC.
- Sandboxing in-process GUI/entity operations.
- Merging full-remote and split-client topologies into one ambiguous mode.
- Replacing ConPTY or containing processes Phantom does not launch.

## Options

### Option A — Extend `ChatClientOverTransport`

Add session attach, tools, subagents, modals, lifecycle, and replay to the existing `chat-client` protocol.

**Pros:** reuses an existing adapter and framing.

**Cons:** overloads `IChatClient`, mixes per-run and whole-session lifetimes, and makes reconnect and independent owning-runtime lifetime difficult.

### Option B — Dedicated `attach-agent-session` protocol

Add a session-oriented listener/client pair over existing message channels. Keep `ChatClientOverTransport` focused on the lower-level remote chat-client topology.

**Pros:** clean ownership, authorization, replay, lifecycle, and error boundaries; supports multiple viewers and subagent channels.

**Cons:** adds a second protocol vocabulary and new proxy abstraction.

### Option C — Entity-store mirror plus thin control channel

Read persisted history/tools/subagents through entity observation and use a channel only for live state and control.

**Pros:** less data on the control channel.

**Cons:** creates two ordering domains and cannot reliably reconstruct unpersisted streaming, queue, modal, and lifecycle state.

## Chosen design

### Approach

Choose **Option B**, a dedicated `attach-agent-session` protocol.

The owning host holds a real `AgentChat` behind a host-owned runtime lease. An authorized `RemoteAgentSessionClient` receives a snapshot and ordered event stream and backs the new `RemoteAgentChat` implementation of the new common `IAgentChat` surface. `IRunningAgentChatTable`, `AgentViewModel`, and session tabs consume `IAgentChat`, allowing the existing concrete `AgentChat` and the proxy to participate without subclassing the currently sealed `AgentChat`.

`ChatClientOverTransport` is unchanged in role: it is the lower-level remote `IChatClient` mechanism used by split-client execution. It is not the remote-session proxy.

### Rationale

One ordered session stream avoids entity/control ordering races and gives authorization, reconnect, viewer lifetime, takeover, and sanitized failures explicit homes. A common `IAgentChat` surface resolves the old subclassing ambiguity without making `AgentChat` virtual merely for remoting. Separate owning-runtime and attachment leases permit a short reconnect grace without making viewer lifetime ambiguous: by default the final released attachment stops the runtime, while an explicit persisted preference keeps it alive.

### Relationship to MXC process containment

Remote ownership and containment compose as three axes:

| Axis | Decision | Authority |
|---|---|---|
| Agent placement | Which host owns `AgentChat` and publishes its event stream? | owning-profile binding + `attach-agent-session` host |
| Component placement | Which host executes the model/tool component? | persisted `ExecutorBindings` / connection descriptor |
| Process confinement | How is a process restricted on that host? | launch-host effective `TrustProfile` + #1475 compiler + #1474 executor |

For full-remote operation, the owning host constructs `AgentChat` and routes components. If it launches Copilot or stdio MCP locally, it resolves and compiles there. If a binding sends a component to another host, that final host resolves and compiles there. The viewer never supplies a compiled policy.

For split-client operation, local `AgentChat` owns routing and persistence, the remote model host confines Copilot, and each tool executor confines its own subprocesses. The same launch-host rule applies even though no full-session proxy is involved.

The only approved serialization of `MxcProcessPolicy` is the protected, one-use, same-machine Copilot wrapper handoff in #1476. Machine boundaries carry stable trust-profile identity/revision and component binding intent, never compiled policy or runtime handles.

### Overlap matrix

| Concern | Remote-agent design owns | MXC / executor design owns | Integration rule |
|---|---|---|---|
| Remote `AgentChat` protocol | Start/attach, snapshots, ordered deltas, proxy commands | None | Protocol selects/observes owner; it does not launch or sandbox. |
| Executor routing | Persists and restores session/component intent | `ExecutorBindings` resolves component connection descriptors | Owner routes; final executor host enforces. |
| Trust-profile resolution | Carries stable profile reference + expected revision | #1472 schema/composition and #1475 compiler input | Resolve effective profile again on the launch host; reject stale/missing revision. |
| Process execution | Owns runtime/component lease boundaries | #1474 ordinary/MXC streaming executor | A non-final detach does not dispose executor handles; final detach disposes them unless background continuation is enabled. |
| Copilot wrapper | Reports lifecycle/error events | #1476 wrapper, one-use policy file, direct path when uncontained | Wrapper and handoff are created only on the Copilot launch host. |
| stdio MCP | Routes tool to selected host | #1477 executor-backed MCP transport | Selected host compiles and launches; channel caller sends no policy. |
| Persistence | Owner/generation, bindings, trust intent, history | No runtime MXC persistence | Reconstruct and recompile after restart/takeover. |
| Lifecycle | Owning lease, attachment leases, reconnect grace, background preference, terminate/takeover | Process handle/tree disposal | Session lease owns children; the final released attachment stops it by default. |
| Errors | Sanitized ordered protocol events | Structured local compiler/executor diagnostics | Map detailed host error to safe wire DTO with correlation id. |
| Security | Peer/session attach authorization | Filesystem/network process confinement | Both checks are required and neither substitutes for the other. |

### Impedance mismatches and resolutions

1. **Per-run `IChatClient` lifetime versus persistent `AgentChat` lifetime.**
   **Resolution:** retain `chat-client` for split-client calls and add `attach-agent-session` for whole-session ownership, replay, and multiple viewers.

2. **`AgentChat` is sealed but the UI and running table need a proxy with equivalent behavior.**
   **Resolution:** introduce `IAgentChat`; adapt the existing `AgentChat` to implement it and add `RemoteAgentChat`. Do not subclass or add transport branches throughout `AgentChat`.

3. **Connection descriptors select a host, while MXC policy is host-specific.**
   **Resolution:** persist descriptors and trust references/revisions. The selected launch host resolves, canonicalizes, compiles, and applies policy locally.

4. **#1475 defines a serializable `MxcProcessPolicy`, which could be mistaken for a network DTO.**
   **Resolution:** permit it only in-process and in #1476's protected same-machine wrapper envelope. Remote contracts have no compiled-policy property and reject unknown attempts to add one.

5. **Viewer/channel disposal must normally stop an unobserved remote session without making transient network loss destructive.**
   **Resolution:** separate `RemoteAgentSessionLease` from `RemoteAgentAttachmentLease`, count logical viewers under the runtime lifecycle gate, and reserve an unexpectedly lost attachment for five seconds. Final explicit detach, or grace expiry with `continue-in-background == false`, gracefully stops the runtime; the persisted opt-in is the only ordinary zero-viewer retention path.

6. **Takeover wants continuity, while confinement contains host-local paths, capabilities, temp directories, and handles.**
   **Resolution:** transfer persisted history and stable intent only. Terminate the old runtime, advance ownership generation, rebuild services, and compile fresh policy on the new host.

7. **Transport errors need useful UI messages, while MXC diagnostics may expose paths or policy details.**
   **Resolution:** map local diagnostics to `RemoteAgentOperationError`; retain details under a correlation id in host logs.

8. **Full-remote routing may include GUI/entity tools that are not child processes.**
   **Resolution:** route them according to the application/component binding and enforce normal application authorization. Do not fabricate an MXC guarantee for in-process code.

9. **Interrupt and shutdown have different child-process consequences.**
   **Resolution:** `interrupt` cancels the active turn while retaining the runtime and reusable clients; `terminate-session` releases the runtime lease and disposes all process-backed resources.

10. **Reconnect can be mistaken for session recreation.**
    **Resolution:** reconnect by attachment token, runtime epoch, and replay cursor during the five-second transport-loss grace. Reattach to the existing runtime and use a fresh snapshot when replay is unavailable. After grace expiry stops a default-policy runtime, `Attach` cannot resurrect it and `StartOrAttach` creates a new epoch only after stop completes.

11. **Existing queue classes expose owner objects and use indexes for some edits.**
    **Resolution:** retain `AgentInputQueue`, `AgentChatQueue`, `AgentInputQueueManager`, and
    `AgentChatQueueManager` as local implementation types, but expose immutable queue/item snapshots
    through `IAgentInputQueues`. Assign stable ids at creation/enqueue, use indexes only as placement
    hints, and route every mutation through owner-authoritative revisioned commands.

12. **Steering is an execution decision, not a separate kind of user input.**
    **Resolution:** there is no steering member on `IAgentChat` and no `steer` protocol verb. A GUI
    enqueues through the same queue API whether a run is idle or active. The owning `AgentChat`
    decides from queue immediacy, mode, and current-run state whether to consume that item as
    Copilot steering or as a future turn.

## Detailed design

### Status legend and code organisation

Paths are relative to the Phantom.Workspaces repository. **Existing** means present on `features`;
**Modified** means an existing type whose contract changes; **New** means introduced by this design.
Public visibility is used only where a type crosses an existing project boundary. Protocol
implementation records remain internal.

### Classes and interfaces

#### `IAgentChat` - New

- **Namespace/project/file:** `Phantom.Workspaces.Llm`;
  `Phantom.Workspaces.Llm.Core/IAgentChat.cs`.
- **Visibility/kind:** `public interface : IAsyncDisposable, IServiceProvider`.
- **Responsibility/lifetime/threading:** the UI-facing chat surface shared by the local engine and
  remote proxy. The implementation owns its observable collections and mutates them only on the
  foreground scheduler captured at construction. It deliberately excludes process handles,
  transports, trust profiles, and MXC objects.

```csharp
public interface IAgentChat : IAsyncDisposable, IServiceProvider
{
    AgentInformation Information { get; }
    Usage Usage { get; }
    bool IsBusy { get; }
    AgentChatHistoryCollection History { get; }
    Task HistoryPopulated { get; }
    AgentChatRunningItemCollection RunningItems { get; }
    IAgentInputQueues InputQueues { get; }
    ReadOnlyObservableCollection<IRunningSubAgent> SubAgents { get; }
    ReadOnlyObservableCollection<AgentChatModal> Modals { get; }
    ISlashCommandRegistry SlashCommands { get; }
    event EventHandler? InformationChanged;
    event EventHandler? ToolsChanged;
    event EventHandler? UsageChanged;
    event EventHandler<AgentChatHistoryItem>? TurnCompleted;
    IReadOnlyList<AgentChatToolItem> GetToolSnapshot();
    Task SetToolEnabledAsync(string toolId, bool enabled, CancellationToken ct = default);
    Task RespondToModalAsync(
        string modalId, JsonElement response, CancellationToken ct = default);
    void EnqueueSystemNote(string text);
    void EnqueueHelpNote(string text);
    void EnqueueTransientDiagnostic(string text);
    void Interrupt();
}
```

The two state values are **New public readonly record structs** in
`Phantom.Workspaces.Llm.Core/IAgentChat.cs`, with exactly these public names and positional fields:

```csharp
public readonly record struct Usage(
    long? TotalInputTokenCount,
    long? TotalOutputTokenCount,
    long? TotalCacheReadTokenCount,
    long? TotalCacheWriteTokenCount,
    long? TotalReasoningTokenCount,
    double? TotalSessionCostUsd);

public readonly record struct AgentInformation(
    string AgentSessionId,
    string AgentId,
    string Name,
    string DisplayName,
    string Description,
    bool AcceptsUserInput,
    string? CurrentModelId,
    AgentDefinition AgentDefinition);
```

`Usage` permits null for a metric the provider did not report; counts must otherwise be nonnegative
and cost remains a nonnegative `double` measured in USD. `AgentInformation` requires non-null,
nonblank values for its first five strings and a non-null complete `AgentDefinition`;
`CurrentModelId` is null or nonblank. Because positional record structs cannot enforce this in
generated setters, local publishers and the protocol codec validate values before accepting,
serializing, or publishing them. Local implementations build a complete replacement value before
publishing it. Proxy implementations deserialize and validate a complete replacement value before
one foreground assignment. `UsageChanged` and `InformationChanged` are raised only after that atomic
assignment, once per applied session sequence; observers never see fields from different versions.
Record equality is the intended value equality.

Both records use the existing JSON options and kebab-case protocol naming. After authorization, the
owner serializes the full definition with `AgentDefinition.ToJson()` and the client deserializes it
with `PhantomAgentSchema.AgentDefinitionFromJson(string)`. Every authorized attached GUI receives
the same complete definition in `AgentInformation`; this is mandatory protocol state, not an
optional capability. Authorization completes before runtime lookup, snapshot construction, or
definition serialization. An unauthorized peer receives only the indistinguishable sanitized
denial and no session metadata, definition bytes, existence signal, or redacted `AgentInformation`.
No field-level redaction is applied after authorization.

`AgentSessionIdChanged` and `ModelChanged` are not on the common interface: those values are members
of the atomically replaced `AgentInformation`, and `InformationChanged` is their sole common event.
During implementation migration, existing scalar getters and events may temporarily forward from
`Information`/`Usage` on concrete `AgentChat` for source compatibility. They are not the desired
public design, must not be added to `RemoteAgentChat`, and are removed after local callers migrate.

#### Common input queues - New

- **Namespace/project/file:** `Phantom.Workspaces.Llm`;
  `Phantom.Workspaces.Llm.Core/IAgentInputQueues.cs`.
- **Visibility/kind:** the interfaces, snapshots, result, and status below are public. Concrete local
  and proxy implementations and protocol DTOs are internal.
- **Decision:** yes, introduce one common queue aggregate. It is the only queue surface consumed by
  `AgentViewModel`/`InputQueueViewModel`, and has local and remote implementations with identical
  semantics.

```csharp
public interface IAgentInputQueues
{
    AgentInputQueuesSnapshot Snapshot { get; }
    IReadOnlyList<IAgentInputQueue> Queues { get; }
    IAgentInputQueue DefaultQueue { get; }
    IAgentInputQueue ImmediateQueue { get; }
    event EventHandler? Changed;

    Task<AgentInputQueueCommandResult> CreateQueueAsync(
        AgentInputQueueConfiguration configuration, Guid commandId,
        long expectedRevision, CancellationToken ct = default);
    Task<AgentInputQueueCommandResult> DeleteQueueAsync(
        string queueId, Guid commandId, long expectedRevision,
        CancellationToken ct = default);
    Task<AgentInputQueueCommandResult> EnqueueAsync(
        string targetQueueId, IReadOnlyList<ChatMessage> messages,
        Guid commandId, long expectedRevision, CancellationToken ct = default);
    Task<AgentInputQueueCommandResult> EditAsync(
        string queueId, string itemId, IReadOnlyList<ChatMessage> messages,
        Guid commandId, long expectedRevision, CancellationToken ct = default);
    Task<AgentInputQueueCommandResult> RemoveAsync(
        string queueId, string itemId, Guid commandId, long expectedRevision,
        CancellationToken ct = default);
    Task<AgentInputQueueCommandResult> MoveAsync(
        string sourceQueueId, string itemId, string targetQueueId, string? beforeItemId,
        Guid commandId, long expectedRevision, CancellationToken ct = default);
    Task<AgentInputQueueCommandResult> ConfigureAsync(
        string queueId, AgentInputQueueConfiguration configuration,
        Guid commandId, long expectedRevision, CancellationToken ct = default);
}

public interface IAgentInputQueue
{
    AgentInputQueueSnapshot Snapshot { get; }
    event EventHandler? Changed;
}

public readonly record struct AgentInputQueuesSnapshot(
    long Revision,
    ImmutableArray<AgentInputQueueSnapshot> Queues);

public readonly record struct AgentInputQueueSnapshot(
    string QueueId,
    string Name,
    bool IsDefault,
    bool IsImmediate,
    AgentInputQueueImmediacy Immediacy,
    int Priority,
    string? CoalescingKey,
    long Revision,
    ImmutableArray<AgentInputItemSnapshot> Items);

public readonly record struct AgentInputItemSnapshot(
    string ItemId,
    ImmutableArray<ChatMessage> Messages);

public readonly record struct AgentInputQueueConfiguration(
    string Name,
    AgentInputQueueImmediacy Immediacy,
    int Priority,
    string? CoalescingKey);

public enum AgentInputQueueCommandStatus
{
    Applied,
    Duplicate,
    Conflict,
    Rejected,
}

public readonly record struct AgentInputQueueCommandResult(
    Guid CommandId,
    AgentInputQueueCommandStatus Status,
    string? QueueId,
    string? ItemId,
    long Revision,
    string? ErrorCode,
    AgentInputQueuesSnapshot? CurrentSnapshot);
```

`AgentInputQueue`, `AgentChatQueue`, `AgentInputQueueManager`, `AgentChatQueueManager`, and
`AgentInputItem` remain the owner-side domain/implementation types; they are not serialized or
returned by the common interface. The implementation adds stable nonblank queue ids when default,
immediate, or custom queues are created and stable nonblank item ids when items are enqueued. Record
updates preserve item ids. Indexes may be calculated for display or placement, but commands identify
items only by id. `beforeItemId == null` means append. `MoveAsync` supports reorder within one queue
and movement between queues atomically.

Snapshots are immutable point-in-time values. Their arrays and messages are deep copied through
`Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions`; no `ObservableCollection`, mutable
manager, `AgentInputQueue`, `AgentChatQueue`, `AgentInputItem`, `ResetSession`, or other live owner
reference crosses the interface or wire. `IAgentInputQueue` instances are stable read models for the
life of a queue and atomically replace `Snapshot`; `Queues` is a read-only projection. Affected
queues raise `Changed`, then the aggregate raises `Changed`, after all snapshots have been replaced
on the captured foreground scheduler.

The aggregate revision increases once for every applied queue transaction; each affected queue's
revision also increases once. Every command compares `expectedRevision` with the aggregate revision,
which gives create/delete and cross-queue movement the same deterministic conflict rule. A
cross-queue move returns both affected queue snapshots in the delta. Blank ids/names, empty message
lists, invalid enum values, negative revisions/priorities, unknown queues/items, edits of consumed
items, deletion of default/immediate or nonempty queues, and configuration that changes fixed queue
roles are rejected before mutation. `Immediate`, `Queue`, and `Held` retain the existing
`AgentInputQueueImmediacy` semantics. Hold/release is `ConfigureAsync` with `Held` or the desired
released immediacy; target selection is the explicit `targetQueueId`.

Commands are acknowledged and server-authoritative; there are no optimistic queue mutations.
Malformed caller arguments throw locally. Owner validation failures return `Rejected` with a stable
safe error code and current revision. A stale expected revision returns `Conflict`, performs no
mutation, and includes the complete current aggregate snapshot; the client atomically replaces its
projection, then may retry as a new command after user intent is reconciled. Exact reuse of a
`commandId` and canonical payload returns `Duplicate` with the original result and no delta; reuse
with a different payload returns `Conflict`. Cancellation before write performs no mutation;
cancellation after write cancels only the wait and retrying the same id is safe.

The aggregate and per-queue read models live exactly as long as their owning `IAgentChat`; callers
borrow them and do not dispose them. After chat disposal, mutation methods throw
`ObjectDisposedException`, no further `Changed` events are raised, and the last immutable snapshot
remains readable. The local adapter unsubscribes from both existing queue managers during chat
disposal; the proxy adapter unsubscribes from its session client before that client detaches.

The owner captures queue mutations, including consumption by a run, on its serialized runtime
scheduler and broadcasts one `queue-changed` delta on the existing session stream. Every delta has
the runtime epoch, global session sequence, resulting aggregate revision, affected full queue
snapshots, and removed queue ids. A fresh/reconnect session snapshot contains the full aggregate
snapshot. Replay applies deltas only in epoch/global-sequence order. Gaps, wrong epochs, or
noncontiguous queue revisions trigger normal reconnect/snapshot refresh. Multiple GUIs therefore
converge without sharing collections or trusting a client mutation.

`AgentChatModal` and its content hierarchy are **New public immutable records** in the same
namespace/file:

```csharp
public sealed record AgentChatModal(
    string Id, string OwnerAgentId, string Title, string Body,
    AgentChatModalContent Content);
public abstract record AgentChatModalContent(string Type);
public sealed record FreeformModalContent(
    string? Placeholder, bool IsRequired) : AgentChatModalContent("freeform");
public sealed record MultipleChoiceModalContent(
    IReadOnlyList<JsonElement> Options, bool AllowsMultiple)
    : AgentChatModalContent("multiple-choice");
public sealed record ApprovalModalContent(
    string ApproveLabel, string RejectLabel)
    : AgentChatModalContent("approval");
```

Constructors reject blank ids/owner/title/body/type and invalid or duplicate options/labels, and
clone option elements. The strict discriminator permits new modal content records in later protocol
versions without changing the modal envelope. All getters return the latest
foreground-applied state and never cross transport. `GetToolSnapshot`
returns an immutable point-in-time copy. Note-enqueue methods reject null/blank text; user input uses
`InputQueues.EnqueueAsync`. `SetToolEnabledAsync` and `RespondToModalAsync` validate before mutation, honor pre-write
cancellation, and serialize a command only for `RemoteAgentChat`. A modal response is accepted once
for an unresolved modal owned by this chat. Tool ids must exist.
`Interrupt` is idempotent and affects only the active turn. Events are raised after the corresponding
state mutation, in wire-sequence order for a proxy. `DisposeAsync` is idempotent; local disposal ends
the local chat, while proxy disposal detaches that viewer and can therefore trigger owner-side
last-viewer stop. Existing engine-only public methods remain on `AgentChat` and are not added to
this UI contract.

#### `AgentChat` - Existing, Modified

- **Namespace/project/file:** `Phantom.Workspaces.Llm`;
  `Phantom.Workspaces.Llm.Core/AgentChat.cs`.
- **Visibility/kind:** existing `public sealed class`, additionally implements `IAgentChat`.
- **Behavior:** existing foreground-scheduler guarantees remain. It exposes atomic `Information` and
  `Usage` values and an owner-backed `IAgentInputQueues` adapter over the existing queue managers.
  That adapter is the sole public mutation path used by migrated UI code. Existing concrete scalar
  and index-based queue members are implementation-migration forwarding members only. The new
  cancellation token on the interface tool-toggle member is honored by the existing async mutation
  path. No transport branch is added to this class and it remains sealed.

The current source already makes the correct steering decision at the owner: `CopilotSdkChatClient`
subscribes to `AgentInputQueueManager.QueueStateChanged` only while a turn is live and
`ForwardPendingImmediateMessages` drains immediate items into `CopilotSession.SendAsync` with
`Mode = "immediate"`; `ToolResultSteeringMiddleware` injects immediate items at tool-result
boundaries for ordinary clients. This remains internal implementation behavior. The public
`IChatSteeringTarget` transport capability is narrowed to an internal split-client adapter seam;
no replacement steering method is added to `IAgentChat`,
`RemoteAgentChat`, or `RemoteAgentSessionClient`. Copilot-specific members
`ForwardPendingImmediateMessages` and `SteeringMessageForwarded` remain internal. During teardown,
the existing suspension gate leaves an item queued rather than losing it. For a non-Copilot client,
the same queued input is consumed at the next supported tool boundary or future turn; enqueue still
succeeds and never reports steering as unsupported.

#### `RemoteAgentChat` - New

- **Namespace/project/file:** `Phantom.Workspaces.Llm.Remote`;
  `Phantom.Workspaces.Llm.Core/Remote/RemoteAgentChat.cs`.
- **Visibility/kind:** `public sealed class : IAgentChat`.
- **Responsibility/lifetime/threading:** owns a proxy-only `AgentChatHistoryCollection`, immutable
  queue/running item projections, tools, subagent proxies, slash-command facade, `Usage`, and
  `AgentInformation`. It owns
  one `RemoteAgentSessionClient`, applies frames on its supplied foreground scheduler, and never owns
  the remote runtime.

```csharp
public static Task<RemoteAgentChat> AttachAsync(
    RemoteAgentSessionClient client,
    AgentSessionOpenRequest request,
    TaskScheduler foregroundScheduler,
    CancellationToken ct = default);
public Task DetachAsync(CancellationToken ct = default);
public Task TerminateAsync(CancellationToken ct = default);
// IAgentChat members have the exact signatures above.
```

`AttachAsync` validates arguments, waits for the first authoritative snapshot, then publishes the
object; cancellation before snapshot disposes the channel and publishes nothing. `DetachAsync` sends
best-effort explicit detach once and disposes proxy state; owner-side release stops the runtime when
this is the final viewer and background continuation is disabled. `TerminateAsync` requires the current epoch,
crosses transport, and completes only after a terminal event or safe command error. Commands after
detach/disposal throw `ObjectDisposedException`; stale epoch maps to
`RemoteAgentSessionException("runtime-changed")`. Local-only slash commands are not advertised by the
proxy. Event production follows apply-state-then-notify ordering.

#### `RemoteAgentSessionClient` - New

- **Namespace/project/file:** `Phantom.Workspaces.Llm.Remote`;
  `Phantom.Workspaces.Llm.Core/Remote/RemoteAgentSessionClient.cs`.
- **Visibility/kind:** `public sealed class : IAsyncDisposable`.
- **Responsibility/lifetime/threading:** owns one `ITransport`-opened `IMessageChannel`, one receive
  pump, pending command completions, and the last accepted cursor. It does not mutate UI collections.

```csharp
public event EventHandler<AgentSessionServerFrame>? FrameReceived;
public ReplayCursor? LastAppliedCursor { get; }
public RemoteAgentSessionClient(ITransport transport);
public static Task<AgentSessionRemoteStatus> GetStatusAsync(
    ITransport transport, AgentSessionOpenRequest request,
    CancellationToken ct = default);
public Task ConnectAsync(AgentSessionOpenRequest request, CancellationToken ct = default);
public Task ReconnectAsync(CancellationToken ct = default);
public Task<AgentInputQueueCommandResult> CreateQueueAsync(
    AgentInputQueueConfiguration configuration, Guid commandId,
    long expectedRevision, CancellationToken ct = default);
public Task<AgentInputQueueCommandResult> DeleteQueueAsync(
    string queueId, Guid commandId, long expectedRevision, CancellationToken ct = default);
public Task<AgentInputQueueCommandResult> EnqueueAsync(
    string targetQueueId, IReadOnlyList<ChatMessage> messages,
    Guid commandId, long expectedRevision, CancellationToken ct = default);
public Task<AgentInputQueueCommandResult> EditAsync(
    string queueId, string itemId, IReadOnlyList<ChatMessage> messages,
    Guid commandId, long expectedRevision, CancellationToken ct = default);
public Task<AgentInputQueueCommandResult> RemoveAsync(
    string queueId, string itemId, Guid commandId, long expectedRevision,
    CancellationToken ct = default);
public Task<AgentInputQueueCommandResult> MoveAsync(
    string sourceQueueId, string itemId, string targetQueueId, string? beforeItemId,
    Guid commandId, long expectedRevision, CancellationToken ct = default);
public Task<AgentInputQueueCommandResult> ConfigureAsync(
    string queueId, AgentInputQueueConfiguration configuration,
    Guid commandId, long expectedRevision, CancellationToken ct = default);
public Task InterruptAsync(Guid commandId, CancellationToken ct = default);
public Task TerminateAsync(
    string reason, Guid commandId, CancellationToken ct = default);
public Task<RemoteSubagentDescriptor> OpenSubagentAsync(
    string agentId, Guid commandId, CancellationToken ct = default);
public Task RespondToModalAsync(
    string modalId, JsonElement response, Guid commandId,
    CancellationToken ct = default);
public Task SetToolEnabledAsync(
    string toolId, bool enabled, Guid commandId,
    CancellationToken ct = default);
public Task SetContinueInBackgroundAsync(
    bool continueInBackground, Guid commandId, CancellationToken ct = default);
public Task DetachAsync(CancellationToken ct = default);
public ValueTask DisposeAsync();
```

`GetStatusAsync` requires `OpenIntent = Status`, borrows the transport, authorizes before lookup,
returns `Running` or `NotRunning` to an authorized peer, maps denial/not-found/unsafe failure to
`Unavailable`, closes its one-shot channel, and returns no snapshot or session metadata.
`ConnectAsync` is the initial-open operation and may succeed once; it stores the validated request
and serializes it into
`ITransport.ConnectToMessageChannelAsync`, starts one reader, and completes after snapshot/replay
validation. A second call throws `InvalidOperationException`. After unexpected channel loss,
`ReconnectAsync` may be called while the five-second grace remains; it single-flights reconnect,
reuses the original peer-bound attachment token, forces `OpenIntent = Attach` regardless of the
initial request's intent, supplies `LastAppliedCursor`, replaces the channel and pump, and completes
after replay/snapshot validation. It can therefore reclaim only the reserved existing attachment
and can never start a replacement runtime after grace expiry. Calls while connected, after explicit
detach/terminal/disposal, or after grace expiry throw `InvalidOperationException`. Cancellation
stops only that attempt and leaves another attempt possible before the deadline.
`RemoteAgentChat` automatically calls it after unexpected loss with delays of 250 ms, 500 ms, then
one second until the grace deadline; an accepted terminal/not-found result ends retry.
Each typed command method validates its
payload and nonempty caller-supplied command id, requires a connected nonterminal epoch, serializes
the corresponding strict DTO, and waits for its correlated acknowledgement/error. Cancellation
cancels only the caller's wait after a successful write; command ids make retry safe.
Queue methods are the transport implementation behind the proxy `IAgentInputQueues` and have the
same validation/result semantics as that interface. An applied queue task completes only after the
proxy has applied the authoritative result/delta revision; no caller observes completion against a
stale projection. `OpenSubagentAsync` returns only the authorized child session/open descriptor.
`SetToolEnabledAsync` completes only after the owner-authoritative `tools-changed` event has been
applied; rejection does not alter the proxy tool snapshot.
`SetContinueInBackgroundAsync` completes only after the owner has persisted the preference and the
matching ordered retention event has been applied.
`FrameReceived` is emitted synchronously in validated epoch and
sequence order; gaps, regressions, unknown discriminators, or mismatched correlations close the
channel with `RemoteAgentProtocolException`. `DetachAsync` is idempotent and best effort.
`DisposeAsync` cancels the pump and channel without sending `terminate-session`; releasing the
attachment can still cause default last-viewer stop. The constructor rejects
a null transport; the client borrows the process-scoped transport and owns only its opened channel.

#### `AgentSessionTransportListener` - New

- **Namespace/project/file:** `Phantom.Workspaces.Services.AgentSessions`;
  `Phantom.Workspaces/Services/AgentSessions/AgentSessionTransportListener.cs`.
- **Visibility/kind:** `public sealed class : ITransportListener`.
- **Responsibility/lifetime/threading:** transport dispatch adapter; per accepted channel it returns
  the attachment lease supplied by the host.

```csharp
internal AgentSessionTransportListener(
    RemoteAgentSessionHost host,
    ITransportPeerIdentityProvider peerIdentityProvider);
public Task<IAsyncDisposable?> OnChannelOpenAsync(
    JsonElement request, IMessageChannel channel, CancellationToken ct = default);
public Task<IAsyncDisposable?> OnStreamOpenAsync(
    JsonElement request, Stream stream, CancellationToken ct = default);
public ValueTask DisposeAsync();
```

`OnChannelOpenAsync` returns null for a discriminator other than `attach-agent-session`. For a match,
it uses strict deserialization, obtains authenticated peer identity, and calls the host. Validation
or authorization failures write one sanitized terminal error and close; authorization occurs before
runtime lookup. `OnStreamOpenAsync` always returns null. Disposal stops accepting channels and
releases listener-owned active attachment leases. It does not directly dispose runtime leases, but
final attachment release applies the normal background policy; owner-host application shutdown then
calls `RemoteAgentSessionHost.DisposeAsync`, which fences and stops every remaining runtime
regardless of that policy.

#### Attach identity and authorization - New

- **Namespace/project/files:** transport-authenticated
  `TransportPeerIdentity.cs` and `ITransportPeerIdentityProvider.cs` remain in
  `Phantom.Workspaces.Transport`; `IAgentSessionAttachAuthorizer.cs` is in
  `Phantom.Workspaces/Services/AgentSessions`.
- **Visibility/kind:** `public sealed record TransportPeerIdentity`; two `internal interface`s.
- **Ownership:** each authenticated transport adapter records identity in a channel-keyed feature
  provider. Anonymous or ambiguous channels have no identity and fail closed.

```csharp
public sealed record TransportPeerIdentity(
    string AuthenticationScheme,
    string StablePeerId,
    string? UserEntityId,
    string? UserComputerProfileEntityId);

internal interface ITransportPeerIdentityProvider
{
    TransportPeerIdentity GetRequiredIdentity(IMessageChannel channel);
}

internal interface IAgentSessionAttachAuthorizer
{
    ValueTask<AgentSessionAuthorizationDecision> AuthorizeAsync(
        TransportPeerIdentity peer,
        AgentSessionAuthorizationRequest request,
        CancellationToken ct = default);
}

internal sealed record AgentSessionAuthorizationRequest(
    string AgentSessionId,
    string ExpectedOwningProfileEntityId,
    long ExpectedOwnershipGeneration,
    AgentSessionAuthorizationOperation Operation,
    string? ChildAgentId);

internal readonly record struct AgentSessionAuthorizationDecision(bool IsAllowed);
internal enum AgentSessionAuthorizationOperation
{
    Status, Open, Reconnect, Send, SetToolState, SetBackgroundPreference, Interrupt, Terminate, OpenSubagent,
    ModalResponse, Takeover
}

internal sealed class AgentSessionAttachAuthorizer : IAgentSessionAttachAuthorizer
{
    internal AgentSessionAttachAuthorizer(IDataAccessLayer dataAccessLayer);
}
```

`TransportPeerIdentity` rejects blank authentication scheme/stable peer id. Optional entity ids are
either null or nonblank canonical entity ids; these values are authenticated claims and are not
serialized by the attach protocol.

`GetRequiredIdentity` returns only transport-authenticated claims, never request fields, and throws a
sanitized unauthenticated error when absent. The concrete internal
`AgentSessionAttachAuthorizer(IDataAccessLayer)` loads the session's owning
`user-computer-profile`, resolves that profile's user, and allows attach/mutation only when:
(a) request owner and generation exactly match persistence; (b) `peer.UserEntityId` equals the
owning profile's user entity id; and (c) the authenticated transport's
`UserComputerProfileEntityId`, when present, resolves to that same user. Takeover additionally
requires the proposed new profile to resolve to the same user. Child attach additionally requires
the child id to be in the owning runtime's subagent registry. There is no caller-authored ACL or
request-supplied identity. Authorization runs for open/reconnect and every mutation. Denial is
indistinguishable from nonexistent session on the wire. Cancellation performs no mutation. The
authorizer is stateless and process-scoped.

#### `RemoteAgentSessionHost` - New

- **Namespace/project/file:** `Phantom.Workspaces.Services.AgentSessions`;
  `Phantom.Workspaces/Services/AgentSessions/RemoteAgentSessionHost.cs`.
- **Visibility/kind:** `internal sealed class`.
- **Responsibility/lifetime/threading:** application-layer coordinator between authorization,
  persistence/runtime hydration, the runtime registry, and a transport channel.

```csharp
internal RemoteAgentSessionHost(
    IAgentSessionAttachAuthorizer authorizer,
    IRemoteAgentSessionRuntimeRegistry runtimeRegistry,
    IAgentSessionRuntimeContextFactory runtimeContextFactory);
internal Task<AgentSessionRemoteStatus> GetStatusAsync(
    TransportPeerIdentity peer,
    AgentSessionOpenRequest request,
    CancellationToken ct = default);
internal Task<RemoteAgentAttachmentLease> OpenAsync(
    TransportPeerIdentity peer,
    AgentSessionOpenRequest request,
    IMessageChannel channel,
    CancellationToken ct = default);
internal Task TakeOverAsync(
    TransportPeerIdentity peer,
    AgentSessionTakeoverRequest request,
    CancellationToken ct = default);
internal ValueTask DisposeAsync();
```

`GetStatusAsync` accepts only `OpenIntent.Status`, authorizes before lookup, and exposes no other
runtime state. `OpenAsync` rejects `Status`, authorizes before lookup, validates owner/generation,
then atomically gets or starts the
runtime according to `OpenIntent`; `Attach` never starts, `Start` fails if already running, and
`StartOrAttach`/`Resume` reuse the matching runtime. It creates the attachment before taking a
snapshot so no delta is lost, then emits snapshot-or-replay. `TakeOverAsync` authorizes takeover,
fences mutations, awaits old runtime disposal, compare/exchanges owner/generation, and starts nothing
until persistence succeeds. A live unconfirmed lease returns `takeover-blocked`. Host disposal drains
attachments then runtime leases. No method accepts a compiled policy.

The ownership lease is a persisted compare/exchange record keyed by session and generation. It uses
the data service's authoritative timestamp, is renewed every 10 seconds, and expires after 30 seconds.
Each successful renewal returns the authoritative expiry instant. If renewal fails or is uncertain,
the owner retries once per second but fences the runtime no later than five seconds before the last
confirmed expiry; after fencing it accepts no mutation and performs graceful stop. It never assumes
renewal from a local clock or transient response. Orderly stop releases the lease only after
terminal/stopped persistence; a crashed host cannot renew it.
After expiry, restart/takeover first marks the abandoned epoch stopped, then creates a new epoch.
`continue-in-background` survives that recovery but is not an auto-resume instruction: only an
ordinary open or the existing `auto-resume` entity setting starts the replacement runtime.

#### Runtime and attachment storage - New

- **Namespace/project/files:** `Phantom.Workspaces.Services.AgentSessions`;
  `IRemoteAgentSessionRuntimeRegistry.cs`, `RemoteAgentSessionRuntimeRegistry.cs`,
  `RemoteAgentSessionLease.cs`, `RemoteAgentAttachmentLease.cs`, `AgentSessionReplayBuffer.cs`.
- **Visibility/kind:** registry interface/implementation and leases are `internal`; epoch/cursor value
  types are public protocol values.
- **Ownership:** the process-scoped registry owns one runtime lease per
  `(AgentSessionId, OwnershipGeneration)`. The lease owns execution resources; its lifecycle gate
  also tracks logical attachment count and the persisted background preference.

```csharp
internal interface IRemoteAgentSessionRuntimeRegistry
{
    ValueTask<RemoteAgentSessionLease?> TryGetAsync(
        string sessionId, long ownershipGeneration, CancellationToken ct = default);
    ValueTask<RemoteAgentSessionLease> GetOrStartAsync(
        PersistedAgentSessionRuntimeIntent intent,
        Func<CancellationToken, Task<RemoteAgentSessionLease>> startAsync,
        CancellationToken ct = default);
    ValueTask<bool> TryTerminateAsync(
        string sessionId, long ownershipGeneration, RuntimeEpoch epoch,
        CancellationToken ct = default);
    ValueTask SetContinueInBackgroundAsync(
        string sessionId, long ownershipGeneration, RuntimeEpoch epoch,
        bool continueInBackground, CancellationToken ct = default);
}

internal sealed class RemoteAgentSessionRuntimeRegistry
    : IRemoteAgentSessionRuntimeRegistry, IAsyncDisposable
{
    internal RemoteAgentSessionRuntimeRegistry(TimeProvider timeProvider);
    public ValueTask DisposeAsync();
}

internal sealed class RemoteAgentSessionLease : IAsyncDisposable
{
    internal RuntimeEpoch Epoch { get; }
    internal IAgentChat Chat { get; }
    internal AgentSessionReplayBuffer Replay { get; }
    internal RemoteAgentAttachmentLease Attach(
        string attachmentToken, IMessageChannel channel, ReplayCursor? cursor);
    internal ValueTask SetContinueInBackgroundAsync(
        bool continueInBackground, CancellationToken ct = default);
    public ValueTask DisposeAsync();
}

internal sealed class RemoteAgentAttachmentLease : IAsyncDisposable
{
    internal ReplayCursor Cursor { get; }
    internal ValueTask PublishAsync(
        AgentSessionServerEvent value, CancellationToken ct = default);
    public ValueTask DisposeAsync();
}

internal sealed class AgentSessionReplayBuffer
{
    internal long HighWaterMark { get; }
    internal AgentSessionServerFrame Append(
        Guid correlationId, AgentSessionServerEvent value);
    internal ReplayReadResult ReadAfter(ReplayCursor cursor);
}

internal readonly record struct ReplayReadResult(
    bool IsCovered, IReadOnlyList<AgentSessionServerFrame> Frames);
```

`GetOrStartAsync` is single-flight and returns the existing matching lease; a failed/cancelled factory
is removed. `TryTerminateAsync` succeeds only on exact generation+epoch, marks the registry entry
fenced in place first, then
performs graceful stop once; explicit terminate always wins over attach, reconnect, grace, or
preference changes. Runtime stop rejects new mutations, interrupts an active turn when necessary,
unsubscribes the local event bridge, disposes `AgentChat`, component transports, MXC/process-executor
leases, wrappers, stdio MCP transports, and contained children, persists terminal/stopped state,
emits terminal once before writable channels close, and only then removes the registry entry. A
fenced entry rejects attach/start so no replacement epoch can appear before cleanup completes.

`Attach` subscribes before snapshot capture and increments the logical viewer count under the same
lifecycle gate. Explicit attachment disposal decrements immediately. Unexpected channel loss marks
the attachment token disconnected and starts one `TimeProvider`-driven five-second timer; reconnect
with that token cancels the timer and keeps the count unchanged. Timer expiry releases the viewer.
When a release changes the count to zero and `ContinueInBackground` is false, graceful stop starts.
`SetContinueInBackgroundAsync` validates the exact generation/epoch, persists first, then updates the
runtime and emits `SessionRetentionChangedEvent`; setting false at zero viewers starts stop in the
same serialized transition. Attach, release, grace expiry, and preference updates emit an ordered
`SessionRetentionChangedEvent` whenever either authoritative value changes; snapshots carry both
values. A later attach cannot cancel a fenced stop.

`AgentSessionReplayBuffer` retains the newest 4,096 events subject to an 8 MiB serialized-size cap
and a 15-minute age cap. Append and cursor reads are locked and sequence-monotonic. A reconnect gets
replay only when epoch matches and every sequence after its cursor is retained; otherwise it gets one
new snapshot whose sequence is the current high-water mark. Snapshot generation and event append use
the runtime's serialized scheduler, preventing snapshot/delta gaps.

#### Protocol records and strict codec - New

- **Namespace/project/file:** `Phantom.Workspaces.Llm.Remote`;
  `Phantom.Workspaces.Llm.Core/Remote/AgentSessionProtocol.cs`.
- **Visibility/kind:** `RuntimeEpoch`, `ReplayCursor`, `AgentSessionOpenRequest`,
  `AgentSessionServerFrame`, `AgentSessionOpenIntent`, and `RemoteSubagentDescriptor` are public
  immutable records because the client/core boundary consumes them. Commands, events, and
  `AgentSessionProtocolCodec` are internal.

```csharp
public readonly record struct RuntimeEpoch(Guid Value);
public readonly record struct ReplayCursor(RuntimeEpoch Epoch, long Sequence);
public enum AgentSessionOpenIntent { Status, Start, Attach, StartOrAttach, Resume }
public enum AgentSessionRemoteStatus { Running, NotRunning, Unavailable }

public sealed record AgentSessionOpenRequest(
    int ProtocolVersion,
    string AgentSessionId,
    string ExpectedOwningProfileEntityId,
    long ExpectedOwnershipGeneration,
    AgentSessionOpenIntent OpenIntent,
    string AttachmentToken,
    ReplayCursor? ReplayCursor,
    IReadOnlyList<string> Capabilities);

internal abstract record AgentSessionCommand(
    string Type, Guid CommandId, Guid CorrelationId, RuntimeEpoch RuntimeEpoch);

public sealed record RemoteSubagentDescriptor(
    string AgentSessionId,
    string AgentId,
    string OwningProfileEntityId,
    long OwnershipGeneration,
    RuntimeEpoch RuntimeEpoch);

public sealed record AgentSessionServerFrame(
    int ProtocolVersion,
    string Type,
    Guid CorrelationId,
    RuntimeEpoch RuntimeEpoch,
    long Sequence,
    JsonElement Payload);

internal abstract record AgentSessionServerEvent(string Type, JsonElement Payload);
internal sealed record SessionStatusEvent(AgentSessionRemoteStatus Status);
internal sealed record AgentSessionTakeoverRequest(
    string AgentSessionId,
    string ExpectedOwningProfileEntityId,
    long ExpectedOwnershipGeneration,
    string NewOwningProfileEntityId,
    Guid CorrelationId);
```

The strict version-1 command records are:

```csharp
internal sealed record CreateQueueCommand(
    Guid CommandId, Guid CorrelationId, RuntimeEpoch RuntimeEpoch,
    long ExpectedRevision, AgentInputQueueConfiguration Configuration);
internal sealed record DeleteQueueCommand(
    Guid CommandId, Guid CorrelationId, RuntimeEpoch RuntimeEpoch,
    long ExpectedRevision, string QueueId);
internal sealed record EnqueueInputCommand(
    Guid CommandId, Guid CorrelationId, RuntimeEpoch RuntimeEpoch,
    long ExpectedRevision, string TargetQueueId, JsonElement Messages);
internal sealed record EditQueueItemCommand(
    Guid CommandId, Guid CorrelationId, RuntimeEpoch RuntimeEpoch,
    long ExpectedRevision, string QueueId, string ItemId, JsonElement Messages);
internal sealed record RemoveQueueItemCommand(
    Guid CommandId, Guid CorrelationId, RuntimeEpoch RuntimeEpoch,
    long ExpectedRevision, string QueueId, string ItemId);
internal sealed record MoveQueueItemCommand(
    Guid CommandId, Guid CorrelationId, RuntimeEpoch RuntimeEpoch,
    long ExpectedRevision, string SourceQueueId, string ItemId,
    string TargetQueueId, string? BeforeItemId);
internal sealed record ConfigureQueueCommand(
    Guid CommandId, Guid CorrelationId, RuntimeEpoch RuntimeEpoch,
    long ExpectedRevision, string QueueId,
    AgentInputQueueConfiguration Configuration);
internal sealed record InterruptCommand(
    Guid CommandId, Guid CorrelationId, RuntimeEpoch RuntimeEpoch);
internal sealed record TerminateSessionCommand(
    Guid CommandId, Guid CorrelationId, RuntimeEpoch RuntimeEpoch, string Reason);
internal sealed record OpenSubagentCommand(
    Guid CommandId, Guid CorrelationId, RuntimeEpoch RuntimeEpoch, string AgentId);
internal sealed record ModalResponseCommand(
    Guid CommandId, Guid CorrelationId, RuntimeEpoch RuntimeEpoch,
    string ModalId, JsonElement Response);
internal sealed record SetToolEnabledCommand(
    Guid CommandId, Guid CorrelationId, RuntimeEpoch RuntimeEpoch,
    string ToolId, bool Enabled);
internal sealed record SetContinueInBackgroundCommand(
    Guid CommandId, Guid CorrelationId, RuntimeEpoch RuntimeEpoch,
    bool ContinueInBackground);
internal sealed record DetachCommand(
    Guid CommandId, Guid CorrelationId, RuntimeEpoch RuntimeEpoch);
```

The strict version-1 event records all inherit `AgentSessionServerEvent`; the frame supplies
version/correlation/epoch/sequence exactly once:

```csharp
internal sealed record SessionSnapshotEvent(AgentSessionSnapshot Snapshot);
internal sealed record HistoryAppendedEvent(JsonElement Item);
internal sealed record UsageChangedEvent(Usage Usage);
internal sealed record AgentInformationChangedEvent(AgentInformation Information);
internal sealed record QueueChangedEvent(
    long Revision,
    IReadOnlyList<AgentInputQueueSnapshot> Queues,
    IReadOnlyList<string> RemovedQueueIds);
internal sealed record StreamingStartedEvent(string RunId, JsonElement Item);
internal sealed record StreamingUpdatedEvent(string RunId, JsonElement Update);
internal sealed record StreamingCompletedEvent(string RunId, JsonElement Item);
internal sealed record BusyChangedEvent(bool IsBusy);
internal sealed record ToolsSnapshotEvent(IReadOnlyList<JsonElement> Tools);
internal sealed record ToolsChangedEvent(IReadOnlyList<JsonElement> Tools);
internal sealed record SubagentsSnapshotEvent(IReadOnlyList<JsonElement> Subagents);
internal sealed record SubagentsChangedEvent(IReadOnlyList<JsonElement> Subagents);
internal sealed record ModalRaisedEvent(AgentChatModal Modal);
internal sealed record ModalUpdatedEvent(AgentChatModal Modal);
internal sealed record ModalDismissedEvent(string ModalId);
internal sealed record SessionRetentionChangedEvent(
    bool ContinueInBackground, int ViewerCount);
internal sealed record CommandCompletedEvent(Guid CommandId, JsonElement? Result);
internal sealed record OperationErrorEvent(RemoteAgentOperationError Error);
internal sealed record SessionTerminalEvent(string Reason, JsonElement CompletionState);

internal sealed record AgentSessionSnapshot(
    AgentInformation Information,
    Usage Usage,
    AgentInputQueuesSnapshot InputQueues,
    bool IsBusy,
    IReadOnlyList<JsonElement> History,
    IReadOnlyList<JsonElement> RunningItems,
    IReadOnlyList<JsonElement> Tools,
    IReadOnlyList<JsonElement> Subagents,
    IReadOnlyList<AgentChatModal> Modals,
    bool ContinueInBackground,
    int ViewerCount,
    JsonElement? CompletionState);
```

`JsonElement` is used only for already-versioned domain payloads whose polymorphism is owned by
`PhantomAgentSchema` or `Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions`; each element is
cloned before the read buffer advances. It is never used to bypass strict top-level member checking.

`RemoteAgentSessionException` is a **New public sealed exception** in
`Phantom.Workspaces.Llm.Core/Remote/RemoteAgentSessionException.cs`, with
`string Code { get; }`, `string Operation { get; }`, `bool IsRetryable { get; }`, and
`Guid CorrelationId { get; }`. Its internal factory accepts only `RemoteAgentOperationError`.
It contains the safe message only; local exceptions are retained solely in host logs.

Version 1 uses kebab-case JSON and rejects unknown members. Queue message arrays use
`Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions`; agent definitions use
`AgentDefinition.ToJson()` and `PhantomAgentSchema.AgentDefinitionFromJson(string)` after the
authorization gate described above. Required fields are non-null and ids are
nonempty. Value-type constructors reject an empty epoch, negative cursor sequence, unsupported
protocol version, negative generation, and duplicate/unknown capabilities. `Sequence` is positive
and increases for every server frame in an epoch. The snapshot
sequence is its high-water mark; replay starts at cursor+1. `CommandId` is the stable idempotency key
and is reused across retries; `CorrelationId` identifies one wire attempt. Acknowledgement/error
frames echo that attempt's correlation id, while unsolicited events use a fresh correlation id.

Open descriptor: `type:"attach-agent-session"`, `protocol-version`, `agent-session-id`,
`expected-owning-profile-entity-id`, `expected-ownership-generation`, `open-intent`
(`status`, `start`, `attach`, `start-or-attach`, `resume`), a cryptographically random 128-bit
`attachment-token`, optional `replay-cursor:{runtime-epoch,sequence}`, and `capabilities`. The token
is scoped to the authenticated peer and runtime epoch and is retained only for the five-second
unexpected-loss grace. `agent-definition` is not a negotiable capability. Commands are:

| Discriminator | Required payload | Semantics |
|---|---|---|
| `create-queue` | expected aggregate revision, name, configuration | Create one custom queue with an owner-issued stable id. |
| `delete-queue` | expected aggregate revision, queue id | Delete a custom queue; default/immediate queues are rejected. |
| `enqueue-input` | expected aggregate revision, target queue id, messages | Enqueue once and assign an owner-issued stable item id. |
| `edit-queue-item` | expected aggregate revision, queue id, item id, messages | Replace item messages while preserving item id. |
| `remove-queue-item` | expected aggregate revision, queue id, item id | Remove an unconsumed item by id. |
| `move-queue-item` | expected aggregate revision, source/item/target ids, optional before-item id | Atomically reorder or move an item. |
| `configure-queue` | expected aggregate revision, queue id, configuration | Rename or change priority/coalescing/immediacy, including hold/release. |
| `interrupt` | none | Cancel active turn; repeated calls succeed without terminating. |
| `terminate-session` | `reason` | Fence, terminate, and emit terminal once. |
| `open-subagent` | `agent-id` | Reauthorize child membership and return child attach descriptor; never local re-parent. |
| `modal-response` | `modal-id`, `response` | Accept only the current unresolved modal; duplicate command id returns prior result. |
| `set-tool-enabled` | `tool-id`, boolean `enabled` | Reauthorize and update owner tool state; complete only after the ordered tools event is applied. |
| `set-continue-in-background` | boolean `continue-in-background` | Persist the per-session preference, publish authoritative retention state, and stop immediately if set false at zero viewers. |
| `detach` | none | Remove only this attachment; no later frame is required. |

Server frames are:

| Discriminator | Required payload |
|---|---|
| `session-status` | `running`, `not-running`, or `unavailable`; terminal one-shot response with no session metadata |
| `session-snapshot` | `AgentInformation` with full definition, `Usage`, full queue snapshot, history, running/streaming state, busy, tools, subagents, modals, `continue-in-background`, viewer count, terminal state |
| `history-appended` | serialized history item |
| `usage-changed` | complete replacement `Usage` |
| `agent-information-changed` | complete replacement `AgentInformation` |
| `queue-changed` | aggregate revision, complete affected queue snapshots, removed queue ids |
| `streaming-started` / `streaming-updated` / `streaming-completed` | run id and serialized item/update |
| `busy-changed` | boolean busy |
| `tools-snapshot` / `tools-changed` | tool ids, display state, enabled/status |
| `subagents-snapshot` / `subagents-changed` | child identity, display, completion, parent relationship |
| `modal-raised` / `modal-updated` / `modal-dismissed` | modal id, owner agent id, title, body, strict typed content/response state |
| `session-retention-changed` | authoritative `continue-in-background` and nonnegative logical viewer count |
| `command-completed` | command id and optional result (including child descriptor) |
| `operation-error` | `RemoteAgentOperationError` |
| `session-terminal` | safe reason and final completion state |

`RemoteAgentOperationError` is an internal strict record:
`(string Code, string Operation, bool IsRetryable, string Message, Guid CorrelationId)`.
Allowed codes are `invalid-request`, `unauthorized`, `not-found`, `owner-mismatch`,
`generation-mismatch`, `runtime-changed`, `unsupported`, `conflict`, `cancelled`,
`containment-required`, `launch-failed`, `takeover-blocked`, and `internal-error`. Queue rejection
uses stable operation-specific error codes in the command result; `conflict` also carries the
authoritative queue revision/snapshot. It never contains
policy JSON, paths, environment, argv, stderr, native handles, or credentials. The command
deduplication cache stores the last 2,048 command results for 15 minutes per runtime. Reusing an id
with a different payload is `conflict`; exact reuse returns the original result without mutation.

`AgentSessionProtocolCodec` has only internal static
`JsonElement SerializeOpen(AgentSessionOpenRequest)`,
`AgentSessionOpenRequest DeserializeOpen(JsonElement)`,
`JsonElement SerializeCommand(AgentSessionCommand)`,
`AgentSessionCommand DeserializeCommand(JsonElement)`,
`JsonElement SerializeFrame(AgentSessionServerFrame)`, and
`AgentSessionServerFrame DeserializeFrame(JsonElement)`. Serialization is deterministic; each
deserialize clones retained `JsonElement` values and rejects unknown top-level members before any
authorization or mutation.

#### Persisted runtime intent and hydration - New/Modified

- **Namespace/project/files:** `Phantom.Workspaces.Data`;
  `AgentSessionRuntimeIntentData.cs` (**New**, public persisted DTO);
  `AgentSessionEntityFactory.cs` and `JsonSchemas/agent-session.json` (**Existing, Modified**).
- **Namespace/project/files:** `Phantom.Workspaces.Services`;
  `PersistedAgentSessionRuntimeIntent.cs`, `IAgentSessionRuntimeContextFactory.cs`,
  `AgentSessionRuntimeContextFactory.cs` (**New**).

```csharp
public sealed record AgentSessionRuntimeIntentData(
    string? OwningProfileEntityId,
    long OwnershipGeneration,
    JsonElement? ExecutorBindings,
    string? TrustProfileReference,
    long? ExpectedTrustProfileRevision,
    bool ContinueInBackground = false);

public sealed record PersistedAgentSessionRuntimeIntent(
    string AgentSessionId,
    string OwningProfileEntityId,
    long OwnershipGeneration,
    ExecutorBindings ExecutorBindings,
    string? TrustProfileReference,
    long? ExpectedTrustProfileRevision,
    bool ContinueInBackground);

public sealed record AgentSessionRuntimeContext(
    PersistedAgentSessionRuntimeIntent Intent,
    ITransportFactoryRegistry? TransportFactoryRegistry);

public interface IAgentSessionRuntimeContextFactory
{
    AgentSessionRuntimeContext Create(JsonElement agentSessionEntity);
}

public sealed class AgentSessionRuntimeContextFactory
    : IAgentSessionRuntimeContextFactory
{
    public AgentSessionRuntimeContextFactory(
        ITransportFactoryRegistry? transportFactoryRegistry);
}
```

The changed persistence factory signature is:

```csharp
public static JsonElement CreateEntityData(
    EntityId agentDefinitionEntityId,
    string agentDisplayName,
    string agentSessionId,
    IReadOnlyCollection<EntityName> agentSessionNames,
    DateTimeOffset currentTime,
    string computerName,
    EntityId hostProfileEntityId,
    IReadOnlyDictionary<string, string>? parameterValues = null,
    JsonElement? sessionExecutor = null,
    JsonElement? executorComponentBindings = null,
    IReadOnlyDictionary<string, JsonElement>? parameterSelections = null,
    long ownershipGeneration = 0,
    JsonElement? trustProfileReference = null,
    long? expectedTrustProfileRevision = null,
    bool continueInBackground = false);
```

The data DTO is serialization-only: setters are `init`, validate through the reader, and contain no
runtime policy. `JsonSchemas/agent-session.json` retains the existing
`host-profile-entity-id` and `executor-bindings` fields and adds
`ownership-generation` (integer, minimum zero, default zero), `trust-profile-reference`,
`expected-trust-profile-revision` (integer, minimum zero), and
`continue-in-background` (boolean, default false). Trust reference/revision must be both present or
both absent. `AgentSessionEntityFactory.CreateEntityData(...)` makes
`EntityId hostProfileEntityId` required for every new entity and adds
`long ownershipGeneration = 0`, optional trust-reference/revision parameters, and
`bool continueInBackground = false`; it always writes owner, generation, executor bindings, and
background preference. All existing creation call sites pass the current profile. Only the reader
accepts a missing owner for the documented legacy migration. `Create` is the #1481
lower-level hydration seam called once at first acquisition. It reads owner/generation/trust reference,
the existing `AgentSessionExecutorBindings`, and background preference; a missing preference is
`false`. It uses the existing canonical `host-profile-entity-id` as owner, reconstructs `ExecutorBindings`, and returns the
process-scoped registry. Missing bindings mean local; malformed or nonlocal-without-registry fails
closed. It does not resolve GUI routes, fetch a remote runtime, compile MXC, or mutate caller services.

#### `AcquireAgentChatRequest`, `IRunningAgentChatTable`, and running leases - Existing, Modified

- **Namespace/project/files:** `Phantom.Workspaces.Services`;
  `AcquireAgentChatRequest.cs`, `IRunningAgentChatTable.cs`, `RunningAgentChatTable.cs`,
  `RunningAgentChatWithEntityInfo.cs`; and `Phantom.Workspaces.Llm/RunningAgentChat*.cs`.
- **Visibility/kind:** existing public DTO/interface/sealed implementations.

```csharp
public enum AgentChatAcquisitionMode { Local, AttachRemote, StartOrAttachRemote }

// Added init-only fields on AcquireAgentChatRequest:
public AgentChatAcquisitionMode AcquisitionMode { get; init; }
public ITransport? OwningProfileTransport { get; init; }
public ReplayCursor? ReplayCursor { get; init; }

public interface IRunningAgentChatTable
{
    ObservableCollection<RunningAgentChatWithEntityInfo> RunningSessions { get; }
    Task<RunningAgentChatLease> AcquireAsync(
        AcquireAgentChatRequest request, CancellationToken ct = default);
    Task<bool> TerminateAsync(
        AgentSessionId sessionId, CancellationToken ct = default);
    Task SetContinueInBackgroundAsync(
        AgentSessionId sessionId, bool continueInBackground,
        CancellationToken ct = default);
}
```

The three new request setters are ordinary init-only storage: they perform no I/O and clone no
transport. `AcquireAsync` validates their legal combinations (`Local` forbids
`OwningProfileTransport`; remote modes require it and persisted owner/generation). The request does
not own or dispose the transport.

`RunningAgentChatLease.AgentChat` and `AgentViewModel.AgentChat` change from `AgentChat` to
`IAgentChat`; local factory internals may retain concrete `AgentChat`. `RunningAgentChat` and
`RunningAgentChatWithEntityInfo` continue to expose `AcquireLeaseAsync` rather than adding a `Chat`
property. `AcquireAsync`
hydrates runtime intent exactly once for a not-yet-running local session, merges it immutably into
`AgentServices`, and preserves current ref-counted reuse. For remote modes its internal attach path
requires persisted entity data and an owning transport, creates client/proxy, and
registers the proxy only after its snapshot. Concurrent calls single-flight by session+owner+
generation. Cancellation before publication leaves no row. `TerminateAsync` authorizes/terminates
according to local versus proxy chat and returns false if absent; it does not merely release leases.
`SetContinueInBackgroundAsync` rejects an absent or subagent session, dispatches local-owner updates
to the runtime registry and remote updates to `RemoteAgentSessionClient`, and completes only after
persistence plus authoritative state publication. Lease disposal remains viewer/reference release;
the owner applies the last-viewer rule.

```csharp
public sealed class RunningAgentChat
{
    public AgentSessionId SessionId { get; }
    public bool IsSubAgent { get; init; }
    public Task<RunningAgentChatLease> AcquireLeaseAsync(CancellationToken ct = default);
}
public sealed class RunningAgentChatLease : IAsyncDisposable
{
    public AgentSessionId SessionId { get; }
    public IAgentChat AgentChat { get; }
    public ValueTask DisposeAsync();
}
public sealed class RunningAgentChatWithEntityInfo
{
    public AgentSessionId SessionId { get; }
    public bool IsSubAgent { get; }
    public bool IsRemote { get; }
    public bool ContinueInBackground { get; }
    public int ViewerCount { get; }
    public Task<RunningAgentChatLease> AcquireLeaseAsync(CancellationToken ct = default);
}
```

`RunningAgentChatLease.AgentChat` returns the acquired registered instance. The metadata getters on
`RunningAgentChatWithEntityInfo` are owner/client-authoritative snapshots and raise the existing
property-change path before `RunningAgentBrainViewModel.Refresh` updates a row. Lease disposal is
idempotent and decrements local/proxy viewer ownership once; it does not send an explicit terminate,
but final release can invoke the default graceful-stop transition.

#### `AgentServices`, `CurrentSessionContext`, and composition - Existing, Modified

- **Namespace/project/files:** `Phantom.Workspaces.Llm/AgentServices.cs`,
  `CurrentSessionContext.cs`; `Phantom.Workspaces.Services/AgentServicesComposition.cs`.
- **Visibility/kind:** existing public sealed records/static composition type.

`AgentServices` keeps its existing object-typed layering seams and adds:

```csharp
public object? AgentExecutionTrustContext { get; init; } // AgentExecutionTrustContext
public object? RemoteAgentSessionRuntimeIntent { get; init; } // persisted intent, host only
```

`CurrentSessionContext` keeps existing properties and adds:

```csharp
public required string OwningProfileEntityId { get; init; }
public required long OwnershipGeneration { get; init; }
public RuntimeEpoch? RuntimeEpoch { get; init; }
```

Init setters reject blank owner, negative generation, and an epoch without owner. The owning host
constructs one context per runtime; attachment identity is never installed as current-session
identity. `AgentServicesComposition.ComposeSessionServicesAsync` remains host-service composition and
does not parse persisted executor/trust fields. `RunningAgentChatTable` applies
`AgentSessionRuntimeContextFactory.Create` afterward via `with`, preventing GUI and auto-resume paths
from diverging and preventing one session's context from leaking into another.

#### UI integration types - Existing, Modified

- `RunningAgentBrainViewModel`, `RunningAgentRowViewModel`, and `RunningAgentBrainControl`:
  `Phantom.Workspaces.ViewModels` / `Phantom.Workspaces.Controls`;
  `Phantom.Workspaces/ViewModels/RunningAgentBrainViewModel.cs`,
  `Phantom.Workspaces/ViewModels/RunningAgentRowViewModel.cs`, and
  `Phantom.Workspaces/Controls/RunningAgentBrainControl.axaml(.cs)`. These are the existing
  brain-icon popup, `Rows` collection, and row type; this design adds no parallel “sessions flyout.”
  `RunningAgentBrainViewModel.Refresh()` continues to derive top-level rows from
  `IRunningAgentChatTable.RunningSessions` and continues filtering `IsSubAgent`.
  `RunningAgentRowViewModel` adds the following public row state:

  ```csharp
  public bool IsRemote { get; }
  public bool ContinueInBackground { get; }
  public int ViewerCount { get; }
  public bool IsBackgroundOptionEnabled { get; }
  public bool IsInterruptEnabled { get; }
  public ICommand InterruptCommand { get; }
  public ICommand TerminateCommand { get; }
  public ICommand SetContinueInBackgroundCommand { get; }
  ```

  Its two existing public constructors remain source-compatible. An internal constructor/factory
  used by `RunningAgentBrainViewModel` supplies runtime metadata and commands; legacy callers receive
  disabled no-op runtime commands and `false`/zero metadata.

  `InterruptCommand` acquires the existing session lease and invokes `IAgentChat.Interrupt`; it is
  enabled only while that row has an active interruptible turn.
  `TerminateCommand` calls `IRunningAgentChatTable.TerminateAsync` and removes the row only through
  the resulting `RunningSessions` change. Both are disabled while terminal/disconnected, and
  terminate disables every row command while pending. The background command is created by
  `RunningAgentBrainViewModel` and calls
  `IRunningAgentChatTable.SetContinueInBackgroundAsync(SessionId, requestedValue)`. It is disabled
  while an update is pending, after terminal/disconnect, or when the row lacks a persisted top-level
  session/owning transport; it is enabled for an attached remote row and for a locally owned runtime
  capable of accepting remote attachments. `ContinueInBackground` changes only from the
  authoritative running-session metadata after persistence/event application. Failure leaves the
  old checked state and reports the existing safe operation error.

  `RunningAgentBrainControl` places one checkbox in each running row beside the existing activate
  button. Its visible label and `AutomationProperties.Name` are exactly **“Continue in background”**;
  its tooltip is **“Keep this agent running when the last viewer disconnects.”** `IsChecked` is
  one-way bound to `ContinueInBackground`, `IsEnabled` to `IsBackgroundOptionEnabled`, and its
  command/boolean parameter to `SetContinueInBackgroundCommand`. Keyboard focus and Space invoke the
  checkbox without activating the row; the row's activate button remains separately focusable. A
  red, right-aligned **X** button binds `InterruptCommand`, `IsEnabled` to `IsInterruptEnabled`,
  tooltip and `AutomationProperties.Name` to **“Interrupt agent”**, and is distinct from the
  terminate action. A separate destructive **Terminate** button follows it, binds
  `TerminateCommand`, is disabled while termination is pending or the row is disconnected/terminal,
  and uses tooltip and `AutomationProperties.Name` **“Terminate agent session”**. It sends the
  explicit terminate verb; it never merely closes the tab or releases a viewer lease.
- `AgentViewModel`:
  `Phantom.Workspaces.Agent.Gui.ViewModels`;
  `Phantom.Workspaces.Agent.Gui/ViewModels/AgentViewModel.cs`; existing `public sealed class`.
  Its constructor becomes
  `AgentViewModel(IAgentChat agentChat, string displayName, string description,
  ObservableLoggerFactory loggerFactory, TaskScheduler foregroundScheduler,
  AgentViewModel? parentAgentViewModel = null)`. Existing foreground validation remains. It uses the
   common collections/events, awaits async remote tool toggles, and owns a
  `ReadOnlyObservableCollection<AgentSessionModalViewModel> Modals`. Modal response calls
  `Task RespondToModalAsync(string modalId, JsonElement response, CancellationToken ct = default)`,
  which delegates to `IAgentChat`, preserves the modal until a dismiss event, and maps cancellation
  or safe remote errors without optimistic removal.
   Input is gated only while this editor has an unresolved modal; descendant modals affect only root
   notification aggregation. Its existing public `AgentChat` getter changes to
   `public IAgentChat AgentChat { get; }`. Disposal unsubscribes before disposing its chat lease/proxy.
- `InputQueueViewModel`:
  `Phantom.Workspaces.Agent.Gui.ViewModels`; existing `public sealed class`. Its constructor and
  fields consume `IAgentChat.InputQueues`, `IAgentInputQueue`, and immutable snapshots rather than
  concrete `AgentChat`, `AgentChatQueue`, or `AgentInputQueueManager`. Submit, create, edit, remove,
  move, hold/release, and target selection await the aggregate commands. Controls stay unchanged
  until the acknowledged owner delta/result is applied; conflict replaces the projection from the
  returned snapshot and prompts/retries only after reconciling user intent. `Changed` is marshalled
  to the existing foreground scheduler, and disposal unsubscribes without disposing the chat-owned
  aggregate.
- `AgentSessionWorkspaceTabViewModel`:
  `Phantom.Workspaces.ViewModels`;
  `Phantom.Workspaces/ViewModels/AgentSessionWorkspaceTabViewModel.cs`; existing public sealed class.
  Adds read-only `bool IsRemote`, `string? RemoteProfileDisplayName`, and
  `bool HasModalsNeedingInput`; `SetReady` derives these from the chat/view model. The
  `modal-pending` notification clears only when aggregate modal count reaches zero; `chat-idle`
  retains activation-clearing behavior.
- `OpenAgentSessionShortcutHandler`:
  same namespace; existing public sealed class. `Handle`,
  `TryCreateAgentSessionTabForRestoreAsync`, `TryCreateTabForRestoreAsync`, and
  `CreateAgentSessionTabAsync` all call one new internal
  `OpenPersistedSessionAsync(JsonElement, AgentSessionOpenIntent, CancellationToken)`.
  `ConnectOnOwner` builds a remote acquisition request; `ResumeLocally` performs takeover before a
   local acquisition. It never parses executor bindings or compiles trust policy.
- `Notification`, `NotificationEntry`, `INotificationService`, `NotificationService`, and
  `NotificationsViewModel`: existing notification types in
  `Phantom.Workspaces/Services/Notifications` and `Phantom.Workspaces/ViewModels`. `Notification`
  adds required `string Kind`; `NotificationEntry` exposes it. `INotificationService` and
  `NotificationService` add
  `void Remove(string tabId, string kind)` and
  `void MarkRead(string tabId, string kind)` while retaining the existing tab-wide overloads for
  callers that intentionally affect every kind. `Notify` upserts only the matching `(TabKey, Kind)`.
  `NotificationsViewModel.UnreadCount` and `HasUnread` aggregate all entries and therefore require no
  signature change.

  ```csharp
  public record Notification(
      TabDescriptor TabDescriptor,
      string Heading,
      string Description,
      DateTime When,
      RunningState RunningState,
      NotificationState NotificationState,
      string Kind = "legacy");

  public sealed record NotificationEntry
  {
      public required string TabKey { get; init; }
      public required string Kind { get; init; }
      // Existing required TabDescriptor, Heading, Description, When,
      // IsRunning, IsInteresting, IsRead, and IsSnoozed properties remain.
  }

  public interface INotificationService
  {
      void Notify(Notification notification);
      void Remove(string tabId);
      void Remove(string tabId, string kind);
      void MarkRead(string tabId);
      void MarkRead(string tabId, string kind);
      // Existing members remain.
  }
  ```

  `NotificationService.Notify` rejects null and blank `Kind`; `"legacy"` preserves existing caller
  source compatibility while still giving every stored entry a nonblank key.

The added/changed public signatures are:

```csharp
public AgentViewModel(
    IAgentChat agentChat,
    string displayName,
    string description,
    ObservableLoggerFactory loggerFactory,
    TaskScheduler foregroundScheduler,
    AgentViewModel? parentAgentViewModel = null);
public IAgentChat AgentChat { get; }
public ReadOnlyObservableCollection<AgentSessionModalViewModel> Modals { get; }
public Task RespondToModalAsync(
    string modalId, JsonElement response, CancellationToken ct = default);

public bool IsRemote { get; }
public string? RemoteProfileDisplayName { get; }
public bool HasModalsNeedingInput { get; }
public void SetReady(AgentViewModel agentViewModel, ObservableLoggerFactory factory);

public override Task<bool> Handle(
    MainWindowViewModel mainWindowViewModel,
    Shortcut shortcut,
    SubscribedEntityViewModel entityViewModel);
public Task<AgentSessionWorkspaceTabViewModel?> TryCreateAgentSessionTabForRestoreAsync(
    MainWindowViewModel mainWindowViewModel,
    SubscribedEntityViewModel agentSessionEntity,
    string? tabId = null,
    string? title = null,
    string? dockRegion = null);
public override Task<WorkspaceTabViewModel?> TryCreateTabForRestoreAsync(
    MainWindowViewModel mainWindowViewModel,
    SubscribedEntityViewModel entityViewModel,
    string? tabId,
    string? title,
    string? dockRegion);
public Task<AgentSessionWorkspaceTabViewModel> CreateAgentSessionTabAsync(
    MainWindowViewModel mainWindowViewModel,
    SubscribedEntityViewModel agentSessionEntity,
    IAgentChat agentChat);
public AgentViewModel ComposeSessionAgentViewModel(
    MainWindowViewModel mainWindowViewModel,
    ObservableLoggerFactory loggerFactory,
    IAgentChat agentChat,
    SubscribedEntityViewModel agentSessionEntity,
    AgentSessionWorkspaceTabViewModel tab,
    TaskScheduler foregroundScheduler);
```

All four UI getters are foreground-owned and nonblocking. `SetReady` remains one-shot, rejects null
arguments/failed or disposed tabs, replaces loading state atomically, then raises property and
notification changes. No UI operation crosses transport except through the `IAgentChat` command
methods.

`AgentSessionModalViewModel` is a **New public sealed view model** in
`Phantom.Workspaces.Agent.Gui.ViewModels`,
`Phantom.Workspaces.Agent.Gui/ViewModels/AgentSessionModalViewModel.cs`. It exposes immutable
`string Id`, `string Title`, `string Body`, and `AgentChatModalContent Content`, plus
`Task RespondAsync(JsonElement response, CancellationToken ct = default)`. It delegates once to its
owning `AgentViewModel`; invalid freeform, choice, or approval responses fail before transport,
cancellation leaves the modal
pending, and only a later ordered dismiss event removes it. `AgentSessionModalStackControl` is a
**New public sealed Avalonia control** in `Phantom.Workspaces.Agent.Gui.Controls`,
`Phantom.Workspaces.Agent.Gui/Controls/AgentSessionModalStackControl.axaml(.cs)`. It adds no public
method and binds only the current editor's modal collection.

`SlashCommandContext` is **Existing, Modified** in `Phantom.Workspaces.Llm.SlashCommands`,
`Phantom.Workspaces.Llm.Core/SlashCommands/SlashCommandContext.cs`: its
`public required IAgentChat AgentChat { get; init; }` replaces the concrete type. The init setter
rejects null. Handlers needing engine-only APIs are not registered by `RemoteAgentChat.SlashCommands`;
common handlers use only `IAgentChat`. This prevents a hidden concrete cast in the open path.

#### MXC/executor-facing types - Existing plans, integrated without parallel APIs

- `ExecutorBindings` - **Existing from the component-binding design**, public sealed record in
  `Phantom.Workspaces.Llm.Core.Manifest`,
  `Phantom.Workspaces.Llm.Core/Manifest/ExecutorBindings.cs`. Remote integration adds no member and
  calls existing
  `JsonElement ResolveComponent(string? executorName)`, `ExecutorTopology ToTopology()`, and
  `JsonElement ToPersistableMap()`. Resolution is deterministic and local; unknown names throw and
  no transport is opened.
- `AgentExecutionTrustContext` - **New in #1477**, public sealed class in
  `Phantom.Workspaces.Llm.Trust`,
  `Phantom.Workspaces.Llm.Core/Trust/AgentExecutionTrustContext.cs`. Remote integration adds no
  parallel context. Its consumed operation is
  `ValueTask<TrustProfileProcessPolicyCompilation> GetCompilationAsync(CancellationToken ct =
  default)`. The first caller resolves the expected revision and compiles on the launch host;
  concurrent callers share the result. Caller cancellation stops only that wait after work starts.
  Missing/stale profiles and compiler errors are cached failures for the runtime epoch.
- `ITrustProfileProcessPolicyCompiler`,
  `TrustProfileProcessPolicyCompilation`, and `MxcProcessPolicy` - **New in #1475**, public interface
  and immutable records in `Phantom.Workspaces.Llm.Trust`,
  `Phantom.Workspaces.Llm.Core/Trust/ITrustProfileProcessPolicyCompiler.cs` and
  `MxcProcessPolicy.cs`. Exact consumed signature:
  `TrustProfileProcessPolicyCompilation Compile(TrustProfile effectiveProfile)`. It validates a
  nonnull composed profile, performs no launch, and returns either uncontained, a complete policy,
  or diagnostics; required-policy failure is never interpreted as uncontained.
- `IProcessExecutor`, `ProcessExecutionRequest`, and `IProcessHandle` - **New in #1474**, public
  interface/records in `Phantom.Workspaces.Processes`,
  `Phantom.Workspaces.Processes/IProcessExecutor.cs`. Exact consumed signatures are
  `Task<IProcessHandle> StartAsync(ProcessExecutionRequest request, CancellationToken ct = default)`,
  `Task<ProcessExitResult> WaitAsync(CancellationToken ct = default)`,
  `Task TerminateAsync(CancellationToken ct = default)`, and `ValueTask DisposeAsync()`.
  Start cancellation owns and cleans any partial process; wait cancellation does not transfer
  ownership; terminate and disposal are idempotent and kill the tree. An MXC launch failure never
  retries ordinary execution.
- `CopilotRuntimeConnectionFactory` and `CopilotLaunchPolicyEnvelope` - **New in #1476**, public
  sealed class/internal strict handoff record in `Phantom.Workspaces.Llm.Copilot`,
  `Phantom.Workspaces.Llm.Core/Copilot/CopilotRuntimeConnectionFactory.cs`. Exact integration method:
  `Task<CopilotRuntimeConnectionLease> CreateConnectionAsync(AgentExecutionTrustContext trustContext,
  string? cliPath, CancellationToken ct = default)`. It returns one async-disposable direct-or-wrapper
  connection lease per `CopilotSdkChatClient`; constrained mode writes a protected one-use local
  envelope. Cancellation/failure deletes unconsumed files. The lease, envelope, policy, and path
  never cross machine transport.
  `CopilotRuntimeConnectionLease` is the **New in #1476 public sealed async-disposable result** in
  the same file; it exposes `RuntimeConnection Connection { get; }` and idempotent
  `ValueTask DisposeAsync()`. Disposal deletes only an unconsumed handoff and never kills an
  SDK-owned runtime after ownership has transferred.
- `ProcessExecutorBackedClientTransport` and `ProcessOwnedMcpTransport` - **New in #1477**, public
  sealed `IClientTransport` and internal sealed `ITransport` decorator in
  `Phantom.Workspaces.Llm.Core/Mcp/ProcessExecutorBackedClientTransport.cs`. Exact public members are
  `string Name { get; }` and
  `Task<ModelContextProtocol.Protocol.ITransport> ConnectAsync(CancellationToken cancellationToken =
  default)`. Connect is single-use, always launches via `IProcessExecutor`, separates stderr, and
  returns the process-owning transport. Its `DisposeAsync()` closes MCP/stdin, kills a still-running
  tree, and drains exit/stderr tasks. A non-final viewer detach does not dispose it; final detach
  reaches it only through graceful runtime stop when background continuation is disabled.
- Effective trust resolution is host-local: the persisted reference/revision is resolved/composed,
  then `Compile` is called. Missing/stale references, unavailable MXC, compiler failure, wrapper
  failure, or executor failure never downgrade to null policy or direct launch.

### Data flow

1. **Create/persist.** The creator persists owner, generation, executor bindings, and trust
   reference/revision. No runtime policy is persisted.
2. **Hydrate.** `RunningAgentChatTable.AcquireAsync` calls the runtime-context factory once at first
   materialization and merges the result into a new `AgentServices` record. The owning host creates
   `CurrentSessionContext` from its own profile/user/computer, never from viewer claims.
3. **Open.** The client opens a message channel with the strict open descriptor. The listener obtains
   authenticated peer identity and the host authorizes before runtime lookup.
4. **Start/attach.** The registry single-flights runtime creation. An attachment subscribes before
   snapshot capture. The host sends a retained replay only when the supplied cursor is fully covered;
   otherwise it sends one authoritative snapshot.
5. **Mutate.** Each command is reauthorized and deduplicated, then checked against current
   generation+epoch. The host mutates the real `AgentChat`, appends the resulting event under the
   runtime scheduler, and broadcasts it in sequence.
6. **Queue and steer.** Every user input, including one intended to affect an active run, is an
    `enqueue-input` command against a stable target queue and expected aggregate revision. The owner assigns the
   item id, applies once, acknowledges, and broadcasts `queue-changed`. Based on current-run state,
   queue immediacy, and mode, owning `AgentChat` may consume the item immediately through the
   internal Copilot path; that consumption is another ordered queue delta. Otherwise the item
   remains for a tool boundary or future turn. The remote GUI never invokes Copilot and there is no
   steering command.
7. **Route/contain.** `ExecutorBindings` chooses the final component host. That host resolves the
   persisted trust reference/revision, composes the effective profile, compiles once through #1475,
   and launches through #1474/#1476/#1477. Machine transports serialize trust intent, never compiled
   policy.
8. **Reconnect.** The proxy keeps only its cursor and persisted open request. Reconnect reauthorizes
   and reuses the runtime. A snapshot includes `Usage`, `AgentInformation`, and all queue snapshots;
   retained replay applies queue deltas by epoch/global sequence. It neither reconstructs
   `AgentChat` nor relaunches children.
9. **Take over.** The old host fences mutations and confirms complete runtime disposal. Persistence
   compare/exchanges owner and increments generation. The new host hydrates and starts a fresh epoch.
   Failure to confirm an unexpired old lease blocks takeover.
10. **Detach/stop.** Explicit detach, proxy disposal, UI tab close, and viewer-application shutdown
    release immediately; transport loss reserves the attachment token for five seconds. On final
    release, `continue-in-background == false` fences and gracefully stops the runtime. Explicit
    terminate, takeover, owner-host shutdown, or runtime-lease expiry always fences and stops it.
    Stop rejects mutations, interrupts the active turn if required, disposes the chat, component
    transports, MXC/process leases, wrappers, process-owned MCP transports and child trees, persists
    terminal/stopped state, emits terminal where the channel remains writable, and closes channels.

### UI, sessions view, and notifications

- `OpenAgentSessionShortcutHandler` is the sole GUI choice point for local, connect-on-owner, and
  takeover paths, including restore.
- `RunningAgentBrainViewModel.Rows` contains `RunningAgentRowViewModel` values with placement, owner
  display, interrupt, a separate terminate action, viewer count, and authoritative background
  preference. A lease close never sends the terminate verb, although final release may activate the
  default lifecycle policy.
- `AgentViewModel` owns only its modal stack and input gate. The root's
  `HasModalsNeedingInput` is the OR of self and descendants.
- `AgentChatEditorControl` renders `AgentSessionModalStackControl` above the input queue.
- `InputQueueViewModel` binds only immutable common snapshots. It awaits commands and owner deltas;
  it never receives owner collections or performs an optimistic mutation.
- Notifications are per-tab and keyed by kind. Activation clears `chat-idle`; only an empty aggregate
  modal set clears `modal-pending`. `Notification`, `NotificationEntry`, and `NotificationService`
  add a required nonblank `Kind`; replacement/removal/read state uses `(TabKey, Kind)` rather than
  only `TabKey`. Existing callers use a stable legacy kind. `NotificationsViewModel.UnreadCount`
  counts all unread entries and `HasUnread` is `UnreadCount > 0`, so it is the app-wide logical OR
  across `chat-idle`, `modal-pending`, and future kinds.
- New `Phantom.Workspaces.Data.Core/JsonEntities/entity-type-views/agent-session-entity-type-view.json`
  groups by the existing persisted `host-profile-entity-id` through `group-by-parent`; there is no
  parallel Sessions UI.

### Public API test matrix

The following matrix is normative. Test names use the repository's
`Method_Scenario_ExpectedOutcome` / `Subject_Scenario_ExpectedOutcome` convention. Existing public
members not changed by this design retain their existing tests; every newly introduced or
behaviorally changed public method, init setter, and event-producing operation is covered below.
For the state/queue refactor, the reviewed surface contains **10 new public types** (`Usage`,
`AgentInformation`, and eight queue interfaces/value types), **7 logical public queue operations**
declared on both `IAgentInputQueues` and `RemoteAgentSessionClient` (**14 method declarations**), and
no public steering method. Lifecycle adds **four public method declarations**:
`RemoteAgentSessionClient.GetStatusAsync`,
`RemoteAgentSessionClient.ReconnectAsync`,
`RemoteAgentSessionClient.SetContinueInBackgroundAsync`, and
`IRunningAgentChatTable.SetContinueInBackgroundAsync`. Each queue declaration has a named success
test plus shared validation, cancellation, rejection, conflict, and deduplication tests; each
lifecycle declaration has success, invalid-state, cancellation, and race coverage as applicable.
Remote tool mutation adds one public
`RemoteAgentSessionClient.SetToolEnabledAsync` declaration with authoritative-apply coverage.
The matrix also covers every
added or behaviorally changed public method on `RemoteAgentChat`, `RemoteAgentSessionClient`,
`IRunningAgentChatTable`, `RunningAgentChatLease`, `RunningAgentChatWithEntityInfo`,
`AgentViewModel`, `AgentSessionModalViewModel`, and the public row command/property surface. The
complete matrix count is stated after the final scenario list.

#### `AgentChatInterfaceTests` (`Phantom.Workspaces.Llm.Core.Tests`)

- `Information_LocalChat_ReturnsAtomicAgentInformation`.
- `Usage_LocalChat_ReturnsAtomicUsage`.
- `InputQueues_LocalChat_ReturnsCommonQueueAggregate`.
- `GetToolSnapshot_MutationAfterRead_DoesNotChangeReturnedSnapshot`.
- `SetToolEnabledAsync_KnownTool_ChangesStateThenRaisesToolsChanged`.
- `SetToolEnabledAsync_UnknownTool_ThrowsArgumentException`.
- `SetToolEnabledAsync_Cancelled_DoesNotMutateOrRaiseEvent`.
- `RespondToModalAsync_CurrentModal_AcceptsExactlyOnce`.
- `RespondToModalAsync_UnknownModal_ThrowsArgumentException`.
- `EnqueueSystemNote_ValidText_AppendsSystemNote`.
- `EnqueueHelpNote_ValidText_AppendsHelpNote`.
- `EnqueueTransientDiagnostic_ValidText_AppendsNonPersistedDiagnostic`.
- `Interrupt_ActiveTurn_CancelsTurnWithoutDisposingChat`.
- `Interrupt_NoActiveTurn_IsIdempotent`.
- `Usage_EqualValues_CompareEqual`.
- `Usage_RoundTrip_PreservesNullableCountsAndDoubleUsd`.
- `UsagePublisher_NegativeMetric_RejectsBeforePublication`.
- `UsageChanged_CompleteReplacement_StateVisibleBeforeSingleEvent`.
- `AgentInformation_EqualValues_CompareEqual`.
- `AgentInformationPublisher_InvalidRequiredString_RejectsBeforePublication`.
- `AgentInformationPublisher_InvalidOptionalModel_RejectsBeforePublication`.
- `AgentInformation_AuthorizedPeer_RoundTripsCompleteDefinition`.
- `AgentInformationPublisher_NullDefinition_RejectsBeforePublication`.
- `SessionSnapshot_TwoAuthorizedViewers_ReceiveEquivalentFullDefinition`.
- `OpenAsync_UnauthorizedPeer_SerializesNoSessionMetadata`.
- `InformationChanged_SessionAndModelChange_StateVisibleBeforeSingleEvent`.
- `TurnCompleted_TurnPersists_EventRaisedAfterHistoryMutation`.
- `DisposeAsync_RepeatedCall_DisposesOnce`.
- `GetService_KnownService_ReturnsExistingService`.

#### `AgentInputQueuesTests` (`Phantom.Workspaces.Llm.Core.Tests`)

- `Snapshot_LocalQueue_ReturnsDeepImmutableCopy`.
- `QueueSnapshotPublisher_InvalidRevisionOrDuplicateIds_RejectsBeforePublication`.
- `QueueSnapshotPublisher_InvalidIdentityRoleOrRevision_RejectsBeforePublication`.
- `QueueSnapshotPublisher_InvalidItemIdentityOrMessages_RejectsBeforePublication`.
- `QueueCommandValidator_InvalidNameImmediacyOrPriority_RejectsBeforeMutation`.
- `AgentInputQueueCommandResult_RoundTrip_PreservesStatusRevisionAndSnapshot`.
- `Queues_LocalAndProxy_ExposeEquivalentReadModels`.
- `DefaultQueue_LocalAndProxy_ReturnSameStableQueueId`.
- `ImmediateQueue_LocalAndProxy_ReturnSameStableQueueId`.
- `CreateQueueAsync_ValidConfiguration_AssignsStableQueueIdAndRevision`.
- `DeleteQueueAsync_CustomQueue_RemovesQueueOnce`.
- `DeleteQueueAsync_DefaultOrImmediateQueue_ReturnsRejected`.
- `EnqueueAsync_DefaultImmediateHeldAndCustomTargets_AssignsStableItemIds`.
- `EnqueueAsync_CancelledBeforeMutation_DoesNotChangeRevision`.
- `EditAsync_ExistingItem_PreservesItemIdAndAdvancesRevision`.
- `RemoveAsync_ExistingItem_RemovesByIdNotIndex`.
- `MoveAsync_BeforeItem_ReordersByStableIds`.
- `MoveAsync_DifferentTarget_MovesAtomicallyAcrossQueues`.
- `ConfigureAsync_ImmediateQueueInvalidRoleChange_ReturnsRejected`.
- `ConfigureAsync_HoldAndRelease_ChangesImmediacy`.
- `Command_StaleExpectedRevision_ReturnsConflictAndAuthoritativeSnapshot`.
- `Command_DuplicateIdSamePayload_ReturnsOriginalResultWithoutSecondMutation`.
- `Command_DuplicateIdDifferentPayload_ReturnsConflict`.
- `Changed_AppliedCommand_ReplacesSnapshotBeforeEvent`.
- `QueueChanged_AppliedCommand_ReplacesQueueSnapshotBeforeEvent`.
- `QueueConsumption_ActiveRun_AdvancesRevisionAndRaisesChanged`.
- `EnqueueAsync_ActiveCopilotRun_ConsumesAsInternalSteering`.
- `EnqueueAsync_ActiveNonCopilotRun_RemainsQueuedUntilSupportedBoundaryOrFutureTurn`.

#### `RemoteAgentChatTests` (`Phantom.Workspaces.Llm.Core.Tests`)

- `AgentChatModal_InvalidIdentityTitleOrBody_RejectsConstruction`.
- `MultipleChoiceModalContent_Options_AreClonedOnConstruction`.
- `FreeformModalContent_ValidSettings_RoundTrips`.
- `MultipleChoiceModalContent_DuplicateOrEmptyOptions_RejectsConstruction`.
- `ApprovalModalContent_BlankLabels_RejectsConstruction`.
- `AttachAsync_ValidSnapshot_PublishesInitializedProxy`.
- `AttachAsync_CancelledBeforeSnapshot_DisposesClientAndPublishesNothing`.
- `AttachAsync_InvalidSnapshot_ThrowsProtocolException`.
- `Reconnect_UnexpectedLoss_RetriesWithinGraceAndKeepsProxyEpoch`.
- `ProxyGetters_AfterOrderedFrames_ReturnMirroredState`.
- `ProxyEvents_OrderedFrame_AreRaisedAfterStateMutation`.
- `UsageChanged_OrderedFrame_AtomicallyReplacesUsage`.
- `InformationChanged_OrderedFrame_AtomicallyReplacesInformation`.
- `InputQueues_OrderedDelta_MatchesLocalReadModel`.
- `InputQueues_RejectedCommand_DoesNotMutateProjection`.
- `InputQueues_ConflictResult_RefreshesFromAuthoritativeSnapshot`.
- `InputQueues_CommandPending_DoesNotMutateProjection`.
- `SetToolEnabledAsync_RemoteTool_SerializesCommandAndAppliesAcknowledgedEvent`.
- `RespondToModalAsync_CurrentModal_SerializesResponseCommand`.
- `EnqueueSystemNote_RemoteProxy_AddsLocalDisplayOnlyNote`.
- `EnqueueHelpNote_RemoteProxy_AddsLocalDisplayOnlyNote`.
- `EnqueueTransientDiagnostic_RemoteProxy_AddsLocalNonPersistedDiagnostic`.
- `Interrupt_ConnectedProxy_SerializesInterrupt`.
- `DetachAsync_RepeatedCall_SendsAtMostOneDetach`.
- `DetachAsync_LastViewer_DefaultPolicy_TerminatesRuntime`.
- `DetachAsync_LastViewer_BackgroundEnabled_PreservesRuntime`.
- `TerminateAsync_CurrentEpoch_WaitsForTerminalFrame`.
- `TerminateAsync_StaleEpoch_ThrowsRuntimeChanged`.
- `DisposeAsync_ConnectedProxy_ReleasesViewerWithoutTerminateCommand`.
- `ProxyCommand_AfterDispose_ThrowsObjectDisposedException`.
- `GetService_TransportOrPolicyType_ReturnsNull`.

#### `RemoteAgentSessionClientTests` (`Phantom.Workspaces.Llm.Core.Tests`)

- `Constructor_NullTransport_ThrowsArgumentNullException`.
- `Constructor_ProcessScopedTransport_DoesNotDisposeBorrowedTransport`.
- `GetStatusAsync_AuthorizedRunningOrStopped_ReturnsAuthoritativeStatusOnly`.
- `GetStatusAsync_UnauthorizedOrMissing_ReturnsUnavailableWithoutMetadata`.
- `ConnectAsync_FirstCall_OpensAttachAgentSessionChannel`.
- `ConnectAsync_SecondCall_ThrowsInvalidOperationException`.
- `ConnectAsync_CancelledBeforeOpen_LeavesClientDisconnected`.
- `ConnectAsync_SnapshotThenDelta_UpdatesCursorAndRaisesFramesInOrder`.
- `ConnectAsync_SequenceGap_ClosesWithProtocolException`.
- `ConnectAsync_UnknownDiscriminator_ClosesWithProtocolException`.
- `ReconnectAsync_UnexpectedLoss_ForcesAttachWithTokenAndLastAppliedCursor`.
- `ReconnectAsync_ConnectedDetachedTerminalOrExpired_ThrowsInvalidOperationException`.
- `ReconnectAsync_CancelledAttempt_AllowsRetryBeforeDeadline`.
- `CreateQueueAsync_Connected_SerializesCommandAndAwaitsResult`.
- `DeleteQueueAsync_Connected_SerializesCommandAndAwaitsResult`.
- `EnqueueAsync_Connected_SerializesMessagesTargetAndRevision`.
- `EditAsync_Connected_SerializesStableItemIdAndMessages`.
- `RemoveAsync_Connected_SerializesStableItemId`.
- `MoveAsync_Connected_SerializesSourceTargetAndPlacementIds`.
- `ConfigureAsync_Connected_SerializesConfigurationAndRevision`.
- `InterruptAsync_Connected_SerializesInterruptAndAwaitsCorrelation`.
- `TerminateAsync_Connected_SerializesReasonAndAwaitsTerminal`.
- `OpenSubagentAsync_Authorized_ReturnsChildDescriptor`.
- `RespondToModalAsync_Connected_SerializesModalIdAndResponse`.
- `SetToolEnabledAsync_Connected_AwaitsAuthoritativeToolsEvent`.
- `SetContinueInBackgroundAsync_Connected_AwaitsPersistedAuthoritativeEvent`.
- `SetContinueInBackgroundAsync_Rejected_LeavesProjectionUnchanged`.
- `CommandMethod_EmptyCommandId_ThrowsArgumentException`.
- `CommandMethod_CancelledAfterWrite_DoesNotRetractCommand`.
- `CommandMethod_NotConnected_ThrowsInvalidOperationException`.
- `FrameReceived_ValidFrame_CursorAdvancesBeforeSubscriberRuns`.
- `DetachAsync_RepeatedCall_IsIdempotent`.
- `DisposeAsync_ActivePump_ClosesChannelWithoutTerminateCommandAndReleasesViewer`.
- `LastAppliedCursor_NoFrames_IsNull`.
- `RemoteAgentSessionException_WireError_ExposesOnlySafeFields`.

#### `AgentSessionTransportListenerTests` (`Phantom.Workspaces.Tests`)

- `OnChannelOpenAsync_OtherType_ReturnsNull`.
- `OnChannelOpenAsync_ValidAttach_ReturnsAttachmentLease`.
- `OnChannelOpenAsync_MalformedRequest_WritesSanitizedTerminalError`.
- `OnChannelOpenAsync_UnauthenticatedChannel_DoesNotLookupSession`.
- `OnChannelOpenAsync_UnauthorizedPeer_DoesNotRevealSessionExistence`.
- `OnStreamOpenAsync_AnyRequest_ReturnsNull`.
- `DisposeAsync_ActiveAttachments_ReleasesViewersThenHostStopsAllRuntimes`.

#### `AgentSessionRuntimeContextFactoryTests` (`Phantom.Workspaces.Tests`)

- `Constructor_NullRegistry_AllowsLocalOnlyHydration`.
- `AgentSessionRuntimeIntentData_InitProperties_PreserveOnlyPersistableIntent`.
- `PersistedAgentSessionRuntimeIntent_Init_InvalidOwnerOrGeneration_RejectsValue`.
- `AgentSessionRuntimeContext_Init_StoresIntentAndProcessRegistry`.
- `Create_PersistedSplitBindings_ReconstructsRuntimeContext`.
- `Create_NoPersistedBindings_UsesLocalDefaults`.
- `Create_LegacyHostProfile_UsesSessionExecutorFallback`.
- `Create_MalformedBinding_ReportsBindingKeyWithoutValue`.
- `Create_NonlocalBindingWithoutRegistry_ReportsConfigurationError`.
- `Create_TrustIntent_PreservesReferenceAndExpectedRevision`.
- `AgentSessionEntityFactory_CreateEntityData_DefaultBackground_PersistsFalse`.
- `AgentSessionEntityFactory_CreateEntityData_BackgroundEnabled_PersistsTrue`.
- `AgentSessionEntityFactory_CreateEntityData_DefaultOwner_ThrowsArgumentException`.
- `AgentSessionEntityFactory_CreateEntityData_RuntimeAuthority_PersistsOwnerGenerationBindingsAndTrust`.
- `Create_MissingContinueInBackground_UsesFalse`.
- `Create_ExplicitContinueInBackground_PreservesTrue`.
- `Create_CompiledPolicyProperty_RejectsEntity`.

#### `RunningAgentChatTableTests` (`Phantom.Workspaces.Tests`)

- `AcquireAsync_NewLocalSession_HydratesServicesBeforeFactoryAcquisition`.
- `AcquireAsync_ExistingLease_DoesNotRehydrateRuntimeContext`.
- `AcquireAsync_RemoteMode_UsesInternalAttachPath`.
- `AcquireAsync_CancelledBeforePublication_AddsNoRunningRow`.
- `AcquireAsync_ValidRemoteSession_AddsProxyAfterSnapshot`.
- `AcquireAsync_RemoteMissingEntityOrTransport_ThrowsArgumentException`.
- `AcquireAsync_ConcurrentSameRemoteRuntime_ReturnsLeasesForOneProxy`.
- `AcquireAsync_RemoteAuthorizationDenied_AddsNoRunningRow`.
- `TerminateAsync_MissingSession_ReturnsFalse`.
- `TerminateAsync_LocalSession_DisposesOwningRuntime`.
- `TerminateAsync_RemoteSession_SendsTerminateNotDetach`.
- `SetContinueInBackgroundAsync_LocalOwner_PersistsAndPublishesPreference`.
- `SetContinueInBackgroundAsync_RemoteProxy_SendsCommandAndAwaitsEvent`.
- `SetContinueInBackgroundAsync_MissingOrSubagentSession_ThrowsArgumentException`.
- `SetContinueInBackgroundAsync_CancelledBeforeWrite_DoesNotPersistOrPublish`.
- `RunningSessions_RemoteAttach_MutatesOnForegroundScheduler`.

#### `CurrentSessionContextTests` and `AgentServicesTests`

- `CurrentSessionContext_ValidOwnerGenerationEpoch_PreservesOwningHostIdentity`.
- `CurrentSessionContext_BlankOwner_RejectsInitialization`.
- `CurrentSessionContext_NegativeGeneration_RejectsInitialization`.
- `CurrentSessionContext_AttachmentPeer_DoesNotReplaceHostIdentity`.
- `AgentServices_AgentExecutionTrustContextSetter_WithExpressionPreservesOtherServices`.
- `AgentServices_RemoteRuntimeIntentSetter_WithExpressionPreservesOtherServices`.
- `AgentServices_GetService_NewObjectTypedSeams_DoesNotExposeConcreteTypes`.
- `AcquireAgentChatRequest_RemoteInitProperties_PreserveModeTransportAndCursor`.
- `AcquireAgentChatRequest_InvalidModeCombination_AcquireRejectsRequest`.
- `AgentViewModel_AgentChatProperty_LocalAndRemote_ReturnsIAgentChat`.
- `RunningAgentChatLease_AgentChatProperty_LocalAndRemote_ReturnsIAgentChat`.
- `RunningAgentChatWithEntityInfo_RetentionMetadataChange_RaisesAuthoritativeUpdate`.

#### UI public API tests

`AgentViewModelTests`:

- `Constructor_RemoteChat_UsesCommonSurfaceWithoutConcreteCast`.
- `Constructor_WrongForegroundContext_Throws`.
- `RespondToModalAsync_CurrentModal_SendsResponseAndKeepsInputGatedUntilDismissed`.
- `RespondToModalAsync_UnknownModal_ThrowsArgumentException`.
- `RespondToModalAsync_Cancelled_DoesNotDismissModal`.
- `ModalEvent_DescendantModal_UpdatesRootAggregateOnly`.
- `DisposeAsync_RemoteChat_UnsubscribesBeforeDetaching`.
- `InterruptCommand_RemoteChat_InvokesCommonInterrupt`.
- `ConfigureSlashCommands_RemoteChat_RegistersOnlyCommonHandlers`.

`InputQueueViewModelTests`:

- `CommandPending_RemoteQueue_DoesNotMutateProjectionOptimistically`.
- `CommandConflict_StaleRevision_RefreshesFromAuthoritativeSnapshot`.
- `CommandApplied_AuthoritativeDeltaAppliedBeforeTaskCompletes`.

`SlashCommandContextTests`:

- `AgentChatSetter_LocalOrRemote_PreservesCommonChat`.
- `AgentChatSetter_Null_RejectsInitialization`.

`AgentSessionModalViewModelTests`:

- `RespondAsync_ValidOption_DelegatesOnceAndWaitsForDismissEvent`.
- `RespondAsync_InvalidOption_RejectsBeforeTransport`.
- `RespondAsync_Cancelled_KeepsModalPending`.

`AgentSessionWorkspaceTabViewModelTests`:

- `SetReady_RemoteAgent_SetsRemoteMetadata`.
- `SetReady_LocalAgent_ClearsRemoteMetadata`.
- `SetReady_AgentWithModal_SetsModalPendingNotification`.
- `ModalDismissed_LastRelevantModal_ClearsModalPendingNotification`.
- `TabActivated_IdleAndModalNotifications_ClearsOnlyIdle`.

`OpenAgentSessionShortcutHandlerTests`:

- `Handle_OwnerIsCurrentProfile_AcquiresLocalRuntime`.
- `Handle_OwnerIsRemoteConnectChoice_AttachesOnOwner`.
- `Handle_OwnerIsRemoteResumeChoice_CompletesTakeoverBeforeLocalAcquire`.
- `TryCreateAgentSessionTabForRestoreAsync_RemoteOwner_UsesSameChoicePipeline`.
- `TryCreateTabForRestoreAsync_RemoteOwner_UsesSameChoicePipeline`.
- `Handle_RemoteOwnerPrompt_ShowsAuthorizedRunningStatus`.
- `Handle_RemoteOwnerPrompt_ShowsAuthorizedNotRunningStatus`.
- `Handle_RemoteOwnerPrompt_UnauthorizedOrMissingShowsUnavailable`.
- `CreateAgentSessionTabAsync_PersistedSession_PassesEntityToAcquisition`.
- `ComposeSessionAgentViewModel_RemoteChat_ConfiguresCommonSlashCommandSurface`.
- `DisposeAsync_InitializationInFlight_CancelsWithoutPublishingReadyTab`.

`RunningAgentBrainViewModelTests`:

- `Refresh_RemoteTopLevelSession_CreatesRunningAgentRowWithRetentionState`.
- `Refresh_Subagent_RemainsExcluded`.
- `InterruptCommand_EnabledRow_InvokesCommonInterrupt`.
- `InterruptCommand_NoInterruptibleRun_IsDisabled`.
- `SetContinueInBackgroundCommand_EnabledRemoteRow_CallsRunningTableOnce`.
- `SetContinueInBackgroundCommand_UpdatePending_DisablesUntilAuthoritativeRefresh`.
- `SetContinueInBackgroundCommand_Rejected_KeepsAuthoritativeCheckedState`.
- `TerminateCommand_RemoteRow_TerminatesAndRemovesRow`.

`RunningAgentBrainControlTests`:

- `ContinueInBackgroundCheckbox_RemoteRow_BindsCheckedEnabledAndCommand`.
- `ContinueInBackgroundCheckbox_Accessibility_UsesRequiredLabelAndTooltip`.
- `ContinueInBackgroundCheckbox_KeyboardSpace_DoesNotActivateRow`.
- `InterruptButton_ActiveRun_IsRedRightAlignedAndAccessible`.
- `TerminateButton_ConnectedRow_IsDistinctAccessibleAndSendsExplicitTerminate`.

`NotificationServiceTests` and `NotificationsViewModelTests`:

- `Notify_SameTabDifferentKinds_PreservesIndependentEntries`.
- `Notify_BlankKind_ThrowsArgumentException`.
- `Remove_TabAndKind_RemovesOnlyMatchingKind`.
- `MarkRead_TabAndKind_MarksOnlyMatchingKind`.
- `HasUnread_AnyUnreadKind_ReturnsTrueUntilAllKindsRead`.
- `TabActivated_ChatIdleAndModalPending_ClearsOnlyChatIdle`.
- `ModalDismissed_LastRelevantModal_ClearsOnlyModalPending`.

#### Protocol/lease/authorization internal contract tests

`AgentSessionProtocolCodecTests`:

- `RuntimeEpoch_EmptyValue_RejectsConstruction`.
- `ReplayCursor_NegativeSequence_RejectsConstruction`.
- `AgentSessionOpenRequest_InvalidVersionOrGeneration_RejectsConstruction`.
- `TransportPeerIdentity_BlankAuthenticatedIdentity_RejectsConstruction`.
- `RemoteSubagentDescriptor_ValidValues_RoundTrips`.
- `Serialize_AllOpenIntents_UsesVersionOneDiscriminators`.
- `RoundTrip_AllCommandDiscriminators_PreservesIdsEpochAndPayload`.
- `RoundTrip_AllServerEventDiscriminators_PreservesSequenceAndCorrelation`.
- `RoundTrip_SessionSnapshot_PreservesUsageInformationAndFullQueues`.
- `RoundTrip_SessionSnapshot_PreservesBackgroundPreferenceViewerCountAndFullDefinition`.
- `RoundTrip_SetContinueInBackgroundCommand_PreservesCommandAndCorrelationIds`.
- `RoundTrip_SessionRetentionChanged_PreservesPreferenceAndViewerCount`.
- `RoundTrip_QueueChanged_PreservesStableIdsRevisionsAndOrdering`.
- `RoundTrip_AgentInformation_ClonesDefinitionJsonElements`.
- `Deserialize_UnknownMember_RejectsFrame`.
- `Deserialize_CompiledPolicyMember_RejectsFrame`.
- `Deserialize_EmptyRequiredId_RejectsFrame`.
- `ServerFrames_ConcurrentPublish_AreStrictlyOrdered`.

`RemoteAgentSessionHostTests`:

- `OpenAsync_Start_CreatesOneRuntimeAndSnapshot`.
- `OpenAsync_AttachMissingRuntime_ReturnsIndistinguishableNotFound`.
- `OpenAsync_StartOrAttachConcurrent_CreatesOneRuntime`.
- `GetStatusAsync_AuthorizedPeer_ReturnsRunningOrNotRunningWithoutAttaching`.
- `GetStatusAsync_UnauthorizedPeer_ReturnsUnavailableWithoutRuntimeLookup`.
- `OpenAsync_ReconnectCoveredCursor_ReplaysWithoutSnapshotOrRelaunch`.
- `OpenAsync_ReconnectExpiredCursor_SendsSnapshotWithoutRelaunch`.
- `OpenAsync_ChildSubagent_ReauthorizesMembership`.
- `Command_DuplicateIdSamePayload_ReturnsCachedResultWithoutMutation`.
- `Command_DuplicateIdDifferentPayload_ReturnsConflict`.
- `QueueCommand_StaleExpectedRevision_ReturnsConflictWithoutMutation`.
- `QueueCommand_InvalidQueueOrItem_ReturnsRejectedWithoutDelta`.
- `SetToolEnabledCommand_EachMutation_ReauthorizesAndBroadcastsToolsEvent`.
- `QueueCommand_Applied_BroadcastsOneOrderedDeltaToEveryViewer`.
- `QueueCommand_Applied_TaskCompletesAfterAuthoritativeRevisionApplied`.
- `QueueConsumption_CurrentRun_BroadcastsOwnerRevisionAfterEnqueue`.
- `Command_EachMutation_ReauthorizesPeer`.
- `TakeOverAsync_ConfirmedOldTermination_AdvancesGenerationThenStartsNewEpoch`.
- `TakeOverAsync_UnconfirmedLiveLease_FailsClosed`.
- `OwnershipLease_RenewalEveryTenSeconds_ExtendsThirtySecondExpiry`.
- `OwnershipLease_RenewalUncertain_FencesBeforeSafetyDeadline`.
- `OwnershipLease_HostCrashExpires_RecoveryMarksOldEpochStoppedBeforeRestart`.
- `DisposeAsync_HostShutdown_DisposesAllRuntimeTrees`.
- `OpenAsync_AttachRacesLastViewerStop_WinnerDeterminesExistingOrFreshEpoch`.
- `OpenAsync_ReconnectAfterGrace_ReturnsNotFoundWithoutStartingRuntime`.
- `OpenAsync_UnauthorizedPeer_DoesNotSerializeDefinitionOrSessionMetadata`.
- `SetContinueInBackgroundAsync_ZeroViewersFalse_StopsImmediately`.

`RemoteAgentSessionRuntimeRegistryTests`:

- `GetOrStartAsync_ConcurrentCallers_StartsFactoryOnce`.
- `GetOrStartAsync_CancelledFactory_RemovesFailedEntry`.
- `TryGetAsync_WrongGeneration_ReturnsNull`.
- `TryTerminateAsync_ExactEpoch_FencesThenDisposesOnce`.
- `TryTerminateAsync_StaleEpoch_ReturnsFalse`.
- `Attach_MultipleViewers_UsesIndependentAttachmentLeases`.
- `AttachmentDispose_NonFinalViewer_DoesNotDisposeRuntime`.
- `AttachmentDispose_LastViewer_DefaultPolicy_DisposesRuntime`.
- `AttachmentDispose_LastViewer_BackgroundEnabled_PreservesRuntime`.
- `TransportLoss_ReconnectWithinFiveSeconds_ReusesAttachmentAndEpoch`.
- `TransportLoss_GraceExpiresAsLastViewer_DefaultPolicy_DisposesRuntimeAndChildren`.
- `TransportLoss_GraceExpiresAsLastViewer_BackgroundEnabled_PreservesRuntimeAndChildren`.
- `SetContinueInBackground_TrueWithViewers_PersistsWithoutStopping`.
- `SetContinueInBackground_FalseWithViewers_PersistsWithoutStopping`.
- `SetContinueInBackground_FalseAtZeroViewers_StopsImmediately`.
- `TryTerminateAsync_ConcurrentAttachOrPreferenceChange_TerminateWins`.
- `RuntimeDispose_ActiveAttachments_DisposesChildrenPersistsThenEmitsOneTerminal`.
- `RuntimeDispose_ActiveTurn_FencesInterruptsPersistsTerminalThenClosesChannels`.
- `HostCrash_Restart_RecordsInterruptedEpochStoppedAndPreservesPreference`.
- `ReplayBuffer_Over4096Events_DropsOldest`.
- `ReplayBuffer_Over8MiB_DropsOldest`.
- `ReplayBuffer_Over15Minutes_ForcesSnapshotFallback`.

`AgentSessionAttachAuthorizerTests`:

- `AuthorizeAsync_OwnerPeerAllowed_ReturnsAllow`.
- `AuthorizeAsync_UnrelatedPeerDenied_ReturnsIndistinguishableDenial`.
- `AuthorizeAsync_MutationAfterAclChange_DeniesPreviouslyAttachedPeer`.
- `AuthorizeAsync_TakeoverWithoutOwnerPermission_Denies`.
- `AuthorizeAsync_Cancelled_PerformsNoRuntimeLookupOrMutation`.

#### End-to-end transport, MXC, and isolation tests

`RemoteAgentSessionScenarioTests` (`Phantom.Workspaces.Tests/Scenarios`):

- `TransportFrames_SnapshotAndConcurrentDeltas_ArriveInSequence`.
- `Reconnect_RetainedCursor_ReplaysExactlyOnce`.
- `Reconnect_ReplayGap_UsesSnapshotAtHighWaterMark`.
- `ConcurrentViewers_OneDetaches_OtherContinuesAndRuntimeSurvives`.
- `ConcurrentViewers_LastDetaches_DefaultPolicy_RuntimeStops`.
- `ConcurrentViewers_LastDetaches_BackgroundEnabled_RuntimeContinues`.
- `Cancellation_SendWaitCancelled_CommandDeduplicatesOnRetry`.
- `Queues_TwoGuisConcurrentCommands_ConvergeOnOwnerState`.
- `Queues_ReconnectSnapshot_ContainsDefaultImmediateHeldAndCustomQueues`.
- `Queues_ReconnectReplay_AppliesDeltasByEpochAndGlobalSequence`.
- `Queues_ReplayGap_RefreshesFromAuthoritativeSnapshot`.
- `Queues_ActiveRunEnqueue_UsesSameCommandAsFutureTurn`.
- `Disposal_ClientChannelLost_ReconnectWithinGrace_RuntimeAndChildrenSurvive`.
- `Disposal_ClientChannelLost_GraceExpiresDefaultPolicy_DisposesRuntimeAndChildren`.
- `Disposal_ClientChannelLost_GraceExpiresBackgroundEnabled_PreservesRuntimeAndChildren`.
- `OpenSubagent_Authorized_ReturnsIndependentChildProxy`.
- `ModalResponse_ConcurrentViewers_AcceptsFirstAndRejectsStaleSecond`.
- `Takeover_ActiveOldRuntime_FencesOldProxyBeforeNewRuntimeMutates`.
- `CurrentSessionContext_TwoRemoteSessions_DoNotCrossContaminate`.

`CopilotQueueSteeringTests` (`Phantom.Workspaces.Llm.Core.Tests`, internal contract):

- `ForwardPendingImmediateMessages_ActiveRun_SendsQueuedItemWithImmediateMode`.
- `ForwardPendingImmediateMessages_QueuedOrHeldItem_DoesNotSendAsSteering`.
- `ForwardPendingImmediateMessages_TeardownSuspended_LeavesItemQueued`.
- `SteeringMessageForwarded_ConsumedItem_RecordsHistoryBeforeInternalSend`.
- `ToolResultSteeringMiddleware_NonCopilotImmediateItem_InjectsAtSupportedBoundary`.
- `ToolResultSteeringMiddleware_NoSupportedBoundary_LeavesItemForFutureTurn`.

`RemoteExecutionContainmentMatrixTests`:

- `ExecutionMatrix_AllEightPlacementContainmentCases_UseExpectedAgentAndLaunchHosts`.
- `ExecutionMatrix_AllEightCases_ResolveTrustOnlyOnFinalLaunchHost`.
- `ExecutionMatrix_ContainmentNotRequired_UsesOrdinaryExecutorBranch`.
- `ExecutionMatrix_ContainmentRequired_UsesMxcExecutorBranch`.
- `ExecutionMatrix_RemoteBoundary_ContainsNoCompiledPolicyOrPolicyPath`.
- `ExecutionMatrix_GuiAndEntityTools_UseApplicationAuthorizationNotMxcClaim`.
- `SplitClient_RemoteCopilot_LocalAgentChat_PreservesDistinctTopology`.
- `ContainedCopilot_RemoteOwner_CreatesWrapperEnvelopeOnlyOnCopilotHost`.
- `ContainedStdioMcp_RemoteExecutor_OwnsProcessTransportOnToolHost`.
- `ContainmentCompileFails_ReturnsSanitizedErrorAndDoesNotLaunch`.
- `MxcLaunchFails_DoesNotFallbackUncontained`.
- `ConstrainedHttpOrSse_ReturnsUnsupportedPolicy`.
- `RemoteError_PolicyPathEnvironmentArgvAndStderr_AreAbsent`.
- `Takeover_NewHost_RehydratesIntentAndRecompilesPolicy`.

This matrix also consumes the public-method tests owned by #1474-#1477:
`Compile_*` in `MxcTrustProfilePolicyCompilerTests`,
`ProcessExecutor_*`/`ProcessHandle_*`,
`CreateConnection_*` in `CopilotRuntimeConnectionFactoryTests`,
`ConnectAsync_*`/`DisposeAsync_*` in `ProcessExecutorBackedClientTransportTests`, and
`RemoteStdio_*` in `RemoteMcpHostHandlerTests`. Those APIs are not duplicated here.
Remote integration additionally requires:

- `GetCompilationAsync_ConcurrentCallers_ResolveAndCompileOnce` in
  `AgentExecutionTrustContextTests`.
- `GetCompilationAsync_StaleRevision_CachesFailClosedResult` in
  `AgentExecutionTrustContextTests`.
- `StartAsync_CancelledDuringLaunch_CleansPartialProcess` and
  `TerminateAsync_RepeatedCall_KillsTreeOnce` in `ProcessExecutorTests`.
- `WaitAsync_Cancelled_DoesNotReleaseProcessOwnership` in `ProcessHandleTests`.
- `CreateConnectionAsync_ConcurrentLifecyclePaths_ReturnsOneSelection` and
  `CreateConnectionAsync_Cancelled_RemovesUnconsumedEnvelope` in
  `CopilotRuntimeConnectionFactoryTests`.
- `DisposeAsync_UnconsumedHandoff_DeletesFileOnce` and
  `DisposeAsync_TransferredConnection_DoesNotKillSdkRuntime` in
  `CopilotRuntimeConnectionFactoryTests`.
- `Name_ConstructedTransport_ReturnsConfiguredName` in
  `ProcessExecutorBackedClientTransportTests`.

The matrix contains **346 scenario bullets representing 349 named test methods**; three integration
bullets each name two methods. Generic
`CommandMethod_EmptyCommandId_ThrowsArgumentException`,
`CommandMethod_CancelledAfterWrite_DoesNotRetractCommand`, and
`CommandMethod_NotConnected_ThrowsInvalidOperationException` are parameterized over every public
client command, including `SetContinueInBackgroundAsync`; the seven queue success mappings cover
both public declarations of each queue operation. Every other added or changed public method has an
explicitly named success test and its applicable validation, cancellation, conflict, disposal, or
idempotency test above.

## Implementation plan

Each step is independently committable and leaves existing local behavior passing.

### Commit 1 - Persist and hydrate runtime intent

**Files:** `Phantom.Workspaces.Data.Core/JsonSchemas/agent-session.json`,
`AgentSessionEntityFactory`, `AgentSessionRuntimeIntentData`,
`PersistedAgentSessionRuntimeIntent`, `AgentSessionRuntimeContext`,
`IAgentSessionRuntimeContextFactory`, `AgentSessionRuntimeContextFactory`,
`JsonEntities/entity-type-views/agent-session-entity-type-view.json`, and acquisition callers.
**Tests:** factory, default/true background persistence, restart hydration, round-trip, legacy,
grouping, malformed, no-policy, and fresh/non-GUI acquisition tests above.
**Boundary:** follows #1481: entity JSON is interpreted at the shared acquisition layer, never in GUI
routing. It consumes the already-present `AgentSessionExecutorBindings` and `ExecutorBindings`
surfaces on `features`; it neither waits for nor recreates the later MXC APIs.
**Dependencies:** none.

### Commit 2 - Add the common chat surface

**Files:** `IAgentChat`, `Usage`, `AgentInformation`, `IAgentInputQueues` and snapshot/result types,
owner queue adapter, stable ids/revisions in the existing queue domain, `AgentChat`,
`RunningAgentChat`, `RunningAgentChatLease`, `RunningAgentChatWithEntityInfo`, `AgentViewModel`, and
`InputQueueViewModel`.
**Tests:** `AgentChatInterfaceTests`, `AgentInputQueuesTests`, internal Copilot/non-Copilot queue
consumption tests, constructor/common-surface tests, and unchanged local regression suite. Migrate
the UI away from concrete queue collections and index identity. No transport behavior yet.
**Dependencies:** none.

### Commit 3 - Add strict protocol and proxy state

**Files:** `Phantom.Workspaces.Llm.Core/Remote/AgentSessionProtocol.cs`, codec,
`RemoteAgentSessionClient`, and `RemoteAgentChat`.
**Tests:** codec, all client public methods, atomic usage/information, proxy queue parity,
full authorized `AgentDefinition`, no metadata on denial, background command/event/snapshot,
revisions/conflicts/deduplication, state/events/commands, cancellation, and disposal. The protocol
contains enqueue/edit/remove/move/configure/create/delete queue commands and no steering command.
Use an in-memory `IMessageChannel`; no server runtime or MXC dependency.
**Dependencies:** Commit 2.

### Commit 4 - Add authorization, host, and viewer lifecycle

**Files:** peer identity provider, attach authorizer, listener, host, runtime registry, runtime and
attachment leases, replay buffer, transport composition registration.
**Tests:** listener, authorizer, host, registry, replay limits, multiple viewers, sanitized errors, and
no existence disclosure, including default final-viewer stop, persisted background retention,
five-second reconnect grace, attach/stop races, explicit-terminate precedence, complete graceful
cleanup, queue convergence, and reconnect snapshot/replay.
**Dependencies:** Commits 1 and 3.

### Commit 5 - Integrate running table and all open paths

**Files:** `AcquireAgentChatRequest`, `IRunningAgentChatTable`, `RunningAgentChatTable`,
`OpenAgentSessionShortcutHandler`, fresh launcher, auto-resume, non-GUI acquisition path.
**Tests:** every table method, local/remote single-flight, restore/fresh/auto-resume parity, and no
rehydration of an existing runtime. Includes local/remote background preference dispatch and
authoritative running-row metadata.
**Dependencies:** Commits 1-4.

### Commit 6 - Add takeover and deterministic runtime shutdown

**Files:** persisted ownership compare/exchange, host takeover coordinator, runtime fencing,
termination acknowledgement, lease expiry.
**Tests:** successful takeover, stale generation/epoch, unconfirmed old lease, command fencing,
terminal ordering, ten-second renewal/thirty-second expiry, crash recovery, and complete child
cleanup.
**Dependencies:** Commits 4-5.

### Commit 7 - Integrate host-local trust and component placement

**Files:** runtime hydration into `AgentServices`, `CurrentSessionContext`,
`AgentExecutionTrustContext`, existing `ExecutorBindings` consumers, remote MCP/Copilot requests.
**Tests:** stale trust revision, per-session context isolation, and all eight
agent-placement/component-placement/containment combinations.
**Dependencies:** Commit 5 and #1472, #1474, #1475, and #1477.

### Commit 8 - Integrate Copilot wrapper and stdio MCP ownership

**Files:** existing #1476 `CopilotRuntimeConnectionFactory` handoff and #1477
`ProcessExecutorBackedClientTransport`/`ProcessOwnedMcpTransport` integration only; no replacement
types.
**Tests:** direct versus wrapper selection, stdio ownership, constrained HTTP/SSE rejection,
fail-closed launch, sanitized projection, and no compiled policy on a machine frame.
**Dependencies:** Commit 7 and #1473-#1477.

### Commit 9 - Complete remote UI, modals, and notifications

**Files:** `AgentViewModel`, `AgentSessionWorkspaceTabViewModel`, editor/modal controls,
`Phantom.Workspaces/ViewModels/RunningAgentBrainViewModel.cs`,
`Phantom.Workspaces/ViewModels/RunningAgentRowViewModel.cs`,
`Phantom.Workspaces/Controls/RunningAgentBrainControl.axaml(.cs)`, `Notification`,
`NotificationEntry`, `INotificationService`, `NotificationService`, and `NotificationsViewModel`.
**Tests:** UI public API tests above, multiple modal ownership, descendant aggregation, independent
notification clearing, remote interrupt versus terminate, and checkbox binding, command,
enabled/pending state, and accessibility.
**Dependencies:** Commits 2, 5, and 6.

### Commit 10 - End-to-end security and lifecycle validation

**Files:** tests only except defects found by integration.
**Tests:** transport ordering, replay/snapshot fallback, concurrent viewers, authorization denial,
cancellation, default/background lifecycle, reconnect grace, attach/stop and terminate races,
persistence/restart, disposal, takeover, fail-closed MXC, complete placement/containment
cross-product, no compiled policy on wire, and no cross-session `CurrentSessionContext`
contamination.
**Dependencies:** Commits 1-9 and completed #1471-#1477 surfaces.

### Resolved implementation choices

- Peer identity comes only from `ITransportPeerIdentityProvider` populated by the authenticated
  transport, never from open-request JSON.
- Runtime ownership is process-scoped and keyed by session+generation; the runtime lease owns
  resources, while logical attachment count plus persisted `continue-in-background` determines
  ordinary zero-viewer retention. Termination additionally matches epoch and always wins.
- Replay is bounded to 4,096 events, 8 MiB, and 15 minutes; any uncovered cursor receives a snapshot.
- Only cross-project contracts are public. Host, authorization, registry, lease, codec, concrete
  payload, and error types are internal.
- Runtime hydration uses the #1481 acquisition seam. GUI code supplies persisted entity data but does
  not build routing or trust objects.
- The only compiled-policy serialization is #1476's protected same-machine one-use envelope.
- Unexpected transport loss has a five-second reconnect grace keyed by peer-bound attachment token;
  explicit detach, UI tab close, and viewer-application shutdown have no grace.
- Every future master or detail issue derived from this design has a title beginning exactly
  `[RemoteChat] - `.

### Master/detail bug derivation

The master title is **`[RemoteChat] - Design: remote agent sessions`**. Create exactly one detail bug
per implementation commit, with these titles and dependency links:

1. **`[RemoteChat] - Persist and hydrate runtime intent`** — no dependencies.
2. **`[RemoteChat] - Add the common chat surface`** — no dependencies.
3. **`[RemoteChat] - Add strict protocol and proxy state`** — depends on detail 2.
4. **`[RemoteChat] - Add authorization, host, and viewer lifecycle`** — depends on details 1 and 3.
5. **`[RemoteChat] - Integrate running table and all open paths`** — depends on details 1-4.
6. **`[RemoteChat] - Add takeover and deterministic runtime shutdown`** — depends on details 4-5.
7. **`[RemoteChat] - Integrate host-local trust and component placement`** — depends on detail 5 and
   prerequisite issues #1472, #1474, #1475, and #1477.
8. **`[RemoteChat] - Integrate Copilot wrapper and stdio MCP ownership`** — depends on detail 7 and
   prerequisite issues #1473-#1477.
9. **`[RemoteChat] - Complete remote UI, modals, and notifications`** — depends on details 2, 5, and 6.
10. **`[RemoteChat] - Add end-to-end security and lifecycle validation`** — depends on details 1-9
    and completed prerequisite issues #1471-#1477.

Each detail body is derived verbatim from its commit's Files, Tests, Boundary where present, and
Dependencies fields, plus the applicable detailed-design types and behavior contracts. The master
body includes the Requirements, Chosen design, complete implementation plan, all ten detail links,
and the same dependency graph. No issue may omit its relevant public signatures or named tests.

No design ambiguity remains for protocol shape, authorization source, lease ownership, replay
fallback, visibility, or containment handoff. Attach uses same-owner-user authorization as specified
above and adds no second ACL schema. Future delegated sharing requires a separate versioned design.
