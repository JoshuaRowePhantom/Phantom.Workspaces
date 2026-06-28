# Git Repository Schema

A git-layer repository entity extending the base repository schema with git remote URL fields.

## Description

The `git-repository` schema extends `repository.json` with git-specific remote URL fields (`clone-url`, `ssh-url`). It sits between the platform-agnostic `repository` base type and platform-specific subtypes (e.g. `github-repository`). Naming follows the same convention as `repository`; the `entity-types` discriminator (`git-repository`) identifies it.

## Properties

### clone-url (string)
HTTPS clone URL.

**Type:** `string` (URI format)  
**Required:** No  
**Description:** The HTTPS URL used to clone this repository (e.g. `https://github.com/org/repo.git`)

### ssh-url (string)
SSH clone URL.

**Type:** `string`  
**Required:** No  
**Description:** The SSH URL used to clone this repository (e.g. `git@github.com:org/repo.git`)

### Inherited from repository.json
- `default-branch`: Default branch name
- `description`: Short repository description

### Inherited from external.json
- `urls`: Map of URL references; `urls.default` is the canonical web URL

### Inherited from entity.json
- `entity-id`, `entity-types`, `names`, `display-name`, `content`

## Naming Convention

Names follow the same pattern as `repository`: `["repositories", <organization-name>, <repository-name>]`

The `entity-types` array discriminates `git-repository` from plain `repository` entities.

## Example

```json
{
  "entity-id": "11223344-5566-7788-99aa-bbccddeeff00",
  "entity-types": ["git-repository", "repository", "external"],
  "names": [["repositories", "my-org", "my-repo"]],
  "display-name": { "default": "my-repo" },
  "default-branch": "main",
  "clone-url": "https://github.com/my-org/my-repo.git",
  "ssh-url": "git@github.com:my-org/my-repo.git",
  "urls": { "default": "https://github.com/my-org/my-repo" }
}
```

## See Also

- [repository.json](repository-schema.md) - Base repository schema
- [git-pull-request.json](git-pull-request-schema.md) - Git pull request schema
- [git-work-item.json](git-work-item-schema.md) - Git work item schema
