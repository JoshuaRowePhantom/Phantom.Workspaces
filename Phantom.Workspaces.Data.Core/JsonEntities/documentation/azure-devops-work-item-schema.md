# Azure DevOps Work Item Schema

An Azure DevOps work item entity that combines task management with external system integration.

## Description

The `azure-devops-work-item` schema represents work items from Azure DevOps, combining task properties (status, assignment), external URL references, and Azure DevOps-specific metadata.

## Properties

### work-item-id (string)
The Azure DevOps work item ID.

**Type:** `string`  
**Required:** No  
**Description:** The unique identifier assigned by Azure DevOps for this work item

### project (string)
The Azure DevOps project name.

**Type:** `string`  
**Required:** No  
**Description:** Name of the Azure DevOps project containing this work item

### work-item-type (string)
Type of work item.

**Type:** `string`  
**Required:** No  
**Description:** Classification of work item (Epic, Feature, User Story, Task, Bug, etc.)

### Inherited from task.json
- `status`: Current task status (pending, in-progress, completed, blocked, cancelled)
- `assigned-to`: User reference

### Inherited from external.json
- `urls`: Map of URL references to external systems

### Inherited from entity.json
- `entity-id`: Unique identifier for this entity
- `entity-types`: Classification of entity types
- `names`: Array name patterns for identification
- `display-name`: Human-readable name with language localization
- `content`: Associated content with MIME type and reference

## Example

```json
{
  "entity-id": "11223344-5566-7788-99aa-bbccddeeff00",
  "entity-types": ["azure-devops-work-item", "task", "external"],
  "names": [
    ["azure-devops-work-items", "contoso-project", "12345"],
    ["tasks", "refactor-database"]
  ],
  "display-name": {
    "default": "Refactor Database Layer"
  },
  "status": "in-progress",
  "assigned-to": "jane-smith",
  "work-item-id": "12345",
  "project": "Contoso Project",
  "work-item-type": "User Story",
  "urls": {
    "ado-link": "https://dev.azure.com/contoso/project/_workitems/edit/12345",
    "pr-link": "https://github.com/contoso/repo/pull/789",
    "related-issue": "https://github.com/contoso/repo/issues/456"
  }
}
```

## See Also

- [entity.json](entity-schema.md) - Base entity schema
- [task.json](task-schema.md) - Task definition schema
- [external.json](external-schema.md) - External references schema
