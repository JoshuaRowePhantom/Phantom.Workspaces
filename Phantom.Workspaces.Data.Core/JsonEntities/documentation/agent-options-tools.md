# Agent Options — Tools

The `tools` array on a `PromptAgent` definition declares which tools the agent can use. Each tool is an object with at minimum a `name` and a discriminator field (`kind`, `type`, or a schema-level discriminator).

<!-- When adding or changing MCP tool connection kinds or options, update ["documentation", "agent-options", "tools"] and ["documentation", "agent-options", "connections"]. -->
<!-- When changing filesystem toolset options, update ["documentation", "agent-options", "tools"]. -->

See also: `["documentation", "agent-options", "connections"]` for connection kind details used by `mcp` tools.

---

## Execution target of tool kinds

Every tool constructed from an AgentDefinition is tagged at construction time with an `ExecutorTarget` (`Phantom.Workspaces.Llm.Core/Transport/ExecutorTarget.cs`) that selects the client instance it runs on via `ExecutorTopology` (`Phantom.Workspaces.Llm.Core/Transport/ExecutorTopology.cs`). Mapping from tool `kind` to target is done by `ExecutorTargetResolver.ForKind` / `ExecutorTargetResolver.ForTool` (`Phantom.Workspaces.Llm.Core/Transport/ExecutorTargetResolver.cs`).

In the single-machine topology all three targets resolve to `TrustProfile.LocalClientInstance` (`"."`) and every call runs on the source. In the `[remote-copilot-sdk]` topology the router remains on the source (G / H) but the `AgentExecutor` target resolves to a remote `user-computer-profile` client instance selected by the `trust-profile` parameter.

| Tool `kind` | `ExecutorTarget` | Single-machine | Remote-hosted (`[remote-copilot-sdk]`) |
|---|---|---|---|
| `workspace-gui` | `GuiLocal` | source | source (initiating machine) |
| `workspace-entity` | `GuiLocal` | source | source |
| `agent-session` / `workspace-agent-session` — target session id equals **source** session id | `GuiLocal` (via `ExecutorTargetResolver.ForKindWithTargetSession`) | source | source |
| `agent-session` / `workspace-agent-session` — target session id is a **different** session | `HostingInstance` | source | remote host of the target session |
| `current-session` — resolved against the source session id | `GuiLocal` (source-targeted rule) | source | source |
| `mcp` (McpTool) | `AgentExecutor` | source | remote profile |
| `function` (FunctionTool) | `AgentExecutor` | source | remote profile |
| `filesystem` | `AgentExecutor` | source | remote profile |
| `web_request` / `web_search` / `web` | `AgentExecutor` | source | remote profile |
| `chat-history` | `AgentExecutor` | source | remote profile |
| `github-cli-builtin-tools` | `AgentExecutor` | source | remote profile |
| Any unknown / provider-specific kind | `AgentExecutor` (default) | source | remote profile |
| Copilot SDK built-in tools (shell, filesystem, …) invoked by the CLI itself | — (SDK self-invokes) | source | remote profile — wherever `CopilotSdkChatClient` runs |

Notes:
- The `agent-session` / `current-session` reclassification to `GuiLocal` when target == source is implemented by `ExecutorTargetResolver.ForKindWithTargetSession` / `ForToolWithTargetSession`; use those overloads whenever a source session id is available at call time.
- `workspace-agent-session` is the pre-cutover alias for `agent-session` and maps identically.
- The `AgentExecutor` row for BYOK Copilot providers (`openai`, `azure-openai`) applies too — the BYOK `CopilotSdkChatClient` runs on the remote profile in the split topology.

See `docs/design/remote-chat-client-session.md` for the full topology narrative and the `["documentation", "agent-options", "providers"]` "Remote hosting" subsection for how a session becomes remote-hosted.

---

## `filesystem` (built-in toolset)

Provides file-read, file-write, and directory-listing MCP tools backed by the `FilesystemServiceContextProvider`. The filesystem MCP server is spawned as a child process from `Phantom.Workspaces.Llm.Core.exe` (or via `dotnet` on non-Windows).

No explicit tool entry is needed in the agent definition; the filesystem toolset is injected at the `AgentServices` layer. The `FilesystemServiceContextProvider` accepts an optional `editStoreConnectionJson` for persistence integration.

---

## `mcp` (McpTool / CustomTool with kind `"mcp"`)

Connects to an external MCP server over HTTP (SSE or streaming) or stdio. Backed by `McpToolContextProvider`.

### Fields

| Field | Type | Description |
|---|---|---|
| `name` | string | Tool display name. |
| `kind` | string | Must be `"mcp"` (or matched by the AgentSchema `McpTool` discriminator). |
| `connection` | object | Connection to the MCP server. See `["documentation", "agent-options", "connections"]`. |
| `serverName` | string | Optional name label for the MCP server (used in transport naming). |
| `allowedTools` | string[] | Optional whitelist of tool names to expose from the MCP server. When omitted, all tools are exposed. |

### Transport selection

The transport is chosen from `connection.endpoint`:

- **`stdio://`** — spawns a local process. URI host or `?command=<process>` sets the executable. `?arg=<value>` (repeatable) sets arguments. `?cwd=<path>` sets the working directory. API keys are **not** supported on stdio.
- **`http://` or `https://`** — HTTP-based SSE transport. API key (if present) is sent as `Authorization: Bearer <key>`.

### Connection kinds

The `connection` is a strict discriminated union (see `["documentation", "agent-options", "connections"]`). Supported `kind` values for MCP tools:

- **`Anonymous`** — unauthenticated remote or `stdio://` server. Requires `endpoint`.
- **`key`** — bearer-token authenticated remote server. Requires `endpoint` and `apiKey`.
- **`oauth`** — OAuth 2.0 authenticated remote server. Requires `endpoint`; `clientId`, `clientSecret`, `tokenUrl`, and `scopes` are optional (metadata and client registration are discovered from the endpoint).

Worked example — an `oauth` MCP tool:

```json
{
  "name": "example",
  "kind": "mcp",
  "description": "Example OAuth-authenticated MCP server",
  "connection": {
    "kind": "oauth",
    "endpoint": "https://mcp.example.com/",
    "clientId": "${SECRET:ExampleClientId}",
    "scopes": ["read", "write"]
  },
  "serverName": "example",
  "serverDescription": "Example remote MCP server authenticated with OAuth",
  "approvalMode": { "kind": "never" }
}
```

---

## `chat-history` (CustomTool with kind `"chat-history"`)

Persists conversation history to an external store. Backed by `AgentPersistenceStoreFactory`.

### Fields

```json
{
  "name": "chat-history",
  "kind": "chat-history",
  "options": {
    "connection": {
      "provider": "mongodb",
      "mongoProvider": "container",
      "database-name": "<db>",
      "collection-name": "<collection>",

      "container-name": "<docker-container>",
      "data-directory": "<host-path>",
      "host-port": 27017
    }
  }
}
```

| Field | Purpose |
|---|---|
| `provider` | `"mongodb"` (only supported value). |
| `mongoProvider` | `"container"` — Docker-managed MongoDB; `"external"` — remote/existing MongoDB. |
| `database-name` | MongoDB database to use. |
| `collection-name` | Collection for storing messages. |
| `container-name` | Docker container name (container mode only). |
| `data-directory` | Host-side data directory for Docker volume (container mode only). |
| `host-port` | Host port mapping for Docker (default 27017, container mode only). |
| `connection-string` | Full MongoDB connection string (external mode only). |

---

## `github-cli-builtin-tools` (GitHub Copilot SDK tool policy)

Provider-specific configuration for `github-copilot` and `github-copilot-subagent` agents. It controls the Copilot CLI SDK default tools through `SessionConfig.AvailableTools`, `SessionConfig.ExcludedTools`, and optional `CopilotClientOptions.Mode`.

```jsonc
{
  "kind": "github-cli-builtin-tools",
  "client-mode": "copilot-cli",
  "available-tools": { "tools": ["read_agent", "list_agents"] },
  "excluded-tools": { "tools": ["shell"] }
}
```

`available-tools` and `excluded-tools` use the same selector:

| Selector | Meaning |
|---|---|
| `{ "tools": ["*"] }` | All built-ins (`available-tools` leaves the SDK default unset; `excluded-tools` maps to `builtin:*`). |
| `{ "tools": ["tool1", "tool2"] }` | Named tools. Bare names are rewritten to `builtin:<name>`. |
| `{ "isolated": true }` | The SDK isolated built-in set. |
| `{ "tools": [] }` | Empty built-in set. In `available-tools`, custom and MCP tools are still allowed. |

Source-qualified entries (`builtin:*`, `mcp:*`, `custom:*`, or `mcp:<wire-name>`) pass through unchanged. For `available-tools`, bare-only lists auto-append `custom:*` and `mcp:*` so restricting built-ins does not hide the agent's own custom/MCP tools. If any entry is source-qualified, the list is treated as an exact global allow-list and nothing is auto-appended.

Common recipes:

```jsonc
// Disable all Copilot built-ins; keep custom and MCP tools.
{ "kind": "github-cli-builtin-tools", "excluded-tools": { "tools": ["*"] } }

// Strict MCP-only agent; custom and built-in tools are unavailable.
{ "kind": "github-cli-builtin-tools", "available-tools": { "tools": ["mcp:*"] } }

// Locked-down MCP-only client mode.
{ "kind": "github-cli-builtin-tools", "client-mode": "empty", "available-tools": { "tools": ["mcp:*"] } }
```

`client-mode` is `"copilot-cli"` by default. `"empty"` selects the SDK Empty client mode; it requires a present, non-empty `available-tools` selector because Empty mode exposes no tools by default.
