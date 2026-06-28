# Agent Options — Tools

The `tools` array on a `PromptAgent` definition declares which tools the agent can use. Each tool is an object with at minimum a `name` and a discriminator field (`kind`, `type`, or a schema-level discriminator).

<!-- When adding or changing MCP tool connection kinds or options, update ["documentation", "agent-options", "tools"] and ["documentation", "agent-options", "connections"]. -->
<!-- When changing filesystem toolset options, update ["documentation", "agent-options", "tools"]. -->

See also: `["documentation", "agent-options", "connections"]` for connection kind details used by `mcp` tools.

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
