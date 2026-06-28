# Git Work Item Schema

A git-layer work item entity extending the base work item schema with repository and pull request linkage fields.

## Description

The `git-work-item` schema extends `work-item.json` with a `repository` entity-id reference (pointing to the owning `git-repository`) and a `related-pull-requests` array of entity-id references to linked `git-pull-request` entities. The `related-pull-requests` field augments the base `work-item.related-commits` (commit SHAs) with typed entity references when the source platform provides explicit PR↔issue links.

## Properties

### repository (entity-id)
Entity ID of the `git-repository` this work item is filed against.

**Type:** `string` (UUID, x-entity-types: `git-repository`)  
**Required:** No

### related-pull-requests (array of entity-id)
Entity IDs of pull requests explicitly linked to this work item.

**Type:** `array` of `string` (UUID, x-entity-types: `git-pull-request`)  
**Required:** No  
**Description:** Populated by discovery tools when the source platform provides explicit PR↔issue links

### Inherited from work-item.json
- `title`: Work item title
- `status`: `open` / `in-progress` / `closed`
- `labels`: Platform label strings
- `related-commits`: Git commit SHAs linked to this work item

### Inherited from task.json
- `assigned-to`: Assignee from the originating system

### Inherited from external.json
- `urls`: Map of URL references; `urls.default` is the canonical web URL

### Inherited from entity.json
- `entity-id`, `entity-types`, `names`, `display-name`, `content`

## Naming Convention

Names follow the pattern: `["work-items", <organization-name>, <repository-name>, <work-item-id>]`

## Example

```json
{
  "entity-id": "aabbccdd-eeff-1122-3344-556677889900",
  "entity-types": ["git-work-item", "work-item", "task", "external"],
  "names": [["work-items", "my-org", "my-repo", "42"]],
  "display-name": { "default": "Fix the crash" },
  "status": "open",
  "labels": ["bug"],
  "repository": "11223344-5566-7788-99aa-bbccddeeff00",
  "related-pull-requests": ["99887766-5544-3322-1100-aabbccddeeff"],
  "urls": { "default": "https://github.com/my-org/my-repo/issues/42" }
}
```

## See Also

- [work-item.json](work-item-schema.md) - Base work item schema
- [git-repository.json](git-repository-schema.md) - Git repository schema
- [git-pull-request.json](git-pull-request-schema.md) - Git pull request schema
