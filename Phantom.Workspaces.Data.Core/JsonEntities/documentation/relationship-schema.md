# Relationship schema

Defines the foundational shape for graph edges between entities.

## Base relationship shape

```json
{
  "entity-id": "<uuid>",
  "entity-types": ["relationship", "<subtype>", "..."],
  "participants": {
    "<role-name>": "<entity-id> | [<entity-id>, ...]"
  }
}
```

## Core rules

- `entity-types` must contain `"relationship"`.
- `participants` is required.
- Minimum `entity-types` count:
  - If `"entity"` is present, at least 3 types.
  - Otherwise, at least 2 types.

## Derived-type patterns

1. **Directed pair** (see `reference`):
```json
{
  "entity-types": ["relationship", "reference"],
  "participants": { "source": "<id>", "target": "<id>" }
}
```
2. **Undirected group** (see `related`):
```json
{
  "entity-types": ["relationship", "related"],
  "participants": { "entities": ["<id1>", "<id2>"] }
}
```
3. **Domain subtype** (example):
```json
{
  "entity-types": ["relationship", "depends-on"],
  "participants": { "dependent": "<id>", "dependency": "<id>" }
}
```

## Guidance

- Always define participant role semantics in subtype docs.
- Add subtype schemas to constrain role names, cardinality, and any additional metadata fields.
