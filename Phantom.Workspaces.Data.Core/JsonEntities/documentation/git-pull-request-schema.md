# Git Pull Request Schema

A git-layer pull request entity extending the base pull request schema with branch and commit fields.

## Description

The `git-pull-request` schema extends `pull-request.json` with git-specific fields: `source-branch`, `target-branch`, `source-commit`, `merge-commit`, and a `repository` entity-id reference to the owning `git-repository`. The `repository` field is a direct property (not a relationship) so view queries can group PRs under their repository without traversing relationship entities.

Discovery tools that create `git-pull-request` entities should also create a `related` relationship entity linking the PR to its `git-repository` so `ViewHierarchyAssembler` can group them in the pull-requests view.

## Properties

### source-branch (string)
The branch being merged (head).

**Type:** `string`  
**Required:** No

### target-branch (string)
The branch being merged into (base).

**Type:** `string`  
**Required:** No

### source-commit (string)
Head commit SHA of the source branch.

**Type:** `string`  
**Required:** No

### merge-commit (string)
Merge commit SHA if merged.

**Type:** `string`  
**Required:** No

### repository (entity-id)
Entity ID of the `git-repository` this pull request belongs to.

**Type:** `string` (UUID, x-entity-types: `git-repository`)  
**Required:** No

### Inherited from pull-request.json
- `title`: Pull request title
- `status`: `open` / `draft` / `closed` / `merged`
- `labels`: Platform label strings

### Inherited from task.json
- `assigned-to`: Assignee from the originating system

### Inherited from external.json
- `urls`: Map of URL references; `urls.default` is the canonical web URL

### Inherited from entity.json
- `entity-id`, `entity-types`, `names`, `display-name`, `content`

## Naming Convention

Names follow the pattern: `["pull-requests", <organization-name>, <repository-name>, <pull-request-id>]`

## Example

```json
{
  "entity-id": "99887766-5544-3322-1100-aabbccddeeff",
  "entity-types": ["git-pull-request", "pull-request", "task", "external"],
  "names": [["pull-requests", "my-org", "my-repo", "7"]],
  "display-name": { "default": "Add new feature" },
  "status": "open",
  "source-branch": "feat/new-feature",
  "target-branch": "main",
  "source-commit": "abc123def456",
  "repository": "11223344-5566-7788-99aa-bbccddeeff00",
  "urls": { "default": "https://github.com/my-org/my-repo/pull/7" }
}
```

## See Also

- [pull-request.json](pull-request-schema.md) - Base pull request schema
- [git-repository.json](git-repository-schema.md) - Git repository schema
- [git-work-item.json](git-work-item-schema.md) - Git work item schema
- [related.json](related-schema.md) - Relationship schema for grouping PRs under repositories
