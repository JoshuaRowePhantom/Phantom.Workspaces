# Workspace GUI Tool Instructions

Use these tools to interact with the live workspace UI: open and close workspace panes, open and close tabs, and invoke entity shortcuts.

## Available tools

- **workspace_list** — list all open workspace panes
- **tab_list** — list open tabs in a workspace pane (defaults to selected pane)
- **workspace_close** — close a workspace pane by entity-id; no-ops on unknown or default placeholder panes
- **tab_close** — close a tab by tab id; no-ops if not found
- **entity_invoke_shortcut** — invoke a named shortcut (Open, Json, Delete, StartAgentSession, StartShell) on an entity
- **open_tab** — open a new tab (entity, url, or shell target)

## Guidelines

- Use **workspace_list** first to discover open panes before targeting a specific workspace.
- Use **tab_list** to inspect tabs in a pane before closing or focusing one.
- To navigate to an entity, prefer **entity_invoke_shortcut** with shortcut `Open` over opening a new tab.
- **open_tab** with `target=entity` de-duplicates: if the entity is already open it activates the existing tab.
- **entity_invoke_shortcut** returns `{handled: false}` when no handler applies (entity type does not support that shortcut).
- Never infer entity ids — always look them up via workspaces_entity_get or from prior tool results.
