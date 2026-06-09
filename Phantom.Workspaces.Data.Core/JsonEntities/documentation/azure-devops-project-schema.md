# Azure DevOps Project Schema

An Azure DevOps project entity representing a project within an Azure DevOps organization.

## Description

The `azure-devops-project` schema represents an Azure DevOps project, combining external URL references and project-specific metadata with a reference to its parent organization.

## Properties

### organization-reference (string)
Reference to the parent azure-devops-organization entity.

**Type:** `string`  
**Required:** No  
**Description:** Identifier or reference to the parent azure-devops-organization entity

### project-id (string)
The Azure DevOps project ID (GUID).

**Type:** `string`  
**Required:** No  
**Description:** The unique GUID assigned by Azure DevOps for this project

### Inherited from external.json
- `urls`: Map of URL references to Azure DevOps resources

### Inherited from entity.json
- `entity-id`: Unique identifier for this entity
- `entity-types`: Classification of entity types
- `names`: Array name patterns for identification
- `display-name`: Human-readable name with language localization
- `content`: Associated content with MIME type and reference

## Naming Convention

Entities of type `azure-devops-project` should use names following the pattern: `["azure-devops", "<organization>", "<project>"]`

Example: `["azure-devops", "contoso", "my-project"]`

## Example

```json
{
  "entity-id": "22222222-3333-4444-5555-666666666666",
  "entity-types": ["azure-devops-project", "external"],
  "names": [
    ["json-schemas", "https://schemas.workspaces.phantom.to/workspaces/data/core/azure-devops-project.json"],
    ["entity-types", "azure-devops-project"],
    ["azure-devops", "contoso", "my-project"]
  ],
  "display-name": {
    "default": "My Project"
  },
  "organization-reference": "contoso-org",
  "project-id": "12345678-9abc-def0-1234-567890abcdef",
  "urls": {
    "api": "https://dev.azure.com/contoso/my-project/_apis",
    "web": "https://dev.azure.com/contoso/my-project",
    "boards": "https://dev.azure.com/contoso/my-project/_boards",
    "repos": "https://dev.azure.com/contoso/my-project/_git"
  }
}
```

## See Also

- [entity.json](entity-schema.md) - Base entity schema
- [external.json](external-schema.md) - External references schema
- [azure-devops-organization.json](azure-devops-organization-schema.md) - Azure DevOps organization schema
- [azure-devops-work-item.json](azure-devops-work-item-schema.md) - Azure DevOps work item schema
