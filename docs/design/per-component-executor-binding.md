# Design: Per-Component Executor Binding

**Feature:** `per-component-executor-binding`
**Bug prefix:** `[per-component-executor]`

## Abstract

Today an agent session routes tools to machines by *kind* — `workspace-gui` /
`workspace-entity` tools are tagged `ExecutorTarget.GuiLocal` and everything else
(including all MCP servers) is tagged `ExecutorTarget.AgentExecutor`
(`Phantom.Workspaces.Llm.Core/Transport/ExecutorTargetResolver.cs:32-51`). The whole chat
client can be remoted, but *individual* MCP servers cannot be pinned to different machines,
and the router that would do that (`ExecutorTargetRouter`) has **no production consumer**.

This design makes each component of a session — the model / Copilot-SDK chat client and each
individual MCP server/tool — bindable to a named **executor** (the local orchestrator, or a
named remote machine), by:

- introducing a manifest `kind:"executor"` resource and an optional `executor` reference on
  the model and on tools;
- making `McpToolContextProvider` actually connect through `ExecutorTargetRouter` when its
  bound executor is non-local (it always connects in-process today);
- registering a production remote MCP-hosting handler on `McpTransportListener`;
- persisting per-executor bindings on the session so the topology reconstructs on resume.

It reuses the existing (unit-tested but dormant) `ExecutorTarget` / `ExecutorTopology` /
`ExecutorTargetRouter` / `ExecutionTargetResolver` / `TransportTrustedExecutor` primitives and
the established `PhantomMcpTool` / `PhantomAgentSchema` extension pattern.

This is a distinct, finer-grained topology from the two existing remote designs:

- [`remote-agent-sessions.md`](remote-agent-sessions.md) moves an **entire session** remote and
  mirrors it locally via a proxy `AgentChat`.
- [`remote-chat-client-session.md`](remote-chat-client-session.md) keeps the router local and
  moves only the **inner `IChatClient`** remote, splitting tools by *kind* (gui-local vs. the
  rest).

**This** design generalises the second: instead of splitting by kind, every component is bound
to an explicitly named executor, and the split is expressed declaratively in the manifest.

---

## Gaps addressed

| Gap | Description | Fixed by |
|---|---|---|
| G1 | No per-tool / per-MCP executor binding; routing is per-kind static (`ExecutorTargetResolver.ForKind`). | Commits 1, 6 |
| G2 | No `executor` field on the MCP tool type (`PhantomMcpTool` carries only `Transport`). | Commit 3 |
| G3 | No `kind:"executor"` manifest resource (`resources[]` is `anyOf:[toolResource, modelResource]`). | Commit 1 |
| G4 | No `user-computer-profile` parameter kind / picker (Launchpad infers kind by *name* only). | Commits 2, 8 |
| G5 | MCP `mcp-server-entity` resolution is not scoped to the bound executor's profile→user→defaults context. | Commit 7 |
| G6 | Session persists only one remote (`host-profile-entity-id`), not per-executor bindings. | Commit 5 |
| G7 | "Session overall executor" is implicit (no explicit default-local concept). | Commit 5 |
| G8 | `ExecutorTargetRouter` has no production consumer; `McpToolContextProvider` always connects in-process. | Commit 6 |
| G9 | No remote production handler to host an arbitrary stdio/HTTP MCP server (`McpTransportListener` is never registered in production). | Commit 6 |

---

## Requirements

1. **Per-component binding.** Each component of an agent session — the model / Copilot-SDK chat
   client, and each individual MCP server/tool — can be bound to a specific executor (the local
   orchestrator, or a named remote machine), instead of routing uniformly by tool-kind.
2. **Executors are named manifest resources.** Executors are expressed as manifest `resources[]`
   entries of `kind:"executor"`, referenced by name from the model and from tools via an
   optional `executor` field.
3. **Unset inherits the session executor.** An unset `executor` inherits the **session's overall
   executor**, which is the local orchestrator machine today (`"."`). The system MUST NOT require
   or emit `executor:"local"`.
4. **Executor `id` resolution strategies.** An executor resource's `id` selects how it resolves
   to a client-instance: `local`, `parameter` (bind to a launch parameter), `user-computer-profile-entity`
   (fixed profile entity-id), and `trust-profile`.
5. **`user-computer-profile` launch parameter.** A new manifest parameter `kind:"user-computer-profile"`
   lets the user pick which remote machine at launch; the chosen profile entity-id is recorded in
   the session's `parameter-values`.
6. **MCP servers must actually execute on their bound executor.** `McpToolContextProvider` MUST
   connect through the transport router (`ExecutorTargetRouter` → `ExecutionTargetResolver` →
   `ITransportFactoryRegistry`) when its resolved client-instance is non-local; today it always
   connects in-process and `ExecutorTargetRouter` has no production consumer.
7. **Remote MCP hosting.** The remote host MUST host an arbitrary stdio/HTTP MCP server on request
   via a production `openConnectionAsync` handler registered on `McpTransportListener`.
8. **Entity resolution scoped to bound executor.** `mcp-server-entity` resource resolution MUST
   resolve in the context of the tool's **bound** executor (search order: machine profile →
   `${USER}/mcp-servers` → `defaults/mcp-servers`), not the resolving instance.
9. **Persist per-executor bindings.** The session MUST persist per-executor bindings
   (`executor-bindings`: name → resolved client-instance) so the topology reconstructs correctly
   on resume — not just the single `host-profile-entity-id`.
10. **Explicit session executor.** The "session overall executor" becomes an **explicit** concept
    (default local `"."`) that per-component executors override.
11. **Default split manifest.** Ship a default manifest entity (`defaults/agent-manifests/...`)
    implementing this split: the Copilot-SDK chat client runs **remotely**; the chat router, the
    workspace tools (`workspace-gui` / `workspace-entity`), and the GitHub web MCP server run
    **locally**.
12. **No web-vs-non-web distinction.** An MCP server with no `executor` simply runs on the local
    session executor. There is no special "web tools go remote" rule.
13. **OAuth interactivity rationale.** MCP servers using **interactive OAuth** (authorization-code
    with a loopback/localhost redirect + a browser) MUST run on the machine that can open the
    user's browser and receive the loopback redirect — i.e. the **local** executor. Therefore
    OAuth-interactive MCP servers MUST be pinned local; the default manifest pins the GitHub web
    MCP local for this reason (and because that is where the user authenticates). A key/PAT-authenticated
    web MCP does not strictly require local, but the default ships it local. Validation note: an
    MCP tool whose connection uses interactive OAuth combined with a non-local `executor` MUST be
    rejected or warned at load/validation time.

---

## Options

### Option A — Per-component executor binding via manifest `kind:"executor"` resources (CHOSEN)

**Architecture:** Add a manifest `kind:"executor"` resource and an optional `executor` string on
the model and on tools. A new resolver maps each executor resource (given `parameter-values` and
trust context) to a client-instance (`"."` or a profile UUID). At session build time these
bindings become an `ExecutorTopology`-like map (`name → client-instance`) persisted as
`executor-bindings`. Each `McpToolContextProvider` receives its bound client-instance; when it is
non-local it connects via the already-existing `ExecutorTargetRouter` →
`ExecutionTargetResolver` → `ITransportFactoryRegistry` (opening an `McpClientOverTransport`),
and a production `openConnectionAsync` handler on the remote `McpTransportListener` hosts the
arbitrary stdio/HTTP MCP connection. When local, the existing in-process path is preserved (no
round-trip).

**Pros:**
- True per-server granularity — any individual MCP server can go to any machine.
- Reuses the existing, unit-tested transport primitives (`ExecutorTarget`, `ExecutorTopology`,
  `ExecutorTargetRouter`, `ExecutionTargetResolver`, `TransportTrustedExecutor`,
  `McpClientOverTransport`) and simply makes `ExecutorTargetRouter` a production consumer.
- Reuses the established `PhantomMcpTool` / `PhantomAgentSchema` subclass extension recipe for the
  new `executor` field, including its round-trip guard test.
- Keeps ONE `AgentChat` / conversation; no session-management duplication.
- Declarative and back-compatible: unset `executor` behaves exactly as today.

**Cons:**
- Requires a new production remote MCP-hosting handler (does not exist yet — G9).
- Requires threading a resolved client-instance into `McpToolContextProvider` (currently it
  receives only an `ExecutorTarget` enum that it ignores).
- Manifest `parameters` are currently loosely typed and the Launchpad infers kind by name; adding
  a real `user-computer-profile` kind touches both the model and the picker.

### Option B — Keep per-kind topology; add one more `ExecutorTarget` class for "MCP" (considered)

**Architecture:** Extend the `ExecutorTarget` enum with, e.g., an `Mcp` class and map all MCP
tools onto it, giving one more topology slot.

**Pros:**
- Minimal schema change; no `executor` field.

**Cons:**
- Still cannot pin **individual** servers to **different** machines — all MCP tools share one
  slot. Fails Requirement 1 and G1. Rejected.

### Option C — Spawn a child agent sub-session per executor (dispatcher pattern) (considered)

**Architecture:** For each executor, spin up a child agent sub-session (reusing the sub-agent
dispatcher) that owns the tools bound to that executor, and federate them into the parent
conversation.

**Pros:**
- Naturally isolates per-executor state.

**Cons:**
- Heavyweight: duplicates session/persistence management, complicates a single conversation, and
  fights the "one `AgentChat`" model. Overkill for per-tool locality. Rejected.

---

## Chosen design

**Approach:** Option A — Per-component executor binding via manifest `kind:"executor"` resources.

**Rationale:** Option A gives true per-server granularity (addressing B's fatal con) by making
the existing but dormant `ExecutorTargetRouter` a production consumer and giving each MCP provider
a resolved client-instance. It avoids C's sub-session sprawl by keeping ONE `AgentChat` whose
components are individually routed over transport. Its own cons are contained: the remote MCP host
handler is a small `McpTransportListener` registration (the listener primitive already exists and
is exercised by tests), the client-instance threading is an additive constructor parameter on
`McpToolContextProvider`, and the parameter-kind work is localised to the manifest parameter model
and the Launchpad picker. Because an unset `executor` and a single-machine topology both resolve
to `"."`, the change is behaviour-preserving for every existing manifest.

---

## Detailed design

> **Legend:** *(NEW/PROPOSED)* marks types, fields, and files this feature introduces.
> *(EXISTING)* marks code that already exists and is verified below.

### Verified starting state (evidence)

- *(EXISTING)* `McpToolContextProvider` connects **in-process** via
  `McpTransportFactory.CreateMcpTransportAsync`
  (`Phantom.Workspaces.Llm.Core/McpToolContextProvider.cs:61-72`). It stores an `ExecutorTarget`
  property (`:43`) that is **never consumed** — a server tagged for another machine still runs
  where the `AgentChat` runs. **Core gap.**
- *(EXISTING)* `ExecutorTargetRouter` is documented as "Routes a per-tool `ExecutorTarget` to the
  correct machine" and maps target → client-instance via `ExecutorTopology`, builds a descriptor
  via `ExecutionTargetResolver`, and connects via `ITransportFactoryRegistry`
  (`Phantom.Workspaces.Llm.Core/Transport/ExecutorTargetRouter.cs:17-49`). It has **no production
  consumer** — `new ExecutorTargetRouter` appears only in
  `Phantom.Workspaces.Llm.Core.Tests/ExecutorTargetRouterTests.cs` and
  `Phantom.Workspaces.Transport.Tests/Scenarios/Scenario2_GuiLocalToolRoutingTests.cs`.
- *(EXISTING)* Whole-session remote execution works: `TransportTrustedExecutor.CreateAgentChatAsync`
  wraps a transport in `ChatClientOverTransport`
  (`Phantom.Workspaces.Llm.Core/Transport/TransportTrustedExecutor.cs:43-67`) — the ENTIRE
  `AgentChat` (Copilot SDK + all in-process MCP providers) runs on ONE remote instance.
  `RunToolAsync` + `McpClientOverTransport` (`:81-99`) route Phantom `CustomTool`s
  (workspace-gui/entity, tagged `GuiLocal`) back to local via a `tool-type-name` / `tool-entity-id`
  protocol (`:156-182`) — **not** arbitrary stdio/HTTP MCP servers. So remoting is all-or-nothing
  for the `AgentExecutor` class today.
- *(EXISTING)* `DeferredTrustedExecutorSelector.SelectExecutorForTarget(ExecutorTarget)` maps a
  target to local vs. remote via `ExecutorTopology.ResolvesLocally`
  (`Phantom.Workspaces/Trust/DeferredTrustedExecutorSelector.cs:64-80`); topology is set via
  `SetTopology` (`:51-54`).
- *(EXISTING)* `AgentChat` tags tools via `Core.Transport.ExecutorTargetResolver.ForTool(tool)`
  and constructs each `McpToolContextProvider` with that target
  (`Phantom.Workspaces.Llm.Core/AgentChat.cs:2403-2416`) — but nothing routes them remotely per
  tag.
- *(EXISTING)* `McpTransportListener` accepts `{"type":"mcp","connection":{...}}` and delegates to
  a registered `openConnectionAsync` callback, wrapping the result in `McpServerSession`
  (`Phantom.Workspaces.Transport/Mcp/McpTransportListener.cs:9-27`). **No production
  `openConnectionAsync` is registered** — `WorkspacesTransportComposition` registers only a
  `ChatClientTransportListener` (`Phantom.Workspaces/Services/WorkspacesTransportComposition.cs:54-63`);
  `new McpTransportListener(...)` appears only in `Phantom.Workspaces.Transport.Tests`. **Confirms
  G9.**
- *(EXISTING)* `PhantomMcpTool : McpTool` adds `Transport`, recovered from dropped JSON props
  (`Phantom.Workspaces.Llm.Interfaces/PhantomMcpTool.cs:34-77`) via `PhantomAgentSchema.CreateContext`
  `PostProcess` → `ReadTransport` (`Phantom.Workspaces.Llm.Interfaces/PhantomAgentSchema.cs:47-79`),
  copied in `From()` and emitted in `Save()`. A source-scan guard test forbids direct AgentSchema
  `FromJson` (enforcing centralisation through `PhantomAgentSchema`, documented in that file's
  `<remarks>`).
- *(EXISTING)* Two similarly-named resolvers exist and must not be confused:
  - `Phantom.Workspaces.Llm.Trust.ExecutionTargetResolver` maps a **client-instance** to a
    transport descriptor: `"."` → `{"type":"local"}`, else →
    `{"type":"user-computer-profile","entity-id":...}`
    (`Phantom.Workspaces.Llm.Core/Trust/ExecutionTargetResolver.cs:34-52`). The
    `auto-resume.trusted-executor` field already uses the same `"."`-or-UUID convention.
  - `Phantom.Workspaces.Llm.Core.Transport.ExecutorTargetResolver` maps a tool **kind** to an
    `ExecutorTarget` enum (`.../Transport/ExecutorTargetResolver.cs`).
- *(EXISTING)* `agent-manifest.json` `resources[].items` is `anyOf:[toolResource, modelResource]`
  (`Phantom.Workspaces.Llm.Core/JsonSchemas/agent-manifest.json:44-53`). `toolResource` requires
  `kind`/`id`/`name` and is `additionalProperties:true` (`:57-87`).
- *(EXISTING)* `agent-session.json` has `host-profile-entity-id`, `parameter-values`, and
  `auto-resume{trusted-executor,resume-prompt}`
  (`Phantom.Workspaces.Data.Core/JsonSchemas/agent-session.json:24-70`).

### Code organisation

**New files** *(NEW/PROPOSED)*:

- `Phantom.Workspaces.Llm.Core/Manifest/ExecutorResource.cs` — parsed model of a `kind:"executor"`
  manifest resource.
- `Phantom.Workspaces.Llm.Core/Manifest/ExecutorResourceResolver.cs` — resolves an
  `ExecutorResource` (+ `parameter-values` + trust context) to a client-instance string.
- `Phantom.Workspaces.Llm.Core/Manifest/ExecutorBindings.cs` — immutable `name → client-instance`
  map plus the explicit session executor; builds an `ExecutorTopology`-equivalent for routing.
- `Phantom.Workspaces.Transport.Mcp/RemoteMcpHostHandler.cs` — the production `openConnectionAsync`
  that opens an arbitrary stdio/HTTP MCP connection described by the request. *(May instead live in
  `Phantom.Workspaces/Services` next to `WorkspacesTransportComposition` if it must reference the
  MCP transport factory; see Data flow.)*
- `Phantom.Workspaces.Data.Core/JsonEntities/agent-manifests/copilot-split-executor.json` — the
  default split-executor manifest entity.

**Modified files** *(EXISTING, to change)*:

- `Phantom.Workspaces.Llm.Core/JsonSchemas/agent-manifest.json` — add an `executorResource` `$def`
  into `resources.items.anyOf`; add optional `executor` to `toolResource` and `modelResource`;
  document a `user-computer-profile` parameter kind.
- `Phantom.Workspaces.Llm.Interfaces/PhantomMcpTool.cs` — add `Executor` (nullable string) via the
  `From`/`Save` recipe.
- `Phantom.Workspaces.Llm.Interfaces/PhantomAgentSchema.cs` — read `executor` in `PostProcess`
  (`ReadExecutor`) so it survives load.
- `Phantom.Workspaces.Llm.Core/McpToolContextProvider.cs` — accept a resolved client-instance +
  router and connect over transport when non-local.
- `Phantom.Workspaces.Llm.Core/AgentChat.cs` — construct each `McpToolContextProvider` with its
  bound client-instance and the production `ExecutorTargetRouter`.
- `Phantom.Workspaces/Services/WorkspacesTransportComposition.cs` — register the production
  `RemoteMcpHostHandler` on `LocalListeners` via `McpTransportListener`.
- `Phantom.Workspaces.Data.Core/JsonSchemas/agent-session.json` — add `executor-bindings`.
- Session build/resume path (the code that constructs `ExecutorTopology` and calls
  `DeferredTrustedExecutorSelector.SetTopology`) — rebuild topology from `executor-bindings`.
- `Phantom.Workspaces/ViewModels/AgentManifestParameterKind.cs` +
  `AgentManifestLaunchpadViewModel.cs` — add a `UserComputerProfile` kind and honour the manifest
  parameter `kind` field (see Contradictions below — kind is currently inferred by name).
- `Phantom.Workspaces.Data.Core/JsonEntities/documentation/agent-options-parameters.md` — document
  parameters + `user-computer-profile` kind + `executor` resources + `executor-bindings` round-trip.

### Classes and interfaces

#### `ExecutorResource` *(NEW/PROPOSED)*

**Namespace:** `Phantom.Workspaces.Llm.Core.Manifest`
**Kind:** record
**Responsibility:** the parsed form of a manifest `resources[]` entry with `kind:"executor"`.

**Members:**
- `string Name { get; init; }` — the executor's name, referenced by `executor` fields.
- `string Id { get; init; }` — resolution strategy: `local`, `parameter`,
  `user-computer-profile-entity`, or `trust-profile`.
- `IReadOnlyDictionary<string, string?> Options { get; init; }` — strategy inputs (e.g. the
  parameter name for `parameter`, the fixed entity-id for `user-computer-profile-entity`, the trust
  profile name for `trust-profile`).

#### `ExecutorResourceResolver` *(NEW/PROPOSED)*

**Namespace:** `Phantom.Workspaces.Llm.Core.Manifest`
**Kind:** class
**Responsibility:** resolve an `ExecutorResource` to a client-instance string (`"."` or a
user-computer-profile UUID) given the resolved parameter values and trust context.

**Members:**
- `string Resolve(ExecutorResource resource, IReadOnlyDictionary<string,string> parameterValues, TrustProfile? trustProfile)`
  — dispatch on `Id`:
  - `local` → `TrustProfile.LocalClientInstance` (`"."`).
  - `parameter` → the value of the named parameter (a user-computer-profile UUID recorded at
    launch); missing/blank → throws.
  - `user-computer-profile-entity` → the fixed `entity-id` option verbatim.
  - `trust-profile` → the client-instance derived from the trust profile's
    `DefaultExecutionTarget` / `HostingWorkspacesClientInstances`.
  - unknown `Id` or unresolved profile → throws with a message mirroring the existing
    `"Tool resource '<id>:<name>' could not be resolved"` convention, e.g.
    `"Executor resource '<id>:<name>' could not be resolved"`.

#### `ExecutorBindings` *(NEW/PROPOSED)*

**Namespace:** `Phantom.Workspaces.Llm.Core.Manifest`
**Kind:** record
**Responsibility:** the resolved, persistable map of executor name → client-instance plus the
explicit **session executor** (default `"."`); knows how to project onto the routing primitives.

**Members:**
- `string SessionExecutor { get; init; }` — the overall session executor; default
  `TrustProfile.LocalClientInstance` (`"."`).
- `IReadOnlyDictionary<string,string> Bindings { get; init; }` — executor name → client-instance.
- `string ResolveComponent(string? executorName)` — returns `SessionExecutor` when
  `executorName` is null/empty (Requirement 3), otherwise the bound client-instance; unknown name
  → throws.
- `ExecutorTopology ToTopology()` — projects the session executor and gui-local classification into
  an `ExecutorTopology` so `DeferredTrustedExecutorSelector.SetTopology` continues to work for the
  `CustomTool` (workspace-gui/entity) routing that already exists.
- `IReadOnlyDictionary<string,string> ToPersistableMap()` — the `executor-bindings` JSON payload.

#### `RemoteMcpHostHandler` *(NEW/PROPOSED)*

**Namespace:** `Phantom.Workspaces.Transport.Mcp` (or `Phantom.Workspaces.Services`)
**Kind:** class (the `Func<JsonElement, IMessageChannel, CancellationToken, Task<IAsyncDisposable?>>`
registered on `McpTransportListener`)
**Responsibility:** on the remote host, open an arbitrary stdio/HTTP MCP connection described by
the inbound `{"type":"mcp","connection":{...}}` request and bridge it to the caller's message
channel. This is the production `openConnectionAsync` that G9 says is missing.

**Members:**
- `Task<IAsyncDisposable?> OpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct)`
  — parse the `connection` descriptor (stdio command/args/cwd/env, or HTTP endpoint + transport
  mode), open the local MCP client via the shared `McpTransportFactory`, and pump messages between
  the MCP server and `channel`; returns the disposable that tears the connection down.

#### `McpToolContextProvider` *(EXISTING — modified)*

**Namespace:** `Phantom.Workspaces.Llm`
**Change:** add a resolved client-instance + router so it connects over transport when non-local.

**New/changed members:**
- Constructor gains `string boundClientInstance` and an optional `ExecutorTargetRouter router`
  (both additive; default `"."` + null preserve today's in-process behaviour).
- `ProvideAIContextAsync` — when `ExecutionTargetResolver.IsLocal(boundClientInstance)` is `true`,
  keep the existing in-process `McpTransportFactory.CreateMcpTransportAsync` path (no round-trip);
  otherwise connect via the router: build a `{"type":"mcp","connection":{...}}` descriptor from
  `this.tool` and open an `McpClientOverTransport` against the transport returned by
  `router.ConnectAsync(...)` / `transportFactoryRegistry.ConnectToAsync(descriptor)`.

#### `ExecutorTargetRouter` *(EXISTING — becomes production consumer)*

No shape change; this feature adds its first production `new ExecutorTargetRouter(...)` in the
session build path and threads it into `McpToolContextProvider` (closing G8).

### Data flow

**Load / build (session start):**

1. `PhantomAgentSchema.AgentManifestFromJson` loads the manifest; `PostProcess` upgrades each
   `McpTool` to `PhantomMcpTool`, now also recovering the dropped `executor` field
   (`ReadExecutor`).
2. The manifest's `resources[]` are parsed; `kind:"executor"` entries become `ExecutorResource`s.
3. `ExecutorResourceResolver.Resolve` turns each `ExecutorResource` into a client-instance using
   the resolved `parameter-values` (including any `user-computer-profile` parameter the user chose)
   and trust context. The result is an `ExecutorBindings` (with `SessionExecutor` = `"."` by
   default).
4. For each component: the model's `executor` and each tool's `executor` are resolved through
   `ExecutorBindings.ResolveComponent` to a client-instance; unset → `SessionExecutor`.
5. `AgentChat` constructs each `McpToolContextProvider` with its bound client-instance and the
   production `ExecutorTargetRouter` (built from `ExecutorBindings.ToTopology()` and the
   `ITransportFactoryRegistry`). `DeferredTrustedExecutorSelector.SetTopology` is set from the same
   topology so `CustomTool` (gui-local) routing is unchanged.
6. `ExecutorBindings.ToPersistableMap()` is written to the session's `executor-bindings`.

**Runtime (per MCP server first use):**

7. `McpToolContextProvider.ProvideAIContextAsync` runs lazily. If its bound client-instance is
   local, it connects in-process exactly as today. If non-local, it connects via the router →
   `ExecutionTargetResolver.ResolveDescriptor` → `ITransportFactoryRegistry.ConnectToAsync`,
   opening an `McpClientOverTransport`.
8. On the remote host, the inbound `{"type":"mcp","connection":{...}}` channel is served by the
   production `RemoteMcpHostHandler` registered on `McpTransportListener`, which opens the described
   stdio/HTTP MCP server locally and bridges it back.
9. Tool listing / calls flow over the channel; results and `tool-error`s round-trip.

**Resume:**

10. On resume, `executor-bindings` is read back and an `ExecutorBindings` / `ExecutorTopology` is
    reconstructed, so the same components bind to the same machines (Requirement 9). If
    `executor-bindings` is absent (legacy session), fall back to `host-profile-entity-id` for the
    single remote (back-compat).

**Entity resolution (Commit 7):**

11. When a tool's `id` is `mcp-server-entity`, the search prefixes (machine profile →
    `${USER}/mcp-servers` → `defaults/mcp-servers`) are evaluated against the **bound** executor's
    profile/user context, not the resolving instance's.

### Tests

Test names follow `<Subject>_<Scenario>_<ExpectedOutcome>` matching neighbours such as
`Scenario2_GuiLocalTool_DuringRemoteTurn_RoutesBackToMachineA`.

#### `ExecutorResourceResolverTests` (`Phantom.Workspaces.Llm.Core.Tests`)
- `Resolve_LocalId_ReturnsDotClientInstance`
- `Resolve_ParameterId_ReturnsResolvedProfileUuid`
- `Resolve_UserComputerProfileEntityId_ReturnsFixedUuid`
- `Resolve_TrustProfileId_ReturnsDerivedClientInstance`
- `Resolve_UnknownId_ThrowsWithClearMessage`
- `Resolve_ParameterMissing_ThrowsWithClearMessage`

#### `ExecutorBindingsTests` (`Phantom.Workspaces.Llm.Core.Tests`)
- `ResolveComponent_UnsetExecutor_InheritsSessionExecutor`
- `ResolveComponent_UnknownName_Throws`
- `ToTopology_LocalSession_ResolvesLocally`

#### `PhantomMcpToolExecutorTests` (`Phantom.Workspaces.Llm.Interfaces.Tests`)
- `Save_WithExecutor_EmitsExecutorField`
- `From_CopiesExecutor`
- `RoundTrip_ExecutorField_Preserved`
- (guard) `PhantomAgentSchema_IsOnlyLoadEntryPoint_NoDirectFromJson` — extend/confirm existing guard.

#### `AgentManifestExecutorResourceTests` (`Phantom.Workspaces.Llm.Core.Tests`)
- `Load_ManifestWithExecutorResource_ParsesResource`
- `RoundTrip_ExecutorResourceAndRefs_Lossless`
- `Load_UserComputerProfileParameter_Recognised`

#### `McpToolContextProviderRoutingTests` (`Phantom.Workspaces.Llm.Core.Tests`)
- `ProvideAIContext_BoundLocal_UsesInProcessFactory_NoRoundTrip`
- `ProvideAIContext_BoundRemote_ConnectsViaRouter`
- `ProvideAIContext_BoundRemote_ExecutorTargetRouterExercisedAsProductionConsumer`

#### `AgentSessionExecutorBindingsTests` (`Phantom.Workspaces.Data.Core.Tests`)
- `Persist_ExecutorBindings_RoundTrips`
- `Resume_RebuildsTopologyFromBindings`
- `Resume_LegacyHostProfileOnly_FallsBackToSingleRemote`

#### `Scenario3_PerMcpServerRoutingTests` (`Phantom.Workspaces.Transport.Tests/Scenarios`)
Mirrors `Scenario2_GuiLocalToolRoutingTests`.
- `Scenario3_LocalBoundMcpServer_ConnectsInProcess_NoTransportRoundTrip`
- `Scenario3_RemoteBoundMcpServer_RoutesOverTransport`
- `Scenario3_RemoteHost_OpensStdioMcpConnection_ViaProductionHandler`
- `Scenario3_RemoteHost_OpensHttpMcpConnection_ViaProductionHandler`
- `Scenario3_RemoteBoundMcpServer_ToolCall_RoundTripsResult`
- `Scenario3_RemoteBoundMcpServer_ToolError_RoundTripsError`

#### `RemoteMcpHostHandlerTests` (`Phantom.Workspaces.Transport.Tests/Mcp`)
- `OpenAsync_StdioConnection_HostsServer`
- `OpenAsync_HttpConnection_HostsServer`
- `OpenAsync_UnknownConnection_ReturnsNull`

#### `McpServerEntityBoundExecutorResolutionTests` (`Phantom.Workspaces.Llm.Core.Tests`)
- `Resolve_BoundExecutorMachineProfile_WinsOverUserAndDefaults`
- `Resolve_FallsBackThroughUserThenDefaults`
- `Resolve_UsesBoundExecutorContext_NotResolvingInstance`

#### `AgentManifestLaunchpadViewModelTests` (`Phantom.Workspaces.Tests` / `Phantom.Workspaces.Agent.Gui.Tests`)
- `Parameters_UserComputerProfileKind_ListsProfileEntities`
- `Parameters_UserComputerProfileSelection_RecordedInParameterValues`

#### `CopilotSplitExecutorManifestTests` (`Phantom.Workspaces.Data.Core.Tests`)
- `Manifest_Loads`
- `Manifest_WorkerProfileParameter_Resolves`
- `Manifest_ModelBoundToWorker_ResolvesRemote`
- `Manifest_WorkspaceToolsAndGithubWebMcp_ResolveLocal`
- `Validation_OAuthInteractiveMcpWithNonLocalExecutor_IsRejected`

#### `SplitExecutorIntegrationTests` (`Phantom.Workspaces.Tests`)
- `DefaultManifest_Session_RecordsExecutorBindings_CopilotWorker_WorkspaceLocal`
- `DefaultManifest_Topology_RoutesComponentsAccordingly`

---

## Testing strategy

This section is the authoritative map of what must be proven and where. Every listed test class
lives in an existing test project and follows the existing `<Subject>_<Scenario>_<ExpectedOutcome>`
convention (read `Scenario2_GuiLocalToolRoutingTests`, `ExecutorTargetRouterTests`, and
`McpTransportListenerTests` before writing, to match style).

### 1. Schema and model round-trip

- **Manifest round-trip.** A manifest carrying `kind:"executor"` resources, `executor` refs on the
  model and on tools, and a `user-computer-profile` parameter loads through `PhantomAgentSchema`
  and re-serialises losslessly, remaining compliant with the AgentSchema source-scan guard test.
  → `AgentManifestExecutorResourceTests`.
- **`PhantomMcpTool.Executor` round-trip + guard.** `Save` emits `executor`, `From` copies it, a
  `ToJson()` → load round-trip preserves it, and the guard test that forbids direct AgentSchema
  `FromJson` still passes. → `PhantomMcpToolExecutorTests`.

### 2. Executor-resource resolution (unit)

- One test per `id` strategy: `local` → `"."`; `parameter` → the resolved profile UUID;
  `user-computer-profile-entity` → the fixed UUID; `trust-profile` → the derived client-instance.
- Failure paths: unknown `id` and unresolved/blank parameter both throw with a clear, convention-matching
  message. → `ExecutorResourceResolverTests`.
- Inheritance: unset `executor` inherits `SessionExecutor`; unknown name throws.
  → `ExecutorBindingsTests`.

### 3. Session persistence / resume

- `executor-bindings` persists and round-trips on the `agent-session` entity.
- Resume rebuilds the topology from `executor-bindings` so each component re-binds identically.
- Unset `executor` inherits the session executor after resume.
- Back-compat: a legacy session with only `host-profile-entity-id` (no `executor-bindings`)
  resolves to the single remote. → `AgentSessionExecutorBindingsTests`.

### 4. Transport scenario tests (integration, hermetic)

Mirror `Scenario2_GuiLocalToolRoutingTests` (in-process `TransportRegistry` machines reached over
`LocalTransport`; routing via `ExecutorTargetRouter`). → `Scenario3_PerMcpServerRoutingTests`:

- A tool bound `local` connects **in-process** — assert **no** transport round-trip (e.g. the
  remote machine's listener is never hit).
- A tool bound to a remote executor routes **over transport**.
- The remote host opens **both** a stdio **and** an HTTP MCP connection via the production
  `openConnectionAsync` handler.
- An end-to-end tool call round-trips a **result**, and a failing tool call round-trips a
  **`tool-error`** (mirroring `TransportTrustedExecutor.RunToolAsync`'s `tool-error` handling).

### 5. `McpToolContextProvider` behaviour

- Bound-local uses the in-process factory (existing `McpTransportFactory` path).
- Bound-remote uses the router.
- `ExecutorTargetRouter` is exercised as a **production** consumer (asserting the router was the
  connection path, closing G8). → `McpToolContextProviderRoutingTests`.

### 6. Remote MCP host handler

- `OpenAsync` hosts a stdio server and an HTTP server; an unrecognised connection descriptor
  returns null (so `McpTransportListener` declines it). → `RemoteMcpHostHandlerTests`.

### 7. MCP entity resolution scoped to bound executor

- Resolves against the bound executor's machine profile first, then `${USER}/mcp-servers`, then
  `defaults/mcp-servers`; correct fallback order; uses the bound executor's context, not the
  resolving instance's. → `McpServerEntityBoundExecutorResolutionTests`.

### 8. Launchpad picker

- The `user-computer-profile` parameter lists `user-computer-profile` entities and records the
  chosen entity-id in `parameter-values`. → `AgentManifestLaunchpadViewModelTests`.

### 9. Default manifest + validation

- The default manifest loads; the `worker-profile` parameter resolves; the model resolves to the
  remote worker; the workspace tools and the GitHub web MCP resolve local.
- A validation test asserts that an OAuth-interactive MCP with a non-local `executor` is
  rejected/warned at load. → `CopilotSplitExecutorManifestTests`.

### 10. Full split-executor integration

- Using the default manifest, assert the resulting session records `executor-bindings` with
  `copilot → worker` and `workspace`/`github-web → "."`, and that the reconstructed topology routes
  accordingly. → `SplitExecutorIntegrationTests`.

### Non-functional checks

- **No-round-trip guarantee.** For a single-machine topology / all-local bindings, assert no
  transport hop is introduced (preserves the `ExecutorTopology.IsSingleMachine` invariant).
- **Determinism / hermeticism.** All transport tests use in-process registries and cancellation
  tokens as in `TransportScenarioSupport`; no network.

---

## Implementation plan

Each commit leaves the build green and all tests passing.

### Commit 1 — Executor resource schema + model

**Scope:** Add the `executorResource` `$def` to `agent-manifest.json` and its parsed model
`ExecutorResource`. Extend `resources.items.anyOf` to include it. Add the optional `executor`
string to `toolResource` and `modelResource` `$defs`. Parse `kind:"executor"` resources into
`ExecutorResource` during manifest load. *(NEW schema fields marked in the schema comments.)*
**Files:** `Phantom.Workspaces.Llm.Core/JsonSchemas/agent-manifest.json`;
`Phantom.Workspaces.Llm.Core/Manifest/ExecutorResource.cs` (new); the manifest loader that
enumerates `resources[]`.
**Tests:** `AgentManifestExecutorResourceTests`
(`Load_ManifestWithExecutorResource_ParsesResource`, `RoundTrip_ExecutorResourceAndRefs_Lossless`).
**Dependencies:** none.

### Commit 2 — `user-computer-profile` parameter kind

**Scope:** Add a `user-computer-profile` parameter kind to the manifest parameter model and its
documentation, plus value recording/substitution (the chosen profile UUID recorded in
`parameter-values`). Make parameter kind read from the manifest parameter `kind` field rather than
being inferred purely by name (see Contradictions). *(NEW parameter kind.)*
**Files:** the `AgentManifest` parameter model / substitutor
(`Phantom.Workspaces.Llm.Core/AgentDefinitionParameterSubstitutor.cs` and the parameter property
model); `Phantom.Workspaces.Data.Core/JsonEntities/documentation/agent-options-parameters.md`.
**Tests:** `AgentManifestExecutorResourceTests.Load_UserComputerProfileParameter_Recognised`; a
substitutor test for the new kind.
**Dependencies:** none.

### Commit 3 — `PhantomMcpTool.Executor` field

**Scope:** Add `Executor` (nullable string) to `PhantomMcpTool` using the established recipe: read
in `PhantomAgentSchema` `PostProcess` (`ReadExecutor`), copy in `From()`, emit in `Save()`. Keep
the source-scan guard intact.
**Files:** `Phantom.Workspaces.Llm.Interfaces/PhantomMcpTool.cs`;
`Phantom.Workspaces.Llm.Interfaces/PhantomAgentSchema.cs`.
**Tests:** `PhantomMcpToolExecutorTests` (`Save_WithExecutor_EmitsExecutorField`,
`From_CopiesExecutor`, `RoundTrip_ExecutorField_Preserved`, guard test).
**Dependencies:** none.

### Commit 4 — Executor-resource resolver

**Scope:** Add `ExecutorResourceResolver` mapping an `ExecutorResource` (+ resolved
`parameter-values` + trust context) to a client-instance for all four `id` strategies, with clear
errors for unknown/unresolved. Add `ExecutorBindings` (session executor default `"."` +
name→client-instance + `ResolveComponent` + `ToTopology`).
**Files:** `Phantom.Workspaces.Llm.Core/Manifest/ExecutorResourceResolver.cs`,
`Phantom.Workspaces.Llm.Core/Manifest/ExecutorBindings.cs` (new).
**Tests:** `ExecutorResourceResolverTests`, `ExecutorBindingsTests`.
**Dependencies:** Commit 1 (`ExecutorResource`); Commit 2 (for the `parameter` strategy).

### Commit 5 — Explicit session executor + `executor-bindings` persistence + resume

**Scope:** Make the session's overall executor explicit (default `"."`). Add `executor-bindings`
(name→client-instance) to `agent-session.json`. On build, write bindings; on resume, rebuild
`ExecutorTopology` and set the deferred selector's topology from the bindings. Keep
`host-profile-entity-id` as back-compat fallback (primary remote), with bindings as source of
truth. *(NEW session field: `executor-bindings`; EXISTING `host-profile-entity-id` retained.)*
**Files:** `Phantom.Workspaces.Data.Core/JsonSchemas/agent-session.json`; the session build/resume
path that constructs `ExecutorTopology` and calls
`DeferredTrustedExecutorSelector.SetTopology`.
**Tests:** `AgentSessionExecutorBindingsTests`.
**Dependencies:** Commit 4.

### Commit 6 — Per-tool MCP execution over transport + production remote MCP host

**Scope:** Thread the resolved client-instance + a production `ExecutorTargetRouter` into each
`McpToolContextProvider` (constructed in `AgentChat`). When the bound client-instance is non-local,
connect via the router → `ExecutionTargetResolver` → `ITransportFactoryRegistry`, opening an
`McpClientOverTransport`; when local, keep the in-process path (no round-trip). Add the production
`RemoteMcpHostHandler` (`openConnectionAsync`) and register it on the remote `McpTransportListener`
in `WorkspacesTransportComposition`. This makes `ExecutorTargetRouter` a production consumer
(G8) and provides the arbitrary stdio/HTTP MCP host (G9).
**Files:** `Phantom.Workspaces.Llm.Core/McpToolContextProvider.cs`,
`Phantom.Workspaces.Llm.Core/AgentChat.cs`,
`Phantom.Workspaces.Transport.Mcp/RemoteMcpHostHandler.cs` (new),
`Phantom.Workspaces/Services/WorkspacesTransportComposition.cs`.
**Tests:** `McpToolContextProviderRoutingTests`, `RemoteMcpHostHandlerTests`,
`Scenario3_PerMcpServerRoutingTests`.
**Dependencies:** Commits 3, 4, 5.

### Commit 7 — MCP `mcp-server-entity` resolution scoped to the bound executor

**Scope:** When a tool `id` is `mcp-server-entity`, evaluate the search prefixes (machine profile →
`${USER}/mcp-servers` → `defaults/mcp-servers`) against the **bound** executor's profile/user
context instead of the resolving instance's.
**Files:** the `mcp-server-entity` resolution code (the toolset factory / resource resolver that
implements the documented prefix search).
**Tests:** `McpServerEntityBoundExecutorResolutionTests`.
**Dependencies:** Commit 6.

### Commit 8 — Launchpad `user-computer-profile` picker UI

**Scope:** Add a `UserComputerProfile` value to `AgentManifestParameterKind` and a picker in the
Launchpad that lists `user-computer-profile` entities and records the chosen entity-id in
`parameter-values`.
**Files:** `Phantom.Workspaces/ViewModels/AgentManifestParameterKind.cs`,
`Phantom.Workspaces/ViewModels/AgentManifestParameterRowViewModel.cs`,
`Phantom.Workspaces/ViewModels/AgentManifestLaunchpadViewModel.cs`,
`Phantom.Workspaces/Templates/AgentManifestLaunchpadView.axaml(.cs)`.
**Tests:** `AgentManifestLaunchpadViewModelTests`.
**Dependencies:** Commit 2.

### Commit 9 — Default split-executor Copilot manifest + OAuth-local validation

**Scope:** Add `defaults/agent-manifests/copilot-split-executor` with: one `kind:"executor"`
resource `worker` (id `parameter` → `worker-profile`); a `worker-profile` parameter (kind
`user-computer-profile`) + a `working-directory` parameter; a model with `executor:"worker"`;
`workspace-gui` / `workspace-entity` tools with **no** `executor` (inherit local); a GitHub web MCP
tool with **no** `executor` (local, for OAuth). Add load-time validation that rejects/warns an
OAuth-interactive MCP whose `executor` is non-local. Cross-check the exact tool/model/connection
JSON shapes against `features/docs/examples/github-copilot-remote-chat.json`.
**Files:** `Phantom.Workspaces.Data.Core/JsonEntities/agent-manifests/copilot-split-executor.json`
(new); the manifest/validation code that enforces the OAuth-local rule.
**Tests:** `CopilotSplitExecutorManifestTests`, `SplitExecutorIntegrationTests`.
**Dependencies:** Commits 1–6.

---

## Cross-references

- [`remote-agent-sessions.md`](remote-agent-sessions.md) — moves an entire session remote (proxy
  `AgentChat`); this design is finer-grained and complementary.
- [`remote-chat-client-session.md`](remote-chat-client-session.md) — router-local + inner
  `IChatClient` remote, split by tool *kind*; this design generalises that split to explicit
  per-component executor bindings.
- [`unified-transport-production-cutover.md`](unified-transport-production-cutover.md) and
  [`unified-transport-layer.md`](unified-transport-layer.md) — the transport primitives
  (`ITransportFactoryRegistry`, listeners, `ChatClientTransportListener`) this feature builds on.

## Contradictions with the initial brief (verified against the codebase)

1. **`agent-manifest.json` `parameters` is untyped.** The schema declares `parameters` as
   `type:object, additionalProperties:true`
   (`Phantom.Workspaces.Llm.Core/JsonSchemas/agent-manifest.json:36-40`) — it is **not** a typed
   `properties[]` array. The `{name,kind,description,required,default}` shape lives in the
   `AgentManifest` model and in `agent-options-parameters.md`, not in the JSON schema. Adding the
   `user-computer-profile` kind is primarily a model + documentation change (Commit 2).
2. **Launchpad infers parameter kind by NAME, not a `kind` field.**
   `AgentManifestLaunchpadViewModel.DetermineParameterKind` returns `Directory` only for the exact
   name `working-directory` (`AgentManifestLaunchpadViewModel.cs:291-296`); `AgentManifestParameterKind`
   has only `Text` and `Directory` (`AgentManifestParameterKind.cs`). Commit 2/8 must switch the
   picker to honour the manifest parameter `kind` field and add a `UserComputerProfile` kind.
3. **`github-copilot-remote-chat.json` is an AgentDefinition, not a manifest.** It is
   `kind:"prompt"` and drives remoting via `model.options.additionalProperties.trust-profile`
   (`features/docs/examples/github-copilot-remote-chat.json:6-22,78-94`) — the OLD per-kind
   approach. It is a useful shape reference for tool/model/connection JSON, but it does **not** use
   `kind:"executor"` resources or an `executor` field.
4. **Two similarly named resolvers.** `ExecutionTargetResolver` (client-instance → descriptor;
   `"."`→local) is `Phantom.Workspaces.Llm.Trust.ExecutionTargetResolver`
   (`Phantom.Workspaces.Llm.Core/Trust/ExecutionTargetResolver.cs`), while
   `Phantom.Workspaces.Llm.Core.Transport.ExecutorTargetResolver` maps tool *kind* → `ExecutorTarget`.
   The brief's "ExecutionTargetResolver maps '.'→local" refers to the former, which is confirmed.
5. **No production `openConnectionAsync` MCP host handler exists.** `WorkspacesTransportComposition`
   registers only a `ChatClientTransportListener`
   (`Phantom.Workspaces/Services/WorkspacesTransportComposition.cs:54-63`); `new McpTransportListener(...)`
   appears only in `Phantom.Workspaces.Transport.Tests`. This confirms G9 — Commit 6 must add it.
