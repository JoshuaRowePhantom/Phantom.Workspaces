# Session context tools

> Cross-link: the `[remote-copilot-sdk]` topology described in
> `docs/design/remote-chat-client-session.md` relies on the "host follows the
> current run" rule documented here — `host-profile-entity-id` on the persisted
> `agent-session` is a resume hint, not the runtime source of truth. The remote
> topology reconstructs the host from the resolved `trust-profile` at each run.

## Purpose

Add agent-facing tools that let an LLM inspect the **session it is running in**. The first is
**`get_current_session`**, which returns the session's profile context: the **agent-session**
entity, the **user-computer-profile** hosting it, the **user**, and the **agent-definition**
the session runs. This grounds the agent in "who am I, who is the user, what machine/profile,
and what definition" without the user having to restate it.

These are LLM-facing tools (`AIFunction`s exposed through an `AIContextProvider` toolset),
distinct from the scheduled `IWorkspaceTool` background tools (discovery/classifier).

## Background: how agent tools are wired

- A tool is an `AIFunction` (snake_case `Name`, `Description`, `JsonSchema`, `InvokeCoreAsync`)
  — e.g. `workspaces_entity_get`, `web_search`.
- Tools are grouped into an `AIContextProvider` subclass that returns them from
  `ProvideAIContextAsync` and exposes a **unique per-instance `StateKeys`** value (to avoid
  `ChatClientAgent` validation conflicts) — e.g. `WorkspaceEntityContextProvider`,
  `FilesystemServiceContextProvider`.
- Providers are produced by `ToolsetFactory.CreateNamedToolsetFactory("<kind>", …)`; an
  agent-definition's tool resources reference a toolset **kind** (e.g. `workspace-entity`,
  `filesystem`, `web_search`). `AgentServices` is passed to the factory at creation.

## What "current session" means (data model)

- The **agent-session** entity (`JsonSchemas/agent-session.json`) has `agent-session-id` and
  `host-profile-entity-id`. **Important:** `host-profile-entity-id` records where the session
  *was last hosted*; it is **not** authoritative for the current run, because a persisted
  session can be **resumed on a different machine / user-computer-profile**. The *current*
  profile and user therefore come from the **live host**, not from the session entity.
- The **user-computer-profile** (`user-computer-profile.json`) has `computer-reference` and
  `user-reference` (entity-name arrays) → resolves the **computer** and **user**.
- The **agent-definition** is the template the session was created from.

### Source of truth for "current"

| Member | Source |
| --- | --- |
| agent-session | resolved from the running `agent-session-id` (stable across resumes) |
| user-computer-profile | the **live host's current profile** (injected by the host) |
| user | the **live host's current user** (injected by the host) |
| agent-definition | the definition the host is currently running (injected by the host) |

So the profile/user are whatever the **Phantom.Workspaces host instance running the session
right now** uses — which is exactly correct when a session is resumed elsewhere.

### Schema change: link a session to its definition (resume support)

Add an optional `agent-definition-reference` (entity-name array) to `agent-session.json` so a
persisted session records which definition it runs, letting the host **reconstruct the
definition on resume** (and then inject it into the toolset context):

```json
"agent-definition-reference": {
  "type": "array",
  "minItems": 1,
  "items": { "type": "string" },
  "description": "Entity-name reference to the agent-definition this session runs."
}
```

This is bookkeeping for resume; the tool reads the definition from the host-provided context
(below), not directly from the session entity. References are entity-name arrays, never
slash-joined strings.

## The `get_current_session` tool

### Surface

- **Name:** `get_current_session`
- **Description:** "Return the current agent session's profile context: the agent-session,
  the current hosting user-computer-profile, the user, and the agent-definition."
- **Input schema:** no required arguments (empty object). Optionally a boolean
  `include_definition` (default true) and `include_profile` (default true) so callers can
  trim the payload; omitted means "include everything".
- **Result:** a JSON object with four nullable members, each an entity serialized in the same
  shape the workspace entity get tool uses (id, names, entity-types, data):


```json
{
  "agent_session": { ... } | null,
  "user_computer_profile": { ... } | null,
  "user": { ... } | null,
  "agent_definition": { ... } | null
}
```

Any member is `null` when that piece is absent (e.g. the host could not resolve a current
profile), rather than erroring — the tool reports what it can resolve.

### Resolution algorithm

Given the host-provided **current session context** (below):

1. Resolve the **agent-session** entity from the running `agent-session-id` (query by the
   `agent-session-id` field / the session's entity name).
2. Take the **user-computer-profile** and **user** from the **host-provided context** (the
   live host's current profile/user) — *not* from `host-profile-entity-id` on the session,
   which may reflect a different machine. The context may carry these as already-resolved
   entities (the host already holds them) or as ids the tool resolves.
3. Resolve the **agent-definition** from the host-provided `AgentDefinitionReference`.
4. Serialize each resolved entity and return the object above.

All steps are async over `IDataAccessLayer`; missing pieces short-circuit to `null` for that
member only.

### Supplying the current session context (host-created toolset factory)

The toolset's context comes from the **Phantom.Workspaces host**, not from data baked onto the
session or flowed through the agent-build plumbing. The host instance running the session
already knows its **current** computer, user-computer-profile, and user (the same live context
the scheduled tools receive), plus the `agent-session-id` and the agent-definition it is
launching/resuming. The host packages these into an immutable context:

```csharp
public sealed record CurrentSessionContext
{
    public required string AgentSessionId { get; init; }
    public EntitySnapshot? UserComputerProfile { get; init; } // the host's CURRENT profile
    public EntitySnapshot? User { get; init; }                // the host's CURRENT user
    public EntityName? AgentDefinitionReference { get; init; }
}
```

and creates a **session-scoped `IToolsetFactory`** closed over it:

```csharp
IToolsetFactory factory =
    ToolsetFactory.CreateCurrentSessionToolsetFactory(dataAccessLayer, currentSessionContext);
```

- The host **`Combine`s** this factory into the toolset-factory chain it passes to the agent
  builder (exactly how `CreateWorkspaceEntityToolsetFactory` is captured with its
  `dataAccessLayer`). The `current-session` toolset reads its context from this **closure**.
- Because the host **re-creates the factory on every start/resume**, a session resumed on a
  different machine naturally reports *that* host's current profile/user — fixing the resume
  problem. Nothing about the current profile is persisted on the session or carried through
  `AgentChat`/`AgentFactory`/`AgentServices`.
- If the host cannot resolve a current profile/user (headless/edge cases), it supplies a
  context with those members null; the toolset still registers and `get_current_session`
  returns nulls for the unresolved members.

### Toolset and registration

- New `CurrentSessionContextProvider : AIContextProvider` with a unique
  `StateKeys = $"current-session:{Guid.NewGuid():n}"`, constructed with the `IDataAccessLayer`
  and the `CurrentSessionContext`. `ProvideAIContextAsync` returns the single
  `GetCurrentSessionTool` (an `AIFunction`).
- New `ToolsetFactory.CreateCurrentSessionToolsetFactory(IDataAccessLayer, CurrentSessionContext, …)`
  registering kind **`current-session`**, closed over the context (no `AgentServices`/
  `AgentChat`/`AgentFactory` changes). The **host** is responsible for building the context and
  combining this factory in when it starts/resumes a session.
- An agent-definition opts in by listing a `current-session` tool resource (like it lists
  `workspace-entity` / `filesystem`).

## Source layout

In `Phantom.Workspaces.Llm.Core`:

- `CurrentSessionContext.cs` — the context record (built by the host).
- `CurrentSessionContextProvider.cs` — the provider + nested `GetCurrentSessionTool` AIFunction.
- `ToolsetFactory.cs` — add `CreateCurrentSessionToolsetFactory(IDataAccessLayer, CurrentSessionContext)`
  (closed over the context). **No** `AgentServices`/`AgentChat`/`AgentFactory` changes.

In the **Phantom.Workspaces host** (the application that starts/resumes chats):

- When starting or resuming a session, build a `CurrentSessionContext` from the live host
  state (current user-computer-profile entity, current user entity, the `agent-session-id`,
  and the agent-definition reference being run), create the `current-session` toolset factory,
  and `Combine` it into the toolset-factory chain used to build the agent.

In `Phantom.Workspaces.Data.Core`:

- `JsonSchemas/agent-session.json` — add `agent-definition-reference` (resume bookkeeping).

## Future session tools (same toolset)

The `current-session` toolset is the natural home for related, focused tools later, e.g.
`get_current_user` (just the user), `get_session_trust_profile`, or `list_session_tools`.
This design implements only `get_current_session`; the toolset is structured to host the rest.

## Testing strategy

The tool is pure resolution logic over `IDataAccessLayer` behind an `AIFunction`, so it is
almost entirely **unit-testable** with an in-memory data access layer and a hand-built
`CurrentSessionContext` — no network, no live agent, no timing.

### Unit tests (the bulk)

Use an in-memory/offline `IDataAccessLayer` seeded with an agent-session and an
agent-definition, and a hand-built `CurrentSessionContext` carrying the host's current
user-computer-profile and user entities (the host already holds these).

- **Happy path:** `get_current_session` returns all four members populated — agent-session
  resolved from the id, profile/user taken from the host context, definition resolved from the
  reference; the serialized shape matches the workspace entity get tool's shape.
- **Resume on a different profile (the key case):** two different `CurrentSessionContext`
  values (profile/user `P1`/`U1` vs. `P2`/`U2`) over the **same** `agent-session-id` produce
  results reporting `P1`/`U1` and `P2`/`U2` respectively — proving the current profile/user
  follow the **host**, not the session's stored `host-profile-entity-id`.
- **Ignores stale `host-profile-entity-id`:** seed the agent-session with a
  `host-profile-entity-id` pointing at `P1` but supply a host context with `P2`; the result is
  `P2` (the session's stored value is not used as the source of truth).
- **Missing pieces (graceful nulls):**
  - host context with null profile/user → `user_computer_profile` and `user` null.
  - host context with no `AgentDefinitionReference` → `agent_definition` null.
  - unknown `agent-session-id` → `agent_session` null (other members still from the host).
- **Argument flags:** `include_profile:false` / `include_definition:false` omit those members.
- **No-context registration:** when the host supplies a context with everything null (or the
  factory is created without a meaningful context), the provider still registers and the tool
  returns nulls with a short explanatory note.
- **Tool metadata:** `Name == "get_current_session"`, the input `JsonSchema` is the documented
  (no-required-args) schema, and the provider exposes exactly one tool with a unique
  `StateKeys`.

### Toolset/factory tests

- `ToolsetFactory.CreateCurrentSessionToolsetFactory(dal, context)` returns a
  `CurrentSessionContextProvider` for kind `current-session` and defers to the underlying
  factory for other kinds.
- The provider uses the context captured in the factory **closure** (assert the seeded
  profile/user/definition appear in the tool result) — confirming the context flows from the
  host-created factory, not from agent-build plumbing.

### Host-wiring tests

- Starting/resuming a session in the host builds a `CurrentSessionContext` from the host's
  **current** profile/user (and the session id + definition reference) and combines the
  `current-session` factory into the chain — so resuming on a different profile yields a
  different reported profile/user.

### Schema test

- `agent-session.json` accepts an entity with `agent-definition-reference` and still accepts
  one without it (optional); a malformed (non-array) reference is rejected.

### Integration test (optional, opt-in)

- Drive a real agent over the existing BYOK harness (`OpenAiCompatibleChatServer` fronting a
  test `IChatClient`, with SSE streaming) configured to call `get_current_session`, and assert
  the tool result contains the seeded session/profile/user/definition. Kept optional; the unit
  tests above are the primary coverage.

### Principles

- Deterministic: in-memory DAL, canned data, event-driven assertions — no timing waits.
- No GUI/network in unit scope; the tool path is fully async (`ConfigureAwait(false)` in the
  data layer).
- Reproduce-bug-first: a resolution bug gets a failing unit test (seeded graph) before the fix.

## Open questions

1. **Agent-definition snapshot vs. reference.** Should the result embed the full
   agent-definition entity, or only its name/id (to keep the payload small and avoid leaking
   large prompt text)? Proposed: return the entity but allow `include_definition:false`.
2. **Trust/secret scoping.** The agent-definition may reference connections/secrets; ensure the
   serialized result never includes resolved secrets (only sources), consistent with the
   no-secrets rule.
3. **Computer entity.** Include the `computer` (from `computer-reference`) too? Proposed: add
   it as a fifth member later if agents need it; out of scope for the first cut.
