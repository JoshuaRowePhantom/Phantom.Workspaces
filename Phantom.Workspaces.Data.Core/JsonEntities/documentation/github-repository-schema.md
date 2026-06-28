# GitHub Repository Schema

A GitHub repository entity extending the git-layer repository schema with GitHub-specific identity and metadata fields.

## Description

The `github-repository` schema extends `git-repository.json` with `github-repo-id` (numeric), `github-node-id` (GraphQL global ID), an `owner` entity-id reference pointing to the owning `github-organization`, `is-fork`, and `is-archived`. It sits at the bottom of the three-tier repository hierarchy: `repository` → `git-repository` → `github-repository`.

## Properties

### github-repo-id (integer)

GitHub numeric repository ID.

**Type:** `integer`
**Required:** No
**Description:** The stable numeric ID assigned by GitHub to this repository

### github-node-id (string)

GitHub global node ID.

**Type:** `string`
**Required:** No
**Description:** Opaque base64-encoded global ID used by the GitHub GraphQL API

### owner (entity-id)

Entity ID of the owning GitHub organization or user.

**Type:** `string` (UUID, x-entity-types: `github-organization`)
**Required:** No

### is-fork (boolean)

Whether this repository is a fork.

**Type:** `boolean`
**Required:** No

### is-archived (boolean)

Whether this repository has been archived.

**Type:** `boolean`
**Required:** No

### Inherited from git-repository.json

- `clone-url`: HTTPS clone URL
- `ssh-url`: SSH clone URL

### Inherited from repository.json

- `default-branch`: Default branch name
- `description`: Short repository description

### Inherited from external.json

- `urls`: Map of URL references; `urls.default` is `https://github.com/<owner>/<repo>`

### Inherited from entity.json

- `entity-id`, `entity-types`, `names`, `display-name`, `content`

## Naming Convention

Names follow the pattern: `["github", <owner-login>, <repo-name>]`

## Example

```json
{
  "entity-id": "11223344-5566-7788-99aa-bbccddeeff00",
  "entity-types": ["github-repository", "git-repository", "repository", "external"],
  "names": [["github", "my-org", "my-repo"]],
  "display-name": { "default": "my-repo" },
  "default-branch": "main",
  "clone-url": "https://github.com/my-org/my-repo.git",
  "ssh-url": "git@github.com:my-org/my-repo.git",
  "github-repo-id": 123456789,
  "github-node-id": "MDEwOlJlcG9zaXRvcnkxMjM0NTY3ODk=",
  "owner": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "is-fork": false,
  "is-archived": false,
  "urls": { "default": "https://github.com/my-org/my-repo" }
}
```

## See Also

- [git-repository.json](git-repository-schema.md) - Git repository schema
- [github-organization.json](github-organization-schema.md) - GitHub organization schema
- [github-pull-request.json](github-pull-request-schema.md) - GitHub pull request schema
- [github-work-item.json](github-work-item-schema.md) - GitHub work item schema
