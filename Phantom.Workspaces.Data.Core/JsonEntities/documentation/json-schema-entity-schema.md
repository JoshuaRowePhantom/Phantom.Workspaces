# JSON schema entity schema

Defines entities that embed a JSON Schema document under `schema`.

## Expected shape

```json
{
  "entity-id": "<uuid>",
  "entity-types": ["json-schema", ...],
  "names": [["json-schemas", "https://.../your-schema.json"]],
  "schema": {
    "$id": "https://.../your-schema.json",
    "type": "object",
    "properties": { ... }
  }
}
```

## Guidance

- Keep `names` and `schema.$id` aligned to the same canonical URI.
- Use this shape for standalone schema entities that are discovered by schema validation.
