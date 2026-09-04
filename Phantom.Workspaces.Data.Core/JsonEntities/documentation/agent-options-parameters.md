# Agent Options — Parameters

Agent manifests (`AgentManifest`) can declare typed parameters that callers must supply when starting a session. `AgentDefinitionParameterSubstitutor` resolves supplied values against the manifest's `parameters` block and substitutes placeholders in `model.options.additionalProperties` string values.

<!-- When changing substitution rules or well-known parameter names, update ["documentation", "agent-options", "parameters"]. -->

Source: `Phantom.Workspaces.Llm.Core/AgentDefinitionParameterSubstitutor.cs`

---

## `parameters` block (in `AgentManifest`)

```json
{
  "parameters": {
    "properties": [
      {
        "name": "working-directory",
        "kind": "string",
        "description": "Directory the Copilot CLI uses for file operations.",
        "required": true
      }
    ]
  }
}
```

### `Property` fields

| Field | Type | Description |
|---|---|---|
| `name` | string | Parameter name (used as the placeholder key in `${name}`). |
| `kind` | string | Parameter type hint: `"string"`, `"integer"`, `"boolean"`, etc. All values are resolved as strings in Phase 1. |
| `description` | string | Human-readable description shown in the Launchpad UI. |
| `required` | bool? | When `true`, a value must be supplied or have a default; otherwise an `ArgumentException` is thrown. |
| `default` | any | Default value used when no value is supplied. Converted to string via `.ToString()`. |

---

## `${name}` placeholder substitution

Substitution scope: **`model.options.additionalProperties` string values only** (Phase 1).

Syntax: `${param-name}` — the braces are literal.

Behavior:
- Provided values take priority over defaults.
- Unknown supplied parameters are silently ignored.
- `${name}` with no matching resolved parameter is left as-is (forward-compatible).
- Required parameters with no value and no default throw `ArgumentException`.

Example in a manifest template:

```json
{
  "model": {
    "options": {
      "additionalProperties": {
        "working-directory": "${working-directory}"
      }
    }
  }
}
```

---

## Well-known parameter names

### `working-directory`

Used by Copilot manifests to supply the working directory for the CLI at session creation time. The Launchpad UI renders a directory-picker button for parameters whose `name` is `"working-directory"` and `kind` is `"string"`.

Both `github-copilot-agent-manifest.json` and `workspaces-agent-manifest.json` declare this parameter. The substituted value in `model.options.additionalProperties` is copied into `ChatOptions.AdditionalProperties` by `AgentFactory.ConfigureChatOptions`; `CopilotSdkChatClient` reads it from there (never from `ModelOptions` directly — issue #896) and forwards it to both `CopilotClientOptions.Cwd` (process level) and `SessionConfig.WorkingDirectory` / `ResumeSessionConfig.WorkingDirectory` (session level).

Example manifest declaration:

```json
{
  "parameters": {
    "properties": [
      {
        "name": "working-directory",
        "kind": "string",
        "description": "Directory the Copilot CLI uses for file operations.",
        "required": true
      }
    ]
  }
}
```

### `trust-profile`

Names an `llm-trust-profile` entity that supplies the effective execution policy for the session. Currently declared on manifests that opt into the `[remote-copilot-sdk]` split topology (see `docs/examples/github-copilot-remote-chat.json`); other manifests may adopt it as trust-profile-driven execution rolls out.

**Kind:** `"string"`. **Required:** manifest-defined (required for remote-hosting manifests; typically optional with a `"default"` default elsewhere).

**How the value drives execution.** At session launch, `AgentFactory` resolves the parameter value to an `llm-trust-profile` entity and composes a runtime `TrustProfile` (`Phantom.Workspaces.Llm.Core/Trust/TrustProfile.cs`). The composed profile drives two knobs that together select the remote host:

| `TrustProfile` field | Purpose | Source |
|---|---|---|
| `HostingWorkspacesClientInstances` (line 137) | Effective set of client instances this profile may run on. Each entry is a client-instance id, `TrustProfile.LocalClientInstance` (`"."`, line 131) for the source, or `TrustProfile.WildcardClientInstance` (`"*"`, line 134) for "any". A non-`"."` id is what opts the session into remote hosting — it becomes `ExecutorTopology.AgentExecutorClientInstance` and every `AgentExecutor`-classed tool routes to that instance. | Composed via `TrustProfileDefinition.HostingWorkspacesClientInstances` (line 99) from the entity. |
| `DefaultExecutionTarget` (line 143) | `JsonElement?` connection descriptor used to reach the remote instance when the manifest does not override it. | Composed from `TrustProfileDefinition.DefaultExecutionTarget` (line 105). |

The launcher writes the resolved host onto the resulting `agent-session` entity's `host-profile-entity-id` field (`Phantom.Workspaces.Data.Core/JsonSchemas/agent-session.json:24`) so the topology can be reconstructed on resume.

**See also:**
- `["documentation", "agent-options", "providers"]` § "Remote hosting" — the `github-copilot` / BYOK path.
- `["documentation", "agent-options", "tools"]` § "Execution target of tool kinds" — the split topology table.
- `docs/design/remote-chat-client-session.md` — master topology design.
- `docs/design/llm-trust-profile.md` — trust-profile entity composition.

---

## Session round-trip

Parameter values are stored on the `agent-session` entity under the `parameter-values` field (a `{ [key: string]: string }` map). When a session is resumed, these values are re-read and passed back into `CreateAgentChatRequest.Parameters` so the same manifest substitution is replayed.
