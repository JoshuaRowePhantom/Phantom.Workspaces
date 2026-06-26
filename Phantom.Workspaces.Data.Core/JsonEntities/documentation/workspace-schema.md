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

## Guidance

- Region/tab objects are closed (`unevaluatedProperties=false`), so keep to declared fields.
- Use stable `tab-id` and `region-id` values for deterministic workspace restores.

## Adding related entities to a workspace

To surface a related entity as a collapsed sub-item in the Workspaces view, follow **two steps**:

1. **Add the tab** — add a workspace-tab entry inside a region's `tabs` array in the workspace entity.
2. **Create a `related` relationship** — create a new entity with `entity-types: ["entity", "related"]` whose participants co-list the workspace entity ID and the target entity ID.  The workspaces view fetches `relationships-to-return: [{"relationship-type-names": ["related"]}]` and renders each linked entity as a collapsed sub-item.
