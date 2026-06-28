# GitHub Organization Schema

A GitHub organization (or user account) entity extending the base organization schema with GitHub-specific identity fields.

## Description

The `github-organization` schema extends `organization.json` with `github-login` (the URL-slug used in all GitHub URLs and API calls) and `github-node-id` (the opaque global node ID used by the GitHub GraphQL API). It is the top-level container in the GitHub entity hierarchy; `github-repository` entities reference it via their `owner` field.

There is no intermediate git-layer organization type — git has no concept of an organization — so `github-organization` extends the platform-agnostic `organization` base directly.

## Properties

### github-login (string)

GitHub org/user login (slug).

**Type:** `string`
**Required:** No
**Description:** The login name used in GitHub URLs, e.g. `github.com/<login>`

### github-node-id (string)

GitHub global node ID.

**Type:** `string`
**Required:** No
**Description:** Opaque base64-encoded global ID used by the GitHub GraphQL API

### Inherited from organization.json

- `organization-name`: Canonical organization/account name

### Inherited from external.json

- `urls`: Map of URL references; `urls.default` is `https://github.com/<login>`

### Inherited from entity.json

- `entity-id`, `entity-types`, `names`, `display-name`, `content`

## Naming Convention

Names follow the pattern: `["github", <login>]`

The `entity-types` discriminator `github-organization` identifies it within the organization hierarchy.

## Example

```json
{
  "entity-id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "entity-types": ["github-organization", "organization", "external"],
  "names": [["github", "my-org"]],
  "display-name": { "default": "my-org" },
  "organization-name": "my-org",
  "github-login": "my-org",
  "github-node-id": "MDEyOk9yZ2FuaXphdGlvbjEyMzQ1",
  "urls": { "default": "https://github.com/my-org" }
}
```

## See Also

- [organization.json](organization-schema.md) - Base organization schema
- [github-repository.json](github-repository-schema.md) - GitHub repository schema
