# Design: Remote GitHub Copilot Chat Client with Local Router and Split Tool Execution

## Abstract

This design describes a new agent-session topology in which the **local instance owns the
router** (the agent loop / tool-calling middleware / history / steering) while the **inner
GitHub Copilot chat client — the `IChatClient` that speaks to the Copilot model / inference
backend — runs on a remote `user-computer-profile`**. Tool execution is **split by location**:
GUI tools (`workspace-gui`) and workspace-entity tools (`workspace-entity`) execute on the
initiating local machine because they touch local UI and local data-access, while all other
tools (filesystem, shell, web, MCP, function, …) execute on the remote profile because they
act on the remote machine's environment.

This is a distinct, lower-level topology from
[`remote-agent-sessions.md`](remote-agent-sessions.md). There, an entire `AgentChat`
(router *and* chat client *and* toolset) runs remotely and the local UI attaches via a proxy
`AgentChat` over transport — the local side is effectively a UI mirror. **Here**, the
`AgentChat` and its function-invoking pipeline live on the *local* machine; only the inner
`IChatClient` (and the Copilot SDK session it wraps) is remoted. The two designs are
complementary: `remote-agent-sessions.md` moves the *session* remote; this design moves only
the *chat client* remote.

---

## Motivation and Requirements

### Requirements

1. **Reuse the existing GitHub Copilot session code.** No parallel Copilot-session stack.
   The remote side must construct exactly the same `CopilotSdkChatClient`
   (`Phantom.Workspaces.Llm.Core\CopilotSdkChatClient.cs:32`) the local `AgentFactory` builds
   today for the `"github-copilot"` provider
   (`Phantom.Workspaces.Llm.Core\AgentFactory.cs:815-860`).
2. **Router stays LOCAL.** The tool-calling loop / message-history owner / steering owner —
   the `AgentChat` and its function-invoking chat-client pipeline
   (`Phantom.Workspaces.Llm.Core\AgentChat.cs:239-280`,
   `Phantom.Workspaces.Llm.Core\InternalCreateAgentChatRequest.cs:58`) — runs on the local
   instance.
3. **Only the inner GitHub chat client runs REMOTE.** The `IChatClient` that talks to the
   Copilot model (holds the remote machine's Copilot auth / working-directory / session
   context) executes on the remote user-computer-profile. Model completions and streaming
   updates are relayed to the local router over the transport channel.
4. **Tool execution is split by location.**
   - GUI tools (`workspace-gui`) and workspace-entity tools (`workspace-entity`) **execute
     locally**.
   - All other tools (`mcp`, `function`, `filesystem`, `web`, `web_search`, `web_request`,
     shell/PTY streams, …) **execute remotely** on the same user-computer-profile that hosts
     the chat client.
   The local router must dispatch each individual tool call to the correct side without any
   per-call configuration by the agent author.

### Non-goals

- **Not** the "attach to an already-running remote `AgentChat`" scenario — that is
  [`remote-agent-sessions.md`](remote-agent-sessions.md).
- **Not** moving the router remote. The router explicitly stays local so the local GUI can
  drive tool approvals, steering, history persistence, and slash-command dispatch without a
  round-trip.
- **Not** a change to trust-profile semantics. The design composes the existing
  `TrustProfile`, `ExecutionTargetResolver` and `ExecutorTopology` primitives; it does not
  introduce a new trust surface.
- **Not** a new wire protocol. It reuses the `chat-client` transport channel
  (`process-streaming` / `steering` / `interrupt` /
  `streaming-update` / `streaming-update-complete` / `streaming-error`) already spoken by
  `ChatClientOverTransport` and `ChatClientTransportSession`.

---

## Background: Current State

### What already exists

- **Client-side chat-client-over-transport proxy.**
  `ChatClientOverTransport`
  (`features\Phantom.Workspaces.Transport\Chat\ChatClientOverTransport.cs`) is a client-side
  `IChatClient` that speaks the `chat-client` channel: it forwards
  `GetStreamingResponseAsync` as `process-streaming` frames, forwards steering as `steering`,
  interrupts as `interrupt`, and reassembles `streaming-update` /
  `streaming-update-complete` / `streaming-error` frames.
- **A `TransportTrustedExecutor` that already builds a local `AgentChat` around a
  transport-backed chat client.**
  `TransportTrustedExecutor.CreateAgentChatAsync`
  (`features\Phantom.Workspaces.Llm.Core\Transport\TransportTrustedExecutor.cs:43-65`) resolves
  the target-client-instance to a connection descriptor via `ExecutionTargetResolver`
  (`features\Phantom.Workspaces.Llm.Core\Trust\ExecutionTargetResolver.cs:34-47`), opens a
  transport, wraps it in `ChatClientOverTransport`, sets it as
  `AgentServices.ChatClientOverride`, and calls `AgentFactory.CreateAgentChatAsync`. The
  resulting `AgentChat` — including its function-invoking pipeline and toolset construction
  — lives on the *local* machine. The chat-client channel-open descriptor carries the full
  agent-definition
  (`TransportTrustedExecutor.BuildChatClientRequest` at
  `TransportTrustedExecutor.cs:123-137`):
  `{"type":"chat-client","agent-definition":…,"agent-session-id":…}`.
- **Server-side listener is a partial stub.**
  `ChatClientTransportListener`
  (`features\Phantom.Workspaces.Transport\Chat\ChatClientTransportListener.cs:12-15`) accepts
  a pre-constructed `IChatClient` in its constructor and returns a
  `ChatClientTransportSession` that pumps frames against it. It does **not** read the
  `agent-definition` from the `chat-client` request and does **not** construct a
  `CopilotSdkChatClient` from it. `ChatClientTransportSession` drives
  `IChatClient.GetStreamingResponseAsync` on inbound `process-streaming` frames
  (`ChatClientTransportSession.cs:98-122`) and forwards steering to
  `IChatSteeringTarget` when the client exposes it
  (`ChatClientTransportSession.cs:76-96`). Per
  [`unified-transport-production-cutover.md`](unified-transport-production-cutover.md), this
  listener is not yet hosted in production.
- **Executor-target tagging (the "which machine does this tool run on" mechanism).**
  `ExecutorTarget` (`features\Phantom.Workspaces.Llm.Core\Transport\ExecutorTarget.cs`)
  defines three execution classes:
  - `AgentExecutor` — the executor instance `E` resolved from the agent's execution target
    (default for `mcp`, `function`, and every unknown/other kind).
  - `GuiLocal` — the GUI / initiating machine `G`; used for `workspace-gui` and
    `workspace-entity` tools.
  - `HostingInstance` — the PW instance `H` that owns the workspace agent session; used for
    `agent-session` / `workspace-agent-session` tools.

  The kind→target mapping is static and happens at tool construction:
  `ExecutorTargetResolver.ForKind`
  (`features\Phantom.Workspaces.Llm.Core\Transport\ExecutorTargetResolver.cs:32-51`). The
  target→client-instance mapping is a per-session `ExecutorTopology`
  (`features\Phantom.Workspaces.Llm.Core\Transport\ExecutorTopology.cs`). The
  target→transport routing is `ExecutorTargetRouter`
  (`features\Phantom.Workspaces.Llm.Core\Transport\ExecutorTargetRouter.cs`) — target →
  topology-resolved client-instance → `ExecutionTargetResolver`-produced connection
  descriptor → `ITransportFactoryRegistry`. In the default single-machine topology
  (`ExecutorTopology.SingleMachine`) all three targets resolve to `"."`, so nothing
  round-trips.
- **Trust-executor selection.**
  `DeferredTrustedExecutorSelector`
  (`features\Phantom.Workspaces\Trust\DeferredTrustedExecutorSelector.cs:15-57`) is the
  production `ITrustedExecutorSelector`: `LocalTrustedExecutor`
  (`features\Phantom.Workspaces.Llm.Core\Trust\LocalTrustedExecutor.cs:18-33`) serves `"."`,
  and the transport-backed executor supplied via `SetRemoteExecutor` serves everything else.
- **Copilot chat client is self-invoking.**
  `CopilotSdkChatClient` implements `ISelfInvokingToolChatClient`
  (`Phantom.Workspaces.Llm.Core\CopilotSdkChatClient.cs:32`) and drives its own tool loop
  inside the Copilot CLI session. `AgentFactory.WrapWithMiddleware`
  (`Phantom.Workspaces.Llm.Core\AgentFactory.cs:406-419`) documents the consequence: when the
  inner client is self-invoking, the framework's `FunctionInvokingChatClient` **is not
  wrapped around it** and `ChatOptions.Tools` is forwarded to the SDK verbatim
  (`CopilotSdkChatClient.cs:305-311, 362-368`). See
  [`copilot-sdk-tool-events.md`](copilot-sdk-tool-events.md) and
  [`github-copilot-provider-support.md`](github-copilot-provider-support.md).

### What is missing

1. A **server-side chat-client listener** that reads the `agent-definition` from the
   incoming `chat-client` request and constructs the correct `IChatClient` (in the target
   scenario, a `CopilotSdkChatClient`) on the remote side. Today
   `ChatClientTransportListener` takes a pre-built client — it has no factory path.
2. A **per-session `ExecutorTopology`** that names the remote profile as `E` for
   `AgentExecutor`-tagged tools while keeping `G` (and, for local-owned sessions, `H`)
   pointed at `"."`. Today `ExecutorTopology.SingleMachine` is used everywhere in production.
3. A **transport-backed filesystem toolset**. `CreateFilesystemToolsetAsync`
   (`Phantom.Workspaces.Llm.Core\ToolsetFactory.cs:223-238`) constructs a
   `FilesystemServiceContextProvider` that spawns a local subprocess via
   `StdioClientTransport`. There is no transport-backed variant that runs the filesystem
   MCP server on the remote profile. Shell / PTY is already remote-capable via
   `TransportTrustedExecutor.OpenStreamAsync`
   (`TransportTrustedExecutor.cs:68-76`) and `ShellOverTransport`, and MCP tool calls already
   route via `TransportTrustedExecutor.RunToolAsync`
   (`TransportTrustedExecutor.cs:79-97`).
4. **Configuration wiring** so a user can request the "local router + remote Copilot chat
   client" topology on a per-session basis — i.e. select a remote user-computer-profile as
   the Copilot-inference host without moving the session itself remote.

---

## Architecture: Chosen Design

### Topology summary

```
                +--------------------- LOCAL (initiating instance) ---------------------+
                |                                                                       |
                |   AgentChat  (owns history, steering, approvals, slash commands)      |
                |     |                                                                 |
                |     v                                                                 |
                |   [ Copilot-provider pipeline: chat-client = ChatClientOverTransport ]|
                |     |                                                                 |
                |     |  process-streaming / steering / interrupt                       |
                |     |  streaming-update / streaming-update-complete / streaming-error |
                |     v                                                                 |
                |   ExecutorTargetRouter -->  local tools:  workspace-gui,              |
                |                                            workspace-entity           |
                +---------|-----------------------|-------------------------------------+
                          |                       |
                          |                       |  MCP / shell / filesystem tools
                          |  chat-client channel  |  (routed via ExecutorTarget = AgentExecutor)
                          v                       v
                +--------------------- REMOTE (user-computer-profile) ------------------+
                |                                                                       |
                |   ChatClientTransportListener (agent-definition -> IChatClient)       |
                |     -> CopilotSdkChatClient  (real GitHub Copilot session)            |
                |                                                                       |
                |   McpTransportListener / ShellTransportListener                       |
                |     -> filesystem MCP server, shell PTY, web tools, mcp tools         |
                +-----------------------------------------------------------------------+
```

Every dashed piece already exists; the design adds (a) the "construct the Copilot client
from the agent-definition" path on the server side and (b) a per-session `ExecutorTopology`
that points `AgentExecutor` at the remote profile while `GuiLocal` stays `"."`.

### Local router assembly

The local `AgentChat` is constructed by `TransportTrustedExecutor.CreateAgentChatAsync`
(`TransportTrustedExecutor.cs:43-65`) exactly as today: the request's
`TargetClientInstance` is the *remote* user-computer-profile (not `"."`),
`ExecutionTargetResolver.ResolveDescriptor`
(`ExecutionTargetResolver.cs:34-47`) produces a `user-computer-profile` descriptor, the
transport is opened via `UserComputerProfileTransportFactory`
(`features\Phantom.Workspaces.Transport\`), and the resulting transport is wrapped in
`ChatClientOverTransport` and set as `AgentServices.ChatClientOverride`. That override is
threaded through to `AgentChat.CreateAsync` via `InternalCreateAgentChatRequest.ClientOverride`
(`AgentFactory.cs:640`, `AgentChat.cs:239-244`).

Two things follow from this:

- The `AgentChat`, its toolset construction, its `SlashCommandRegistry`, its history, its
  approval flow, and (crucially) its function-invoking wrapper live on the local machine.
- The inner `IChatClient` is a `ChatClientOverTransport`. From the local router's point of
  view, it is a normal streaming `IChatClient`; from the wire's point of view, every
  `GetStreamingResponseAsync` call becomes a `process-streaming` frame carrying the
  serialized chat history and `ChatOptions` (including `ChatOptions.Tools` when the
  underlying model consumes tool declarations directly — see below).

The router does **not** need to know the chat client is remote. `ChatClientOverTransport`
already exposes `IChatSteeringTarget` semantics via
`ChatClientTransportSession.InjectSteering`
(`ChatClientTransportSession.cs:76-96`), preserving the local steering-message /
tool-result-steering behavior.

### Remote chat-client hosting

The design completes the server-side listener so that a single `chat-client` channel-open
request builds the real Copilot client:

- Extend `ChatClientTransportListener.OnChannelOpenAsync`
  (`ChatClientTransportListener.cs:20-29`) to also handle requests where the `type` is
  `"chat-client"` **and no pre-built `IChatClient` was supplied to the listener**. In that
  path it reads `agent-definition` (already serialized on the wire by
  `TransportTrustedExecutor.BuildChatClientRequest` at
  `TransportTrustedExecutor.cs:123-137`) and calls the same
  `AgentFactory.CreateChatClientAsync` used for local sessions
  (`AgentFactory.cs:301-360`). The result is the identical
  `CopilotSdkChatClient` that a local `"github-copilot"` session would use
  (`AgentFactory.cs:815-860`) — requirement (1).
- Host the extended listener under the transport composition described in
  [`unified-transport-production-cutover.md`](unified-transport-production-cutover.md) (§ R5,
  GUI-side dispatcher hosting), so it is reachable from any peer that opens a
  `chat-client` channel over an inbound transport.
- The remote side must have the Copilot SDK / Copilot CLI installed and authenticated as
  *its* user. The chat client uses the remote machine's Copilot credentials; the local
  machine's Copilot auth is not consulted. This is the intended behavior — the whole point
  of remoting the chat client is to run the Copilot session where the developer's remote
  Copilot context lives.

### Tool routing (the split)

The local router's toolset is constructed exactly as today from the agent's
`AgentDefinition.Tools` via `ToolsetFactory.CreateDefaultToolsetFactory`
(`Phantom.Workspaces.Llm.Core\ToolsetFactory.cs:258-266`) plus the workspace-entity /
workspace-gui / agent-session factories. Each tool is tagged with an `ExecutorTarget` at
construction time via `ExecutorTargetResolver.ForKind`
(`ExecutorTargetResolver.cs:32-51`):

| Tool kind | Tag |
|---|---|
| `workspace-gui` | `GuiLocal` |
| `workspace-entity` | `GuiLocal` |
| `agent-session` / `workspace-agent-session` | `HostingInstance` |
| `mcp`, `function`, `filesystem`, `web`, `web_search`, `web_request`, everything else | `AgentExecutor` |

For a "local router + remote chat client" session the session's `ExecutorTopology`
(`ExecutorTopology.cs:10-44`) is configured as:

```
GuiLocalClientInstance         = "."                       // local machine
HostingInstanceClientInstance  = "."                       // owning session is local
AgentExecutorClientInstance    = <remote profile entity-id>
```

`ExecutorTargetRouter.ResolveDescriptor(target)`
(`ExecutorTargetRouter.cs:42-48`) then produces `{"type":"local"}` for
`GuiLocal` / `HostingInstance` and
`{"type":"user-computer-profile","entity-id":"…"}` for `AgentExecutor`. At tool-dispatch
time this is turned into an `ITrustedExecutor` via `DeferredTrustedExecutorSelector`
(`DeferredTrustedExecutorSelector.cs:34-57`) — `LocalTrustedExecutor` for `"."`, the
transport-backed executor for the remote profile. The result is exactly requirement (4):
`workspace-gui` and `workspace-entity` tools dispatch through
`LocalTrustedExecutor.RunToolAsync` (`LocalTrustedExecutor.cs`); everything else dispatches
through `TransportTrustedExecutor.RunToolAsync`
(`TransportTrustedExecutor.cs:79-97`) or, for streams, `OpenStreamAsync`
(`TransportTrustedExecutor.cs:68-76`).

Concretely:

- **`workspace-gui` / `workspace-entity` — LOCAL.** These tools require the local GUI's
  `IDataAccessLayer`, `WorkspaceEntitySession`, and — for shortcuts — the running Avalonia
  windowing. Their `GuiLocal` tag already routes them to `"."` under this design's topology.
  See [`workspace-entity-toolset-factory.md`](workspace-entity-toolset-factory.md).
- **Shell / PTY — REMOTE.** `AgentExecutor` → remote profile →
  `TransportTrustedExecutor.OpenStreamAsync` → `ShellOverTransport`. No change needed.
- **MCP / function tools — REMOTE.** `AgentExecutor` → remote profile →
  `TransportTrustedExecutor.RunToolAsync` → `McpClientOverTransport`. No change needed.
- **Filesystem — REMOTE, with new plumbing.** Today
  `FilesystemServiceContextProvider` (`ToolsetFactory.cs:223-238`) spawns the filesystem
  MCP server as a local subprocess via `StdioClientTransport`. The design introduces a
  transport-backed variant that, when `AgentExecutor` resolves to a remote client-instance,
  opens the filesystem MCP server on the remote side through the same `mcp` channel used by
  other MCP tools. Implementation-wise this is a `FilesystemServiceContextProvider`
  overload that takes an `ExecutorTargetRouter` (or the resolved descriptor) and uses
  `McpClientOverTransport` instead of `StdioClientTransport` when the target is non-local.
  The subprocess itself is then owned by the remote instance.

### Reconciling the Copilot SDK's own tool loop

`CopilotSdkChatClient` is `ISelfInvokingToolChatClient`
(`CopilotSdkChatClient.cs:32`). Two implications flow through the local router:

1. `AgentFactory.WrapWithMiddleware` (`AgentFactory.cs:406-419`) intentionally skips the
   framework `FunctionInvokingChatClient` wrap for self-invoking clients. That is unchanged
   here — the router *does not* try to run a second tool loop above a client that already
   runs its own. This is the same behavior as a fully-local Copilot session and is the
   correct behavior over transport too: the Copilot session's tool loop stays *inside* the
   remote `CopilotSdkChatClient`, and its emitted tool calls are surfaced through the
   `IChatClient` streaming contract exactly the way they are locally
   (`CopilotSdkChatClient.cs:525-555`).
2. `ChatOptions.Tools` (the workspace `AIFunction` declarations for `workspace-gui`,
   `workspace-entity`, `mcp`, `function`, …) is forwarded verbatim into the Copilot SDK
   session config (`CopilotSdkChatClient.cs:305-311`, `362-368`). Over transport, the
   `process-streaming` frame carries the declarations, the remote client passes them to the
   SDK, and the SDK's tool loop calls back into the framework's tool-invocation contract
   for those declarations. Because the router lives locally and the toolset was constructed
   locally with `ExecutorTarget` tags, the *invocation* of each declared tool executes on
   the tagged side via the router's dispatcher — the SDK does not care where the
   `AIFunction` body runs, only that it returns a `FunctionResultContent`.
3. Copilot's **built-in** tools (Copilot CLI's shell, file, git, … tools — see
   [`copilot-sdk-tool-events.md`](copilot-sdk-tool-events.md)) are executed inside the
   Copilot CLI process on the *remote* machine. This is intentional and consistent with
   requirement (4): "all other tools run remote". The remote Copilot session's built-in
   shell/file operations therefore act on the remote filesystem, which is the same
   filesystem the transport-backed `filesystem` and `shell` toolsets target. **Open
   question:** whether the built-in tool policy
   (`CopilotBuiltinToolPolicy`, `CopilotSdkChatClient.cs:373-397`) needs any per-topology
   default — see below.

### Single-turn sequence

```
1. User submits input on local GUI.
2. Local AgentChat pushes the message onto its history and calls
   IChatClient.GetStreamingResponseAsync on the inner ChatClientOverTransport.
3. ChatClientOverTransport sends process-streaming{messages, options.Tools} over the
   chat-client channel.
4. Remote ChatClientTransportSession receives the frame and calls
   GetStreamingResponseAsync on the CopilotSdkChatClient it constructed from the
   agent-definition at channel-open.
5. The Copilot SDK runs its own tool loop. Streaming updates and tool-call /
   tool-result content parts are streamed back as streaming-update frames.
6. Local ChatClientOverTransport reassembles updates and hands them to the router.
7. For each tool call:
      - workspace-gui / workspace-entity: ExecutorTargetRouter -> "." ->
        LocalTrustedExecutor.RunToolAsync (or the tool's direct AIFunction body).
      - mcp / function / filesystem / web / shell:
        ExecutorTargetRouter -> remote profile ->
        TransportTrustedExecutor.RunToolAsync / .OpenStreamAsync.
8. Tool result flows back through the SDK's tool loop over the same chat-client channel.
9. Router finalizes the turn, persists history locally, and updates the UI.
```

---

## Detailed Design: Component-Level Changes

### 1. Complete `ChatClientTransportListener` as a factory

`features\Phantom.Workspaces.Transport\Chat\ChatClientTransportListener.cs`

- Add a factory-mode constructor that takes a
  `Func<AgentDefinition, CancellationToken, Task<IChatClient>>` (or, more precisely, the
  full `AgentServices` needed by `AgentFactory.CreateChatClientAsync`).
- In `OnChannelOpenAsync`, when `type == "chat-client"` and the request carries
  `agent-definition`, deserialize the agent definition and call the factory to build the
  remote-side `IChatClient`, then hand it to a `ChatClientTransportSession` scoped to the
  channel lifetime. Dispose the built client when the channel closes (this session's
  channel-scoped disposal is already respected — see
  `ChatClientTransportSession.DisposeAsync` at `ChatClientTransportSession.cs:23-40`).
- Keep the existing "pre-built client" constructor for tests and for hosts that want to
  short-circuit the factory path.

### 2. Host the completed listener on remote user-computer-profiles

Per [`unified-transport-production-cutover.md`](unified-transport-production-cutover.md) § R5,
register the completed `ChatClientTransportListener` alongside `McpTransportListener` and
`ShellTransportListener` in every PW instance that can serve as an `AgentExecutor` for a
peer's session. This is the same `TransportRegistry` composition used for other channels;
no new registration mechanism is needed.

### 3. Session-scoped `ExecutorTopology`

Today `ExecutorTopology.SingleMachine` (`ExecutorTopology.cs:22`) is used implicitly
wherever an `ExecutorTargetRouter` is constructed. Introduce a per-session
`ExecutorTopology` selection:

- When the user opens an agent session and selects a **remote** Copilot-inference profile
  (a `user-computer-profile` entity that is not `"."`), construct
  `new ExecutorTopology { AgentExecutorClientInstance = <profile-entity-id> }` — leaving
  `GuiLocalClientInstance` and `HostingInstanceClientInstance` at `"."` (their default).
- Thread this topology through to whatever code composes the session's
  `ExecutorTargetRouter`. In the local-router / remote-chat-client topology, the router is
  local, so `ExecutorTargetRouter` is constructed in the local composition and its
  `ExecutorTopology` is a per-session value rather than the static `SingleMachine` default.

Existing callers (single-machine local sessions and existing `remote-agent-sessions.md`
proxy sessions) keep passing `ExecutorTopology.SingleMachine` and are unaffected.

### 4. `TransportTrustedExecutor.CreateAgentChatAsync` — Copilot-topology entry point

`features\Phantom.Workspaces.Llm.Core\Transport\TransportTrustedExecutor.cs:43-65`

Add a code path that, given a `TrustedExecutionRequest` in which the caller has requested
"local router + remote chat client":

1. Opens a transport to the *remote* profile (as today).
2. Wraps it in `ChatClientOverTransport` (as today).
3. Sets `AgentServices.ChatClientOverride = chatClient` (as today).
4. Additionally sets an `AgentServices` field / analog carrying the per-session
   `ExecutorTopology` so the toolset composition uses the split routing described above.
   (The exact shape — new property on `AgentServices` vs. a wrapper on
   `ToolsetFactory` — is an implementation choice; the important contract is that the
   *toolset construction path* produces `ExecutorTarget`-tagged tools dispatched through an
   `ExecutorTargetRouter` built with the session's topology.)
5. Calls `AgentFactory.CreateAgentChatAsync` (unchanged), which produces the local
   `AgentChat` with its function-invoking pipeline / history / slash commands.

Note that the returned `AgentChat` lives locally even though `TrustedExecutionRequest.TargetClientInstance`
is a remote profile — this is the whole point of the topology and is a deliberate departure
from the "target-client-instance == where the AgentChat runs" assumption in
`remote-agent-sessions.md`.

### 5. Transport-backed filesystem provider

`features\Phantom.Workspaces.Llm.Core\ToolsetFactory.cs:223-238` and
`FilesystemServiceContextProvider`

- Overload the provider to accept an `ExecutorTargetRouter` (or the resolved connection
  descriptor for `ExecutorTarget.AgentExecutor`). When the descriptor is non-local, open the
  filesystem MCP server on the remote profile via `McpClientOverTransport`; when local, keep
  the current `StdioClientTransport` behavior.
- The change is confined to `FilesystemServiceContextProvider`; `CreateFilesystemToolsetAsync`
  merely forwards the router.

### 6. Auth and credentials

- The **Copilot session** uses the *remote* machine's GitHub token / Copilot CLI login. The
  local machine's Copilot auth is not consulted for model completions. This is the whole
  point of the topology (the remote is where the developer's Copilot context lives). See
  `AgentFactory.CreateGitHubCopilotClient` (`AgentFactory.cs:815-860`) — resolution runs on
  the remote side.
- Transport-level auth (dev-tunnel `X-Tunnel-Authorization` etc.) is unchanged; it is
  supplied by `UserComputerProfileTransportFactory` per the profile's
  `connection-descriptor`. See
  [`user-computer-profile-connectivity.md`](user-computer-profile-connectivity.md) and
  [`reverse-tunnel-trust-execution.md`](reverse-tunnel-trust-execution.md).
- **Trust profile.** The local `TrustProfile.AllowsClientInstance(<remote-profile>)`
  (checked by `DeferredTrustedExecutorSelector.SelectExecutor`,
  `DeferredTrustedExecutorSelector.cs:39-43`) must be true or the router will refuse to
  dispatch `AgentExecutor`-tagged tools. This is the same trust gate that
  `remote-agent-sessions.md` relies on; no new trust surface is required.

### 7. Session persistence

The session's history persistence store is chosen by the *local* `AgentChat` composition
(local `AgentPersistenceStoreOverride` is set to `NullAgentPersistenceStore.Instance` by
`TransportTrustedExecutor.CreateAgentChatAsync` today at `TransportTrustedExecutor.cs:56`,
because `remote-agent-sessions.md`'s remote-owned sessions want persistence to live on the
owning side). For a **local-router** session this default is wrong: the router lives
locally, therefore history should be persisted locally. The design's
`TransportTrustedExecutor.CreateAgentChatAsync` Copilot-topology path
must **not** override the persistence store to null — it should either leave it unset (so
the caller's `AgentServices.AgentPersistenceStoreOverride` wins) or explicitly wire the
local persistence store.

---

## Open Questions

1. **Copilot built-in tools on the remote side.** The Copilot SDK's built-in tools (shell,
   file, git, …) act on the *remote* machine. Should the local router expose a
   `CopilotBuiltinToolPolicy` (`CopilotSdkChatClient.cs:373-397`) that mirrors the local
   trust profile's allow/deny for those built-ins, or is deferring entirely to the remote
   machine's own policy sufficient?
2. **Where does session history persist?** Local persistence store (per §7 above) is the
   working assumption, but sessions that intermittently roam between "local Copilot" and
   "remote Copilot chat client" topologies raise a re-attach question: is history a
   property of the session entity, of the router's instance, or of the chosen topology?
3. **Chat-client channel reconnect semantics.** `ChatClientOverTransport` handles a
   `streaming-error` frame from `ChatClientTransportSession.cs:120-121`, but the
   channel-level "remote profile went away mid-turn" case is not yet a first-class state on
   the local router. What is the correct UX — treat as an interrupt, reconnect and resume,
   or fail the turn?
4. **Whose Copilot auth is used when the remote is a shared machine?** The remote side
   uses the account that owns the Copilot CLI login on that machine. When two developers
   share a remote profile this is ambiguous. Do we require per-user auth on the remote, or
   accept "the remote's logged-in user is authoritative"?
5. **Interaction with `TrustProfile.HostingWorkspacesClientInstances` /
   `DefaultExecutionTarget`.** `ExecutionTargetResolver.Resolve(TrustProfile)`
   (`ExecutionTargetResolver.cs:14-23`) reads `DefaultExecutionTarget` — does the
   Copilot-topology entry point compose with, or override, that default? The proposed
   answer is "override for this session only; leave the profile default untouched", but
   this should be validated against the existing `llm-trust-profile.md` semantics.
6. **Steering targets across transport.** `IChatSteeringTarget`
   (`ChatClientTransportSession.cs:76-96`) is discovered on the client via
   `GetService(typeof(IChatSteeringTarget))`. Does `ChatClientOverTransport` need to
   advertise steering capability so the local router's `ToolResultSteeringMiddleware` still
   fires? (Likely yes, and the wire path already exists via the `steering` frame — the
   question is whether the client-side proxy advertises the capability service.)
7. **Filesystem provider descriptor plumbing.** How is the resolved
   `AgentExecutor` connection descriptor threaded into
   `FilesystemServiceContextProvider` construction inside
   `ToolsetFactory.CreateFilesystemToolsetAsync`? Options: (a) pass an
   `ExecutorTargetRouter` through `AgentServices`; (b) construct the provider with a
   pre-resolved descriptor at session start; (c) resolve lazily per tool call.

---

## Relationship to Other Designs

- **[`remote-agent-sessions.md`](remote-agent-sessions.md)** — moves the entire `AgentChat`
  remote and attaches locally via a proxy. This design is orthogonal and lower-level: the
  `AgentChat` stays local; only the inner Copilot chat client is remote. A future
  composition could combine both (a proxy `AgentChat` whose owning-profile-side router uses
  this topology to remote *its* Copilot client to a third profile) but such composition is
  out of scope.
- **[`unified-transport-layer.md`](unified-transport-layer.md)** — provides the
  `ITransport` / `IMessageChannel` / `ITransportFactoryRegistry` primitives and the
  `LocalTransport` / `HttpTransport` / `ReverseHttpTransport` factories. This design consumes
  them unchanged.
- **[`unified-transport-production-cutover.md`](unified-transport-production-cutover.md)** —
  hosts the transport composition in production and is the correct place to host the
  completed `ChatClientTransportListener` (§ R5).
- **[`reverse-tunnel-trust-execution.md`](reverse-tunnel-trust-execution.md)** — the reverse
  direction needed when the remote profile is a *connecting* instance (C) rather than a
  server-hosted instance (S). No new reverse plumbing is required beyond what already
  exists.
- **[`llm-trust-profile.md`](llm-trust-profile.md)** — governs which client-instances the
  local `AgentChat` may dispatch to. `DeferredTrustedExecutorSelector` enforces this before
  any tool call reaches `TransportTrustedExecutor`.
- **[`workspace-entity-toolset-factory.md`](workspace-entity-toolset-factory.md)** — the
  entity-tool concurrency semantics that make it essential for these tools to run against
  the *local* `IDataAccessLayer`.
- **[`github-copilot-provider-support.md`](github-copilot-provider-support.md),
  [`copilot-sdk-session-events.md`](copilot-sdk-session-events.md),
  [`copilot-sdk-tool-events.md`](copilot-sdk-tool-events.md)** — the Copilot SDK integration
  this design remotes wholesale.

---

## Testing Strategy

Tests follow the existing `Subject_Scenario_ExpectedOutcome` PascalCase convention
(cf. `Scenario3_RemoteCopilotSdkTests.Scenario3_FullTurn_ViaReverseHubRelay` in
`features\Phantom.Workspaces.Transport.Tests\Scenarios\Scenario3_RemoteCopilotSdkTests.cs`).

| Test | Scenario | Expected outcome |
|---|---|---|
| `ChatClientTransportListener_ChatClientChannelWithAgentDefinition_BuildsCopilotClientOnServer` | Client opens `chat-client` channel carrying an `agent-definition` whose provider is `github-copilot`. | Listener deserializes the definition and constructs a `CopilotSdkChatClient` via `AgentFactory.CreateChatClientAsync`; disposes it on channel close. |
| `TransportTrustedExecutor_LocalRouterRemoteChatClientTopology_ReturnsLocalAgentChatWithTransportChatClient` | `CreateAgentChatAsync` with a remote `TargetClientInstance` in Copilot topology. | Returned `AgentChat` runs on the local instance; its `IChatClient` is a `ChatClientOverTransport`; persistence store is not force-nulled. |
| `LocalRouterRemoteChatClient_ModelTurn_StreamsCompletionOverTransport` | Local router runs a turn; `process-streaming` is sent, remote SDK streams `streaming-update` frames. | Streaming updates delivered to the local router in order; `streaming-update-complete` finalizes the turn. |
| `LocalRouterRemoteChatClient_WorkspaceGuiToolCall_ExecutesLocally` | Model requests a `workspace-gui` tool. | `ExecutorTargetRouter` resolves `GuiLocal` to `"."`; `DeferredTrustedExecutorSelector` returns `LocalTrustedExecutor`; the transport is not used for the tool call. |
| `LocalRouterRemoteChatClient_WorkspaceEntityToolCall_ExecutesLocally` | Model requests a `workspace-entity` tool. | Dispatched via `LocalTrustedExecutor` against the local `IDataAccessLayer`; concurrency semantics from `workspace-entity-toolset-factory.md` are preserved. |
| `LocalRouterRemoteChatClient_ShellToolCall_ExecutesRemotelyOverTransport` | Model opens a shell stream. | `ExecutorTargetRouter` resolves `AgentExecutor` to the remote profile; `TransportTrustedExecutor.OpenStreamAsync` opens a `ShellOverTransport` on the remote side. |
| `LocalRouterRemoteChatClient_FilesystemToolCall_ExecutesRemotelyOverTransport` | Model calls a `filesystem` tool. | `FilesystemServiceContextProvider` uses `McpClientOverTransport` (not `StdioClientTransport`); the filesystem MCP subprocess runs on the remote profile. |
| `LocalRouterRemoteChatClient_McpToolCall_ExecutesRemotelyOverTransport` | Model calls a generic `mcp` tool. | `TransportTrustedExecutor.RunToolAsync` dispatches to remote MCP listener. |
| `LocalRouterRemoteChatClient_SteeringInjection_ForwardedToRemoteChatClient` | Router injects a steering message mid-turn. | Frame reaches remote `ChatClientTransportSession.InjectSteering`; forwarded to the Copilot SDK via `IChatSteeringTarget`. |
| `LocalRouterRemoteChatClient_Interrupt_CancelsRemoteTurn` | Local router interrupts. | `interrupt` frame cancels the remote `turnCts` (`ChatClientTransportSession.cs:62-68`); no orphaned SDK session remains. |
| `LocalRouterRemoteChatClient_TrustProfileDenies_RemoteProfileTargetsRefused` | Local `TrustProfile` does not allow the remote profile. | `DeferredTrustedExecutorSelector.SelectExecutor` throws before any transport is opened. |
| `LocalRouterRemoteChatClient_ExecutorTopology_SingleMachineDefaultUnaffected` | A locally-hosted session (unchanged topology) is created after the design lands. | Topology is `SingleMachine`; every tool routes to `"."`; behavior is byte-for-byte identical to today. |
