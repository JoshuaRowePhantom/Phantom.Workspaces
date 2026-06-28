# Azure DevOps Work Item Schema

An Azure DevOps work item entity that combines task management with Azure DevOps-specific metadata. This is the Azure DevOps platform-specific variant of the `work-item` concept.

## Description

The `azure-devops-work-item` schema represents work items from Azure DevOps. It composes `entity.json`, `task.json`, and `external.json`, providing task status, assignee, and external URL fields alongside Azure DevOps-specific properties (`work-item-id`, `project`, `work-item-type`).

## Composition

`azure-devops-work-item` composes:
- `work-item.json` — `title`, `status`, `labels`, `related-commits` fields
  - `entity.json` — base entity fields
  - `task.json` — `status` and `assigned-to` fields
  - `external.json` — canonical web URL via `urls`

It is the Azure DevOps implementation of the `work-item` concept. Discovery tools should create `azure-devops-work-item` entities rather than plain `work-item` entities when synchronising Azure DevOps work items.

## Properties

### work-item-id (string)
The Azure DevOps work item ID.

**Type:** `string`  
**Required:** No  
**Description:** The numeric identifier assigned by Azure DevOps for this work item (e.g. `12345`)

### project (string)
The Azure DevOps project name.

**Type:** `string`  
**Required:** No  
**Description:** Name of the Azure DevOps project containing this work item

### work-item-type (string)
Type of work item.

**Type:** `string`  
**Required:** No  
**Description:** Classification of the work item in Azure DevOps (e.g. `Epic`, `Feature`, `User Story`, `Task`, `Bug`)

### Inherited from task.json
- `status`: Current task status. Azure DevOps work items map their state to: `open` (Active/New), `in-progress` (In Progress/Committed/Resolved), `closed` (Closed/Done/Completed)
- `assigned-to`: User reference — name or unique name of the assignee

### Inherited from external.json
- `urls`: Map of URL references; `urls.default` is the canonical web URL for this work item

### Inherited from entity.json
- `entity-id`: Unique identifier for this entity
- `entity-types`: Classification of entity types
- `names`: Array name patterns for identification
- `display-name`: Human-readable name with language localization
- `content`: Associated content with MIME type and reference

## Naming Convention

Names follow the pattern: `["work-items", <organization-name>, <project-name>, <work-item-id>]`

This aligns with the platform-agnostic `work-item` naming convention.

## urls Convention

Set `urls.default` to the work item edit URL:
```
https://dev.azure.com/<organization>/<project>/_workitems/edit/<id>
```

## Example

```json
{
  "entity-id": "11223344-5566-7788-99aa-bbccddeeff00",
  "entity-types": ["azure-devops-work-item", "task", "external"],
  "names": [["work-items", "contoso", "my-project", "12345"]],
  "display-name": { "default": "Refactor Database Layer" },
  "status": "in-progress",
  "assigned-to": "jane-smith",
  "work-item-id": "12345",
  "project": "my-project",
  "work-item-type": "User Story",
  "urls": {
    "default": "https://dev.azure.com/contoso/my-project/_workitems/edit/12345"
  }
}
```

## See Also

- [work-item.json](work-item-schema.md) - Platform-agnostic work item schema
- [entity.json](entity-schema.md) - Base entity schema
- [task.json](task-schema.md) - Task definition schema
- [external.json](external-schema.md) - External references schema
- [azure-devops-project.json](azure-devops-project-schema.md) - Azure DevOps project schema
