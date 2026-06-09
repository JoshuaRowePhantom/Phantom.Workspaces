# Filesystem Path Schema

Filesystem path entities represent paths to files on a filesystem.
They are typically not used in isolation, but as part of another
entity type, such as a git-workspace.

## Expected shape

```json
{
  "entity-id": "<stable deterministic id>",
  "entity-types": ["folder"],
  "$schema": "https://schemas.workspaces.phantom.to/workspaces/data/core/folder.json",
  "names": [["prefix", "child"]],
  "path": "c:\\path\\"
}
```

## Guidance

- A filesystem-path entity is typically not created in isolation, but as part of another entity type, such as a git-workspace.
