# Design: Unified Transport Production Cutover

> **Status:** Phase 5 — Implementation plan complete; ready for commit and bug filing.
> **Tracks:** steps 4–6 of #1044 ("Integrate unified-transport into production"). Steps 1–3 (transport primitives) are already implemented on branch `fix/1044-transport-integration`.

This document designs the **production cutover** for the unified-transport layer: migrating every production consumer off the old reverse/remote stack (`ReverseExecutionRegistry` / `TrustedExecutorComposition.CreateSelector(reverseExecutionRegistry)`) onto the new transport layer, building the missing production surfaces required to do so, and removing the old reverse/remote stack. It makes #982 (T11 removals + `TransportTrustedExecutor`), #984 (T13 scenarios 1–4), and #985 (T14 scenario 5 + error paths) implementable.

---

## Requirements

- **R1 — Land the already-written transport primitives.** The three commits on `fix/1044-transport-integration` (`15278e48` executor-side `ReverseExecutionDispatcher`; `b2396342` `ReverseHttpTransport` inbound `channel-message` demux + `channel-open-error` propagation; `4c0a74f6` reverse-HTTP `stream-data` multiplexing) must land on `features` as the prerequisite for the cutover. They build green and keep the fast suite at 4069/0.
- **R2 — Transport-backed connection-status surface.** Provide a transport-layer replacement for `ReverseExecutionRegistry.GetConnectedInstances()` / `ConnectionsChanged` so `ConnectionStatusViewModel` and `RemoteExecutionRegistry.SyncFrom(...)` can source inbound-connection state (a per-client `ClientInstanceId` / `ConnectedAt` / `InFlightCount` snapshot plus a change event) from `ReverseHttpServerTransportFactory` instead of `ReverseExecutionRegistry`.
- **R3 — Transport-backed `ITrustedExecutor` adapter.** Provide a `ITrustedExecutor` implementation (`CanExecute` / `CreateAgentChatAsync` / `OpenStreamAsync` / `RunToolAsync`) over `ITransportFactoryRegistry` + `ChatClientOverTransport` / `ShellOverTransport` / `McpClientOverTransport`, replacing `ReverseTrustedExecutor` (and hence `TrustedExecutorComposition.CreateSelector`) in `MainWindowViewModel`.
- **R4 — Web.Server `/reverse` relay hosting on the transport model.** Rewrite the Web.Server reverse-hub hosting from the `ReverseConnectionAcceptor` / `IReverseMessageChannel` / `ReverseFrame` model to the `reverse-register` / `reverse-http` hub-relay model built on `ReverseHttpServerTransportFactory` (hosted as a `TransportRegistry` listener) and the relay pump.
- **R5 — GUI-side client registration + dispatcher hosting.** Host `ReverseHttpClientTransportFactory` (client registration against the hub) plus `ReverseExecutionDispatcher` (servicing the registration channel and dispatching relayed `channel-open` / `stream-open` frames to local `ChatClientTransportListener` / `McpTransportListener` / `ShellTransportListener`) in the GUI worker, replacing the `ReverseExecutionClientHost` / `ReverseConnectionAcceptor` / `LocalReverseExecutionHandler` role.
- **R6 — Resolve and use `ITransportFactoryRegistry` in production.** Register and build `UserComputerProfileTransportFactory` / `LocalTransportFactory` (alongside `HttpClientTransportFactory` / `ReverseHttpForwardingTransportFactory`) and resolve the registry in production so `ITrustedExecutor`, chat clients, MCP clients, and shells are produced through the transport layer rather than through `CreateSelector(reverseExecutionRegistry)`.
- **R7 — Per-consumer migration in coupling order.** Migrate each production consumer off `ReverseExecutionRegistry`: `MainWindowViewModel` (`:59` field, `:124` `CreateSelector`), `ConnectionStatusViewModel`, `WorkspacesWebHost`, `RemoteExecutionRegistry` (`SyncFrom` path), and the Web.Server reverse/agent endpoints (`Program.cs`, `ReverseEndpointRouteBuilderExtensions.cs`, `AgentEndpointRouteBuilderExtensions.cs`, `AgentRespondHandler.cs`).
- **R8 — Remove the old reverse/remote stack.** Once no production reference to `ReverseExecutionRegistry` (or the `IReverseMessageChannel` / `ReverseFrame`-based types) remains, delete the old types, feeding #982's T11 Removals table.
- **R9 — Guard / integration tests.** Add the deferred guard tests: `TrustedExecutor_Production_UsesTransportFactoryRegistry`, `Production_NoReferenceTo_ReverseExecutionRegistry_Remains`, and `Web.Server_ReverseEndpoints_UseTransportLayer`.
- **R10 — Never leave production half-migrated.** Every commit must build, keep the fast suite green, and leave production coherent: new surfaces are built and wired behind the transport registry **before** any old references are removed; the physical old-stack deletion is last.

---

## Current state

**Old stack (in production today):**

| Concern | Production wiring | Type surface |
|---|---|---|
| Executor selection | `MainWindowViewModel.cs:124` → `TrustedExecutorComposition.CreateSelector(this.reverseExecutionRegistry)` returns `ITrustedExecutorSelector` | `TrustedExecutorComposition.CreateSelector(ReverseExecutionRegistry, ITrustedExecutor?)` composes `ReverseTrustedExecutor` + optional remote + `LocalTrustedExecutor` |
| Reverse registry lifetime | `MainWindowViewModel.cs:59` (`private readonly Llm.Trust.ReverseExecutionRegistry reverseExecutionRegistry = new();`); `Web.Server/Program.cs:15-16,40` | `ReverseExecutionRegistry`: `Register(IReverseConnection)`, `Unregister(IReverseConnection)`, `TryGetConnection(string, out IReverseConnection)`, `IsConnected(string)`, `IReadOnlyList<ConnectedInstanceStatus> GetConnectedInstances()`, `event EventHandler? ConnectionsChanged` |
| Connection info shape | `ConnectionStatusViewModel.cs:51,175-178` | `record ConnectedInstanceStatus(string ClientInstanceId, DateTimeOffset ConnectedAt, int InFlightCount, string? AnnouncedEndpoint = null)` |
| Remote snapshot sync | `RemoteExecutionRegistry.cs:35 SyncFrom(ReverseExecutionRegistry)`, `:121 GetConnectedInstances()` | `RemoteExecutionRegistry`: `SyncFrom(ReverseExecutionRegistry)`, `Register(...)`, `Unregister(...)`, `TryGetExecutor(...)`, `event ExecutorsChanged` |
| Web host bridge | `WorkspacesWebHost.cs:21,26,32,61,68` (`MapReverseEndpoints(this.reverseExecutionRegistry)`) | `WorkspacesWebHost(ReverseExecutionRegistry)`, property `ReverseExecutionRegistry` |
| Reverse endpoints | `ReverseEndpointRouteBuilderExtensions.MapReverseEndpoints(this IEndpointRouteBuilder, ReverseExecutionRegistry, Func<string,bool>?)` → `new ReverseConnectionAcceptor(registry, ...)`; `GET /reverse/connect` (WebSocket), `POST /reverse/connect-http` (NDJSON) | `ReverseConnectionAcceptor.AcceptAsync(IReverseMessageChannel, CancellationToken)` |
| Agent endpoints | `AgentEndpointRouteBuilderExtensions.cs:40-41` resolves `ReverseExecutionRegistry`; `AgentRespondHandler.RespondAsync(RemoteAgentRequest, ReverseExecutionRegistry?, CancellationToken)` → `new ReverseRemoteChatClient(...)` | `ReverseRemoteChatClient : IChatClient` |
| Executor-side reverse host | old client host on `IReverseMessageChannel`/`ReverseFrame` | `ReverseExecutionClientHost`, `LocalReverseExecutionHandler : IReverseExecutionHandler` |

**New stack (built + unit-tested, not yet used in production):**

| Component | State |
|---|---|
| `ITransport`, `IMessageChannel`, `TransportFrame`, `TransportException` (`Phantom.Workspaces.Transport`) | Built + tested |
| `ITransportFactory` (`Task<ITransport?> ConnectToAsync(JsonElement, CancellationToken)`), `ITransportFactoryRegistry` (`void Register(ITransportFactory)`, `Task<ITransport> ConnectToAsync(JsonElement, CancellationToken)`), `TransportFactoryRegistry` | Built + tested |
| `TransportRegistry : ITransportRegistry` (`Register(ITransportListener)`, `OnChannelOpenAsync`, `OnStreamOpenAsync`) + `ITransportListener` (`OnChannelOpenAsync(JsonElement, IMessageChannel, CancellationToken)`, `OnStreamOpenAsync(JsonElement, Stream, CancellationToken)`) | Built + tested |
| `LocalTransportFactory(TransportRegistry)`, `HttpClientTransportFactory`, `UserComputerProfileTransportFactory(IDataAccessLayer, WorkspaceEntitySession, ITransportFactoryRegistry)` | Built + tested; **not registered/built in production** |
| `ReverseHttpServerTransportFactory : ITransportListener` (`int RegistrationCount`, `bool IsRegistered(string entityId)`, registration channels by entity-id) | Built + tested; **no per-client status snapshot / change event** |
| `ReverseHttpForwardingTransportFactory : ITransportFactory` (hub-urls race, `channel-open-error` → `TransportException`) | Built + tested; registered inert in `Program.cs:20-23` |
| `ReverseHttpClientTransportFactory(hubUrl, entityId) : ITransportFactory` + `ReverseHttpClientTransportRegistry : ITransportFactoryRegistry` (`EnsureRegisteredAsync`, `ReconnectAsync`) | Built + tested; **not hosted in production** |
| `ReverseHttpTransport : ITransport` (inbound demux, stream-data mux) + `ReverseExecutionDispatcher(IMessageChannel, TransportRegistry)` + `DispatchedStream` | On `fix/1044-transport-integration` (R1) |
| `ChatClientOverTransport(ITransport, JsonElement) : IChatClient`, `McpClientOverTransport(ITransport, JsonElement)`, `ShellOverTransport(ITransport, JsonElement)` | Built + tested; **no production construction** |
| `ChatClientTransportListener(IChatClient)`, `McpTransportListener(Func<...>)`, `ShellTransportListener` — all `: ITransportListener` | Built + tested; **never hosted** |

> **Note on `ITransportFactoryRegistry.ConnectToAsync`:** the interface method is non-nullable (`Task<ITransport>`) and dispatches a `JsonElement` connection descriptor to the first registered `ITransportFactory` whose `ConnectToAsync` returns a non-null `ITransport`. Individual factories return `Task<ITransport?>` and return `null` when they do not recognise the descriptor `type`.

---

## Options

### Option A — Big-bang cutover in one commit

**Architecture:** Build all missing surfaces, rewire every consumer, and delete the old stack in a single commit.

**Pros:**
- No transitional dual-stack code.
- Shortest total diff.

**Cons:**
- Spans ~28 files and ~15 test files; cannot be kept building/green incrementally.
- Two prior fix agents explicitly rejected this as unsafe in one pass.
- Impossible to review or bisect; violates R10.

### Option B — Incremental cutover: build new surfaces behind the registry, migrate per consumer, remove old stack last (**chosen**)

**Architecture:** First land the transport primitives (R1). Then add each missing production surface (status surface, `ITrustedExecutor` adapter, Web.Server relay hosting, GUI client host, transport-registry resolution) as an independent, additive commit that leaves the old stack in place and green. Then migrate each production consumer off `ReverseExecutionRegistry` one coupling-cluster at a time. Finally, once no production reference remains, delete the old stack and add the guard tests.

**Pros:**
- Every commit builds and keeps the fast suite green (R10).
- Each commit maps to one reviewable sub-issue with explicit dependencies.
- New surfaces are exercised by their own unit tests before consumers switch to them.
- Directly feeds #982/#984/#985.

**Cons:**
- Transient dual-stack period where both `ReverseExecutionRegistry` and the transport surfaces exist. Mitigated: the old stack is untouched and inert-where-replaced until the final removal commit; the guard test `Production_NoReferenceTo_ReverseExecutionRegistry_Remains` enforces that the transient state is fully collapsed at the end.

### Option C — Adapter shim (`ReverseExecutionRegistry` façade over transport)

**Architecture:** Keep `ReverseExecutionRegistry`'s public surface but reimplement it internally on top of the transport layer, leaving consumers unchanged.

**Pros:**
- Minimal consumer churn.

**Cons:**
- Preserves the exact old surface (`IReverseConnection`, `ConnectedInstanceStatus`, `ReverseFrame`) the cutover exists to delete — directly contradicts R8 and #982's Removals table.
- The old surface (`IReverseConnection.ExecuteAsync/OpenStreamAsync/RunToolAsync`) does not map cleanly onto `ITransport` channel semantics; the shim would be as much work as the real adapters with none of the cleanup.

---

## Chosen design

**Approach:** Option B — Incremental cutover: build new surfaces behind the registry, migrate per consumer, remove old stack last.

**Rationale:** Option B is the only approach that satisfies R10 (never leave production half-migrated) while producing independently committable, buildable, test-passing increments — each of which becomes one sub-issue with explicit dependency links. It was the sequencing both prior fix agents recommended after determining the migration "cannot be landed as a green, coherent increment in a single pass." Option A's single-commit cons (unbuildable intermediate states, un-reviewable diff) are fatal. Option C's façade cons (retaining the very types #982 must delete) defeat the purpose of the cutover. Option B's only con — a transient dual-stack window — is mitigated by keeping the old stack inert-but-present until the final removal commit and by the guard test that enforces zero production references at the end.

---

## Detailed design

### Code organisation

New production code lives in the existing projects:

- **`Phantom.Workspaces.Transport`** (`ReverseHttp` namespace): a new `ReverseConnectionStatusRegistry` connection-status surface fed by `ReverseHttpServerTransportFactory`.
- **`Phantom.Workspaces.Llm.Core`** (`Transport` namespace): `TransportTrustedExecutor` — the transport-backed `ITrustedExecutor` adapter (aligned with #982's `Phantom.Workspaces.Llm.Core/Transport/TransportTrustedExecutor.cs`), plus the trust/target resolution helpers it needs.
- **`Phantom.Workspaces`** (`Services` / `Trust` / `ViewModels` namespaces): a GUI-side transport host (`WorkspacesTransportHost`) that owns the `ReverseHttpClientTransportFactory`, `ReverseExecutionDispatcher`, and the local `TransportRegistry` of chat/mcp/shell listeners; and the migrated view models.
- **`Phantom.Workspaces.Web.Server`**: a new `MapTransportReverseEndpoints` route builder and transport-based agent-respond path; migration of `Program.cs`.

Existing files modified: `MainWindowViewModel.cs`, `ConnectionStatusViewModel.cs`, `WorkspacesWebHost.cs`, `RemoteExecutionRegistry.cs`, `Web.Server/Program.cs`, `ReverseEndpointRouteBuilderExtensions.cs`, `AgentEndpointRouteBuilderExtensions.cs`, `AgentRespondHandler.cs`.

Deleted (final commit, feeding #982): `ReverseExecutionRegistry.cs`, `TrustedExecutorComposition.cs`, `ReverseTrustedExecutor.cs`, `ReverseRemoteChatClient.cs`, `ReverseConnectionAcceptor.cs`, `ReverseExecutionClientHost.cs`, `LocalReverseExecutionHandler.cs`, `IReverseMessageChannel.cs`, `ReverseFrame.cs`.

### Classes and interfaces

#### `ReverseConnectionStatusRegistry`

**Namespace:** `Phantom.Workspaces.Transport.ReverseHttp`
**Kind:** class
**Responsibility:** Track the set of currently-registered reverse-HTTP executor clients and raise a change event, providing the transport-layer replacement for `ReverseExecutionRegistry.GetConnectedInstances()` / `ConnectionsChanged`.

**Members:**
- `void OnRegistered(string clientInstanceId, DateTimeOffset connectedAt)` — record a new registration (called by `ReverseHttpServerTransportFactory` when a `reverse-register` channel opens).
- `void OnUnregistered(string clientInstanceId)` — remove a registration when its channel completes.
- `void OnInFlightChanged(string clientInstanceId, int inFlightCount)` — update the in-flight count for a client.
- `IReadOnlyList<ReverseConnectionStatus> GetConnectedInstances()` — snapshot of connected clients, ordered by `ConnectedAt`.
- `event EventHandler? ConnectionsChanged` — raised on any registration/unregistration/in-flight change.

#### `ReverseConnectionStatus`

**Namespace:** `Phantom.Workspaces.Transport.ReverseHttp`
**Kind:** non-positional record
**Responsibility:** Immutable per-client status shape (the transport-layer analogue of `ConnectedInstanceStatus`).

**Members:**
- `string ClientInstanceId { get; init; }`
- `DateTimeOffset ConnectedAt { get; init; }`
- `int InFlightCount { get; init; }`

#### `ReverseHttpServerTransportFactory` (modified)

**Namespace:** `Phantom.Workspaces.Transport.ReverseHttp`
**Change:** accept an optional `ReverseConnectionStatusRegistry` and call `OnRegistered` / `OnUnregistered` as registration channels open and complete, and `OnInFlightChanged` as relayed channels open/close against a registration. No change to `ITransportListener` semantics.

**Added members:**
- constructor overload taking `ReverseConnectionStatusRegistry? statusRegistry`.

#### `TransportTrustedExecutor`

**Namespace:** `Phantom.Workspaces.Llm.Core.Transport`
**Kind:** sealed class, implements `ITrustedExecutor`, `IAsyncDisposable`
**Responsibility:** Sole transport-backed `ITrustedExecutor`; produces agent chats / streams / tool runs by resolving a connection descriptor and connecting through `ITransportFactoryRegistry`, then wrapping the resulting `ITransport` in `ChatClientOverTransport` / `ShellOverTransport` / `McpClientOverTransport`.

**Members (matching the retained `ITrustedExecutor` interface):**
- `TransportTrustedExecutor(ITransportFactoryRegistry transportFactoryRegistry, ExecutionTargetResolver executionTargetResolver, AgentTrustProfileResolver trustProfileResolver)` — constructor (positional resolver types align with #982's proposed adapter).
- `bool CanExecute(string targetClientInstance)` — whether the resolver can build a descriptor for the target.
- `Task<AgentChat> CreateAgentChatAsync(TrustedExecutionRequest request, CancellationToken cancellationToken = default)` — resolve descriptor → `ConnectToAsync` → `ChatClientOverTransport` → build `AgentChat`.
- `Task<Stream> OpenStreamAsync(TrustedStreamRequest request, CancellationToken ct = default)` — resolve descriptor → `ConnectToAsync` → `ShellOverTransport.Stream`.
- `Task RunToolAsync(TrustedToolRequest request, CancellationToken cancellationToken = default)` — resolve descriptor → `ConnectToAsync` → `McpClientOverTransport` round-trip.
- `ValueTask DisposeAsync()`.

> `ExecutionTargetResolver` builds the `JsonElement` connection descriptor (`local` for the local machine, `user-computer-profile` / `reverse-http` for a remote target-client-instance) from a `TrustedExecutionRequest.TargetClientInstance`. Where a resolver type does not yet exist it is introduced here as a thin production helper; #982 owns its final DI shape.

#### `WorkspacesTransportHost`

**Namespace:** `Phantom.Workspaces.Services`
**Kind:** sealed class, implements `IAsyncDisposable`
**Responsibility:** GUI-side host that (a) builds the local `TransportRegistry` of `ChatClientTransportListener` / `McpTransportListener` / `ShellTransportListener`, (b) registers this machine with each configured hub via `ReverseHttpClientTransportFactory.EnsureRegisteredAsync`, and (c) hosts a `ReverseExecutionDispatcher` on the returned registration channel so relayed `channel-open` / `stream-open` frames reach the local listeners. Replaces `ReverseExecutionClientHost` / `ReverseConnectionAcceptor` / `LocalReverseExecutionHandler`.

**Members:**
- `WorkspacesTransportHost(TransportRegistry localListeners, IReadOnlyList<ReverseHttpClientTransportFactory> hubFactories)`.
- `Task StartAsync(CancellationToken cancellationToken = default)` — register with hubs and start dispatchers; reconnect via `ReconnectAsync` on channel loss.
- `event EventHandler? ConnectionStateChanged`.
- `ValueTask DisposeAsync()`.

#### `MapTransportReverseEndpoints` (new route builder)

**Namespace:** `Phantom.Workspaces.Web.Server`
**Kind:** static extension method on `IEndpointRouteBuilder`
**Responsibility:** Host the reverse hub on the transport model: map `reverse-register` (client registration) and `reverse-http` relay endpoints backed by a `ReverseHttpServerTransportFactory` hosted as a `TransportRegistry` listener, with a byte-transparent relay pump. Replaces `MapReverseEndpoints(ReverseExecutionRegistry, ...)`.

**Members:**
- `static IEndpointRouteBuilder MapTransportReverseEndpoints(this IEndpointRouteBuilder endpointRouteBuilder, ReverseHttpServerTransportFactory serverTransportFactory, ReverseConnectionStatusRegistry statusRegistry)`.

### Data flow

**Inbound (executor hosting) — GUI as executor behind a hub:**
1. `WorkspacesTransportHost.StartAsync` calls `ReverseHttpClientTransportFactory.EnsureRegisteredAsync`, opening a registration channel to the hub (`reverse-register`).
2. `ReverseExecutionDispatcher(registrationChannel, localListeners)` reads relayed frames: `channel-open` → `TransportRegistry.OnChannelOpenAsync` → the matching `ChatClientTransportListener` / `McpTransportListener`; `stream-open` → `ShellTransportListener` via a `DispatchedStream`.
3. Inbound `channel-message` / `stream-data` frames are demuxed by `channelId` / `streamId` and delivered to the accepted channel; the listener's outbound writes are multiplexed back over the single registration channel.

**Outbound (executor selection) — GUI initiating execution:**
1. `MainWindowViewModel` resolves `ITrustedExecutor` = `TransportTrustedExecutor` (via DI) instead of `CreateSelector(reverseExecutionRegistry)`.
2. `CreateAgentChatAsync` → `ExecutionTargetResolver` builds a descriptor → `ITransportFactoryRegistry.ConnectToAsync(descriptor)`:
   - local target → `LocalTransportFactory` (in-process, to the local `TransportRegistry`);
   - remote target → `UserComputerProfileTransportFactory` → `ReverseHttpForwardingTransportFactory` (relay via hub) or `HttpClientTransportFactory` (direct).
3. The returned `ITransport` is wrapped by `ChatClientOverTransport` / `ShellOverTransport` / `McpClientOverTransport`.

**Connection status:** `ReverseHttpServerTransportFactory` feeds `ReverseConnectionStatusRegistry` as registrations open/close; `ConnectionStatusViewModel` and `RemoteExecutionRegistry.SyncFrom` subscribe to `ConnectionsChanged` and read `GetConnectedInstances()`.

**Relay (Web.Server hub):** `MapTransportReverseEndpoints` accepts `reverse-register` (stores the registration channel in `ReverseHttpServerTransportFactory`) and `reverse-http` relay requests (byte-transparent relay pump between a forwarding client and a registered executor's registration channel; unknown entity-id → `channel-open-error {"error-code":"not-registered"}`).

### Tests

Test names follow the codebase `Subject_Scenario_ExpectedOutcome` convention.

#### `ReverseConnectionStatusRegistryTests`
- `OnRegistered_NewClient_AppearsInSnapshotOrderedByConnectedAt`
- `OnUnregistered_KnownClient_RemovedFromSnapshot`
- `OnInFlightChanged_KnownClient_UpdatesInFlightCount`
- `AnyChange_RaisesConnectionsChanged`

#### `ReverseHttpServerTransportFactoryTests` (additions)
- `Registration_WithStatusRegistry_RecordsConnectedInstance`
- `RegistrationChannelCompletes_WithStatusRegistry_RemovesConnectedInstance`

#### `TransportTrustedExecutorTests`
- `CanExecute_ResolvableTarget_ReturnsTrue`
- `CreateAgentChat_LocalProfile_UsesLocalTransport`
- `CreateAgentChat_RemoteProfile_UsesTransportFactoryRegistry`
- `OpenStream_ShellTarget_ReturnsShellOverTransportStream`
- `RunTool_McpTarget_RoundTripsViaMcpClientOverTransport`

#### `WorkspacesTransportHostTests`
- `StartAsync_RegistersWithConfiguredHubs`
- `RelayedChannelOpen_DispatchesToLocalChatListener`
- `RelayedStreamOpen_DispatchesToLocalShellListener`
- `RegistrationChannelLost_ReconnectsViaReconnectAsync`

#### `TransportReverseEndpointsTests` (Web.Server)
- `Register_KnownClientInstance_StoresRegistrationChannel`
- `Relay_RegisteredTarget_PumpIsByteTransparent`
- `Relay_UnknownEntityId_SendsChannelOpenErrorNotRegistered`
- `Web.Server_ReverseEndpoints_UseTransportLayer` — endpoints resolve the transport layer, not `ReverseExecutionRegistry`.

#### `ConnectionStatusViewModelTests` (migrated)
- `Inbound_SourcedFromReverseConnectionStatusRegistry_ReflectsSnapshot`
- `ConnectionsChanged_RefreshesInboundCollection`

#### `MainWindowIntegrationTests` (additions)
- `TrustedExecutor_Production_UsesTransportFactoryRegistry` — the production executor is produced via `ITransportFactoryRegistry`, not `CreateSelector(reverseExecutionRegistry)`.

#### `ArchitectureRegressionTests`
- `Production_NoReferenceTo_ReverseExecutionRegistry_Remains` — no production (non-test) source references `ReverseExecutionRegistry` after migration.

---

## Implementation plan

Each commit builds, keeps the fast suite green (run via `.\scripts\run-tests.ps1`), and becomes one sub-issue. Commits 1–6 are additive (old stack untouched); 7–10 migrate consumers; 11 removes the old stack.

### Commit 1 — Land the already-written transport primitives

**Scope:** Fast-forward/cherry the three `fix/1044-transport-integration` commits (`15278e48`, `b2396342`, `4c0a74f6`) onto `features`: executor-side `ReverseExecutionDispatcher` + `DispatchedStream`; `ReverseHttpTransport` inbound `channel-message` demux + `MultiplexingChannelWriter`; `channel-open-error` → `TransportException` propagation; reverse-HTTP `stream-data` multiplexing. No redesign — these are existing, reviewed primitives.
**Files:** `Phantom.Workspaces.Transport/ReverseHttp/ReverseExecutionDispatcher.cs`, `DispatchedStream.cs`, `MultiplexingChannelWriter.cs`, `ReverseHttpTransport.cs`, `ReverseHttpForwardingTransportFactory.cs`, `ReverseHttpServerTransportFactory.cs` (+ existing transport tests).
**Tests:** `ReverseHttpTransportTests` (round-trip demux, per-channel routing, channel-close, ack/error/close handshake, `ReverseHttpTransport_RelayedStream_RoundTripsDataFrames`, `ReverseHttpTransport_StreamClose_CompletesOriginatingStream`); `ReverseExecutionDispatcherTests` (chat/mcp/shell dispatch, no-listener error, `ExecutorDispatcher_RelayedStream_RoundTripsDataFrames`); updated `ReverseHttpForwardingTransportFactoryTests` / `ReverseHttpServerTransportFactoryTests`. Fast suite 4069/0.
**Dependencies:** none.

### Commit 2 — Transport-backed connection-status surface

**Scope:** Add `ReverseConnectionStatusRegistry` + `ReverseConnectionStatus`; feed them from `ReverseHttpServerTransportFactory` (new optional constructor param; `OnRegistered` / `OnUnregistered` / `OnInFlightChanged`). Additive — no consumer switched yet.
**Files:** new `Phantom.Workspaces.Transport/ReverseHttp/ReverseConnectionStatusRegistry.cs`, `ReverseConnectionStatus.cs`; modify `ReverseHttpServerTransportFactory.cs`.
**Tests:** `ReverseConnectionStatusRegistryTests`; `ReverseHttpServerTransportFactoryTests` additions (`Registration_WithStatusRegistry_RecordsConnectedInstance`, `RegistrationChannelCompletes_WithStatusRegistry_RemovesConnectedInstance`).
**Dependencies:** depends on commit 1.

### Commit 3 — `TransportTrustedExecutor` adapter + target/trust resolution

**Scope:** Add `TransportTrustedExecutor : ITrustedExecutor, IAsyncDisposable` over `ITransportFactoryRegistry`, plus the `ExecutionTargetResolver` descriptor-building helper. Not yet wired into `MainWindowViewModel`. Feeds #982's T11 `TransportTrustedExecutor`.
**Files:** new `Phantom.Workspaces.Llm.Core/Transport/TransportTrustedExecutor.cs`, `ExecutionTargetResolver.cs`.
**Tests:** `TransportTrustedExecutorTests` (`CanExecute_ResolvableTarget_ReturnsTrue`, `CreateAgentChat_LocalProfile_UsesLocalTransport`, `CreateAgentChat_RemoteProfile_UsesTransportFactoryRegistry`, `OpenStream_ShellTarget_ReturnsShellOverTransportStream`, `RunTool_McpTarget_RoundTripsViaMcpClientOverTransport`).
**Dependencies:** depends on commit 1.

### Commit 4 — Web.Server transport reverse-relay hosting

**Scope:** Add `MapTransportReverseEndpoints` mapping `reverse-register` + `reverse-http` relay endpoints backed by a `ReverseHttpServerTransportFactory` (hosted as a `TransportRegistry` listener) with a byte-transparent relay pump and `channel-open-error {"not-registered"}` handling. Registered alongside the existing `/reverse` endpoints (not yet replacing them).
**Files:** new `Phantom.Workspaces.Web.Server/TransportReverseEndpointRouteBuilderExtensions.cs`; wire optional registration in `Program.cs` (additive).
**Tests:** `TransportReverseEndpointsTests` (`Register_KnownClientInstance_StoresRegistrationChannel`, `Relay_RegisteredTarget_PumpIsByteTransparent`, `Relay_UnknownEntityId_SendsChannelOpenErrorNotRegistered`).
**Dependencies:** depends on commit 1.

### Commit 5 — GUI-side client registration + dispatcher hosting

**Scope:** Add `WorkspacesTransportHost` owning the local `TransportRegistry` (chat/mcp/shell listeners), `ReverseHttpClientTransportFactory` registration against configured hubs, and `ReverseExecutionDispatcher` hosting on each registration channel, with reconnect. Not yet wired into `MainWindowViewModel` startup.
**Files:** new `Phantom.Workspaces/Services/WorkspacesTransportHost.cs`.
**Tests:** `WorkspacesTransportHostTests` (`StartAsync_RegistersWithConfiguredHubs`, `RelayedChannelOpen_DispatchesToLocalChatListener`, `RelayedStreamOpen_DispatchesToLocalShellListener`, `RegistrationChannelLost_ReconnectsViaReconnectAsync`).
**Dependencies:** depends on commit 1 and commit 4.

### Commit 6 — Resolve and build `ITransportFactoryRegistry` in production

**Scope:** Register + build `UserComputerProfileTransportFactory` and `LocalTransportFactory` (alongside `HttpClientTransportFactory` / `ReverseHttpForwardingTransportFactory`) in the production DI/composition, and expose the resolved `ITransportFactoryRegistry` + `TransportTrustedExecutor` + `WorkspacesTransportHost` + `ReverseConnectionStatusRegistry` for consumers to resolve. No consumer behaviour switched yet (bindings added, old stack still wired).
**Files:** `Phantom.Workspaces.Web.Server/Program.cs`, GUI composition/DI (e.g. `Phantom.Workspaces/Services` startup wiring).
**Tests:** DI-resolution smoke test that `ITransportFactoryRegistry` resolves and builds `UserComputerProfileTransportFactory` / `LocalTransportFactory`.
**Dependencies:** depends on commits 2, 3, 4, 5.

### Commit 7 — Migrate `MainWindowViewModel` executor selection

**Scope:** Replace `TrustedExecutorComposition.CreateSelector(this.reverseExecutionRegistry)` (`:124`) with the resolved `TransportTrustedExecutor`; remove the `reverseExecutionRegistry` field (`:59`) and start `WorkspacesTransportHost` at GUI startup.
**Files:** `Phantom.Workspaces/ViewModels/MainWindowViewModel.cs`.
**Tests:** `MainWindowIntegrationTests.TrustedExecutor_Production_UsesTransportFactoryRegistry`; existing MainWindow integration suite stays green.
**Dependencies:** depends on commits 3, 6.

### Commit 8 — Migrate `ConnectionStatusViewModel`

**Scope:** Source inbound connection state from `ReverseConnectionStatusRegistry` (`GetConnectedInstances()` + `ConnectionsChanged`) instead of `ReverseExecutionRegistry`.
**Files:** `Phantom.Workspaces/ViewModels/ConnectionStatusViewModel.cs`.
**Tests:** `ConnectionStatusViewModelTests` migrated (`Inbound_SourcedFromReverseConnectionStatusRegistry_ReflectsSnapshot`, `ConnectionsChanged_RefreshesInboundCollection`).
**Dependencies:** depends on commits 2, 6.

### Commit 9 — Migrate `WorkspacesWebHost` + `RemoteExecutionRegistry`

**Scope:** Remove the `ReverseExecutionRegistry` bridge from `WorkspacesWebHost` (host `MapTransportReverseEndpoints` instead of `MapReverseEndpoints`); replace `RemoteExecutionRegistry.SyncFrom(ReverseExecutionRegistry)` snapshot path with a `ReverseConnectionStatusRegistry`-sourced sync.
**Files:** `Phantom.Workspaces/Services/WorkspacesWebHost.cs`, `Phantom.Workspaces/Trust/RemoteExecutionRegistry.cs`.
**Tests:** updated `RemoteExecutionRegistryTests` (sync from status registry); `WorkspacesWebHost` startup test.
**Dependencies:** depends on commits 2, 4, 6.

### Commit 10 — Migrate Web.Server reverse/agent endpoints

**Scope:** Switch `Program.cs` to `MapTransportReverseEndpoints` and drop the `ReverseExecutionRegistry` singleton; migrate `ReverseEndpointRouteBuilderExtensions` (remove `ReverseConnectionAcceptor` path), `AgentEndpointRouteBuilderExtensions` (`:40-41` resolve transport instead of `ReverseExecutionRegistry`), and `AgentRespondHandler.RespondAsync` (use `ChatClientTransportListener` / transport instead of `new ReverseRemoteChatClient(...)`).
**Files:** `Phantom.Workspaces.Web.Server/Program.cs`, `ReverseEndpointRouteBuilderExtensions.cs`, `AgentEndpointRouteBuilderExtensions.cs`, `AgentRespondHandler.cs`.
**Tests:** `TransportReverseEndpointsTests.Web.Server_ReverseEndpoints_UseTransportLayer`; migrated `AgentRespondHandlerTests`, `ReverseEndpointRouteBuilderExtensionsTests`.
**Dependencies:** depends on commits 4, 6.

### Commit 11 — Remove the old reverse/remote stack + guard tests

**Scope:** With no production reference remaining, delete `ReverseExecutionRegistry.cs`, `TrustedExecutorComposition.cs`, `ReverseTrustedExecutor.cs`, `ReverseRemoteChatClient.cs`, `ReverseConnectionAcceptor.cs`, `ReverseExecutionClientHost.cs`, `LocalReverseExecutionHandler.cs`, `IReverseMessageChannel.cs`, `ReverseFrame.cs` (retaining `ITrustedExecutor` + `LocalTrustedExecutor`). Add the guard test. Feeds #982's T11 Removals table.
**Files:** delete the listed `Phantom.Workspaces.Llm.Core/Trust/*` files; new `ArchitectureRegressionTests.Production_NoReferenceTo_ReverseExecutionRegistry_Remains`.
**Tests:** `ArchitectureRegressionTests.Production_NoReferenceTo_ReverseExecutionRegistry_Remains`; full fast suite green after removal.
**Dependencies:** depends on commits 7, 8, 9, 10.

### Dependency graph

```
1 ── 2 ─┬─────────────── 8 ─┐
   ├ 3 ─┼──── 7 ────────────┤
   └ 4 ─┼─ 5 ─┐             ├─ 11
        ├─────┴ 6 ─┬─ 9 ────┤
        └──────────┴─ 10 ───┘
```

- Commit 2 → 1
- Commit 3 → 1
- Commit 4 → 1
- Commit 5 → 1, 4
- Commit 6 → 2, 3, 4, 5
- Commit 7 → 3, 6
- Commit 8 → 2, 6
- Commit 9 → 2, 4, 6
- Commit 10 → 4, 6
- Commit 11 → 7, 8, 9, 10
