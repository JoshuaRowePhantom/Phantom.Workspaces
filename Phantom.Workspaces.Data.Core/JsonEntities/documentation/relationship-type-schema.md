# Relationship Type Schema

Base schema for relationship-specific entity types.

This schema validates participant role definitions when a relationship type schema
declares `schema.properties.participants.properties`.

Each participant role must be either:

1. a singleton schema with `x-entity-type` set to an `entity-type-id`, or
2. an array schema (`type: "array"`) whose `items` schema includes `x-entity-type`
   set to an `entity-type-id`.
