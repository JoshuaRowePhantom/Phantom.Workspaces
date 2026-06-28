# Azure DevOps Pull Request Schema

An Azure DevOps pull request entity extending the git-layer pull request schema with ADO-specific identity and status fields. This is the Azure DevOps platform-specific variant of the `git-pull-request` / `pull-request` concept.

## Description

The `azure-devops-pull-request` schema represents a pull request in Azure DevOps Repos. It extends `git-pull-request.json` (which extends `pull-request.json`) with `pull-request-id` (the ADO numeric PR ID), `is-draft`, `author` (ADO unique name), and `merge-status` (the ADO merge policy status).

It sits at the bottom of the three-tier pull request hierarchy: `pull-request` → `git-pull-request` → `azure-devops-pull-request`.

## Composition

`azure-devops-pull-request` composes:
- `git-pull-request.json` — `source-branch`, `target-branch`, `source-commit`, `merge-commit`, `repository`
  - `pull-request.json` — `title`, `status`, `labels`
    - `entity.json` — base entity fields
    - `task.json` — `status` and `assigned-to`
    - `external.json` — canonical web URL via `urls`

## Properties

### pull-request-id (integer)

ADO pull request numeric ID.

**Type:** `integer`
**Required:** No
**Description:** The numeric identifier displayed in the Azure DevOps UI and used in URLs

### is-draft (boolean)

Whether this pull request is in draft state.

**Type:** `boolean`
**Required:** No
**Description:** Corresponds to `status: draft` in the base `pull-request.json`; discovery tools should set both consistently

### author (string)

ADO unique name of the author.

**Type:** `string`
**Required:** No
**Description:** The ADO unique name (email format) of the pull request creator

### merge-status (string)

ADO merge status.

**Type:** `string`
**Required:** No
**Description:** ADO merge policy status: `notSet` / `queued` / `conflicts` / `succeeded` / `rejectedByPolicy` / `failure`

### Inherited from git-pull-request.json

- `source-branch`: The branch being merged (head)
- `target-branch`: The branch being merged into (base)
- `source-commit`: Head commit SHA of the source branch
- `merge-commit`: Merge commit SHA if merged
- `repository`: Entity ID of the owning `git-repository` (should point to an `azure-devops-repository`)

### Inherited from pull-request.json

- `title`: Pull request title
- `status`: `open` / `draft` / `closed` / `merged`
- `labels`: Platform label strings

### Inherited from external.json

- `urls`: Map of URL references; `urls.default` is `https://dev.azure.com/<org>/<project>/_git/<repo>/pullrequest/<id>`

### Inherited from entity.json

- `entity-id`, `entity-types`, `names`, `display-name`, `content`

## Naming Convention

Names follow the pattern: `["azure-devops", <organization-name>, <project-name>, <repo-name>, "pull-requests", <pull-request-id>]`

## Status Mapping

| ADO status | `status` value |
|---|---|
| active (not draft) | `open` |
| active (draft) | `draft` |
| completed | `merged` |
| abandoned | `closed` |

## urls Convention

Set `urls.default` to the pull request URL:
```
https://dev.azure.com/<organization>/<project>/_git/<repo>/pullrequest/<pull-request-id>
```

## Example

```json
{
  "entity-id": "44445555-6666-7777-8888-999900001111",
  "entity-types": ["azure-devops-pull-request", "git-pull-request", "pull-request", "task", "external"],
  "names": [["azure-devops", "contoso", "my-project", "my-repo", "pull-requests", "42"]],
  "display-name": { "default": "Add new feature" },
  "status": "open",
  "pull-request-id": 42,
  "is-draft": false,
  "author": "jane.smith@contoso.com",
  "merge-status": "succeeded",
  "source-branch": "refs/heads/feat/new-feature",
  "target-branch": "refs/heads/main",
  "repository": "33334444-5555-6666-7777-888899990000",
  "urls": {
    "default": "https://dev.azure.com/contoso/my-project/_git/my-repo/pullrequest/42"
  }
}
```

## See Also

- [git-pull-request.json](git-pull-request-schema.md) - Git pull request schema
- [pull-request.json](pull-request-schema.md) - Platform-agnostic pull request schema
- [azure-devops-repository.json](azure-devops-repository-schema.md) - Azure DevOps repository schema
- [azure-devops-project.json](azure-devops-project-schema.md) - Azure DevOps project schema
