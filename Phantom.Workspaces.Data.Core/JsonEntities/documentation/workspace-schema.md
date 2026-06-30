# Workspace schema

Defines saved workspace layout with regions and tab content declarations.

## Expected shape

```json
{
  "entity-id": "<uuid>",
  "entity-types": ["workspace", ...],
  "names": [["workspaces", "<name>"]],
  "display-name": { "default": "Workspace" },
  "regions": [
    {
      "region-id": "center",
      "title": "Center",
      "dock": "center",
      "size": 1,
      "tabs": [ ... ]
    }
  ]
}
```

## Tab content variants

1. Entity tab:
```json
{ "kind": "entity-view", "content": { "target-entity-name": ["documentation","getting-started"] } }
```
2. Browser tab:
```json
{ "kind": "browser-view", "content": { "url": "https://example.com" } }
```

## Choosing a tab kind

Use **`entity-view`** whenever you want to open a URL that should be persisted, shared, or revisited. Create an `external` entity to hold the URL, then reference it via `entity-view`. This is the correct choice for any meaningful URL (documentation, dashboards, external tools, websites).

Use **`browser-view`** only for transient, one-off URLs — a single ephemeral navigation that does not need to be remembered or associated with any entity.

**Decision rules:**
- Has a URL that belongs to a resource or service the user cares about? → create an `external` entity + use `entity-view`
- Is it a disposable/throw-away URL with no ongoing relevance? → `browser-view` is acceptable

## Adding a URL tab: the external entity pattern

To open a URL as a persistent tab in a workspace:

1. Call `workspaces_entity_generate_guid` twice to get two GUIDs — one for the `external` entity and one for the `related` relationship.
2. In a single `workspaces_entity_update` call:
   - **Create the `external` entity** with `entity-types: ["entity", "external"]`, named `["external", "<name>"]`, and the URL stored under the `"default"` key in `urls`.
   - **Create the `related` relationship** linking the workspace entity ID and the new external entity ID (include a `note`).
   - **Patch the workspace entity** to add an `entity-view` tab pointing at the new external entity's name.

Full example (three changes in one update):

```json
{
  "update-metadata": { "comment": { "text": "Add external URL tab to workspace" } },
  "changes": [
    {
      "entity-id": "<external-guid>",
      "entity-change-mode": "replace",
      "data": {
        "entity-id": "<external-guid>",
        "entity-types": ["entity", "external"],
        "names": [["external", "my-dashboard"]],
        "display-name": { "default": "My Dashboard" },
        "urls": { "default": "https://dashboard.example.com" }
      }
    },
    {
      "entity-id": "<related-guid>",
      "entity-change-mode": "replace",
      "data": {
        "entity-id": "<related-guid>",
        "entity-types": ["entity", "related", "relationship"],
        "participants": {
          "source": "<workspace-entity-id>",
          "target": "<external-guid>"
        },
        "note": "Surfaces the external entity as a tab in the workspace."
      }
    },
    {
      "entity-id": "<workspace-entity-id>",
      "concurrency-tag": "<current-concurrency-tag>",
      "entity-change-mode": "json-patch",
      "data": [
        {
          "op": "add",
          "path": "/regions/0/tabs/-",
          "value": {
            "tab-id": "<stable-tab-id>",
            "title": "My Dashboard",
            "kind": "entity-view",
            "content": { "target-entity-name": ["external", "my-dashboard"] }
          }
        }
      ]
    }
  ]
}
```

## Guidance

- Region/tab objects are closed (`unevaluatedProperties=false`), so keep to declared fields.
- Use stable `tab-id` and `region-id` values for deterministic workspace restores.

## Adding related entities to a workspace

To surface a related entity as a collapsed sub-item in the Workspaces view, follow **two steps**:

1. **Add the tab** — add a workspace-tab entry inside a region's `tabs` array in the workspace entity.
2. **Create a `related` relationship** — create a new entity with `entity-types: ["entity", "related"]` whose participants co-list the workspace entity ID and the target entity ID.  The workspaces view fetches `relationships-to-return: [{"relationship-type-names": ["related"]}]` and renders each linked entity as a collapsed sub-item.
