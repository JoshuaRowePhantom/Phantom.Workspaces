# Azure DevOps Repository Schema

An Azure DevOps repository entity extending the git-layer repository schema with ADO-specific identity fields. This is the Azure DevOps platform-specific variant of the `git-repository` / `repository` concept.

## Description

The `azure-devops-repository` schema represents a Git repository hosted within an Azure DevOps project. It extends `git-repository.json` (which extends `repository.json`) with a `repository-id` (the ADO repository GUID) and a `project` entity-id reference pointing to the containing `azure-devops-project`.

It sits at the bottom of the three-tier repository hierarchy: `repository` → `git-repository` → `azure-devops-repository`.

## Composition

`azure-devops-repository` composes:
- `git-repository.json` — HTTPS clone URL and SSH clone URL
  - `repository.json` — `default-branch` and `description`
    - `entity.json` — base entity fields
    - `external.json` — canonical web URL via `urls`

## Properties

### repository-id (string)

Azure DevOps repository GUID.

**Type:** `string`
**Required:** No
**Description:** The unique GUID assigned by Azure DevOps for this repository

### project (entity-id)

Entity ID of the containing Azure DevOps project.

**Type:** `string` (UUID, x-entity-types: `azure-devops-project`)
**Required:** No

### Inherited from git-repository.json

- `clone-url`: HTTPS clone URL
- `ssh-url`: SSH clone URL

### Inherited from repository.json

- `default-branch`: Default branch name
- `description`: Short repository description

### Inherited from external.json

- `urls`: Map of URL references; `urls.default` is `https://dev.azure.com/<org>/<project>/_git/<repo>`

### Inherited from entity.json

- `entity-id`, `entity-types`, `names`, `display-name`, `content`

## Naming Convention

Names follow the pattern: `["azure-devops", <organization-name>, <project-name>, <repo-name>]`

## urls Convention

Set `urls.default` to the repository web URL:
```
https://dev.azure.com/<organization>/<project>/_git/<repo-name>
```

## Example

```json
{
  "entity-id": "33334444-5555-6666-7777-888899990000",
  "entity-types": ["azure-devops-repository", "git-repository", "repository", "external"],
  "names": [["azure-devops", "contoso", "my-project", "my-repo"]],
  "display-name": { "default": "my-repo" },
  "repository-id": "abcdefab-cdef-abcd-efab-cdefabcdefab",
  "project": "22222222-3333-4444-5555-666666666666",
  "clone-url": "https://contoso@dev.azure.com/contoso/my-project/_git/my-repo",
  "urls": {
    "default": "https://dev.azure.com/contoso/my-project/_git/my-repo"
  }
}
```

## See Also

- [git-repository.json](git-repository-schema.md) - Git repository schema
- [repository.json](repository-schema.md) - Platform-agnostic repository schema
- [azure-devops-project.json](azure-devops-project-schema.md) - Azure DevOps project schema
- [azure-devops-pull-request.json](azure-devops-pull-request-schema.md) - Azure DevOps pull request schema
