# Work Item Schema

A platform-agnostic work item entity representing an issue or task on any hosting platform.

## Description

The `work-item` schema is the base type for all work item entities regardless of platform (GitHub Issues, Azure DevOps Work Items, GitLab Issues, etc.). It combines `entity.json`, `task.json`, and `external.json`, inheriting task management fields (`assigned-to`) alongside external URL references.

## Properties

### title (string)
Title or summary of the work item.

**Type:** `string` (localized)  
**Required:** No

### status (string)
Current status of the work item.

**Type:** `string`  
**Enum:** `open`, `in-progress`, `closed`  
**Required:** No  
**Good status values:** `closed`

### labels (array of string)
Platform-native label/tag strings.

**Type:** `array`  
**Required:** No

### related-commits (array of string)
Git commit SHAs explicitly associated with this work item by the source platform.

**Type:** `array` of `string`  
**Required:** No

### Inherited from task.json
- `assigned-to`: Assignee value from the originating system

### Inherited from external.json
- `urls`: Map of URL references; `urls.default` is the canonical web URL

### Inherited from entity.json
- `entity-id`, `entity-types`, `names`, `display-name`, `content`

## Naming Convention

Names follow the pattern: `["work-items", <organization-name>, <repository-or-project-name>, <work-item-id>]`

## Notes

This is an abstract base type. Tools should create platform-specific subtypes rather than raw `work-item` entities.

## Example

```json
{
  "entity-id": "aabbccdd-eeff-1122-3344-556677889900",
  "entity-types": ["work-item", "task", "external"],
  "names": [["work-items", "my-org", "my-repo", "42"]],
  "display-name": { "default": "Fix the bug" },
  "status": "open",
  "labels": ["bug"],
  "urls": { "default": "https://github.com/my-org/my-repo/issues/42" }
}
```

## See Also

- [entity.json](entity-schema.md) - Base entity schema
- [task.json](task-schema.md) - Task schema
- [external.json](external-schema.md) - External references schema
