# Organization Schema

A platform-agnostic organization entity representing an organization or account on any hosting platform.

## Description

The `organization` schema is the base type for all organization entities regardless of platform (GitHub, Azure DevOps, GitLab, etc.). It combines `entity.json` and `external.json`, carrying a `urls` map with a canonical web URL. Platform-specific subtypes extend this schema with hosting-specific fields.

## Properties

### organization-name (string)
The canonical organization/account name used by the hosting platform.

**Type:** `string`  
**Required:** No  
**Description:** The short name used to identify the organization on its hosting platform (e.g. `my-org` on GitHub)

### Inherited from external.json
- `urls`: Map of URL references; `urls.default` is the canonical web URL

### Inherited from entity.json
- `entity-id`: Unique identifier for this entity
- `entity-types`: Classification of entity types
- `names`: Array name patterns for identification
- `display-name`: Human-readable name with language localization
- `content`: Associated content with MIME type and reference

## Naming Convention

Names follow the pattern: `["organizations", <organization-name>]`

## Notes

This is an abstract base type. Tools should create platform-specific subtypes rather than raw `organization` entities.

## Example

```json
{
  "entity-id": "aabbccdd-eeff-1122-3344-556677889900",
  "entity-types": ["organization", "external"],
  "names": [["organizations", "my-org"]],
  "display-name": { "default": "My Organization" },
  "organization-name": "my-org",
  "urls": { "default": "https://github.com/my-org" }
}
```

## See Also

- [entity.json](entity-schema.md) - Base entity schema
- [external.json](external-schema.md) - External references schema
