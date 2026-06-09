# Azure DevOps Organization Schema

An Azure DevOps organization entity representing a top-level organization in Azure DevOps.

## Description

The `azure-devops-organization` schema represents an Azure DevOps organization, combining external URL references and organization-specific metadata.

## Properties

### organization-name (string)
The Azure DevOps organization name.

**Type:** `string`  
**Required:** No  
**Description:** The name identifier of the Azure DevOps organization

### Inherited from external.json
- `urls`: Map of URL references to Azure DevOps resources

### Inherited from entity.json
- `entity-id`: Unique identifier for this entity
- `entity-types`: Classification of entity types
- `names`: Array name patterns for identification
- `display-name`: Human-readable name with language localization
- `content`: Associated content with MIME type and reference

## Naming Convention

Names follow the pattern: `["entity-types", "azure-devops-organization"]` for document identification. Alternative reference pattern: `["azure-devops", "organization"]`

## Example

```json
{
  "entity-id": "11111111-2222-3333-4444-555555555555",
  "entity-types": ["azure-devops-organization", "external"],
  "names": [
    ["json-schemas", "https://schemas.workspaces.phantom.to/workspaces/data/core/azure-devops-organization.json"],
    ["entity-types", "azure-devops-organization"],
    ["azure-devops", "organization"]
  ],
  "display-name": {
    "default": "Contoso Azure DevOps"
  },
  "organization-name": "contoso",
  "urls": {
    "api": "https://dev.azure.com/contoso/_apis",
    "web": "https://dev.azure.com/contoso"
  }
}
```

## See Also

- [entity.json](entity-schema.md) - Base entity schema
- [external.json](external-schema.md) - External references schema
- [azure-devops-project.json](azure-devops-project-schema.md) - Azure DevOps project schema
