# Related schema

Defines a multi-party relationship with a single `entities` participant list.

## Expected shape

```json
{
  "entity-id": "<uuid>",
  "entity-types": ["relationship", "related", ...],
  "participants": {
    "entities": ["<entity-id>", "<entity-id>", "..."]
  }
}
```

## Derived type guidance

- Use this as a pattern when order/direction is not the core concern.
- Enforce minimum cardinality in subtype schemas if your domain requires 2+ members.
