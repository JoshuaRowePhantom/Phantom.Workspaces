# Workspaces profile schema

Defines persisted user-level workspace preferences.

## Expected shape

```json
{
  "entity-id": "<uuid>",
  "entity-types": ["workspaces-profile", ...],
  "names": [["defaults","profiles","default"]],
  "theme": "dark|light",
  "initial-workspace": "<entity-id>",
  "opened-workspaces": ["<entity-id>", "..."]
}
```

## Guidance

- `opened-workspaces` must be unique.
- `initial-workspace` should be one of the opened workspace ids for coherent startup behavior.
