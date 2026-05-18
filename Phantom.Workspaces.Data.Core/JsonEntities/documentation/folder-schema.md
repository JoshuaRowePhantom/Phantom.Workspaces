# Folder schema

Defines folder entities used to materialize name-prefix hierarchy.

## Expected shape

```json
{
  "entity-id": "<stable deterministic id>",
  "entity-types": ["folder"],
  "$schema": "https://schemas.workspaces.phantom.to/workspaces/data/core/folder.json",
  "names": [["prefix", "child"]],
  "display-name": { "default": "<last name segment>" }
}
```

## Guidance

- Folder entities represent prefixes, not the leaf entity itself.
- Exactly one name is allowed per folder entity.
- The root folder uses an empty name path (`[]`) and a display name of `root`.
- Referential integrity creates missing prefix folders as named entities are written.
