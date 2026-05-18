# Reference schema

Defines a strict two-party relationship for directed references.

## Expected shape

```json
{
  "entity-id": "<uuid>",
  "entity-types": ["relationship", "reference", ...],
  "participants": {
    "source": "<entity-id>",
    "target": "<entity-id>"
  }
}
```

## Derived type guidance

- Custom reference-like types should include both `"relationship"` and a subtype name.
- Keep `participants` closed to exactly `source` and `target` when direction matters.
