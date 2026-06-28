# Repository Schema

A platform-agnostic repository entity representing a source-control repository on any hosting platform.

## Description

The `repository` schema is the base type for all repository entities regardless of hosting platform (GitHub, Azure DevOps, GitLab, etc.). It combines `entity.json` and `external.json`, meaning it carries a `urls` map with a canonical web URL. Platform-specific subtypes extend this schema with hosting-specific fields.

## Properties

### default-branch (string)
The default branch of the repository.

**Type:** `string`  
**Required:** No  
**Description:** The default branch name (e.g. `main` or `master`)

### description (string)
A short description of the repository.

**Type:** `string`  
**Required:** No

### Inherited from external.json
- `urls`: Map of URL references; `urls.default` is the canonical web URL

### Inherited from entity.json
- `entity-id`: Unique identifier for this entity
- `entity-types`: Classification of entity types
- `names`: Array name patterns for identification
- `display-name`: Human-readable name with language localization
- `content`: Associated content with MIME type and reference

## Naming Convention

Names follow the pattern: `["repositories", <organization-name>, <repository-name>]`

## Notes

This is an abstract base type. Tools should create platform-specific subtypes rather than raw `repository` entities.

## Example

```json
{
  "entity-id": "11223344-5566-7788-99aa-bbccddeeff00",
  "entity-types": ["repository", "external"],
  "names": [["repositories", "my-org", "my-repo"]],
  "display-name": { "default": "my-repo" },
  "default-branch": "main",
  "description": "My repository",
  "urls": { "default": "https://github.com/my-org/my-repo" }
}
```

## See Also

- [entity.json](entity-schema.md) - Base entity schema
- [external.json](external-schema.md) - External references schema
