# Task Schema

A task entity that represents an assignable work item with status tracking.

## Description

The `task` schema defines assignable work items with lifecycle status management and user assignment.

## Properties

### status (string)
Current status of the task.

**Type:** `string` (enum)  
**Required:** No  
**Allowed values:**
- `pending` - Not yet started
- `in-progress` - Currently being worked on
- `completed` - Finished
- `blocked` - Cannot proceed due to blockers
- `cancelled` - Explicitly cancelled

### assigned-to (string)
Reference to the user this task is assigned to.

**Type:** `string`  
**Required:** No  
**Description:** Identifier or reference to a user entity from user.json

### Inherited from entity.json
- `entity-id`: Unique identifier for this entity
- `entity-types`: Classification of entity types
- `names`: Array name patterns for identification
- `display-name`: Human-readable name with language localization
- `content`: Associated content with MIME type and reference

## Example

```json
{
  "entity-id": "87654321-4321-4321-4321-210987654321",
  "entity-types": ["task"],
  "names": [
    ["tasks", "implement-auth-module"],
    ["project-acme", "q1-sprint-1", "task-42"]
  ],
  "display-name": {
    "default": "Implement Authentication Module"
  },
  "status": "in-progress",
  "assigned-to": "john-doe"
}
```

## See Also

- [entity.json](entity-schema.md) - Base entity schema
- [external.json](external-schema.md) - External references schema
- [azure-devops-work-item.json](azure-devops-work-item-schema.md) - Azure DevOps integration schema
