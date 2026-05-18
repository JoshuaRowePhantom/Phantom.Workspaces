# Entity schema

Defines the common object shape for nearly all persisted entities.

## Base shape

```json
{
  "entity-id": "<uuid>",
  "entity-types": ["entity", "<derived-type>", "..."],
  "names": [["single"], ["multi", "part"]],
  "display-name": { "default": "Human readable name" }
}
```

## Notes

- The runtime composes this schema with all schemas for the listed `entity-types`.
- Final validation is closed-world (`unevaluatedProperties=false` at composed level), so properties must come from one of the composed schemas.
- Pattern properties permit additional `*entity-id` and `*entity-ids` fields that are valid id/id-array references.
