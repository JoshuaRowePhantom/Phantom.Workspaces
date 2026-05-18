# Entity type schema

Defines entities that declare a new type in the system.

## Expected shape

```json
{
  "entity-id": "<uuid>",
  "entity-types": ["entity-type", "note"],
  "names": [["entity-types", "<new-type-name>"], ...],
  "display-name": { "default": "Type Name" },
  "content": { "default": { "mime-type": "text/markdown", "url": "documentation/<file>.md" } },
  "schema": { "$id": "<schema-uri>", "type": "object", ... }
}
```

## Guidance

- Every reusable type should be represented by one `entity-type` entity.
- `schema.$id` should match the canonical URI used by `$schema` references in entities.
- Include `"note"` + markdown content to document intended usage and examples for contributors.
