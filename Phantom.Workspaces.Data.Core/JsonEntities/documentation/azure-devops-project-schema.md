# Azure DevOps Project Schema

An Azure DevOps project entity representing a project within an Azure DevOps organization. In the platform-agnostic type hierarchy, an Azure DevOps project plays the role of a `repository` — it is the container for source code, work items, and pipelines within one Azure DevOps organization.

## Description

The `azure-devops-project` schema represents an Azure DevOps project, combining external URL references and project-specific metadata with a reference to its parent organization. Discovery tools should create `azure-devops-project` entities to represent ADO projects; they occupy the same position in the naming hierarchy that `repository` entities occupy for Git-based platforms.

## Composition

`azure-devops-project` composes:
- `repository.json` — `default-branch` and `description` fields
  - `entity.json` — base entity fields
  - `external.json` — canonical web URL via `urls`

It is the Azure DevOps platform-specific counterpart to the platform-agnostic `repository` concept, sharing the `["repositories", <org>, <project>]` naming convention.

## Properties

### project-id (string)
The Azure DevOps project ID (GUID).

**Type:** `string`  
**Required:** No  
**Description:** The unique GUID assigned by Azure DevOps for this project (e.g. `12345678-9abc-def0-1234-567890abcdef`)

### Inherited from external.json
- `urls`: Map of URL references; `urls.default` (or `urls.web`) is the canonical web URL

### Inherited from entity.json
- `entity-id`: Unique identifier for this entity
- `entity-types`: Classification of entity types
- `names`: Array name patterns for identification
- `display-name`: Human-readable name with language localization
- `content`: Associated content with MIME type and reference

## Naming Convention

Names follow the same pattern as `repository`: `["repositories", <organization-name>, <project-name>]`

This convention aligns azure-devops-project entities with the platform-agnostic `repository` naming so that cross-platform tools can locate them using a single hierarchy.

## urls Convention

Set `urls.default` to the project web URL:
```
https://dev.azure.com/<organization>/<project>
```

## Example

```json
{
  "entity-id": "22222222-3333-4444-5555-666666666666",
  "entity-types": ["azure-devops-project", "external"],
  "names": [["repositories", "contoso", "my-project"]],
  "display-name": { "default": "My Project" },
  "project-id": "12345678-9abc-def0-1234-567890abcdef",
  "urls": {
    "default": "https://dev.azure.com/contoso/my-project",
    "api": "https://dev.azure.com/contoso/my-project/_apis",
    "boards": "https://dev.azure.com/contoso/my-project/_boards",
    "repos": "https://dev.azure.com/contoso/my-project/_git"
  }
}
```

## See Also

- [repository.json](repository-schema.md) - Platform-agnostic repository schema (counterpart in the type hierarchy)
- [entity.json](entity-schema.md) - Base entity schema
- [external.json](external-schema.md) - External references schema
- [azure-devops-organization.json](azure-devops-organization-schema.md) - Azure DevOps organization schema
- [azure-devops-work-item.json](azure-devops-work-item-schema.md) - Azure DevOps work item schema
