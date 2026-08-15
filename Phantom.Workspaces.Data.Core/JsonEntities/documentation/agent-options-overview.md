# Agent Options — Overview

This entity is the index for the agent definition options reference. Each major topic has its own sibling entity. Retrieve them by `entity-name` using `workspaces_entity_get`.

## Sibling entities

| Entity name | Topic |
|---|---|
| `["documentation", "agent-options", "providers"]` | Per-provider reference: github-copilot, github-models, ollama, echo/test |
| `["documentation", "agent-options", "model-options"]` | `model.options` fields and `additionalProperties` key reference |
| `["documentation", "agent-options", "tools"]` | Tool kinds: filesystem, web_request, mcp, chat-history, github-cli-builtin-tools |
| `["documentation", "agent-options", "parameters"]` | Manifest parameter mechanism, `${name}` substitution, well-known parameters |
| `["documentation", "agent-options", "connections"]` | Connection kinds and their fields per provider |

## Top-level agent definition fields

The agent definition follows the [Microsoft AgentSchema](https://microsoft.github.io/AgentSchema/) `PromptAgent` shape. Key fields:

| Field | Type | Description |
|---|---|---|
| `kind` | string | Agent type. Use `"prompt"` for LLM-based agents. |
| `name` | string | Internal kebab-case identifier. |
| `displayName` | string | Human-readable name shown in UI. |
| `instructions` | string | System prompt / persona for the agent. |
| `additionalInstructions` | string | Extra instructions appended at runtime (e.g. date, cwd). |
| `model` | object | Model selection, provider, connection, and options. See siblings. |
| `tools` | array | Tool definitions. See `["documentation", "agent-options", "tools"]`. |
| `parameters` | object (`PropertySchema`) | Declared parameters for manifest-driven agents. See `["documentation", "agent-options", "parameters"]`. |
| `metadata` | object | Free-form metadata for versioning, tags, and custom fields. |
| `workingDirectory` | string | Default working directory forwarded to the Copilot CLI (github-copilot provider). |

## `model` sub-object

| Field | Description |
|---|---|
| `model.id` | Model identifier string (provider-specific). |
| `model.provider` | Provider name. See `["documentation", "agent-options", "providers"]`. |
| `model.connection` | Authentication details. See `["documentation", "agent-options", "connections"]`. |
| `model.options` | LLM sampling parameters. See `["documentation", "agent-options", "model-options"]`. |

## Special model.id value

Setting `model.id` to `"test"` (case-insensitive) bypasses provider dispatch entirely and returns a `TestProviderChatClient` regardless of the `model.provider` value. Useful in unit tests.

## GitHub Copilot built-in tool policy

`github-copilot` and `github-copilot-subagent` agents can include a `tools[]` entry with `kind: "github-cli-builtin-tools"` to allow or exclude Copilot CLI SDK default tools. The same entry can set `client-mode: "empty"`, which selects the SDK's Empty client mode at client construction time (not per session). See `["documentation", "agent-options", "tools"]` for selectors and examples.

## Remote hosting (`[remote-copilot-sdk]` topology)

`github-copilot` (and BYOK `openai` / `azure-openai`) can run their `CopilotSdkChatClient` on a remote `user-computer-profile` while the source instance retains the `AgentChat` router, persistence, and GUI. A manifest opts into this topology by declaring a `trust-profile` parameter whose resolved `llm-trust-profile` populates `TrustProfile.HostingWorkspacesClientInstances` with a non-`"."` client-instance id.

- Provider details: `["documentation", "agent-options", "providers"]` § "Remote hosting".
- Split executor mapping (which tool `kind` runs where): `["documentation", "agent-options", "tools"]` § "Execution target of tool kinds".
- Trust-profile-driven host selection: `["documentation", "agent-options", "parameters"]` § `trust-profile`.
- `agent-session` `host-profile-entity-id` worked example: `["documentation", "agent-configuration"]` § "Remote-hosted `agent-session`".
- Master design: `docs/design/remote-chat-client-session.md`.
- Example manifest: `docs/examples/github-copilot-remote-chat.json`.
