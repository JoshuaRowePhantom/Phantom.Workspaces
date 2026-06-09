# External Schema

An external entity that contains a map of URLs for integration with external systems and services.

## Description

The `external` schema represents external references and integrations, maintaining a collection of URLs indexed by key names.

## Properties

### urls (object)
Map of URL keys to URL values. Each key is a string identifier, and each value must be a valid URI.

**Type:** `object` with string keys and URI values  
**Required:** No  
**Description:** A flexible mapping of URL references to external systems and resources

### Inherited from entity.json
- `entity-id`: Unique identifier for this entity
- `entity-types`: Classification of entity types
- `names`: Array name patterns for identification
- `display-name`: Human-readable name with language localization
- `content`: Associated content with MIME type and reference

## Example

```json
{
  "entity-id": "12345678-1234-1234-1234-123456789012",
  "entity-types": ["external"],
  "names": [
    ["external-systems", "our-api"],
    ["service-references", "primary"]
  ],
  "display-name": {
    "default": "Our API Integration"
  },
  "urls": {
    "api-docs": "https://api.example.com/docs",
    "api-endpoint": "https://api.example.com/v1",
    "status-page": "https://status.example.com"
  }
}
```

## See Also

- [entity.json](entity-schema.md) - Base entity schema
- [task.json](task-schema.md) - Task definition schema
- [azure-devops-work-item.json](azure-devops-work-item-schema.md) - Azure DevOps integration schema
