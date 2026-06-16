# Relationship Type Schema

Base schema for relationship-specific entity types.

This schema validates participant role definitions when a relationship type schema
declares `schema.properties.participants.properties`.

Each participant role is either:

1. a singleton schema, optionally with `x-entity-types` (an array of allowed `entity-type-id`s), or
2. an array schema (`type: "array"`) whose `items` schema optionally includes `x-entity-types`
   (an array of allowed `entity-type-id`s).

`x-entity-types` is optional; when absent, any entity type is allowed for that participant.
