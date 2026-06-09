# Git Worktree Schema

A git worktree entity representing a git working tree on a filesystem.

## Description

The `git-worktree` schema represents a git worktree, which is a lightweight working directory associated with a git repository. It inherits from both `entity.json` and `filesystem-path.json`, meaning it automatically has the `hosted-on-computer` requirement (must be associated with a computer or user profile on a computer).

## Properties

### Inherited from filesystem-path.json
- `path`: The filesystem path of the worktree

### Inherited from entity.json
- `entity-id`: Unique identifier for this entity
- `entity-types`: Classification of entity types
- `names`: Array name patterns for identification
- `display-name`: Human-readable name with language localization
- `content`: Associated content with MIME type and reference

## Constraints

Entities of type `git-worktree` must:
1. Be of type both `git-worktree` and `filesystem-path`
2. Have a `path` property defining the filesystem location
3. Have at least one name entry that references either:
   - A `computer` entity: `["computer", "<name-type>", "<value>"]`
   - A `computer-user-profile` entity: `["computer-user-profiles", "<computer>", "<user>"]`

These constraints ensure that worktrees are always associated with a specific computer or user profile on a computer.

## Example

```json
{
  "entity-id": "44444444-5555-6666-7777-888888888888",
  "entity-types": ["git-worktree", "filesystem-path"],
  "names": [
    ["git-worktrees", "feature-branch"],
    ["computer", "hostname", "devbox"]
  ],
  "display-name": {
    "default": "Feature Branch Worktree"
  },
  "path": "/home/dev/repos/myproject-feature"
}
```

## See Also

- [entity.json](entity-schema.md) - Base entity schema
- [filesystem-path.json](filesystem-path-schema.md) - Filesystem path schema
- [computer.json](computer-schema.md) - Computer schema
- [git.json](git-schema.md) - Git schema
