# Agent Definition Schema

Schema for LLM agent definition configuration documents.

## Selection, configuration and availability

The canonical definition schema describes **toolset selection/configuration**, not
the parameter schemas of individual callable functions. Function parameters are
published by each runtime toolset.

`tools` accepts an authored array and the SDK's `Save`/`ToJson` name-keyed object
representation. `kind` selects a factory using exact ordinal identifiers. `name`
is normally display metadata: a custom kind named `filesystem` is still custom;
`kind: filesystem` with another display name still selects filesystem.

The exception is persistence configuration: exact custom-tool `kind: chat-history`
**or** `name: chat-history` selects chat-history options. A name-keyed entry
`"chat-history": {...}` has the same effect. This does not exempt another selected
known kind from its own contract. MCP entries are not custom-tool configuration
aliases. `AgentChat` separately suppresses attachment of the chat-history *kind*
case-insensitively; that is not a global case-insensitive factory policy.

Unknown kinds, arbitrary names and extension properties remain schema-valid.
Known selections must satisfy their typed constraints: a generic extension
branch cannot bypass them. Schema validity does **not** guarantee the current
host supplies a factory or the resources required by that factory.

## Built-in inventory

| Exact kind | Scope / host requirement | Consumed authored configuration |
|---|---|---|
| `web_search` | Core default; host web-search capability | None |
| `web_request` | Core default; host HTTP/web-request capability | None |
| `web` | Core default; combines search and request | None |
| `filesystem` | Core default; filesystem access; selected infrastructure for persistence | Top-level `connection` as described below |
| `workspace-entity` | Application composition; data access | None |
| `workspace-gui` | Application composition; host-supplied GUI context provider | None |
| `current-session` | Application composition; data access and current-session context | None |
| `agent-session` | Application composition; running-chat factory, current-session context and populated parent chat reference | None; not a baseline fixed resource |

For kinds with no consumed configuration, `options` may be an object containing
extension keys, but those keys do not enable runtime features.
`shell` is proposed, not registered.

Three existing provider/dispatcher entries remain separately typed:

- `mcp`: requires its MCP connection and `serverName`; preserves transport,
  authentication and provider handling. It is not manifest MCP-entity resolution.
- `github-cli-builtin-tools`: `client-mode` is `copilot-cli` by default, or `empty`.
  Preserve `available-tools`/`excluded-tools` selectors, built-in/custom/MCP prefixes
  and isolation. Empty client mode requires an available-tools selector.
- `agent-definition`: named dispatcher entry requiring `kind`, nonempty `name`
  and `description`, with inline `definition` or `manifest-reference` support.
  In a name-keyed serialization the key supplies the name.

`chat-history` is a special configuration entry, **not** a callable toolset.

## Filesystem edit history

```json
{
  "kind": "prompt",
  "name": "file-editor",
  "model": { "id": "echo", "provider": "echo" },
  "tools": [
    {
      "kind": "filesystem",
      "name": "files",
      "connection": {
        "provider": "mongodb",
        "mongoProvider": "external",
        "database-name": "workspace",
        "collection-name": "edits",
        "connection-string": "mongodb://localhost"
      }
    }
  ]
}
```

Absent `connection` uses **in-memory** edit history. A supplied object uses
`https://schemas.workspaces.phantom.to/chat-history-provider.json`.
Malformed supplied configuration fails; it does not silently fall back.

Filesystem provider JSON is **not** an SDK `AgentSchema.Connection`.
The central Phantom loader uses a kind-specific bridge to `FilesystemTool`,
whose `Save` preserves the complete provider object. Load → save → load passes
the same provider JSON to the edit-store factory without affecting MCP or model
connections. Programmatic callers use `FilesystemTool.EditStoreConnection`;
SDK connections and `options.connection` are rejected for filesystem rather than
being silently ignored.

JSON and YAML definition loaders use the same schema-validated load path.
Central YAML definition/manifest entry points normalize nested collections
through JSON before loading, so tools are not silently dropped by SDK YAML
dictionary handling. Both authored arrays and name-keyed collections work.

Both MongoDB variants require `provider: mongodb`, `mongoProvider`, nonempty
`database-name` and `collection-name`:

- `external`: nonempty `connection-string`.
- `container`: nonempty `container-name` and `data-directory`; optional integer
  `host-port` from 1 through 65535.

No credential is needed to validate or round-trip these configurations. Actually
using persistent storage requires the corresponding provider infrastructure.
Never put credentials in checked-in configuration.

## Chat-history persistence

Chat history consumes **`options.connection`**, not filesystem's top-level field:

```json
{
  "kind": "chat-history",
  "name": "history",
  "options": {
    "connection": {
      "provider": "mongodb",
      "mongoProvider": "container",
      "database-name": "workspace",
      "collection-name": "messages",
      "container-name": "workspace-history",
      "data-directory": "data",
      "host-port": 27017
    }
  }
}
```

It uses the same shared provider constraints. Absent configuration uses
in-memory persistence. Runtime parse failure also defaults to in-memory
chat history, unlike filesystem's failure behavior; schema validation rejects
malformed authored provider configuration before that runtime fallback.

Agent sessions consume referenced definitions/manifests. Their entity schemas
reference this canonical catalog rather than maintaining a second tool inventory.
