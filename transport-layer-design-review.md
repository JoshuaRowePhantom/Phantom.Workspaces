# Transport Layer Design Review

Structured findings from analysis of `unified-transport-layer.md`, `trust-models.md`, Phase 3–4 wire protocol, and T1–T14 task definitions.

---

## Protocol

Issues in the wire protocol definition, frame types, descriptor schemas, and transport selection logic.

**[Protocol] #1 — `channel-open-error` frame type missing from wire protocol**
- **Location:** Phase 3 wire protocol table (lines 159–169), Phase 4 `TransportFrame.Types` (line 282), T14 `RelayErrorTests`
- **Gap:** T14 tests reference a `channel-open-error` frame type that does not exist in the wire protocol table or in `TransportFrame.Types`; no server-to-client error response for failed `channel-open` is defined anywhere in the protocol.
- **Severity:** Critical

**[Protocol] #2 — No default response when no `ITransportListener` handles a `channel-open`**
- **Location:** Phase 4 `HttpServerTransportFactory` read loop description; `ITransportRegistry` definition
- **Gap:** The read loop description says "iterate registered listeners until one returns non-null" but specifies no action if all return null — no error frame is sent back and the channel silently hangs open.
- **Severity:** Critical

**[Protocol] #3 — In-flight frames when a channel is closed mid-stream are not specified**
- **Location:** Phase 3 wire protocol; Phase 4 `ServerHttpTransport` read loop
- **Gap:** When a `channel-close` frame is received, the design says "complete the inbound reader" but does not say whether already-buffered channel messages are drained or discarded.
- **Severity:** Moderate

**[Protocol] #4 — No frame ordering or duplicate-delivery guarantee is stated**
- **Location:** Phase 3 wire protocol (line ~156)
- **Gap:** The design assumes ordering and at-most-once delivery from the underlying transport but never explicitly states this, leaving ambiguity about whether the channel abstraction must add sequence numbers if WebSocket fragmentation or HTTP/2 stream resets can reorder frames.
- **Severity:** Moderate

**[Protocol] #5 — Simultaneous `channel-close` from both sides (close race) is unspecified**
- **Location:** Phase 3 wire protocol table, "bidirectional" direction for `channel-close`
- **Gap:** If both peers send `channel-close` concurrently for the same channel-id, both will attempt to call `DisposeAsync` on the server-side object — the design does not require idempotent or once-only disposal.
- **Severity:** Moderate

**[Protocol] #6 — `$http` connection descriptor schema omits `dev-tunnel-token` field**
- **Location:** Connection descriptor table (lines 37–43) vs. `HttpTransport` implementation notes (line 307)
- **Gap:** The `$http` descriptor shape in the schema table is `{ "type": "http", "url": "..." }` with no `dev-tunnel-token` field, but the `HttpTransport` implementation section says "If the outer descriptor has `dev-tunnel-token`" — the field is used but not specified in the schema.
- **Severity:** Moderate

**[Protocol] #7 — `$local` descriptor uses an undocumented `target` field in all scenario examples**
- **Location:** Scenario 1 step 6 (line ~952), Scenario 4 step 6 (lines ~1516–1522), `$local` schema definition (line 39)
- **Gap:** The `$local` schema is defined as `{ "type": "local" }` with no other fields, yet every scenario embeds `{ "type": "local", "target": "workspace-gui-listener" }` — the `target` field has no definition in the descriptor table, the `LocalTransportFactory` description, or anywhere in Phase 4.
- **Severity:** Critical

**[Protocol] #8 — `$reverse-http` descriptor stored in entity omits the `target` field required by the schema**
- **Location:** Descriptor schema table (line 41) vs. `ReverseHttpClientTransportFactory` hub-urls writing (line 345)
- **Gap:** The `$reverse-http` schema requires a `target: $connection` field, but `ReverseHttpClientTransportFactory` writes `{ "type": "reverse-http", "hub-urls": [...], "entity-id": "..." }` — no `target` — into the `user-computer-profile` entity; Scenario 2 step 5 then re-dispatches with `"target": { "type": "local" }` that appears from nowhere.
- **Severity:** Moderate

**[Protocol] #9 — WebSocket vs HTTP/2 NDJSON selection criteria are undefined**
- **Location:** Phase 4 `HttpClientTransportFactory` / `HttpTransport` (line ~305)
- **Gap:** The design says WebSocket is "preferred" and HTTP/2 NDJSON is a "fallback" but never defines what triggers the fallback — whether it is based on server capability, proxy presence, explicit configuration, or a failed upgrade attempt.
- **Severity:** Moderate

**[Protocol] #10 — Relay amplification back-pressure risk not addressed**
- **Location:** Q3 answer (line 95), Phase 4 relay pump description
- **Gap:** Q3 accepts unbounded channels for point-to-point transports, but a relay pump on Machine A between B and C creates a two-hop unbounded buffer with no flow control; a slow Machine C can cause Machine A to buffer unboundedly.
- **Severity:** Moderate

**[Protocol] #11 — `lease` frame does not exist; "lease" terminology used in requirements but not in protocol**
- **Location:** Requirements section heading "leasing", Phase 3 wire protocol table
- **Gap:** The requirements describe a "leasing mechanism" but the protocol has no `lease` frame — only `keepalive`; if the intent was for the server to send a lease extension or for the client to explicitly request a lease, that mechanism is absent.
- **Severity:** Low

**[Protocol] #12 — Maximum frame/chunk size for binary stream data is unspecified**
- **Location:** Phase 3 wire protocol, stream binary framing description (line 157)
- **Gap:** The design specifies the 5-byte binary frame header format but sets no maximum payload size per frame, leaving implementers without guidance on how to chunk large PTY outputs.
- **Severity:** Low

---

## Auth

Issues with identity verification, authorization enforcement, and token lifecycle.

**[Auth] #1 — Machine B can register with any `entity-id` — the registration claim is not verified**
- **Location:** Phase 4 `ReverseHttpServerTransportFactory` (lines ~361–365); trust-models.md enforcement section (line ~495: "Client assertions are never trusted")
- **Gap:** When Machine B sends `channel-open { "type": "reverse-register", "entity-id": "X" }`, `ReverseHttpServerTransportFactory` stores the channel indexed by `entity-id` X without verifying that the devtunnel-authenticated connection actually belongs to the entity whose id is X — a compromised peer can hijack any entity's reverse channel.
- **Severity:** Critical

**[Auth] #2 — `TransportRelayListener` performs no authorization before relaying between B and C**
- **Location:** Phase 4 `TransportRelayListener` (lines ~382–391)
- **Gap:** The relay listener calls `ITransportFactoryRegistry.ConnectToAsync(target-descriptor)` as soon as a `channel-open { "type": "relay" }` arrives from Machine B with no check that Machine B's identity has any access right to Machine C; any registered peer can relay to any other registered peer.
- **Severity:** Critical

**[Auth] #3 — Trust model enforcement is bypassed entirely in relay scenarios**
- **Location:** trust-models.md enforcement section; Phase 4 relay design
- **Gap:** `ExecutionTargetResolver` validates `allowed-client-instances` on the initiating machine (A in the single-machine model), but when Machine B uses `HubRelayTransportFactory`, the trust profile validation — if any — runs on Machine B, not on hub A where the relay decision is made; the trust model document has no section addressing multi-hop relay authorization.
- **Severity:** Critical

**[Auth] #4 — Source of the devtunnel token for `HttpClientTransportFactory` is unspecified**
- **Location:** Q4 answer (line 97), Phase 4 `HttpClientTransportFactory` (lines ~304–310)
- **Gap:** The design states dev tunnel tokens are passed via `X-Tunnel-Authorization` but never specifies where the token comes from, how it is acquired, how it is refreshed when it expires, or whether it is stored in the connection descriptor or retrieved at connection time.
- **Severity:** Moderate

**[Auth] #5 — Plain HTTP connections (non-devtunnel) have no authentication mechanism defined**
- **Location:** Q4 answer ("Dev Tunnel only"), connection descriptor table
- **Gap:** Q4 answers "Dev Tunnel only" for auth but the `$http` descriptor supports any URL; the design gives no guidance on what authenticates plain intranet HTTP connections and whether they should be rejected.
- **Severity:** Moderate

---

## Lifecycle

Issues with connection management, startup races, reconnection, and disposal ordering.

**[Lifecycle] #1 — Hub crash while relay is active — error propagation to B and C is not specified**
- **Location:** Phase 4 relay pump description (lines ~386–392)
- **Gap:** The design says the relay pump dies when either side closes its channel, but does not describe what error Machine B receives when Machine A crashes mid-relay (connection drop vs. clean cancellation vs. silent hang).
- **Severity:** Moderate

**[Lifecycle] #2 — Machine C crash mid-turn — relay pump drain behavior and error propagation to B are unspecified**
- **Location:** Phase 4 relay pump description
- **Gap:** If Machine C's registration channel closes while the relay pump is mid-write, the design does not specify whether the pump exits cleanly, whether a `channel-close` or error frame is sent to Machine B, or whether the relay session `IAsyncDisposable` is disposed.
- **Severity:** Moderate

**[Lifecycle] #3 — Startup clear race window is acknowledged but not mitigated**
- **Location:** Phase 4 `ReverseHttpClientTransportFactory`, Startup section (line ~353)
- **Gap:** The design explicitly acknowledges "a brief window at startup where `hub-urls` is empty — callers during this window will receive a transport error" but defines no retry, back-off, or readiness-gate mechanism for callers.
- **Severity:** Moderate

**[Lifecycle] #4 — Reconnect/retry semantics are undefined for any transport**
- **Location:** Phase 4 `ReverseHttpClientTransportFactory` (reconnect path), `HttpTransport` (connection drop handling, line ~310)
- **Gap:** The design describes what happens when a connection drops (all channels faulted) but never defines whether any transport automatically reconnects, with what backoff, for how many attempts, or whether callers are expected to retry `ConnectToAsync` themselves.
- **Severity:** Moderate

**[Lifecycle] #5 — `ITransport`-to-physical-connection multiplicity is undefined for forward HTTP**
- **Location:** Phase 4 `HttpClientTransportFactory` / `HttpTransport`; Requirements §2 (lines 8–31)
- **Gap:** The design defines one physical reverse-registration connection per hub but never states whether multiple calls to `HttpClientTransportFactory.ConnectToAsync` with the same URL each open a new TCP/WebSocket connection or share one.
- **Severity:** Moderate

**[Lifecycle] #6 — `transport-close` vs. connection drop during in-flight `channel-open` race not handled**
- **Location:** Phase 4 `HttpServerTransportFactory` read loop, `ServerHttpTransport` lifecycle
- **Gap:** If the client sends `transport-close` while a `channel-open` response is still being processed by a listener, the server may complete the `channel-open` (returning an `IAsyncDisposable`) after it has already started disposal — no ordering guarantee or cancellation token chain is described.
- **Severity:** Moderate

---

## Error

Issues with missing error types, undefined failure propagation, and unspecified exception contracts.

**[Error] #1 — `ConnectToAsync` exception types are unspecified**
- **Location:** Phase 4 `ITransportFactory` interface definition (line ~236); `ITransportFactoryRegistry` (line ~258)
- **Gap:** The design never defines what exceptions `ConnectToAsync` can throw (network failure, auth failure, descriptor not handled, timeout), whether there is a typed `TransportException` hierarchy, or how callers should distinguish retriable from fatal errors.
- **Severity:** Moderate

**[Error] #2 — `ITransportFactoryRegistry.ConnectToAsync` behavior when no factory matches is undefined**
- **Location:** Phase 4 `ITransportFactoryRegistry` (line ~258)
- **Gap:** Individual `ITransportFactory.ConnectToAsync` returns null when it doesn't handle a descriptor, but the registry-level `ConnectToAsync` has no specified behavior (throw? return null?) when all registered factories return null.
- **Severity:** Moderate

**[Error] #3 — `channel-open-error` response frame is referenced in tests but absent from the protocol**
- **Location:** T14 `RelayErrorTests` (line 691), `TransportFrame.Types` (lines 283–291)
- **Gap:** Duplicates Protocol #1 — noted here as an error-handling gap; `channel-open-error` is tested but neither defined in the protocol nor implemented in any error propagation path.
- **Severity:** Critical

---

## Concurrency

Issues with missing thread-safety contracts, synchronization gaps, and potential deadlocks or races.

**[Concurrency] #1 — Thread-safety of `ITransportRegistry` and `ITransportFactoryRegistry` is unstated**
- **Location:** Phase 4 interface definitions (lines ~247–258)
- **Gap:** The design does not state whether `Register` calls are safe concurrent with dispatch calls, leaving implementers to guess whether a reader-writer lock or concurrent collection is required.
- **Severity:** Moderate

**[Concurrency] #2 — Thread-safety of `ReverseHttpServerTransportFactory` registration dictionary is unstated**
- **Location:** Phase 4 `ReverseHttpServerTransportFactory` (lines ~360–365)
- **Gap:** Multiple machines may send `reverse-register` channel-opens concurrently; the design does not specify what synchronization protects the entity-id→channel map.
- **Severity:** Moderate

**[Concurrency] #3 — Relay pump task ownership and thread affinity are unspecified**
- **Location:** Phase 4 `TransportRelayListener` (line ~387: "Starts a background relay pump")
- **Gap:** The design says one pump task per relay channel but does not specify what thread pool or scheduler the pump runs on, whether it is `Task.Run`, a dedicated `Channel` consumer, or a `ValueTask`-based loop.
- **Severity:** Low

**[Concurrency] #4 — `LocalTransport.ConnectToMessageChannelAsync` calling `OnChannelOpenAsync` synchronously can deadlock if the listener is slow**
- **Location:** Phase 4 `LocalTransport` (line ~297); Q8 answer (line 100)
- **Gap:** Q8 says all `LocalTransport` work runs on background threads, but the description says `ConnectToMessageChannelAsync` "calls `ITransportRegistry.OnChannelOpenAsync` on the local registry" without specifying whether this call is awaited synchronously on the caller's thread or dispatched to a background task.
- **Severity:** Moderate

**[Concurrency] #5 — `ChannelWriter` write after close in relay pump is not guarded**
- **Location:** Phase 4 relay pump description (lines ~387–390)
- **Gap:** The relay pump "reads frames from B's channel and writes to C's channel"; if C's channel is closed while a write is in progress, `ChannelWriter.TryWrite` returns false but the design specifies no error-propagation back to B at that point.
- **Severity:** Moderate

---

## Config

Issues with missing configuration contracts, role classification, and identity bootstrapping.

**[Config] #1 — Hub vs. non-hub machine classification mechanism is undefined**
- **Location:** Phase 4 `HubRelayTransportFactory` vs. `ReverseHttpServerTransportFactory` (lines ~397–409)
- **Gap:** The design says these factories are "mutually exclusive" across machine roles but never defines what configuration flag, entity property, environment variable, or startup parameter determines whether a given PW instance registers as a hub.
- **Severity:** Critical

**[Config] #2 — Local-machine identity detection mechanism is undefined**
- **Location:** Phase 4 `UserComputerProfileTransportFactory` (line ~428); Scenario 2 step 5 (line ~1134)
- **Gap:** `UserComputerProfileTransportFactory` must determine "is this entity-id the local machine?" but the design never specifies how the local machine's own entity-id is discovered or injected at startup.
- **Severity:** Critical

**[Config] #3 — Authorization to write `hub-urls` into a `user-computer-profile` entity is unspecified**
- **Location:** Phase 4 `ReverseHttpClientTransportFactory` hub-url lifecycle (lines ~343–357)
- **Gap:** `ReverseHttpClientTransportFactory` writes to the `user-computer-profile` entity on behalf of Machine C; the design does not state whether this requires a DAL permission, how concurrent writes from multiple processes are handled, or who owns that entity's write right.
- **Severity:** Moderate

**[Config] #4 — Devtunnel URL stability across PW restarts is not addressed for the hub-url store**
- **Location:** Phase 4 `ReverseHttpClientTransportFactory` reconnect path (line ~349: "replaces its existing slot in place")
- **Gap:** The design handles devtunnel URL rotation via "replace-in-place on reconnect" but does not address the window between Machine C's old devtunnel URL becoming invalid and the new registration completing, during which `hub-urls` may contain a stale URL that causes `HubRelayTransportFactory` to waste a parallel connection attempt.
- **Severity:** Low

**[Config] #5 — Machine B's `AgentDefinition` for Machine C is constructed by whom in Scenario 5**
- **Location:** Scenario 5 (not written up); T14 `Scenario5_HubRelayTests`
- **Gap:** Scenario 5 (B→A→C relay) is referenced in T14 but no detailed walkthrough is written — it is entirely absent from the Scenarios section; who constructs the `chat-client` channel-open payload on Machine B targeting Machine C via relay is unspecified.
- **Severity:** Moderate (blocks T14 implementation)

---

## Migration

Issues with rollout continuity, API compatibility, and removed endpoint handling.

**[Migration] #1 — No coexistence strategy for old and new endpoints during rollout**
- **Location:** Phase 2 O2 cons (line 133: "old and new code must coexist"); Q9 (line 101: "Same-version assumption")
- **Gap:** The design notes that old and new code must coexist but Q9 dismisses versioning/negotiation entirely — this means the moment a server is updated to expose `/transport/connect`, any client still calling `/reverse/connect` breaks, with no defined cutover plan.
- **Severity:** Critical

**[Migration] #2 — `ITrustedExecutor` disposition is ambiguous — "rebuilt on top of `ITransport`" but T11 says "remove"**
- **Location:** Requirements (line 81: "rebuilt on top of `ITransport`") vs. T11 (line ~666: "Remove old `ITrustedExecutor` layer")
- **Gap:** The requirements state `ITrustedExecutor` is "rebuilt" on `ITransport`, but T11 calls it removal — it is not clear whether a new `ITrustedExecutor` adapter remains in the public API (for backward compatibility with callers not yet migrated) or whether it is completely deleted.
- **Severity:** Moderate

**[Migration] #3 — External API clients of the removed `AgentRespondHandler` endpoint are unaddressed**
- **Location:** Removals table (line ~566: "`AgentRespondHandler.cs` → `ChatClientTransportListener` + new HTTP endpoint")
- **Gap:** The design removes `AgentRespondHandler` with no analysis of whether any external clients, scripts, or integrations call its old URL and what their migration path is.
- **Severity:** Low

---

## Plan

Issues with task coverage, naming inconsistencies, and missing scenario walkthroughs.

**[Plan] #1 — `AgentTrustProfileResolver` and `ExecutionTargetResolver` are designed but absent from T1–T14**
- **Location:** Phase 4 `AgentTrustProfileResolver` (line ~454), `ExecutionTargetResolver` (line ~444); T1–T14 task list
- **Gap:** Neither class appears as a deliverable in any T-task, meaning they are designed but will not be tracked or assigned.
- **Severity:** Moderate

**[Plan] #2 — `CopilotSubAgentRouterMiddleware` is designed but absent from T1–T14**
- **Location:** Phase 4 `CopilotSubAgentRouterMiddleware` (lines ~537–548); T10 description
- **Gap:** T10 covers `ChatClientTransportListener` and `ChatClientOverTransport` but does not mention implementing `CopilotSubAgentRouterMiddleware`, leaving a designed class with no assigned task.
- **Severity:** Moderate

**[Plan] #3 — `InMemoryTransport` (T1) and `InProcessTransportPair` (T6d) appear to overlap in purpose**
- **Location:** T1 (line ~582), T6d (line ~635)
- **Gap:** T1 defines `InMemoryTransport` as a "test double: linked pair of unbounded channels" while T6d defines `InProcessTransportPair` as "matched pair of in-memory `ITransport` instances" — the design does not explain the difference or why both are needed.
- **Severity:** Low

**[Plan] #4 — `channel-open-error` frame must be added to T1 but is not mentioned there**
- **Location:** T1 deliverables (lines ~577–582); T14 `RelayErrorTests` (line ~691)
- **Gap:** T14 depends on `channel-open-error` frame behavior but T1 does not list it as a deliverable, so T14 cannot be implemented without an untracked addition to T1.
- **Severity:** Moderate

**[Plan] #5 — Scenario 5 is the most complex scenario but has no written end-to-end walkthrough**
- **Location:** Scenarios section (lines ~840–1540, Scenarios 1–4); T14 which tests Scenario 5
- **Gap:** Scenarios 1–4 each have full `AgentChat.InitializeAsync` step-by-step walkthroughs, but Scenario 5 (Machine B routes through hub A to Machine C) is entirely absent from the Scenarios section, leaving implementers without a reference for what the call graph looks like end to end.
- **Severity:** Moderate

**[Plan] #6 — CRITICAL INTERNAL INCONSISTENCY — `tool-call` frames in the chat protocol contradict Phase 4 implementation**
- **Location:** Phase 3 chat-client wire protocol (lines ~176–205) vs. Phase 4 `ChatClientTransportListener` (lines ~485–501) and `ChatClientOverTransport` (lines ~504–522)
- **Gap:** Phase 3 defines `tool-call`, `tool-result`, and `tool-error` frames and states "for `gui-local` tools, the call is forwarded back to the client over the channel"; Phase 4 states "The `IChatClient` on Machine B uses MCP directly — **no `tool-call` frames traverse the chat channel**" — these are directly contradictory, and `ChatClientOverTransport` in Phase 4 still has code handling `tool-call` frames, making it unclear which mechanism is canonical.
- **Severity:** Critical

**[Plan] #7 — `execution-target` vs. `default-execution-target` field name inconsistency between companion documents**
- **Location:** trust-models.md (uses `default-execution-target` throughout); unified-transport-layer.md lines ~723, 736 (uses `execution-target`); Phase 4 `ExecutionTargetResolver` (line ~447: uses `default-execution-target`)
- **Gap:** The trust profile field is called `default-execution-target` in trust-models.md and in most of unified-transport-layer.md Phase 4, but the Trusted Executor scenarios section uses `execution-target` for the same field — a naming inconsistency that will cause bugs in the JSON schema and runtime deserialization.
- **Severity:** Moderate

---

## Tests

Gaps in test coverage relative to the T1–T14 plan.

**[Tests] #1 — No test for relay lease expiry while B and C are active**
- **Location:** T13 `LeaseExpiryTests.cs`; T14 relay tests
- **Gap:** `LeaseExpiryTests` covers the single-hop case, but there is no test for what happens when the Machine A hub's lease expires on the B-side transport while a relay to C is in progress (should the relay pump be cancelled, and should C receive a clean close?).
- **Severity:** Moderate

**[Tests] #2 — No integration test for `ShellTransportListener` / `ShellOverTransport`**
- **Location:** T8 (implements shell transport); T13, T14 (no shell scenario)
- **Gap:** T13 and T14 are entirely focused on message-channel (chat and MCP) scenarios; no test exercises the stream (PTY/shell) code path end-to-end.
- **Severity:** Moderate

**[Tests] #3 — No test for `channel-close` sent while messages are still in the channel buffer**
- **Location:** T13, T14
- **Gap:** No test verifies whether buffered messages before a `channel-close` are delivered or discarded, which is the open question in Protocol #3.
- **Severity:** Moderate

**[Tests] #4 — No test for `UserComputerProfileTransportFactory`**
- **Location:** T7 (implements it); T13, T14 (not referenced)
- **Gap:** T7 implements `UserComputerProfileTransportFactory` but no T-task defines a test covering local/remote/relay routing through this factory.
- **Severity:** Moderate

**[Tests] #5 — No test for concurrent `channel-open` frames on the same transport**
- **Location:** T13, T14
- **Gap:** No test verifies that multiple simultaneous `channel-open` frames are all dispatched correctly and that the channel-id dispatch table is correctly populated under concurrency.
- **Severity:** Low

**[Tests] #6 — No test for `AgentTrustProfileResolver` or `ExecutionTargetResolver`**
- **Location:** T1–T14 (neither class appears in any test task)
- **Gap:** Both classes implement security-critical logic (trust profile resolution, target validation) but have no test coverage in the plan.
- **Severity:** Moderate

**[Tests] #7 — `HubUrlFallbackTests` timeout value is unspecified**
- **Location:** T14 `HubUrlFallbackTests` (line 693: "all URLs fail → throws within bounded timeout")
- **Gap:** The test asserts a "bounded timeout" but no timeout value is defined anywhere in the design; without this, the test cannot be written and the production code has no deadline to enforce.
- **Severity:** Moderate

**[Tests] #8 — No test for double-dispose when both sides send `channel-close` simultaneously**
- **Location:** T13, T14; Protocol gap #5
- **Gap:** The concurrency issue of simultaneous `channel-close` frames (Protocol #5) has no corresponding test that would catch double-dispose or double-completion bugs.
- **Severity:** Low

**[Tests] #9 — `CopilotSubAgentRouterMiddleware` has no test task**
- **Location:** Phase 4 `CopilotSubAgentRouterMiddleware` (lines ~537–548); T13 Scenario 3 test
- **Gap:** T13 `Scenario3_RemoteCopilotSdkTests` exercises the full Scenario 3 flow but the description makes no mention of verifying sub-agent lifecycle events, which is the primary responsibility of `CopilotSubAgentRouterMiddleware`.
- **Severity:** Moderate
