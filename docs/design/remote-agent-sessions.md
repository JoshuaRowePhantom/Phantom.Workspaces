# Remote Agent Sessions

## Problem statement

Today, an agent session is always started and run on the process that hosts the current user-computer-profile: `OpenAgentSessionShortcutHandler` uses `ExecutionTargetResolver` to pick a trusted executor, and the resulting `AgentChat` lives entirely in the calling instance. There is no first-class model of "this session belongs to profile P", no UX for opening/resuming a session whose profile differs from the local one, no way to see or interrupt sessions that are actually running on another profile, and no way for a running session to surface a modal question above the chat input without either blocking the whole GUI or losing the "attention needed" indicator when the user switches tabs. We want a coherent model in which every session is bound to a specific user-computer-profile, cross-profile start/resume is an explicit user choice, remotely-running sessions are visible and controllable in the local UI via a proxy `AgentChat`, the Session view groups sessions by their owning profile, and per-tab notifications support multiple concurrent, independently-cleared notification sources (idle-chat vs. modal-info-needed).

## Requirements

### Session ↔ profile association

- Every agent session entity CREATED after this feature ships MUST carry a persisted reference to exactly one user-computer-profile — the session's "owning profile" — established at creation and never null thereafter for such sessions.
- The owning-profile reference MUST be stored as a persisted field on the `agent-session` entity (alongside its existing `agent-session-id`), using the same `EntityId`-style reference the codebase already uses for `user-computer-profile` entities. The specific field name / shape MUST match whatever `group-by-parent` on `agent-session`'s entity-type-view can read (see §"Sessions view" below), so that the same field drives both persistence and view grouping.
- Legacy sessions that predate this feature and that therefore have NO owning-profile reference are NOT force-migrated. They remain in the store with a missing/absent owning-profile field. In the Sessions view they appear UNGROUPED (i.e. not under any profile group), and are otherwise addressed exactly like today's sessions (opened via the local profile).
- The "current" user-computer-profile of a running Phantom.Workspaces instance MUST remain determined as it is today (via `WorkspaceEntitySession.UserComputerProfileEntityId` / `CurrentSessionContext.UserComputerProfile`); the new per-session field is a separate, per-session value and MUST NOT be conflated with the current-instance profile.

### Starting / resuming a session across profiles

- When the user opens a session whose owning profile equals the current profile, behavior MUST be unchanged from today (`OpenAgentSessionShortcutHandler` proceeds directly).

- When the user opens a legacy session (no owning-profile reference), behavior MUST be unchanged from today: it runs locally on the current profile, and MUST NOT rewrite the entity to record an owning profile (no forced migration).

> This is incorrect. The session should automatically be associated with the user computer profile starting it.

- When the user opens a session whose owning profile differs from the current profile, the user MUST be presented with a prompt offering exactly two choices:
  1. **Start/connect on the session's owning profile** — the session is started (if not running) or attached to (if already running) on its owning profile; the local UI interacts with it via the proxy `AgentChat` described below. The owning-profile reference is unchanged.
  2. **Resume locally** — see next bullet.
- **Resume locally is a PERMANENT rebind.** Choosing "Resume locally" MUST permanently rewrite the session entity's owning-profile reference to the local profile (persisted). After this rebind, the local profile is the session's owning profile forever after; opening the session from another profile subsequently will trigger the same prompt again, but now relative to the newly-recorded local owner.
- The "Resume locally" option MUST indicate to the user whether the session is currently running on the previous owning (remote) profile at the moment of the prompt.
- If the user chooses "Resume locally" AND the session is currently running on the previous owning profile, the resume action MUST terminate that remote run (invoke `Interrupt()` / cancel it on the previous owning-profile instance) before or as part of starting the local run. The user MUST NOT end up with the same session running concurrently on two profiles.
- The prompt MUST be reachable from every existing entry point that starts/opens an agent session (at minimum `OpenAgentSessionShortcutHandler.Handle` / `TryCreateAgentSessionTabForRestoreAsync`), so the choice cannot be bypassed.
- **No extra trust is required** to connect to a remote session beyond what `ExecutionTargetResolver` / `TransportTrustedExecutor` already require for today's remote execution. If the local profile is already permitted to reach the owning-profile instance for chat-client purposes, that same trust suffices for attach/subscribe.

### Remote connection and proxy `AgentChat`

- The client instance MUST be able to "connect to" a remote session — i.e. attach to an `AgentChat` that lives in the owning-profile instance — via the existing JSON-RPC/message-channel transport (`ITransport`, `ConnectToMessageChannelAsync`).
- Connection MUST be implemented as a proxy `AgentChat` on the client side backed by an `IChatClient` (or a small family of transport-backed services) that talks to the remote `AgentChat` over transport. This SHOULD reuse and extend the pattern already established by `ChatClientOverTransport` / `ChatClientTransportSession` / `ChatClientTransportListener`, extending the JSON message set (currently `process-streaming` / `streaming-update` / `streaming-update-complete` / `streaming-error`) as needed. The concrete choice of protocol shape is a Phase-2 decision (see Options).
- The proxy `AgentChat` on the client MUST expose the remote session's TOOLS such that they appear in the client-side agent's tool list (equivalent to `AgentChat.Tools` / `AgentChat.GetToolSnapshot()` / `ToolsChanged` today) — the client MUST be able to enumerate and display them, and receive change notifications, even though execution happens remotely.
- The proxy `AgentChat` on the client MUST expose the remote session's SUBAGENTS such that they appear in the client-side subagent registry (`ISubAgentChatRegistry`) and the client's `SubAgents` collection, so that subagent chats can be listed and opened locally the same way local subagents are.
- **Cross-profile subagents (design decision).** When the client is connected to a remote session and the user opens one of that session's SUBAGENT chats locally, that opened subagent chat is itself a connection to the remote subagent `AgentChat` via the SAME proxy mechanism (i.e. it becomes another connected/proxied `AgentChat` in the client's `IRunningAgentChatTable`). Subagents are NOT re-parented onto the local profile; the entire subagent tree remains owned by the remote owning-profile instance.
- A connected remote session MUST appear as a running chat in the CLIENT instance's `IRunningAgentChatTable` and therefore in the client's Running Agents flyout, with the same `IsThinking` / activity semantics as a local running chat.
- The proxy `AgentChat` MUST keep the client-side chat history and streaming state up to date with the remote session (streaming updates, completion, errors, interrupt state) via the transport channel; this includes mirroring the `IsChatRunning` / `IsBusy` state used by `AgentSessionWorkspaceTabViewModel` to drive tab notifications, and firing `ToolsChanged` and subagent-registry mutations on remote changes.

> Ideally, queue states are kept in sync, too. Probably this means something like the an AgentChat implementation that never dequeues things on the client side and the ordinary implementaiton on the server side?

- Transport failure (disconnect, remote profile going away) MUST be a well-defined state on the client-side proxy chat (surfaced to the UI), not a silent hang.

### Running-agents visibility (Running Agents flyout)

- The Running Agents flyout (`RunningAgentBrainViewModel`, backed by `IRunningAgentChatTable`) MUST show exactly two kinds of rows:
  1. Sessions running LOCALLY on this instance (unchanged from today).

> This should include sessions that remote clients have connected to on this instance.

  2. Remote sessions that THIS local instance has explicitly CONNECTED to (i.e. sessions for which a proxy `AgentChat` currently exists in this client and is therefore already registered in `IRunningAgentChatTable`).
- The flyout MUST NOT attempt to discover, poll, subscribe to, or otherwise surface sessions that are merely running on other profiles without a client-established connection. There is no cross-profile "running sessions" broadcast.
- Each row SHOULD make visually clear whether the row is a local session or a connected-remote session (and if remote, which user-computer-profile it belongs to), so the user can distinguish "same profile as me" from "on profile X".
- Sort order MAY continue to be by `LastActivityAt` (as `ResortRows` does today).

### Sessions view (existing view, add group-by-parent)

- The Sessions view is the EXISTING `views/sessions` view definition (`Phantom.Workspaces.Data.Core\JsonEntities\views\sessions-view.json`, entity-id `441f53e1-9a08-422b-bf09-675985d8dfe8`). It is an ordinary schema-driven view rendered by the generic view host, NOT a bespoke session-list view model — no new "list of sessions" VM is required.
- Grouping of the top-level agent-session sub-view MUST use the SAME `group-by-parent` mechanism used elsewhere: the `entity-type-view` for the `agent-session` entity type declares `group-by-parent` with `source: "field"`, `field-path: [<owning-profile field>]`, and `parent-entity-type-names: ["user-computer-profile"]`, exactly analogous to `git-worktree-entity-type-view.json`. `ViewHierarchyAssembler`'s existing group-by-parent path then handles the actual grouping at render time.
- There is currently NO `agent-session-entity-type-view.json` under `Phantom.Workspaces.Data.Core\JsonEntities\entity-type-views\`; adding it (with the above `group-by-parent`) is the concrete change that enables profile grouping in the Sessions view.
- Sessions whose owning-profile field is present group under that profile. Sessions whose owning-profile field is absent (legacy sessions) MUST render UNGROUPED in the Sessions view, using whatever "no parent" fallback `ViewHierarchyAssembler`'s group-by-parent already emits for missing-field cases.
- Group headers MUST identify the profile using the profile entity's normal display name (whatever the profile entity-type-view's title/display already provides).

### Interrupting agents from the Running Agents flyout

- Every row in the Running Agents flyout MUST have a red "X" (interrupt) button aligned on the right-hand side of the row.
- Because the flyout shows only local + connected-remote sessions (see above), interrupt applies only to those two categories. There is NO requirement to transiently connect to an unconnected remote session just to interrupt it.
- Clicking the interrupt button MUST interrupt that agent's current run:
  - For local sessions, this MUST invoke the existing `AgentChat.Interrupt()` path (already reachable via `AgentViewModel.InterruptCommand` and its `activeRunCancellation` `CancellationTokenSource`).
  - For connected-remote (proxy) sessions, this MUST send an interrupt request over the transport channel that causes `Interrupt()` to be called on the remote `AgentChat`, and the client MUST reflect the resulting state change.
- The interrupt button MUST only be enabled when the agent has something interruptible to interrupt (equivalent to today's `IsChatRunning` / `IsThinking` condition).

### Modal-above-input model

- A running session MUST be able to raise a "modal information" request that is displayed as a modal box ABOVE the agent chat input control (i.e. above `AgentChatInputQueueControl` / `InputQueueViewModel` / `QueueComposerViewModel` in `Phantom.Workspaces.Agent.Gui`), NOT as a window-level modal dialog and NOT blocking any other tab or GUI element outside that session's chat pane.
- **Multiple simultaneous modals per session.** A session MUST support MULTIPLE concurrent modal boxes at the same time (e.g. several subagents each requesting information in parallel). The chat pane's above-input region MUST be able to display a stack/queue of active modals for the session, and the input control MUST remain gated for that session until every modal is dismissed.
- While at least one modal box is active for a session, the user MUST NOT be able to submit new input to that session's chat, but MUST remain free to interact with the rest of the application (other tabs, other sessions, menus, etc.).
- The modal-box mechanism MUST support a session raising, updating, and dismissing modal information programmatically (i.e. an API on the session/agent-chat layer, not just a one-shot UI helper).
- **Modal content is a general abstraction, not a fixed payload.** The above-input modal is a *general modal content model*: a modal has a title, a body, and zero or more choice actions (extensible for future kinds — freeform-text response, multi-choice, tool-approval, etc.). Each concrete modal kind supplies its own content type implementing that abstraction. Day-one example: a "take over remote session locally" modal shows a question plus a confirmation button that, when pressed, triggers the local-rebind flow.
- The modal-box mechanism MUST work identically for local and proxied (remote) sessions; when a remote session raises a modal, the modal, its updates, its user response, and its dismissal MUST be transported over the same JSON-RPC channel used for the chat stream (message kinds to be defined in Phase 2).

### Notifications scheme

- A tab MUST be able to have MULTIPLE notifications outstanding simultaneously, each with its own source/kind and its own lifecycle. The current `NotificationState { NotInteresting, Interesting }` per-tab boolean surfaced by `NotificationIndicatorTabHeaderItemViewModel.HasUnread` is insufficient and MUST be replaced (or wrapped) by a per-tab collection of notifications.
- The notification-kind scheme MUST be OPEN/EXTENSIBLE. Day-one kinds are exactly two:
  - **"Chat became idle"** — raised in `AgentSessionWorkspaceTabViewModel.OnAgentPropertyChanged` when `IsChatRunning` goes true→false. MUST be cleared automatically when the user switches TO that tab (today's behaviour).
  - **"Modal pending"** — raised when a session has at least one active modal. MUST NOT be cleared by tab activation; MUST be cleared only when the last modal for that session is dismissed.
- The scheme MUST accommodate additional future notification kinds (e.g. tool-approval-required, error, mention, etc.) without redesign: each kind declares its own clearing policy (on-tab-activation vs. on-explicit-event) and its own lifetime source.
- The tab's exclamation indicator (`HasUnread`) MUST reflect the LOGICAL OR of all currently-outstanding notifications for that tab: the icon is shown if any notification is outstanding, and hidden only when every notification for the tab has been cleared.
- Clearing rules MUST be per-notification, not per-tab: e.g. activating a tab that has both an idle notification and a modal-pending notification MUST clear only the idle one and leave the modal-pending one (and therefore the exclamation icon) in place.
- The app-wide notifications aggregate (`NotificationsViewModel.HasUnread`) MUST likewise reflect the logical OR of per-tab notifications, consistent with the per-tab indicator.

### Context (existing code)

Key existing types and files that this feature will touch or extend (all paths relative to the `features\` submodule):

- **Profile model**
  - `Phantom.Workspaces.Data.Core\WorkspaceEntitySession.cs` — `WorkspaceEntitySession.UserComputerProfileEntityId` (current-instance profile).
  - `Phantom.Workspaces.Llm\CurrentSessionContext.cs` — `CurrentSessionContext.UserComputerProfile` runtime accessor.
  - `Phantom.Workspaces.Transport\UserComputerProfileTransportFactory.cs` — builds transport to a specific profile.
  - `Phantom.Workspaces.Llm.Core\Trust\ExecutionTargetResolver.cs` — resolves a `targetClientInstance` string (`.` = local, or `{"type":"user-computer-profile","entity-id":"…"}`) into a connection descriptor. Existing trust envelope for cross-profile execution; reused unchanged for attach.
  - `Phantom.Workspaces.Llm.Core\Transport\TransportTrustedExecutor.cs` — builds the trusted executor / `ChatClientOverTransport` used for remote execution today.
- **AgentChat + proxy chat client**
  - `Phantom.Workspaces.Llm.Core\AgentChat.cs` — sealed `AgentChat : IAsyncDisposable, IServiceProvider, ISubAgentChatRegistry, IRunningSubAgent, ISubAgentTable`. Key surface: constructor via `InternalCreateAgentChatRequest`; `History` (`AgentChatHistoryCollection`); `HistoryPopulated` task; `IsBusy`; `AcceptsUserInput`; `SubAgents` (`ReadOnlyObservableCollection<IRunningSubAgent>`); `Tools` / `GetToolSnapshot()`; `ToolsChanged` event; `RaiseToolsChanged()`; `Interrupt()` (~line 830) and private `activeRunCancellation` CTS.
  - `Phantom.Workspaces.Transport\Chat\ChatClientOverTransport.cs`, `ChatClientTransportSession.cs`, `ChatClientTransportListener.cs`, `IChatSteeringTarget.cs` — existing `IChatClient`-over-`ITransport` layer. Current JSON kinds: `process-streaming` (client→server), `streaming-update`, `streaming-update-complete`, `streaming-error` (server→client). All framed as `JsonElement` via `ToJsonElement`/`FromJsonElement`.
- **Session opening / routing**
  - `Phantom.Workspaces\ViewModels\OpenAgentSessionShortcutHandler.cs` — `Handle()`, `TryCreateAgentSessionTabForRestoreAsync()`, `TryBuildAgentAsync()`; today consults `ITrustedExecutorSelector` / `ExecutionTargetResolver`.
- **Running agents UI**
  - `Phantom.Workspaces\ViewModels\RunningAgentBrainViewModel.cs` — flyout VM, `IsOpen`, `Rows`, `ResortRows()` (sorts by `LastActivityAt`).
  - `Phantom.Workspaces\ViewModels\RunningAgentRowViewModel.cs` — per-row VM, `IsThinking`.
  - `Phantom.Workspaces\Controls\RunningAgentBrainControl.axaml(.cs)` — view.
  - `IRunningAgentChatTable` / `RunningSessions` collection — source of running-session rows; connected-remote proxy chats are inserted here on connect.
  - `AgentViewModel.InterruptCommand` (in `Phantom.Workspaces.Agent.Gui`) — existing local interrupt path calling `AgentChat.Interrupt()`.
- **Sessions view**
  - `Phantom.Workspaces.Data.Core\JsonEntities\views\sessions-view.json` — the existing "Sessions" view (entity-id `441f53e1-9a08-422b-bf09-675985d8dfe8`); its top-level agent-session sub-view queries `entity-type: agent-session` filtered to sessions with no `parent-agent-session-ids[0]`. Group-by-parent is NOT set at the view level — it comes from the entity-type-view.
  - `Phantom.Workspaces.Data.Core\JsonEntities\entity-type-views\git-worktree-entity-type-view.json` — reference implementation of `group-by-parent` (source: field, field-path, parent-entity-type-names).
  - `Phantom.Workspaces\ViewHierarchyAssembler.cs` — the generic host that consumes `group-by-parent` at render time.
  - **NEW file to add in Phase 3+:** `Phantom.Workspaces.Data.Core\JsonEntities\entity-type-views\agent-session-entity-type-view.json` — the entity-type-view for `agent-session` carrying the `group-by-parent` clause pointing at the owning-profile field.
- **Notifications / tab indicator**
  - `Phantom.Workspaces\Services\Notifications\Notification.cs` — record with `NotificationState { NotInteresting, Interesting }`; `INotificationService`.
  - `Phantom.Workspaces\Services\Notifications\NotificationService.cs` — `Notify()`; sets/clears per-tab flag; auto-marks read on active tab.
  - `Phantom.Workspaces\ViewModels\NotificationsViewModel.cs` — app-wide `HasUnread` aggregate.
  - `Phantom.Workspaces\ViewModels\TabHeaderViewModel.cs` — `NotificationIndicatorTabHeaderItemViewModel.HasUnread`.
  - `Phantom.Workspaces\ViewModels\AgentSessionWorkspaceTabViewModel.cs` — `OnAgentPropertyChanged` raises "chat became idle" notification when `IsChatRunning` goes true→false; `IsInterrupted` detection.
- **Agent chat input control (host of the modal box)**
  - `Phantom.Workspaces.Agent.Gui\Controls\AgentChatInputQueueControl.axaml(.cs)` — view.
  - `Phantom.Workspaces.Agent.Gui\Controls\QueueComposerControl.axaml(.cs)` — hosts the existing above-input popup (`Border` with class `slash-completions-popup` bound to `Completions.IsVisible`).
  - `Phantom.Workspaces.Agent.Gui\ViewModels\InputQueueViewModel.cs` — `DefaultComposer`, per-queue composer factory.
  - `Phantom.Workspaces.Agent.Gui\ViewModels\QueueComposerViewModel.cs` — composer; owns `SlashCompletionsViewModel Completions` (the existing above-input popup precedent).
  - `Phantom.Workspaces.Agent.Gui\ViewModels\SlashCompletionsViewModel.cs` — reference implementation of an above-input popup driven by a per-composer VM property.
- **Interrupt path**
  - `AgentChat.Interrupt()` (see above) — local interrupt entry point that any remote-interrupt transport message must ultimately invoke on the owning-profile instance.

## Options

Only sections where a genuine architectural choice exists are enumerated as multi-option. Other decisions are already fixed by the requirements + existing patterns and are stated as "intended approach" for reference.

### Decided (single intended approach, no alternatives to weigh)

- **Session ↔ profile field on the entity.** Add a persisted `EntityId`-style reference field on `agent-session` (e.g. `owning-user-computer-profile-id`) alongside the existing `agent-session-id`. Same shape used by `git-worktree.computer-user-profile-id`. Legacy sessions leave the field absent.
- **Sessions view grouping.** Add `Phantom.Workspaces.Data.Core\JsonEntities\entity-type-views\agent-session-entity-type-view.json` with `group-by-parent: { source: "field", field-path: ["owning-user-computer-profile-id"], parent-entity-type-names: ["user-computer-profile"] }`. `sessions-view.json` is not modified. `ViewHierarchyAssembler`'s existing group-by-parent path renders the grouping; legacy sessions with no field fall into the "ungrouped" bucket.
- **Interrupt.** Local interrupts continue to invoke `AgentChat.Interrupt()` directly. Remote interrupts go over the same proxy transport as the chat itself (a new `interrupt` message kind on that transport, dispatched by the server-side session to the remote `AgentChat.Interrupt()`). No separate control channel.
- **Notifications data structure.** Replace `NotificationIndicatorTabHeaderItemViewModel.HasUnread : bool` (or wrap it) with a per-tab collection of notification kinds, where `HasUnread` becomes a computed OR. Each `Notification` gains a `Kind` discriminator plus a `ClearOnTabActivation : bool` policy (or equivalent). `NotificationService.Notify()` learns to add/replace by (tab, kind) rather than by tab only. The single-VM-per-tab wiring in `MainWindowViewModel` / `WorkspacePaneDocument` is preserved; only its input semantics change.

### Option area A — Proxy remote-`AgentChat` protocol and connection layer

The client-side proxy `AgentChat` must (i) attach to an existing (or start a new) remote `AgentChat`; (ii) mirror its `History`, streaming state, `IsBusy` / `IsChatRunning`, `Tools` (with `ToolsChanged`), `SubAgents` / `ISubAgentChatRegistry`; (iii) allow opening subagents as further proxies; (iv) send `Interrupt()`; and (v) transport modal-raise / modal-update / modal-response / modal-dismiss. Today's `ChatClientOverTransport` speaks only per-run request/response streaming; there is no "attach to existing chat" verb, no tool-list mirroring, no subagent enumeration, no interrupt verb, and no modal channel. Three real options for how to close that gap.

#### Option A.1 — Extend `ChatClientOverTransport` in place with additional RPC verbs; proxy `AgentChat` built on the extended `IChatClient`

- **Architecture.** Add new message kinds to the existing chat-client transport session (`ChatClientTransportSession` server side, `ChatClientOverTransport` client side): `attach` (bind to an existing session-id instead of implicitly creating a run), `enumerate-tools` + server-pushed `tools-changed`, `enumerate-subagents` + server-pushed `subagents-changed` + `open-subagent` (returns a nested channel for that subagent), `interrupt`, `modal-raised` / `modal-updated` / `modal-dismissed` (server→client) and `modal-respond` (client→server). The client's proxy `AgentChat` is instantiated with a variant `IChatClient` (extended `ChatClientOverTransport`) whose `GetStreamingResponseAsync` streams the remote run, and whose extra methods (exposed via `GetService`) feed the local `history` / `Tools` / `subAgentItems` collections and drive `Interrupt` / modal APIs.
- **Streaming updates & history.** Reuses today's `streaming-update` / `streaming-update-complete` / `streaming-error` for the live run stream. History back-fill on attach is delivered as a burst of `streaming-update` frames (or a dedicated `history-snapshot` frame) drained into `AgentChatHistoryCollection` before `historyPopulated.TrySetResult()`.
- **Tool enumeration.** New `enumerate-tools` request + `tools-changed` push events; the proxy translates them into `Tools` snapshots and raises `ToolsChanged`.
- **Subagent enumeration / opening.** New `enumerate-subagents` + `subagents-changed`; opening a subagent locally calls `open-subagent(subagentId)` which yields a nested attached-channel used to construct a child proxy `AgentChat` (registered in `IRunningAgentChatTable` and `ISubAgentChatRegistry`).
- **Remote interrupt.** New `interrupt` verb on the same channel; server dispatches to the remote `AgentChat.Interrupt()`.
- **Modal raise/update/dismiss + responses.** New `modal-*` message kinds multiplexed on the same message channel.
- **Pros.**
  - Smallest surface change; extends one existing well-understood adapter (`ChatClientOverTransport`).
  - One channel per session ⇒ simple lifetime, matches existing `IChatClient.Dispose()` semantics.
  - Reuses `ChatClientTransportListener` framing helpers unchanged.
- **Cons.**
  - Overloads `IChatClient` well beyond its `Microsoft.Extensions.AI` contract; the "chat client" adapter becomes a full session controller.
  - Tools/subagents/modals are conceptually orthogonal to the streaming request/response abstraction; hiding them behind `GetService`-style downcasts hurts clarity.
  - `ChatClientOverTransport` currently expects a single `process-streaming` request per lifetime; making it re-drivable for attach + multiple mid-life pushes complicates its state machine.

#### Option A.2 — Dedicated "remote-agent-session" RPC surface, separate from the per-run chat-client transport

- **Architecture.** Introduce a NEW transport verb — call it `attach-agent-session` — that opens a dedicated bidirectional message channel per remote session (client side: `RemoteAgentSessionClient`; server side: a listener that resolves the local `AgentChat` by session-id and pipes events out). This channel carries an event-stream union: `history-snapshot`, `history-append`, `streaming-update`, `streaming-complete`, `streaming-error`, `is-busy-changed`, `tools-snapshot`, `tools-changed`, `subagents-snapshot`, `subagents-changed`, `modal-raised`, `modal-updated`, `modal-dismissed`, plus client→server verbs `send-user-message`, `interrupt`, `open-subagent` (returns a child channel id), `modal-respond`. The proxy `AgentChat` is a first-class subclass / factory of `AgentChat` that consumes this event stream directly to mutate its `history`, `subAgentItems`, `toolRoots`; internally it still owns an `IChatClient`, but that client is a trivial local shim that just relays user messages onto the channel. Today's `ChatClientOverTransport` remains untouched and continues to serve remote *execution* (as it does for `TransportTrustedExecutor`).
- **Streaming updates & history.** History delivered as `history-snapshot` on attach then `history-append` deltas; live model streaming as `streaming-update` deltas.
- **Tool enumeration.** `tools-snapshot` on attach + `tools-changed` events; drives `Tools` / `ToolsChanged`.
- **Subagent enumeration / opening.** `subagents-snapshot` + `subagents-changed`; `open-subagent(id)` returns a new channel id, over which a child `RemoteAgentSessionClient` is opened (same protocol) ⇒ nested proxy `AgentChat`.
- **Remote interrupt.** `interrupt` verb on the session's own channel.
- **Modal raise/update/dismiss + responses.** First-class message kinds in the same event stream; the proxy translates them into the same modal-stack VM as local sessions raise.
- **Pros.**
  - Clean separation of concerns: session attach is not shoehorned into `IChatClient`.
  - Purpose-built for the full mirror (tools, subagents, modals, is-busy), so no downcast tricks; easy to evolve.
  - Leaves `ChatClientOverTransport` doing its one thing (per-run streaming), preserving today's `TransportTrustedExecutor` path unchanged.
  - Natural place to add auth/attach diagnostics and reconnection.
- **Cons.**
  - Larger new-code surface: a new protocol, a new server listener alongside `ChatClientTransportListener`, and a new proxy chat.
  - Two parallel transport-chat protocols (per-run streaming vs. session-attach) increases maintenance surface; risk of drift if history/streaming semantics diverge.
  - Nested child channels for subagents require a channel-multiplexing convention on `ITransport` (either one channel per subagent, or logical stream IDs inside one channel).

#### Option A.3 — Split: reuse data-store / persistence for history + tools + subagents; new thin control channel only for live streaming, interrupt, and modals

- **Architecture.** History, tool list, and subagent list are already persisted entity data (chat history collection, tool registrations, `agent-session` sub-entities). A connected client subscribes to those entity streams via the existing data-access layer (the dev-tunnel / entity subscription channel Phantom.Workspaces already uses to observe entities across profiles), and only opens a small, per-session "control" channel over `ITransport` for the truly-live things: `streaming-update` frames of an in-flight LLM run, `is-busy` transitions, `interrupt`, and `modal-*`. The proxy `AgentChat` observes entity-store changes for history/tools/subagents and observes the control channel for the live-only pieces.
- **Streaming updates & history.** History comes from entity-store subscription (`AgentChatHistoryCollection` is backed by `chatHistoryProvider` — see `historyService`). Live streaming stays on the control channel and merges into the same history collection.
- **Tool enumeration.** Comes from entity-store subscription (tool registrations / MCP tool entities); no dedicated transport verb.
- **Subagent enumeration / opening.** Comes from entity-store subscription (sub-`agent-session` entities via `parent-agent-session-ids`); "open subagent" is just recursively attaching the same proxy to the child session-id.
- **Remote interrupt.** Control-channel verb, as in A.2.
- **Modal raise/update/dismiss + responses.** Control-channel verbs (modals are ephemeral; not persisted).
- **Pros.**
  - Reuses a lot of existing plumbing; the "big" data (history, tools, subagents) rides free on the already-designed cross-profile entity-observation mechanism.
  - Minimal new transport protocol: only ephemeral live events on the control channel.
  - History and subagent trees are consistent-by-construction with local views because they're the same entity stream.
- **Cons.**
  - Requires that the entity store actually is reliably observable cross-profile with low enough latency for a chat UI; if any of tools/subagents/history are NOT already fully mirrored to the client's entity view, this option's assumptions break and the "simple" thin channel has to grow.
  - Splits the source of truth for a running session across two channels (entity store + control), complicating ordering (e.g. a `history-append` entity event vs. a control-channel `streaming-update` that hasn't been persisted yet).
  - Depends on components outside `Phantom.Workspaces.Transport\Chat\`; scope creep risk into the data-access layer.

### Option area B — Modal-above-input + notifications integration

Two options for WHERE the modal stack lives, both compatible with the notifications design (`ClearOnTabActivation = false` for `modal-pending`; OR-aggregated `HasUnread`).

#### Option B.1 — New per-session modal-stack VM owned by `AgentSessionWorkspaceTabViewModel`, rendered in a new view region above `AgentChatInputQueueControl`

- **Architecture.** Add `AgentSessionWorkspaceTabViewModel.Modals : ObservableCollection<AgentSessionModalViewModel>` (or `ModalStackViewModel`). The session's `AgentChat` gains a `RaiseModal(IModalContent) / UpdateModal / DismissModal` API that mutates this collection through the tab VM. A new templated control (e.g. `AgentSessionModalStackControl`) is placed in `AgentChatInputQueueControl.axaml` above the composer, bound to `Modals`. Input gating: `InputQueueViewModel` (or the tab VM's already-present `IsChatRunning`-analogue) gains an `IsBlockedByModal` computed from `Modals.Count > 0` that disables submit.
- **Notifications integration.** The same tab VM that owns `Modals` also owns the notifications wiring (it already raises the "chat became idle" notification from `OnAgentPropertyChanged`). When `Modals` transitions 0→N it raises a `modal-pending` notification (kind carrying `ClearOnTabActivation = false`); when it returns to 0 it clears that notification. The idle notification's clearing on tab activation is untouched.
- **Pros.**
  - Modals are a session-lifecycle concern, not a composer concern; owning them at the session-tab VM keeps them alive across composer rebuilds, alt-composer swaps (`InputQueueGroupViewModel`), and formatted-mode toggles.
  - Clean interaction with proxied remote sessions: the remote-side control channel just feeds `RaiseModal/UpdateModal/DismissModal` on the same API.
  - Cleanly extensible to future modal kinds (freeform text, multi-choice, tool-approval) — just new `IModalContent` implementations.
- **Cons.**
  - Adds a new view region + control to `AgentChatInputQueueControl.axaml` (touches XAML that's on a hot path).
  - Slightly more surface than reusing the slash-completions popup pattern.

#### Option B.2 — Extend the existing `QueueComposerViewModel` above-input popup pattern (`SlashCompletionsViewModel`) with a modal-stack sibling on the composer

- **Architecture.** Add `QueueComposerViewModel.Modals : ObservableCollection<AgentSessionModalViewModel>` alongside its existing `Completions : SlashCompletionsViewModel`. `QueueComposerControl.axaml` (which already hosts the slash-completions popup) grows a second above-composer region bound to `Modals`. The `AgentChat`'s `RaiseModal / DismissModal` API pushes into the composer's `Modals`. Input gating uses the composer's own submit-enabled logic.
- **Notifications integration.** `AgentSessionWorkspaceTabViewModel` observes the composer's `Modals` collection (or a `HasActiveModal` bool) and raises/clears the `modal-pending` notification exactly as in B.1.
- **Pros.**
  - Reuses an established above-composer popup precedent (`slash-completions-popup`) — minimal new XAML idioms.
  - Puts the input-gating logic next to the composer that already owns submit.
- **Cons.**
  - Modal state is now tied to a specific composer instance; `InputQueueViewModel` creates additional `QueueComposerViewModel`s (for non-default queues) and swaps them, so care is needed to keep the modal stack tied to the SESSION, not to a composer that can be destroyed/recreated (essentially forces most of B.1's session-VM ownership anyway, but expressed via the composer).
  - Mixes two very different concerns (slash-completions autocomplete popup vs. session-level modal information) in one composer VM.
  - Awkward for remote sessions: the remote push has to reach into whichever composer is currently attached, not the session VM.




## Chosen design

### Approach

**Protocol layer — Option A.2 (dedicated `attach-agent-session` RPC surface).**

The client's proxy `AgentChat` is backed by a new, purpose-built transport verb `attach-agent-session` that opens a bidirectional message channel per remote session:

- **Server side** — a new listener alongside (not replacing) `ChatClientTransportListener` — accepts `attach-agent-session` requests, resolves the local `AgentChat` by `agent-session-id`, and pipes events out.
- **Client side** — a new `RemoteAgentSessionClient` consumes the event stream and drives a first-class proxy subclass/variant of `AgentChat` that mutates its own `history` (`AgentChatHistoryCollection`), `subAgentItems`, `toolRoots`, and busy/interrupt state.

> Does this imply some sort of virtual function behavior in AgentChat? These instances must obviously be obtained via the IRunningAgentChat API.

- **Event-stream union (server→client):** `history-snapshot`, `history-append`, `streaming-update`, `streaming-complete`, `streaming-error`, `is-busy-changed`, `tools-snapshot`, `tools-changed`, `subagents-snapshot`, `subagents-changed`, `modal-raised`, `modal-updated`, `modal-dismissed`.
- **Verbs (client→server):** `send-user-message`, `interrupt`, `open-subagent(id)` (returns a child channel id — nested `RemoteAgentSessionClient` for each opened subagent), `modal-respond`.
- **`ChatClientOverTransport` is left untouched.** It continues to serve today's per-run remote *execution* path via `TransportTrustedExecutor`. The two protocols are used for different purposes: `ChatClientOverTransport` for a single LLM run's request/response streaming; `attach-agent-session` for whole-session mirroring (attach, history, tools, subagents, modals, interrupt).

**Modal-above-input layer — refined B.1 (modals owned by the agent editor view, not the tab).**

The editor view that hosts the chat output + input is `AgentChatEditorControl.axaml` (in `Phantom.Workspaces.Agent.Gui\Controls\`), whose data context is `AgentViewModel` (`Phantom.Workspaces.Agent.Gui\ViewModels\AgentViewModel.cs`). Inside `AgentChatEditorControl`, `AgentChatInputQueueControl` is `DockPanel.Dock="Bottom"` bound to `Agent.InputQueue`; `AgentChatOutputControl` fills the rest. The modal stack lives **on the editor VM (`AgentViewModel`), not on the tab VM**:

- `AgentViewModel` gains a `Modals : ReadOnlyObservableCollection<AgentSessionModalViewModel>` and a computed `HasModalsNeedingInput : bool` (true iff any modal in `Modals` is currently awaiting user response).
- The associated `AgentChat` gains a `RaiseModal(IModalContent) / UpdateModal / DismissModal` API on the session/agent-chat layer; the local wiring in `AgentViewModel` translates those calls into mutations of its own `Modals` collection. For proxied remote sessions, the same `RaiseModal/UpdateModal/DismissModal` API is driven by `modal-raised / modal-updated / modal-dismissed` frames on the `attach-agent-session` channel; user responses go back as `modal-respond`.
- A new `AgentSessionModalStackControl` region is placed **inside `AgentChatEditorControl.axaml`, above `AgentChatInputQueueControl`**, bound to `Modals`. Because every `AgentViewModel` (root or subagent) renders its own `AgentChatEditorControl` when it becomes the active editor, each editor gets its own modal region "above its own input" for free.
- **Input gating is per-editor.** `AgentViewModel` (and/or its `InputQueueViewModel`) exposes an `IsBlockedByModal` computed from `HasModalsNeedingInput`; the composer's submit path honours it. Blocking is confined to the editor whose modals are active — other editors, other tabs, and the rest of the app remain interactive.
- **Only a presence bool bubbles to the tab VM.** `AgentSessionWorkspaceTabViewModel.OnAgentPropertyChanged` already observes `AgentViewModel.PropertyChanged` for the idle notification; it additionally observes `HasModalsNeedingInput` on the tab's root `Agent`, and:
  - On transition `false → true`, calls `NotificationService.Notify(...)` with the new `modal-pending` kind (declares `ClearOnTabActivation = false`).
  - On transition `true → false`, clears that `modal-pending` notification (by kind, not by tab).
- **Subagent modal aggregation.** Because subagents are nested `AgentViewModel`s under the root `AgentViewModel` (see `AgentViewModel.subAgentViewModels : List<AgentViewModel>`, `AddSubAgentSlotEager` creating a child `AgentViewModel` per `IRunningSubAgent`), the root `AgentViewModel`'s `HasModalsNeedingInput` is an OR aggregate over its own modal stack AND every descendant `AgentViewModel`'s `HasModalsNeedingInput`. This makes the tab's `modal-pending` notification correctly reflect "somewhere under this tab, a modal wants input" even when the user is currently viewing the root editor and the modal is on a not-yet-opened subagent editor. Each subagent editor still owns and displays its own modal stack in its own editor view — the aggregation is purely for the tab-level presence signal.

### Rationale

**Why A.2 over A.1 and A.3.**

- A.1 overloads `IChatClient` well beyond its `Microsoft.Extensions.AI` contract and forces `ChatClientOverTransport`'s single-`process-streaming`-per-lifetime state machine into a full session controller. That is precisely the coupling `TransportTrustedExecutor` and today's per-run execution path rely on staying simple.
- A.3 makes the source of truth for a running session bimodal (entity store + control channel) and adds ordering hazards between persisted history events and unpersisted streaming deltas. It also assumes cross-profile entity observation is already reliable and low-latency enough for a chat UI — a load-bearing assumption we do not want to make in this design.
- A.2 gives us a single ordered event stream per session for the whole mirror (history + tools + subagents + modals + busy + interrupt), a clean place to add attach diagnostics / reconnection, and leaves the per-run streaming path (`ChatClientOverTransport` / `TransportTrustedExecutor`) untouched.

**Mitigating A.2's cons.**

- *Two parallel transport-chat protocols.* Accepted intentionally. The two protocols serve genuinely different jobs (per-run execution vs. session-attach mirroring) and were conflated only historically. Shared framing helpers (`ToJsonElement` / `FromJsonElement` and the message-channel primitives) are reused; only the *verbs* differ. Drift risk is contained by treating streaming semantics (`streaming-update` / `streaming-complete` / `streaming-error`) as a shared vocabulary defined once and referenced by both protocols.
- *New server listener alongside `ChatClientTransportListener`.* Modelled on the existing listener; adds one dispatcher method and one channel-lifetime hook. The design deliberately does not touch `ChatClientTransportListener` so existing remote-execution flows keep passing their tests unchanged.
- *Nested channels for subagents.* Handled by `open-subagent(id)` returning a **new channel id** on the same underlying `ITransport`, opened as an independent `RemoteAgentSessionClient` instance. This mirrors how subagents already work locally (each subagent is its own `AgentChat` with its own `AgentViewModel`), and confines multiplexing to a single "open a child channel" verb rather than inventing logical stream IDs inside one channel.

**Why editor-owned modals with a bubbled presence bool.**

- Modals are a property of the *editor experience* (an agent chat that has questions its user must answer to unblock its input). Both the render surface (the region above the input) and the input-gating logic already live inside `AgentViewModel` / `AgentChatEditorControl`, not the tab VM. Placing the modal stack there keeps the stack colocated with the input it gates and avoids reaching from the tab VM down into a composer/InputQueue instance that can be rebuilt/swapped (`InputQueueViewModel` creates alt composers).
- Subagents are already their own `AgentViewModel`s (recursive nesting under the root `AgentViewModel`). Giving each subagent its own modal stack is therefore essentially free: the same `AgentChatEditorControl` template shows its own modals when the user navigates into that subagent's editor. A remote subagent's `modal-raised` frame naturally lands on the child `RemoteAgentSessionClient` / child proxy `AgentChat` / child `AgentViewModel`, exactly matching the local case.
- Only one signal ever needs to reach the tab VM: "somewhere under this tab, a modal needs input." A single OR-aggregated bool (`HasModalsNeedingInput`) exposed on the root `AgentViewModel` is enough to drive the tab-level `modal-pending` notification via the existing `OnAgentPropertyChanged` observation channel. The tab VM never needs to know *which* modal or *which* editor — that concern stays where the modal is displayed.

### Modal ownership — two-scenario evaluation

Both scenarios were walked end-to-end under editor-ownership. Verdict: editor-ownership with a bubbled presence bool holds cleanly for both, with one adjustment (the presence bool must aggregate across the subagent tree, not be strictly per-editor).

**Scenario (a): local takeover of a remote session.**

The client is connected (via `attach-agent-session`) to a remote session and viewing it in a proxy `AgentViewModel`. The "take over this session locally?" prompt is a purely **client-side affordance** (it is offered by `OpenAgentSessionShortcutHandler` / the cross-profile prompt flow, is not raised by the remote `AgentChat`, and its confirm action mutates local state — permanently rewrites the owning-profile field, calls remote `Interrupt()`, and rebinds the tab to a fresh local `AgentChat`). Therefore the modal is raised **locally in the client's proxy `AgentViewModel`'s modal stack**, not transported from the remote. It shows in that editor's above-input region, gates that editor's input (so the user cannot half-continue chatting to the remote while the rebind is pending), and its presence flips `HasModalsNeedingInput` true on that `AgentViewModel`, bubbling to the tab as `modal-pending`. On confirm, the local-rebind flow runs; on any outcome (confirm, cancel, dismiss), the modal is removed, the presence bool goes false, and the tab notification clears. No wrinkle: editor-ownership handles this cleanly, and it correctly localises the modal to the client that offered it (a second client attached to the same remote session must not see this modal — it is not a remote event).

**Scenario (b): hypothetical rights/permission request, possibly with multiple concurrent subagents.**

- *Fully local agent tree.* When a (root or sub) agent needs to request rights, its `AgentChat.RaiseModal(...)` mutates its own `AgentViewModel.Modals`. The modal renders in that specific editor's above-input region, gates only that editor's input, and flips its own `HasModalsNeedingInput`. The root's OR-aggregated `HasModalsNeedingInput` goes true and the tab raises `modal-pending`.
- *Remote/proxied root agent.* The remote `AgentChat.RaiseModal(...)` on the owning-profile instance emits a `modal-raised` frame on the session's `attach-agent-session` channel. The client's `RemoteAgentSessionClient` translates that into `RaiseModal` on the proxy `AgentChat`, which mutates the proxy `AgentViewModel.Modals`. Rendering, input gating, and presence-bubble are identical to the local case. User grant/deny → `modal-respond` frame → server-side dispatch to the original `IModalContent` completion.
- *Remote subagent with a rights request.* The remote subagent's `AgentChat.RaiseModal(...)` emits `modal-raised` on **that subagent's own child channel** (opened via `open-subagent(id)`). The client's child `RemoteAgentSessionClient` populates the child proxy `AgentViewModel`'s `Modals`. The modal displays in the subagent's editor when the user navigates there; the input gate applies to that subagent's composer only.
- *Multiple simultaneous subagents each requesting.* Each subagent's request lands in **its own** `AgentViewModel.Modals`. The root editor's own input is unaffected unless the root has its own modal too; each subagent editor is independently gated. The tab-level `modal-pending` notification is the OR aggregate across the whole tree, so the tab exclamation stays lit while ANY subagent still has an unanswered modal, matching the requirement's "multiple simultaneous modals per session ⇒ input gated ⇒ tab exclamation set."

**Aggregation decision (recorded explicitly).** The `HasModalsNeedingInput` value that bubbles to the tab **aggregates across the whole subagent tree of that tab's root `AgentViewModel`** (OR of self + descendants). Individual editors' modal stacks and per-editor input gating remain strictly per-editor (not aggregated). Rationale: the tab exclamation must fire even when the user has not yet opened the subagent editor where the modal lives, so an unnoticed cross-tree modal cannot silently gate input the user will only discover on navigation.

**Existing user-approval mechanism (grounding for the hypothetical rights modal).** Today the codebase has *no* interactive user-approval / trust-prompt UI. Trust decisions for tool calls flow through the declarative `TrustProfile` model (`Phantom.Workspaces.Llm.Core\Trust\TrustProfile.cs`, `TrustToolCallAuthorizer.cs`, `TrustToolAuthorization.cs`, `AgentTrustProfileResolver.cs`) and are resolved without an interactive prompt. No `IUserConfirmation` / `IPromptService` / `RaiseModal` / `IModalContent` type exists yet (verified by grep). The modal-above-input mechanism introduced by this feature is therefore the first general-purpose interactive prompt surface; the "rights request" modal is a hypothetical future consumer of the same abstraction, not a rewrite of existing plumbing.

**Verdict.** Editor-owned modals (on `AgentViewModel`) + a single `HasModalsNeedingInput` presence bool bubbled to `AgentSessionWorkspaceTabViewModel` cleanly satisfy both scenarios and all requirement bullets in "Modal-above-input model" and "Notifications scheme." One explicit adjustment recorded above: the presence bool must be tree-aggregated at the root editor.

