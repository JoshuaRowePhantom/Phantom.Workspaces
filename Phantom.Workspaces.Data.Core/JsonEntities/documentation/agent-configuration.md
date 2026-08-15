# Agent Configuration

This note covers the full agent configuration model: entity types, manifest structure, model options, tools, built-in toolsets, and common configuration patterns.

---

## Entity Type Relationships

Three entity types make up the agent configuration model:

| Entity type | Role |
|---|---|
| `agent-manifest` | Reusable configuration template. Holds a `template` (AgentDefinition) plus `resources` that are resolved at runtime (per-user, per-machine, per-workspace). |
| `agent-definition` | Standalone, fully-resolved AgentDefinition. No parameter substitution or resource resolution needed. Stored in the `definition` property. |
| `agent-session` | A running or completed agent session. Stores the runtime `agent-session-id`, a reference back to the originating `agent-definition`, and the `parameter-values` that were supplied at launch. |

---

## `agent-manifest` — Full Field Reference

An `agent-manifest` entity carries a `manifest` property that must conform to the agent manifest JSON schema.

```json
{
  "entity-types": ["entity", "agent-manifest"],
  "display-name": { "default": "My Agent" },
  "manifest": {
    "name": "my-agent",
    "displayName": "My Agent",
    "description": "What this agent does.",
    "template": { ... },
    "parameters": {
      "properties": [
        {
          "name": "endpoint",
          "kind": "string",
          "description": "The API endpoint URL.",
          "required": true,
          "default": "https://api.example.com",
          "example": "https://api.example.com/v2",
          "enumValues": []
        }
      ]
    },
    "resources": [
      { "kind": "tool", "id": "fixed",             "name": "workspace-entity" },
      { "kind": "tool", "id": "mcp-server-entity", "name": "github" }
    ]
  }
}
```

### `manifest` top-level fields

| Field | Required | Description |
|---|---|---|
| `name` | ✔ | Unique identifier for the manifest. |
| `displayName` | ✔ | Human-readable label shown in the UI. |
| `description` | | Description of the agent's purpose and capabilities. |
| `template` | ✔ | Base AgentDefinition (see below). |
| `parameters` | | Parameter schema; properties define substitution variables. |
| `resources` | | Runtime resources (tools, models) resolved from execution context. |
| `metadata` | | Arbitrary metadata (version, author, tags, prerequisites, …). |

### `manifest.parameters.properties[]` — parameter descriptor fields

| Field | Description |
|---|---|
| `name` | Parameter name. Used in `${name}` placeholders. |
| `kind` | Parameter type hint (e.g., `"string"`). |
| `description` | Human-readable description shown in the launch UI. |
| `required` | `true` if a value must be supplied before the session can start. |
| `default` | Default value used when no value is provided. |
| `example` | Example value shown as a hint in the launch UI. |
| `enumValues` | List of allowed values; renders as a dropdown in the launch UI. |

Parameter values are substituted into `model.options.additionalProperties` string values using the `${name}` placeholder syntax at session-launch time. For example, a parameter named `endpoint` is referenced as `"${endpoint}"`.

### `manifest.resources[]` — resource descriptor fields

| Field | Description |
|---|---|
| `kind` | `"tool"` or `"model"`. |
| `id` | Resolution strategy. `"fixed"` for built-in toolsets; `"mcp-server-entity"` to resolve an `mcp-server` entity by name. |
| `name` | For `"fixed"`: the built-in toolset name. For `"mcp-server-entity"`: the entity name of the MCP server (e.g., `"github"`). |
| `options` | Optional tool-specific configuration. |

---

## `template` — AgentDefinition (PromptAgent)

The `template` (and the `definition` property on `agent-definition` entities) is an AgentDefinition object:

```json
{
  "kind": "prompt",
  "name": "my-agent",
  "displayName": "My Agent",
  "description": "Short description.",
  "instructions": "You are a helpful assistant…",
  "additionalInstructions": "Extra runtime instructions appended to the system prompt.",
  "model": { ... },
  "tools": [ ... ],
  "metadata": { "version": "1.0", "tags": ["example"] }
}
```

| Field | Required | Description |
|---|---|---|
| `kind` | ✔ | Always `"prompt"` for PromptAgent. |
| `name` | ✔ | Unique agent name. |
| `displayName` | | Human-readable label. |
| `description` | | Agent description. |
| `instructions` | | System prompt / instructions. |
| `additionalInstructions` | | Extra instructions appended to `instructions` at runtime. |
| `model` | ✔ | Model configuration (see below). |
| `tools` | | Inline tool definitions (see below). |
| `metadata` | | Arbitrary metadata. |

---

## `model` — Model Configuration

```json
{
  "id": "gpt-4.1-mini",
  "provider": "github-models",
  "apiType": "OpenAI",
  "connection": {
    "kind": "key",
    "endpoint": "https://models.github.ai/inference",
    "apiKey": "${GITHUB_TOKEN}"
  },
  "options": {
    "temperature": 0.2,
    "topP": 0.9,
    "topK": 40,
    "maxOutputTokens": 2048,
    "frequencyPenalty": 0.0,
    "presencePenalty": 0.0,
    "additionalProperties": {
      "num_ctx": 32768,
      "keep_alive": "15m"
    }
  }
}
```

### `model` fields

| Field | Description |
|---|---|
| `id` | Model identifier (e.g., `"gpt-4.1-mini"`, `"qwen3.6"`, `"auto"`). |
| `provider` | Provider hint: `"echo"`, `"github-models"`, `"github-copilot"`, `"ollama"`, `"openai"`, `"azure-openai"`. The `openai`/`azure-openai` providers select BYOK mode via the Copilot SDK. |
| `apiType` | API protocol used by the client (e.g., `"OpenAI"`, `"Ollama"`). |
| `connection` | Connection configuration (see below). |
| `options` | Sampling and inference options (see below). |

### `model.connection` — connection kinds

| `kind` | Fields | Notes |
|---|---|---|
| `Anonymous` | `endpoint` | No authentication; used for local servers (e.g., Ollama). |
| `key` | `endpoint`?, `apiKey` | API-key authentication. `apiKey` supports `${ENV_VAR}` expansion. |
| `foundry` | `endpoint`, `name`, `connectionType` | Azure AI Foundry connection. |
| `oauth` | (provider-specific) | OAuth-based authentication. |
| `reference` | `name`, `target` | References a named connection defined elsewhere. |
| `remote` | `name`, `endpoint` | Remote connection by name and endpoint. |

### `model.options` fields

| Field | Type | Description |
|---|---|---|
| `temperature` | number | Sampling temperature (0–2). |
| `topP` | number | Nucleus sampling probability. |
| `topK` | integer | Top-k sampling limit. |
| `maxOutputTokens` | integer | Maximum tokens in the response. |
| `frequencyPenalty` | number | Penalty for token frequency. |
| `presencePenalty` | number | Penalty for token presence. |
| `seed` | integer | Random seed for deterministic sampling. |
| `stopSequences` | string[] | Sequences that stop generation. |
| `allowMultipleToolCalls` | boolean | Whether multiple tool calls per turn are allowed. |
| `additionalProperties` | object | Provider-specific options (e.g., `num_ctx`, `keep_alive`, `thinking`). String values support `${paramName}` substitution. |

---

## Tools in `template.tools[]`

Inline tools are specified directly in the agent definition and are always loaded (as opposed to `resources`, which are resolved from context).

| `kind` | Type | Description |
|---|---|---|
| `mcp` | McpTool | MCP server tool. Requires `connection` and `serverName`. |
| `function` | FunctionTool | Custom function tool. |
| `openapi` | OpenApiTool | OpenAPI-backed tool. |
| `file_search` | — | File search built-in. |
| `code_interpreter` | — | Code interpreter built-in. |
| `bing_search` | — | Bing search built-in. |
| `github-cli-builtin-tools` | CustomTool | GitHub Copilot SDK default-tool policy for `github-copilot`/`github-copilot-subagent` agents. |
| `""` (empty) | CustomTool | Fixed built-in toolset referenced by name (resolved via the toolset factory). |

### McpTool fields

| Field | Required | Description |
|---|---|---|
| `kind` | ✔ | `"mcp"` |
| `connection` | ✔ | Connection to the MCP server (must include `kind`). |
| `serverName` | ✔ | Name of the MCP server. |
| `name` | | Tool alias within the agent. |
| `serverDescription` | | Human-readable server description. |
| `approvalMode` | | `{ "kind": "always" }`, `{ "kind": "never" }`, or `{ "kind": "specify", "alwaysRequireApprovalTools": [], "neverRequireApprovalTools": [] }`. |
| `allowedTools` | | Allowlist of MCP tool names to expose. |

### Locked-down GitHub Copilot agent recipe

Safety-sensitive Copilot SDK agents can disable the SDK's ambient Copilot CLI tools and opt into only MCP tools:

```jsonc
{
  "kind": "github-cli-builtin-tools",
  "client-mode": "empty",
  "available-tools": { "tools": ["mcp:*"] }
}
```

`client-mode: "empty"` is a Copilot client construction option. It must be paired with a present, non-empty `available-tools` selector because the SDK exposes no tools by default in Empty mode. Use `excluded-tools: { "tools": ["*"] }` instead when you only want to remove Copilot built-ins while keeping all custom and MCP tools.

---

## Built-in Tools

Built-in tools are referenced in `manifest.resources[]` with `"id": "fixed"` and the corresponding `name`, or in `template.tools[]` with `"kind": ""` and the toolset name.

<!-- NOTE: Update docs/JsonEntities/documentation/agent-configuration.md when built-in tool names change. -->

| Name | Description |
|---|---|
| `workspace-entity` | Read and write workspace entities via the data-access-layer. Core toolset for workspace-aware agents. |
| `workspace-gui` | Open/close panes and tabs, and invoke entity shortcuts in the desktop GUI. Only meaningful in the desktop host. |
| `filesystem` | Read and write files on the local filesystem via an MCP-backed service. Supports optional `connection` configuration for remote edit stores. |
| `web_search` | Perform web searches and return results. |
| `web_request` | Fetch arbitrary HTTP URLs and return their content. |
| `web` | Combined toolset: equivalent to both `web_search` and `web_request`. |

---

## `agent-session` Entity

An `agent-session` entity is created when a session is launched from a manifest or definition.

```json
{
  "entity-types": ["entity", "agent-session"],
  "agent-session-id": "abc123",
  "host-profile-entity-id": "<uuid of the user-computer-profile>",
  "agent-definition-reference": ["user", "agent-definitions", "my-agent"],
  "parameter-values": {
    "endpoint": "https://api.example.com"
  }
}
```

| Field | Required | Description |
|---|---|---|
| `agent-session-id` | ✔ | Runtime session identifier used by `IAgentPersistenceStore`. |
| `host-profile-entity-id` | | Entity ID of the `user-computer-profile` hosting this session. |
| `agent-definition-reference` | | Entity-name path to the `agent-definition` used to reconstruct the session on resume. |
| `parameter-values` | | Parameter values supplied at launch (string key → string value). |

### Remote-hosted `agent-session`

When the wrapping manifest opts into the `[remote-copilot-sdk]` topology by declaring a `trust-profile` parameter whose resolved `llm-trust-profile` names a non-`"."` client instance (`TrustProfile.HostingWorkspacesClientInstances`), the launcher records the selected host on the resulting `agent-session` entity's `host-profile-entity-id`. The `agent-definition-reference` still resolves against the source workspace so the same manifest reconstructs the session on resume; the topology is rebuilt from the trust-profile resolution, not from the persisted host id (which is a hint, not the source of truth — see `docs/design/session-context-tools.md`).

```json
{
  "entity-types": ["entity", "agent-session"],
  "agent-session-id": "sess-42",
  "host-profile-entity-id": "d3b07384-d113-4f45-9d6f-2b6a1c7d9e01",
  "agent-definition-reference": ["user", "agent-definitions", "github-copilot-remote-chat"],
  "parameter-values": {
    "working-directory": "/home/agent/projects/phantom",
    "trust-profile": "remote-copilot"
  }
}
```

- `host-profile-entity-id` — entity id of the remote `user-computer-profile` (see `["documentation", "user-computer-profile-schema"]`).
- `parameter-values["trust-profile"]` — the parameter value the launcher used to look up the `llm-trust-profile` entity that populated `HostingWorkspacesClientInstances` with the remote client-instance id.
- `parameter-values["working-directory"]` — the CWD the Copilot CLI process should use on the remote host.

See `docs/examples/github-copilot-remote-chat.json` for the wrapping AgentDefinition and `docs/design/remote-chat-client-session.md` for the full topology.

---

## Typical Configuration Patterns

### Local Ollama Agent

```json
{
  "manifest": {
    "name": "local-ollama",
    "displayName": "Local Ollama Agent",
    "template": {
      "kind": "prompt",
      "name": "local-ollama",
      "model": {
        "id": "qwen3.6",
        "provider": "ollama",
        "apiType": "Ollama",
        "connection": { "kind": "Anonymous", "endpoint": "http://localhost:11434" },
        "options": {
          "temperature": 0.7,
          "maxOutputTokens": 2048,
          "additionalProperties": { "num_ctx": 32768, "keep_alive": "15m" }
        }
      },
      "instructions": "You are a helpful assistant."
    },
    "resources": [
      { "kind": "tool", "id": "fixed", "name": "workspace-entity" },
      { "kind": "tool", "id": "fixed", "name": "workspace-gui" }
    ]
  }
}
```

### GitHub Copilot Agent

```json
{
  "manifest": {
    "name": "github-copilot",
    "displayName": "GitHub Copilot Assistant",
    "template": {
      "kind": "prompt",
      "name": "github-copilot",
      "model": {
        "id": "auto",
        "provider": "github-copilot",
        "apiType": "OpenAI",
        "connection": { "kind": "key", "apiKey": "${GITHUB_TOKEN}" },
        "options": { "temperature": 0.2, "maxOutputTokens": 2048 }
      },
      "instructions": "You are a helpful AI assistant."
    },
    "resources": [
      { "kind": "tool", "id": "fixed",             "name": "workspace-entity" },
      { "kind": "tool", "id": "fixed",             "name": "workspace-gui" },
      { "kind": "tool", "id": "mcp-server-entity", "name": "github" }
    ]
  }
}
```

### GitHub Models Agent

```json
{
  "manifest": {
    "name": "github-models",
    "displayName": "GitHub Models Assistant",
    "template": {
      "kind": "prompt",
      "name": "github-models",
      "model": {
        "id": "gpt-4.1-mini",
        "provider": "github-models",
        "apiType": "OpenAI",
        "connection": {
          "kind": "key",
          "endpoint": "https://models.github.ai/inference",
          "apiKey": "${GITHUB_TOKEN}"
        },
        "options": { "temperature": 0.2, "maxOutputTokens": 2048 }
      },
      "instructions": "You are a helpful AI assistant."
    },
    "resources": [
      { "kind": "tool", "id": "fixed", "name": "workspace-entity" },
      { "kind": "tool", "id": "fixed", "name": "workspace-gui" }
    ]
  }
}
```

### Parameterised Agent

Use parameters to let users supply values at launch (e.g., an endpoint or API key). String values in `model.options.additionalProperties` are substituted with `${paramName}`.

```json
{
  "manifest": {
    "name": "parameterised-agent",
    "displayName": "Parameterised Agent",
    "parameters": {
      "properties": [
        {
          "name": "model-id",
          "kind": "string",
          "description": "The Ollama model to use.",
          "required": true,
          "default": "qwen3.6",
          "enumValues": ["qwen3.6", "llama3.2", "mistral"]
        }
      ]
    },
    "template": {
      "kind": "prompt",
      "name": "parameterised-agent",
      "model": {
        "id": "qwen3.6",
        "provider": "ollama",
        "apiType": "Ollama",
        "connection": { "kind": "Anonymous", "endpoint": "http://localhost:11434" },
        "options": {
          "additionalProperties": { "model": "${model-id}" }
        }
      },
      "instructions": "You are a helpful assistant."
    },
    "resources": [
      { "kind": "tool", "id": "fixed", "name": "workspace-entity" }
    ]
  }
}
```
