# Azure DevOps Organization Schema

An Azure DevOps organization entity representing a top-level organization in Azure DevOps. This is the Azure DevOps platform-specific variant of the `organization` concept.

## Description

The `azure-devops-organization` schema represents an Azure DevOps organization, combining external URL references and organization-specific metadata. It is semantically equivalent to the platform-agnostic `organization` type (sharing the `organization-name` property and naming convention), but is typed specifically for Azure DevOps contexts.

## Composition

`azure-devops-organization` composes:
- `organization.json` — `organization-name` field
  - `entity.json` — base entity fields
  - `external.json` — canonical web URL via `urls`

It is the Azure DevOps implementation of the `organization` concept. Discovery tools should create `azure-devops-organization` entities rather than plain `organization` entities when connecting to Azure DevOps.

## Properties

### organization-name (string)
The Azure DevOps organization name.

**Type:** `string`  
**Required:** No  
**Description:** The short name used to identify the Azure DevOps organization (e.g. `contoso`)

### Inherited from external.json
- `urls`: Map of URL references; `urls.default` (or `urls.web`) is the canonical web URL

### Inherited from entity.json
- `entity-id`: Unique identifier for this entity
- `entity-types`: Classification of entity types
- `names`: Array name patterns for identification
- `display-name`: Human-readable name with language localization
- `content`: Associated content with MIME type and reference

## Naming Convention

Names follow the pattern: `["organizations", <organization-name>]`

This aligns with the platform-agnostic `organization` naming convention so the two types are interchangeable in name-based lookups.

## urls Convention

Set `urls.default` (or `urls.web`) to the Azure DevOps organization root URL:
```
https://dev.azure.com/<organization-name>
```

## Example

```json
{
  "entity-id": "11111111-2222-3333-4444-555555555555",
  "entity-types": ["azure-devops-organization", "external"],
  "names": [["organizations", "contoso"]],
  "display-name": { "default": "Contoso" },
  "organization-name": "contoso",
  "urls": {
    "default": "https://dev.azure.com/contoso",
    "api": "https://dev.azure.com/contoso/_apis"
  }
}
```

## See Also

- [organization.json](organization-schema.md) - Platform-agnostic organization schema
- [entity.json](entity-schema.md) - Base entity schema
- [external.json](external-schema.md) - External references schema
- [azure-devops-project.json](azure-devops-project-schema.md) - Azure DevOps project schema
