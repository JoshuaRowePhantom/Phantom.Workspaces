# Trust models

## Purpose

Each tool and each whole agent definition can run associated with a **trust profile**.
A trust profile defines:

- The set of Workspaces client instances (computers) that the agent / tool **may** run on
  (`allowed-client-instances`) and the set that are **explicitly denied** (`denied-client-instances`).
- A `default-execution-target` specifying the specific machine to use when this profile is
  selected (optional; can also be overridden in the agent or tool definition). **[New]**
- The permissions allowed to that agent / tool at the **tool execution** level (MCP tool-call
  schema policy).
- The OS / container permissions allowed to that agent / tool at the **container** level
  (mounts, network access, HTTPS proxy policy).

A trust profile can inherit other trust profiles, so that one profile can set tool
permissions, another can set OS permissions, and another can restrict the computer set.
Each base is inherited in one of two **modes**:

- **Restrictive** (default): composing the base can only narrow the effective policy
  (intersection / most-restrictive).
- **Permissive**: composing the base widens the effective policy (union / most-permissive),
  granting additional capabilities.

A profile can mix modes across its bases — for example, restrictively inheriting a
computer-set restriction while permissively inheriting an extra network grant.

This document describes the trust *model* — how trust profiles are authored, composed, and
enforced across local and remote execution. It builds on two existing pieces:

- The persisted entity schema in
  `Phantom.Workspaces.Data.Core/JsonSchemas/llm-trust-profile.json`
  (documented in `docs/design/llm-trust-profile.md` and
  `Phantom.Workspaces.Data.Core/JsonEntities/documentation/llm-trust-profile-schema.md`).
- The runtime trust-profile types and Docker materialization described in
  `docs/design/llm-session.md`.

## Entity vs. runtime forms

Trust profiles follow the same entity / runtime split used elsewhere in the codebase:

- **`LlmTrustProfileEntity`** — the persisted, user-semantic form used for authoring. It
  carries user semantics such as `names` and `base-trust-profiles`.
- **`LlmTrustProfile`** (a.k.a. the runtime/composed `AgentTrustProfile`) — the effective,
  composed form used for execution. It strips user semantics (`names`,
  `base-trust-profiles`) and keeps only the effective execution policy.

The persisted entity schema fields are:

- `base-trust-profiles` — zero or more base profiles to inherit from. Each entry is either a
  bare reference (inherited restrictively) or an object `{ "profile": <ref>,
  "inheritance-mode": "restrictive" | "permissive" }`.
- `allowed-client-instances` — the client instances (computers) this profile explicitly
  permits; `"."` denotes the local client instance, `"*"` denotes all machines. **Optional**:
  absent means "open at this level" (universe — no machine restriction added here). An empty
  array means "deny all machines at this level". **[Renamed from
  `hosting-workspaces-client-instances`]**
- `denied-client-instances` — the client instances this profile explicitly denies, regardless
  of the allowed set. **Optional**: absent means no additional denies at this level. **[New]**
- `default-execution-target` — a `$connection` descriptor specifying the specific machine to
  use at execution time. **Optional**: absent means no default set by this profile. **[New]**
- `mount-points` — container mount declarations (bind / volume / tmpfs, read-only / read-write).
- `network-access-policy` — `no-network` / `local-network` / `natted-network` / `host-network`.
- `https-proxy-policy` — `disabled` / `required` / `optional` with optional `proxy-url` and
  `credentials-reference`.
- `allowed-mcp-tool-call-schemas` — one or more JSON Schemas; effective policy composes them
  with `anyOf`.
- `restricted-mcp-tool-call-schemas` — one or more JSON Schemas explicitly denied; composed
  independently of allowed schemas (their `anyOf` is negated).

## Authoring references

Trust profiles can be referenced from an agent or tool definition in three ways:

### By entity name

```json
"trust-profile": { "$ref": { "entity-name": ["trust-profiles", "my-trust-profile"] } }
```

### By entity-id

```json
"trust-profile": { "$ref": { "entity-id": "7a1d9c20-1111-4aaa-8bbb-000000000001" } }
```

### Inline (by value)

```json
"trust-profile": {
  "allowed-client-instances": ["."],
  "denied-client-instances": ["untrusted-machine"],
  "default-execution-target": { "type": "local" },
  "base-trust-profiles": [
    {
      "profile": { "$ref": { "entity-name": ["trust-profiles", "base-os-policy"] } },
      "inheritance-mode": "restrictive"
    },
    {
      "profile": { "$ref": { "entity-name": ["trust-profiles", "extra-network-grant"] } },
      "inheritance-mode": "permissive"
    }
  ]
}
```

Entity references are always entity-name arrays (for example
`["trust-profiles", "my-trust-profile"]`), never slash-delimited strings. An inline profile
may carry `base-trust-profiles` references to entity-backed profiles; the
`EntityTrustProfileProvider` resolves those bases. The inline profile's own fields are applied
as the "outermost" (deriving) layer when composing.

## Machine execution policy

This section defines the semantics of the two machine-set fields and how they compose.

### `allowed-client-instances` (optional)

Specifies the set of Workspaces client instance identifiers on which the agent or tool **may**
run:

- `"."` — the local (current) client instance.
- `"*"` — any client instance (all machines).
- Any other string — a specific remote client instance identifier.

**Absent (`allowed-client-instances` not specified)**: treated as the **universe** — no machine
restriction is added by this profile level. The profile is "open at this level".

**Empty array (`allowed-client-instances: []`)**: an **empty set** — no machine is permitted by
this profile level. This effectively prevents execution on any machine (unless a permissive base
widens the set).

### `denied-client-instances` (optional) **[New]**

Specifies client instances that are **explicitly denied** regardless of the allowed set:

**Absent (`denied-client-instances` not specified)**: no additional denies at this level.

**Non-empty array**: the listed client instances are prohibited even if they appear in
`allowed-client-instances`.

### Effective allowed set

The effective allowed set for a single profile level (before composition with bases) is:

```
effective-allowed = (allowed-client-instances ?? universe) \ denied-client-instances
```

Where `\` denotes set subtraction, `??` means "use universe if absent", and
`denied-client-instances` defaults to the empty set if absent.

### Unspecified field semantics

| Field | Absent means | Empty array means |
|---|---|---|
| `allowed-client-instances` | Universe (open at this level) | Empty set (deny all machines at this level) |
| `denied-client-instances` | No additional denies at this level | No additional denies at this level (same as absent) |

### Deny-only profiles **[New]**

A profile with `denied-client-instances` present but `allowed-client-instances` absent is a
**deny-only profile**. Its effective allowed set is `universe \ denied-client-instances`. This
profile does not claim to restrict the allowed set — it only adds denies.

When a deny-only profile Q is composed **restrictively** with a profile P that has
`allowed-client-instances: ["A", "B", "C"]`:

| | Profile P | Deny-only Q | Restrictive P ∩ Q |
|---|---|---|---|
| `allowed-client-instances` | `["A","B","C"]` | absent (= universe) | `["A","B","C"]` |
| `denied-client-instances` | absent (= {}) | `["D"]` | `["D"]` |
| effective-allowed | {A, B, C} | universe \ {D} | {A,B,C} \ {D} = {A,B,C} |

D was not in P's allowed set so it makes no difference here — but if D had also been in P's
`allowed-client-instances`, it would be removed from the result.

### Allow-and-deny in the same profile

A profile may specify both `allowed-client-instances` and `denied-client-instances`. The denied
instances are always removed from the effective allowed set, even if they also appear in the
allowed list. The explicit deny is preserved in the composed denied set for subsequent
compositions. Example:

```json
{
  "allowed-client-instances": [".", "machine-A", "machine-B", "machine-C"],
  "denied-client-instances": ["machine-D"]
}
```

Effective allowed set: `{., A, B, C}` (D excluded, even though D was not in the allowed list —
the deny is still propagated for composition).

### Composition under restrictive mode

| Field | Restrictive result |
|---|---|
| `allowed-client-instances` | Intersection of the two effective allowed sets |
| `denied-client-instances` | Union of the two denied sets |

### Composition under permissive mode

| Field | Permissive result |
|---|---|
| `allowed-client-instances` | Union of the two effective allowed sets |
| `denied-client-instances` | Intersection of the two denied sets |

## Execution target specificity **[New]**

The trust profile defines the **policy** — which machines are allowed and which are denied.
The trust profile does **not** automatically select a machine to run on; it validates a proposed
target. The **specific execution target** (one concrete machine) must be resolved separately.

### `default-execution-target` field **[New]**

A trust profile may carry a `default-execution-target` field: a `$connection` descriptor that
names the specific machine to use when this profile is selected. Supported descriptor types:

| Type | Description |
|---|---|
| `{ "type": "local" }` | The current (local) Workspaces client instance (`"."`) |
| `{ "type": "user-computer-profile", "entity-id": "<uuid>" }` | A remote machine identified by a user-computer-profile entity |
| `{ "type": "http", "base-url": "...", ... }` | A direct HTTP-addressed remote machine |

If `default-execution-target` is absent from the composed profile, the agent or tool manifest
must supply an `execution-target` field directly — otherwise the runtime rejects session
construction.

### Override in agent / tool definition

An agent or tool definition may override the composed profile's `default-execution-target` with
its own `execution-target` field. The manifest's field takes precedence. This lets multiple
tools in the same agent each specify their preferred machine while sharing a common trust
profile.

### `ExecutionTargetResolver` **[New]**

At session construction time, `ExecutionTargetResolver` resolves the concrete `$connection`
descriptor to a **connection descriptor (`JsonElement`)**, which the caller then passes to
`ITransportFactoryRegistry.ConnectToAsync` to obtain an `ITransport`:

1. Use the `execution-target` from the manifest if present; otherwise use
   `default-execution-target` from the effective trust profile.
2. Validate that the resolved target is **permitted** by the effective allowed set and is **not**
   in the effective denied set. If validation fails, session construction is rejected.
3. Return the connection descriptor (`JsonElement`); the caller passes it to
   `ITransportFactoryRegistry.ConnectToAsync` to obtain the `ITransport`.

### Trust profile as policy, not selector

The trust profile does not select arbitrarily from the allowed set. It enforces that the
**chosen** target is permitted. An empty effective allowed set means no machine may be used;
any proposed target will fail validation and session construction is rejected at construction
time.

When an agent or toolset targets a non-local client instance, the connection is described by the
associated **user-computer-profile** entity. Each user-computer-profile reflects the
remote-access configuration for a Workspaces instance, which lets remote execution paths
determine how to connect to the remote computer (see `docs/design/devtunnels-web-access.md` for
the dev-tunnel transport).

Remote execution is abstracted behind `ITrustedExecutor`, which creates Llm.Core runtime
components by passing the agent / toolset configuration plus the resolved trust profile to the
remote server for its own interpretation of container definition, tools, etc.:

```text
ITrustedExecutor
  CreateAIAgent(agentDefinition /* includes trust profile */) : AIAgent
  CreateToolset(toolDefinition /* from agent definition */, trustProfile) : AIContextProvider
```

The `AIAgent` and `AIContextProvider` implementations returned for remote execution tunnel
via `ITransportFactoryRegistry.ConnectToAsync` → `ChatClientOverTransport` (unified transport
layer, dev-tunnel authenticated). `ITrustedExecutor` remains as a thin adapter
(`TransportTrustedExecutor`) during the transition period. For the local
client instance (`"."`), `ITrustedExecutor` constructs the components in-process.

## MCP rights

MCP tool-call schemas control which tool invocations an agent or tool is permitted to make.

### `allowed-mcp-tool-call-schemas`

One or more JSON Schema objects. Effective policy combines all schemas across the inheritance
chain into a single `anyOf` envelope. A tool call is permitted if and only if its envelope
validates against the composed `anyOf`. An empty policy (no schemas in the composed `anyOf`)
denies all tool calls.

### `restricted-mcp-tool-call-schemas`

One or more JSON Schema objects. Any tool call matching a restricted schema is rejected even
if it also matches an allowed schema. The composed restricted set produces:

```
allOf: [ allowed-envelope, { not: { anyOf: restricted } } ]
```

### Composition of MCP schemas **[New clarification]**

Both `allowed-mcp-tool-call-schemas` and `restricted-mcp-tool-call-schemas` **always compose
additively (union) regardless of inheritance mode** — this behaviour is intentional and differs
from other fields:

- The **allowed** set always accumulates: a restrictive base cannot silently remove allowed
  schemas from a deriving profile (which would widen the effective policy by accident, because
  the combined envelope now covers more patterns).
- The **restricted** set always accumulates: a permissive base cannot silently remove deny
  schemas from a deriving profile (which would create a security gap by lifting an explicit
  denial).

**Rationale**: schemas are capability grants and capability revocations whose union must be
preserved under both modes to prevent silent policy errors. Allowing a restrictive base to
discard deny schemas could create security gaps; allowing a permissive base to discard allowed
schemas would produce confusing silent denials.

## Other properties

### `mount-points`

Container mount declarations. Each entry specifies:

| Field | Values |
|---|---|
| `source-path` | Host path or volume identifier |
| `target-path` | Container mount path |
| `access-mode` | `read-only` \| `read-write` |
| `type` | `bind` \| `volume` \| `tmpfs` |

Composition: restrictive intersects by `(source, target, type)` with read-only narrowing;
permissive unions with read-write widening.

### `network-access-policy`

| Value | Meaning |
|---|---|
| `no-network` | No network access |
| `local-network` | Local network only |
| `natted-network` | NAT-routed network |
| `host-network` | Full host network |

Composition: restrictive takes most restrictive; permissive takes most permissive.

### `https-proxy-policy`

| Field | Values |
|---|---|
| `mode` | `disabled` \| `required` \| `optional` |
| `proxy-url` | Required for `required` / `optional` modes |
| `credentials-reference` | Optional reference to credentials entity |

Composition: restrictive takes strongest requirement (`required` > `optional` > `disabled`);
permissive takes weakest.

## Composition rules

Complete composition table across both inheritance modes for all fields:

| Field | Restrictive | Permissive |
|---|---|---|
| `allowed-client-instances` **[Updated]** | Intersection of effective allowed sets | Union of effective allowed sets |
| `denied-client-instances` **[New]** | Union of denied sets | Intersection of denied sets |
| `default-execution-target` **[New]** | Primary profile's target takes precedence | Base profile's target may be adopted; primary wins on conflict |
| `mount-points` | Intersection by `(source, target, type)`, read-only narrows | Union by `(source, target, type)`, read-write widens |
| `network-access-policy` | Most restrictive (`no-network` < `local-network` < `natted-network` < `host-network`) | Most permissive |
| `https-proxy-policy` | Strongest requirement (`required` > `optional` > `disabled`) | Weakest requirement |
| `allowed-mcp-tool-call-schemas` | Union (additive, mode-independent) | Union (additive, mode-independent) |
| `restricted-mcp-tool-call-schemas` | Union (additive, mode-independent) | Union (additive, mode-independent) |

Additional composition rules:

1. A profile may inherit from zero or more base profiles (`base-trust-profiles`), each with an
   inheritance mode (`restrictive` by default, or `permissive`).
2. Effective fields are produced by deterministic merge rules per the table above.
3. Each merge operation is commutative, so the order of bases does not affect the result; mixing
   modes across bases applies each base's mode in turn.
4. Cycles in `base-trust-profiles` inheritance are invalid.
5. The runtime/composed form strips `names` and `base-trust-profiles`. Entity references in
   `default-execution-target` are resolved to concrete transport descriptors.

## Local trust profile definition **[New]**

### Default trust profile entities shipped with PW

Phantom Workspaces ships five built-in trust profile entities in
`Phantom.Workspaces.Data.Core/JsonEntities/defaults/trust-profiles/`:

| Entity name | Entity-id | Description |
|---|---|---|
| `trust-profiles/all-machines` | `7a1d9c20-1111-4aaa-8bbb-000000000002` | Permits all client instances (`"*"`). No mounts, no network, no proxy. Allows any tool call. Useful as a permissive machine-set base. |
| `trust-profiles/current-machine` | `7a1d9c20-1111-4aaa-8bbb-000000000001` | Permits only the local client instance (`"."`). No mounts, no network, no proxy. Allows any tool call. |
| `trust-profiles/no-tool` | `7a1d9c20-1111-4aaa-8bbb-000000000004` | Permits only the local machine. MCP tool-call schema is `{ "not": {} }` — denies all tool calls. |
| `trust-profiles/all-tools` | `7a1d9c20-1111-4aaa-8bbb-000000000003` | Permits only the local machine. MCP schema allows any tool call (permissive tool policy). |
| `trust-profiles/workspace-read-only` | `7a1d9c20-1111-4aaa-8bbb-000000000005` | Permits only the local machine. Allowed MCP schemas: `workspaces_entity_get` and `workspaces_entity_generate_guid`. Restricted MCP schema: `workspaces_entity_update` (explicitly denied). |

These profiles are composed or referenced in agent manifests as bases rather than used directly;
they provide building blocks for common policy combinations.

### Inline (by-value) trust profiles in agent manifests

An agent manifest may embed a full trust profile definition directly in the `trust-profile`
field:

```json
"trust-profile": {
  "allowed-client-instances": ["."],
  "denied-client-instances": ["untrusted-machine"],
  "default-execution-target": { "type": "local" },
  "network-access-policy": "no-network",
  "https-proxy-policy": { "mode": "disabled" },
  "mount-points": [],
  "allowed-mcp-tool-call-schemas": [
    { "properties": { "toolName": { "type": "string" } } }
  ]
}
```

An inline profile may also carry `base-trust-profiles` pointing to entity-backed profiles:

```json
"trust-profile": {
  "allowed-client-instances": ["."],
  "base-trust-profiles": [
    {
      "profile": { "$ref": { "entity-name": ["trust-profiles", "workspace-read-only"] } },
      "inheritance-mode": "restrictive"
    }
  ]
}
```

The inline profile's own fields form the outermost (deriving) layer. Entity-backed bases are
resolved by `EntityTrustProfileProvider` and composed per their `inheritance-mode`.

### Entity-referenced profiles

An agent or tool definition may reference a trust profile stored in the PW entity store:

**By entity name:**
```json
"trust-profile": { "$ref": { "entity-name": ["trust-profiles", "workspace-read-only"] } }
```

**By entity-id:**
```json
"trust-profile": { "$ref": { "entity-id": "7a1d9c20-1111-4aaa-8bbb-000000000005" } }
```

### How `EntityTrustProfileProvider` resolves entity chains

`EntityTrustProfileProvider` implements `ITrustProfileProvider` backed by the entity
`IDataAccessLayer`. Resolution proceeds as follows:

1. Look up the referenced entity (by name or entity-id) from the DAL.
2. For each base in `base-trust-profiles`:
   a. Recursively resolve the base entity (cycle detection enforced — a cycle is a hard error).
   b. Compose the base into the current profile using its declared `inheritance-mode`
      (`restrictive` by default).
3. Return the fully composed `LlmTrustProfileEntity`.

Inline profiles in manifests drive the same composition path: their `$ref` bases are resolved
through `EntityTrustProfileProvider`, and the inline profile's own fields are applied as the
outermost layer after all bases have been composed.

## Resolution

An `ITrustProfileProvider` resolves a trust profile by name (entity reference) into an
`LlmTrustProfileEntity`, then composes it (and its bases) into the effective runtime
`LlmTrustProfile`. `AgentChat` uses an injected `ITrustProfileProvider` to resolve `$ref`
trust profiles found in an agent definition before constructing execution environments.

Inline (by-value) trust profiles are composed directly; only their `$ref` bases require the
provider.

The `ExecutionTargetResolver` then resolves the concrete `$connection` descriptor (from
`default-execution-target` or the manifest's `execution-target`) and validates it against the
effective machine policy. **[New]**

## Enforcement

Trust enforcement happens at the layer responsible for it, not eagerly at parse time:

1. **Computer set** — the `ExecutionTargetResolver` validates the chosen execution target
   against the effective `allowed-client-instances` (minus `denied-client-instances`). A target
   not in the effective allowed set, or present in the effective denied set, causes session
   construction to be rejected. **[Updated for deny set]**
2. **Tool-call policy** — MCP tool calls are validated against the composed
   `allowed-mcp-tool-call-schemas` (`anyOf`); calls whose payload does not validate are
   rejected. A call matching any `restricted-mcp-tool-call-schemas` entry is also rejected.
3. **Container policy** — `DockerContainerTrustProfileMaterializer` converts the effective
   profile into container-enforced settings (mounts with `:ro`/`:rw`, network mode, proxy,
   process allowlist), written once at container start and treated as immutable for that
   container instance (see `docs/design/llm-session.md`).

Transport-level authentication (direct web auth or dev-tunnel gate) only identifies the
caller; DAL-level and tool-level authorization is still enforced server-side. Client
assertions are never trusted for identity or access rights.

## Implementation status

Implemented (`Phantom.Workspaces.Llm.Core/Trust/`):

1. Runtime model (`TrustProfile.cs`): `TrustProfile` (composed), `TrustProfileDefinition`
   (entity-level), and value types `TrustMountPoint`, `TrustHttpsProxyPolicy`, and the
   `TrustNetworkAccessPolicy` / `TrustMountAccessMode` / `TrustMountType` /
   `TrustHttpsProxyMode` enums. `TrustProfile.AllowsClientInstance` /
   `AllowsLocalExecution` express the computer-set check. **Pending: update to evaluate
   `denied-client-instances` in addition to `allowed-client-instances`. [New]**
2. Composition (`TrustProfileComposer.cs`): `Merge(primary, other, mode)` supports both
   `Restrictive` (intersect client instances, most-restrictive network, intersect mounts with
   read-only narrowing, strongest proxy) and `Permissive` (union client instances, most-permissive
   network, union mounts with read-write widening, weakest proxy). MCP tool-call schemas are
   composed in two independent lists: **allowed** schemas compose into one additive `anyOf`
   envelope, and **restricted** (deny) schemas compose into their own additive `anyOf`. `Finalize`
   produces the runtime `TrustProfile` whose effective schema is the allowed envelope,
   and—when any restricted schema is present—`allOf: [ allowed-envelope, { not: { anyOf:
   restricted } } ]`, so a tool call matching any restricted schema is rejected even if it also
   matches an allowed schema. Both lists accumulate (union) across inheritance regardless of mode.
   `Compose(list)` keeps restrictive behavior. Order-independent (each merge is commutative).
   **Pending: extend to compose `denied-client-instances` sets (union for restrictive,
   intersection for permissive). [New]**
3. Resolution (`TrustProfileEntityReader.cs`, `ITrustProfileProvider.cs`,
   `DictionaryTrustProfileProvider.cs`): parses persisted `llm-trust-profile` entity JSON into
   a `TrustProfileEntity` whose `Bases` carry a `TrustProfileBaseReference` (name + mode), and
   resolves a profile by name — recursively composing each base into the deriving profile per its
   mode (cycle-detected). Covered by `TrustProfileResolutionTests`. **Pending: read
   `allowed-client-instances` (renamed), `denied-client-instances`, and
   `default-execution-target` from entity JSON. [New]**
4. Execution seam (`ITrustedExecutor.cs`, `TrustedExecutorSelector.cs`,
   `LocalTrustedExecutor.cs`, `TrustToolCallAuthorizer.cs`): the layered execution interface.
   - `ITrustedExecutor` is implemented at the right layers — `LocalTrustedExecutor` in
     **Llm.Core** for local execution (containers, processes, and tool permissions), and
     `TransportTrustedExecutor` in **Phantom.Workspaces** for cross-machine remoting.
   - `TrustedExecutorSelector` enforces the profile's computer set and selects the executor
     whose `CanExecute` matches the target client instance. **Pending: update
     `TrustedExecutorSelector` to also check `denied-client-instances`. [New]**
   - `TrustToolCallAuthorizer` validates MCP tool-call envelopes (`{ toolName, input }`)
     against the composed `anyOf` schema; an empty policy denies all tool calls.
   Covered by `TrustedExecutorTests` and `TransportTrustedExecutorTests`.
5. Remoting transport (`TransportTrustedExecutor.cs`): `TransportTrustedExecutor` builds a thin
   local agent shell whose `IChatClient` (`ChatClientOverTransport`) relays the conversation to a
   remote host via the transport layer (`ITransportFactoryRegistry` → `ChatClientOverTransport`).
   The remote host performs the trusted execution via its own `LocalTrustedExecutor`.
6. **Execution target resolution** (`ExecutionTargetResolver.cs`): **Pending: new class.**
   Resolves `default-execution-target` / manifest `execution-target` `$connection` descriptors
   to a connection descriptor (`JsonElement`); the caller then passes it to
   `ITransportFactoryRegistry.ConnectToAsync` to obtain the `ITransport`. Validates the chosen
   target against the effective machine policy. **[New]**

Pending: installation setup-wizard / settings view models.

The server-side endpoint is now served via the unified transport layer
(`ITransportFactoryRegistry` → `ChatClientOverTransport`): the remote host receives the agent
definition, runs a turn via `AgentFactory`, and returns the `ChatResponse`. Covered by
`AgentRespondHandlerTests`.

Trust-profile resolution is wired into agent construction (`AgentTrustProfileResolver`,
`CreateAgentChatRequest.TrustProfileProvider`): an agent definition references a profile via
`Metadata["trust-profile"]`; `AgentFactory.CreateAgentChatAsync` resolves it and refuses
construction when the profile does not permit local execution (`"."`). Covered by
`AgentTrustProfileWiringTests`.

## New classes

1. `ITrustProfileProvider`
   - Resolves a trust-profile entity reference and composes it (plus bases) into a runtime
     `LlmTrustProfile`.
2. `EntityTrustProfileProvider : ITrustProfileProvider`
   - Default provider backed by the entity `IDataAccessLayer`.
3. `TrustProfileComposer`
   - Applies restrictive or permissive merge rules over a profile and its resolved bases.
4. `ITrustedExecutor`
   - Creates `AIAgent` / `AIContextProvider` instances for a given agent definition + trust
     profile, locally or over the web transport.
5. `LocalTrustedExecutor : ITrustedExecutor`
   - In-process executor for the local client instance (`"."`).
6. `TransportTrustedExecutor : ITrustedExecutor`
   - Adapter that implements `ITrustedExecutor` by delegating to `ITransportFactoryRegistry`;
     created in T11 of the unified transport plan. Tunnels agent / toolset construction via
     `ITransportFactoryRegistry.ConnectToAsync` → `ChatClientOverTransport` during the
     transition period.
7. `ExecutionTargetResolver` **[New]**
   - Resolves `$connection` descriptors to a connection descriptor (`JsonElement`); validates the
     chosen target against the effective allowed and denied sets from the composed trust profile.
     The caller passes the returned descriptor to `ITransportFactoryRegistry.ConnectToAsync` to
     obtain the `ITransport`.

## Key integration points

1. `AgentChat` construction
   - Resolves `$ref` trust profiles via `ITrustProfileProvider` and selects an
     `ITrustedExecutor` based on the effective `allowed-client-instances` minus
     `denied-client-instances`. **[Updated]**
2. Agent definition model
   - `trust-profile` (inline or `$ref`) carried on the agent definition and per-tool
     definitions; interpreted only by `AgentFactory` / executor construction.
   - `execution-target` **[New]**: optional per-definition override for the trust profile's
     `default-execution-target`.
3. The unified transport layer (`ITransportFactoryRegistry` / `ChatClientOverTransport`)
   - Transport for `TransportTrustedExecutor`; dev-tunnel authentication gates the connection.
4. Docker materialization (`docs/design/llm-session.md`)
   - `DockerContainerTrustProfileMaterializer` consumes the composed `LlmTrustProfile`.
5. user-computer-profile entities
   - Supply remote connection settings used by `TransportTrustedExecutor` and resolved by
     `ExecutionTargetResolver`.

## Test tasks

1. Composition tests
   - Restrictive inheritance intersects the computer set and narrows network/mounts/proxy. ✅
   - Permissive inheritance unions the computer set and widens network/mounts/proxy. ✅
   - Mixed-mode bases apply each base's mode. ✅
   - `allowed-mcp-tool-call-schemas` compose with `anyOf`. ✅
   - Inheritance cycles are rejected. ✅
   - **[New]** Restrictive inheritance unions `denied-client-instances`.
   - **[New]** Permissive inheritance intersects `denied-client-instances`.
   - **[New]** Deny-only profile (absent `allowed-client-instances`) composes correctly under
     restrictive mode: allowed set is preserved, denied set is unioned.
   - **[New]** Allow-and-deny in same profile: denied instance is excluded from effective
     allowed set.
   - **[New]** Absent `allowed-client-instances` treated as universe in composition.
   - **[New]** Empty `allowed-client-instances: []` treated as empty set (deny all machines).
2. Resolution tests
   - `ITrustProfileProvider` resolves `$ref` profiles and inline profiles.
   - The reader parses per-base `inheritance-mode` (object form and bare-string default). ✅
   - Missing referenced profiles surface a clear error at the resolution layer. ✅
   - **[New]** Reader parses `allowed-client-instances`, `denied-client-instances`, and
     `default-execution-target` from entity JSON.
3. Executor selection tests
   - Local client instance (`"."`) selects `LocalTrustedExecutor`.
   - Non-local instance selects `TransportTrustedExecutor` using the matching user-computer-profile.
   - **[New]** A machine in `denied-client-instances` is rejected even if also in
     `allowed-client-instances`.
4. Enforcement tests
   - Running on a client instance outside the effective allowed set is refused.
   - Tool calls failing the composed MCP schema are rejected.
   - **[New]** Running on a denied client instance is refused at session construction time.
   - **[New]** A trust profile with empty effective allowed set rejects any proposed execution
     target.
5. Materialization tests
   - Effective profile maps to expected Docker mount / network / proxy enforcement (extends
     existing `llm-session` materialization coverage).
6. **[New]** Execution target resolution tests
   - `ExecutionTargetResolver` resolves `{ "type": "local" }` to a connection descriptor passed to `ITransportFactoryRegistry.ConnectToAsync`.
   - `ExecutionTargetResolver` resolves `{ "type": "user-computer-profile", "entity-id": "..." }`
     to a connection descriptor; `ITransportFactoryRegistry.ConnectToAsync` returns the correct remote transport.
   - `ExecutionTargetResolver` rejects a target not in the effective allowed set.
   - `ExecutionTargetResolver` rejects a target in the effective denied set.
   - Manifest `execution-target` overrides `default-execution-target` from the trust profile.
   - Session construction fails when neither the manifest nor the trust profile supplies an
     execution target.

## Non-goals

1. Trusting client-side identity or access assertions.
2. Per-file-extension deny rules inside a mounted path (express denies by mounting narrower
   allow scopes instead; see `docs/design/llm-session.md`).
