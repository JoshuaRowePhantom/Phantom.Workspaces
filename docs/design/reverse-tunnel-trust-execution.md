# Reverse-tunnel trust execution

> **Status: design — for review/approval before implementation.**

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
| `WebRemoteChatClient` (on C) `POST /agent/respond` → S | `ReverseRemoteChatClient` (on S) enqueues a request for C and awaits its result |
| S runs the turn locally and returns `ChatResponse` | C runs the turn locally and returns `ChatResponse` |

The only structural addition is that, because S cannot dial C, S **enqueues** a reverse request and C
**drains the queue by long-polling**, executes, and **posts the result back**.

### Components

1. **`ReverseExecutionRegistry` (on S).** An in-memory map of `clientInstanceId → ConnectedInstance`.
   A `ConnectedInstance` holds a bounded queue of pending `ReverseExecutionRequest`s and a map of
   in-flight `correlationId → TaskCompletionSource<ReverseExecutionResult>`. Registration is
   ephemeral and lease-based (heartbeat/expiry); a dropped connection removes the instance and faults
   its pending requests.

2. **`ReverseTrustedExecutor` (on S), implements `ITrustedExecutor`.** Constructed per registered
   instance (or one selector-aware executor that consults the registry).
   - `CanExecute(targetClientInstance)` → true iff the registry currently has a live connection for
     that instance id.
   - `CreateAgentChatAsync(request)` → builds an `AgentChat` whose chat client is a
     `ReverseRemoteChatClient` bound to that instance + a fresh session id (mirrors how
     `RemoteTrustedExecutor` builds `WebRemoteChatClient` and overrides `AgentServices.ChatClientOverride`).

3. **`ReverseRemoteChatClient` (on S), implements `IChatClient`.** Instead of HTTP-POSTing to a
   remote endpoint, `GetResponseAsync` enqueues a `ReverseExecutionRequest` (agent definition JSON,
   session id, messages, options) on the target instance's queue, registers a `correlationId` TCS,
   and awaits the matching `ReverseExecutionResult` (the `ChatResponse`). Symmetric to
   `WebRemoteChatClient`; per-turn request/response first, streaming deferred (today's
   `WebRemoteChatClient` already wraps a single response as the streaming case).

4. **Reverse channel endpoints (on S).** New `POST/GET` routes under `/reverse/*` (auth: same
   `X-Tunnel-Authorization` tunnel token as the forward path):
   - `POST /reverse/register` → body `{ clientInstanceId, capabilities?, acceptedAgentDefinitions? }`;
     returns a `registrationId` + lease TTL. Creates/refreshes the `ConnectedInstance`.
   - `GET /reverse/poll?registrationId=…` → **long-poll**. Blocks up to N seconds; returns the next
     `ReverseExecutionRequest` (with `correlationId`) or `204 No Content` on timeout (C re-polls). One
     in-flight request per poll keeps ordering simple; C may run several pollers for concurrency.
   - `POST /reverse/result` → body `ReverseExecutionResult { correlationId, chatResponse? , error? }`;
     completes the server-side TCS.
   - `POST /reverse/heartbeat` (or fold into poll) → refreshes the lease.
   - `POST /reverse/deregister` → graceful teardown.

5. **`ReverseExecutionWorker` (on C).** A background loop started when C connects to S with reverse
   execution enabled. It `register`s, then repeatedly `poll`s; for each request it executes **locally**
   via the normal `AgentFactory` / `LocalTrustedExecutor` path (so C enforces *its own* trust profile
   and `TrustToolCallAuthorizer`), and `POST`s the `ReverseExecutionResult` back. On error it returns a
   structured error result; on disconnect it backs off and re-registers.

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
- **Identity.** The `clientInstanceId` C announces is the trust identity S matches against. (Open
  question below on how this id is established/verified.)
- **No ambient authority.** A reverse request carries only an agent definition + messages, exactly
  like `RemoteAgentRequest`; it cannot ask C to run arbitrary code outside C's tool/trust policy.

## DTOs (sketch)

```text
ReverseRegistrationRequest  { clientInstanceId: string, acceptedAgentDefinitionNames?: string[] }
ReverseRegistrationResult   { registrationId: string, leaseSeconds: int }

ReverseExecutionRequest     { correlationId: string, agentDefinitionJson: string,
                              agentSessionId?: string, messages: ChatMessage[], /* + options */ }
ReverseExecutionResult      { correlationId: string, chatResponse?: ChatResponse,
                              error?: { code: string, message: string } }
```

Serialized with the shared `WebDataAccessJsonSerialization.Options` (camel/null-ignore) like the rest
of the web transport.

## Lifecycle & failure handling

- **Lease/expiry:** each `ConnectedInstance` has a TTL refreshed by poll/heartbeat. On expiry, S
  removes it and faults any in-flight TCS with a "client disconnected" error so callers on S fail fast
  rather than hang.
- **Backpressure:** the per-instance queue is bounded; if full, S rejects new reverse requests
  (executor reports it cannot currently execute → selection fails as today).
- **At-most-once per correlationId:** results are matched by `correlationId`; duplicate/late results
  are ignored.
- **Reconnect:** C's worker re-registers on transient failures with backoff; a new `registrationId`
  supersedes the old.
- **Shutdown:** `deregister` on C exit; S also reaps on lease expiry.

## Testing strategy

- **Registry unit tests:** register/lease/expiry, enqueue/dequeue, correlation completion, faulting on
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
  and updates as connections come and go, leases refresh, and requests flow — using event-driven
  change notifications (no polling timers in the view-model; deterministic for tests).
- **Drill-in.** Selecting a connection shows detail: recent reverse executions (which agent, when,
  succeeded/failed) and last error, so a user can diagnose why a remote execution failed.
- **Actions.** Where meaningful: disconnect an outbound connection; deregister/evict an inbound
  client; toggle "accept reverse execution".
- **Security surfacing.** The window makes explicit *who can ask this machine to run agents* (inbound
  reverse registrations), since that is the security-sensitive direction.

The status data needs small surfaces on the runtime components:

- `ReverseExecutionRegistry` exposes a snapshot + change events of connected instances (id, registered
  time, lease, queue depth, in-flight).
- The `ReverseExecutionWorker` (and the forward `WebClientDataAccessLayer` / `WebRemoteChatClient`)
  expose their current connection state + counters.
- A `ConnectionStatusViewModel` aggregates both into the two lists for `ConnectionStatusWindow`
  (opened from the network icon), mirroring the master-detail style used elsewhere.

This connection-status GUI is part of this feature's scope and is covered by view-model tests (state
projection and live updates from registry/worker events).



1. **Transport:** long-poll (`GET /reverse/poll`, simplest, proposed) vs. SSE vs. WebSocket. Long-poll
   reuses plain HTTP + the existing tunnel/auth with no new infra. OK to proceed with long-poll?
2. **Client-instance identity:** how is C's `clientInstanceId` established and trusted on S — a value
   from C's `user-computer-profile`, a configured id, or derived from the tunnel identity? Should S
   verify it, or accept C's claim within an already-tunnel-authenticated channel?
3. **Streaming:** start request/response only (matching today's `WebRemoteChatClient`), add streaming
   later? Proposed: yes, defer streaming.
4. **Where the reverse worker lives:** background service in the `Phantom.Workspaces` app started when a
   `DevTunnelWeb`/web connection is configured *and* "accept reverse execution" is enabled. Confirm it
   should auto-start on connect.
5. **Registry scope:** in-memory per server process (proposed) — no persistence/multi-process
   coordination. Acceptable?
6. **Naming:** `ReverseTrustedExecutor` / `ReverseRemoteChatClient` / `ReverseExecutionRegistry` /
   `ReverseExecutionWorker` / `/reverse/*`. OK, or prefer different names (e.g. "callback",
   "inbound-execution")?

## Implementation plan (after approval)

1. DTOs + `ReverseExecutionRegistry` (with status snapshot + change events) (+ tests).
2. `/reverse/*` endpoints on `Phantom.Workspaces.Web.Server` (+ handler tests).
3. `ReverseRemoteChatClient` + `ReverseTrustedExecutor`; register with the server's
   `ITrustedExecutorSelector` (+ tests).
4. `ReverseExecutionWorker` on the client (with connection state + counters); `RemoteAccess`
   "accept reverse execution" opt-in setting + GUI.
5. **Connection status GUI:** `ConnectionStatusViewModel` + `ConnectionStatusWindow`, opened from a
   network icon in the main window's top-right, showing outbound ("where we are connected to") and
   inbound ("who is connected to us") with live updates (+ view-model tests).
6. In-process end-to-end test; docs update; wire auto-start on connect.
