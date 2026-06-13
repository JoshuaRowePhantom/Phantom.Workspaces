# Trust models

## Purpose

Each tool and each whole agent definition can run associated with a **trust profile**.
A trust profile defines:

- The set of Workspaces client instances (computers) that the agent / tool may run on.
- The permissions allowed to that agent / tool at the **tool execution** level (MCP tool-call
  schema policy).
- The OS / container permissions allowed to that agent / tool at the **container** level
  (mounts, network access, HTTPS proxy policy).

A trust profile can inherit other trust profiles, so that one profile can set tool
permissions, another can set OS permissions, and another can restrict the computer set.
**All inheritance is restrictive**: composing profiles can only narrow the effective policy,
never widen it.

This document describes the trust *model* — how trust profiles are authored, composed, and
enforced across local and remote execution. It builds on two existing pieces:

- The persisted entity schema in
  `Phantom.Workspaces.Data.Core/JsonSchemas/llm-trust-profile.json`
  (documented in `docs/design/llm-trust-profile.md` and
  `Phantom.Workspaces.Data.Core/JsonEntities/documentation/llm-trust-profile-schema.md`).
- The runtime trust-profile types and Docker materialization described in
  `docs/design/llm-session.md`.

## Implementation status

Implemented (`Phantom.Workspaces.Llm.Core/Trust/`):

1. Runtime model (`TrustProfile.cs`): `TrustProfile` (composed), `TrustProfileDefinition`
   (entity-level), and value types `TrustMountPoint`, `TrustHttpsProxyPolicy`, and the
   `TrustNetworkAccessPolicy` / `TrustMountAccessMode` / `TrustMountType` /
   `TrustHttpsProxyMode` enums. `TrustProfile.AllowsClientInstance` /
   `AllowsLocalExecution` express the computer-set check.
2. Restrictive composition (`TrustProfileComposer.cs`): client instances intersect, network
   access takes the most restrictive policy, mount points intersect (read-only narrowing),
   HTTPS proxy takes the strongest requirement, and MCP tool-call schemas compose into one
   `anyOf` envelope. Order-independent.

Covered by `TrustProfileComposerTests`.

3. Resolution (`TrustProfileEntityReader.cs`, `ITrustProfileProvider.cs`,
   `DictionaryTrustProfileProvider.cs`): parses persisted `llm-trust-profile` entity JSON into
   a `TrustProfileEntity`, and resolves a profile by name — flattening transitive bases
   (depth-first, cycle-detected) and composing them restrictively. Covered by
   `TrustProfileResolutionTests`.
4. Execution seam (`ITrustedExecutor.cs`, `TrustedExecutorSelector.cs`,
   `LocalTrustedExecutor.cs`, `TrustToolCallAuthorizer.cs`): the layered execution interface.
   - `ITrustedExecutor` is implemented at the right layers — `LocalTrustedExecutor` in
     **Llm.Core** for local execution (containers, processes, and tool permissions), and
     `RemoteTrustedExecutor` in **Phantom.Workspaces** for cross-machine remoting.
   - `TrustedExecutorSelector` enforces the profile's computer set and selects the executor
     whose `CanExecute` matches the target client instance.
   - `TrustToolCallAuthorizer` validates MCP tool-call envelopes (`{ toolName, input }`)
     against the composed `anyOf` schema; an empty policy denies all tool calls.
   Covered by `TrustedExecutorTests` and `RemoteTrustedExecutorTests`.
5. Remoting transport (`Phantom.Workspaces/Trust/WebRemoteChatClient.cs`,
   `RemoteTrustedExecutor.cs`): `RemoteTrustedExecutor` builds a thin local agent shell whose
   `IChatClient` (`WebRemoteChatClient`) relays the conversation to a remote host's
   `POST /agent/respond` endpoint (with optional `X-Tunnel-Authorization`). The remote host
   performs the trusted execution via its own `LocalTrustedExecutor`.

Pending: installation setup-wizard / settings view models.

The server-side `POST /agent/respond` endpoint is implemented on
`Phantom.Workspaces.Web.Server` (`AgentRespondHandler` + `AgentEndpointRouteBuilderExtensions`):
it parses the agent definition, runs a turn via `AgentFactory`, and returns the
`ChatResponse`. Covered by `AgentRespondHandlerTests`.

Trust-profile resolution is wired into agent construction (`AgentTrustProfileResolver`,
`CreateAgentChatRequest.TrustProfileProvider`): an agent definition references a profile via
`Metadata["trust-profile"]`; `AgentFactory.CreateAgentChatAsync` resolves it and refuses
construction when the profile does not permit local execution (`"."`). Covered by
`AgentTrustProfileWiringTests`.

## Entity vs runtime forms

Trust profiles follow the same entity / runtime split used elsewhere in the codebase:

- **`LlmTrustProfileEntity`** — the persisted, user-semantic form used for authoring. It
  carries user semantics such as `names` and `base-trust-profiles`.
- **`LlmTrustProfile`** (a.k.a. the runtime/composed `AgentTrustProfile`) — the effective,
  composed form used for execution. It strips user semantics (`names`,
  `base-trust-profiles`) and keeps only the effective execution policy.

The persisted entity schema fields are:

- `base-trust-profiles` — zero or more trust-profile entity references to inherit from.
- `hosting-workspaces-client-instances` — the client instances (computers) this profile may
  run on; `"."` denotes the local client instance.
- `mount-points` — container mount declarations (bind / volume / tmpfs, read-only / read-write).
- `network-access-policy` — `no-network` / `local-network` / `natted-network` / `host-network`.
- `https-proxy-policy` — `disabled` / `required` / `optional` with optional `proxy-url` and
  `credentials-reference`.
- `allowed-mcp-tool-call-schemas` — one or more JSON Schemas; effective policy composes them
  with `anyOf`.

## Authoring references

Trust profiles are entities and can be referenced from an agent definition by name:

```json
"trust-profile": { "$ref": { "entity-name": ["trust-profiles", "my-trust-profile"] } }
```

Or inline by value, optionally inheriting a base profile:

```json
"trust-profile": {
  "hosting-workspaces-client-instances": ["."],
  "base-trust-profiles": [
    { "$ref": { "entity-name": ["trust-profiles", "base-trust-profile"] } }
  ]
}
```

Entity references are always entity-name arrays (for example
`["trust-profiles", "my-trust-profile"]`), never slash-delimited strings.

## Composition rules

1. A profile may inherit from zero or more base profiles (`base-trust-profiles`).
2. Effective `hosting-workspaces-client-instances`, `mount-points`, `network-access-policy`,
   `https-proxy-policy`, and MCP tool-call schema are produced by deterministic merge rules.
3. Composition is **restrictive**:
   - The effective computer set is the **intersection** of all inherited and local sets.
   - Mount points narrow (a base read-write mount may be restricted to read-only by a derived
     profile; new broad grants are not introduced by inheritance).
   - Network access composes down to the most restrictive policy.
   - `allowed-mcp-tool-call-schemas` are composed with `anyOf` at runtime to validate the
     tool-call envelope.
4. Cycles in `base-trust-profiles` inheritance are invalid.
5. The runtime/composed form strips `names` and `base-trust-profiles`.

## Resolution

An `ITrustProfileProvider` resolves a trust profile by name (entity reference) into an
`LlmTrustProfileEntity`, then composes it (and its bases) into the effective runtime
`LlmTrustProfile`. `AgentChat` uses an injected `ITrustProfileProvider` to resolve `$ref`
trust profiles found in an agent definition before constructing execution environments.

Inline (by-value) trust profiles are composed directly; only their `$ref` bases require the
provider.

## Local vs remote execution

When an agent or toolset is assigned to a non-local client instance, the connection is
described by the associated **user-computer-profile** entity. Each user-computer-profile
reflects the remote-access configuration for a Workspaces instance, which lets remote
execution paths determine how to connect to the remote computer (see
`docs/design/devtunnels-web-access.md` for the dev-tunnel transport).

Remote execution is abstracted behind `ITrustedExecutor`, which creates Llm.Core runtime
components by passing the agent / toolset configuration plus the resolved trust profile to the
remote server for its own interpretation of container definition, tools, etc.:

```text
ITrustedExecutor
  CreateAIAgent(agentDefinition /* includes trust profile */) : AIAgent
  CreateToolset(toolDefinition /* from agent definition */, trustProfile) : AIContextProvider
```

The `AIAgent` and `AIContextProvider` implementations returned for remote execution tunnel
over the web client / server connection (`Phantom.Workspaces.Data.Web.Client` /
`Phantom.Workspaces.Data.Web.Server` transport, dev-tunnel authenticated). For the local
client instance (`"."`), `ITrustedExecutor` constructs the components in-process.

## Enforcement

Trust enforcement happens at the layer responsible for it, not eagerly at parse time:

1. **Computer set** — the executor refuses to run an agent / toolset on a client instance not
   present in the effective `hosting-workspaces-client-instances`.
2. **Tool-call policy** — MCP tool calls are validated against the composed
   `allowed-mcp-tool-call-schemas` (`anyOf`); calls whose payload does not validate are
   rejected.
3. **Container policy** — `DockerContainerTrustProfileMaterializer` converts the effective
   profile into container-enforced settings (mounts with `:ro`/`:rw`, network mode, proxy,
   process allowlist), written once at container start and treated as immutable for that
   container instance (see `docs/design/llm-session.md`).

Transport-level authentication (direct web auth or dev-tunnel gate) only identifies the
caller; DAL-level and tool-level authorization is still enforced server-side. Client
assertions are never trusted for identity or access rights.

## New classes

1. `ITrustProfileProvider`
   - Resolves a trust-profile entity reference and composes it (plus bases) into a runtime
     `LlmTrustProfile`.
2. `EntityTrustProfileProvider : ITrustProfileProvider`
   - Default provider backed by the entity `IDataAccessLayer`.
3. `TrustProfileComposer`
   - Applies the restrictive merge rules over a profile and its resolved bases.
4. `ITrustedExecutor`
   - Creates `AIAgent` / `AIContextProvider` instances for a given agent definition + trust
     profile, locally or over the web transport.
5. `LocalTrustedExecutor : ITrustedExecutor`
   - In-process executor for the local client instance (`"."`).
6. `RemoteTrustedExecutor : ITrustedExecutor`
   - Tunnels agent / toolset construction over the web client / server connection.

## Key integration points

1. `AgentChat` construction
   - Resolves `$ref` trust profiles via `ITrustProfileProvider` and selects an
     `ITrustedExecutor` based on the effective `hosting-workspaces-client-instances`.
2. Agent definition model
   - `trust-profile` (inline or `$ref`) carried on the agent definition and per-tool
     definitions; interpreted only by `AgentFactory` / executor construction.
3. `Phantom.Workspaces.Data.Web.Client` / `Phantom.Workspaces.Data.Web.Server`
   - Transport for `RemoteTrustedExecutor`; dev-tunnel authentication gates the connection.
4. Docker materialization (`docs/design/llm-session.md`)
   - `DockerContainerTrustProfileMaterializer` consumes the composed `LlmTrustProfile`.
5. user-computer-profile entities
   - Supply remote connection settings used by `RemoteTrustedExecutor`.

## Test tasks

1. Composition tests
   - Inheritance intersects the computer set restrictively.
   - Mount points / network policy / proxy policy narrow (never widen) under inheritance.
   - `allowed-mcp-tool-call-schemas` compose with `anyOf`.
   - Inheritance cycles are rejected.
2. Resolution tests
   - `ITrustProfileProvider` resolves `$ref` profiles and inline profiles.
   - Missing referenced profiles surface a clear error at the resolution layer.
3. Executor selection tests
   - Local client instance (`"."`) selects `LocalTrustedExecutor`.
   - Non-local instance selects `RemoteTrustedExecutor` using the matching
     user-computer-profile.
4. Enforcement tests
   - Running on a client instance outside the effective computer set is refused.
   - Tool calls failing the composed MCP schema are rejected.
5. Materialization tests
   - Effective profile maps to expected Docker mount / network / proxy enforcement
     (extends existing `llm-session` materialization coverage).

## Non-goals

1. Widening permissions through inheritance (composition is restrictive only).
2. Trusting client-side identity or access assertions.
3. Per-file-extension deny rules inside a mounted path (express denies by mounting narrower
   allow scopes instead; see `docs/design/llm-session.md`).
