# Agent Manifest — Executors (per-component executor binding)

This is the self-contained authoring contract for **per-component executor binding** (design
`docs/design/per-component-executor-binding.md`, issue #1432). Read it end-to-end to author a valid
executor-bound `AgentManifest`, or to read a persisted `agent-session` and explain/modify where each
component runs.

<!-- When changing executor authoring rules, update ["documentation", "agent-manifest-executors"] and keep it consistent with ["documentation", "agent-options", "parameters"]. -->

Sources:
- Schema: `Phantom.Workspaces.Llm.Core/JsonSchemas/agent-manifest.json` (`$defs/executorResource`, the
  `executor` fields on `toolResource`/`modelResource`).
- Model: `Phantom.Workspaces.Llm.Core/Manifest/ExecutorResource.cs`,
  `ExecutorResourceResolver.cs`, `ExecutorBindings.cs`, `ExecutorParameterSelection.cs`.
- Session persistence: `Phantom.Workspaces.Data.Core/AgentSessionExecutorBindings.cs`.

---

## The one idea to hold onto

**An executor binding IS a transport connection-descriptor.** There is **no** parallel executor
schema/type. An `executor` resource *resolves to* the same `type`-discriminated JSON that
`ITransportFactory.ConnectToAsync(JsonElement)` already consumes (`local`, `user-computer-profile`,
`http`, `reverse-http`, …). Executor **selection** made at launch is recorded in a dedicated typed
`parameter-selections` map (`string → JSON`) — a sibling root key of the `string→string`
`parameter-values` text-templating map, never inside it.

If you take one rule away: **unset `executor` and a single-machine topology both resolve to
`{"type":"local"}`.** You only need executor resources when a component must run somewhere else.

---

## 1. The `kind:"executor"` resource

An executor resource is an entry in the manifest's `resources[]` array. Required fields: `kind`
(always `"executor"`), `id` (the resolution strategy), and `name` (referenced by `executor` fields).
Optional: `options` (a string map feeding the convenience strategies) and `connection-descriptor` (the
inline escape hatch).

The `id` strategy is one of exactly **five** values, and each resolves to a connection-descriptor:

| `id` strategy | Resolves to | When chosen |
|---|---|---|
| `local` | `{"type":"local"}` | authoring time |
| `parameter` | the launch-selected trust profile's `DefaultExecutionTarget` (via a named `kind:"executor"` parameter → selected/implicit trust profile) | launch time (interactive) |
| `user-computer-profile-entity` | `{"type":"user-computer-profile","entity-id":"<uuid>"}` | authoring time (fixed entity-id) |
| `trust-profile` | that trust profile's `DefaultExecutionTarget` | authoring time (fixed trust-profile) |
| `connection-descriptor` | the inline `connection-descriptor` **verbatim** (extension escape hatch) | authoring time |

`parameter` overlaps conceptually with `trust-profile` / `user-computer-profile-entity`, but differs by
*when* the executor is chosen: `parameter` is the launch-time interactive selection; the others are
fixed when the manifest is authored.

A minimal `local` executor resource:

<!-- example: descriptor - -->
```json
{ "kind": "executor", "id": "local", "name": "here" }
```

The `connection-descriptor` escape hatch — a raw descriptor used verbatim, which is what makes the
model open-endedly extensible with **no schema change** (a future container/k8s/WSL `type` needs no new
manifest field):

<!-- example: descriptor - -->
```json
{
  "kind": "executor",
  "id": "connection-descriptor",
  "name": "container",
  "connection-descriptor": { "type": "reverse-http", "endpoint": "https://host.example/mcp/" }
}
```

---

## 2. Referencing an executor

The optional `executor` string appears on the **model** and on **each tool** resource. It names an
executor resource.

- **Unset `executor`** → the component inherits the **session executor**, which defaults to
  `{"type":"local"}`. Never author `"executor":"local"`; simply leave it unset.
- On the model, an executor NAME is *also* authored under the SDK-specific `model.options.executor`
  key (that is the key `CopilotSdkChatClient` reads to route the SDK chat client), in addition to the
  optional top-level `model.executor`.

A tool bound to the `worker` executor, and a model bound to it via the SDK key:

<!-- example: descriptor - -->
```json
{
  "model": { "options": { "executor": "worker" } },
  "tool":  { "kind": "tool", "id": "mcp-server-entity", "name": "some-remote-mcp", "executor": "worker" }
}
```

---

## 3. The `kind:"executor"` launch parameter

A parameter of `kind:"executor"` lets a user pick, at launch, which executor a `parameter`-strategy
executor resource resolves to. The user picks by choosing **either**:

- a **trust-profile** entity ("choose by trust policy") → resolves to that trust profile's
  `DefaultExecutionTarget`; **or**
- a **user-computer-profile** entity → synthesizes an *implicit* trust profile whose
  `DefaultExecutionTarget = {"type":"user-computer-profile","entity-id":<chosen uuid>}`.

The disambiguated selection is recorded in the typed `parameter-selections` map as a small JSON object
that identifies both the kind and the id — one of these two shapes:

<!-- example: selection - -->
```json
{ "worker-profile": { "trust-profile": "defaults/trust-profiles/remote" } }
```

<!-- example: selection - -->
```json
{ "worker-profile": { "user-computer-profile": "a1b2c3d4-e5f6-7788-99aa-bbccddeeff00" } }
```

Model helpers: `ExecutorParameterSelection.ForTrustProfile` / `ForUserComputerProfile` build these;
`TryGetTrustProfile` / `TryGetUserComputerProfile` read them. The selection is **never** stored as a
JSON-encoded string inside `parameter-values`, and `parameter-values` is not widened to
`string→object`.

---

## 4. Connection-descriptor types the model reuses

An executor resolves to one of the transport connection-descriptor `type`s:

- `{"type":"local"}` — the local orchestrator (client instance `"."`).
- `{"type":"user-computer-profile","entity-id":"<uuid>"}` — a persisted remote machine; the profile
  entity carries the real `connection-descriptor`, resolved recursively.
- `{"type":"http", …}` / `{"type":"reverse-http", …}` — HTTP-reachable hosts.

**Nesting is host-OUTER, target-INNER.** The outer descriptor reaches the host; an inner `target` is
what runs there:

<!-- example: descriptor - -->
```json
{ "type": "user-computer-profile", "entity-id": "a1b2c3d4-e5f6-7788-99aa-bbccddeeff00", "target": { "type": "local" } }
```

---

## 5. Session shape (what gets persisted)

At launch the manifest's executor resources are resolved into an `ExecutorBindings` map and persisted on
the `agent-session` entity under the `executor-bindings` root key, alongside the typed
`parameter-selections` root key:

- `executor-bindings.session` — the explicit session executor descriptor (default `{"type":"local"}`).
- `executor-bindings.components` — a map of **executor name → connection-descriptor object** (never a
  bare string).
- `parameter-selections` — the typed launch selections that fed resolution.

A component with an unset `executor` inherits `executor-bindings.session`; a component naming an
executor gets that executor's bound descriptor. This is how you read a session and explain where each
component runs.

---

## 6. Worked example A — split-executor Copilot topology

The default split from #1441: the Copilot SDK chat client runs **remotely** (bound to the `worker`
executor, chosen at launch via the `worker-profile` parameter); the chat router, the workspace tools
(`workspace-gui` / `workspace-entity`), and the GitHub web MCP all run **locally** (no `executor`).

The manifest:

<!-- example: manifest split -->
```json
{
  "name": "copilot-split-executor",
  "displayName": "GitHub Copilot (split executor)",
  "parameters": {
    "properties": [
      { "name": "worker-profile", "kind": "executor", "required": true },
      { "name": "working-directory", "kind": "string", "required": false }
    ]
  },
  "template": {
    "kind": "prompt",
    "name": "copilot-split-executor",
    "model": {
      "id": "auto",
      "provider": "github-copilot",
      "apiType": "OpenAI",
      "connection": { "kind": "key", "apiKey": "${GITHUB_TOKEN}" },
      "options": {
        "additionalProperties": {
          "executor": "worker",
          "working-directory": "${working-directory}"
        }
      }
    }
  },
  "resources": [
    { "kind": "executor", "id": "parameter", "name": "worker", "options": { "parameter": "worker-profile" } },
    { "kind": "tool", "id": "fixed", "name": "workspace-entity" },
    { "kind": "tool", "id": "fixed", "name": "workspace-gui" },
    { "kind": "tool", "id": "mcp-server-entity", "name": "github" }
  ]
}
```

The user picks a machine at launch. The resulting session — the `worker` executor resolved to the
chosen machine, the session executor and every unset component staying local:

<!-- example: session split -->
```json
{
  "parameter-selections": {
    "worker-profile": { "user-computer-profile": "a1b2c3d4-e5f6-7788-99aa-bbccddeeff00" }
  },
  "executor-bindings": {
    "session": { "type": "local" },
    "components": {
      "worker": { "type": "user-computer-profile", "entity-id": "a1b2c3d4-e5f6-7788-99aa-bbccddeeff00" }
    }
  }
}
```

Reading it back: the model (bound to `worker`) runs on
`a1b2c3d4-e5f6-7788-99aa-bbccddeeff00`; `workspace-entity`, `workspace-gui`, and `github` (all unset)
inherit `executor-bindings.session` = `{"type":"local"}`.

---

## 7. Worked example B — trivial all-local baseline ("you don't need executors")

A manifest with **no** executor resources and **no** `executor` fields. Everything resolves local.

<!-- example: manifest local -->
```json
{
  "name": "workspace-local",
  "displayName": "Workspace (all local)",
  "template": {
    "kind": "prompt",
    "name": "workspace-local",
    "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
  },
  "resources": [
    { "kind": "tool", "id": "fixed", "name": "workspace-entity" },
    { "kind": "tool", "id": "fixed", "name": "workspace-gui" }
  ]
}
```

Its session records only the default session executor; there are no per-component bindings, and every
component inherits `{"type":"local"}`:

<!-- example: session local -->
```json
{
  "executor-bindings": {
    "session": { "type": "local" },
    "components": {}
  }
}
```

---

## 8. OAuth-local guidance

MCP servers that authenticate with **interactive OAuth** (authorization-code with a loopback/localhost
redirect + a browser) MUST run on the machine that can open the user's browser and receive the loopback
redirect — i.e. the **local** executor. The default split manifest therefore pins the GitHub web MCP
**local** (unset `executor`): that is where the user authenticates, and the loopback redirect only works
locally. The workspace tools are likewise local because they use the interactive local credential store.

Only the SDK **chat client** (the model) is remote. There is **no** "web tools go remote" rule — an MCP
server with no `executor` simply runs on the local session executor. A key/PAT-authenticated web MCP
does not strictly require local, but the default ships it local.

**Validation:** an MCP tool whose connection uses interactive OAuth combined with a **non-local**
`executor` is rejected/warned at load time (see
`Phantom.Workspaces.Llm.Core/Manifest/OAuthExecutorBindingValidator.cs`). Do not bind an
interactive-OAuth MCP to a remote executor.

---

## See also

- `["documentation", "agent-options", "parameters"]` § `executor` parameter kind — the launch-parameter
  selection channel and the typed `parameter-selections` map.
- `docs/design/per-component-executor-binding.md` — the full design.
