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
