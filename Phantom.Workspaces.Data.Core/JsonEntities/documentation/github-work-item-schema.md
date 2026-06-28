# GitHub Work Item Schema

A GitHub issue entity extending the git-layer work item schema with GitHub-specific number and identity fields.

## Description

The `github-work-item` schema extends `git-work-item.json` with `number` (the issue number within the repository), `github-node-id` (GraphQL global ID), `author` (the GitHub login of the issue author), and `milestone` (the milestone title if set). It sits at the bottom of the three-tier work item hierarchy: `work-item` → `git-work-item` → `github-work-item`.

GitHub issues and PRs share the same number namespace per repository. A GitHub issue linked to a PR is modelled as two separate entities (`github-work-item` and `github-pull-request`) connected by a `related` relationship entity.

## Properties

### number (integer)

GitHub issue number within the repository.

**Type:** `integer`
**Required:** No
**Description:** The issue number displayed in GitHub UI and used in URLs

### github-node-id (string)

GitHub global node ID.

**Type:** `string`
**Required:** No
**Description:** Opaque base64-encoded global ID used by the GitHub GraphQL API

### author (string)

GitHub login of the issue author.

**Type:** `string`
**Required:** No

### milestone (string)

Milestone title if set.

**Type:** `string`
**Required:** No

### Inherited from git-work-item.json

- `repository`: Entity ID of the owning `git-repository`
- `related-pull-requests`: Entity IDs of pull requests explicitly linked to this work item

### Inherited from work-item.json

- `title`: Work item title
- `status`: `open` / `in-progress` / `closed`
- `labels`: Platform label strings
- `related-commits`: Git commit SHAs linked to this work item

### Inherited from external.json

- `urls`: Map of URL references; `urls.default` is `https://github.com/<owner>/<repo>/issues/<number>`

### Inherited from entity.json

- `entity-id`, `entity-types`, `names`, `display-name`, `content`

## Naming Convention

Names follow the pattern: `["github", <owner-login>, <repo-name>, "work-items", <number>]`

## Status Mapping

| GitHub state | `status` value |
|---|---|
| open | `open` |
| closed | `closed` |

## Example

```json
{
  "entity-id": "aabbccdd-eeff-0011-2233-445566778899",
  "entity-types": ["github-work-item", "git-work-item", "work-item", "task", "external"],
  "names": [["github", "my-org", "my-repo", "work-items", "7"]],
  "display-name": { "default": "Fix crash on startup" },
  "status": "open",
  "number": 7,
  "github-node-id": "I_kwDOBxxxxxxx",
  "author": "octocat",
  "milestone": "v2.0",
  "repository": "11223344-5566-7788-99aa-bbccddeeff00",
  "urls": { "default": "https://github.com/my-org/my-repo/issues/7" }
}
```

## See Also

- [git-work-item.json](git-work-item-schema.md) - Git work item schema
- [github-repository.json](github-repository-schema.md) - GitHub repository schema
- [github-pull-request.json](github-pull-request-schema.md) - GitHub pull request schema
