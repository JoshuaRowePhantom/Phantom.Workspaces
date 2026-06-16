# Reverse-tunnel trust execution

> **Status: approved — ready to implement.** Decisions captured below (WebSocket transport,
> streaming, `user-computer-profile`-id identity, auto-start, in-memory registry).

## Problem & scenario

Phantom.Workspaces instances connect to one another over a **dev tunnel**. Today the relationship is
strictly one-directional at the HTTP layer:

- The **connecting instance** (call it **C**) is the HTTP *client*. It reaches a
  **connected-to instance** (call it **S**, the HTTP *server* running
  `Phantom.Workspaces.Web.Server`) through the tunnel relay URL, using
  `WebClientDataAccessLayer` (`/data/*`) and, for remote agent execution,
  `RemoteTrustedExecutor` → `WebRemoteChatClient` (`POST /agent/respond`). Tunnel auth is the
  `X-Tunnel-Authorization: tunnel <token>` header (`docs/design/devtunnels-web-access.md`).

So **S can never initiate** a call to **C** — there is no socket from S to C. But trust profiles are
symmetric: a profile resolved **on S** may require that an agent/tool run on a *specific client
instance* that happens to be **C** (the machine that connected in). For example, S hosts the
workspace data, but a tool must execute against files or a container that only exist on C.

**Goal:** allow **S to send trust-execution requests back to C** — the reverse direction of the
tunnel — so that `TrustedExecutorSelector` on S can select an executor that runs the agent on C,
reusing the existing `ITrustedExecutor` seam. Because only C can open connections, C must **poll**
(or otherwise receive notifications from) S to pick up reverse requests, execute them locally under
its own trust enforcement, and return the results.

## Terminology

- **Connecting instance (C):** opened the tunnel connection; HTTP client; the machine an agent may
  need to run on.
- **Connected-to instance (S):** runs the web server; HTTP server; wants to dispatch execution to C.
- **Client instance id:** the existing trust-profile identity for a machine (`TrustProfile`:
  `"."` = local, `"*"` = any; otherwise a stable per-instance id). C announces its id to S; S uses it
  as the `TargetClientInstance` it can satisfy via the reverse channel.

## Design overview

The reverse path **mirrors the existing forward path with the transport inverted**:

| Forward (today) | Reverse (this design) |
| --- | --- |
| `RemoteTrustedExecutor` (on C) selects remote execution | `ReverseTrustedExecutor` (on S) selects reverse execution |
| `WebRemoteChatClient` (on C) `POST /agent/respond` → S | `ReverseRemoteChatClient` (on S) sends an `execute` frame over C's WebSocket and consumes the streamed result |
| S runs the turn locally and returns `ChatResponse` | C runs the turn locally and **streams** `ChatResponseUpdate`s back |

The only structural addition is that, because S cannot dial C, **C opens a WebSocket to S**; over that
duplex connection S **pushes** reverse-execution requests and C streams **results** back. The
connection itself is the registration and the liveness signal.

### Transport: WebSocket (decided)

C opens a **WebSocket** to S at `GET /reverse/connect` (upgrade), authenticated with the same
`X-Tunnel-Authorization: tunnel <token>` header as the forward path. The connection is a single
duplex channel carrying a small framed message protocol (JSON frames, serialized with
`WebDataAccessJsonSerialization.Options`):

| Direction | Frame | Payload |
| --- | --- | --- |
| C → S (first frame) | `register` | `{ clientInstanceId /* a user-computer-profile entity id */, acceptedAgentDefinitionNames? }` |
| S → C | `execute` | `ReverseExecutionRequest { correlationId, agentDefinitionJson, agentSessionId?, messages, options }` |
| C → S | `update` | `ReverseExecutionUpdate { correlationId, chatResponseUpdate }` (streaming deltas) |
| C → S | `complete` | `ReverseExecutionResult { correlationId, error? }` (terminates a streamed turn) |
| C ↔ S | `cancel` | `{ correlationId }` (either side cancels an in-flight turn) |

Because the channel is duplex, **no long-poll or heartbeat is needed** — a closed/broken socket is the
disconnect signal. The registry entry lives for the lifetime of the socket. If the listener (S) fails,
C reconnects with backoff — which it must do anyway to keep its forward data-model connection alive.

### Components

1. **`ReverseExecutionRegistry` (on S).** An in-memory map of `clientInstanceId → ConnectedInstance`.
   A `ConnectedInstance` wraps the live WebSocket plus a map of in-flight
   `correlationId → streaming sink/`TaskCompletionSource``. When the socket closes, the entry is
   removed and all its in-flight turns are faulted. (No leases/heartbeats — the socket is the liveness
   signal.) Exposes a snapshot + change events for the connection-status GUI.

2. **`ReverseTrustedExecutor` (on S), implements `ITrustedExecutor`.** One selector-aware executor
   that consults the registry.
   - `CanExecute(targetClientInstance)` → true iff the registry has a live socket for that instance id.
   - `CreateAgentChatAsync(request)` → builds an `AgentChat` whose chat client is a
     `ReverseRemoteChatClient` bound to that instance + a fresh session id (mirrors how
     `RemoteTrustedExecutor` builds `WebRemoteChatClient` and overrides `AgentServices.ChatClientOverride`).

3. **`ReverseRemoteChatClient` (on S), implements `IChatClient`.** Instead of HTTP-POSTing to a remote
   endpoint, it sends an `execute` frame (with a fresh `correlationId`) over the target instance's
   socket and consumes the `update`/`complete` frames streamed back. `GetStreamingResponseAsync`
   yields each `ReverseExecutionUpdate.chatResponseUpdate`; `GetResponseAsync` aggregates them into a
   single `ChatResponse`. **Streaming is first-class** (decided).

4. **WebSocket endpoint (on S).** `GET /reverse/connect` (WebSocket upgrade), auth via the tunnel
   token. On `register`, S validates that `clientInstanceId` is a known `user-computer-profile` entity
   id (S **accepts C's claim** to that profile within the already-tunnel-authenticated channel —
   decided) and adds a `ConnectedInstance` to the registry. S then pushes `execute` frames and reads
   `update`/`complete`/`cancel` frames; closing the socket deregisters.

5. **`ReverseExecutionWorker` (on C).** A background service started when C has a web/dev-tunnel
   connection configured **and** "accept reverse execution" is enabled (auto-start on connect —
   decided). It opens the WebSocket, sends `register`, then for each `execute` frame runs the agent
   **locally** via the normal `AgentFactory` / `LocalTrustedExecutor` path (so C enforces *its own*
   trust profile and `TrustToolCallAuthorizer`), **streaming** `update` frames as the turn produces
   deltas and a final `complete`. On error it sends a `complete` with a structured error. On socket
   loss it reconnects with backoff and re-registers.

### Forward streaming parity (decided)

`WebRemoteChatClient` (the **forward** path) currently wraps a single `ChatResponse` as the streaming
case. As part of this work it is upgraded to **real streaming** so forward and reverse share the same
streamed `ChatResponseUpdate` contract (e.g. the forward `/agent/respond` gains a streaming variant,
or the agent endpoint streams NDJSON/SSE of `ChatResponseUpdate`). This keeps both directions
consistent and gives reverse execution a streaming server-to-agent path end-to-end.

### Wiring into the existing selector

On **S**, the `ITrustedExecutorSelector` is given a `ReverseTrustedExecutor` (registry-backed)
*alongside* `LocalTrustedExecutor`. The existing selection logic is unchanged:
`TrustedExecutorSelector.SelectExecutor(profile, target)` first checks
`profile.AllowsClientInstance(target)`, then returns the first executor whose `CanExecute(target)` is
true. The reverse executor answers `CanExecute` from the registry, so when S resolves a profile that
permits `target = C` and C is connected, S transparently runs the agent on C. No change to
`AgentFactory` or `TrustedExecutionRequest`.

## Trust & security

- **Two independent trust checks (defense in depth).**
  1. *On S (requester):* the resolved `TrustProfile` must permit `TargetClientInstance = C` (existing
     `AllowsClientInstance`, now wildcard-aware). S will not enqueue a reverse request otherwise.
  2. *On C (executor):* C runs the agent through its **own** local trust pipeline
     (`LocalTrustedExecutor` + `TrustToolCallAuthorizer`). C is authoritative over what runs on C.
     **C never blindly executes** what S sends; it re-resolves/enforces its local policy.
- **Opt-in.** Reverse execution is **off by default**. C must explicitly enable "accept reverse
  execution" (a `RemoteAccess` setting) before the worker registers. C may also restrict which agent
  definitions / tool schemas it will accept.
- **Transport auth.** Reuse the tunnel token (`X-Tunnel-Authorization`) on all `/reverse/*` calls; the
  reverse channel is only reachable by an instance already trusted to reach S.
- **Identity.** The `clientInstanceId` C announces in its `register` frame is a **`user-computer-profile`
  entity id**. S **accepts C's claim** to that profile within the already-tunnel-authenticated channel
  (decided), and matches it as the trust `TargetClientInstance`.
- **No ambient authority.** A reverse request carries only an agent definition + messages, exactly
  like `RemoteAgentRequest`; it cannot ask C to run arbitrary code outside C's tool/trust policy.

## DTOs (sketch)

```text
RegisterFrame               { clientInstanceId: string /* user-computer-profile entity id */,
                              acceptedAgentDefinitionNames?: string[] }

ReverseExecutionRequest     { correlationId: string, agentDefinitionJson: string,
                              agentSessionId?: string, messages: ChatMessage[], /* + options */ }
ReverseExecutionUpdate      { correlationId: string, chatResponseUpdate: ChatResponseUpdate }
ReverseExecutionResult      { correlationId: string, error?: { code: string, message: string } }
```

Serialized with the shared `WebDataAccessJsonSerialization.Options` (camel/null-ignore) like the rest
of the web transport.

## Lifecycle & failure handling

- **Liveness = the socket.** No leases/heartbeats. While C's WebSocket is open it is registered; when
  the socket closes (graceful or broken), S removes the `ConnectedInstance` and faults any in-flight
  turns with a "client disconnected" error so callers on S fail fast rather than hang.
- **Backpressure:** S may cap concurrent in-flight turns per instance; once at the cap the executor
  reports it cannot currently execute → selection fails as today.
- **Correlation:** `update`/`complete` frames are matched to their turn by `correlationId`; frames for
  an unknown/closed correlation are ignored. `cancel` (either direction) ends a turn.
- **Reconnect:** C's worker reconnects on socket loss with backoff and re-registers — the same retry
  it already needs for its forward data-model connection if S restarts.
- **Shutdown:** closing the socket on C exit deregisters; S reaps on socket close.

## Testing strategy

- **Registry unit tests:** register/replace/remove on connect/disconnect, correlation completion,
  faulting in-flight turns on
  disconnect, bounded-queue rejection.
- **`ReverseTrustedExecutor` tests:** `CanExecute` reflects registry membership; `CreateAgentChatAsync`
  produces a chat backed by `ReverseRemoteChatClient`.
- **`ReverseRemoteChatClient` tests:** enqueues a request and resolves on a matching result;
  times out / faults on disconnect.
- **In-process end-to-end (no real tunnel):** a `TestServer`/in-memory transport where a "C" worker
  drains the queue and executes against a deterministic test chat client, and an "S"-side
  `TrustedExecutorSelector` selects the reverse executor for a profile that permits the C instance;
  assert the agent ran on the C side and the `ChatResponse` flowed back. (Use the existing
  deterministic test chat client; no `Task.Delay`.)
- **Trust enforcement tests:** S refuses to enqueue when the profile disallows the target; C refuses
  to execute an agent/tool its local policy denies.

## Connection status GUI

Both directions of connectivity must be visible to the user through a dedicated **connection status
window**, opened from a **network icon in the top-right** of the main window (next to the existing
gear/settings icon). The icon doubles as an at-a-glance indicator (e.g. connected / connecting /
disconnected / error badge).

The window shows two lists:

1. **Outbound — "Where we are connected to"** (this instance acting as connecting instance **C**):
   - Each configured/active connection to a connected-to instance **S**: its endpoint / tunnel URL,
     display name, connection state (connecting / connected / retrying / failed), the data-access
     connection (`/data/*`) health, and — when reverse execution is enabled — the
     `ReverseExecutionWorker` registration state (registered, last poll, lease remaining) and how many
     reverse requests it has executed.
2. **Inbound — "Who is connected to us"** (this instance acting as connected-to instance **S**):
   - One row per live entry in the `ReverseExecutionRegistry`: the remote `clientInstanceId` /
     display name, when it registered, lease remaining, queue depth (pending reverse requests), and
     in-flight count. Also surfaces forward inbound activity (clients hitting `/data/*` and
     `/agent/respond`) where available.

Behavior:

- **Live updates.** The view-model observes the registry (inbound) and the worker/clients (outbound)
  and updates as connections come and go and requests flow — using event-driven change notifications
  (no polling timers in the view-model; deterministic for tests).
- **Drill-in.** Selecting a connection shows detail: recent reverse executions (which agent, when,
  succeeded/failed) and last error, so a user can diagnose why a remote execution failed.
- **Actions.** Where meaningful: disconnect an outbound connection; deregister/evict an inbound
  client; toggle "accept reverse execution".
- **Security surfacing.** The window makes explicit *who can ask this machine to run agents* (inbound
  reverse connections), since that is the security-sensitive direction.

The status data needs small surfaces on the runtime components:

- `ReverseExecutionRegistry` exposes a snapshot + change events of connected instances (id, connected
  time, in-flight count).
- The `ReverseExecutionWorker` (and the forward `WebClientDataAccessLayer` / `WebRemoteChatClient`)
  expose their current connection state + counters.
- A `ConnectionStatusViewModel` aggregates both into the two lists for `ConnectionStatusWindow`
  (opened from the network icon), mirroring the master-detail style used elsewhere.

This connection-status GUI is part of this feature's scope and is covered by view-model tests (state
projection and live updates from registry/worker events).

## Decisions (approved)

1. **Transport:** **WebSocket** (`GET /reverse/connect`, duplex, framed JSON), not long-poll.
2. **Client-instance identity:** C claims a **`user-computer-profile` entity id**; S accepts the claim
   within the tunnel-authenticated channel.
3. **Streaming:** **first-class** for reverse execution, and the forward `WebRemoteChatClient` is
   upgraded to real streaming for parity.
4. **Worker lifetime:** auto-starts on connect when a web/dev-tunnel connection is configured and
   "accept reverse execution" is enabled.
5. **Registry scope:** in-memory per server process; C reconnects with backoff if S restarts (it must
   reconnect for the forward data-model connection anyway).
6. **Naming:** `ReverseTrustedExecutor` / `ReverseRemoteChatClient` / `ReverseExecutionRegistry` /
   `ReverseExecutionWorker` / `/reverse/connect`. (Accepted.)

## Implementation plan (approved)

1. Frame DTOs + `ReverseExecutionRegistry` (socket-backed; status snapshot + change events) (+ tests).
2. Upgrade the **forward** path to streaming: `WebRemoteChatClient` real streaming + a streaming
   `/agent/respond` variant on `Phantom.Workspaces.Web.Server` (+ tests).
3. `GET /reverse/connect` WebSocket endpoint on `Phantom.Workspaces.Web.Server` with the framed
   protocol; validate the claimed `user-computer-profile` id (+ handler tests).
4. `ReverseRemoteChatClient` (streaming over the socket) + `ReverseTrustedExecutor`; register with the
   server's `ITrustedExecutorSelector` (+ tests).
5. `ReverseExecutionWorker` on the client (WebSocket connect/register, execute locally + stream back,
   reconnect with backoff, connection state + counters); `RemoteAccess` "accept reverse execution"
   opt-in setting + GUI.
6. **Connection status GUI:** `ConnectionStatusViewModel` + `ConnectionStatusWindow`, opened from a
   network icon in the main window's top-right, showing outbound ("where we are connected to") and
   inbound ("who is connected to us") with live updates (+ view-model tests).
7. In-process end-to-end test; docs update; wire auto-start on connect.
