# Filesystem Path Schema

A filesystem path entity representing a location in a filesystem, hosted on a computer.

## Description

The `filesystem-path` schema represents a filesystem path on a computer. It inherits from `entity.json` and is a type of `hosted-on-computer`, meaning it must have either a `computer` or `computer-user-profile` name pattern.

## Properties

### path (string)
The filesystem path.

**Type:** `string`  
**Required:** No  
**Description:** The full or relative filesystem path value

### Inherited from entity.json
- `entity-id`: Unique identifier for this entity
- `entity-types`: Classification of entity types
- `names`: Array name patterns for identification
- `display-name`: Human-readable name with language localization
- `content`: Associated content with MIME type and reference

## Constraints

Entities of type `filesystem-path` must have at least one name entry that references either:
- A `computer` entity: `["computer", "<name-type>", "<value>"]`
- A `computer-user-profile` entity: `["computer-user-profiles", "<computer>", "<user>"]`

This constraint enforces that filesystem paths are always associated with a specific computer or user profile on a computer (the `hosted-on-computer` concept).

## Example

```json
{
  "entity-id": "33333333-4444-5555-6666-777777777777",
  "entity-types": ["filesystem-path"],
  "names": [
    ["filesystem-path", "/home/user/projects"],
    ["computer", "dns", "myhost.example.com"]
  ],
  "display-name": {
    "default": "/home/user/projects on myhost"
  },
  "path": "/home/user/projects"
}
```

## See Also

- [entity.json](entity-schema.md) - Base entity schema
- [computer.json](computer-schema.md) - Computer schema
- [user-computer-profile.json](user-computer-profile-schema.md) - User profile on computer schema
- [git-worktree.json](git-worktree-schema.md) - Git worktree schema (inherits from filesystem-path)
