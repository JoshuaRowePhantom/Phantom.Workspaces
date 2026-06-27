# Default schema

Defines a general-purpose default relationship: records that a given `value` entity is the default for a given `applied-to` context entity.

## Expected shape

```json
{
  "entity-id": "<uuid>",
  "entity-types": ["entity", "relationship", "default"],
  "participants": {
    "applied-to": "<entity-id>",
    "value": "<entity-id>"
  }
}
```

## Usage

- `applied-to` — the context in which the default applies (e.g. a `user-computer-profile`).
- `value` — the entity that is the default for that context (e.g. a `workspace`).

Multiple `default` relationships for the same `applied-to` entity are supported; each `value` is treated as a co-default.
