# Agent Manifest Schema

Schema for LLM agent manifest configuration documents. Agent manifests declare an agent's configuration with tool resource references that are resolved at runtime based on execution context (user, machine, workspace).

## Resource selection versus configuration

`template` references the canonical Agent Definition schema; `template.tools`
has the same conditional known-kind validation, persistence configuration and
extension behavior as a standalone definition. These are **toolset-selection**
contracts, not the callable functions' parameter schemas.

In resources, `kind: tool` selects the category, `id` selects the resolution
strategy, and `name` selects a resource *within that strategy*. Only an exact
`id: fixed` plus a baseline name selects that fixed built-in. A custom strategy
with `name: filesystem` is not reclassified as baseline filesystem.

Unknown resource ids, custom fixed names/mappings, arbitrary names and extension
properties remain open. Known fixed selections still enforce their typed/common
resource contract, including object `options` and string `executor`. Schema
validity does not guarantee a host resolves the resource.

## Seven baseline fixed names

| Resource name | Host requirements |
|---|---|
| `web_search` | Core default; web search |
| `web_request` | Core default; web requests |
| `web` | Core default; combines search and request |
| `filesystem` | Core default; filesystem access; mapped tool's edit-store configuration |
| `workspace-entity` | Application composition; data access |
| `workspace-gui` | Application composition; host-supplied GUI context provider |
| `current-session` | Application composition; data access and current-session context |

`agent-session` is a registered **direct kind**, requiring a running-chat factory,
current-session context and populated parent chat reference. It is **not** one
of these baseline fixed names. Callers may provide custom mappings. `shell` is
not registered.

```json
{
  "name": "workspace-helper",
  "displayName": "Workspace helper",
  "template": {
    "kind": "prompt",
    "name": "workspace-helper",
    "model": { "id": "echo", "provider": "echo" }
  },
  "resources": [
    { "kind": "tool", "id": "fixed", "name": "web_search" },
    { "kind": "tool", "id": "fixed", "name": "web_request" },
    { "kind": "tool", "id": "fixed", "name": "web" },
    { "kind": "tool", "id": "fixed", "name": "filesystem" },
    { "kind": "tool", "id": "fixed", "name": "workspace-entity" },
    { "kind": "tool", "id": "fixed", "name": "workspace-gui" },
    { "kind": "tool", "id": "fixed", "name": "current-session" },
    { "kind": "tool", "id": "mcp-server-entity", "name": "github" }
  ]
}
```

The example lists all names for discovery, not a recommendation to attach the
overlapping web toolsets simultaneously.

## Resolution and ignored resource options

Fixed resolution returns the supplied exact `(id, name)` mapping. Baseline fixed
and MCP-entity resolvers do **not** forward `resources[].options` to the resolved
tool. In particular, `resources[].options.connection` does not configure
filesystem persistence. There is no implicit forwarding or new global
case-folding policy. Custom strategies may define their own options.

MCP-entity resolution appends `name` to these entity-name search prefixes, in order:

1. Current machine profile:
   `computer-user-profiles/users/username/<user>/computers/hostname/<host>/copilot/mcp-servers`.
2. `${USER}/mcp-servers`.
3. `defaults/mcp-servers`.

The first matching entity with an `mcp-server` property wins. For the example,
`defaults/mcp-servers/github` is a possible name; the MCP `serverName` field is
not the lookup key. Direct `kind: mcp` configuration is a separate path.

An unresolved resource preserves a placeholder and diagnostic while other tools
continue loading; it does not fail the whole session.

## Configured tools belong in the template

```json
{
  "name": "persistent-editor",
  "displayName": "Persistent editor",
  "template": {
    "kind": "prompt",
    "name": "persistent-editor",
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
}
```

Filesystem's top-level `connection` uses the real loader/save/reload provider
bridge. Absence means in-memory edit history; malformed supplied configuration
fails. `options.connection` is not a filesystem configuration location.

The special non-callable `chat-history` entry instead consumes
`options.connection`, uses in-memory persistence when absent, and retains its
runtime parse-failure/in-memory behavior. Exact custom-tool kind or name
`chat-history` selects that configuration; normal display names do not select
built-ins. Both persistence surfaces reference the shared provider schema:
MongoDB `container`/`external`, required nonempty database and collection,
container name/data directory and optional integer port 1–65535, or an external
connection string. See Agent Definition Schema for complete examples, host
requirements and provider-policy/dispatcher contracts.

Entity definitions, manifests and sessions resolve the embedded canonical and
shared references; the session schema does not duplicate this catalog.
