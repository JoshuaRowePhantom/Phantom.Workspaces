# Pull Request Schema

A platform-agnostic pull request entity representing a code review request on any hosting platform.

## Description

The `pull-request` schema is the base type for all pull request entities regardless of platform (GitHub, Azure DevOps, GitLab, etc.). It combines `entity.json`, `task.json`, and `external.json`, inheriting task management fields and external URL references.

## Properties

### title (string)
Title of the pull request.

**Type:** `string`  
**Required:** No

### status (string)
Current status of the pull request.

**Type:** `string`  
**Enum:** `open`, `draft`, `closed`, `merged`  
**Required:** No  
**Good status values:** `merged`  
**Bad status values:** `closed`

### labels (array of string)
Platform-native label/tag strings.

**Type:** `array`  
**Required:** No

### Inherited from task.json
- `assigned-to`: Assignee value from the originating system

### Inherited from external.json
- `urls`: Map of URL references; `urls.default` is the canonical web URL

### Inherited from entity.json
- `entity-id`, `entity-types`, `names`, `display-name`, `content`

## Naming Convention

Names follow the pattern: `["pull-requests", <organization-name>, <repository-name>, <pull-request-id>]`

## Notes

This is an abstract base type. Tools should create platform-specific subtypes rather than raw `pull-request` entities.

## Example

```json
{
  "entity-id": "99887766-5544-3322-1100-aabbccddeeff",
  "entity-types": ["pull-request", "task", "external"],
  "names": [["pull-requests", "my-org", "my-repo", "7"]],
  "display-name": { "default": "Add new feature" },
  "status": "open",
  "labels": ["enhancement"],
  "urls": { "default": "https://github.com/my-org/my-repo/pull/7" }
}
```

## See Also

- [entity.json](entity-schema.md) - Base entity schema
- [task.json](task-schema.md) - Task schema
- [external.json](external-schema.md) - External references schema
