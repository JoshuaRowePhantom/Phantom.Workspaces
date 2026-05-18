# Interest schema

Defines interest relationships and UX text for applied/not-applied states.

## Expected shape

```json
{
  "entity-id": "<uuid>",
  "entity-types": ["interest", "relationship", ...],
  "names": [["interests", "<name>"]],
  "participants": { "source": "<entity-id>", "target": "<entity-id>" },
  "applied": { "indicator": "...", "description": "...", "actionText": "..." },
  "notApplied": { "indicator": "...", "description": "...", "actionText": "..." }
}
```

## Guidance

- Must include both `"interest"` and `"relationship"` in `entity-types`.
- `applied` and `notApplied` fields are required and should use localizable strings.
