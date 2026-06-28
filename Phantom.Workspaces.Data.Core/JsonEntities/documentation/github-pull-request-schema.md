# GitHub Pull Request Schema

A GitHub pull request entity extending the git-layer pull request schema with GitHub-specific number and identity fields.

## Description

The `github-pull-request` schema extends `git-pull-request.json` with `number` (the PR number within the repository), `github-node-id` (GraphQL global ID), `is-draft`, and `author` (the GitHub login of the PR author). It sits at the bottom of the three-tier pull request hierarchy: `pull-request` → `git-pull-request` → `github-pull-request`.

The `is-draft` field corresponds to `status: draft` in the base `pull-request.json`; discovery tools should set both fields consistently.

## Properties

### number (integer)

GitHub PR number within the repository.

**Type:** `integer`
**Required:** No
**Description:** The PR number displayed in GitHub UI and used in URLs

### github-node-id (string)

GitHub global node ID.

**Type:** `string`
**Required:** No
**Description:** Opaque base64-encoded global ID used by the GitHub GraphQL API

### is-draft (boolean)

Whether this pull request is in draft state.

**Type:** `boolean`
**Required:** No

### author (string)

GitHub login of the PR author.

**Type:** `string`
**Required:** No

### Inherited from git-pull-request.json

- `source-branch`: The branch being merged (head)
- `target-branch`: The branch being merged into (base)
- `source-commit`: Head commit SHA of the source branch
- `merge-commit`: Merge commit SHA if merged
- `repository`: Entity ID of the owning `git-repository`

### Inherited from pull-request.json

- `title`: Pull request title
- `status`: `open` / `draft` / `closed` / `merged`
- `labels`: Platform label strings

### Inherited from external.json

- `urls`: Map of URL references; `urls.default` is `https://github.com/<owner>/<repo>/pull/<number>`

### Inherited from entity.json

- `entity-id`, `entity-types`, `names`, `display-name`, `content`

## Naming Convention

Names follow the pattern: `["github", <owner-login>, <repo-name>, "pull-requests", <number>]`

## Status Mapping

| GitHub state | `status` value |
|---|---|
| open (not draft) | `open` |
| open (draft) | `draft` |
| merged | `merged` |
| closed without merge | `closed` |

## Example

```json
{
  "entity-id": "99887766-5544-3322-1100-aabbccddeeff",
  "entity-types": ["github-pull-request", "git-pull-request", "pull-request", "task", "external"],
  "names": [["github", "my-org", "my-repo", "pull-requests", "42"]],
  "display-name": { "default": "Add new feature" },
  "status": "open",
  "number": 42,
  "github-node-id": "PR_kwDOBxxxxxxx",
  "author": "octocat",
  "is-draft": false,
  "source-branch": "feat/new-feature",
  "target-branch": "main",
  "source-commit": "abc123def456",
  "repository": "11223344-5566-7788-99aa-bbccddeeff00",
  "urls": { "default": "https://github.com/my-org/my-repo/pull/42" }
}
```

## See Also

- [git-pull-request.json](git-pull-request-schema.md) - Git pull request schema
- [github-repository.json](github-repository-schema.md) - GitHub repository schema
- [github-work-item.json](github-work-item-schema.md) - GitHub work item schema
